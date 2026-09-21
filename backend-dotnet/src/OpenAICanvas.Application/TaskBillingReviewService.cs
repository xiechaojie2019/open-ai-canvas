#nullable enable
using Microsoft.Extensions.Logging;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>
/// 账单巡检：只读统计长期未闭合的计费订单，提醒运维进入人工核对链路。
/// 对应 Go: <c>app/billing_review.go</c>。
/// 资金状态必须由人工依据上游证据结算或退款，巡检不能代替资金动作。
/// </summary>
public sealed class TaskBillingReviewService
{
    /// <summary>订单多旧算“待关注”。对应 Go: <c>billingReviewStaleAfter</c>。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    private readonly Repository _repository;
    private readonly ILogger<TaskBillingReviewService>? _logger;

    public TaskBillingReviewService(Repository repository, ILogger<TaskBillingReviewService>? logger = null)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>执行一次只读巡检。对应 Go: <c>AuditBillingReview</c>。</summary>
    public async Task AuditAsync(CancellationToken cancellationToken = default)
    {
        Repository.BillingReviewStats stats = await _repository
            .StaleBillingReviewStatsAsync(DateTime.UtcNow, StaleAfter, cancellationToken)
            .ConfigureAwait(false);
        if (stats.Total == 0)
        {
            return;
        }
        _logger?.LogWarning(
            "billing review audit stale_orders={Total} reserved={Reserved} running={Running} uncertain={Uncertain} oldest={Oldest:O}",
            stats.Total, stats.Reserved, stats.Running, stats.Uncertain, stats.Oldest);
    }
}
