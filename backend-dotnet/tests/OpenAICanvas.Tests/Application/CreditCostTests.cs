#nullable enable
using System.Text.Json;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 管理员积分成本（Go v27）：价格校验、下单快照与已结算成本金额、管理端投影。
/// 对应 Go: <c>app/credit_cost.go</c> 与 <c>handler.adminChannelModel</c>。
/// </summary>
public sealed class CreditCostTests
{
    private static CreditCostPricingDto Cost(bool configured = true, long unit = 500, long input = 0, long output = 1_000, long cached = 0) =>
        new()
        {
            Configured = configured,
            UnitPriceMicrocredits = unit,
            InputTokenPriceMicrocredits = input,
            OutputTokenPriceMicrocredits = output,
            CachedTokenPriceMicrocredits = cached,
        };

    [Fact]
    public void 成本价校验_上限_未配置_视频约束()
    {
        // 超上限。
        Assert.Throws<AppError>(() => CreditCostOps.ValidateCreditCostPricing(
            "video", "per_second", Cost(unit: 1_000_000_000_001)));
        // 未配置只查范围。
        CreditCostOps.ValidateCreditCostPricing("video", "per_second", Cost(configured: false, unit: 0, output: 5));
        // 配置后 token 模式按 validateTokenPrices：视频输入/缓存必须为 0。
        Assert.Throws<AppError>(() => CreditCostOps.ValidateCreditCostPricing(
            "video", "token", Cost(input: 10)));
        CreditCostOps.ValidateCreditCostPricing("text", "token", Cost(input: 10, cached: 5));
        // 非 token 模式 capability 清空，不做视频约束。
        CreditCostOps.ValidateCreditCostPricing("video", "per_second", Cost());
    }

    [Fact]
    public void 下单快照按计费方式落列()
    {
        BillingOrder order = new();
        ChannelModelPriceTier tier = new()
        {
            CostConfigured = true,
            CostUnitPriceMicrocredits = 500,
            CostInputTokenPriceMicrocredits = 0,
            CostOutputTokenPriceMicrocredits = 1_000,
            CostCachedTokenPriceMicrocredits = 0,
            BillingMode = "token",
        };
        var estimate = new BillingCalc.TokenBillingEstimate(0, 1_100_000)
        {
            Video = new BillingCalc.VideoTokenEstimate { FormulaTokens = 1_000_000, ReservedTokens = 1_100_000 },
        };
        CreditCostOps.Snapshot(order, tier, quantity: 5, estimate);
        Assert.True(order.CostConfigured);
        Assert.Equal("token", order.CostBillingMode);
        Assert.Equal(1, order.CostQuantity); // token 恒为 1
        Assert.Equal(1_000_000, order.CostVideoFormulaTokens);

        ChannelModelPriceTier perSecond = new()
        {
            CostConfigured = true,
            CostUnitPriceMicrocredits = 500,
            BillingMode = "per_second",
        };
        BillingOrder perSecondOrder = new();
        CreditCostOps.Snapshot(perSecondOrder, perSecond, quantity: 6, default);
        Assert.Equal(6, perSecondOrder.CostQuantity);
    }

    [Fact]
    public void 成本金额_未配置或未结算返回空_视频回退公式()
    {
        BillingOrder unsettled = new()
        {
            CostConfigured = true,
            CostBillingMode = "token",
            Status = BillingStatus.BillingStatusRunning,
        };
        Assert.Null(CreditCostOps.BillingCreditCost(unsettled));

        // 视频无 usage：回退公式快照，成本 = ceil(公式 × 成本输出价 / 1e6)。
        BillingOrder videoFormula = new()
        {
            CostConfigured = true,
            CostBillingMode = "token",
            Status = BillingStatus.BillingStatusSettled,
            Capability = "video",
            CostOutputTokenPriceMicrocredits = 20_000,
            CostVideoFormulaTokens = 1_000_000,
            InputTokens = 123, // 不可用时不采信
            OutputTokens = 999,
            CachedTokens = 7,
        };
        long? cost = CreditCostOps.BillingCreditCost(videoFormula);
        Assert.Equal((1_000_000L * 20_000 + 999_999) / 1_000_000, cost);

        // 有可用 usage：真实用量（视频只按输出）。
        BillingOrder videoUsage = new()
        {
            CostConfigured = true,
            CostBillingMode = "token",
            Status = BillingStatus.BillingStatusSettled,
            Capability = "video",
            CostOutputTokenPriceMicrocredits = 20_000,
            UsageAvailable = true,
            InputTokens = 123,
            OutputTokens = 800_000,
            CachedTokens = 7,
        };
        Assert.Equal((800_000L * 20_000 + 999_999) / 1_000_000, CreditCostOps.BillingCreditCost(videoUsage));

        // 固定价格：单价 × 快照数量。
        BillingOrder fixedOrder = new()
        {
            CostConfigured = true,
            CostBillingMode = "fixed_request",
            Status = BillingStatus.BillingStatusSettled,
            CostUnitPriceMicrocredits = 500,
            CostQuantity = 3,
        };
        Assert.Equal(BillingCalc.CreditAmount(500, 3, 10_000), CreditCostOps.BillingCreditCost(fixedOrder));
    }

    [Fact]
    public void 管理端投影携带costPricing且模型字段不变()
    {
        ChannelModel model = new()
        {
            ID = "MODEL_000001",
            ModelKey = "gpt-text",
            Capability = "text",
            PriceTiers =
            [
                new ChannelModelPriceTier
                {
                    ID = "PTIER_000001",
                    BillingMode = "token",
                    PriceConfigured = true,
                    CostConfigured = true,
                    CostOutputTokenPriceMicrocredits = 20_000,
                },
            ],
        };
        System.Text.Json.Nodes.JsonObject node = ChannelModelAdminService.AdminChannelModelNode(model);
        Assert.Equal("gpt-text", node["modelKey"]?.GetValue<string>());
        System.Text.Json.Nodes.JsonArray tiers = (node["priceTiers"] as System.Text.Json.Nodes.JsonArray)!;
        System.Text.Json.Nodes.JsonObject tier0 = (tiers[0] as System.Text.Json.Nodes.JsonObject)!;
        Assert.True(tier0["costPricing"]?["configured"]?.GetValue<bool>());
        Assert.Equal(20_000, tier0["costPricing"]?["outputTokenPriceMicrocredits"]?.GetValue<long>());
    }
}
