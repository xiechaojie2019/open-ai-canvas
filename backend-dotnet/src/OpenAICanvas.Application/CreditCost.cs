#nullable enable
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Application;

/// <summary>管理员积分成本价。对应 Go: <c>model.CreditCostPricing</c>（JSON 契约一致）。</summary>
public sealed class CreditCostPricingDto
{
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("unitPriceMicrocredits")]
    public long UnitPriceMicrocredits { get; set; }

    [JsonPropertyName("inputTokenPriceMicrocredits")]
    public long InputTokenPriceMicrocredits { get; set; }

    [JsonPropertyName("outputTokenPriceMicrocredits")]
    public long OutputTokenPriceMicrocredits { get; set; }

    [JsonPropertyName("cachedTokenPriceMicrocredits")]
    public long CachedTokenPriceMicrocredits { get; set; }

    /// <summary>对应 Go: <c>model.ChannelModelPriceTier.CostPricing</c> 的行字段投影。</summary>
    public static CreditCostPricingDto Of(ChannelModelPriceTier tier) => new()
    {
        Configured = tier.CostConfigured,
        UnitPriceMicrocredits = tier.CostUnitPriceMicrocredits,
        InputTokenPriceMicrocredits = tier.CostInputTokenPriceMicrocredits,
        OutputTokenPriceMicrocredits = tier.CostOutputTokenPriceMicrocredits,
        CachedTokenPriceMicrocredits = tier.CostCachedTokenPriceMicrocredits,
    };
}

/// <summary>
/// 管理员积分成本：校验、下单快照与成本金额。
/// 对应 Go: <c>app/credit_cost.go</c> 与 <c>price_validation.go</c> 的 <c>validateTokenPrices</c>。
/// 成本使用下单快照和真实用量，不套用销售倍率或用户扣费上限；
/// 未结算、未配置或无法确定用量时不编造金额，历史订单也不反查当前价格。
/// </summary>
public static class CreditCostOps
{
    private const long MaxCostPriceMicrocredits = 1_000_000_000_000;
    private const long MaxChannelModelTokenPriceMicrocredits = 1_000_000_000_000;

    /// <summary>对应 Go: <c>validateCreditCostPricing</c>。</summary>
    public static void ValidateCreditCostPricing(string capability, string billingMode, CreditCostPricingDto cost)
    {
        foreach (long price in new[]
                 {
                     cost.UnitPriceMicrocredits, cost.InputTokenPriceMicrocredits,
                     cost.OutputTokenPriceMicrocredits, cost.CachedTokenPriceMicrocredits,
                 })
        {
            if (price < 0 || price > MaxCostPriceMicrocredits)
            {
                throw AppError.BadAuthRequest("积分成本价必须在 0 到 1000000 积分之间");
            }
        }
        if (!cost.Configured)
        {
            return;
        }
        if (billingMode != "token")
        {
            capability = "";
        }
        ValidateTokenPrices(capability, cost.InputTokenPriceMicrocredits, cost.OutputTokenPriceMicrocredits, cost.CachedTokenPriceMicrocredits);
    }

    /// <summary>对应 Go: <c>validateTokenPrices</c>。</summary>
    public static void ValidateTokenPrices(string capability, long inputPrice, long outputPrice, long cachedPrice)
    {
        if (inputPrice < 0 || outputPrice < 0 || cachedPrice < 0)
        {
            throw AppError.BadAuthRequest("模型积分价格不能小于 0");
        }
        if (capability == "video" && (inputPrice != 0 || cachedPrice != 0))
        {
            throw AppError.BadAuthRequest("视频 Token 仅按视频用量计费，输入和缓存 Token 价格必须为 0");
        }
        if (inputPrice > MaxChannelModelTokenPriceMicrocredits || outputPrice > MaxChannelModelTokenPriceMicrocredits
            || cachedPrice > MaxChannelModelTokenPriceMicrocredits)
        {
            throw AppError.BadAuthRequest("Token 每百万用量价格不能超过 1,000,000 积分");
        }
    }

    /// <summary>
    /// 下单时把实际执行线路的成本快照写入订单。对应 Go: <c>snapshotCreditCost</c>。
    /// </summary>
    public static void Snapshot(BillingOrder order, ChannelModelPriceTier? tier, long quantity, BillingCalc.TokenBillingEstimate estimate)
    {
        if (tier is null)
        {
            return;
        }
        order.CostConfigured = tier.CostConfigured;
        order.CostUnitPriceMicrocredits = tier.CostUnitPriceMicrocredits;
        order.CostInputTokenPriceMicrocredits = tier.CostInputTokenPriceMicrocredits;
        order.CostOutputTokenPriceMicrocredits = tier.CostOutputTokenPriceMicrocredits;
        order.CostCachedTokenPriceMicrocredits = tier.CostCachedTokenPriceMicrocredits;
        order.CostBillingMode = tier.BillingMode;
        order.CostQuantity = 1;
        if (tier.BillingMode == "per_second")
        {
            order.CostQuantity = quantity;
        }
        if (tier.BillingMode == "token" && estimate.Video is not null)
        {
            order.CostVideoFormulaTokens = estimate.Video.FormulaTokens;
        }
    }

    /// <summary>
    /// 已结算订单的管理员成本金额；未配置/未结算/用量不可定时返回 null。
    /// 对应 Go: <c>billingCreditCost</c>（倍率固定 1：×10_000 后按 1e10 向上取整，等价于除以 1e6 向上取整）。
    /// </summary>
    public static long? BillingCreditCost(BillingOrder order)
    {
        if (!order.CostConfigured || order.Status != BillingStatus.BillingStatusSettled)
        {
            return null;
        }
        switch (order.CostBillingMode)
        {
            case "fixed_request":
            case "per_second":
                return BillingCalc.CreditAmount(order.CostUnitPriceMicrocredits, order.CostQuantity, 10_000);
            case "token":
                long input = order.InputTokens;
                long output = order.OutputTokens;
                long cached = order.CachedTokens;
                if (!order.UsageAvailable)
                {
                    if (order.Capability != "video" || order.CostVideoFormulaTokens <= 0)
                    {
                        return null;
                    }
                    input = 0;
                    cached = 0;
                    output = order.CostVideoFormulaTokens;
                }
                // 与 Go 的 big.Int 一致：先求和再乘倍率最后 ceil，这里等价化简为
                // ceil(总微积分基 / 1_000_000)，每一步乘加都做溢出检查。
                long inputNet = Math.Max(input - cached, 0);
                long basis = SafeCostBasis(inputNet, order.CostInputTokenPriceMicrocredits);
                basis = SafeCostAdd(basis, SafeCostBasis(output, order.CostOutputTokenPriceMicrocredits));
                basis = SafeCostAdd(basis, SafeCostBasis(cached, order.CostCachedTokenPriceMicrocredits));
                if (basis > (long.MaxValue - 999_999) / 1)
                {
                    throw new InvalidOperationException("invalid credit cost amount");
                }
                return (basis + 999_999) / 1_000_000;
            default:
                return null;
        }
    }

    private static long SafeCostBasis(long tokens, long price)
    {
        if (tokens < 0 || price < 0 || (tokens > 0 && price > long.MaxValue / tokens))
        {
            throw new InvalidOperationException("invalid credit cost amount");
        }
        return tokens * price;
    }

    private static long SafeCostAdd(long left, long right)
    {
        if (left > long.MaxValue - right)
        {
            throw new InvalidOperationException("invalid credit cost amount");
        }
        return left + right;
    }
}
