#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Canvas.Capability;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;
using OpenAICanvas.Prompts;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>
/// 云 Agent 运行时：每次推进一个有界状态转移；模型调用与审批等待都不持有数据库锁。
/// 对应 Go: <c>app/cloud_agent_runtime.go</c>、<c>cloud_agent_recovery.go</c>、
/// <c>cloud_agent_undo.go</c> 与 <c>DecideCloudAgentApproval</c>/<c>CancelCloudAgent</c>。
/// </summary>
public sealed partial class CloudAgentRuntimeService
{
    private readonly Repository _repository;
    private readonly TaskCreationService _taskCreation;
    private readonly IRuntimePolicyProvider _runtimePolicy;
    private readonly CloudAgentMediaService _media;
    private readonly CloudAgentSessionService _sessions;
    private readonly SkillsService _skills;
    private readonly TaskLifecycleService _taskLifecycle;

    public CloudAgentRuntimeService(
        Repository repository,
        TaskCreationService taskCreation,
        IRuntimePolicyProvider runtimePolicy,
        CloudAgentMediaService media,
        CloudAgentSessionService sessions,
        SkillsService skills,
        TaskLifecycleService taskLifecycle)
    {
        _repository = repository;
        _taskCreation = taskCreation;
        _runtimePolicy = runtimePolicy;
        _media = media;
        _sessions = sessions;
        _skills = skills;
        _taskLifecycle = taskLifecycle;
    }

    // ------------------------------------------------------------ 校验与解码

