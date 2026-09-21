#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>创作端当前参数命中的实际供应线路报价。对应 Go: <c>app.LogicalModelQuote</c>。</summary>
public sealed class LogicalModelQuoteDto
{
    [JsonPropertyName("logicalModelId")]
    public string LogicalModelID { get; init; } = "";

    [JsonPropertyName("billingMode")]
    public string BillingMode { get; init; } = "";

    [JsonPropertyName("quantity")]
    public long Quantity { get; init; }

    [JsonPropertyName("amountMicrocredits")]
    public long AmountMicrocredits { get; init; }

    [JsonPropertyName("estimated")]
    public bool Estimated { get; init; }
}

/// <summary>路由模拟候选。对应 Go: <c>app.RouteSimulationCandidate</c>。</summary>
public sealed record RouteSimulationCandidateDto
{
    [JsonPropertyName("routeId")]
    public string RouteID { get; init; } = "";

    [JsonPropertyName("channelModelId")]
    public string ChannelModelID { get; init; } = "";

    [JsonPropertyName("channelModelKey")]
    public string ChannelModelKey { get; init; } = "";

    [JsonPropertyName("channelModelName")]
    public string ChannelModelName { get; init; } = "";

    [JsonPropertyName("priority")]
    public long Priority { get; init; }

    [JsonPropertyName("weight")]
    public long Weight { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("matched")]
    public bool Matched { get; init; }

    [JsonPropertyName("blocked")]
    public bool Blocked { get; init; }

    [JsonPropertyName("inPool")]
    public bool InPool { get; init; }

