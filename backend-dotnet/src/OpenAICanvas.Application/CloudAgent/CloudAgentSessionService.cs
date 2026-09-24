#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAICanvas.Domain.Canvas.Capability;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>已安装技能包内文件的元数据（快照只需要路径）。</summary>
public static class CloudAgentSkills
{
    /// <summary>技能文件清单（入口 + 文本引用）。对应 Go: <c>cloudAgentSkillPaths</c>。</summary>
    public static JsonArray PathsArray(CloudAgentSkillDto skill)
    {
        List<string> paths = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        if (skill.Instruction.Trim().Length > 0)
        {
            paths.Add(CloudAgentContracts.SkillEntryPath);
            seen.Add(CloudAgentContracts.SkillEntryPath);
        }
        if (skill.Files is not null)
        {
            paths.AddRange(skill.Files.Keys.Where(path => seen.Add(path)));
        }
        paths.Sort(StringComparer.Ordinal);
        JsonArray array = new();
        foreach (string path in paths)
        {
            array.Add(path);
        }
        return array;
    }

    /// <summary>构建技能快照。对应 Go: <c>cloudAgentSkills</c>。</summary>
    public static async Task<List<CloudAgentSkillDto>> SnapshotAsync(
        SkillsService skills,
        string userID,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        List<CloudAgentSkillDto> snapshots = [];
        foreach (string id in ids)
        {
            SkillItemDto skill = await skills.SkillDetailAsync(userID, id, cancellationToken).ConfigureAwait(false);
            if (!skill.IsAdded || skill.Status != 1)
            {
                throw AppError.BadAuthRequest("只能使用用户技能库中已安装且启用的技能");
            }
            // 技能正文只在模型显式 skill_read_file 后加载；运行上下文仅保留元数据与路径。
            CloudAgentSkillDto snapshot = new()
            {
                ID = id,
                Name = skill.SkillName,
                Version = skill.VersionID,
                Hash = skill.ContentHash,
                Files = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [CloudAgentContracts.SkillEntryPath] = "",
                },
            };
            List<SkillPackageFileItemDto> files = await skills
                .SkillPackageFilesAsync(userID, id, cancellationToken).ConfigureAwait(false);
            foreach (SkillPackageFileItemDto file in files)
            {
                if (file.Path == CloudAgentContracts.SkillEntryPath)
                {
                    continue;
                }
                if (!file.Path.EndsWith(".md", StringComparison.Ordinal)
                    && !file.Path.EndsWith(".txt", StringComparison.Ordinal)
                    && !file.Path.EndsWith(".json", StringComparison.Ordinal))
                {
                    continue;
                }
                snapshot.Files![file.Path] = "";
            }
            SkillItemDto latest = await skills.SkillDetailAsync(userID, id, cancellationToken).ConfigureAwait(false);
            if (latest.VersionID != skill.VersionID || latest.ContentHash != skill.ContentHash)
            {
                throw CloudAgentSessionService.CreationConflict("技能在读取时已更新，请重试");
            }
            snapshots.Add(snapshot);
        }
        return snapshots;
    }
}

/// <summary>
/// 云 Agent 会话：一次不可变、持久的模型轮次由普通任务承载，后续轮次引用上一轮。
/// 复用任务的事务化计费、worker 租约、取消与文本回放。
/// 对应 Go: <c>app/cloud_agent.go</c>。
/// </summary>
/// <remarks>
/// 运行推进（advanceCloudAgentByID/advanceCloudAgent）属阶段 11.1 运行时批次；
/// 本批次 CreateAsync 的续聊分支暂不调用推进（PENDING #68），运行路由整体未开放。
/// </remarks>
public sealed class CloudAgentSessionService
{
    private readonly Repository _repository;
    private readonly TaskCreationService _taskCreation;
    private readonly SkillsService _skills;
    private readonly AgentProfileService _profiles;
    private readonly IRuntimePolicyProvider _runtimePolicy;

