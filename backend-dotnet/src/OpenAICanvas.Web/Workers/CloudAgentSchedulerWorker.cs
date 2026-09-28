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
                await runtime.AdvanceAsync(run, cancellationToken).ConfigureAwait(false);
            }
            catch (AppError error) when (error.Status == 409)
            {
                // 并发推进：跳过，等下一轮。
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "agent transition {RunId}", run.ID);
            }
        }
    }
}
