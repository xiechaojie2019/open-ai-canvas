#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAICanvas.Application.CloudAgent;

namespace OpenAICanvas.Web.Workers;

/// <summary>
/// 个人记忆定时压缩调度。对应 Go: <c>startAgentMemoryCompactScheduler</c>。
/// 立即跑一跳，之后每 5 分钟一次，每跳最多启动 3 个。
/// </summary>
public sealed class AgentMemoryCompactWorker : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentMemoryCompactWorker> _logger;

    public AgentMemoryCompactWorker(IServiceScopeFactory scopeFactory, ILogger<AgentMemoryCompactWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
        using PeriodicTimer timer = new(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机。
        }
    }

    private async Task DispatchOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            AgentMemoryCompactService compact = scope.ServiceProvider
                .GetRequiredService<AgentMemoryCompactService>();
            await compact.DispatchDueAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "agent memory compact dispatch failed");
        }
    }
}
