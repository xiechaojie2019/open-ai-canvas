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
        // 同轮多写接续快照：把本轮后续写调用手里的 snapshotHash 接到最新画布上。
        // 必须在审批判定之前——模型读一次画布后连下多个写调用时，第一个写成功就会让后面的快照过期。
        call = await RefreshStepSnapshotHashAsync(run, state, call, cancellationToken).ConfigureAwait(false);
        state.Calls[state.CallIndex] = call;
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
        // 审批边界：request_approval 下所有写工具都要人工审批；auto 下写工具直接执行。
        // ⚠️ 与 Go 的有意差异：Go 对 generate_media 是**无条件**审批（媒体策略 v2 原文
        // 「创建媒体草稿及连线不等于收费授权；所有模式都必须等待界面独立审批」）。
        // 本 fork 按产品要求改为 auto 直接提交，费用边界完全交给本轮 Budget
        //（maxCredits / maxGenerationTasks / maxVideoSeconds）兜底。
        bool needsApproval = state.Request.PermissionMode == "request_approval";
        if (allowed && CloudAgentTools.IsWrite(name) && needsApproval && state.Approval is null)
        {
            await PlanApprovalAsync(run, state, call, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (allowed && name == "generate_media")
        {
            if (state.Approval is null && !needsApproval)
            {
                // auto：先建草稿并固化快照/依赖哈希（与审批路径同一套固化逻辑），再提交。
                if (!await PrepareAutoMediaDraftAsync(run, state, call, cancellationToken).ConfigureAwait(false))
                {
                    return; // 草稿或准入失败，已记成工具失败。
                }
            }
            if (state.Approval is null || state.Approval.Decision == "approve")
            {
                await AdvanceMediaAsync(run, state, call, cancellationToken).ConfigureAwait(false);
                return;
            }
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

        // 看图的资源与能力校验在写事务外完成；真实图片只在模型任务执行时读取。
        CloudAgentImageInspectionDto? inspection = null;
        Exception? inspectionError = null;
        if (allowed && name == "canvas_inspect_image" && state.Request.VisionEnabled)
        {
            try
            {
                inspection = await PrepareImageInspectionAsync(
                    run.UserID, state.Request.CanvasID, state, call, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudAgentImageInspectionBudgetException)
            {
                await FailAsync(run, state, CloudAgentRuntimeService.ImageInspectionBudgetMessage)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception cause) when (cause is AppError or CloudAgentReadLoopException)
            {
                inspectionError = cause;
            }
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
                else if (name == "canvas_inspect_image")
                {
                    if (inspectionError is not null)
                    {
                        toolError = inspectionError;
                    }
                    else
                    {
                        result = inspection?.Receipt;
                    }
                    if (toolError is null && inspection is not null)
                    {
                        CloudAgentRuntimeService.MarkCanvasImageInspection(
                            state,
                            CloudAgentJsonHelpers.StringValue(
                                CloudAgentJsonHelpers.Get(inspection.Receipt, "nodeId")),
                            inspection.ImageURL.Trim().Length > 0);
                        if (inspection.CacheKey.Length > 0)
                        {
                            state.ImageInspectionReads ??= new Dictionary<string, int>(StringComparer.Ordinal);
                            state.ImageInspectionReads[inspection.CacheKey] =
                                state.ImageInspectionReads.GetValueOrDefault(inspection.CacheKey) + 1;
                        }
                    }
                }
                else
                {
                    (result, toolError) = await ReadToolAsync(context, run.UserID, state, call)
                        .ConfigureAwait(false);
                }
                if (toolError is CloudAgentReadLoopException readLoop)
                {
                    // 重复读取护栏：先记回执再终局，模型能看到最后一次结果与失败原因。
                    ToolResult(run.ID, state, call, result, null, inspection);
                    CloudAgentContracts.Save(current, state);
                    current.Status = "failed";
                    current.FailureMessage = CloudAgentContracts.TruncateRunes(readLoop.Message, 1000);
                    CloudAgentContracts.AddEvent(state, run.ID, "run_failed", CloudAgentContracts.Payload(
                        ("text", current.FailureMessage),
                        ("reason", "repeated_read_guard"),
                        ("toolName", call.Function.Name),
                        ("repeatCount", readLoop.Count)));
                    CloudAgentContracts.Save(current, state);
                    await context.SaveRunAsync(current).ConfigureAwait(false);
                    return;
                }
                ToolResult(run.ID, state, call, result, toolError, inspection);
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
                (List<CreateTaskRequestDto> requests, CloudAgentMediaPlan prepared) = await _media.PrepareAsync(
                    planningContext, run, state, call, cancellationToken).ConfigureAwait(false);
                // 批量产物会同时占用多个「排队或运行」额度：不足时必须在建草稿之前就拒绝。
                if (prepared.IsBatch)
                {
                    await EnsureBatchConcurrencyAsync(
                        run.UserID, planningContext.Repository, prepared.Items.Count,
                        cancellationToken).ConfigureAwait(false);
                }
                // 干跑准入：不落库、不计费，仅校验模型与规格。批量时逐项都验一遍。
                foreach (CreateTaskRequestDto request in requests)
                {
                    await _taskCreation.AdmitQueuedAsync(
                        run.UserID, request, request.Input, request.Type!,
                        request.Prompt, "", "", cancellationToken).ConfigureAwait(false);
                }
                plan = prepared;
                modelName = await _media.ModelNameAsync(plan.Args, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception cause) when (
                cause is AppError or CloudAgentArgumentException or InvalidOperationException
                or System.Data.Common.DbException)
            {
                bool recorded = await MediaErrorAsync(
                    run, state, "admission", submitted: false, terminal: false, cause).ConfigureAwait(false);
                if (!recorded)
                {
                    throw CloudAgentSessionService.CreationConflict();
                }
                return;
            }
            bool claimed = await _repository.MutateCloudAgentAsync(run.UserID, run.ID, run.Revision,
                async (current, context) =>
                {
                    try
                    {
                        await MutatePlanApprovalAsync(
                            current, context, run, state, call, policy, plan, modelName)
                            .ConfigureAwait(false);
                    }
                    catch (Exception cause) when (
                        cause is AppError or InvalidOperationException or System.Data.Common.DbException)
                    {
                        // 与 auto 路径同理：建草稿失败绝不能穿出事务变成静默 409，
                        // 必须当场记成工具失败（含批量 count>1 的第二项快照过期）。
                        state.MediaBatch = null;
                        ToolResult(run.ID, state, call, null, cause);
                        CloudAgentContracts.Save(current, state);
                        await context.SaveRunAsync(current).ConfigureAwait(false);
                    }
                },
                cancellationToken).ConfigureAwait(false);
            if (!claimed)
            {
                // ⚠️ 踩过的坑：这里原本直接抛 409。而 409 会被调度器当成「并发推进，跳过等下一轮」
                // 静默吞掉（CloudAgentSchedulerWorker 只累计 _conflictStreak）。一旦某个运行稳定落进
                // 这个分支（线上 run agf09c94b248cbdabb578e15c2ecc16ad9，2026-10-13），
                // 它就每 2 秒原地重跑同一步，直到 5 分钟看门狗判 failed —— 用户看到的就是「生节点卡死」。
                // 必须先把失败记进运行（推进 callIndex、留 tool_failed 事件），让模型能换招；
                // 连失败都写不进去（revision 被持续抢占）才允许推向终态。
                bool recorded = await MediaErrorAsync(
                    run, state, "admission", submitted: false, terminal: false,
                    CloudAgentSessionService.CreationConflict()).ConfigureAwait(false);
                if (!recorded)
                {
                    throw CloudAgentSessionService.CreationConflict();
                }
                return;
            }
            run.Revision++;
            return;
        }
        bool claimedPlan = await _repository.MutateCloudAgentAsync(run.UserID, run.ID, run.Revision,
            (current, context) => MutatePlanApprovalAsync(current, context, run, state, call, policy, plan: null, modelName: ""),
            cancellationToken).ConfigureAwait(false);
        if (!claimedPlan)
        {
            bool recorded = await MediaErrorAsync(
                run, state, "admission", submitted: false, terminal: false,
                CloudAgentSessionService.CreationConflict()).ConfigureAwait(false);
            if (!recorded)
            {
                throw CloudAgentSessionService.CreationConflict();
            }
            return;
        }
        run.Revision++;
    }

    /// <summary>
    /// auto 模式下 generate_media 的前置：建草稿节点，并把快照/依赖哈希固化进当前调用参数。
    /// 与审批路径共用同一套固化逻辑（<see cref="BakeMediaDraftAsync"/>），但不产生审批记录、
    /// 不进入 waiting_approval、不钉资源租约（草稿与提交在同一拍完成，窗口极短）。
    /// ⚠️ 与 Go 的有意差异：Go 在 auto 下也会 suspend 到人工审批。
    /// 返回 false 表示已记成工具失败，调用方必须直接返回。
    /// </summary>
    private async Task<bool> PrepareAutoMediaDraftAsync(
        CloudAgentExecution run, CloudAgentRuntimeDto state, CloudAgentCallDto call,
        CancellationToken cancellationToken)
    {
        CloudAgentMediaPlan plan;
        try
        {
            await using CloudAgentMutationContext planningContext =
                await _repository.OpenCloudAgentContextAsync(cancellationToken).ConfigureAwait(false);
            (List<CreateTaskRequestDto> requests, CloudAgentMediaPlan prepared) = await _media.PrepareAsync(
                planningContext, run, state, call, cancellationToken).ConfigureAwait(false);
            // 批量产物会同时占用多个「排队或运行」额度：不足时必须在建草稿之前就拒绝。
            if (prepared.IsBatch)
            {
                await EnsureBatchConcurrencyAsync(
                    run.UserID, planningContext.Repository, prepared.Items.Count,
                    cancellationToken).ConfigureAwait(false);
            }
            // 干跑准入：与审批路径一致，不落库、不计费，仅校验模型与规格。批量时逐项都验一遍。
            foreach (CreateTaskRequestDto request in requests)
            {
                await _taskCreation.AdmitQueuedAsync(
                    run.UserID, request, request.Input, request.Type!,
                    request.Prompt, "", "", cancellationToken).ConfigureAwait(false);
            }
            plan = prepared;
        }
        catch (Exception cause) when (
            cause is AppError or CloudAgentArgumentException or InvalidOperationException
            or System.Data.Common.DbException)
        {
            bool recorded = await MediaErrorAsync(
                run, state, "admission", submitted: false, terminal: false, cause).ConfigureAwait(false);
            if (!recorded)
            {
                // 连失败检查点都写不进去（revision 被别处持续抢占）。此时返回 false 只会让调度器
                // 2 秒后原地重跑同一步、再撞同一个错误 —— 正是「生节点卡死」的成因。
                // 宁可如实终态化，也不要静默空转到看门狗超时。
                throw CloudAgentSessionService.CreationConflict();
            }
            return false;
        }
        bool claimed = await _repository.MutateCloudAgentAsync(run.UserID, run.ID, run.Revision,
            async (current, context) =>
            {
                try
                {
                    await BakeMediaDraftAsync(context, run, state, _runtimePolicy.Current(), plan)
                        .ConfigureAwait(false);
                }
                catch (Exception cause) when (
                    cause is AppError or InvalidOperationException or System.Data.Common.DbException)
                {
                    // ⚠️ 踩过的坑：建草稿失败原本直接穿出事务 → 抛到 AdvanceAsync 的 409 分支 →
                    // 被调度器当「并发冲突」静默跳过，每 2 秒原地重跑直到看门狗判 failed。
                    // 批量 count>1 时第二项快照过期就会稳定触发这条（已在本轮修掉快照接续），
                    // 但任何残留的建草稿失败都必须**当场记进运行**、让模型能换招，绝不静默空转。
                    state.MediaBatch = null;
                    ToolResult(run.ID, state, call, null, cause);
                    CloudAgentContracts.Save(current, state);
                    await context.SaveRunAsync(current).ConfigureAwait(false);
                }
            }, cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            throw CloudAgentSessionService.CreationConflict();
        }
        run.Revision++;
        return true;
    }

    /// <summary>
    /// 批量产物（count&gt;1）与平台并发额度的前置校验。
    /// </summary>
    /// <remarks>
    /// 批量提交是一条条落库的，而平台对「排队或运行」的任务数有硬上限
    /// （<see cref="RuntimePolicySetting.Task"/>.<c>ActiveTaskLimit</c>，默认 5）。
    /// 干跑准入查的是<b>当前库里</b>的活动任务数，看不到本批还没落库的那些，
    /// 于是 N 张图会在提交到第 limit+1 张时才被配额拒 —— 前面几张已经在跑了，
    /// 收不回来，整轮只能判失败。所以必须在建草稿之前就用「当前活动数 + 本批张数」预判。
    /// 拒绝文案要给出剩余额度，模型才知道该减到几张。
    /// </remarks>
    private async Task EnsureBatchConcurrencyAsync(
        string userID, Repository repository, int count, CancellationToken cancellationToken)
    {
        int limit = _runtimePolicy.Current().Task.ActiveTaskLimit;
        long active = await repository.ActiveTaskCountForUserAsync(userID, cancellationToken).ConfigureAwait(false);
        if (active + count > limit)
        {
            throw AppError.BadAuthRequest(
                $"平台最多同时排队或运行 {limit} 个任务，当前已有 {active} 个，一次生成 {count} 个会超出；"
                + "请减少张数或等已有任务完成");
        }
    }

    /// <summary>
    /// 建媒体草稿节点，并把每项的快照哈希与依赖哈希固化进参数。
    /// <b>必须此刻计算</b>：草稿节点与引用连线已经落画布，依赖哈希才反映真实投影；
    /// 执行准入时依赖匹配可替代全画布快照，使同画布其他生成的回写不致连坐判「画布已变化」。
    /// count&gt;1 时逐项建节点，并把整批参数写进 <see cref="CloudAgentRuntimeDto.MediaBatch"/>。
    /// 对应 Go: <c>cloudAgentPreparedMedia</c> 的 SnapshotHash / DependencyHash 赋值。
    /// </summary>
    /// <remarks>
    /// ⚠️ 批量（count&gt;1，本 fork 扩展）的**接续快照**是这段的核心：
    /// <see cref="CloudAgentMediaService.CreateMediaNodeAsync"/> 内部会再走一次
    /// <c>MediaDocumentAsync</c> 校验 <c>args.SnapshotHash</c>。而每建成功一项草稿，
    /// 画布就被这一项改过（多了一个节点 + 一条引用连线），上一项手里的快照立刻过期。
    /// 若第二项仍拿模型原始快照去校验，必然抛 409「画布已变化」——
    /// 且因为它抛在 CAS 回调内部（调用点在 <c>PrepareAutoMediaDraftAsync</c> 的 try 之外），
    /// 会绕过工具失败落库、被 <c>AdvanceAsync</c> 当成并发冲突静默跳过，
    /// 每 2 秒原地重跑直到 5 分钟看门狗判 failed —— 线上表现为「生节点卡死」。
    /// 因此每一项都必须先用**当前**画布的 <c>MediaContentHash</c> 刷新自己的快照再建节点。
    /// （<c>MediaContentHash</c> 不看节点 position，也不看 viewport/updatedAt，
    /// 所以前端高频自动保存不会污染这里；只有本批自己加节点才会改它，属于预期接续。）
    /// </remarks>
    private async Task BakeMediaDraftAsync(
        CloudAgentMutationContext context, CloudAgentExecution run, CloudAgentRuntimeDto state,
        RuntimePolicySetting policy, CloudAgentMediaPlan plan)
    {
        CloudAgentCallDto call = state.Calls[state.CallIndex];
        List<CloudAgentMediaBatchItemDto>? batch =
            plan.IsBatch ? new List<CloudAgentMediaBatchItemDto>() : null;
        bool first = true;
        foreach (CloudAgentMediaArgs item in plan.Items)
        {
            if (!first)
            {
                // 上一项已经把草稿节点写进画布：本项手里的快照（模型原始值，或上一轮刷新值）
                // 对当前画布必然过期。先用当前画布刷新，再让 CreateMediaNodeAsync 校验。
                CanvasProject? current = await context.CanvasProjectForUserAsync(
                    run.UserID, state.Request.CanvasID).ConfigureAwait(false)
                    ?? throw AppError.NotFound("画布不存在");
                JsonObject currentDoc = CloudAgentJsonHelpers.Document(current.PayloadJSON);
                item.SnapshotHash = CloudAgentContracts.MediaContentHash(currentDoc);
            }
            first = false;
            await _media.CreateMediaNodeAsync(
                context, run.UserID, state.Request.CanvasID,
                new CloudAgentMediaPlan { Items = [item], CallID = plan.CallID },
                task: null, policy, CloudAgentMutations.RecorderForRun(run.ID, state), state, run.ID)
                .ConfigureAwait(false);
            CanvasProject? canvas = await context.CanvasProjectForUserAsync(
                run.UserID, state.Request.CanvasID).ConfigureAwait(false)
                ?? throw AppError.NotFound("画布不存在");
            JsonObject doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
            item.SnapshotHash = CloudAgentContracts.MediaContentHash(doc);
            item.DependencyHash = CloudAgentMediaService.MediaDependencyHash(doc, item);
            batch?.Add(new CloudAgentMediaBatchItemDto { Args = item });
        }
        if (batch is not null)
        {
            state.MediaBatch = batch;
        }
        // 首项始终写回调用参数：审批哈希、撤销与单产物路径都按 call.Function.Arguments 走。
        call.Function.Arguments = JsonSerializer.Serialize(plan.Args, GoJson.WriteOptions);
        state.Calls[state.CallIndex] = call;
    }

    private async Task MutatePlanApprovalAsync(
        CloudAgentExecution current, CloudAgentMutationContext context, CloudAgentExecution run,
        CloudAgentRuntimeDto state, CloudAgentCallDto call, RuntimePolicySetting policy,
        CloudAgentMediaPlan? plan, string modelName)
    {
        string name = call.Function.Name;
        string approvalID = $"{run.ID}-{state.Step}-{state.CallIndex}";
        CloudAgentApprovalPreviewDto preview;
        if (plan is not null)
        {
            // 草稿节点 + 快照/依赖哈希固化，与 auto 路径共用同一段逻辑。
            await BakeMediaDraftAsync(context, run, state, policy, plan).ConfigureAwait(false);
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
            ID = approvalID,
            Call = call,
            CallHash = CloudAgentMutations.ApprovalCallHash(call),
            Preview = preview,
            ModelName = modelName,
        };
        if (plan is not null)
        {
            // 钉住参考资源到审批所有者名下（对应 Go: pinCloudAgentPreparedMedia），
            // 报价有效期 30 分钟（对应 Go: cloudAgentMediaQuoteLifetime）。
            DateTime leaseExpiresAt = DateTime.UtcNow.AddMinutes(30);
            List<string> leaseResources = await LeaseResourceIDsAsync(
                context, run.UserID, state.Request.CanvasID, plan.Args.ReferenceNodeIDs).ConfigureAwait(false);
            if (leaseResources.Count > 0)
            {
                await context.UpsertCloudAgentResourceLeasesAsync(
                    run.UserID, run.ID, approvalID, leaseResources, leaseExpiresAt).ConfigureAwait(false);
            }
            state.Approval.LeaseExpiresAt = leaseExpiresAt;
        }
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
        CloudAgentMediaPlan? media, string mediaOperation = "", CancellationToken cancellationToken = default,
        CloudAgentMediaBatchItemDto? batchItem = null, bool batchPartiallySubmitted = false)
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
            // 媒体任务的 operation 必须用 PrepareAsync 按模式+参考素材推导的值
            // （Go: cloudAgentMediaOperation）——写死 cloud_agent_media 会被任何
            // 声明了 operations 白名单的模型能力合同拒绝（如火山 text_to_video/image_to_video）。
            Operation = media is null ? "cloud_agent_step" : mediaOperation,
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
                // 批量途中被拒且前面已有项提交出去时，不能只记一次工具失败就走人：
                // 那些已提交的收费任务收不回来，本轮必须如实终态化（与"部分成功"同一套口径）。
                await MediaErrorAsync(run, state, "admission", submitted: false,
                    terminal: batchPartiallySubmitted, cause).ConfigureAwait(false);
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
                    // ⚠️ 提交前必须用**当前**画布刷新快照：批量（count>1）里前一项的任务一旦提交，
                    // CreateMediaNodeAsync 就把它自己的草稿节点写进画布（status 由 idle 变 loading），
                    // 后一项手里的快照立刻过期 → 409「画布已变化」，整批提交卡在中途。
                    // 单产物路径（batchItem 为 null）刷新也无害：值不变，等价于原样。
                    CanvasProject? current0 = await context.CanvasProjectForUserAsync(
                        run.UserID, state.Request.CanvasID).ConfigureAwait(false);
                    if (current0 is not null)
                    {
                        JsonObject currentDoc = CloudAgentJsonHelpers.Document(current0.PayloadJSON);
                        media.Args.SnapshotHash = CloudAgentContracts.MediaContentHash(currentDoc);
                        // 依赖哈希同样按当前投影重算：它才是执行准入真正要比对的基线。
                        media.Args.DependencyHash =
                            CloudAgentMediaService.MediaDependencyHash(currentDoc, media.Args);
                    }
                    await _media.CreateMediaNodeAsync(
                        context, run.UserID, state.Request.CanvasID, media, task, policy,
                        CloudAgentMutations.RecorderForRun(run.ID, state), state, run.ID).ConfigureAwait(false);
                }
                await context.CreateTaskWithQuotaAsync(task, order, policy.Task.ActiveTaskLimit)
                    .ConfigureAwait(false);
                state.TaskIDs.Add(task.ID);
                if (media is not null)
                {
                    if (batchItem is not null)
                    {
                        // 批量产物：任务 ID 挂在批次项上，等待/回写按整批处理。
                        batchItem.TaskID = task.ID;
                    }
                    else
                    {
                        state.MediaTaskID = task.ID;
                    }
                    state.Generations++;
                    state.VideoSeconds += media.Args.Duration;
                    // 审批通过即提交：租约从审批所有者转移到任务所有者
                    // （对应 Go: TransferCloudAgentResourceLeases(userID, approvalID, "task:"+taskID, ...)）。
                    if (state.Approval is not null)
                    {
                        await context.TransferCloudAgentResourceLeasesAsync(
                            run.UserID, state.Approval.ID, "task:" + task.ID, task.ID,
                            state.Approval.LeaseExpiresAt ?? DateTime.UtcNow.AddMinutes(30)).ConfigureAwait(false);
                    }
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
    /// <remarks>
    /// ⚠️ 与 Go 的**有意差异（缺陷修复）**：Go 版（<c>cloud_agent_runtime.go:1609</c>）直接把
    /// <c>MutateCloudAgent</c> 的返回值（CAS 是否成功）丢掉。<c>callIndex</c> 不推进、<c>tool_failed</c>
    /// 不落库、<c>revision</c> 原地不动 —— 调度器 2 秒后重跑同一步，撞同一个错误，无限循环，
    /// 直到 5 分钟看门狗判 failed（日志只留一串「连续 15 次 CAS 冲突」）。
    /// 本 fork 的 count 批量与 auto 免审批让这条路径从「偶发」变成「必现」。
    /// 因此这里必须：① 用最新 revision 重试 CAS；② 把「是否真的落地」回传给调用方，
    /// 由调用方决定是继续等下一拍，还是把运行推向终态，绝不允许静默空转。
    /// </remarks>
    /// <returns>true 表示错误检查点已成功落库；false 表示连失败都写不进去（调用方必须终态化运行）。</returns>
    private async Task<bool> MediaErrorAsync(
        CloudAgentExecution run, CloudAgentRuntimeDto state, string phase,
        bool submitted, bool terminal, Exception cause)
    {
        // 先按调用方手里的 revision 试；冲突（同一拍里 revision 已被别的写入推进）就重取一次再试。
        long revision = run.Revision;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool claimed = await _repository.MutateCloudAgentAsync(run.UserID, run.ID, revision,
                (current, context) =>
                {
                    if (state.CallIndex < 0 || state.CallIndex >= state.Calls.Count)
                    {
                        current.Status = "failed";
                        CloudAgentContracts.AddEvent(state, run.ID, "run_failed",
                            CloudAgentContracts.Payload(("text", "Agent 媒体调用状态无效，本轮已停止")));
                        CloudAgentContracts.Save(current, state);
                        return context.SaveRunAsync(current);
                    }
                    // ⚠️ 批内已有任务成功提交时，这个错误**不属于这一批**：它是模型在提交成功后
                    // 又多发的一次（多半是重复/续用草稿的）generate_media 重试，而那次调用失败了。
                    // 若此时把 MediaBatch 清掉，等于把"已经交出去、正在等结果"的一批任务的回写通道
                    // 一并掐断 —— 任务会成功、成品会落存储，但画布节点永远停在 loading。
                    //
                    // ⚠️⚠️ 这时**绝不能走 ToolResult**：ToolResult 会 state.CallIndex++，
                    // 而本批仍挂在当前那条 generate_media 调用上（这是 ValidateRuntime 的硬要求：
                    // CallIndex < Calls.Count 且 Calls[CallIndex].Function.Name == "generate_media"）。
                    // 索引一旦推过表尾，下一拍 Decode 就抛「media batch is not attached to current call」
                    // ⇒ 409 被调度器静默吞掉 ⇒ 运行永久卡 running（比原缺陷更糟）。
                    // 所以这里只追加一条 tool_failed 事件留痕，**不动 CallIndex、不动 canonical 消息**。
                    bool batchAlreadySubmitted = state.MediaBatch is { Count: > 0 } submittedBatch
                        && submittedBatch.Any(entry => entry.TaskID.Length > 0);
                    JsonObject detail = new()
                    {
                        ["phase"] = phase,
                        ["taskSubmitted"] = submitted || batchAlreadySubmitted,
                    };
                    if (batchAlreadySubmitted)
                    {
                        // 与 ToolResult 同构的事件载荷（但**不**推进 CallIndex、**不**追加 canonical 消息）。
                        CloudAgentCallDto pending = state.Calls[state.CallIndex];
                        string message = SafeToolError(cause);
                        Console.Error.WriteLine(
                            $"cloud agent tool error: run={run.ID} tool={pending.Function.Name} "
                            + $"cause={cause.GetType().FullName}: {cause.Message}\n{cause.StackTrace}");
                        detail["error"] = message;
                        CloudAgentContracts.AddEvent(state, run.ID, "tool_failed",
                            CloudAgentContracts.Payload(
                                ("toolName", pending.Function.Name),
                                ("callId", pending.ID),
                                ("arguments", JsonSerializer.SerializeToElement(pending.Function.Arguments)),
                                ("text", message),
                                ("result", JsonSerializer.SerializeToElement(
                                    (JsonNode?)detail ?? new JsonObject(), GoJson.WriteOptions))));
                    }
                    else
                    {
                        ToolResult(run.ID, state, state.Calls[state.CallIndex], detail, cause);
                    }
                    if (submitted)
                    {
                        state.MediaTaskID = "";
                    }
                    if (!batchAlreadySubmitted)
                    {
                        // 批次必须一并清空：ToolResult 已经把 CallIndex 推过这次 generate_media 调用，
                        // 留下 MediaBatch 会让 ValidateRuntime 的「批次必须在 generate_media 调用上」判成状态损坏。
                        // 已提交项的结果仍留在任务中心（不自动重试收费生成），未提交项的草稿节点留在画布上可续用。
                        state.MediaBatch = null;
                        if (terminal)
                        {
                            current.Status = "failed";
                            CloudAgentContracts.AddEvent(state, run.ID, "run_failed",
                                CloudAgentContracts.Payload(("text", "媒体任务已提交，但结果处理失败；任务不会自动重试")));
                        }
                    }
                    CloudAgentContracts.Save(current, state);
                    return context.SaveRunAsync(current);
                }).ConfigureAwait(false);
            if (claimed)
            {
                run.Revision++;
                return true;
            }
            // CAS 冲突：库里 revision 比手里新。重取一次最新值再试，最多 3 轮。
            CloudAgentExecution? latest = await _repository
                .CloudAgentAsync(run.UserID, run.ID, CancellationToken.None).ConfigureAwait(false);
            if (latest is null)
            {
                return false;
            }
            revision = latest.Revision;
        }
        Console.Error.WriteLine(
            $"agent media error checkpoint failed: run={run.ID} phase={phase} revision={revision} cause={cause.Message}");
        return false;
    }

    /// <summary>已批准媒体调用：等待完成或提交任务。对应 Go: <c>advanceCloudAgentMedia</c>。</summary>
    private async Task AdvanceMediaAsync(
        CloudAgentExecution run, CloudAgentRuntimeDto state, CloudAgentCallDto call,
        CancellationToken cancellationToken)
    {
        if (state.MediaBatch is { Count: > 0 } batch)
        {
            // count>1（本 fork 扩展）：草稿已在建批时落好，这里负责补齐提交并等整批到终态。
            await AdvanceMediaBatchAsync(run, state, call, batch, cancellationToken).ConfigureAwait(false);
            return;
        }
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
        (List<CreateTaskRequestDto> Requests, CloudAgentMediaPlan Plan)? preparedPair =
            await _repository.MutateCloudAgentAsync(
                run.UserID, run.ID, run.Revision,
                async (current, context) =>
                {
                    (List<CreateTaskRequestDto> Requests, CloudAgentMediaPlan Plan) prepared =
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
        (List<CreateTaskRequestDto> requests, CloudAgentMediaPlan plan) = preparedPair.Value;
        run.Revision++;
        CreateTaskRequestDto request = requests[0];
        await EnqueueTaskAsync(run, state, request.Type!, request.Prompt,
            request.Model, request.LogicalModelID, request.Input, plan, request.Operation,
            cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 批量产物（count&gt;1，本 fork 扩展）的提交与等待。
    /// 与单产物路径只差两点：任务 ID 记在批次项上（<see cref="CloudAgentMediaBatchItemDto.TaskID"/>），
    /// 且必须整批都到终态才收尾——这样 N 个任务才能真正并行跑。
    /// </summary>
    private async Task AdvanceMediaBatchAsync(
        CloudAgentExecution run, CloudAgentRuntimeDto state, CloudAgentCallDto call,
        List<CloudAgentMediaBatchItemDto> batch, CancellationToken cancellationToken)
    {
        // 先把还没提交的项补齐。逐项提交而不是一次性事务：每项各自计算预算与配额，
        // 任何一项被拒都会立刻终止本批，不会留下"节点建了但没提交"的静默状态。
        foreach (CloudAgentMediaBatchItemDto item in batch)
        {
            if (item.TaskID.Length > 0)
            {
                continue;
            }
            CreateTaskRequestDto request;
            CloudAgentMediaPlan itemPlan;
            try
            {
                await using CloudAgentMutationContext planningContext =
                    await _repository.OpenCloudAgentContextAsync(cancellationToken).ConfigureAwait(false);
                // ⚠️ 先把本项快照刷到当前画布：前一项提交时把自己的草稿节点写成 loading，
                // 画布已经变了；沿用固化时的旧快照，PrepareItemAsync 会直接 409
                //（「画布已变化」），整批卡在中途 —— 这正是 count>1 卡死的直接来源。
                CanvasProject? currentItem = await planningContext.CanvasProjectForUserAsync(
                    run.UserID, state.Request.CanvasID).ConfigureAwait(false);
                if (currentItem is not null)
                {
                    JsonObject currentItemDoc = CloudAgentJsonHelpers.Document(currentItem.PayloadJSON);
                    item.Args.SnapshotHash = CloudAgentContracts.MediaContentHash(currentItemDoc);
                    item.Args.DependencyHash =
                        CloudAgentMediaService.MediaDependencyHash(currentItemDoc, item.Args);
                }
                (List<CreateTaskRequestDto> requests, CloudAgentMediaPlan prepared) =
                    await _media.PrepareItemAsync(
                        planningContext, run, state, item.Args, call.ID, cancellationToken)
                        .ConfigureAwait(false);
                request = requests[0];
                itemPlan = prepared;
            }
            catch (Exception cause) when (
                cause is AppError or CloudAgentArgumentException or InvalidOperationException
                or System.Data.Common.DbException)
            {
                bool recorded = await MediaErrorAsync(
                    run, state, "admission", submitted: false, terminal: false, cause).ConfigureAwait(false);
                if (!recorded)
                {
                    throw CloudAgentSessionService.CreationConflict();
                }
                return;
            }
            int submittedBefore = batch.Count(entry => entry.TaskID.Length > 0);
            await EnqueueTaskAsync(run, state, request.Type!, request.Prompt, request.Model,
                request.LogicalModelID, request.Input, itemPlan, request.Operation, cancellationToken,
                batchItem: item, batchPartiallySubmitted: submittedBefore > 0).ConfigureAwait(false);
            // EnqueueTaskAsync 内部吞掉准入异常并直接返回：它没能建出任务，本批就此中断。
            // 已提交项是否终态化由 batchPartiallySubmitted 决定，这里只需停手。
            if (item.TaskID.Length == 0)
            {
                return;
            }
        }
        List<TaskEntity> completed = [];
        foreach (CloudAgentMediaBatchItemDto item in batch)
        {
            TaskEntity? task = await _repository.TaskForUserAsync(
                run.UserID, item.TaskID, cancellationToken).ConfigureAwait(false);
            if (task is null)
            {
                await MediaErrorAsync(run, state, "completion", submitted: true, terminal: true,
                    AppError.BadAuthRequest("媒体任务不存在或已失去归属，结果未回写画布"))
                    .ConfigureAwait(false);
                return;
            }
            if (task.Status is TaskStatus.TaskStatusQueued or TaskStatus.TaskStatusRunning)
            {
                return; // 还有在跑的，下一拍再看。
            }
            completed.Add(task);
        }
        RuntimePolicySetting policy = _runtimePolicy.Current();
        bool claimed = await _repository.MutateCloudAgentAsync(run.UserID, run.ID, run.Revision,
            async (current, context) =>
            {
                CanvasProject? before = await context.CanvasProjectForUserAsync(
                    run.UserID, state.Request.CanvasID).ConfigureAwait(false);
                List<JsonObject> results = new(completed.Count);
                Exception? firstError = null;
                int succeeded = 0;
                foreach (TaskEntity task in completed)
                {
                    string nodeID;
                    Exception? writeError = null;
                    try
                    {
                        nodeID = await _media.CompleteMediaNodeAsync(
                            context, run.UserID, state.Request.CanvasID, task, policy).ConfigureAwait(false);
                    }
                    catch (Exception cause) when (
                        cause is AppError error && error.Status is 400 or 409
                            || cause is InvalidOperationException
                            || cause.Message == "record not found")
                    {
                        writeError = cause;
                        nodeID = await CompleteLenientAsync(
                            context, run.UserID, state.Request.CanvasID, task, policy).ConfigureAwait(false);
                    }
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
                    JsonObject item = new()
                    {
                        ["taskId"] = task.ID,
                        ["nodeId"] = nodeID,
                        ["status"] = task.Status,
                    };
                    if (task.Status == TaskStatus.TaskStatusSucceeded && writeError is null)
                    {
                        succeeded++;
                        item["summary"] = "生成结果已回写画布节点";
                    }
                    else
                    {
                        firstError ??= writeError ?? AppError.BadAuthRequest(
                            "媒体任务" + task.Status + "：" + CloudAgentMediaService.SafeMediaTaskError(task)
                            + "；请在任务中心查看任务 " + task.ID + "，不会自动重试收费生成");
                        item["summary"] = writeError is not null
                            ? "未回写画布；任务结果保留在任务中心"
                            : "媒体任务未成功；结果保留在任务中心";
                    }
                    results.Add(item);
                }
                JsonObject result = new()
                {
                    ["phase"] = "completion",
                    ["taskSubmitted"] = true,
                    ["batchSize"] = completed.Count,
                    ["succeeded"] = succeeded,
                    ["items"] = new JsonArray(results.Select(r => (JsonNode?)r).ToArray()),
                    ["summary"] = $"本批 {completed.Count} 个生成任务中 {succeeded} 个已回写画布"
                        + (firstError is null ? "" : "；其余请在任务中心查看"),
                };
                // 工具诊断保持为最后一个事件；终态事件先行，保证失败完成有 run 级信号。
                if (firstError is not null && current.Status != "cancelled")
                {
                    current.Status = "failed";
                    CloudAgentContracts.AddEvent(state, run.ID, "run_failed", CloudAgentContracts.Payload(
                        ("text", "批量媒体任务部分失败；任务不会自动重试"),
                        ("succeeded", succeeded),
                        ("total", completed.Count)));
                }
                ToolResult(run.ID, state, call, result, firstError);
                state.MediaBatch = null;
                CloudAgentContracts.Save(current, state);
                await context.SaveRunAsync(current).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            throw CloudAgentSessionService.CreationConflict();
        }
        run.Revision++;
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
        // 必须走 run 绑定的 recorder（=`RecorderForRun`），它内部注入 RunID 并投影 canvas_updated
        // 事件，与 Go 的 cloudAgentCanvasEventRecorder 一一对应。
        // 直接调 RecordAsync 会漏掉 RunID，RecordAsync 的首道校验即抛
        // 「画布变更缺少可追踪的 Agent 操作信息」。
        // 注意失败形态并不"干净"：异常在 AdvanceToolAsync 的派发 lambda 里被吞成 toolError
        // （与 Go 一致，事务照常提交），而上面的 SaveDocumentAsync 已经落库 —— 于是画布确实
        // 被改了，却没有对应的 cloudAgentCanvasMutations 行，既无法撤销也无从追溯；
        // 用户看到的是「更新画布内容失败」，画布上却已经有内容。
        // RunID 只由 recorder 注入，别再内联展开。
        await CloudAgentMutations.RecorderForRun(run.ID, state)(context, new CloudAgentMutationInput
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
        // 资源租约只是保护机制：过期清理在控制面路径上执行是安全的，
        // 不改变审批决策本身（对应 Go: DecideCloudAgentApproval 开头的过期清扫）。
        await _repository.ReleaseExpiredCloudAgentResourceLeasesAsync(DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (settings is not null)
        {
            await UpdateMediaApprovalAsync(run, state, settings, cancellationToken).ConfigureAwait(false);
        }
        bool claimed = await _repository.MutateCloudAgentAsync(userID, id, run.Revision, async (current, context) =>
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
                // 拒绝后审批所有者名下的资源租约一并释放（对应 Go: runtime 的拒绝路径）。
                await context.ReleaseCloudAgentResourceLeasesAsync(userID, approvalID).ConfigureAwait(false);
                CloudAgentContracts.AddEvent(state, id, "approval_decided", CloudAgentContracts.Payload(
                    ("approvalId", approvalID),
                    ("decision", decision),
                    ("reason", reason),
                    ("text", "已拒绝本次操作，未写入画布。你可以告诉 Agent 修改方向后重新申请。")));
                CloudAgentContracts.Save(current, state);
                await context.SaveRunAsync(current).ConfigureAwait(false);
                return;
            }
            if (settings is not null && state.Approval.LeaseExpiresAt is DateTime leaseExpiresAt
                && state.Approval.Call.Function.Name == "generate_media")
            {
                // 参数修改后以同一所有者重写租约集合并刷新有效期（对应 Go: ReplaceCloudAgentResourceLeases）。
                CloudAgentMediaArgs planArgs =
                    CloudAgentContracts.DecodeObject<CloudAgentMediaArgs>(state.Approval.Call.Function.Arguments);
                List<string> leaseResources = await LeaseResourceIDsAsync(
                    context, userID, state.Request.CanvasID, planArgs.ReferenceNodeIDs).ConfigureAwait(false);
                await context.ReplaceCloudAgentResourceLeasesAsync(
                    userID, id, approvalID, leaseResources, leaseExpiresAt).ConfigureAwait(false);
            }
            current.Status = "running";
            CloudAgentContracts.AddEvent(state, id, "approval_decided", CloudAgentContracts.Payload(
                ("approvalId", approvalID),
                ("decision", decision),
                ("arguments", JsonSerializer.SerializeToElement(state.Approval!.Call.Function.Arguments)),
                ("preview", JsonSerializer.SerializeToElement(state.Approval.Preview, GoJson.WriteOptions)),
                ("modelName", state.Approval.ModelName)));
            CloudAgentContracts.Save(current, state);
            await context.SaveRunAsync(current).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (!claimed)
        {
            throw CloudAgentSessionService.CreationConflict();
        }
    }

    private static string CreationSettingsHash(CloudAgentMediaSettings settings) =>
        CloudAgentContracts.Sha256Hex(JsonSerializer.Serialize(settings, GoJson.WriteOptions));

    /// <summary>
    /// 收集参考节点的资源 ID（metadata.storageKey 的 resource: 前缀）。
    /// 对应 Go: prepared.ResourceSignatures 的键集合。
    /// </summary>
    private async Task<List<string>> LeaseResourceIDsAsync(
        CloudAgentMutationContext context, string userID, string canvasID,
        IReadOnlyList<string> referenceNodeIDs)
    {
        if (referenceNodeIDs.Count == 0)
        {
            return [];
        }
        CanvasProject? canvas = await context.CanvasProjectForUserAsync(userID, canvasID).ConfigureAwait(false);
        if (canvas is null)
        {
            return [];
        }
        JsonObject doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
        Dictionary<string, JsonObject> nodes = CloudAgentJsonHelpers.Objects(CloudAgentJsonHelpers.Get(doc, "nodes"));
        List<string> ids = [];
        foreach (string nodeID in referenceNodeIDs)
        {
            if (!nodes.TryGetValue(nodeID, out JsonObject? node))
            {
                continue;
            }
            string? key = (node["metadata"] as JsonObject)?["storageKey"] is JsonValue value
                && value.TryGetValue<string>(out string? text)
                ? text
                : null;
            if (key is not null && key.StartsWith("resource:", StringComparison.Ordinal))
            {
                ids.Add(key["resource:".Length..]);
            }
        }
        return ids;
    }

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
        // 批量产物（count>1）：真实参数在 state.MediaBatch 里，call.Function.Arguments 只是首项快照。
        // 必须整批一起改，否则预览会显示 1 张、后几张却按旧的模型/画幅提交。
        List<CloudAgentMediaArgs> items = state.MediaBatch is { Count: > 0 } batch
            ? [.. batch.Select(entry => entry.Args)]
            : [args];
        foreach (CloudAgentMediaArgs item in items)
        {
            item.LogicalModelID = settings.LogicalModelID;
            item.ChannelID = settings.ChannelID;
            item.ChannelModelKey = settings.ChannelModelKey;
            item.Size = settings.Size;
            item.Quality = settings.Quality;
        }
        // 首项回写调用参数：审批哈希、撤销与「审批内容一致性」校验都按它走。
        call.Function.Arguments = JsonSerializer.Serialize(items[0], GoJson.WriteOptions);
        await using CloudAgentMutationContext planningContext =
            await _repository.OpenCloudAgentContextAsync(cancellationToken).ConfigureAwait(false);
        // 只重算请求与预览，不重建草稿节点：节点在首次 prepare 时已落画布，
        // PrepareCoreAsync 本身不写画布（写画布的是 BakeMediaDraftAsync）。
        // 逐项走 PrepareItemAsync：批量项的 Count 已是 1，再走 PrepareAsync 会把编号节点
        // 又派生一层（image-new-1-1）。
        foreach (CloudAgentMediaArgs item in items)
        {
            (List<CreateTaskRequestDto> requests, CloudAgentMediaPlan _) = await _media.PrepareItemAsync(
                planningContext, run, state, item, call.ID, cancellationToken).ConfigureAwait(false);
            // 干跑准入使用与提交相同的模型、归属与规格校验，不预留积分、不建任务。
            (TaskEntity admitted, BillingOrder? _) = await _taskCreation.AdmitQueuedAsync(
                run.UserID, requests[0], requests[0].Input, requests[0].Type!, requests[0].Prompt, "", "",
                cancellationToken).ConfigureAwait(false);
            Dictionary<string, JsonElement>? resolvedConfig = InputConfigInputJson(admitted.InputJSON);
            CloudAgentMediaService.ValidateResolvedOptions(InputConfig(requests[0].Input), resolvedConfig!);
        }
        CloudAgentMediaPlan plan = new() { Items = [.. items.Select(item => item)], CallID = call.ID };
        string modelName = await _media.ModelNameAsync(plan.Args, cancellationToken).ConfigureAwait(false);
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
        // 批量产物（本 fork 扩展）的任务 ID 在批次项上：收尾时要一并取消，否则会留下没人管的收费任务。
        List<string> cleanupIDs = [run.ID, activeID, mediaID];
        if (state?.MediaBatch is { Count: > 0 } cleanupBatch)
        {
            cleanupIDs.AddRange(cleanupBatch.Select(item => item.TaskID));
        }
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string id in cleanupIDs)
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
        if ((mediaID.Length > 0 || state?.MediaBatch is { Count: > 0 })
            && state is not null && state.CallIndex < state.Calls.Count)
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
                // 清理收尾释放本轮全部资源租约（对应 Go: finishCloudAgentCleanup 的 ReleaseByRun）。
                await context.ReleaseCloudAgentResourceLeasesByRunAsync(run.UserID, run.ID).ConfigureAwait(false);
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
