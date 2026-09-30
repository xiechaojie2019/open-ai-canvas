#nullable enable
using System.Collections.Concurrent;
using System.Text.Json;
using OpenAICanvas.Application.CloudAgent;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Providers;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application;

/// <summary>上游拒绝分类码。对应 Go: <c>routeFailureCode</c> / <c>safeRouteRejection</c>。</summary>
public static class RouteFailure
{
    /// <summary>上游 401/403/404/429 或渠道槽位失败属于“安全拒绝”：上游未创建任务，可切换线路。</summary>
    public static bool IsSafeRejection(Exception? error)
    {
        if (error is null)
        {
            return false;
        }
        if (error is ProviderChannelSlotException)
        {
            return true;
        }
        if (error is ProviderHttpException http)
        {
            return http.StatusCode is 401 or 403 or 404 or 429;
        }
        return false;
    }

    public static string FailureCode(Exception? error)
    {
        if (error is ProviderChannelSlotException slot)
        {
            return slot.Code;
        }
        if (error is ProviderHttpException http)
        {
            return "upstream_" + http.StatusCode;
        }
        return "submission_unknown";
    }
}

/// <summary>本地路由健康阻断（Redis 分散式阻断待协调器接口补齐后接入）。</summary>
public sealed class RouteHealthBlocker
{
    private readonly ConcurrentDictionary<string, DateTime> _blocked = new(StringComparer.Ordinal);

    public void Block(string channelID, string channelModelID, string routeID, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }
        DateTime until = DateTime.UtcNow.Add(duration);
        _blocked["channel:" + channelID] = until;
        _blocked["channel-model:" + channelModelID] = until;
        _blocked["route:" + routeID] = until;
    }

    /// <summary>上游 401/403 封 10 分钟渠道；404 封 10 分钟渠道模型；429 封 30 秒渠道（RetryAfter 可延长）。</summary>
    public void BlockForFailure(RouteAttempt attempt, Exception? error)
    {
        string key = "";
        TimeSpan duration = TimeSpan.Zero;
        switch (attempt.FailureCode)
        {
            case "upstream_401" or "upstream_403":
                key = "channel:" + attempt.ChannelID;
                duration = TimeSpan.FromMinutes(10);
                break;
            case "upstream_404":
                key = "channel-model:" + attempt.ChannelModelID;
                duration = TimeSpan.FromMinutes(10);
                break;
            case "upstream_429":
                key = "channel:" + attempt.ChannelID;
                duration = TimeSpan.FromSeconds(30);
                if (error is ProviderHttpException { RetryAfter: var retry } && retry > TimeSpan.Zero)
                {
                    duration = retry;
                }
                break;
        }
        if (key.Length == 0 || duration <= TimeSpan.Zero)
        {
            return;
        }
        Block(attempt.ChannelID, attempt.ChannelModelID, attempt.RouteID, duration);
    }

    /// <summary>线路被健康阻断则跳过（过期项顺手清理）。对应 Go: <c>logicalRouteBlocked</c> 的本地部分。</summary>
    public bool IsBlocked(string channelID, string channelModelID, string routeID)
    {
        DateTime now = DateTime.UtcNow;
        foreach (string key in new[]
                 {
                     "channel:" + channelID,
                     "channel-model:" + channelModelID,
                     "route:" + routeID,
                 })
        {
            if (_blocked.TryGetValue(key, out DateTime until))
            {
                if (until > now)
                {
                    return true;
                }
                _blocked.TryRemove(key, out _);
            }
        }
        return false;
    }
}

/// <summary>
/// 一次任务执行中的路由提交与失败切换编排。
/// 对应 Go: <c>taskRouteExecutor</c> + <c>beginTaskRouteAttempt</c> / <c>finishTaskRouteAttempt</c> /
/// <c>nextRouteAttemptAfterFailure</c> 的路由状态机部分。
/// </summary>
public sealed class RouteAttemptOrchestrator
{
    private readonly Repository _repository;
    private readonly LogicalModelService _logicalModels;

    public RouteAttemptOrchestrator(Repository repository, LogicalModelService logicalModels)
    {
        _repository = repository;
        _logicalModels = logicalModels;
    }

