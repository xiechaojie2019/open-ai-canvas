#nullable enable
using System.Text.Json;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Protocol;
using OpenAICanvas.Platform;
using OpenAICanvas.Providers;
using OpenAICanvas.Outbound;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Application;

/// <summary>
/// 任务 Worker：领取、租约维护与执行编排。
/// 对应 Go: <c>app/task_worker.go</c> 的 <c>taskWorkerCoordinator</c>。
/// </summary>
public sealed class TaskWorkerService
{
    /// <summary>全局并发槽 TTL。对应 Go: <c>workerSlotLeaseDuration</c>。</summary>
    private static readonly TimeSpan SlotLeaseDuration = TimeSpan.FromMinutes(1);

    /// <summary>任务租约时长。对应 Go: <c>ClaimNextTask</c> 的 45s。</summary>
    private static readonly TimeSpan TaskLeaseDuration = TimeSpan.FromSeconds(45);

    /// <summary>执行失败后的续排队延迟。对应 Go: <c>DeferRunningTaskForProviderPoll</c> 的 15s。</summary>
    private static readonly TimeSpan ProviderPollDelay = TimeSpan.FromSeconds(15);

    private readonly Repository _repository;
    private readonly TaskTerminalService _terminal;
    private readonly CloudAgent.AgentMemoryCompactService _compact;
    private readonly IRuntimePolicyProvider _policy;
    private readonly Coordinator? _coordinator;
    private readonly TimelineTaskExecutor? _timeline;
    private readonly ApiCallAuditWriter _apiCallAudit;

    public TaskWorkerService(
        Repository repository,
        IRuntimePolicyProvider policy,
        Coordinator? coordinator = null,
        TimelineTaskExecutor? timeline = null)
    {
        _repository = repository;
        _policy = policy;
        _coordinator = coordinator;
        _timeline = timeline;
        _apiCallAudit = new ApiCallAuditWriter(repository, policy);
        // CanvasService 只为文本回放收尾服务；未注入时退化为基础任务服务（回放跳过）。
        _terminal = new TaskTerminalService(
            repository, CanvasService?.Tasks ?? new TaskService(repository), _apiCallAudit);
        _compact = new CloudAgent.AgentMemoryCompactService(repository);
    }

    /// <summary>终态协调依赖文本回放收尾，暂时借道 CanvasService.Tasks；测试可用 <see cref="CanvasService"/> 注入。</summary>
    public CanvasService? CanvasService { get; set; }

    /// <summary>
    /// 生成媒体落盘服务：任务成功后把结果内联 dataUrl 存为账号资源再落 ResultJSON。
    /// 对应 Go: <c>persistGeneratedMediaResult</c>；未注入时结果保留内联 dataUrl（退化行为）。
    /// </summary>
    public ResourceUploadService? GeneratedMediaUpload { get; set; }

    // ------------------------------------------------------------- 调度循环

    /// <summary>
    /// 单轮调度：按策略并发数领任务。对应 Go: <c>start</c> 的第二个 runWorkerLoop。
    /// 返回本轮实际启动的任务数（-1 表示全局槽或运行时策略不可用）。
    /// </summary>
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        if (Draining)
        {
            return 0;
        }
        RuntimeTaskPolicy setting = _policy.Current().Task;
        // 与 Go 一致：并发数下限 1，缺失或 0 视为未配置，直接用默认 3。
        int workerConcurrency = Math.Max(1, setting.WorkerConcurrency);

