#nullable enable
using System.Text.Json;
using OpenAICanvas.Auth;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence.Repositories;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application;

/// <summary>爱创用户的外部积分冻结、结算与释放协调。</summary>
public sealed class ExternalBillingService
{
    private const string Provider = AuthService.ExternalIdentityProvider;
    private readonly Repository _repository;
    private readonly IExternalBillingClient _client;
    private readonly IExternalCredentialStore _credentials;

    public ExternalBillingService(
        Repository repository,
        IExternalBillingClient client,
        IExternalCredentialStore credentials)
    {
        _repository = repository;
        _client = client;
        _credentials = credentials;
    }

    /// <summary>判断用户是否绑定爱创身份。</summary>
    public async Task<bool> IsExternalUserAsync(string userId, CancellationToken cancellationToken = default) =>
        await _repository.UserHasIdentityAsync(userId, Provider, cancellationToken).ConfigureAwait(false);

    /// <summary>读取爱创用户余额。</summary>
    public Task<ExternalBalance> GetBalanceAsync(string userId, CancellationToken cancellationToken = default) =>
        _client.GetBalanceAsync(userId, cancellationToken);

    /// <summary>冻结爱创积分并缓存外部冻结单号。</summary>
    public async Task HoldAsync(
        BillingOrder order,
        TaskEntity task,
        CancellationToken cancellationToken = default)
    {
        if (order.AmountMicrocredits <= 0)
        {
            throw new InvalidOperationException("计费报价必须大于 0");
        }
        ExternalHold hold = await _client.HoldAsync(
            order.UserID,
            order.ID,
            order.AmountMicrocredits,
            task.ID,
            task.Prompt,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(hold.Status, "held", StringComparison.OrdinalIgnoreCase)
            || hold.HoldId.Length == 0
            || !string.Equals(hold.OrderId, order.ID, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("爱创积分冻结结果无效");
        }

        DateTimeOffset expiresAt = hold.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(90);
        TimeSpan ttl = expiresAt - DateTimeOffset.UtcNow + TimeSpan.FromHours(24);
        if (ttl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("爱创积分冻结已过期");
        }
        try
        {
            await _credentials.SetAsync(
                HoldCacheKey(order.ID),
                JsonSerializer.Serialize(new ExternalHoldCache(
                    hold.HoldId, order.ID, order.UserID, hold.AmountMicrocredits,
                    DateTimeOffset.UtcNow, expiresAt)),
                ttl,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _client.ReleaseAsync(
                    order.UserID, order.ID, hold.HoldId, "本站冻结单缓存失败", cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 外部冻结已成功但缓存和释放均失败，调用方会记录人工核对状态
            }
            throw;
        }
    }

    /// <summary>确认爱创积分，并只更新本站账单状态。</summary>
    public async Task SettleAsync(string orderId, CancellationToken cancellationToken = default)
    {
        BillingOrder order = await _repository.BillingOrderAsync(orderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("计费订单不存在");
        if (order.Status == BillingStatus.BillingStatusSettled)
        {
            return;
        }
        ExternalHoldCache hold = await ReadHoldAsync(order, cancellationToken).ConfigureAwait(false);
        long actual = await _repository.ExternalBillingAmountAsync(order.ID, cancellationToken).ConfigureAwait(false);
        if (actual <= 0)
        {
            throw new InvalidOperationException("实际计费金额必须大于 0");
        }
        ExternalBillingResult result = await _client.ConfirmAsync(
            order.UserID, order.ID, hold.HoldId, actual, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.Status, "confirmed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("爱创积分确认结果无效");
        }
        await _repository.CompleteExternalBillingOrderAsync(
            order.ID, BillingStatus.BillingStatusSettled, actual, result.DebtMicrocredits,
            cancellationToken).ConfigureAwait(false);
        await _credentials.DeleteAsync(HoldCacheKey(order.ID), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>释放爱创积分，并只更新本站账单状态。</summary>
    public async Task ReleaseAsync(
        string orderId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        BillingOrder order = await _repository.BillingOrderAsync(orderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("计费订单不存在");
        if (order.Status == BillingStatus.BillingStatusRefunded)
        {
            return;
        }
        ExternalHoldCache hold = await ReadHoldAsync(order, cancellationToken).ConfigureAwait(false);
        ExternalBillingResult result = await _client.ReleaseAsync(
            order.UserID, order.ID, hold.HoldId, Truncate(reason, 200), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.Status, "released", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("爱创积分释放结果无效");
        }
        await _repository.CompleteExternalBillingOrderAsync(
            order.ID, BillingStatus.BillingStatusRefunded, 0, 0, cancellationToken).ConfigureAwait(false);
        await _credentials.DeleteAsync(HoldCacheKey(order.ID), cancellationToken).ConfigureAwait(false);
    }

    public static string HoldCacheKey(string orderId) => Provider + ":billing-hold:" + orderId;

    /// <summary>读取缓存中的外部冻结单号，供本地事务失败补偿使用。</summary>
    public async Task<string?> ReadHoldIdAsync(string orderId, CancellationToken cancellationToken = default)
    {
        string? value = await _credentials.GetAsync(HoldCacheKey(orderId), cancellationToken).ConfigureAwait(false);
        return value is null ? null : JsonSerializer.Deserialize<ExternalHoldCache>(value)?.HoldId;
    }

    /// <summary>本地事务失败时，使用已返回的冻结单号释放外部积分。</summary>
    public async Task ReleaseHeldAsync(
        string userId, string orderId, string holdId, string reason,
        CancellationToken cancellationToken = default)
    {
        ExternalBillingResult result = await _client.ReleaseAsync(
            userId, orderId, holdId, Truncate(reason, 200), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.Status, "released", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("爱创积分释放结果无效");
        }
        await _credentials.DeleteAsync(HoldCacheKey(orderId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExternalHoldCache> ReadHoldAsync(BillingOrder order, CancellationToken cancellationToken)
    {
        string? value = await _credentials.GetAsync(HoldCacheKey(order.ID), cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            throw new InvalidOperationException("爱创冻结单号不存在，账单需要人工核对");
        }
        ExternalHoldCache cache = JsonSerializer.Deserialize<ExternalHoldCache>(value)
            ?? throw new InvalidOperationException("爱创冻结缓存无效，账单需要人工核对");
        if (!string.Equals(cache.OrderId, order.ID, StringComparison.Ordinal)
            || !string.Equals(cache.UserId, order.UserID, StringComparison.Ordinal)
            || cache.AmountMicrocredits != order.AmountMicrocredits)
        {
            throw new InvalidOperationException("爱创冻结缓存与本站账单不匹配，账单需要人工核对");
        }
        return cache;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private sealed record ExternalHoldCache(
        string HoldId,
        string OrderId,
        string UserId,
        long AmountMicrocredits,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);
}
