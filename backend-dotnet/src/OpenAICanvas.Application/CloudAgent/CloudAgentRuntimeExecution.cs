#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAICanvas.Domain.Canvas.Capability;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>
/// 运行时执行面：工具事务、模型步入队、媒体提交/完成、审批决策、取消、撤销与清理交接。
/// 对应 Go: <c>advanceCloudAgentTool</c>、<c>enqueueCloudAgentTask</c>、
/// <c>advanceCloudAgentMedia</c>、<c>DecideCloudAgentApproval</c>、<c>CancelCloudAgent</c>、
/// <c>UndoCloudAgentCanvas</c>、<c>finishCloudAgentCleanup</c>、<c>cloudAgentReadTool</c>。
/// </summary>
public sealed partial class CloudAgentRuntimeService
{
    // ------------------------------------------------------------ 工具事务

    private async Task AdvanceToolAsync(
        CloudAgentExecution run, CloudAgentRuntimeDto state, CancellationToken cancellationToken)
    {
        if (state.CallIndex < 0 || state.CallIndex >= state.Calls.Count)
        {
            await FailAsync(run, state, "Agent 工具调用状态无效，本轮已停止").ConfigureAwait(false);
            return;
        }
        CloudAgentCallDto call = state.Calls[state.CallIndex];
        if (state.Approval is not null && state.Approval.Decision.Length == 0)
        {
            return; // 等待审批。
        }
        if (state.Approval is not null && state.Approval.Decision.Length > 0
            && state.Approval.CallHash.Length > 0
            && state.Approval.CallHash != CloudAgentMutations.ApprovalCallHash(call))
        {
            await TerminateAsync(run, "审批内容与待执行操作不一致，本轮已停止").ConfigureAwait(false);
            return;
        }
        string name = call.Function.Name;
        bool allowed = CloudAgentTools.Allowed(state.Request, name);
        if (allowed && CloudAgentTools.IsWrite(name)
            && (state.Request.PermissionMode == "request_approval" || name == "generate_media")
            && state.Approval is null)
        {
            await PlanApprovalAsync(run, state, call, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (allowed && name == "generate_media"
            && state.Approval is not null && state.Approval.Decision == "approve")
        {
            await AdvanceMediaAsync(run, state, call, cancellationToken).ConfigureAwait(false);
            return;
        }
        RuntimePolicySetting policy = _runtimePolicy.Current();

        // 读目录/技能在检查点事务外完成，避免 SQLite 嵌套读。
        JsonObject? modelList = null;
        Exception? modelListError = null;
        if (allowed && name == "model_list")
        {
            try
            {
                ModelRequestIntent? intent = await _media.ModelIntentAsync(
                    run.UserID, state.Request.CanvasID, call.Function.Arguments, cancellationToken)
                    .ConfigureAwait(false);
                modelList = await _media.ModelListAsync(intent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception cause) when (cause is AppError or JsonException or CloudAgentArgumentException)
            {
                modelListError = cause;
            }
        }
        (JsonObject? skillResult, Exception? skillError) = (null, null);
        if (allowed && name == "skill_read_file")
        {
            (skillResult, skillError) = await ReadSkillFileAsync(run.UserID, state, call, cancellationToken)
                .ConfigureAwait(false);
        }

        bool claimed = await _repository.MutateCloudAgentAsync(run.UserID, run.ID, run.Revision,
            async (current, context) =>
            {
                JsonObject? result = null;
                Exception? toolError = null;
                if (!allowed)
                {
                    toolError = AppError.BadAuthRequest("工具未获本轮权限授权");
                }
                else if (name == "canvas_apply_ops")
                {
                    try
                    {
                        result = await ApplyCanvasOpsAsync(context, run, state, call, policy)
                            .ConfigureAwait(false);
                    }
                    catch (Exception cause) when (cause is AppError or CloudAgentCheckpointException or InvalidOperationException)
                    {
                        toolError = cause;
                    }
                }
                else if (name is "canvas_create_storyboard" or "canvas_edit_storyboard")
                {
                    try
                    {
                        result = await CloudAgentStructuredEdits.ApplyStoryboardMutationAsync(
                            context, run.UserID, state.Request.CanvasID, call, policy,
                            CloudAgentMutations.RecorderForRun(run.ID, state)).ConfigureAwait(false);
                    }
                    catch (Exception cause) when (cause is AppError or CloudAgentCheckpointException or InvalidOperationException)
                    {
                        toolError = cause;
                    }
                }
                else if (name == "canvas_edit_batch_table")
                {
                    try
                    {
                        result = await CloudAgentStructuredEdits.ApplyBatchTableMutationAsync(
                            context, run.UserID, state.Request.CanvasID, call, policy,
                            CloudAgentMutations.RecorderForRun(run.ID, state)).ConfigureAwait(false);
                    }
                    catch (Exception cause) when (cause is AppError or CloudAgentCheckpointException or InvalidOperationException)
                    {
                        toolError = cause;
                    }
                }
                else if (name == "model_list")
                {
                    if (modelListError is not null)
                    {
                        toolError = modelListError;
                    }
                    else
                    {
                        result = modelList;
                    }
                }
                else if (name == "skill_read_file")
                {
                    result = skillResult;
                    toolError = skillError;
                }
                else
                {
                    (result, toolError) = await ReadToolAsync(context, run.UserID, state, call)
                        .ConfigureAwait(false);
                }
                ToolResult(run.ID, state, call, result, toolError);
                CloudAgentContracts.Save(current, state);
                await context.SaveRunAsync(current).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            throw CloudAgentSessionService.CreationConflict();
        }
        run.Revision++;
    }

    /// <summary>写工具的审批规划阶段。对应 Go: <c>advanceCloudAgentTool</c> 的 Approval == nil 分支。</summary>
    private async Task PlanApprovalAsync(
        CloudAgentExecution run, CloudAgentRuntimeDto state, CloudAgentCallDto call,
        CancellationToken cancellationToken)
    {
        string name = call.Function.Name;
        RuntimePolicySetting policy = _runtimePolicy.Current();
        if (name == "generate_media")
        {
            // 与 Go 一致：prepare 与干跑准入在检查点事务外执行，事务内只写草稿节点与审批状态。
            CloudAgentMediaPlan plan;
            string modelName;
            try
            {
                await using CloudAgentMutationContext planningContext =
                    await _repository.OpenCloudAgentContextAsync(cancellationToken).ConfigureAwait(false);
                (CreateTaskRequestDto request, CloudAgentMediaPlan prepared) = await _media.PrepareAsync(
                    planningContext, run, state, call, cancellationToken).ConfigureAwait(false);
                // 干跑准入：不落库、不计费，仅校验模型与规格。
                await _taskCreation.AdmitQueuedAsync(
                    run.UserID, request, request.Input, request.Type!,
                    request.Prompt, "", "", cancellationToken).ConfigureAwait(false);
                plan = prepared;
                modelName = await _media.ModelNameAsync(plan.Args, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception cause) when (
                cause is AppError or CloudAgentArgumentException or InvalidOperationException
                or System.Data.Common.DbException)
            {
                await MediaErrorAsync(run, state, "admission", submitted: false, terminal: false, cause)
                    .ConfigureAwait(false);
                return;
            }
            bool claimed = await _repository.MutateCloudAgentAsync(run.UserID, run.ID, run.Revision,
                (current, context) => MutatePlanApprovalAsync(
                    current, context, run, state, call, policy, plan, modelName),
                cancellationToken).ConfigureAwait(false);
            if (!claimed)
            {
                throw CloudAgentSessionService.CreationConflict();
            }
            run.Revision++;
            return;
        }
        bool claimedPlan = await _repository.MutateCloudAgentAsync(run.UserID, run.ID, run.Revision,
            (current, context) => MutatePlanApprovalAsync(current, context, run, state, call, policy, plan: null, modelName: ""),
            cancellationToken).ConfigureAwait(false);
        if (!claimedPlan)
        {
            throw CloudAgentSessionService.CreationConflict();
        }
        run.Revision++;
    }

    private async Task MutatePlanApprovalAsync(
        CloudAgentExecution current, CloudAgentMutationContext context, CloudAgentExecution run,
        CloudAgentRuntimeDto state, CloudAgentCallDto call, RuntimePolicySetting policy,
        CloudAgentMediaPlan? plan, string modelName)
    {
        string name = call.Function.Name;
        CloudAgentApprovalPreviewDto preview;
        if (plan is not null)
        {
            await _media.CreateMediaNodeAsync(
                context, run.UserID, state.Request.CanvasID, plan, task: null, policy,
                CloudAgentMutations.RecorderForRun(run.ID, state), state, run.ID).ConfigureAwait(false);
            CanvasProject? canvas = await context.CanvasProjectForUserAsync(
                run.UserID, state.Request.CanvasID).ConfigureAwait(false)
                ?? throw AppError.NotFound("画布不存在");
            JsonObject doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
            plan.Args.SnapshotHash = CloudAgentContracts.MediaContentHash(doc);
            call.Function.Arguments = JsonSerializer.Serialize(plan.Args, GoJson.WriteOptions);
            state.Calls[state.CallIndex] = call;
            preview = CloudAgentMediaService.ApprovalPreview(plan, modelName);
        }
        else
        {
            try
            {
                if (name is "canvas_create_storyboard" or "canvas_edit_storyboard")
                {
                    preview = (await CloudAgentStructuredEdits.PrepareStoryboardAsync(
                        context, run.UserID, state.Request.CanvasID, call, name).ConfigureAwait(false)).Preview;
                }
                else if (name == "canvas_edit_batch_table")
                {
                    preview = (await CloudAgentStructuredEdits.PrepareBatchTableAsync(
                        context, run.UserID, state.Request.CanvasID, call).ConfigureAwait(false)).Preview;
                }
                else
                {
                    preview = (await CloudAgentMutations.PrepareCanvasMutationAsync(
                        context, run.UserID, state.Request.CanvasID, call).ConfigureAwait(false)).Preview;
                }
            }
            catch (CloudAgentArgumentException cause)
            {
                // 参数语法错误可由模型修复：记一次工具失败，不终止。
                ToolResult(run.ID, state, call, null, cause);
                CloudAgentContracts.Save(current, state);
                await context.SaveRunAsync(current).ConfigureAwait(false);
                return;
            }
        }
        state.Approval = new CloudAgentApprovalDto
        {
            ID = $"{run.ID}-{state.Step}-{state.CallIndex}",
            Call = call,
            CallHash = CloudAgentMutations.ApprovalCallHash(call),
            Preview = preview,
            ModelName = modelName,
        };
        current.Status = "waiting_approval";
        CloudAgentContracts.AddEvent(state, run.ID, "approval_requested", CloudAgentContracts.Payload(
            ("approvalId", state.Approval.ID),
            ("toolName", name),
            ("modelName", modelName),
            ("arguments", JsonSerializer.SerializeToElement(call.Function.Arguments)),
            ("preview", JsonSerializer.SerializeToElement(preview, GoJson.WriteOptions)),
            ("text", preview.Description)));
        CloudAgentContracts.Save(current, state);
        await context.SaveRunAsync(current).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 模型步入队

    private async Task EnqueueTaskAsync(
        CloudAgentExecution run, CloudAgentRuntimeDto state, string taskType, string prompt,
        string model, string logicalModelID, Dictionary<string, JsonElement> input,
        CloudAgentMediaPlan? media, CancellationToken cancellationToken)
    {
        Dictionary<string, BillingOrder> existingOrders = await _repository.BillingOrdersByTaskIDsAsync(
            run.UserID, state.TaskIDs, cancellationToken).ConfigureAwait(false);
        long remaining = (long)Math.Floor(state.Request.Budget.MaxCredits * CreditPolicyService.CreditScale);
        foreach (BillingOrder existingOrder in existingOrders.Values)
        {
            remaining -= existingOrder.AmountMicrocredits;
        }
        if (remaining < 0)
        {
            await FailAsync(run, state, "Agent 累计预算已耗尽").ConfigureAwait(false);
            return;
        }
        string stepTaskID = CloudAgentContracts.AgentID(
            run.UserID, $"{run.ID}:task:{state.TaskIDs.Count}");
        CreateTaskRequestDto request = new()
        {
            ProjectID = state.Request.CanvasID,
            Type = taskType,
            Operation = media is null ? "cloud_agent_step" : "cloud_agent_media",
            Prompt = prompt,
            Model = model,
            LogicalModelID = logicalModelID,
            Input = input,
        };
        TaskEntity task;
        BillingOrder? order;
        try
        {
            (TaskEntity admittedTask, BillingOrder? admittedOrder) = await _taskCreation.AdmitQueuedAsync(
                run.UserID, request, input, taskType, prompt, "", "", cancellationToken,
                new TaskAdmission(stepTaskID, remaining)).ConfigureAwait(false);
            task = admittedTask;
            order = admittedOrder;
        }
        catch (Exception cause)
            when (cause is AppError or InvalidOperationException or System.Data.Common.DbException
                or JsonException)
        {
            if (media is not null)
            {
                await MediaErrorAsync(run, state, "admission", submitted: false, terminal: false, cause)
                    .ConfigureAwait(false);
            }
            else
            {
                await FailAsync(run, state, SafeToolError(cause)).ConfigureAwait(false);
            }
            return;
        }
        if (media is not null)
        {
            Dictionary<string, JsonElement>? requestedConfig = InputConfig(input);
            Dictionary<string, JsonElement>? resolvedConfig = InputConfigInputJson(task.InputJSON);
            try
            {
                CloudAgentMediaService.ValidateResolvedOptions(requestedConfig!, resolvedConfig!);
            }
            catch (AppError cause)
            {
                await MediaErrorAsync(run, state, "admission", submitted: false, terminal: false, cause)
                    .ConfigureAwait(false);
                return;
            }
        }
        RuntimePolicySetting policy = _runtimePolicy.Current();
        bool claimed = await _repository.MutateCloudAgentAsync(run.UserID, run.ID, run.Revision,
            async (current, context) =>
            {
                if (media is not null)
                {
                    await _media.CreateMediaNodeAsync(
                        context, run.UserID, state.Request.CanvasID, media, task, policy,
                        CloudAgentMutations.RecorderForRun(run.ID, state), state, run.ID).ConfigureAwait(false);
                }
                await context.CreateTaskWithQuotaAsync(task, order, policy.Task.ActiveTaskLimit)
                    .ConfigureAwait(false);
                state.TaskIDs.Add(task.ID);
                if (media is not null)
                {
                    state.MediaTaskID = task.ID;
                    state.Generations++;
                    state.VideoSeconds += media.Args.Duration;
                    CloudAgentContracts.AddEvent(state, run.ID, "generation_task_created",
                        CloudAgentContracts.Payload(
                            ("toolName", "generate_media"),
                            ("taskId", task.ID),
                            ("nodeId", media.Args.NodeID),
                            ("title", media.Args.Title),
                            ("mode", media.Args.Mode),
                            ("canvasId", state.Request.CanvasID),
                            ("referenceNodeIds", JsonSerializer.SerializeToElement(media.Args.ReferenceNodeIDs)),
                            ("text", "媒体节点与引用连线已创建，生成任务已提交")));
                }
                else
                {
                    state.ActiveTaskID = task.ID;
                    state.Step++;
                }
                CloudAgentContracts.Save(current, state);
                await context.SaveRunAsync(current).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            if (media is not null)
            {
                // 回滚可能发生在检查点编辑之后：重载最新运行后再记工具失败，
                // 绝不把过期修订变成第二个结果。
                CloudAgentExecution? latest = await _repository.CloudAgentAsync(
                    run.UserID, run.ID, cancellationToken).ConfigureAwait(false);
                if (latest is null || latest.Revision != run.Revision)
                {
                    throw CloudAgentSessionService.CreationConflict();
                }
                CloudAgentRuntimeDto fresh = Decode(latest);
                await MediaErrorAsync(latest, fresh, "admission", submitted: false, terminal: false,
                    CloudAgentSessionService.CreationConflict()).ConfigureAwait(false);
            }
            throw CloudAgentSessionService.CreationConflict();
        }
        run.Revision++;
    }

    private static Dictionary<string, JsonElement> InputConfig(Dictionary<string, JsonElement> input)
    {
        if (input.TryGetValue("config", out JsonElement config)
            && config.ValueKind == JsonValueKind.Object)
        {
            Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
            foreach (JsonProperty property in config.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }
            return result;
        }
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }

    private static Dictionary<string, JsonElement>? InputConfigInputJson(string inputJSON)
    {
        if (string.IsNullOrEmpty(inputJSON))
        {
            return null;
        }
        try
        {
            Dictionary<string, JsonElement>? input = JsonSerializer.Deserialize<
                Dictionary<string, JsonElement>>(inputJSON, GoJson.ReadOptions);
            return input is null ? null : InputConfig(input);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------ 媒体提交与完成

    /// <summary>媒体错误检查点。对应 Go: <c>cloudAgentMediaError</c>。</summary>
    private Task MediaErrorAsync(
        CloudAgentExecution run, CloudAgentRuntimeDto state, string phase,
        bool submitted, bool terminal, Exception cause) =>
        _repository.MutateCloudAgentAsync(run.UserID, run.ID, run.Revision, (current, context) =>
        {
            if (state.CallIndex < 0 || state.CallIndex >= state.Calls.Count)
            {
                current.Status = "failed";
                CloudAgentContracts.AddEvent(state, run.ID, "run_failed",
                    CloudAgentContracts.Payload(("text", "Agent 媒体调用状态无效，本轮已停止")));
                CloudAgentContracts.Save(current, state);
                return context.SaveRunAsync(current);
            }
            JsonObject detail = new()
            {
                ["phase"] = phase,
                ["taskSubmitted"] = submitted,
            };
            ToolResult(run.ID, state, state.Calls[state.CallIndex], detail, cause);
            if (submitted)
            {
                state.MediaTaskID = "";
            }
            if (terminal)
            {
                current.Status = "failed";
                CloudAgentContracts.AddEvent(state, run.ID, "run_failed",
                    CloudAgentContracts.Payload(("text", "媒体任务已提交，但结果处理失败；任务不会自动重试")));
            }
            CloudAgentContracts.Save(current, state);
            return context.SaveRunAsync(current);
        });

    /// <summary>已批准媒体调用：等待完成或提交任务。对应 Go: <c>advanceCloudAgentMedia</c>。</summary>
    private async Task AdvanceMediaAsync(
        CloudAgentExecution run, CloudAgentRuntimeDto state, CloudAgentCallDto call,
        CancellationToken cancellationToken)
    {
        if (state.MediaTaskID.Length > 0)
        {
            TaskEntity? task = await _repository.TaskForUserAsync(
                run.UserID, state.MediaTaskID, cancellationToken).ConfigureAwait(false);
            if (task is null)
            {
                await MediaErrorAsync(run, state, "completion", submitted: true, terminal: true,
                    AppError.BadAuthRequest("媒体任务不存在或已失去归属，结果未回写画布")).ConfigureAwait(false);
                return;
            }
            if (task.Status is TaskStatus.TaskStatusQueued or TaskStatus.TaskStatusRunning)
            {
                return;
            }
            RuntimePolicySetting policy = _runtimePolicy.Current();
            bool claimed = await _repository.MutateCloudAgentAsync(run.UserID, run.ID, run.Revision,
                async (current, context) =>
                {
                    CanvasProject? before = await context.CanvasProjectForUserAsync(
                        run.UserID, state.Request.CanvasID).ConfigureAwait(false);
                    string nodeID;
                    try
                    {
                        nodeID = await _media.CompleteMediaNodeAsync(
                            context, run.UserID, state.Request.CanvasID, task, policy).ConfigureAwait(false);
                    }
                    catch (Exception writeError) when (
                        writeError is AppError error && error.Status is 400 or 409
                            || writeError is InvalidOperationException
                            || writeError.Message == "record not found")
                    {
                        // 只有匹配节点才需要画布增量；写失败时仍要记录工具失败。
                        nodeID = await CompleteLenientAsync(
                            context, run.UserID, state.Request.CanvasID, task, policy).ConfigureAwait(false);
                        await RecordMediaCompletionAsync(
                            current, context, run, state, call, task, nodeID, before, writeError)
                            .ConfigureAwait(false);
                        return;
                    }
                    await RecordMediaCompletionAsync(
                        current, context, run, state, call, task, nodeID, before, null)
                        .ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            if (!claimed)
            {
                throw CloudAgentSessionService.CreationConflict();
            }
            run.Revision++;
            return;
        }
        (CreateTaskRequestDto Request, CloudAgentMediaPlan Plan)? preparedPair =
            await _repository.MutateCloudAgentAsync(
                run.UserID, run.ID, run.Revision,
                async (current, context) =>
                {
                    (CreateTaskRequestDto Request, CloudAgentMediaPlan Plan) prepared =
                        await _media.PrepareAsync(context, run, state, call, cancellationToken)
                            .ConfigureAwait(false);
                    current.Status = "running";
                    CloudAgentContracts.Save(current, state);
                    await context.SaveRunAsync(current).ConfigureAwait(false);
                    return prepared;
                }, cancellationToken).ConfigureAwait(false);
        if (preparedPair is null)
        {
            throw CloudAgentSessionService.CreationConflict();
        }
        (CreateTaskRequestDto request, CloudAgentMediaPlan plan) = preparedPair.Value;
        run.Revision++;
        await EnqueueTaskAsync(run, state, request.Type!, request.Prompt,
            request.Model, request.LogicalModelID, request.Input, plan, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>canvas_apply_ops 执行。对应 Go: <c>applyCloudAgentCanvas</c>。</summary>
    private async Task<JsonObject> ApplyCanvasOpsAsync(
        CloudAgentMutationContext context, CloudAgentExecution run, CloudAgentRuntimeDto state,
        CloudAgentCallDto call, RuntimePolicySetting policy)
    {
        (CanvasProject canvas, JsonObject document, string beforeJSON, string beforeHash,
            CloudAgentApprovalPreviewDto preview, CloudAgentMutations.CanvasArgs args) =
            await CloudAgentMutations.PrepareCanvasMutationAsync(
                context, run.UserID, state.Request.CanvasID, call).ConfigureAwait(false);
        await CloudAgentMutations.SaveDocumentAsync(context, canvas, document, policy).ConfigureAwait(false);
        await CloudAgentMutations.RecordAsync(context, new CloudAgentMutationInput
        {
            UserID = run.UserID,
            CanvasID = state.Request.CanvasID,
            StepID = call.ID,
            Operation = "canvas_apply_ops",
            BeforeSnapshotHash = beforeHash,
            AfterSnapshotHash = CloudAgentContracts.CanvasHash(document),
            BeforeJSON = beforeJSON,
            Preview = preview,
        }).ConfigureAwait(false);
        await CloudAgentMutations.EmitCanvasChangeOnlyAsync(context, run.ID, state,
            new CloudAgentMutationInput
            {
                UserID = run.UserID,
                CanvasID = state.Request.CanvasID,
                Operation = "canvas_apply_ops",
                BeforeJSON = beforeJSON,
                Preview = preview,
            }).ConfigureAwait(false);
        _ = args;
        return new JsonObject
        {
            ["canvasId"] = state.Request.CanvasID,
            ["snapshotHash"] = CloudAgentContracts.CanvasHash(document),
            ["summary"] = $"已完成 {args.Ops.Count} 项节点/连线操作",
            ["preview"] = JsonSerializer.SerializeToNode(preview, GoJson.WriteOptions),
        };
    }

    private async Task<string> CompleteLenientAsync(
        CloudAgentMutationContext context, string userID, string canvasID,
        TaskEntity task, RuntimePolicySetting policy)
    {
        try
        {
            return await _media.CompleteMediaNodeAsync(context, userID, canvasID, task, policy)
                .ConfigureAwait(false);
        }
        catch (Exception cause) when (cause is AppError error && error.Status is 400 or 409
            || cause is InvalidOperationException || cause.Message == "record not found")
        {
            return "";
        }
    }

    private async Task RecordMediaCompletionAsync(
        CloudAgentExecution current, CloudAgentMutationContext context, CloudAgentExecution run,
        CloudAgentRuntimeDto state, CloudAgentCallDto call, TaskEntity task, string nodeID,
        CanvasProject? before, Exception? writeError)
    {
        if (nodeID.Length > 0 && before is not null)
        {
            await CloudAgentMutations.EmitCanvasChangeOnlyAsync(
                context, run.ID, state, new CloudAgentMutationInput
                {
                    UserID = run.UserID,
                    CanvasID = state.Request.CanvasID,
                    Operation = "generate_media_complete",
                    BeforeJSON = before.PayloadJSON,
                }).ConfigureAwait(false);
        }
        JsonObject result = new()
        {
            ["phase"] = "completion",
            ["taskSubmitted"] = true,
            ["taskId"] = task.ID,
            ["nodeId"] = nodeID,
            ["status"] = task.Status,
        };
        Exception? toolError = null;
        if (task.Status != TaskStatus.TaskStatusSucceeded)
        {
            toolError = AppError.BadAuthRequest(
                "媒体任务" + task.Status + "：" + CloudAgentMediaService.SafeMediaTaskError(task)
                + "；请在任务中心查看任务 " + task.ID + "，不会自动重试收费生成");
            result["summary"] = "媒体任务未成功；结果保留在任务中心";
        }
        if (writeError is not null)
        {
            toolError = AppError.BadAuthRequest("生成结果未能安全回写画布；任务结果保留在任务中心，不会自动重试收费生成");
            result["summary"] = "未回写画布；任务结果保留在任务中心";
        }
        if (task.Status == TaskStatus.TaskStatusSucceeded && writeError is null)
        {
            result["summary"] = "生成结果已回写画布节点";
        }
        // 工具诊断保持为最后一个事件；终态事件先行，保证失败完成有明确的 run 级信号。
        if (task.Status != TaskStatus.TaskStatusSucceeded || writeError is not null)
        {
            if (current.Status != "cancelled")
            {
                current.Status = "failed";
            }
            CloudAgentContracts.AddEvent(state, run.ID, "run_failed", CloudAgentContracts.Payload(
                ("text", "媒体任务已提交，但结果处理失败；任务不会自动重试"), ("taskId", task.ID)));
        }
        ToolResult(run.ID, state, call, result, toolError);
        state.MediaTaskID = "";
        CloudAgentContracts.Save(current, state);
        await context.SaveRunAsync(current).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 审批决策

    /// <summary>审批决策。对应 Go: <c>DecideCloudAgentApproval</c>。</summary>
    public async Task DecideAsync(
        string userID, string id, string approvalID, string decision, string reason,
        CloudAgentMediaSettings? settings, CancellationToken cancellationToken)
    {
        if (settings is not null && decision != "approve")
        {
            throw AppError.BadAuthRequest("仅批准生成时可修改生成参数");
        }
        if (decision is not ("approve" or "reject"))
        {
            throw AppError.BadAuthRequest("无效审批决定");
        }
        if (reason.Length > 2000)
        {
            throw AppError.BadAuthRequest("审批理由过长");
        }
        await _sessions.LoadTaskAsync(userID, id, cancellationToken).ConfigureAwait(false);
        CloudAgentExecution? run = await _repository.CloudAgentAsync(userID, id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.NotFound("Agent 运行不存在");
        CloudAgentRuntimeDto state = await DecodeForExecutionSafeAsync(run).ConfigureAwait(false);
        if (state.Decisions.TryGetValue(approvalID, out string? previous))
        {
            bool same = previous == decision
                && (settings is null
                    || state.DecisionSettings!.TryGetValue(approvalID, out string? recorded)
                    && recorded == CreationSettingsHash(settings));
            if (same)
            {
                return;
            }
            throw CloudAgentSessionService.CreationConflict("该审批已有不同决定");
        }
        if (run.Status != "waiting_approval" || state.Approval is null
            || state.Approval.ID != approvalID)
        {
            throw CloudAgentSessionService.CreationConflict("审批不存在或已过期");
        }
        if (settings is not null)
        {
            await UpdateMediaApprovalAsync(run, state, settings, cancellationToken).ConfigureAwait(false);
        }
        bool claimed = await _repository.MutateCloudAgentAsync(userID, id, run.Revision, (current, context) =>
        {
            state.Approval!.Decision = decision;
            state.Approval.Reason = reason;
            state.Decisions[approvalID] = decision;
            if (settings is not null)
            {
                state.DecisionSettings ??= new Dictionary<string, string>(StringComparer.Ordinal);
                state.DecisionSettings[approvalID] = CreationSettingsHash(settings);
            }
            if (decision == "reject")
            {
                // 拒绝是用户控制面决策而非失败的工具调用：先于调度推进终态化。
                current.Status = "rejected";
                current.FailureMessage = "";
                state.Approval = null;
                CloudAgentContracts.AddEvent(state, id, "approval_decided", CloudAgentContracts.Payload(
                    ("approvalId", approvalID),
                    ("decision", decision),
                    ("reason", reason),
                    ("text", "已拒绝本次操作，未写入画布。你可以告诉 Agent 修改方向后重新申请。")));
                CloudAgentContracts.Save(current, state);
                return context.SaveRunAsync(current);
            }
            current.Status = "running";
            CloudAgentContracts.AddEvent(state, id, "approval_decided", CloudAgentContracts.Payload(
                ("approvalId", approvalID),
                ("decision", decision),
                ("arguments", JsonSerializer.SerializeToElement(state.Approval!.Call.Function.Arguments)),
                ("preview", JsonSerializer.SerializeToElement(state.Approval.Preview, GoJson.WriteOptions)),
                ("modelName", state.Approval.ModelName)));
            CloudAgentContracts.Save(current, state);
            return context.SaveRunAsync(current);
        }).ConfigureAwait(false);
        if (!claimed)
        {
            throw CloudAgentSessionService.CreationConflict();
        }
    }

    private static string CreationSettingsHash(CloudAgentMediaSettings settings) =>
        CloudAgentContracts.Sha256Hex(JsonSerializer.Serialize(settings, GoJson.WriteOptions));

    private async Task<CloudAgentRuntimeDto> DecodeForExecutionSafeAsync(CloudAgentExecution run)
    {
        try
        {
            return await DecodeForExecutionAsync(run).ConfigureAwait(false);
        }
        catch (AppError error) when (error.Status == 409)
        {
            throw;
        }
    }

    /// <summary>批准时修改图片生成参数（原地改 state，由决策事务统一落库）。
    /// 对应 Go: <c>updateCloudAgentMediaApproval</c>。</summary>
    private async Task UpdateMediaApprovalAsync(
        CloudAgentExecution run, CloudAgentRuntimeDto state, CloudAgentMediaSettings settings,
        CancellationToken cancellationToken)
    {
        CloudAgentCallDto call = state.Approval!.Call;
        if (call.Function.Name != "generate_media")
        {
            throw AppError.BadAuthRequest("当前审批不是图片生成，不能修改生成参数");
        }
        CloudAgentMediaArgs args;
        try
        {
            args = CloudAgentContracts.DecodeObject<CloudAgentMediaArgs>(call.Function.Arguments);
        }
        catch (Exception cause) when (cause is CloudAgentArgumentException or AppError)
        {
            throw AppError.Wrap(400, cause.Message, cause);
        }
        if (args.Mode != "image")
        {
            throw AppError.BadAuthRequest("仅图片生成审批支持修改模型、画幅和质量");
        }
        args.LogicalModelID = settings.LogicalModelID;
        args.ChannelID = settings.ChannelID;
        args.ChannelModelKey = settings.ChannelModelKey;
        args.Size = settings.Size;
        args.Quality = settings.Quality;
        call.Function.Arguments = JsonSerializer.Serialize(args, GoJson.WriteOptions);
        CloudAgentMediaPlan plan = new() { Args = args, CallID = call.ID };
        await using CloudAgentMutationContext planningContext =
            await _repository.OpenCloudAgentContextAsync(cancellationToken).ConfigureAwait(false);
        (CreateTaskRequestDto request, CloudAgentMediaPlan _) = await _media.PrepareAsync(
            planningContext, run, state, call, cancellationToken).ConfigureAwait(false);
        // 干跑准入使用与提交相同的模型、归属与规格校验，不预留积分、不建任务。
        (TaskEntity admitted, BillingOrder? _) = await _taskCreation.AdmitQueuedAsync(
            run.UserID, request, request.Input, request.Type!, request.Prompt, "", "",
            cancellationToken).ConfigureAwait(false);
        Dictionary<string, JsonElement>? resolvedConfig = InputConfigInputJson(admitted.InputJSON);
        CloudAgentMediaService.ValidateResolvedOptions(InputConfig(request.Input), resolvedConfig!);
        string modelName = await _media.ModelNameAsync(args, cancellationToken).ConfigureAwait(false);
        state.Calls[state.CallIndex] = call;
        state.Approval.Call = call;
        state.Approval.CallHash = CloudAgentMutations.ApprovalCallHash(call);
        state.Approval.ModelName = modelName;
        state.Approval.Preview = CloudAgentMediaService.ApprovalPreview(plan, modelName);
    }

    // ------------------------------------------------------------ 取消 / 撤销 / 清理

    /// <summary>取消运行。对应 Go: <c>CancelCloudAgent</c>。</summary>
    public async Task CancelAsync(string userID, string id, CancellationToken cancellationToken)
    {
        // 取消是控制面操作：即使运行时 blob 损坏也必须可用，先从任务行做归属校验。
        TaskEntity? task = await _repository.TaskForUserAsync(userID, id, cancellationToken).ConfigureAwait(false);
        if (task is null || task.Operation != CloudAgentContracts.CloudAgentOperation)
        {
            throw AppError.NotFound("Agent 运行不存在");
        }
        CloudAgentExecution? run = await _repository.CloudAgentAsync(userID, id, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            await _sessions.GetAsync(userID, id, cancellationToken).ConfigureAwait(false);
            run = await _repository.CloudAgentAsync(userID, id, cancellationToken).ConfigureAwait(false)
                ?? throw AppError.NotFound("Agent 运行不存在");
        }
        if (run.Status == "completed" || (run.Status == "failed" && !run.CleanupPending))
        {
            return;
        }
        if (run.Status != "failed")
        {
            CloudAgentRuntimeDto? state = null;
            try
            {
                state = CloudAgentContracts.Decode(run);
            }
            catch (InvalidOperationException)
            {
                // 损坏状态也能取消：不触碰 StateJSON。
            }
            bool claimed = await _repository.MutateCloudAgentAsync(userID, id, run.Revision, (current, context) =>
            {
                current.Status = "cancelled";
                current.CleanupPending = true;
                if (state is not null)
                {
                    current.CanvasID = state.Request.CanvasID;
                    current.ActiveTaskID = state.ActiveTaskID;
                    current.MediaTaskID = state.MediaTaskID;
                }
                return context.SaveRunAsync(current);
            }).ConfigureAwait(false);
            if (!claimed)
            {
                throw CloudAgentSessionService.CreationConflict();
            }
        }
        CloudAgentExecution latest = await _repository.CloudAgentAsync(userID, id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.NotFound("Agent 运行不存在");
        await FinishCleanupAsync(latest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>清理交接。对应 Go: <c>finishCloudAgentCleanup</c>。</summary>
    public async Task FinishCleanupAsync(CloudAgentExecution run, CancellationToken cancellationToken)
    {
        if (!run.CleanupPending || !CloudAgentContracts.IsRunTerminal(run.Status))
        {
            return;
        }
        CloudAgentRuntimeDto? state = null;
        try
        {
            state = CloudAgentContracts.Decode(run);
        }
        catch (Exception cause) when (cause is InvalidOperationException or AppError)
        {
            state = null;
        }
        string activeID = state?.ActiveTaskID ?? run.ActiveTaskID;
        string mediaID = state?.MediaTaskID ?? run.MediaTaskID;
        string canvasID = state?.Request.CanvasID ?? run.CanvasID;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string id in new[] { run.ID, activeID, mediaID })
        {
            if (id.Length == 0 || !seen.Add(id))
            {
                continue;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            TaskEntity? task = await _repository.TaskForUserAsync(run.UserID, id, cancellationToken)
                .ConfigureAwait(false);
            if (task is null)
            {
                continue;
            }
            if (task.Status is TaskStatus.TaskStatusQueued or TaskStatus.TaskStatusRunning)
            {
                try
                {
                    await _taskLifecycle.CancelAsync(run.UserID, id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 完成可能赢得取消竞争：重读而不是把真实终态当永久清理错误。
                    TaskEntity? latest = await _repository.TaskForUserAsync(run.UserID, id, cancellationToken)
                        .ConfigureAwait(false);
                    if (latest is null || !CloudAgentContracts.IsTaskTerminal(latest.Status))
                    {
                        throw;
                    }
                }
            }
        }
        if (mediaID.Length > 0 && state is not null && state.CallIndex < state.Calls.Count)
        {
            try
            {
                await AdvanceMediaAsync(run, state, state.Calls[state.CallIndex], cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CloudAgentCheckpointException)
            {
                throw;
            }
            CloudAgentExecution? reloaded = await _repository.CloudAgentAsync(run.UserID, run.ID, cancellationToken)
                .ConfigureAwait(false)
                ?? throw AppError.NotFound("Agent 运行不存在");
            run = reloaded;
            mediaID = "";
        }
        TaskEntity? mediaTask = null;
        RuntimePolicySetting policy = _runtimePolicy.Current();
        if (mediaID.Length > 0 && canvasID.Length > 0)
        {
            mediaTask = await _repository.TaskForUserAsync(run.UserID, mediaID, cancellationToken)
                .ConfigureAwait(false);
        }
        bool claimed = await _repository.MutateCloudAgentAsync(run.UserID, run.ID, run.Revision,
            async (current, context) =>
            {
                if (mediaTask is not null)
                {
                    try
                    {
                        await _media.CompleteMediaNodeAsync(
                            context, run.UserID, canvasID, mediaTask, policy).ConfigureAwait(false);
                    }
                    catch (Exception cause) when (cause is AppError error && error.Status is 400 or 409
                        || cause is InvalidOperationException || cause.Message == "record not found")
                    {
                        current.FailureMessage = "生成节点已删除、被修改或结果不可用；任务结果保留在任务中心";
                    }
                }
                current.CleanupPending = false;
                current.ActiveTaskID = "";
                current.MediaTaskID = "";
                await context.SaveRunAsync(current).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            throw CloudAgentSessionService.CreationConflict();
        }
    }

    /// <summary>撤销最近一次画布变更。对应 Go: <c>UndoCloudAgentCanvas</c>。</summary>
    public async Task<Dictionary<string, JsonElement>> UndoAsync(
        string userID, string runID, string stepID, string expectedSnapshotHash, string reason,
        CancellationToken cancellationToken)
    {
        if (userID.Trim().Length == 0 || runID.Trim().Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        expectedSnapshotHash = expectedSnapshotHash.Trim();
        if (expectedSnapshotHash.Length != 64 || !expectedSnapshotHash.All(Uri.IsHexDigit))
        {
            throw AppError.BadAuthRequest("需要有效的画布快照哈希");
        }
        stepID = stepID.Trim();
        if (stepID.Length > 160)
        {
            throw AppError.BadAuthRequest("Agent 操作 ID 过长");
        }
        if (reason.EnumerateRunes().Count() > 2000)
        {
            throw AppError.BadAuthRequest("撤销理由过长");
        }
        CloudAgentExecution? run = await _repository.CloudAgentAsync(userID, runID, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.NotFound("Agent 运行不存在");
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal)
        {
            ["accepted"] = JsonSerializer.SerializeToElement(false),
        };
        bool claimed = await _repository.MutateCloudAgentAsync(userID, runID, run.Revision,
            async (current, context) =>
            {
                CloudAgentCanvasMutation? mutation = await context.LatestCanvasMutationAsync(
                    userID, runID).ConfigureAwait(false);
                if (mutation is null)
                {
                    throw AppError.NotFound("当前 Agent 运行没有可撤销的画布变更");
                }
                if (stepID.Length > 0 && mutation.StepID != stepID)
                {
                    throw CloudAgentSessionService.CreationConflict("待撤销的 Agent 操作已不是当前运行的最新变更");
                }
                CanvasProject? canvas = await context.CanvasProjectForUserAsync(
                    userID, mutation.CanvasID).ConfigureAwait(false)
                    ?? throw AppError.NotFound("画布不存在或无权访问");
                JsonObject currentDoc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
                string currentHash = CloudAgentContracts.CanvasHash(currentDoc);
                if (mutation.Status == "undone")
                {
                    if (currentHash == mutation.BeforeSnapshotHash && expectedSnapshotHash == currentHash)
                    {
                        result = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["accepted"] = JsonSerializer.SerializeToElement(true),
                            ["snapshotHash"] = JsonSerializer.SerializeToElement(currentHash),
                        };
                        return;
                    }
                    throw CloudAgentSessionService.CreationConflict("该 Agent 变更已经撤销，但画布之后又发生了变化");
                }
                if (mutation.Status == "not_undoable")
                {
                    throw AppError.BadAuthRequest("该 Agent 画布变更未保留完整快照，无法撤销");
                }
                if (mutation.Status != "applied")
                {
                    throw CloudAgentSessionService.CreationConflict("该 Agent 变更当前不可撤销");
                }
                if (mutation.HasSubmittedTask)
                {
                    throw AppError.BadAuthRequest("已提交的生成任务不能撤销；任务不会取消或退款");
                }
                if (expectedSnapshotHash != currentHash || mutation.AfterSnapshotHash != currentHash)
                {
                    throw CloudAgentSessionService.CreationConflict("画布已发生后续变化，未执行撤销");
                }
                if (mutation.BeforeJSON.Length == 0)
                {
                    throw AppError.BadAuthRequest("该 Agent 画布变更未保留可撤销快照");
                }
                JsonObject beforeDoc = CloudAgentJsonHelpers.Document(mutation.BeforeJSON);
                if (CloudAgentContracts.CanvasHash(beforeDoc) != mutation.BeforeSnapshotHash)
                {
                    throw AppError.BadAuthRequest("该 Agent 画布变更的撤销快照校验失败");
                }
                string previousPayload = canvas.PayloadJSON;
                canvas.PayloadJSON = mutation.BeforeJSON;
                try
                {
                    await context.CompareSaveCreationCanvasAsync(canvas, previousPayload).ConfigureAwait(false);
                }
                catch (AppError error) when (error.Status == 409)
                {
                    throw CloudAgentSessionService.CreationConflict("画布已发生后续变化，未执行撤销");
                }
                bool undone = await context.MarkCanvasMutationUndoneAsync(
                    userID, runID, mutation.ID, DateTime.UtcNow).ConfigureAwait(false);
                if (!undone)
                {
                    throw CloudAgentSessionService.CreationConflict("该 Agent 变更已经被处理，请重新读取画布");
                }
                CloudAgentRuntimeDto state = CloudAgentContracts.Decode(current);
                CloudAgentContracts.AddEvent(state, runID, "canvas_undone", CloudAgentContracts.Payload(
                    ("canvasId", mutation.CanvasID),
                    ("stepId", mutation.StepID),
                    ("snapshotHash", mutation.BeforeSnapshotHash),
                    ("reason", reason.Trim())));
                CloudAgentContracts.Save(current, state);
                await context.SaveRunAsync(current).ConfigureAwait(false);
                result = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["accepted"] = JsonSerializer.SerializeToElement(true),
                    ["snapshotHash"] = JsonSerializer.SerializeToElement(mutation.BeforeSnapshotHash),
                };
            }, cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            throw CloudAgentSessionService.CreationConflict();
        }
        return result;
    }
}
