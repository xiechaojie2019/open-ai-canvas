#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAICanvas.Application;

namespace OpenAICanvas.Web.Workers;

/// <summary>
/// 任务调度后台作业：每 2 秒推进一轮任务领取与执行。
/// 对应 Go: <c>taskWorkerCoordinator.start</c> 的 dispatch 循环。
/// </summary>
public sealed class TaskDispatchWorker : BackgroundService
{
    /// <summary>调度间隔。对应 Go: <c>time.NewTicker(2 * time.Second)</c>。</summary>
    private static readonly TimeSpan DispatchInterval = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TaskDispatchWorker> _logger;

    public TaskDispatchWorker(IServiceScopeFactory scopeFactory, ILogger<TaskDispatchWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(DispatchInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await DispatchAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机。
        }
    }

    private async Task DispatchAsync(CancellationToken stoppingToken)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            TaskWorkerService worker = scope.ServiceProvider.GetRequiredService<TaskWorkerService>();
            await worker.DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "任务调度暂停");
        }
    }
}