    /// <summary>开始一次路由运行：复用未发送尝试；安全拒绝触发切换；否则新选。</summary>
    public async Task<RouteAttempt?> BeginTaskRouteAttemptAsync(
        TaskEntity task, CancellationToken cancellationToken = default)
    {
        if (task.LogicalModelID.Length == 0)
        {
            // 直连任务：记录一次尝试以保持提交状态机完整（旧任务无提交记录时拒绝自动重发）。
            if (task.Attempts > 1 && task.ProviderRequestID.Length == 0)
            {
                throw AppError.BadAuthRequest("旧任务已尝试执行但缺少提交记录，为避免重复扣费已停止自动重发");
            }
            RouteAttempt direct = DirectAttempt(task);
            if (task.ProviderRequestID.Length > 0)
            {
                direct.ProviderRequestID = task.ProviderRequestID;
                direct.DispatchState = "accepted";
            }
            await _repository.CreateRouteAttemptAsync(direct, cancellationToken).ConfigureAwait(false);
            return direct;
        }
        IReadOnlyList<RouteAttempt> attempts = await _repository.RouteAttemptsAsync(
            task.ID, task.RouteRun, cancellationToken).ConfigureAwait(false);
        if (attempts.Count > 0)
        {
            RouteAttempt existing = attempts[^1];
            switch (existing.DispatchState)
            {
                case "not_sent":
                    return existing;
                case "accepted" when existing.ProviderRequestID.Length > 0 || task.ProviderRequestID.Length > 0:
                    if (task.ProviderRequestID.Length == 0)
                    {
                        task.ProviderRequestID = existing.ProviderRequestID;
                    }
                    return existing;
                case "accepted":
                    throw AppError.New(409, "上游已接受请求，但没有可恢复的任务 ID");
                case "submission_unknown" when existing.ProviderRequestID.Length > 0 || task.ProviderRequestID.Length > 0:
                    if (task.ProviderRequestID.Length == 0)
                    {
                        task.ProviderRequestID = existing.ProviderRequestID;
                        existing.DispatchState = "accepted";
                        await _repository.SaveRouteAttemptAsync(existing, cancellationToken).ConfigureAwait(false);
                    }
                    return existing;
                case "submission_unknown":
                    throw AppError.New(409, "上一次提交结果不明确，为避免重复扣费已停止自动重发");
                case "rejected_no_job":
                    return await SwitchToNextRouteAsync(task, attempts, cancellationToken).ConfigureAwait(false);
            }
        }
        // 首次选型失败时也尝试切换到备用线路。
        try
        {
            RoutedModel routed = await _logicalModels.ResolveLogicalModelAsync(
                task.LogicalModelID,
                TaskCreationService.ModelRequestIntentFromTaskInput(
                    ParseInput(task.InputJSON), task.Type, task.Operation),
                cancellationToken).ConfigureAwait(false);
            return await CreateAttemptAsync(task, routed, attempts.Count + 1, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AppError)
        {
            return await SwitchToNextRouteAsync(task, attempts, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>CAS 独占分发确认：陈旧 Worker 不得二次分发同一尝试。</summary>
    public async Task MarkDispatchingAsync(RouteAttempt attempt, CancellationToken cancellationToken = default)
    {
        if (attempt.DispatchState != "not_sent")
        {
            return;
        }
        await _repository.MarkRouteAttemptDispatchingAsync(attempt.ID, cancellationToken).ConfigureAwait(false);
        attempt.Status = "dispatching";
        attempt.DispatchState = "submission_unknown";
    }

    /// <summary>尝试终态：成功/失败分类并落库。对应 Go: <c>finishTaskRouteAttempt</c>。</summary>
    public async Task FinishAttemptAsync(
        RouteAttempt attempt, TaskEntity task, Exception? taskError,
        CancellationToken cancellationToken = default)
    {
        attempt.CompletedAt = DateTime.UtcNow;
        attempt.ProviderRequestID = task.ProviderRequestID;
        if (taskError is null)
        {
            attempt.Status = "succeeded";
            attempt.DispatchState = "accepted";
        }
        else
        {
            attempt.Status = "failed";
            attempt.FailureMessage = CloudAgentContracts.TruncateRunes(taskError.Message, 1000);
            attempt.FailureCode = RouteFailure.FailureCode(taskError);
            if (attempt.ProviderRequestID.Length > 0)
            {
                attempt.DispatchState = "accepted";
            }
            else if (RouteFailure.IsSafeRejection(taskError))
            {
                attempt.DispatchState = "rejected_no_job";
            }
            else
            {
                attempt.DispatchState = "submission_unknown";
            }
        }
        await _repository.SaveRouteAttemptAsync(attempt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 上游安全拒绝后的备用线路切换：阻断失败线路 → 排除已尝试路由重选 →
    /// 创建替换订单重算成本 → 事务写回。对应 Go: <c>nextRouteAttemptAfterFailure</c> +
    /// <c>switchTaskToNextRoute</c> 的编排；返回 null 表示无可切换线路。
    /// </summary>
    public async Task<RouteAttempt?> NextAttemptAfterFailureAsync(
        TaskEntity task, RouteAttempt attempt, Exception? taskError,
        CancellationToken cancellationToken = default)
    {
        if (task.LogicalModelID.Length == 0 || attempt.DispatchState != "rejected_no_job")
        {
            return null;
        }
        HealthBlocker.BlockForFailure(attempt, taskError);
        IReadOnlyList<RouteAttempt> attempts = await _repository.RouteAttemptsAsync(
            task.ID, task.RouteRun, cancellationToken).ConfigureAwait(false);
        return await SwitchToNextRouteAsync(task, attempts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>共享健康阻断器（进程内；跨实例阻断待协调器接口）。</summary>
    public static RouteHealthBlocker HealthBlocker { get; } = new();

    /// <summary>共享编排入口：编排器为无状态组合，单例持有阻断器。</summary>
    public static RouteAttemptOrchestrator Shared(Repository repository, LogicalModelService logicalModels) =>
        new(repository, logicalModels);

    private async Task<RouteAttempt> SwitchToNextRouteAsync(
        TaskEntity task, IReadOnlyList<RouteAttempt> attempts, CancellationToken cancellationToken)
    {
        HashSet<string> tried = new(attempts.Select(item => item.RouteID), StringComparer.Ordinal);
        if (task.RouteID.Length > 0)
        {
            tried.Add(task.RouteID);
        }
        LogicalModelService.TaskRouteSwitchResult result = await _logicalModels.SwitchTaskToNextRouteAsync(
            task, tried, cancellationToken).ConfigureAwait(false);
        RouteAttempt attempt = new()
        {
            ID = await _repository.NextPrefixedIdAsync("ATTEMPT", cancellationToken).ConfigureAwait(false),
            TaskID = task.ID,
            RouteRun = task.RouteRun,
            AttemptNumber = attempts.Count + 1,
            LogicalModelID = result.Routed.LogicalModel.ID,
            LogicalModelRevisionID = result.Routed.Revision.ID,
            RouteID = result.Routed.Route.ID,
            ChannelModelID = result.Routed.ChannelModel.ID,
            ChannelID = result.Routed.ChannelModel.ChannelID,
            Status = "selected",
            DispatchState = "not_sent",
            StartedAt = DateTime.UtcNow,
        };
        await _repository.CreateRouteAttemptAsync(attempt, cancellationToken).ConfigureAwait(false);
        return attempt;
    }

    private static RouteAttempt DirectAttempt(TaskEntity task) => new()
    {
        ID = CloudAgentContracts.AgentID(task.UserID, "ATTEMPT:" + task.ID + ":" + task.RouteRun + ":1"),
        TaskID = task.ID,
        RouteRun = task.RouteRun,
        AttemptNumber = 1,
        ChannelModelID = task.ChannelModelID,
        Status = "selected",
        DispatchState = "not_sent",
        StartedAt = DateTime.UtcNow,
    };

    private static async Task<RouteAttempt> CreateAttemptAsync(
        TaskEntity task, RoutedModel routed, long attemptNumber, CancellationToken cancellationToken)
    {
        RouteAttempt attempt = new()
        {
            ID = await TaskRouteIdAsync(task, cancellationToken).ConfigureAwait(false),
            TaskID = task.ID,
            RouteRun = task.RouteRun,
            AttemptNumber = attemptNumber,
            LogicalModelID = routed.LogicalModel.ID,
            LogicalModelRevisionID = routed.Revision.ID,
            RouteID = routed.Route.ID,
            ChannelModelID = routed.ChannelModel.ID,
            ChannelID = routed.ChannelModel.ChannelID,
            Status = "selected",
            DispatchState = "not_sent",
            StartedAt = DateTime.UtcNow,
        };
        return attempt;
    }

    private static async Task<string> TaskRouteIdAsync(TaskEntity task, CancellationToken cancellationToken)
    {
        // ID 由仓储层生成（ATTEMPT 前缀序列），这里先落一个占位再由仓储覆盖不现实；
        // 直接复用 CloudAgent 的确定性 ID 生成（用户 + 任务 + 轮次 + 序号）。
        return await Task.FromResult(CloudAgentContracts.AgentID(
            task.UserID, $"ATTEMPT:{task.ID}:{task.RouteRun}:{Guid.NewGuid():N}"));
    }

    private static Dictionary<string, JsonElement> ParseInput(string inputJSON)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                inputJSON, ProjectCharacterService.GoPayloadOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