    /// <summary>策略快照结构校验。对应 Go: <c>validateCloudAgentPolicySnapshotStructure</c>。</summary>
    public static void ValidatePolicySnapshotStructure(CloudAgentPolicySnapshotDto snapshot)
    {
        foreach ((string name, string value) in new[]
                 {
                     ("policy compiler", snapshot.CompilerVersion),
                     ("system policy ID", snapshot.SystemPolicyID),
                     ("media policy ID", snapshot.MediaPolicyID),
                     ("capability set version", snapshot.CapabilitySetVersion),
                 })
        {
            if (value.Trim().Length == 0 || value.EnumerateRunes().Count() > 120)
            {
                throw new InvalidOperationException($"Agent runtime {name} is invalid");
            }
        }
        if (snapshot.SystemPolicyVersion <= 0 || snapshot.MediaPolicyVersion <= 0)
        {
            throw new InvalidOperationException("Agent runtime policy version is invalid");
        }
        if (snapshot.ReasoningMode is not ("off" or "auto" or "deep"))
        {
            throw new InvalidOperationException("Agent runtime reasoning mode is invalid");
        }
        foreach ((string name, string value) in new[]
                 {
                     ("system policy hash", snapshot.SystemPolicyHash),
                     ("media policy hash", snapshot.MediaPolicyHash),
                     ("capability set hash", snapshot.CapabilitySetHash),
                     ("profile revision", snapshot.ProfileRevision),
                     ("profile hash", snapshot.ProfileHash),
                 })
        {
            if (!IsSha256Hex(value))
            {
                throw new InvalidOperationException($"Agent runtime {name} is invalid");
            }
        }
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>运行状态整体校验。对应 Go: <c>validateCloudAgentRuntime</c>。</summary>
    public static void ValidateRuntime(CloudAgentExecution run, CloudAgentRuntimeDto state)
    {
        if (run.ID.Length == 0)
        {
            return; // 包级工具测试可使用无持久身份的内存运行。
        }
        if (state.Request.CanvasID.Length == 0 || state.Request.Prompt.Length == 0
            || state.Request.PermissionMode.Length == 0)
        {
            throw new InvalidOperationException("Agent runtime request is incomplete");
        }
        CloudAgentContracts.ValidateRequest(state.Request);
        ValidatePolicySnapshotStructure(state.Policy);
        CloudAgentProfileSnapshots.ValidateSnapshot(state.Profile, state.Policy);
        if (state.ProfileReads is not null)
        {
            foreach ((string scope, bool read) in state.ProfileReads)
            {
                bool found = state.Profile.Layers.Any(layer => layer.Scope == scope);
                if (!read || !found)
                {
                    throw new InvalidOperationException("Agent runtime profile read history is invalid");
                }
            }
        }
        if (state.Step < 0 || state.Generations < 0 || state.VideoSeconds < 0)
        {
            throw new InvalidOperationException("Agent runtime budget or step is invalid");
        }
        if ((state.Request.Budget.MaxGenerationTasks > 0
                && state.Generations > state.Request.Budget.MaxGenerationTasks)
            || (state.Request.Budget.MaxVideoSeconds > 0
                && state.VideoSeconds > state.Request.Budget.MaxVideoSeconds))
        {
            throw new InvalidOperationException("Agent runtime generation budget is invalid");
        }
        if (state.CallIndex < 0 || state.CallIndex > state.Calls.Count || state.Calls.Count > 8)
        {
            throw new InvalidOperationException("Agent runtime call cursor is invalid");
        }
        if (state.ActiveTaskID.Length > 0 && state.MediaTaskID.Length > 0)
        {
            throw new InvalidOperationException("Agent runtime has multiple active tasks");
        }
        if (state.TaskIDs.Count == 0)
        {
            throw new InvalidOperationException("Agent runtime task history is invalid");
        }
        HashSet<string> seenTasks = new(StringComparer.Ordinal);
        foreach (string taskID in state.TaskIDs)
        {
            CloudAgentContracts.ValidateCloudAgentID(taskID, "任务 ID", 80);
            if (!seenTasks.Add(taskID))
            {
                throw new InvalidOperationException("Agent runtime task history contains duplicates");
            }
        }
        if (state.ActiveTaskID.Length > 0 && !state.TaskIDs.Contains(state.ActiveTaskID))
        {
            throw new InvalidOperationException("Agent runtime active task is not in task history");
        }
        if (state.MediaTaskID.Length > 0
            && (!state.TaskIDs.Contains(state.MediaTaskID)
                || state.CallIndex >= state.Calls.Count
                || state.Calls[state.CallIndex].Function.Name != "generate_media"))
        {
            throw new InvalidOperationException("Agent runtime media task is not attached to current call");
        }
        if (state.Decisions is null || state.Events is null)
        {
            throw new InvalidOperationException("Agent runtime maps are missing");
        }
        if (state.Events.Count > 4096)
        {
            throw new InvalidOperationException("Agent runtime event history is too large");
        }
        for (int index = 0; index < state.Events.Count; index++)
        {
            CloudAgentEventDto agentEvent = state.Events[index];
            if (agentEvent.RunID != run.ID || agentEvent.Seq != index + 1 || agentEvent.EventID.Length == 0
                || agentEvent.Type.Length == 0 || agentEvent.Payload is null
                || agentEvent.CreatedAt == default)
            {
                throw new InvalidOperationException("Agent runtime event history is invalid");
            }
            CloudAgentContracts.ValidateCloudAgentID(agentEvent.EventID, "事件 ID", 240);
            string raw = JsonSerializer.Serialize(agentEvent.Payload, GoJson.WriteOptions);
            if (raw.Length > 128 << 10)
            {
                throw new InvalidOperationException("Agent runtime event payload is too large");
            }
        }
        foreach (CloudAgentCallDto call in state.Calls)
        {
            CloudAgentContracts.ValidateCloudAgentID(call.ID, "工具调用 ID", 160);
            if (call.Function.Name.Length == 0
                || call.Function.Name.EnumerateRunes().Count() > 80)
            {
                throw new InvalidOperationException("Agent runtime tool call is invalid");
            }
            if (call.Function.Arguments.Length > 32000)
            {
                throw new InvalidOperationException("Agent runtime tool arguments are too large");
            }
            try
            {
                CloudAgentContracts.DecodeObject<Dictionary<string, JsonElement>>(call.Function.Arguments);
            }
            catch (Exception cause) when (cause is CloudAgentArgumentException or AppError)
            {
                throw new InvalidOperationException("Agent runtime tool arguments are invalid");
            }
        }
        if (state.Approval is not null)
        {
            bool approvalIdValid = true;
            try
            {
                CloudAgentContracts.ValidateCloudAgentID(state.Approval.ID, "审批 ID", 200);
            }
            catch (AppError)
            {
                approvalIdValid = false;
            }
            if (!approvalIdValid || state.CallIndex >= state.Calls.Count)
            {
                throw new InvalidOperationException("Agent runtime approval is invalid");
            }
            CloudAgentCallDto current = state.Calls[state.CallIndex];
            if (state.Approval.Call.ID != current.ID
                || state.Approval.Call.Function.Name != current.Function.Name
                || state.Approval.Call.Function.Arguments != current.Function.Arguments)
            {
                throw new InvalidOperationException("Agent runtime approval does not match current call");
            }
            if (state.Approval.Decision.Length > 0
                && state.Approval.Decision is not ("approve" or "reject"))
            {
                throw new InvalidOperationException("Agent runtime approval decision is invalid");
            }
        }
        if (state.CallIndex == state.Calls.Count && state.Approval is not null)
        {
            throw new InvalidOperationException("Agent runtime has approval without a pending call");
        }
    }

    /// <summary>历史读取解码。对应 Go: <c>cloudAgentDecode</c>（含校验）。</summary>
    private static CloudAgentRuntimeDto Decode(CloudAgentExecution run)
    {
        CloudAgentRuntimeDto state = CloudAgentContracts.Decode(run);
        ValidateRuntime(run, state);
        return state;
    }

    /// <summary>执行路径解码：合同/策略版本必须与当前运行时一致。对应 Go: <c>cloudAgentDecodeForExecution</c>。</summary>
    private static async Task<CloudAgentRuntimeDto> DecodeForExecutionAsync(CloudAgentExecution run)
    {
        CloudAgentRuntimeDto state;
        try
        {
            state = Decode(run);
        }
        catch (Exception cause) when (cause is InvalidOperationException or AppError)
        {
            throw AppError.Wrap(409, "Agent 运行记录无法安全恢复；请新建一轮消息", cause);
        }
        try
        {
            ValidatePolicySnapshotStructure(state.Policy);
            await ValidatePolicyVersions(state.Policy).ConfigureAwait(false);
        }
        catch (Exception cause) when (cause is InvalidOperationException or AggregateException or AppError)
        {
            throw AppError.New(409, "Agent 运行使用旧版执行合同，无法继续原运行；请新建一轮消息");
        }
        return state;
    }

    private static async Task ValidatePolicyVersions(CloudAgentPolicySnapshotDto snapshot)
    {
        (AgentPolicy system, AgentPolicy media) = await OpenAICanvas.Prompts.AgentPolicyDocuments
            .LoadAgentPoliciesAsync().ConfigureAwait(false);
        if (snapshot.CompilerVersion != CloudAgentContracts.CompilerVersion)
        {
            throw new InvalidOperationException("Agent runtime policy compiler is unsupported");
        }
        if (snapshot.CapabilitySetVersion != CapabilityRegistry.SetVersion)
        {
            throw new InvalidOperationException("Agent runtime capability contract is unsupported");
        }
        if (snapshot.SystemPolicyID != system.Id || snapshot.SystemPolicyVersion != system.Version
            || snapshot.MediaPolicyID != media.Id || snapshot.MediaPolicyVersion != media.Version)
        {
            throw new InvalidOperationException("Agent runtime policy version is unsupported");
        }
        if (snapshot.SystemPolicyHash != system.Hash || snapshot.MediaPolicyHash != media.Hash
            || snapshot.CapabilitySetHash != CloudAgentNodes.Registry.Hash())
        {
            throw new InvalidOperationException("Agent runtime policy or capability contract has changed");
        }
    }

    // ------------------------------------------------------------ 失败与终止

    private static string CheckpointMessage(Exception cause) =>
        cause is CloudAgentCheckpointException checkpoint ? checkpoint.Message : cause.Message;

    /// <summary>终态 CAS 终止。对应 Go: <c>terminateCloudAgent</c>。</summary>
    private async Task TerminateAsync(CloudAgentExecution run, string message)
    {
        await _repository.MarkCloudAgentFailedAsync(run.UserID, run.ID, run.Revision, message)
            .ConfigureAwait(false);
    }

    /// <summary>检查点失败终止。对应 Go: <c>failCloudAgent</c>。</summary>
    private Task FailAsync(CloudAgentExecution run, CloudAgentRuntimeDto state, string message) =>
        _repository.MutateCloudAgentAsync(run.UserID, run.ID, run.Revision, (current, context) =>
        {
            current.Status = "failed";
            CloudAgentContracts.AddEvent(
                state, run.ID, "run_failed", CloudAgentContracts.Payload(("text", message)));
            CloudAgentContracts.Save(current, state);
            return context.SaveRunAsync(current);
        });

    /// <summary>安全的用户可见错误文案。对应 Go: <c>cloudAgentSafeToolError</c>。</summary>
    private static string SafeToolError(Exception cause)
    {
        if (cause is AppError appError)
        {
            string message = appError.Message.Trim();
            if (SafeUserMessage(message))
            {
                return message;
            }
        }
        return "工具执行失败，请检查输入或稍后重试";
    }

    private static bool SafeUserMessage(string message)
    {
        if (message.Length == 0 || message.IndexOfAny(new[] { char.MinValue, (char)13, (char)10 }) >= 0
            || message.EnumerateRunes().Count() > 240)
        {
            return false;
        }
        string lower = message.ToLowerInvariant();
        foreach (string marker in new[]
                 {
                     "http://", "https://", "ftp://", "file://", "authorization", "cookie", "secret",
                     "token", "api_key", "apikey", "x-api-key", "/var/", "/tmp/", "\\", "stack trace",
                     "traceback", " at ", "sql:", "sqlite", "postgres",
                 })
        {
            if (lower.Contains(marker))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>模型任务失败分类。对应 Go: <c>cloudAgentModelFailure</c>。</summary>
    private static (string Detail, string Reason) ModelFailure(TaskEntity task)
    {
        string detail = "模型任务未成功";
        string reason = "model_task_failed";
        string raw = task.Error.ToLowerInvariant();
        if (raw.Contains("connection reset by peer"))
        {
            detail = "模型连接被对端或中间网络设备重置";
            reason = "model_connection_reset";
        }
        else if (raw.Contains("timeout") || raw.Contains("deadline exceeded"))
        {
            detail = "模型请求超时";
            reason = "model_request_timeout";
        }
        else if (raw.Contains("connection refused"))
        {
            detail = "无法连接模型服务（连接被拒绝）";
            reason = "model_connection_refused";
        }
        return (detail + "；本轮已停止。请在任务中心检查模型任务 " + task.ID, reason);
    }

    /// <summary>工具结果事件 + 规范消息推进。对应 Go: <c>cloudAgentToolResult</c>。</summary>
    private static void ToolResult(
        string runID, CloudAgentRuntimeDto state, CloudAgentCallDto call,
        JsonObject? result, Exception? cause)
    {
        Dictionary<string, JsonElement> payload = CloudAgentContracts.Payload(
            ("toolName", call.Function.Name),
            ("callId", call.ID),
            ("arguments", JsonSerializer.SerializeToElement(call.Function.Arguments)));
        string kind;
        if (cause is not null)
        {
            JsonObject detail = result is not null
                ? (JsonObject)result.DeepClone()
                : new JsonObject();
            string message = SafeToolError(cause);
            detail["error"] = message;
            result = detail;
            kind = "tool_failed";
            payload["text"] = JsonSerializer.SerializeToElement(message);
        }
        else
        {
            payload["text"] = JsonSerializer.SerializeToElement("工具执行成功");
            kind = "tool_completed";
        }
        payload["result"] = JsonSerializer.SerializeToElement(
            result ?? new JsonObject(), GoJson.WriteOptions);
        if (call.Function.Name == "skill_read_file" && cause is null && result is not null)
        {
            // SSE/UI 只需要读取回执，不再持久化技能正文副本。
            JsonObject receipt = new();
            foreach ((string key, JsonNode? value) in result)
            {
                if (key != "content")
                {
                    receipt[key] = value?.DeepClone();
                }
            }
            payload["result"] = JsonSerializer.SerializeToElement(receipt);
        }
        CloudAgentContracts.AddEvent(state, runID, kind, payload);
        state.Canonical.Messages.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["role"] = JsonSerializer.SerializeToElement("tool"),
            ["tool_call_id"] = JsonSerializer.SerializeToElement(call.ID),
            ["content"] = JsonSerializer.SerializeToElement(
                result is null ? "null" : CloudAgentContracts.CanonicalJson(result)),
        });
        state.CallIndex++;
        state.Approval = null;
    }

    // ------------------------------------------------------------ 推进入口

    /// <summary>按 ID 推进一步。对应 Go: <c>advanceCloudAgentByID</c>。</summary>
    public async Task AdvanceByIDAsync(string userID, string id, CancellationToken cancellationToken)
    {
        // 先确认执行记录是否已存在，损坏的运行时必须可终态恢复，而不是被新行覆盖。
        TaskEntity? task = await _repository.TaskForUserAsync(userID, id, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("Agent 运行不存在");
        if (task.Operation != CloudAgentContracts.CloudAgentOperation)
        {
            throw AppError.NotFound("Agent 运行不存在");
        }
        CloudAgentExecution? run = await _repository.CloudAgentAsync(userID, id, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            (_, CloudAgentStateDto initial) = await _sessions.LoadTaskAsync(userID, id, cancellationToken)
                .ConfigureAwait(false);
            await _sessions.EnsureExecutionAsync(task, initial, cancellationToken).ConfigureAwait(false);
            run = await _repository.CloudAgentAsync(userID, id, cancellationToken).ConfigureAwait(false)
                ?? throw AppError.NotFound("Agent 运行不存在");
        }
        await AdvanceAsync(run, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>调度器恢复入口：为还没有执行行的根任务补建执行行。对应 Go 恢复分支。</summary>
    public async Task EnsureRootAsync(TaskEntity task, CancellationToken cancellationToken)
    {
        if (await _repository.CloudAgentAsync(task.UserID, task.ID, cancellationToken).ConfigureAwait(false)
            is not null)
        {
            return;
        }
        (TaskEntity _, CloudAgentStateDto state) = await _sessions.LoadTaskAsync(
            task.UserID, task.ID, cancellationToken).ConfigureAwait(false);
        await _sessions.EnsureExecutionAsync(task, state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 推进一次。检查点失败必须终态化，而不是像瞬时 DB 错误那样无限重试。
    /// 对应 Go: <c>advanceCloudAgent</c>。
    /// </summary>
    public async Task AdvanceAsync(CloudAgentExecution run, CancellationToken cancellationToken)
    {
        try
        {
            await AdvanceCoreAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudAgentCheckpointException cause)
        {
            await TerminateAsync(
                run,
                "Agent 上下文或执行记录超过安全限制，本轮已停止；已有任务结果保留在任务中心: "
                + cause.Message).ConfigureAwait(false);
        }
        catch (InvalidOperationException cause)
        {
            await TerminateAsync(run, "Agent 运行状态损坏，本轮已停止: " + cause.Message)
                .ConfigureAwait(false);
        }
    }

    private async Task AdvanceCoreAsync(CloudAgentExecution run, CancellationToken cancellationToken)
    {
        if (run.CleanupPending)
        {
            // 清理交接（finishCloudAgentCleanup）随运行时第二批接入（PENDING #68）。
            return;
        }
        if (run.Status is not ("running" or "queued"))
        {
            return;
        }
        CloudAgentRuntimeDto state = await DecodeForExecutionAsync(run).ConfigureAwait(false);
        if (state.ActiveTaskID.Length > 0)
        {
            TaskEntity? task = await _repository.TaskForUserAsync(run.UserID, state.ActiveTaskID, cancellationToken)
                .ConfigureAwait(false);
            if (task is null)
            {
                await TerminateAsync(run, "Agent 模型任务已不存在，本轮已停止").ConfigureAwait(false);
                return;
            }
            if (task.Status is TaskStatus.TaskStatusQueued or TaskStatus.TaskStatusRunning)
            {
                // 把已持久化的模型增量转成 Agent 事件；不拆分完整答案伪装成流式。
                if (task.TextDraft != state.ActiveTextDraft)
                {
                    string delta = "";
                    string eventType = "assistant_snapshot";
                    Dictionary<string, JsonElement> payload = CloudAgentContracts.Payload(
                        ("messageId", task.ID), ("text", task.TextDraft), ("replace", true));
                    if (task.TextDraft.StartsWith(state.ActiveTextDraft, StringComparison.Ordinal))
                    {
                        delta = task.TextDraft[state.ActiveTextDraft.Length..];
                        if (delta.Length == 0)
                        {
                            return;
                        }
                        eventType = "assistant_delta";
                        payload = CloudAgentContracts.Payload(("messageId", task.ID), ("text", delta));
                    }
                    bool claimed = await _repository.MutateCloudAgentAsync(
                        run.UserID, run.ID, run.Revision, (current, context) =>
                        {
                            CloudAgentContracts.AddEvent(state, run.ID, eventType, payload);
                            state.ActiveTextDraft = task.TextDraft;
                            CloudAgentContracts.Save(current, state);
                            return context.SaveRunAsync(current);
                        }).ConfigureAwait(false);
                    if (!claimed)
                    {
                        throw CloudAgentSessionService.CreationConflict();
                    }
                    // 修订已被 CAS 前移，同步调用方句柄。
                    run.Revision++;
                    run.StateJSON = JsonSerializer.Serialize(state, GoJson.WriteOptions);
                }
                return;
            }
            ModelOutcome result = new();
            if (task.Status == TaskStatus.TaskStatusSucceeded)
            {
                try
                {
                    result = JsonSerializer.Deserialize<ModelOutcome>(task.ResultJSON, GoJson.ReadOptions)
                        ?? new ModelOutcome();
                }
                catch (JsonException)
                {
                    await TerminateAsync(run, "模型任务结果损坏，本轮已停止").ConfigureAwait(false);
                    return;
                }
                List<CloudAgentCallDto> calls = result.ToolCalls.Count > 0 ? result.ToolCalls : result.Legacy;
                if ((result.Text ?? "").Length > 32000 || calls.Count > 8)
                {
                    await TerminateAsync(run, "模型输出超出 Agent 单步限制").ConfigureAwait(false);
                    return;
                }
                try
                {
                    ValidateCalls(calls);
                }
                catch (AppError)
                {
                    await TerminateAsync(run, "模型返回了无效或重复的工具调用").ConfigureAwait(false);
                    return;
                }
                result.ToolCalls = calls;
            }
            bool resultClaimed = await _repository.MutateCloudAgentAsync(run.UserID, run.ID, run.Revision,
                (current, context) =>
                {
                    if (task.Status != TaskStatus.TaskStatusSucceeded)
                    {
                        current.Status = "failed";
                        (string text, string reason) = ModelFailure(task);
                        CloudAgentContracts.AddEvent(state, run.ID, "run_failed", CloudAgentContracts.Payload(
                            ("text", text), ("reason", reason), ("taskId", task.ID)));
                        CloudAgentContracts.Save(current, state);
                        return context.SaveRunAsync(current);
                    }
                    List<CloudAgentCallDto> calls = result.ToolCalls;
                    if ((result.Reasoning ?? "").Length > 0)
                    {
                        CloudAgentContracts.AddEvent(state, run.ID, "reasoning_message",
                            CloudAgentContracts.Payload(
                                ("messageId", task.ID + ":reasoning"),
                                ("text", CloudAgentContracts.TruncateRunes(result.Reasoning!, 8000))));
                    }
                    if ((result.Text ?? "").Length > 0)
                    {
                        CloudAgentContracts.AddEvent(state, run.ID, "assistant_message",
                            CloudAgentContracts.Payload(("messageId", task.ID), ("text", result.Text)));
                        if (calls.Count == 0)
                        {
                            state.Canonical.Messages.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                            {
                                ["role"] = JsonSerializer.SerializeToElement("assistant"),
                                ["content"] = JsonSerializer.SerializeToElement(result.Text),
                            });
                        }
                    }
                    state.ActiveTaskID = "";
                    state.Calls = calls;
                    state.CallIndex = 0;
                    if (calls.Count > 0)
                    {
                        Dictionary<string, JsonElement> assistantMessage = new(StringComparer.Ordinal)
                        {
                            ["role"] = JsonSerializer.SerializeToElement("assistant"),
                            ["content"] = JsonSerializer.SerializeToElement(result.Text ?? ""),
                            ["tool_calls"] = JsonSerializer.SerializeToElement(calls, GoJson.WriteOptions),
                        };
                        state.Canonical.Messages.Add(assistantMessage);
                    }
                    if (calls.Count == 0)
                    {
                        current.Status = "completed";
                    }
                    CloudAgentContracts.Save(current, state);
                    return context.SaveRunAsync(current);
                }).ConfigureAwait(false);
            if (!resultClaimed)
            {
                throw CloudAgentSessionService.CreationConflict();
            }
            run.Revision++;
            return;
        }
        if (state.CallIndex < state.Calls.Count)
        {
            await AdvanceToolAsync(run, state, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (CompactContext(state.Canonical))
        {
            // 被移出的读取正文必须可以重新读取。
            state.SkillReads = null;
            state.ProfileReads = null;
        }
        Dictionary<string, JsonElement> stepInput = new(StringComparer.Ordinal)
        {
            ["mode"] = JsonSerializer.SerializeToElement("text"),
            ["prompt"] = JsonSerializer.SerializeToElement(state.Request.Prompt),
            ["agentRequests"] = JsonSerializer.SerializeToElement(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["canonical"] = JsonSerializer.SerializeToElement(state.Canonical, GoJson.WriteOptions),
                }),
            ["config"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["channelId"] = JsonSerializer.SerializeToElement(state.Request.ChannelID),
                ["channelModelKey"] = JsonSerializer.SerializeToElement(state.Request.ChannelModelKey),
                ["model"] = JsonSerializer.SerializeToElement(
                    CloudAgentContracts.FirstNonEmpty(state.Request.ChannelModelKey, state.Request.Model)),
            }),
            ["textOptions"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["stream"] = JsonSerializer.SerializeToElement(true),
                ["thinking"] = JsonSerializer.SerializeToElement(
                    CloudAgentPolicyCompiler.ReasoningEnabled(state.Policy.ReasoningMode)),
            }),
        };
        string canonicalRaw = JsonSerializer.Serialize(state.Canonical, GoJson.WriteOptions);
        if (canonicalRaw.Length > 192 << 10)
        {
            await FailAsync(run, state, "模型上下文超过 192KB 上限").ConfigureAwait(false);
            return;
        }
        await EnqueueTaskAsync(run, state, "canvas_text", state.Request.Prompt,
            state.Request.Model, state.Request.LogicalModelID, stepInput,
            media: null, cancellationToken).ConfigureAwait(false);
    }

    public sealed class ModelOutcome
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("reasoning")]
        public string? Reasoning { get; set; }

        [JsonPropertyName("toolCalls")]
        public List<CloudAgentCallDto> ToolCalls { get; set; } = [];

        [JsonPropertyName("tool_calls")]
        public List<CloudAgentCallDto> Legacy { get; set; } = [];
    }

    /// <summary>工具调用去重与合法性校验。对应 Go: <c>validateCloudAgentCalls</c>。</summary>
    private static void ValidateCalls(List<CloudAgentCallDto> calls)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (CloudAgentCallDto call in calls)
        {
            CloudAgentContracts.ValidateCloudAgentID(call.ID, "工具调用 ID", 160);
            if (!seen.Add(call.ID) || call.Function.Name.Length == 0
                || call.Function.Name.Length > 80)
            {
                throw AppError.BadAuthRequest("invalid Agent tool call");
            }
            CloudAgentContracts.DecodeObject<Dictionary<string, JsonElement>>(call.Function.Arguments);
            if (call.Function.Arguments.Length > 32000)
            {
                throw AppError.BadAuthRequest("invalid Agent tool arguments");
            }
        }
    }

    /// <summary>历史读取正文移出上下文。对应 Go: <c>compactCloudAgentContext</c>。</summary>
    private static bool CompactContext(CloudAgentCanonicalRequestDto request)
    {
        string raw = JsonSerializer.Serialize(request, GoJson.WriteOptions);
        if (raw.Length < 96 << 10 && request.Messages.Count <= 24)
        {
            return false;
        }
        // 保留最近的完整工具轮次；绝不移除 call/result 信封、用户指令、参数或写回执。
        int cut = request.Messages.Count - 1;
        while (cut > 0 && MessageRole(request.Messages[cut]) == "tool")
        {
            cut--;
        }
        bool changed = false;
        for (int index = 0; index < Math.Max(0, cut); index++)
        {
            Dictionary<string, JsonElement> message = request.Messages[index];
            if (MessageRole(message) != "tool")
            {
                continue;
            }
            string content = MessageString(message, "content");
            JsonObject result;
            try
            {
                result = JsonNode.Parse(content) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                continue;
            }
            if (result.TryGetPropertyValue("contextCompacted", out JsonNode? compacted)
                && compacted is JsonValue cv && cv.TryGetValue<bool>(out bool done) && done)
            {
                continue;
            }
            bool omitted = false;
            foreach (string key in new[] { "content", "nodes" })
            {
                if (result.Remove(key))
                {
                    omitted = true;
                }
            }
            if (!omitted)
            {
                continue;
            }
            result["contextCompacted"] = true;
            result["guidance"] = "历史读取正文已移出模型上下文；需要时重新读取。保留的历史状态不是当前状态，也不是执行授权，不得据此重复提交生成。";
            string body = CloudAgentContracts.CanonicalJson(result);
            if (body.Length >= content.Length)
            {
                continue;
            }
            message["content"] = JsonSerializer.SerializeToElement(body);
            changed = true;
        }
        return changed;
    }

    private static string MessageRole(Dictionary<string, JsonElement> message) =>
        message.TryGetValue("role", out JsonElement role) && role.ValueKind == JsonValueKind.String
            ? role.GetString() ?? ""
            : "";

    private static string MessageString(Dictionary<string, JsonElement> message, string key) =>
        message.TryGetValue(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