    [JsonPropertyName("reasons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Reasons { get; init; }
}

/// <summary>路由模拟结果。对应 Go: <c>app.RouteSimulationResult</c>。</summary>
public sealed class RouteSimulationResultDto
{
    [JsonPropertyName("productMatch")]
    public CapabilityMatch ProductMatch { get; init; } = new() { Matched = false };

    [JsonPropertyName("candidates")]
    public IReadOnlyList<RouteSimulationCandidateDto> Candidates { get; init; } = [];
}

/// <summary>解析完成的路由快照。对应 Go: <c>app.RoutedModel</c>。</summary>
/// <remarks>
/// 解析同时约束能力合同、启用状态、价格档和渠道协议；调用方不得在解析完成后
/// 自行替换其中任一供应链字段，否则会出现“目录显示可用、任务实际走另一条线路”的配置漂移。
/// </remarks>
public sealed class RoutedModel
{
    public LogicalModel LogicalModel { get; init; } = new();

    public LogicalModelRevision Revision { get; init; } = new();

    public LogicalModelRoute Route { get; init; } = new();

    public ChannelModel ChannelModel { get; init; } = new();

    public ChannelModelPriceTier? PriceTier { get; init; }

    public Dictionary<string, JsonElement> Defaults { get; init; } = new();
}

/// <summary>
/// 逻辑模型服务：任务选路、路由模拟与报价。
/// 对应 Go: <c>model_router.go</c> 的 ResolveLogicalModel / sortedRouteDiagnostics 与
/// <c>logical_model_quote.go</c>。
/// </summary>
public sealed partial class LogicalModelService
{
    /// <summary>
    /// 将创作意图解析为一次可执行的路由快照。对应 Go: <c>ResolveLogicalModel</c>。
    /// </summary>
    public async Task<RoutedModel> ResolveLogicalModelAsync(
        string logicalModelId,
        ModelRequestIntent intent,
        CancellationToken cancellationToken = default)
    {
        RouteCatalogSnapshot snapshot = await RouteCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Models.TryGetValue(logicalModelId.Trim(), out CachedLogicalModel? cached))
        {
            throw AppError.BadAuthRequest("所选模型不可用");
        }

        intent.Options = CapabilitySpecOps.MergeIntentDefaults(intent.Options, cached.Defaults);
        CapabilityMatch match = CapabilitySpecOps.MatchCapability(cached.ProductSpec, intent);
        if (!match.Matched)
        {
            throw AppError.BadAuthRequest("所选模型不支持当前请求：" + string.Join("；", match.Reasons ?? []));
        }

        bool requirePriceTier = cached.Model.PricePolicy == "channel";
        List<CachedLogicalRoute> eligible = EligibleLogicalRoutes(cached.Routes, intent, tried: null, requirePriceTier);
        if (eligible.Count == 0)
        {
            throw AppError.BadAuthRequest("当前模型暂时无法满足这组输入和参数");
        }

        CachedLogicalRoute selected = WeightedRoute(eligible);
        ChannelModelPriceTier? priceTier = null;
        if (requirePriceTier)
        {
            priceTier = ModelSku.ChannelModelPriceTierForIntent(selected.ChannelModel, intent);
            if (priceTier is null)
            {
                throw AppError.BadAuthRequest("当前模型尚未配置所选规格的价格");
            }
        }

        return new RoutedModel
        {
            LogicalModel = cached.Model,
            Revision = cached.Revision,
            Route = selected.Route,
            ChannelModel = selected.ChannelModel,
            PriceTier = priceTier,
            Defaults = cached.Defaults,
        };
    }

    /// <summary>对应 Go: <c>eligibleLogicalRoutes</c>。同优先级内加权，高优先级出现时清空低优先级池。</summary>
    private List<CachedLogicalRoute> EligibleLogicalRoutes(
        IReadOnlyList<CachedLogicalRoute> routes,
        ModelRequestIntent intent,
        HashSet<string>? tried,
        bool requirePriceTier)
    {
        List<CachedLogicalRoute> eligible = [];
        long maxPriority = long.MinValue;
        foreach (CachedLogicalRoute route in routes)
        {
            if (!route.Route.Enabled || route.Route.Weight <= 0 ||
                (tried is not null && tried.Contains(route.Route.ID)) ||
                LogicalRouteBlocked(route))
            {
                continue;
            }
            if (!CapabilitySpecOps.MatchCapability(route.CapabilitySpec, intent).Matched)
            {
                continue;
            }
            if (requirePriceTier && ModelSku.ChannelModelPriceTierForIntent(route.ChannelModel, intent) is null)
            {
                continue;
            }
            if (route.Route.Priority > maxPriority)
            {
                eligible.Clear();
                maxPriority = route.Route.Priority;
            }
            if (route.Route.Priority == maxPriority)
            {
                eligible.Add(route);
            }
        }
        return eligible;
    }

    /// <summary>对应 Go: <c>weightedRoute</c>。加密随机数 + 拒绝采样，避免取模导致权重偏差。</summary>
    private static CachedLogicalRoute WeightedRoute(IReadOnlyList<CachedLogicalRoute> routes)
    {
        if (routes.Count == 1)
        {
            return routes[0];
        }

        long total = routes.Sum(route => route.Route.Weight);
        if (total <= 0)
        {
            return routes[0];
        }

        ulong totalUnsigned = (ulong)total;
        // 丢弃不能整除 total 的低概率尾部，避免取模造成轻微权重偏差。
        ulong threshold = ~totalUnsigned + 1;
        threshold %= totalUnsigned;
        byte[] raw = new byte[8];
        ulong randomValue = NextRandomUlong(raw);
        while (randomValue < threshold)
        {
            randomValue = NextRandomUlong(raw);
        }

        long pick = (long)(randomValue % totalUnsigned);
        foreach (CachedLogicalRoute route in routes)
        {
            if (pick < route.Route.Weight)
            {
                return route;
            }
            pick -= route.Route.Weight;
        }
        return routes[^1];
    }

    private static ulong NextRandomUlong(byte[] buffer)
    {
        System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
        return BitConverter.ToUInt64(buffer, 0);
    }

    /// <summary>路由模拟。对应 Go: <c>SimulateLogicalModelRoute</c>（管理端）。</summary>
    public async Task<RouteSimulationResultDto> SimulateLogicalModelRouteAsync(
        string logicalModelId,
        ModelRequestIntent intent,
        CancellationToken cancellationToken = default)
    {
        RouteCatalogSnapshot snapshot = await RouteCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Models.TryGetValue(logicalModelId, out CachedLogicalModel? cached))
        {
            throw AppError.BadAuthRequest("前台模型未启用或尚未发布");
        }

        intent.Options = CapabilitySpecOps.MergeIntentDefaults(intent.Options, cached.Defaults);
        return new RouteSimulationResultDto
        {
            ProductMatch = CapabilitySpecOps.MatchCapability(cached.ProductSpec, intent),
            Candidates = SortedRouteDiagnostics(cached.Routes, intent),
        };
    }