        int started = 0;
        while (started < workerConcurrency)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_coordinator is null)
            {
                return started > 0 ? started : -1;
            }
            (SlotLease? slot, bool acquired, string? slotError) =
                await _coordinator.AcquireLeaseAsync("workers", workerConcurrency, SlotLeaseDuration, cancellationToken)
                    .ConfigureAwait(false);
            if (slotError is not null || !acquired || slot is null)
            {
                if (slot is not null)
                {
                    await slot.ReleaseAsync().ConfigureAwait(false);
                }
                return started > 0 ? started : -1;
            }

            TaskEntity? task = await ClaimWithTimeoutAsync(slot, cancellationToken).ConfigureAwait(false);
            if (task is null)
            {
                await slot.ReleaseAsync().ConfigureAwait(false);
                return started;
            }

            started++;
            SlotLease owned = slot;
            string taskID = task.ID;
            string leaseOwner = task.LeaseOwner;
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await ProcessClaimedTaskAsync(task, owned).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        await _terminal.EnsureFailedTerminalStateAsync(task, error).ConfigureAwait(false);
                        try
                        {
                            await _repository.CreateTaskLogAsync(
                                task.UserID, taskID, "error", "后台任务处理失败", error.Message).ConfigureAwait(false);
                        }
                        catch
                        {
                            // 任务终态和日志都失败时不能让后台 Task 未观察异常。
                        }
                    }
                    finally
                    {
                        await owned.ReleaseAsync().ConfigureAwait(false);
                    }
                },
                CancellationToken.None);
        }
        return started;
    }

    /// <summary>优雅停机开关。对应 Go: <c>s.IsDraining()</c>。</summary>
    public bool Draining { get; set; }

    /// <summary>领取带 5 秒上限，防止数据库抖动挂住调度循环。对应 Go: <c>claimCtx 5s</c>。</summary>
    private async Task<TaskEntity?> ClaimWithTimeoutAsync(SlotLease slot, CancellationToken outer)
    {
        using CancellationTokenSource claim = CancellationTokenSource.CreateLinkedTokenSource(outer);
        claim.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await _repository
                .ClaimNextTaskAsync(slot.Token, TaskLeaseDuration, claim.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>测试与运维入口：领取并同步处理一个任务。对应 Go: <c>processNextTask</c>。</summary>
    /// <summary>
    /// 管理端渠道模型连通性测试：复用真实生成协议与运行时策略，
    /// 不创建用户任务或计费订单。对应 Go: <c>TestAdminChannelModel</c> 的执行分支。
    /// </summary>
    public async Task<long> RunProviderProbeAsync(
        string capability, TextTaskInput input, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        ProtocolAdapterRegistry? declarativeAdapters = CanvasService?.Plugins.RegistrySnapshot();
        ProviderRequestContext context = new(_policy, _coordinator, declarativeAdapter: declarativeAdapters);
        long startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            switch (capability)
            {
                case "text":
                    await new ProviderTextTask(context).RunTextTaskAsync(
                        input, cancellationToken: timeout.Token).ConfigureAwait(false);
                    break;
                case "image":
                    await new ProviderImageTask(context).RunAsync(input, timeout.Token).ConfigureAwait(false);
                    break;
                case "video":
                    await new ProviderVideoTask(context).RunAsync(
                        input, cancellationToken: timeout.Token).ConfigureAwait(false);
                    break;
                case "audio":
                    await new ProviderAudioTask(context).RunAsync(input, timeout.Token).ConfigureAwait(false);
                    break;
                default:
                    throw AppError.BadAuthRequest("不支持测试的模型能力");
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            bool timeoutHit = !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested;
            string message = error.Message;
            throw AppError.Wrap(
                timeoutHit ? 504 : 502, "模型测试失败：" + message, error);
        }
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt;
    }

    public async Task<bool> ProcessNextTaskAsync(CancellationToken cancellationToken = default)
    {
        if (_coordinator is null)
        {
            TaskEntity? claimed = await _repository
                .ClaimNextTaskAsync(NewOwner(), TaskLeaseDuration, cancellationToken)
                .ConfigureAwait(false);
            if (claimed is null)
            {
                return false;
            }
            await ProcessClaimedTaskAsync(claimed, null).ConfigureAwait(false);
            return true;
        }
        (SlotLease? slot, bool acquired, string? error) = await _coordinator
            .AcquireLeaseAsync("workers", 999, SlotLeaseDuration, cancellationToken)
            .ConfigureAwait(false);
        if (!acquired || slot is null)
        {
            if (error is not null)
            {
                throw new InvalidOperationException($"获取任务并发槽失败：{error}");
            }
            return false;
        }
        try
        {
            TaskEntity? claimed = await _repository
                .ClaimNextTaskAsync(slot.Token, TaskLeaseDuration, cancellationToken)
                .ConfigureAwait(false);
            if (claimed is null)
            {
                return false;
            }
            await ProcessClaimedTaskAsync(claimed, slot).ConfigureAwait(false);
            return true;
        }
        finally
        {
            await slot.ReleaseAsync().ConfigureAwait(false);
        }
    }

    private static string NewOwner() => $"manual:{Guid.NewGuid():N}";

    /// <summary>对应 Go: <c>firstNonEmpty</c>。按序取第一个非空（已去除首尾空白）值。</summary>
    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (value.Trim().Length > 0)
            {
                return value;
            }
        }
        return "";
    }

    // ------------------------------------------------------------- 执行编排

    /// <summary>
    /// 处理已领取任务的全流程。对应 Go: <c>processClaimedTask</c>。
    /// 返回 null 表示取消已被正常收尾；其余错误交给调用方记入任务日志。
    /// </summary>
    public async Task<Exception?> ProcessClaimedTaskAsync(
        TaskEntity claimed, SlotLease? slot, CancellationToken dispatcherToken = default)
    {
        // 执行不受 HTTP 请求生命周期影响：超时来自任务类型策略，取消只由续租失败或停机触发。
        using CancellationTokenSource timeoutCancellation = new();
        timeoutCancellation.CancelAfter(TaskExecutionTimeout(claimed.Type));
        using CancellationTokenSource execution = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCancellation.Token);
        int timeoutHit = 0;
        int leaseLost = 0;
        using CancellationTokenRegistration timeoutRegistration = timeoutCancellation.Token.Register(
            () => Interlocked.Exchange(ref timeoutHit, 1));
        CancellationToken ct = execution.Token;
        CancellationTokenSource? renewLoop = null;
        Task renewTask = Task.CompletedTask;
        if (slot is not null)
        {
            renewLoop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            renewTask = Task.Run(
                () => RenewLeaseLoopAsync(
                    claimed, slot, execution, () => Interlocked.Exchange(ref leaseLost, 1), renewLoop.Token),
                CancellationToken.None);
        }

        try
        {
            await _repository.CreateTaskLogAsync(claimed.UserID, claimed.ID, "info", "后端任务开始处理", "",
                CancellationToken.None).ConfigureAwait(false);

            // 取消请求可能在领取和注册执行上下文之间到达；重读终态避免该窗口仍向上游发起调用。
            TaskEntity? latest = await _repository.TaskAsync(claimed.ID, ct).ConfigureAwait(false);
            if (latest is not null && latest.Status == TaskStatus.TaskStatusCancelled)
            {
                await _terminal.HandleAlreadyCancelledAsync(latest).ConfigureAwait(false);
                return null;
            }

            if (claimed.Type is "timeline_transcription" or "timeline_render")
            {
                if (_timeline is null)
                {
                    throw new TimelineTaskException("时间线 Worker 未配置本地执行器");
                }

                await _repository.UpdateTaskProgressForLeaseAsync(
                    claimed.ID, claimed.LeaseOwner, "准备本地媒体任务", 10, ct).ConfigureAwait(false);
                Dictionary<string, object?> timelineResult = await _timeline
                    .ExecuteAsync(claimed, ct).ConfigureAwait(false);
                latest = await _repository.TaskAsync(claimed.ID, ct).ConfigureAwait(false);
                if (latest is null)
                {
                    throw new InvalidOperationException("任务记录不存在");
                }
                if (latest.Status == TaskStatus.TaskStatusCancelled)
                {
                    await _terminal.HandleCancelledResultAsync(latest).ConfigureAwait(false);
                    return null;
                }

                string timelineResultJSON = JsonSerializer.Serialize(
                    timelineResult, ProjectCharacterService.GoPayloadOptions);
                await SaveCompletionWithinQuotaAsync(latest, timelineResultJSON, ct).ConfigureAwait(false);
                await _terminal.HandleSuccessAsync(latest).ConfigureAwait(false);
                return null;
            }

            string stage = "调用生成模型";
            long progress = 35;
            if (TaskUsesUpstreamReportedProgress(claimed.Type))
            {
                // 图片/视频百分比只能来自供应商状态响应，连接阶段不能再用 35% 冒充真实进度。
                stage = "正在连接上游";
                progress = 0;
            }
            await _repository.UpdateTaskProgressForLeaseAsync(claimed.ID, claimed.LeaseOwner, stage, progress, ct)
                .ConfigureAwait(false);

            // 记忆压缩任务开始调用模型（对应 Go: markAgentMemoryCompactRunning）。
            await _compact.MarkRunningAsync(claimed, ct).ConfigureAwait(false);

            await _repository.MarkBillingRunningAsync(claimed.BillingOrderID, ct).ConfigureAwait(false);

            (Dictionary<string, object?> result, bool providerSucceeded) =
                await ExecuteWithRouteFailoverAsync(claimed, ct).ConfigureAwait(false);

            latest = await _repository.TaskAsync(claimed.ID, ct).ConfigureAwait(false);
            if (latest is null)
            {
                throw new InvalidOperationException("任务记录不存在");
            }
            if (latest.Status == TaskStatus.TaskStatusCancelled)
            {
                await _compact.NoteTaskAsync(latest, null, new InvalidOperationException("压缩任务已取消"), ct)
                    .ConfigureAwait(false);
                await _terminal.HandleCancelledResultAsync(latest).ConfigureAwait(false);
                return null;
            }

            // 媒体恢复恢复路径：带检查点的任务是“作品已生成、保存未完成”的重试，
            // 物化检查点而不是使用原始结果（对应 Go: resumeTaskMedia 分支；
            // 管线注册 Materializer 后生效，未注册的部署行为不变）。
            if (claimed.MediaRecoveryJSON.Length > 0 && CanvasService?.MediaRecovery.Materializer is not null)
            {
                try
                {
                    result = await CanvasService.MediaRecovery.MaterializeAsync(claimed, ct).ConfigureAwait(false);
                }
                catch (Exception recoveryError) when (recoveryError is not OperationCanceledException)
                {
                    await CanvasService.MediaRecovery.HandleFailureAsync(
                        claimed, claimed.MediaStage, retryable: true, recoveryError,
                        (taskId, owner, stage, delay, token) =>
                            _repository.DeferRunningTaskForProviderPollAsync(taskId, owner, stage, delay, token),
                        CancellationToken.None).ConfigureAwait(false);
                    return null;
                }
            }

            // 上游成功后先把结果内联 dataUrl 落库为账号资源，再序列化保存
            // （对应 Go: task_worker 的 persistGeneratedMediaResult）。落盘失败按
            // 「上游已成功、本地保存失败」收尾：计费转待核对而不退款。
            if (GeneratedMediaUpload is not null)
            {
                try
                {
                    result = await GeneratedMediaUpload.PersistGeneratedMediaResultAsync(
                        claimed.UserID, result, ct).ConfigureAwait(false);
                }
                catch (Exception persistError) when (persistError is not OperationCanceledException)
                {
                    await _compact.NoteTaskAsync(claimed, null, persistError, CancellationToken.None)
                        .ConfigureAwait(false);
                    return await _terminal.HandleExecutionFailureAsync(
                        claimed, persistError, providerSucceeded: true, channelSlotFailedBeforeRequest: false,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            string resultJSON = JsonSerializer.Serialize(result, ProjectCharacterService.GoPayloadOptions);
            await SaveCompletionWithinQuotaAsync(latest, resultJSON, ct).ConfigureAwait(false);
            await _compact.NoteTaskAsync(latest, result, null, ct).ConfigureAwait(false);
            await _terminal.HandleSuccessAsync(latest).ConfigureAwait(false);
            return null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            bool channelSlotFailedBeforeRequest = error is ProviderChannelSlotException;
            await _compact.NoteTaskAsync(claimed, null, error, CancellationToken.None).ConfigureAwait(false);
            return await _terminal.HandleExecutionFailureAsync(
                claimed, error, providerSucceeded: false, channelSlotFailedBeforeRequest,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref leaseLost) != 0 && Volatile.Read(ref timeoutHit) == 0)
            {
                // 租约已丢失，允许新 worker 接管，旧 worker 不能覆盖其状态。
                return null;
            }

            Exception cancellationError = Volatile.Read(ref timeoutHit) != 0
                ? new TimeoutException(TaskTimeoutMessage(claimed.Type))
                : new OperationCanceledException("任务已取消");
            await _compact.NoteTaskAsync(claimed, null, cancellationError, CancellationToken.None)
                .ConfigureAwait(false);
            await _terminal.HandleExecutionFailureAsync(
                claimed,
                cancellationError,
                providerSucceeded: false,
                channelSlotFailedBeforeRequest: false,
                CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        finally
        {
            if (renewLoop is not null)
            {
                await renewLoop.CancelAsync().ConfigureAwait(false);
                try
                {
                    await renewTask.ConfigureAwait(false);
                }
                catch
                {
                    // 续租循环的退出异常不影响执行结果（失败已在循环内触发执行取消）。
                }
                renewLoop.Dispose();
            }
        }
    }

    /// <summary>15 秒周期续租；失败即取消执行，让任务可被其他 worker 接管。对应 Go: <c>leaseLost</c> goroutine。</summary>
    private async Task RenewLeaseLoopAsync(
        TaskEntity task,
        SlotLease slot,
        CancellationTokenSource execution,
        Action markLeaseLost,
        CancellationToken stopToken)
    {
        PeriodicTimer timer = new(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(stopToken).ConfigureAwait(false))
            {
                (bool renewed, string? error) = await slot.RenewAsync(stopToken).ConfigureAwait(false);
                if (renewed && error is null)
                {
                    await _repository.RenewTaskLeaseAsync(task.ID, task.LeaseOwner, TaskLeaseDuration, stopToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    markLeaseLost();
                    await execution.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出（执行结束或租约失效）。
        }
    }

    // ------------------------------------------------------------- 上游执行分发

    /// <summary>任务结果（媒体/文本字典）与上游是否已确认执行的标记。</summary>
    private readonly record struct ProviderExecutionResult(
        Dictionary<string, object?> Result, bool ProviderSucceeded);

    /// <summary>
    /// 带路由失败切换的执行循环（managed 任务）：上游安全拒绝（401/403/404/429、渠道槽位）
    /// 时阻断失败线路、重算成本并切换备用线路重试；非安全拒绝保持原始失败。
    /// 直连任务仍单次执行，仅留路由尝试记录。对应 Go: <c>routeExecutor.execute</c>。
    /// </summary>
    private async Task<(Dictionary<string, object?> Result, bool ProviderSucceeded)> ExecuteWithRouteFailoverAsync(
        TaskEntity task, CancellationToken cancellationToken)
    {
        bool managed = task.LogicalModelID.Length > 0;
        RouteAttemptOrchestrator orchestrator = RouteAttemptOrchestrator.Shared(
            _repository, CanvasService?.LogicalModels
                ?? throw new InvalidOperationException("任务 Worker 未注入 CanvasService"));
        RouteAttempt? attempt = await orchestrator.BeginTaskRouteAttemptAsync(task, cancellationToken)
            .ConfigureAwait(false);
        while (true)
        {
            await orchestrator.MarkDispatchingAsync(attempt!, cancellationToken).ConfigureAwait(false);
            Exception? executionError = null;
            Dictionary<string, object?> result;
            try
            {
                result = (await ExecuteProviderTaskAsync(task, cancellationToken).ConfigureAwait(false)).Result;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                executionError = error;
                result = [];
            }
            await orchestrator.FinishAttemptAsync(attempt!, task, executionError, cancellationToken)
                .ConfigureAwait(false);
            if (executionError is null)
            {
                return (result, true);
            }
            if (!managed)
            {
                throw executionError;
            }
            RouteAttempt? next;
            try
            {
                next = await orchestrator.NextAttemptAfterFailureAsync(
                    task, attempt!, executionError, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception routeError) when (
                routeError is AppError or TaskRouteChargeLimitException
                or TaskRouteConflictException or InsufficientCreditsException)
            {
                await _repository.CreateTaskLogAsync(
                    task.UserID, task.ID, "warn", "备用路由不可用，保留原始失败", routeError.Message,
                    CancellationToken.None).ConfigureAwait(false);
                throw executionError;
            }
            if (next is null)
            {
                throw executionError;
            }
            task.RouteID = next.RouteID;
            task.ChannelModelID = next.ChannelModelID;
            task.ProviderRequestID = "";
            await _repository.CreateTaskLogAsync(
                task.UserID, task.ID, "warn", "上游未创建任务，切换备用能力路由", next.RouteID,
                CancellationToken.None).ConfigureAwait(false);
            attempt = next;
        }
    }

    /// <summary>
    /// 解密输入、解析渠道并按任务类型分发给对应协议实现。
    /// 对应 Go: <c>routeExecutor.execute</c> 的单次协议执行。
    /// </summary>
    private async Task<ProviderExecutionResult> ExecuteProviderTaskAsync(
        TaskEntity task, CancellationToken cancellationToken)
    {
        TextTaskInput input;
        try
        {
            input = JsonSerializer.Deserialize<TextTaskInput>(
                DecryptTaskInputJSON(task.InputJSON), ProjectCharacterService.GoPayloadOptions) ?? new TextTaskInput();
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"任务输入不是合法 JSON：{error.Message}", error);
        }

        input.Config = await ResolveProviderConfigAsync(input.Config, cancellationToken).ConfigureAwait(false);
        // 参考素材水合：resource: 引用解析为供应商可用的 URL/字节
        // （对应 Go: processCanvasGenerationTask 的 hydrateGenerationMedia；无引用时零开销直通）。
        // 有引用时水合是必须步骤：跳过会让 multipart 协议在打包阶段报 data URL 错误，
        // 且被归类成"连接模型服务失败"误导排障 —— 组合根漏注入必须在此显式失败。
        if (ProviderMediaHydrator.HasResourceReferences(input))
        {
            if (CanvasService is null)
            {
                throw new InvalidOperationException(
                    "任务 Worker 未注入 CanvasService，无法解析 resource: 参考素材");
            }
            // Agent 看图按文本能力合同强制数量/字节上限（普通任务传 null 走协议策略）。
            OpenAICanvas.Application.Capabilities.TextReferenceConfig? textReferences = null;
            if (input.Mode == "text" && input.AgentRequests?.Canonical is not null)
            {
                OpenAICanvas.Application.Capabilities.ModelCapabilityConfig? textCapability =
                    await ResolveTextCapabilityAsync(input.Config, cancellationToken).ConfigureAwait(false);
                textReferences = textCapability?.Text?.References;
            }
            await ProviderMediaHydrator.HydrateGenerationMediaAsync(
                task.UserID, input, _repository, CanvasService.ResourceDomain,
                _policy.Current(), textReferences, cancellationToken).ConfigureAwait(false);
        }
        // 注入插件运行时的声明式注册表快照（10.2）：图片/视频/音频声明式分支由此生效。
        ProtocolAdapterRegistry? declarativeAdapters = CanvasService?.Plugins.RegistrySnapshot();
        // 调用审计元数据随上下文下发到 Provider 出站收口（对应 Go: withProviderAnalytics）：
        // 渠道 ID/模型以解析后的 config 为准，能力以 input.Mode 覆盖任务类型推断。
        ProviderCallAudit audit = new(
            UserID: task.UserID,
            TaskID: task.ID,
            TraceID: task.TraceID,
            RequestID: task.RequestID,
            BillingOrderID: task.BillingOrderID,
            ChannelID: FirstNonEmpty(
                input.Config.ChannelID.Trim(),
                ApiCallAuditWriter.SystemChannelIDFromBaseURL(input.Config.BaseURL)),
            Capability: FirstNonEmpty(
                ApiCallAuditWriter.NormalizeCapability(input.Mode),
                ApiCallAuditWriter.CapabilityFromTaskType(task.Type)),
            Operation: task.Operation,
            Model: FirstNonEmpty(
                input.Config.ChannelModelKey.Trim(), input.Config.Model.Trim(), task.Model),
            VideoSeconds: int.TryParse(input.Config.VideoSeconds.Trim(), out int videoSeconds)
                ? videoSeconds
                : 0);
        ProviderRequestContext context = new(
            _policy, _coordinator, declarativeAdapter: declarativeAdapters,
            auditWriter: _apiCallAudit, audit: audit);
        if (ProviderWorkflowValues.IsWorkflowProviderInterface(input.Config.InterfaceType))
        {
            // 后台执行仍要过平台门控（对应 Go: provider.go 的 RequireWorkflowPluginForInterface）：
            // 任务创建后被管理员停用时，执行侧以平台状态行为准拒绝并走失败路径。
            await new WorkflowPluginGate(_repository)
                .RequireForInterfaceAsync(input.Config.InterfaceType, cancellationToken).ConfigureAwait(false);
            // 工作流协议是独立执行器：三要素里只有 API Key 必填，模型能力校验
            // 全部由工作流字段定义承担，不能套用普通模型的三要素检查。
            return new(await new ProviderWorkflowTask(context).RunAsync(
                input, task.ProviderRequestID, cancellationToken: cancellationToken).ConfigureAwait(false), true);
        }
        if (input.Config.BaseURL.Trim().Length == 0
            || input.Config.APIKey.Trim().Length == 0
            || input.Config.Model.Trim().Length == 0)
        {
            throw new InvalidOperationException("后端生成任务缺少 Base URL、API Key 或模型名");
        }
        if (input.Prompt.Trim().Length == 0 && task.Prompt.Trim().Length > 0)
        {
            input.Prompt = task.Prompt;
        }
        // 提示词模板编译：唯一开关是 metadata.promptTemplateOperation
        // （对应 Go: processCanvasGenerationTask 的 compilePrompt 分支，provider.go:350-359）。
        // 编译把「启用模板 → 用户定制 → 变量渲染 → 受保护上下文（剧情/画布资产/角色版本/JSON 契约）」
        // 装配成真正发给模型的一整段提示词；没有这一步，运营模板和用户定制全是死数据。
        // 视频节点的最终 Prompt 只取输入框内容，不允许被分镜模板替换。
        string promptTemplateOperation =
            ProviderHelpers.MetadataString(input.Metadata, "promptTemplateOperation");
        if (input.Mode != "video" && promptTemplateOperation.Length > 0)
        {
            try
            {
                input.Metadata.TryGetValue("promptTemplateVariables", out object? templateValues);
                OpenAICanvas.Application.Prompts.CompiledPrompt compiled =
                    await new OpenAICanvas.Application.Prompts.PromptTemplateService(_repository)
                        .CompilePromptAsync(
                            task.UserID,
                            promptTemplateOperation,
                            OpenAICanvas.Application.Prompts.PromptTemplateService
                                .TemplateValues(templateValues),
                            cancellationToken).ConfigureAwait(false);
                input.Prompt = compiled.Content;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                // 文案必须原样透出（Go 用 %w 包一层后照样落在 task.Error 上）：
                // 兜底异常会经 UserFacing 变成「连接模型服务失败」，把排障方向带偏。
                throw AppError.BadAuthRequest($"编译用户提示词失败：{error.Message}");
            }
        }
        // 将 @[tool:type:ID:label:icon] 令牌替换为对应工具的提示词文本。
        // 对应 Go: processCanvasGenerationTask（无令牌时零查询直通）。
        input.Prompt = await new ToolsService(_repository).ResolveToolMentionTokensAsync(
            task.UserID, input.Mode, input.Prompt, cancellationToken).ConfigureAwait(false);
        // 执行端补注入视频能力声明（对应 Go: validateResolvedVideoCapability 的渠道模型分支）：
        // 任务 input 不持久化 VideoCapability，分辨率等参数归一依赖执行时按渠道模型能力
        // 合同重放，缺失时裸值（如 UI 发的 "720"）会原样透传给上游并被 400。
        if (task.Type.StartsWith("canvas_video", StringComparison.Ordinal)
            || task.Type.StartsWith("video_", StringComparison.Ordinal))
        {
            OpenAICanvas.Providers.VideoCapabilityConfig? videoProfile = await ResolveVideoCapabilityAsync(
                input.Config, cancellationToken).ConfigureAwait(false);
            if (videoProfile is not null)
            {
                input.VideoCapability = videoProfile;
                ProviderVideoOptions.ApplyFixedVideoResolution(input.Config, videoProfile);
            }
        }

        ProviderExecutionResult execution = task.Type switch
        {
            _ when task.Type.StartsWith("canvas_text", StringComparison.Ordinal) || task.Type == "text" =>
                input.AgentRequests is not null
                    ? new(await RunAgentToolTaskAsync(input, context, cancellationToken)
                        .ConfigureAwait(false), true)
                    : new(await RunTextTaskWithContractAsync(
                        input, context, promptTemplateOperation, cancellationToken)
                        .ConfigureAwait(false), true),
            _ when task.Type.StartsWith("canvas_image", StringComparison.Ordinal) =>
                new(await new ProviderImageTask(context).RunAsync(input, cancellationToken).ConfigureAwait(false), true),
            _ when task.Type.StartsWith("canvas_video", StringComparison.Ordinal)
                || task.Type.StartsWith("video_", StringComparison.Ordinal) =>
                new(await new ProviderVideoTask(context)
                    .RunAsync(input, task.ProviderRequestID, cancellationToken: cancellationToken)
                    .ConfigureAwait(false), true),
            _ when task.Type.StartsWith("canvas_audio", StringComparison.Ordinal) =>
                new(await new ProviderAudioTask(context).RunAsync(input, cancellationToken).ConfigureAwait(false), true),
            _ => throw new InvalidOperationException("任务类型没有可用的执行分支"),
        };
        return execution;
    }

    /// <summary>
    /// 文本任务执行 + 受保护结果契约校验。
    /// 对应 Go: <c>processCanvasGenerationTask</c> 的
    /// <c>runTextTask</c> 之后紧跟的 <c>validatePromptTemplateResult</c>（provider.go:471-473）。
    /// </summary>
    /// <remarks>
    /// 校验发生在文本任务<b>成功之后</b>：此时上游已经产生结果，但结果不符合操作声明的 JSON 契约
    /// （模型返回散文、字段缺失、镜头数不足等）。这种失败必须让任务显式失败，
    /// 否则前端会拿到一段散文当「分镜结果」，报出更难定位的「没有返回镜头行」。
    /// Agent 分支不做校验（与 Go 一致：agent 有自己的一套工具结果处理）。
    /// </remarks>
    private static async Task<Dictionary<string, object?>> RunTextTaskWithContractAsync(
        TextTaskInput input,
        IProviderRequestContext context,
        string promptTemplateOperation,
        CancellationToken cancellationToken)
    {
        Dictionary<string, object?> result = await new ProviderTextTask(context)
            .RunTextTaskAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (promptTemplateOperation.Length > 0)
        {
            OpenAICanvas.Application.Prompts.PromptTemplateService
                .ValidatePromptTemplateResult(promptTemplateOperation, result);
        }
        return result;
    }

    /// <summary>
    /// Agent 工具任务执行：水合规划图片占位，按能力合同决定流式与模型声明输出上限。
    /// 对应 Go: <c>processCanvasGenerationTask</c> 的 text+agentRequests 分支
    /// 与 <c>provider.go</c> 的 StreamText/MaxOutputTokens 判定。
    /// </summary>
    private async Task<Dictionary<string, object?>> RunAgentToolTaskAsync(
        TextTaskInput input, IProviderRequestContext context, CancellationToken cancellationToken)
    {
        AgentResourceReferences.Hydrate(input);
        // Agent 请求在此时才展开上游协议；流式与否由任务 textOptions 与模型能力合同共同决定。
        ModelCapabilityConfig? capability = await ResolveTextCapabilityAsync(input.Config, cancellationToken)
            .ConfigureAwait(false);
        bool supportsStream = capability?.Text?.Streaming ?? true;
        if (capability?.Text is not null)
        {
            // 与 Go 一致：模型声明了输出上限时不静默回退传输层固定值。
            input.MaxOutputTokens = capability.Text.MaxOutputTokens;
        }
        bool requestedStream = input.TextOptions.Stream is null || input.TextOptions.Stream == true;
        return await new ProviderTextTask(context).RunAgentToolAsync(
            input, requestedStream && supportsStream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>系统渠道文本能力合同（自定义渠道无合同，返回 null 走默认）。</summary>
    private async Task<ModelCapabilityConfig?> ResolveTextCapabilityAsync(
        ProviderConfig config, CancellationToken cancellationToken)
    {
        string channelID = config.ChannelID.Trim();
        if (channelID.Length == 0)
        {
            return null;
        }
        ModelChannel? channel = await _repository.SystemChannelAsync(channelID, cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            return null;
        }
        string modelKey = config.ChannelModelKey.Trim();
        if (modelKey.StartsWith("models/", StringComparison.Ordinal))
        {
            modelKey = modelKey["models/".Length..].Trim();
        }
        if (modelKey.Length == 0)
        {
            modelKey = config.Model.Trim();
        }
        ChannelModel? channelModel = await _repository
            .ChannelModelByKeyIncludingDisabledAsync(channel.ID, modelKey, cancellationToken)
            .ConfigureAwait(false);
        return channelModel is null ? null : ChannelModelCapability.NormalizedChannelModelCapability(channelModel);
    }

    /// <summary>
    /// 解析最终出站配置。自定义渠道（无 ChannelID）按原样使用；
    /// 系统渠道以 channel_models 记录为唯一授权来源。对应 Go: <c>resolveProviderConfig</c> 的简化版
    /// （RunningHub 默认地址与价格档匹配待 4.12 接入）。
    /// </summary>
    private async Task<ProviderConfig> ResolveProviderConfigAsync(
        ProviderConfig config, CancellationToken cancellationToken)
    {
        config.Headers = OutboundGuard.NormalizeOutboundHeaders(config.Headers);
        string channelID = config.ChannelID.Trim();
        if (channelID.Length == 0)
        {
            return config;
        }

        ModelChannel? channel = await _repository.SystemChannelAsync(channelID, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            throw new InvalidOperationException("系统渠道不存在或已停用");
        }
        string modelKey = config.ChannelModelKey.Trim();
        string requestedModel = config.Model.Trim();
        if (modelKey.StartsWith("models/", StringComparison.Ordinal))
        {
            modelKey = modelKey["models/".Length..].Trim();
        }
        if (requestedModel.StartsWith("models/", StringComparison.Ordinal))
        {
            requestedModel = requestedModel["models/".Length..].Trim();
        }
        if (modelKey.Length == 0)
        {
            modelKey = requestedModel;
        }
        if (modelKey.Length == 0)
        {
            throw new InvalidOperationException("系统渠道未配置可用模型");
        }

        ChannelModel? channelModel = await _repository
            .ChannelModelByKeyIncludingDisabledAsync(channel.ID, modelKey, cancellationToken)
            .ConfigureAwait(false);
        if (channelModel is null)
        {
            // ModelsJSON 只是旧目录缓存；SKU 合并后唯一授权来源是已启用的 channel_models 记录。
            throw new InvalidOperationException("当前系统渠道未授权该模型");
        }
        if (channelModel.Protocol.Length == 0)
        {
            throw new InvalidOperationException("当前模型尚未配置请求协议");
        }

        string providerModelKey = config.ProviderModelKey.Trim();
        if (config.PriceTierID.Length > 0)
        {
            ChannelModelPriceTier? tier = (channelModel.PriceTiers ?? [])
                .FirstOrDefault(item => item.ID == config.PriceTierID && item.Enabled && item.PriceConfigured);
            if (tier is null)
            {
                throw new InvalidOperationException("当前模型规格价格档已更新，请重新创建任务");
            }
            providerModelKey = providerModelKey.Length > 0 ? providerModelKey : tier.ProviderModelKey;
        }
        else if (requestedModel.Length > 0 && modelKey != requestedModel)
        {
            throw new InvalidOperationException("系统渠道模型标识不一致");
        }

        string protocol = ChannelModelProtocolNormalization.Normalize(
            channel.APIFormat,
            channelModel.Capability,
            channelModel.Protocol);
        config.ChannelID = channel.ID;
        config.InterfaceType = protocol;
        config.APIFormat = protocol is ChannelInterfaceType.ChannelInterfaceGeminiVeo
            or ChannelInterfaceType.ChannelInterfaceGeminiImage ? "gemini"
            : protocol == ChannelInterfaceType.ChannelInterfaceClaudeAPI ? "claude"
            : channel.APIFormat;
        config.BaseURL = channel.BaseURL;
        config.APIKey = channel.APIKey;
        config.SecretKey = channel.SecretKey;
        config.Headers = OutboundGuard.ParseOutboundHeadersJson(channel.HeadersJSON);
        config.ChannelModelKey = modelKey;
        config.ProviderModelKey = providerModelKey;
        config.Model = FirstNonEmpty(providerModelKey, channelModel.ProviderModelKey, modelKey);
        return config;
    }

    private static string FirstNonEmpty(string first, string second, string third)
    {
        if (first.Trim().Length > 0) return first;
        if (second.Trim().Length > 0) return second;
        return third;
    }

    /// <summary>
    /// 执行端解析视频能力声明（Go: validateResolvedVideoCapability 的最小移植）。
    /// Admission 创建时已按能力合同校验参数，执行端只需让归一化函数拿到同一份 profile；
    /// 渠道模型缺失、能力配置为空或损坏时保持历史行为（返回 null，参数原样透传），
    /// 不让没有能力配置的模型在执行端开始报错。
    /// </summary>
    private async Task<OpenAICanvas.Providers.VideoCapabilityConfig?> ResolveVideoCapabilityAsync(
        ProviderConfig config, CancellationToken cancellationToken)
    {
        string channelID = config.ChannelID.Trim();
        if (channelID.Length == 0)
        {
            return null;
        }
        string modelKey = config.ChannelModelKey.Trim();
        if (modelKey.StartsWith("models/", StringComparison.Ordinal))
        {
            modelKey = modelKey["models/".Length..].Trim();
        }
        if (modelKey.Length == 0)
        {
            return null;
        }
        ChannelModel? channelModel = await _repository
            .ChannelModelByKeyIncludingDisabledAsync(channelID, modelKey, cancellationToken)
            .ConfigureAwait(false);
        if (channelModel is null)
        {
            return null;
        }
        ModelCapabilityConfig? capabilityConfig;
        try
        {
            capabilityConfig = ChannelModelCapability.NormalizedChannelModelCapability(channelModel);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        return ChannelModelCapability.MirrorVideoConfig(capabilityConfig);
    }

    // ------------------------------------------------------------- 输入解密

    /// <summary>解密任务输入里的密钥字段。对应 Go: <c>decryptTaskInputJSON</c>；与 TaskLifecycleService 保持一致。</summary>
    private static string DecryptTaskInputJSON(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !raw.Contains(SettingsCrypto.EncryptedPrefix, StringComparison.Ordinal))
        {
            return raw;
        }
        JsonElement input;
        try
        {
            input = JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch (JsonException)
        {
            return raw;
        }
        return JsonSerializer.Serialize(DecryptSecretsElement(input), ProjectCharacterService.GoPayloadOptions);
    }

    private static JsonElement DecryptSecretsElement(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (TaskCreationService.IsTaskSecretFieldPublic(property.Name)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    result[property.Name] = JsonSerializer.SerializeToElement(
                        SettingsCrypto.DecryptSecret(property.Value.GetString() ?? "", "data"));
                    continue;
                }
                result[property.Name] = property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? DecryptSecretsElement(property.Value)
                    : property.Value.Clone();
            }
            return JsonSerializer.SerializeToElement(result);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            List<JsonElement> items = value.EnumerateArray()
                .Select(item => item.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? DecryptSecretsElement(item)
                    : item.Clone())
                .ToList();
            return JsonSerializer.SerializeToElement(items);
        }
        return value.Clone();
    }

    // ------------------------------------------------------------- 超时与落库

    /// <summary>任务类型对应的执行超时。对应 Go: <c>taskExecutionTimeoutWithPolicy</c>。</summary>
    private TimeSpan TaskExecutionTimeout(string taskType)
    {
        RuntimeTaskPolicy task = _policy.Current().Task;
        if (taskType.StartsWith("canvas_video", StringComparison.Ordinal)
            || taskType.StartsWith("video_", StringComparison.Ordinal))
        {
            return TimeSpan.FromMinutes(Math.Max(task.VideoTimeoutMinutes, 5));
        }
        if (taskType.StartsWith("canvas_image", StringComparison.Ordinal))
        {
            return TimeSpan.FromMinutes(task.ImageTimeoutMinutes);
        }
        if (taskType.StartsWith("canvas_audio", StringComparison.Ordinal))
        {
            return TimeSpan.FromMinutes(task.AudioTimeoutMinutes);
        }
        if (taskType == "timeline_transcription")
        {
            return TimeSpan.FromMinutes(20);
        }
        if (taskType == "timeline_render")
        {
            return TimeSpan.FromMinutes(60);
        }
        if (taskType.StartsWith("canvas_text", StringComparison.Ordinal))
        {
            return TimeSpan.FromMinutes(task.TextTimeoutMinutes);
        }
        return TimeSpan.FromMinutes(task.DefaultTimeoutMinutes);
    }

    /// <summary>图片/视频进度由上游状态响应回报。对应 Go: <c>taskUsesUpstreamReportedProgress</c>。</summary>
    private static bool TaskUsesUpstreamReportedProgress(string taskType) =>
        taskType == "canvas_image" || taskType == "canvas_video" || taskType.StartsWith("video_", StringComparison.Ordinal);

    /// <summary>超时文案。对应 Go: <c>taskTimeoutMessage</c>。</summary>
    private static string TaskTimeoutMessage(string taskType) =>
        taskType.StartsWith("canvas_video", StringComparison.Ordinal)
            || taskType.StartsWith("video_", StringComparison.Ordinal)
            ? "视频生成等待超时，请稍后到任务中心查看或重试。"
            : taskType.StartsWith("canvas_image", StringComparison.Ordinal)
                ? "图片生成等待超时，请稍后重试。"
                : "任务执行超时，请稍后重试。";

    /// <summary>
    /// 存储配额核算 + 原子写任务完成态。对应 Go: <c>saveTaskCompletionWithinStorageQuota</c>。
    /// 结构化画布配额通道（structuredDelta）当前恒为 0，待画布操作落库（阶段 7）接入。
    /// </summary>
    private async Task SaveCompletionWithinQuotaAsync(
        TaskEntity current, string resultJSON, CancellationToken cancellationToken)
    {
        RuntimeResourcePolicy resource = _policy.Current().Resource;
        UserStorageUsage usage = await _repository
            .UserStorageUsageAsync(current.UserID, cancellationToken).ConfigureAwait(false);
        string publicInputJSON = TaskOutput.PublicTaskInputJSON(current.InputJSON);
        long taskDelta = Math.Max(
            0, resultJSON.Length + publicInputJSON.Length - current.ResultJSON.Length - current.InputJSON.Length);
        long limitBytes = resource.TaskDataGB * (1L << 30);
        if (usage.TaskBytes + taskDelta > limitBytes)
        {
            throw AppError.QuotaExceeded($"账号任务历史数据已达到 {resource.TaskDataGB}GB 上限，请联系管理员归档");
        }

        TaskEntity completed = CloneTask(current);
        completed.Status = TaskStatus.TaskStatusSucceeded;
        completed.Stage = "任务完成";
        completed.Progress = 100;
        completed.ResultJSON = resultJSON;
        completed.InputJSON = publicInputJSON;
        completed.CompletedAt = DateTime.UtcNow;
        string expectedStatus = current.Status;
        await _repository.SaveTaskCompletionAsync(completed, expectedStatus, [], cancellationToken).ConfigureAwait(false);
        CopyTaskInto(completed, current);
    }

    private static TaskEntity CloneTask(TaskEntity source) => new()
    {
        CreationSubmissionID = source.CreationSubmissionID,
        ID = source.ID,
        UserID = source.UserID,
        ProjectID = source.ProjectID,
        Type = source.Type,
        Status = source.Status,
        Stage = source.Stage,
        Progress = source.Progress,
        Prompt = source.Prompt,
        Operation = source.Operation,
        Provider = source.Provider,
        Model = source.Model,
        BillingOrderID = source.BillingOrderID,
        ProviderRequestID = source.ProviderRequestID,
        PollStage = source.PollStage,
        NextPollAt = source.NextPollAt,
        LeaseOwner = source.LeaseOwner,
        LeaseExpiresAt = source.LeaseExpiresAt,
        InputJSON = source.InputJSON,
        ResultJSON = source.ResultJSON,
        TextDraft = source.TextDraft,
        Error = source.Error,
        Attempts = source.Attempts,
        StartedAt = source.StartedAt,
        CompletedAt = source.CompletedAt,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
    };

    private static void CopyTaskInto(TaskEntity source, TaskEntity target)
    {
        target.Status = source.Status;
        target.Stage = source.Stage;
        target.Progress = source.Progress;
        target.ResultJSON = source.ResultJSON;
        target.InputJSON = source.InputJSON;
        target.CompletedAt = source.CompletedAt;
    }
}
