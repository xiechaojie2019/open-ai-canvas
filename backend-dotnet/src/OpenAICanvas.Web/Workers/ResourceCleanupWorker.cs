#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAICanvas.Application;

namespace OpenAICanvas.Web.Workers;

/// <summary>
/// 孤儿资源清理后台作业。
/// 对应 Go: <c>app/resource_deletion_worker.go</c> 的 <c>startResourceDeletionWorker</c>。
/// </summary>
/// <remarks>
/// 与 Go 一致：启动时先跑一轮（含 drain 与孤儿清理），随后每 15 秒 drain 一次删除任务，
/// 每满 1 小时才执行一次孤儿资源清理（该操作较重，不宜高频）。
/// Go 侧该 worker 同时负责公告草稿与回收站过期清理，这两项尚未移植，见 PENDING-CONFIRMATIONS.md。
/// </remarks>
public sealed class ResourceCleanupWorker : BackgroundService
{
    /// <summary>drain 间隔。对应 Go: <c>time.NewTicker(15 * time.Second)</c>。</summary>
    private static readonly TimeSpan DrainInterval = TimeSpan.FromSeconds(15);

    /// <summary>孤儿清理周期。对应 Go: <c>time.Since(lastPeriodicCleanup) &gt;= time.Hour</c>。</summary>
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ResourceCleanupWorker> _logger;

    public ResourceCleanupWorker(IServiceScopeFactory scopeFactory, ILogger<ResourceCleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        ResourceDeleteService deletions = scope.ServiceProvider.GetRequiredService<ResourceDeleteService>();
        ResourceCleanupService cleanup = scope.ServiceProvider.GetRequiredService<ResourceCleanupService>();

        // 启动即做一轮：先 drain 积压的删除任务，再清理孤儿资源。
        await RunStartupAsync(deletions, cleanup, stoppingToken).ConfigureAwait(false);

        DateTime lastPeriodicCleanup = DateTime.UtcNow;
        using PeriodicTimer timer = new(DrainInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await deletions.DrainResourceDeletionJobsAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _logger.LogWarning(error, "资源删除任务清理失败");
                }

                if (DateTime.UtcNow - lastPeriodicCleanup < CleanupInterval)
                {
                    continue;
                }
                lastPeriodicCleanup = DateTime.UtcNow;
                await RunPeriodicAsync(cleanup, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机。
        }
    }

    private async Task RunStartupAsync(
        ResourceDeleteService deletions, ResourceCleanupService cleanup, CancellationToken cancellationToken)
    {
        try
        {
            await deletions.DrainResourceDeletionJobsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "资源删除任务启动清理失败");
        }
        await RunPeriodicAsync(cleanup, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunPeriodicAsync(ResourceCleanupService cleanup, CancellationToken cancellationToken)
    {
        try
        {
            (int removed, int jobs) = await cleanup.CleanupDetachedResourcesAsync(
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                _logger.LogInformation("孤儿资源清理：删除 {Removed} 条资源记录，生成 {Jobs} 个物理删除任务",
                    removed, jobs);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "孤儿资源清理失败");
        }
    }
}