    /// <summary>对应 Go: <c>sortedRouteDiagnostics</c>。</summary>
    private List<RouteSimulationCandidateDto> SortedRouteDiagnostics(
        IReadOnlyList<CachedLogicalRoute> routes,
        ModelRequestIntent intent)
    {
        List<(RouteSimulationCandidateDto Candidate, bool Eligible)> staged = new(routes.Count);
        long poolPriority = long.MinValue;
        foreach (CachedLogicalRoute route in routes)
        {
            CapabilityMatch match = CapabilitySpecOps.MatchCapability(route.CapabilitySpec, intent);
            bool blocked = LogicalRouteBlocked(route);
            bool eligible = route.Route.Enabled && route.Route.Weight > 0 && match.Matched && !blocked;
            if (eligible && route.Route.Priority > poolPriority)
            {
                poolPriority = route.Route.Priority;
            }
            staged.Add((new RouteSimulationCandidateDto
            {
                RouteID = route.Route.ID,
                ChannelModelID = route.ChannelModel.ID,
                ChannelModelKey = route.ChannelModel.ModelKey,
                ChannelModelName = route.ChannelModel.DisplayName,
                Priority = route.Route.Priority,
                Weight = route.Route.Weight,
                Enabled = route.Route.Enabled,
                Matched = match.Matched,
                Blocked = blocked,
                Reasons = match.Reasons,
            }, eligible));
        }

        return staged
            .Select(pair => pair.Candidate with
            {
                InPool = pair.Eligible && pair.Candidate.Priority == poolPriority,
            })
            .OrderByDescending(candidate => candidate.Priority)
            .ToList();
    }

    // ------------------------------------------------------------ 报价

