#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAICanvas.Application;

namespace OpenAICanvas.Web.Workers;

/// <summary>
/// 账单巡检后台作业：每小时跑一次只读审计。对应 Go: <c>startBillingReviewAudit</c>。
/// </summary>
public sealed class BillingReviewWorker : BackgroundService
{
    /// <summary>巡检周期。对应 Go: <c>time.NewTicker(time.Hour)</c>。</summary>
    private static readonly TimeSpan AuditInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BillingReviewWorker> _logger;

    public BillingReviewWorker(IServiceScopeFactory scopeFactory, ILogger<BillingReviewWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 与 Go 一致：先立即跑一次，之后每小时一次。
        await AuditOnceAsync(stoppingToken).ConfigureAwait(false);
        using PeriodicTimer timer = new(AuditInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await AuditOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机。
        }
    }

    private async Task AuditOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            TaskBillingReviewService review = scope.ServiceProvider.GetRequiredService<TaskBillingReviewService>();
            await review.AuditAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "billing review audit failed");
        }
    }
}