    public CloudAgentSessionService(
        Repository repository,
        TaskCreationService taskCreation,
        SkillsService skills,
        AgentProfileService profiles,
        IRuntimePolicyProvider runtimePolicy)
    {
        _repository = repository;
        _taskCreation = taskCreation;
        _skills = skills;
        _profiles = profiles;
        _runtimePolicy = runtimePolicy;
    }

    /// <summary>创作冲突。对应 Go: <c>creationConflict</c>（409）。</summary>
    public static AppError CreationConflict(string message = "创作运行已变化，请刷新后重试") =>
        AppError.New(409, message);

    // ------------------------------------------------------------ 创建运行

    /// <summary>创建一轮运行。对应 Go: <c>CreateCloudAgentRun</c>。</summary>
    public async Task<CloudAgentRunDto> CreateAsync(
        string userID,
        CloudAgentRequestDto request,
        string parentID,
        CancellationToken cancellationToken)
    {
        CloudAgentContracts.ValidateRequest(request);
        if (userID.Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        CanvasProject? canvas = await _repository.CanvasProjectForUserAsync(
            userID, request.CanvasID, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("画布不存在或尚未保存到服务端，请先完成画布同步");

        // 幂等查询前先固定生效偏好文档：无显式 revision 的重试必须指向同一不可变输入。
        AgentProfileViewDto profileView = await _profiles.ProfileForScopeAsync(
            userID, "", request.CanvasID, cancellationToken).ConfigureAwait(false);
        CloudAgentProfileSnapshotDto profile = CloudAgentProfileSnapshots.FromView(profileView);
        if (request.ProfileRevision.Length > 0 && request.ProfileRevision != profile.Revision)
        {
            throw CreationConflict("Agent 偏好已变化，请重新读取后提交");
        }
        request.ProfileRevision = profile.Revision;

        string id = CloudAgentContracts.AgentID(userID, request.IdempotencyKey);
        string fingerprint = CloudAgentContracts.Fingerprint(request, parentID);
        try
        {
            (TaskEntity existing, CloudAgentStateDto stored) = await LoadTaskAsync(userID, id, cancellationToken)
                .ConfigureAwait(false);
            if (stored.Fingerprint.Length == 0 || stored.Fingerprint != fingerprint)
            {
                throw AppError.New(409, "幂等键已用于不同请求，请使用新的幂等键");
            }
            return await GetAsync(userID, existing.ID, cancellationToken).ConfigureAwait(false);
        }
        catch (AppError lookupError) when (lookupError.Status == 404)
        {
            // 正常路径：同键尚无运行。
        }

        List<CloudAgentTextMessageDto> history = [];
        CloudAgentCreativeAnchorDto creativeAnchor = new();
        if (parentID.Length > 0)
        {
            (TaskEntity parent, CloudAgentStateDto _) =
                await LoadTaskAsync(userID, parentID, cancellationToken).ConfigureAwait(false);
            if (parent.ProjectID != request.CanvasID)
            {
                throw AppError.Forbidden("不能跨画布追加 Agent 消息");
            }
            if (parent.Status is TaskStatus.TaskStatusQueued or TaskStatus.TaskStatusRunning)
            {
                throw AppError.New(409, "上一轮仍在执行，请等待结束");
            }
            // 运行时批次接入 advanceCloudAgentByID（PENDING #68）。
            CloudAgentRunDto parentRun = await GetAsync(userID, parentID, cancellationToken).ConfigureAwait(false);
            if (!CloudAgentContracts.IsRunTerminal(parentRun.Status) || parentRun.CleanupPending)
            {
                throw AppError.New(409, "上一轮 Agent 尚未结束");
            }
            CloudAgentExecution? parentExecution = await _repository.CloudAgentAsync(
                userID, parentID, cancellationToken).ConfigureAwait(false)
                ?? throw AppError.NotFound("Agent 运行不存在");
            CloudAgentRuntimeDto parentState;
            try
            {
                parentState = CloudAgentContracts.Decode(parentExecution);
            }
            catch (InvalidOperationException cause)
            {
                throw AppError.Wrap(409, "上一轮 Agent 历史记录不完整，无法继续对话；请新建对话", cause);
            }
            if (parentState.CreativeAnchor is not null)
            {
                creativeAnchor = parentState.CreativeAnchor;
            }
            history = parentState.TextHistory ?? LegacyHistory(parentState.Canonical.Messages, parent.Prompt);
            string text = ContinuationReply(parent, parentRun);
            // 用户目标必须能熬过失败的首次模型调用；工具事实只是上下文，不构成重放授权。
            history.Add(new CloudAgentTextMessageDto("user", parent.Prompt));
            history.Add(new CloudAgentTextMessageDto("assistant", text));
        }
        string encodedHistory = JsonSerializer.Serialize(history, GoJson.WriteOptions);
        if (encodedHistory.Length > 64000)
        {
            throw AppError.BadAuthRequest("对话上下文超过 64KB，请新建对话");
        }
        CloudAgentCreativeAnchorDto? inheritedAnchor =
            creativeAnchor.Version > 0 ? creativeAnchor : null;
        creativeAnchor = await CloudAgentAnchors.BuildAsync(
            _repository, userID, canvas, request.Prompt, inheritedAnchor, cancellationToken).ConfigureAwait(false);
        List<CloudAgentSkillDto> skillSnapshots =
            await CloudAgentSkills.SnapshotAsync(_skills, userID, request.SkillIDs, cancellationToken)
                .ConfigureAwait(false);
        string canvasSummary = "";
        if (request.ContextScope.Count != 0)
        {
            canvasSummary = CanvasSummary(canvas);
        }
        (string system, CloudAgentPolicySnapshotDto policy) = await CloudAgentPolicyCompiler.CompileAsync(
            request, skillSnapshots, canvasSummary, profile,
            creativeAnchor.Version > 0 ? creativeAnchor : null, cancellationToken).ConfigureAwait(false);
        CloudAgentStateDto state = new()
        {
            Version = 1,
            Request = request,
            ParentID = parentID,
            Fingerprint = fingerprint,
            CreativeAnchor = creativeAnchor.Version > 0 ? creativeAnchor : null,
            Skills = skillSnapshots,
            Profile = profile,
            Policy = policy,
        };
        CloudAgentCanonicalRequestDto canonical = Canonical(system, history, request.Prompt, request.CanvasID);
        Dictionary<string, JsonElement> input = new(StringComparer.Ordinal)
        {
            ["mode"] = JsonSerializer.SerializeToElement("text"),
            ["prompt"] = JsonSerializer.SerializeToElement(request.Prompt),
            ["textHistory"] = JsonSerializer.SerializeToElement(history, GoJson.WriteOptions),
            ["textOptions"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["stream"] = JsonSerializer.SerializeToElement(true),
                ["thinking"] = JsonSerializer.SerializeToElement(
                    CloudAgentPolicyCompiler.ReasoningEnabled(policy.ReasoningMode)),
            }),
            ["cloudAgent"] = JsonSerializer.SerializeToElement(state, GoJson.WriteOptions),
            ["agentRequests"] = JsonSerializer.SerializeToElement(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["canonical"] = JsonSerializer.SerializeToElement(canonical, GoJson.WriteOptions),
                }),
            ["config"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["channelId"] = JsonSerializer.SerializeToElement(request.ChannelID),
                ["channelModelKey"] = JsonSerializer.SerializeToElement(request.ChannelModelKey),
                ["model"] = JsonSerializer.SerializeToElement(
                    CloudAgentContracts.FirstNonEmpty(request.ChannelModelKey, request.Model)),
                ["systemPrompt"] = JsonSerializer.SerializeToElement(system),
            }),
        };
        long maxCharge = (long)Math.Floor(request.Budget.MaxCredits * CreditPolicyService.CreditScale);
        CreateTaskRequestDto taskRequest = new()
        {
            ProjectID = request.CanvasID,
            Type = "canvas_text",
            Operation = CloudAgentContracts.CloudAgentOperation,
            Prompt = request.Prompt,
            Model = request.Model,
            LogicalModelID = request.LogicalModelID,
            Input = input,
        };
        TaskEntity task;
        try
        {
            task = await _taskCreation.CreateQueuedAsync(
                userID, taskRequest, input, "canvas_text", request.Prompt,
                "", "",
                cancellationToken,
                new TaskAdmission(id, maxCharge)).ConfigureAwait(false);
        }
        catch (System.Data.Common.DbException)
        {
            // 并发的相同请求可能赢得了事务；绝不覆盖其结果或二次预留积分。
            (TaskEntity winner, CloudAgentStateDto stored) =
                await LoadTaskAsync(userID, id, cancellationToken).ConfigureAwait(false);
            if (stored.Fingerprint.Length == 0 || stored.Fingerprint != fingerprint)
            {
                throw AppError.New(409, "幂等键已用于不同请求");
            }
            return await GetAsync(userID, winner.ID, cancellationToken).ConfigureAwait(false);
        }
        return await GetAsync(userID, task.ID, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 读取运行

    /// <summary>读取一轮运行。对应 Go: <c>CloudAgentRun</c>。</summary>
    public async Task<CloudAgentRunDto> GetAsync(string userID, string id, CancellationToken cancellationToken)
    {
        (TaskEntity task, CloudAgentStateDto state) = await LoadTaskAsync(userID, id, cancellationToken)
            .ConfigureAwait(false);
        // 持久执行记录一旦存在即权威；ensure 只用于尚未建执行行的旧根任务。
        if (await _repository.CloudAgentAsync(userID, id, cancellationToken).ConfigureAwait(false) is null)
        {
            await EnsureExecutionAsync(task, state, cancellationToken).ConfigureAwait(false);
        }
        return await OutputAsync(task, state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>空闲事件流的轻量变更探测。对应 Go: <c>CloudAgentRunIfChanged</c>。</summary>
    public async Task<CloudAgentRunDto?> GetIfChangedAsync(
        string userID, string id, long revision, CancellationToken cancellationToken)
    {
        long? current = await _repository.CloudAgentRevisionAsync(userID, id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.NotFound("Agent 运行不存在");
        if (current == revision)
        {
            return null;
        }
        return await GetAsync(userID, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>加载并校验 Agent 任务与其冻结状态；不匹配一律 404。对应 Go: <c>cloudAgentTask</c>。</summary>
    public async Task<(TaskEntity Task, CloudAgentStateDto State)> LoadTaskAsync(
        string userID, string id, CancellationToken cancellationToken)
    {
        CloudAgentStateDto inputState = new();
        TaskEntity? task = await _repository.TaskForUserAsync(userID, id, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("Agent 运行不存在");
        if (task.Operation != CloudAgentContracts.CloudAgentOperation)
        {
            throw AppError.NotFound("Agent 运行不存在");
        }
        try
        {
            Dictionary<string, JsonElement>? input = string.IsNullOrEmpty(task.InputJSON)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(task.InputJSON, GoJson.ReadOptions);
            if (input is not null && input.TryGetValue("cloudAgent", out JsonElement agentElement))
            {
                CloudAgentStateDto? parsed = JsonSerializer.Deserialize<CloudAgentStateDto>(
                    agentElement, GoJson.ReadOptions);
                if (parsed is not null)
                {
                    inputState = parsed;
                }
            }
        }
        catch (JsonException)
        {
            // 走执行记录回退路径。
        }
        if (inputState.Version == 1 && task.ID == CloudAgentContracts.AgentID(userID, inputState.Request.IdempotencyKey))
        {
            return (task, inputState);
        }
        // 成功、失败和取消任务的 InputJSON 都可能被存储配额压缩；执行记录才是持久来源。
        CloudAgentExecution? run = await _repository.CloudAgentAsync(userID, id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.NotFound("Agent 运行不存在");
        CloudAgentRuntimeDto state;
        try
        {
            state = CloudAgentContracts.Decode(run);
        }
        catch (InvalidOperationException)
        {
            if (CloudAgentContracts.IsTaskTerminal(task.Status) || CloudAgentContracts.IsRunTerminal(run.Status))
            {
                // 终态运行仍可查询，但绝不从损坏状态伪造审批、权限或活动任务。
                return (task, new CloudAgentStateDto
                {
                    Version = 1,
                    Request = new CloudAgentRequestDto { CanvasID = task.ProjectID },
                });
            }
            throw AppError.NotFound("Agent 运行不存在");
        }
        if (task.ID != CloudAgentContracts.AgentID(userID, state.Request.IdempotencyKey)
            || task.ProjectID != state.Request.CanvasID)
        {
            throw AppError.NotFound("Agent 运行不存在");
        }
        return (task, new CloudAgentStateDto
        {
            Version = 1,
            Request = state.Request,
            ParentID = state.ParentID,
            Fingerprint = state.Fingerprint,
            CreativeAnchor = state.CreativeAnchor,
            Skills = state.Skills,
            Profile = state.Profile,
            Policy = state.Policy,
        });
    }

    /// <summary>为旧根任务补建执行行。对应 Go: <c>ensureCloudAgentExecution</c>。</summary>
    public async Task EnsureExecutionAsync(
        TaskEntity task, CloudAgentStateDto initial, CancellationToken cancellationToken)
    {
        List<CloudAgentTextMessageDto> textHistory = [];
        CloudAgentCanonicalRequestDto canonical = new();
        if (!string.IsNullOrEmpty(task.InputJSON))
        {
            try
            {
                Dictionary<string, JsonElement>? input = JsonSerializer.Deserialize<
                    Dictionary<string, JsonElement>>(task.InputJSON, GoJson.ReadOptions);
                if (input is not null)
                {
                    if (input.TryGetValue("textHistory", out JsonElement historyElement)
                        && historyElement.ValueKind == JsonValueKind.Array)
                    {
                        textHistory = JsonSerializer.Deserialize<List<CloudAgentTextMessageDto>>(
                            historyElement, GoJson.ReadOptions) ?? [];
                    }
                    if (input.TryGetValue("agentRequests", out JsonElement requestsElement)
                        && requestsElement.ValueKind == JsonValueKind.Object
                        && requestsElement.TryGetProperty("canonical", out JsonElement canonicalElement))
                    {
                        canonical = JsonSerializer.Deserialize<CloudAgentCanonicalRequestDto>(
                            canonicalElement, GoJson.ReadOptions) ?? new CloudAgentCanonicalRequestDto();
                    }
                }
            }
            catch (JsonException cause)
            {
                throw new InvalidOperationException(cause.Message, cause);
            }
        }
        CloudAgentRuntimeDto state = new()
        {
            Request = initial.Request,
            Policy = initial.Policy,
            ParentID = initial.ParentID,
            Fingerprint = initial.Fingerprint,
            CreativeAnchor = initial.CreativeAnchor,
            TextHistory = textHistory,
            Skills = initial.Skills ?? [],
            Profile = initial.Profile,
            Canonical = canonical,
            ActiveTaskID = task.ID,
            TaskIDs = [task.ID],
            Step = 1,
            Decisions = new Dictionary<string, string>(StringComparer.Ordinal),
            Events = [],
        };
        if ((initial.Skills?.Count ?? 0) > 0)
        {
            CloudAgentContracts.AddEvent(state, task.ID, "tool_completed", CloudAgentContracts.Payload(
                ("toolName", "skills_load"),
                ("text", $"已启用 {initial.Skills!.Count} 个技能，正文将按需读取")));
        }
        CloudAgentExecution run = new()
        {
            ID = task.ID,
            UserID = task.UserID,
            Status = "running",
            Revision = 1,
            CreatedAt = task.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
        };
        CloudAgentContracts.Save(run, state);
        await _repository.EnsureCloudAgentAsync(run, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>对外输出装配。对应 Go: <c>cloudAgentExecutionOutput</c>。</summary>
    private async Task<CloudAgentRunDto> OutputAsync(
        TaskEntity task, CloudAgentStateDto initial, CancellationToken cancellationToken)
    {
        CloudAgentExecution run = await _repository.CloudAgentAsync(task.UserID, task.ID, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.NotFound("Agent 运行不存在");
        CloudAgentRuntimeDto state;
        bool stateDecoded = true;
        try
        {
            state = CloudAgentContracts.Decode(run);
        }
        catch (InvalidOperationException)
        {
            stateDecoded = false;
            state = new CloudAgentRuntimeDto
            {
                Request = initial.Request,
                ParentID = initial.ParentID,
                CreativeAnchor = initial.CreativeAnchor,
                Skills = initial.Skills ?? [],
                Profile = initial.Profile,
                TaskIDs = [task.ID],
                Events = [],
                Decisions = new Dictionary<string, string>(StringComparer.Ordinal),
            };
        }
        CloudAgentRunDto output = AgentRunOutput(task, initial);
        output.Status = run.Status;
        output.Revision = run.Revision;
        output.CleanupPending = run.CleanupPending;
        output.FailureMessage = run.FailureMessage;
        output.UpdatedAt = run.UpdatedAt;
        output.Events = state.Events;
        output.Approval = state.Approval;
        if (CloudAgentContracts.IsRunTerminal(run.Status))
        {
            output.Approval = null;
        }
        output.Step = state.Step;
        if (stateDecoded && state.ActiveTaskID.Length > 0
            && run.Status is "running" or "queued")
        {
            TaskEntity? active = await _repository.TaskForUserAsync(
                task.UserID, state.ActiveTaskID, cancellationToken).ConfigureAwait(false)
                ?? throw AppError.NotFound("Agent 运行不存在");
            if (active.TextDraft.Length > 0)
            {
                output.ActiveMessage = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["messageId"] = active.ID,
                    ["text"] = active.TextDraft,
                };
            }
        }
        output.Skills = new List<CloudAgentSkillDto>(state.Skills.Count);
        foreach (CloudAgentSkillDto skill in state.Skills)
        {
            output.Skills.Add(new CloudAgentSkillDto
            {
                ID = skill.ID,
                Name = skill.Name,
                Version = skill.Version,
                Hash = skill.Hash,
            });
        }
        Dictionary<string, BillingOrder> orders = await _repository.BillingOrdersByTaskIDsAsync(
            task.UserID, state.TaskIDs, cancellationToken).ConfigureAwait(false);
        foreach (BillingOrder order in orders.Values)
        {
            output.SpentCredits += order.AmountMicrocredits / (double)CreditPolicyService.CreditScale;
        }
        return output;
    }

    /// <summary>基础输出。对应 Go: <c>agentRunOutput</c>。</summary>
    private static CloudAgentRunDto AgentRunOutput(TaskEntity task, CloudAgentStateDto state)
    {
        string status = task.Status;
        if (status == TaskStatus.TaskStatusSucceeded)
        {
            status = "completed";
        }
        return new CloudAgentRunDto
        {
            ID = task.ID,
            CanvasID = task.ProjectID,
            ParentID = state.ParentID,
            Status = status,
            PermissionMode = state.Request.PermissionMode,
            Model = task.Model,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
            Skills = state.Skills ?? [],
        };
    }

    // ------------------------------------------------------------ 纯函数

    /// <summary>续聊摘要回复。对应 Go: <c>cloudAgentContinuationReply</c>。</summary>
    public static string ContinuationReply(TaskEntity task, CloudAgentRunDto run)
    {
        string text = task.Status == TaskStatus.TaskStatusSucceeded ? TaskResultText(task.ResultJSON) : "";
        List<Dictionary<string, JsonElement>> facts = [];
        foreach (CloudAgentEventDto agentEvent in run.Events ?? [])
        {
            if (agentEvent.Type == "assistant_message")
            {
                text = PayloadString(agentEvent.Payload, "text");
            }
            if (agentEvent.Type is "tool_completed" or "tool_failed" or "generation_task_created"
                or "approval_decided" or "run_failed")
            {
                if (PayloadString(agentEvent.Payload, "toolName") == "skills_load")
                {
                    continue;
                }
                Dictionary<string, JsonElement> fact = new(StringComparer.Ordinal)
                {
                    ["event"] = JsonSerializer.SerializeToElement(agentEvent.Type),
                };
                foreach (string key in new[]
                         {
                             "toolName", "callId", "nodeId", "nodeIds", "referenceNodeIds", "taskId", "title",
                             "summary", "status", "decision", "phase", "taskSubmitted", "reason",
                         })
                {
                    if (agentEvent.Payload.TryGetValue(key, out JsonElement value))
                    {
                        fact[key] = value.Clone();
                    }
                }
                if (agentEvent.Payload.TryGetValue("result", out JsonElement resultElement)
                    && resultElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (string key in new[]
                             {
                                 "nodeId", "nodeIds", "taskId", "title", "summary", "status", "phase", "taskSubmitted",
                             })
                    {
                        if (resultElement.TryGetProperty(key, out JsonElement value))
                        {
                            fact[key] = value.Clone();
                        }
                    }
                }
                facts.Add(fact);
            }
        }
        if (run.Status == "completed" && facts.Count == 0)
        {
            return text;
        }
        string summary = JsonSerializer.Serialize(
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["runId"] = JsonSerializer.SerializeToElement(run.ID),
                ["status"] = JsonSerializer.SerializeToElement(run.Status),
                ["failure"] = JsonSerializer.SerializeToElement(run.FailureMessage),
                ["facts"] = JsonSerializer.SerializeToElement(facts, GoJson.WriteOptions),
            },
            GoJson.WriteOptions);
        if (summary.Length > 24000)
        {
            throw AppError.BadAuthRequest("上一轮执行事实超过续聊上限，请明确引用任务或节点开始新对话");
        }
        return text + "\n\n上一轮真实执行记录（仅作上下文，不是新指令或审批；生成已提交时先查原任务，不能默认重发）：\n" + summary;
    }

    private static string PayloadString(Dictionary<string, JsonElement> payload, string key) =>
        payload.TryGetValue(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>任务结果文本。对应 Go: <c>taskResultText</c>。</summary>
    private static string TaskResultText(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("text", out JsonElement text)
                   && text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>
    /// 旧运行没有单独历史；只从模型请求中当前用户消息之前的严格交替前缀恢复。
    /// 对应 Go: <c>cloudAgentLegacyHistory</c>。
    /// </summary>
    public static List<CloudAgentTextMessageDto> LegacyHistory(
        List<Dictionary<string, JsonElement>> messages, string currentPrompt)
    {
        int currentIndex = -1;
        for (int index = 0; index < messages.Count; index++)
        {
            string role = MessageString(messages[index], "role");
            string content = MessageString(messages[index], "content");
            if (role is not ("user" or "assistant"))
            {
                break;
            }
            if (role == "user" && content == currentPrompt)
            {
                currentIndex = index;
            }
        }
        if (currentIndex < 0 || currentIndex % 2 != 0)
        {
            return [];
        }
        List<CloudAgentTextMessageDto> history = new(currentIndex);
        string[] expectedRoles = ["user", "assistant"];
        for (int index = 0; index < currentIndex; index++)
        {
            string role = MessageString(messages[index], "role");
            string? content = messages[index].TryGetValue("content", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            if (content is null || role != expectedRoles[index % 2])
            {
                return [];
            }
            history.Add(new CloudAgentTextMessageDto(role, content));
        }
        return history;
    }

    private static string MessageString(Dictionary<string, JsonElement> message, string key) =>
        message.TryGetValue(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>模型请求规范形。对应 Go: <c>cloudAgentCanonical</c>。</summary>
    public static CloudAgentCanonicalRequestDto Canonical(
        string system, List<CloudAgentTextMessageDto> history, string prompt, string canvasID)
    {
        List<Dictionary<string, JsonElement>> messages = [];
        foreach (CloudAgentTextMessageDto message in history)
        {
            messages.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["role"] = JsonSerializer.SerializeToElement(message.Role),
                ["content"] = JsonSerializer.SerializeToElement(message.Content),
            });
        }
        messages.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["role"] = JsonSerializer.SerializeToElement("user"),
            ["content"] = JsonSerializer.SerializeToElement(prompt),
        });
        // 保持工具轮次间路由稳定，不暴露画布标识。
        string cacheKey = CloudAgentContracts.Sha256Hex(canvasID + "\x00" + system);
        return new CloudAgentCanonicalRequestDto
        {
            SystemPrompt = system,
            Messages = messages,
            Tools = CloudAgentTools.BuildTools(null),
            ToolChoice = JsonSerializer.SerializeToElement("auto"),
            PromptCacheKey = "cloud-agent:" + cacheKey[..48],
        };
    }

    /// <summary>画布摘要。对应 Go: <c>cloudAgentCanvasSummary</c>。</summary>
    public static string CanvasSummary(CanvasProject canvas)
    {
        JsonObject doc;
        try
        {
            doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
        }
        catch (AppError)
        {
            throw AppError.BadAuthRequest("服务端画布内容无法解析，请先重新同步");
        }
        catch (InvalidOperationException)
        {
            throw AppError.BadAuthRequest("服务端画布内容无法解析，请先重新同步");
        }
        List<JsonObject> nodes = CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "nodes"));
        JsonArray items = [];
        int included = 0;
        foreach (JsonObject node in nodes)
        {
            if (included == 80)
            {
                break;
            }
            string type = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "type"));
            (CapabilityDescriptor descriptor, bool known) = CloudAgentNodes.ForType(type);
            JsonObject item = new()
            {
                ["id"] = CloudAgentContracts.TruncateRunes(
                    CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "id")), 100),
                ["type"] = CloudAgentContracts.TruncateRunes(type, 40),
                ["title"] = CloudAgentContracts.TruncateRunes(
                    CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "title")), 300),
            };
            if (!known)
            {
                item["agentSupported"] = false;
                item["agentUnsupportedReason"] = "仅展示基础信息；当前 Agent 不支持操作此类型节点";
                items.Add(item);
                included++;
                continue;
            }
            JsonObject meta = node["metadata"] as JsonObject ?? new JsonObject();
            JsonObject projected = CloudAgentCanvasState.ProjectNodeFields(
                node, meta, descriptor, descriptor.SummaryFields, 600, precise: false, structuredOffset: 0);
            foreach ((string key, JsonNode? value) in projected)
            {
                item[key] = value?.DeepClone();
            }
            items.Add(item);
            included++;
        }
        JsonObject payload = new()
        {
            ["title"] = CloudAgentContracts.TruncateRunes(canvas.Title, 240),
            ["savedAt"] = canvas.UpdatedAt,
            ["totalNodes"] = nodes.Count,
            ["includedNodes"] = included,
            ["nodes"] = items,
        };
        string data = payload.ToJsonString();
        if (data.Length > 64000)
        {
            throw AppError.BadAuthRequest("画布摘要超过 64KB，请缩小画布后重试");
        }
        return data;
    }
}