    /// <summary>
    /// 创作端当前参数命中的实际供应线路报价。
    /// 对应 Go: <c>QuoteLogicalModel</c>（logical_model_quote.go）。
    /// </summary>
    public async Task<LogicalModelQuoteDto> QuoteLogicalModelAsync(
        string logicalModelId,
        ModelRequestIntent intent,
        CancellationToken cancellationToken = default)
    {
        RoutedModel routed = await ResolveLogicalModelAsync(logicalModelId, intent, cancellationToken)
            .ConfigureAwait(false);

        string capability = CapabilitySpecOps.NormalizeCapability(intent.Capability);
        if (capability.Length == 0)
        {
            throw AppError.BadAuthRequest("报价请求缺少模型能力类型");
        }

        Dictionary<string, JsonElement> resolvedOptions =
            CapabilitySpecOps.MergeIntentDefaults(intent.Options, routed.Defaults);
        BillingCalc.QuoteInputContext input = BillingCalc.QuoteInput(intent.Capability, routed.ChannelModel.ModelKey, resolvedOptions, intent.Inputs);
        long quantity = BillingCalc.BillingQuantity(capability, input.VideoSeconds);
        if (capability != "video")
        {
            quantity = 1;
        }
        BillingCalc.TokenBillingEstimate tokenEstimate = BillingCalc.EstimateTaskBillingTokens(input, capability);

        if (routed.LogicalModel.PricePolicy == "channel")
        {
            string priceTierId = routed.PriceTier?.ID ?? "";
            BillingOrder order = await BuildBillingOrderWithPriceTierAsync(
                userID: "",
                taskId: "",
                idempotencyKey: "quote",
                channelId: routed.ChannelModel.ChannelID,
                modelKey: routed.ChannelModel.ModelKey,
                capability: capability,
                scene: "model_quote",
                requestedQuantity: quantity,
                tokenEstimate: tokenEstimate,
                priceTierId: priceTierId,
                intent: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new LogicalModelQuoteDto
            {
                LogicalModelID = routed.LogicalModel.ID,
                BillingMode = order.BillingMode,
                Quantity = order.Quantity,
                AmountMicrocredits = order.AmountMicrocredits,
                Estimated = order.BillingMode == "token",
            };
        }

        if (routed.LogicalModel.PricePolicy != "unified")
        {
            throw AppError.BadAuthRequest("当前模型价格策略无效");
        }

        long amount;
        switch (routed.LogicalModel.BillingMode)
        {
            case "fixed_request":
                quantity = 1;
                amount = routed.LogicalModel.UnitPriceMicrocredits;
                break;
            case "per_second":
                if (capability != "video" || quantity <= 0)
                {
                    throw AppError.BadAuthRequest("当前模型按时长计费，但请求未提供有效时长");
                }
                amount = BillingCalc.CreditAmount(routed.LogicalModel.UnitPriceMicrocredits, quantity, 10_000);
                break;
            case "token":
            {
                if (routed.ChannelModel.Capability != capability ||
                    !ModelCapabilityConfigOps.SupportsTokenBilling(capability, routed.ChannelModel.Protocol))
                {
                    throw AppError.BadAuthRequest("当前供应线路不支持前台模型的 Token 计费方式");
                }
                var pricing = new ChannelModel
                {
                    InputTokenPriceMicrocredits = routed.LogicalModel.InputPriceMicrocredits,
                    OutputTokenPriceMicrocredits = routed.LogicalModel.OutputPriceMicrocredits,
                    CachedTokenPriceMicrocredits = routed.LogicalModel.CachedPriceMicrocredits,
                };
                amount = BillingCalc.TokenEstimateAmount(pricing, tokenEstimate, 10_000);
                quantity = tokenEstimate.InputTokens + tokenEstimate.OutputTokens;
                break;
            }
            default:
                throw AppError.BadAuthRequest("当前模型计费方式暂不支持");
        }

        if (amount <= 0)
        {
            throw AppError.BadAuthRequest("当前模型尚未配置有效的用户价格");
        }

        return new LogicalModelQuoteDto
        {
            LogicalModelID = routed.LogicalModel.ID,
            BillingMode = routed.LogicalModel.BillingMode,
            Quantity = quantity,
            AmountMicrocredits = amount,
            Estimated = routed.LogicalModel.BillingMode == "token",
        };
    }

    /// <summary>
    /// 组装账单对象（只计价，不落库——报价场景 userID/taskID 均为空）。
    /// 对应 Go: <c>newBillingOrderWithPriceTier</c>（finance.go）。
    /// </summary>
    public async Task<BillingOrder> BuildBillingOrderWithPriceTierAsync(
        string userID,
        string taskId,
        string idempotencyKey,
        string channelId,
        string modelKey,
        string capability,
        string scene,
        long requestedQuantity,
        BillingCalc.TokenBillingEstimate tokenEstimate,
        string priceTierId,
        ModelRequestIntent? intent,
        CancellationToken cancellationToken = default)
    {
        ChannelModel? item = await _repository.ChannelModelByKeyAsync(channelId, modelKey, cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
        {
            throw AppError.BadAuthRequest("当前模型暂时不可用，请重新选择");
        }

        ChannelModelPriceTier? tier = PriceTierForBilling(item, priceTierId, capability, intent);
        if (tier is null)
        {
            throw AppError.BadAuthRequest("当前模型尚未配置所选规格的用户积分价格");
        }

        long quantity = 1;
        switch (tier.BillingMode)
        {
            case "fixed_request":
                break;
            case "per_second":
                if (item.Capability != "video" || capability != "video")
                {
                    throw AppError.BadAuthRequest("按秒计费仅适用于视频生成");
                }
                if (requestedQuantity <= 0)
                {
                    throw AppError.BadAuthRequest("视频生成时长无效，无法按秒计费");
                }
                quantity = requestedQuantity;
                break;
            case "token":
                if (!ModelCapabilityConfigOps.SupportsTokenBilling(item.Capability, item.Protocol) ||
                    item.Capability != capability)
                {
                    throw AppError.BadAuthRequest("Token 计费仅支持文本生成和火山方舟视频生成");
                }
                if (capability == "text" && (tokenEstimate.InputTokens <= 0 || tokenEstimate.OutputTokens <= 0))
                {
                    throw AppError.BadAuthRequest("无法估算文本 Token 用量");
                }
                if (capability == "video" && tokenEstimate.OutputTokens <= 0)
                {
                    throw AppError.BadAuthRequest("无法估算火山方舟视频 Token 用量");
                }
                quantity = tokenEstimate.InputTokens + tokenEstimate.OutputTokens;
                break;
            default:
                throw AppError.BadAuthRequest("当前模型计费方式暂不支持");
        }

        CreditPolicy policy = await _creditPolicy.GetAsync(cancellationToken).ConfigureAwait(false);
        long multiplierBps = policy.DefaultMultiplierBPS;
        if (policy.ModelMultiplierBPS.TryGetValue(modelKey, out long configured) && configured > 0)
        {
            multiplierBps = configured;
        }

        long amount = tier.BillingMode == "token"
            ? BillingCalc.TokenEstimateAmount(
                new ChannelModel
                {
                    InputTokenPriceMicrocredits = tier.InputTokenPriceMicrocredits,
                    OutputTokenPriceMicrocredits = tier.OutputTokenPriceMicrocredits,
                    CachedTokenPriceMicrocredits = tier.CachedTokenPriceMicrocredits,
                },
                tokenEstimate,
                multiplierBps)
            : BillingCalc.CreditAmount(tier.UnitPriceMicrocredits, quantity, multiplierBps);

        return new BillingOrder
        {
            ID = IdGenerator.NewId(),
            UserID = userID,
            IdempotencyKey = idempotencyKey,
            TaskID = taskId,
            ChannelID = channelId,
            ChannelModelID = item.ID,
            PriceTierID = tier.ID,
            PriceTierVersion = tier.PriceVersion,
            PriceSelectorJSON = tier.SelectorJSON,
            Model = modelKey,
            Capability = capability,
            Scene = TruncateRunes(scene, 80),
            BillingMode = tier.BillingMode,
            PriceVersion = item.PriceVersion,
            UnitPriceMicrocredits = tier.UnitPriceMicrocredits,
            MultiplierBasisPoints = multiplierBps,
            Quantity = quantity,
            AmountMicrocredits = amount,
            ReservedAmountMicrocredits = amount,
            InputTokenPriceMicrocredits = tier.InputTokenPriceMicrocredits,
            OutputTokenPriceMicrocredits = tier.OutputTokenPriceMicrocredits,
            CachedTokenPriceMicrocredits = tier.CachedTokenPriceMicrocredits,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Status = BillingStatuses.BillingStatusReserved,
        };
    }

    /// <summary>对应 Go: <c>channelModelPriceTierForBilling</c>。</summary>
    private static ChannelModelPriceTier? PriceTierForBilling(
        ChannelModel channelModel,
        string priceTierId,
        string capability,
        ModelRequestIntent? intent)
    {
        if (priceTierId.Length > 0)
        {
            foreach (ChannelModelPriceTier tier in channelModel.PriceTiers)
            {
                if (tier.ID == priceTierId && tier.Enabled && tier.PriceConfigured)
                {
                    return tier;
                }
            }
            return null;
        }

        ModelRequestIntent resolved = intent ?? new ModelRequestIntent { Capability = capability };
        if (CapabilitySpecOps.NormalizeCapability(resolved.Capability).Length == 0)
        {
            resolved.Capability = capability;
        }
        return ModelSku.ChannelModelPriceTierForIntent(channelModel, resolved);
    }

    /// <summary>对应 Go: <c>truncateRunes</c>。按字符数截断。</summary>
    internal static string TruncateRunes(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }
        return string.Create(max, value, (span, source) =>
        {
            int written = 0;
            foreach (char ch in source)
            {
                if (written >= span.Length)
                {
                    break;
                }
                span[written++] = ch;
            }
        });
    }
}

/// <summary>账单状态常量别名。对应 Go: <c>model.BillingStatus*</c>。</summary>
internal static class BillingStatuses
{
    public const string BillingStatusReserved = "reserved";
}
