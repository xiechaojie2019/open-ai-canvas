#nullable enable
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using System.Text.Json;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Web.Workers;

/// <summary>
/// 云 Agent 调度器：推进待处理运行并补建历史根任务的执行行。
/// 每次推进一个有界状态转移；跨实例由持久修订号 CAS 协调，无需进程内互斥。
/// 对应 Go: <c>app.advanceCloudAgents</c>（worker 循环内调用）。
/// </summary>
public sealed class CloudAgentSchedulerWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<CloudAgentSchedulerWorker> logger) : BackgroundService
{
    private string _cursor = "";

    /// <summary>
    /// 每个运行连续 CAS 冲突的次数。409 属于"并发推进，跳过等下一轮"，本身不写日志；
    /// 但一直 409 说明这个运行很可能卡住了，超过阈值要留一条诊断痕迹。
    /// 对应 Go: <c>Service.agentConflictStreak</c>。
    /// </summary>
    private readonly Dictionary<string, int> _conflictStreak = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 与 worker 启动节奏一致：先等数据库/依赖就绪再进入循环。
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AdvanceOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "agent scheduler tick failed");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task AdvanceOnceAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        Application.CloudAgent.CloudAgentRuntimeService runtime =
            scope.ServiceProvider.GetRequiredService<Application.CloudAgent.CloudAgentRuntimeService>();

        // 恢复：还没有执行行的历史根任务先补建执行行。
        IReadOnlyList<TaskEntity> roots = await repository.CloudAgentRootsAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (TaskEntity task in roots)
        {
            try
            {
                await runtime.EnsureRootAsync(task, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is AppError or InvalidOperationException or JsonException)
            {
                logger.LogWarning(error, "agent recovery {TaskId}", task.ID);
            }
        }

        // 稳定 keyset：等待中的行让位给后面的运行，不改变业务时间戳。
        IReadOnlyList<CloudAgentExecution> runs = await repository.ActiveCloudAgentsAfterAsync(
            _cursor, 50, cancellationToken).ConfigureAwait(false);
        if (runs.Count == 0 && _cursor.Length > 0)
        {
            _cursor = "";
            runs = await repository.ActiveCloudAgentsAfterAsync(_cursor, 50, cancellationToken)
                .ConfigureAwait(false);
        }
        foreach (CloudAgentExecution run in runs)
        {
            _cursor = run.ID;
            try
            {
                // 兜底先于推进：长时间没有任何进展的运行直接收尾，避免它每 2 秒空转一次。
                double? stuckMinutes = await runtime
                    .TryTerminateStuckCloudAgentAsync(run, cancellationToken).ConfigureAwait(false);
                if (stuckMinutes is double minutes)
                {
                    logger.LogWarning(
                        "agent scheduler: run={RunId} 判定卡死并收尾（最后一条事件在 {Minutes:0} 分钟前）",
                        run.ID, minutes);
                    _conflictStreak.Remove(run.ID);
                    continue;
                }
                await runtime.AdvanceAsync(run, cancellationToken).ConfigureAwait(false);
                _conflictStreak.Remove(run.ID);
            }
            catch (AppError error) when (error.Status == 409)
            {
                // 并发推进：跳过，等下一轮。持续冲突要留痕，否则卡死的运行完全没有可观测性。
                int streak = _conflictStreak.GetValueOrDefault(run.ID) + 1;
                _conflictStreak[run.ID] = streak;
                if (streak == Application.CloudAgent.CloudAgentRuntimeService.ConflictLogThreshold)
                {
                    logger.LogWarning(
                        "agent scheduler: run={RunId} 连续 {Streak} 次 CAS 冲突（另有写者持续占用该行），这一轮很可能卡住了",
                        run.ID, streak);
                }
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "agent transition {RunId}", run.ID);
            }
        }
    }
}
