#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>创作端前台模型公开投影。对应 Go: <c>app.PublicLogicalModel</c>（logical_models.go）。</summary>
/// <remarks>
/// 字段声明顺序即 Go 结构体顺序，逐字节契约不可调换。
/// 非密封以便 <see cref="AdminLogicalModelDto"/> 以继承复刻 Go 的匿名内嵌字段顺序。
/// </remarks>
public class PublicLogicalModelDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("capability")]
    public string Capability { get; init; } = "";

    [JsonPropertyName("sortOrder")]
    public long SortOrder { get; init; }

    [JsonPropertyName("pricePolicy")]
    public string PricePolicy { get; init; } = "";

    [JsonPropertyName("pricingMode")]
    public string PricingMode { get; init; } = "";

    [JsonPropertyName("displayPrice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DisplayPrice { get; init; }

    [JsonPropertyName("priceLabel")]
    public string PriceLabel { get; init; } = "";

    [JsonPropertyName("billingMode")]
    public string BillingMode { get; init; } = "";

    [JsonPropertyName("unitPriceMicrocredits")]
    public long UnitPriceMicrocredits { get; init; }

    [JsonPropertyName("inputPriceMicrocredits")]
    public long InputPriceMicrocredits { get; init; }

    [JsonPropertyName("outputPriceMicrocredits")]
    public long OutputPriceMicrocredits { get; init; }

    [JsonPropertyName("cachedPriceMicrocredits")]
    public long CachedPriceMicrocredits { get; init; }

    [JsonPropertyName("priceTiers")]
    public IReadOnlyList<PublicLogicalModelPriceTierDto> PriceTiers { get; init; } = [];

    [JsonPropertyName("legacyModelIds")]
    public IReadOnlyList<string> LegacyModelIDs { get; init; } = [];

    [JsonPropertyName("capabilitySpec")]
    public CapabilitySpec CapabilitySpec { get; init; } = new();

    /// <summary>创作端可见的匿名能力组合，不暴露其背后的供应线路关系。</summary>
    [JsonPropertyName("capabilityProfiles")]
    public IReadOnlyList<CapabilitySpec> CapabilityProfiles { get; init; } = [];

    [JsonPropertyName("defaultOptions")]
    public IReadOnlyDictionary<string, JsonElement> DefaultOptions { get; init; } =
        new Dictionary<string, JsonElement>();

    [JsonPropertyName("available")]
    public bool Available { get; init; }
}

/// <summary>
/// 创作端价格档安全投影，不暴露供应渠道、上游模型 ID 或内部路由信息。
/// 对应 Go: <c>app.PublicLogicalModelPriceTier</c>。
/// </summary>
public sealed class PublicLogicalModelPriceTierDto
{
    [JsonPropertyName("selector")]
    public IReadOnlyDictionary<string, string> Selector { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("resolution")]
    public string Resolution { get; init; } = "";

    [JsonPropertyName("videoSeconds")]
    public long VideoSeconds { get; init; }

    [JsonPropertyName("billingMode")]
    public string BillingMode { get; init; } = "";

    [JsonPropertyName("unitPriceMicrocredits")]
    public long UnitPriceMicrocredits { get; init; }

    [JsonPropertyName("inputTokenPriceMicrocredits")]
    public long InputTokenPriceMicrocredits { get; init; }

    [JsonPropertyName("outputTokenPriceMicrocredits")]
    public long OutputTokenPriceMicrocredits { get; init; }

    [JsonPropertyName("cachedTokenPriceMicrocredits")]
    public long CachedTokenPriceMicrocredits { get; init; }
}

/// <summary>
/// 前台模型目录服务：路由目录快照与创作端公开投影。
/// 对应 Go: <c>model_router.go</c> 的目录缓存与 <c>logical_models.go</c> 的公开读取部分。
/// </summary>
/// <remarks>
/// Go 通过 Redis 协调多实例的目录版本与健康状态；.NET 侧当前只有单实例运行面，
/// 版本号与健康阻断先落本地内存，接口形状保持一致，接入协调器时无需改动调用方。
/// </remarks>
public sealed partial class LogicalModelService
{
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CatalogMaxStale = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(2);

    private readonly Repository _repository;
    private readonly CreditPolicyService _creditPolicy;
    private readonly object _catalogLock = new();
    private RouteCatalogSnapshot? _catalog;
    private Exception? _refreshError;
    private DateTime _retryAt = DateTime.MinValue;
    private long _catalogVersion;
    private readonly Dictionary<string, DateTime> _routeHealthBlocked = new(StringComparer.Ordinal);

    public LogicalModelService(Repository repository, CreditPolicyService? creditPolicy = null)
    {
        _repository = repository;
        _creditPolicy = creditPolicy ?? new CreditPolicyService(repository);
    }

    // ------------------------------------------------------------ 目录快照

    /// <summary>失效目录缓存。对应 Go: <c>invalidateRouteCatalog</c>（本地部分）。</summary>
    public void InvalidateRouteCatalog()
    {
        lock (_catalogLock)
        {
            _catalog = null;
            _catalogVersion++;
            _retryAt = DateTime.MinValue;
            _refreshError = null;
        }
    }

    /// <summary>标记线路不可用直到指定时刻。对应 Go: <c>blockLogicalRouteForFailure</c> 的本地降级路径。</summary>
    public void BlockLogicalRoute(string key, DateTime until)
    {
        lock (_catalogLock)
        {
            _routeHealthBlocked[key] = until;
        }
    }

    /// <summary>对应 Go: <c>logicalRouteBlocked</c>。当前无分布式协调器，只查本地健康表。</summary>
    private bool LogicalRouteBlocked(CachedLogicalRoute route)
    {
        DateTime now = DateTime.UtcNow;
        // 这里会删除过期项，必须持锁；不要改成读锁。
        lock (_catalogLock)
        {
            foreach (string key in new[]
                     {
                         "channel:" + route.ChannelModel.ChannelID,
                         "channel-model:" + route.ChannelModel.ID,
                         "route:" + route.Route.ID,
                     })
            {
                if (!_routeHealthBlocked.TryGetValue(key, out DateTime until))
                {
                    continue;
                }
                if (until <= now)
                {
                    _routeHealthBlocked.Remove(key);
                    continue;
                }
                return true;
            }
        }
        return false;
    }

    private long CurrentRouteCatalogVersion()
    {
        lock (_catalogLock)
        {
            return _catalogVersion;
        }
    }

    /// <summary>
    /// 获取目录快照：30 秒 TTL，刷新失败后 2 秒冷却，冷却期内允许 5 分钟旧快照续服务。
    /// 对应 Go: <c>routeCatalogSnapshot</c>。
    /// </summary>
    public async Task<RouteCatalogSnapshot> RouteCatalogSnapshotAsync(CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;
        long version = CurrentRouteCatalogVersion();
        lock (_catalogLock)
        {
            if (_catalog is not null && now - _catalog.LoadedAt < CatalogTtl && _catalog.CatalogVersion == version)
            {
                return _catalog;
            }
        }

        // 刷新锁只能防止并行回源，失败后还需要冷却，否则等待者会逐个重打数据库。
        lock (_catalogLock)
        {
            now = DateTime.UtcNow;
            version = CurrentRouteCatalogVersion();
            if (_catalog is not null && now - _catalog.LoadedAt < CatalogTtl && _catalog.CatalogVersion == version)
            {
                return _catalog;
            }

            if (_refreshError is not null && now < _retryAt)
            {
                if (_catalog is not null && _catalog.CatalogVersion == version && now - _catalog.LoadedAt <= CatalogMaxStale)
                {
                    return _catalog;
                }
                throw _refreshError;
            }
        }

        RouteCatalogSnapshot loaded;
        try
        {
            loaded = await LoadRouteCatalogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            lock (_catalogLock)
            {
                _refreshError = error;
                _retryAt = DateTime.UtcNow + RetryCooldown;
                // 已有快照过期时允许短暂继续服务，数据库首次加载失败则明确失败。
                if (_catalog is not null && _catalog.CatalogVersion == version &&
                    DateTime.UtcNow - _catalog.LoadedAt <= CatalogMaxStale)
                {
                    return _catalog;
                }
            }
            throw;
        }

        lock (_catalogLock)
        {
            _refreshError = null;
            _retryAt = DateTime.MinValue;
            _catalog = loaded;
        }
        return loaded;
    }

    /// <summary>对应 Go: <c>loadRouteCatalog</c>。</summary>
    private async Task<RouteCatalogSnapshot> LoadRouteCatalogAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<LogicalModel> items = await _repository
            .LogicalModelsAsync(false, cancellationToken).ConfigureAwait(false);

        RouteCatalogSnapshot snapshot = new()
        {
            LoadedAt = DateTime.UtcNow,
            CatalogVersion = CurrentRouteCatalogVersion(),
        };

        IReadOnlyDictionary<string, LogicalModelGraph> graphs = await _repository
            .LogicalModelGraphsAsync(items, false, cancellationToken).ConfigureAwait(false);

        List<string> systemChannelIDs = [];
        foreach (LogicalModelGraph? graph in graphs.Values)
        {
            foreach (ChannelModel channelModel in graph.ChannelModels)
            {
                systemChannelIDs.Add(channelModel.ChannelID);
            }
        }
        IReadOnlyList<ModelChannel> systemChannels = await _repository
            .SystemChannelsByIDsAsync(systemChannelIDs, false, cancellationToken).ConfigureAwait(false);
        HashSet<string> enabledSystemChannels = new(StringComparer.Ordinal);
        foreach (ModelChannel channel in systemChannels)
        {
            enabledSystemChannels.Add(channel.ID);
        }

        foreach (LogicalModel item in items)
        {
            if (!graphs.TryGetValue(item.ID, out LogicalModelGraph? graph) || graph.Revision is null)
            {
                Console.Error.WriteLine($"logical model omitted from route catalog id={item.ID}: graph unavailable");
                continue;
            }

            CapabilitySpec productSpec;
            try
            {
                productSpec = CapabilitySpecOps.DecodeCapabilitySpec(graph.Revision.CapabilitySpecJSON);
            }
            catch (AppError decodeError)
            {
                Console.Error.WriteLine(
                    $"logical model omitted from route catalog id={item.ID}: invalid product capability: {decodeError.Message}");
                continue;
            }

            Dictionary<string, ChannelModel> channelModelByID = new(StringComparer.Ordinal);
            foreach (ChannelModel channelModel in graph.ChannelModels)
            {
                channelModelByID[channelModel.ID] = channelModel;
            }

            CachedLogicalModel cached = new()
            {
                Model = item,
                Revision = graph.Revision,
                ProductSpec = productSpec,
            };

            foreach (LogicalModelRoute route in graph.Routes)
            {
                if (!channelModelByID.TryGetValue(route.ChannelModelID, out ChannelModel? channelModel) ||
                    !channelModel.Enabled ||
                    !enabledSystemChannels.Contains(channelModel.ChannelID))
                {
                    continue;
                }
                if (item.PricePolicy == "unified" && item.BillingMode == "token" &&
                    !ModelCapabilityConfigOps.SupportsTokenBilling(item.Capability, channelModel.Protocol))
                {
                    continue;
                }
                if (item.PricePolicy == "channel" && !ChannelModelHasActivePriceTier(channelModel))
                {
                    continue;
                }

                CapabilitySpec capabilitySpec;
                try
                {
                    capabilitySpec = ChannelModelCapabilitySpec(channelModel);
                }
                catch (AppError specError)
                {
                    Console.Error.WriteLine(
                        $"logical route omitted from catalog route_id={route.ID} channel_model_id={channelModel.ID}: invalid capability: {specError.Message}");
                    continue;
                }
                cached.Routes.Add(new CachedLogicalRoute
                {
                    Route = route,
                    CapabilitySpec = capabilitySpec,
                    ChannelModel = channelModel,
                });
            }

            List<CapabilitySpec> routeSpecs = cached.Routes.Select(entry => entry.CapabilitySpec).ToList();
            CapabilitySpec enriched = CapabilitySpecPresets.WithRoutePresets(productSpec, routeSpecs);
            Dictionary<string, JsonElement> defaults;
            try
            {
                defaults = CapabilitySpecOps.DecodeLogicalDefaults(graph.Revision.DefaultOptionsJSON, enriched);
            }
            catch (Exception defaultsError) when (defaultsError is AppError or JsonException)
            {
                Console.Error.WriteLine(
                    $"logical model omitted from route catalog id={item.ID}: invalid defaults: {defaultsError.Message}");
                continue;
            }
            cached.ProductSpec = enriched;
            cached.Defaults = defaults;

            snapshot.Models[item.ID] = cached;
            snapshot.Ordered.Add(item.ID);
        }
        return snapshot;
    }

    // ------------------------------------------------------------ 公开目录

    /// <summary>
    /// 创作端前台模型目录。对应 Go: <c>PublicLogicalModels</c>。
    /// </summary>
    public async Task<IReadOnlyList<PublicLogicalModelDto>> PublicLogicalModelsAsync(
        ModelRequestIntent? intent,
        CancellationToken cancellationToken = default)
    {
        RouteCatalogSnapshot snapshot = await RouteCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);
        List<PublicLogicalModelDto> result = new(snapshot.Ordered.Count);
        foreach (string id in snapshot.Ordered)
        {
            CachedLogicalModel cached = snapshot.Models[id];
            List<CapabilitySpec> structuralSpecs = AvailableCachedRouteSpecs(cached.Routes);
            bool coverageValid = LogicalModelCapabilityCovered(cached.ProductSpec, structuralSpecs);
            bool available = coverageValid && HasHealthyCachedRoute(cached.Routes);
            if (intent is not null)
            {
                ModelRequestIntent resolvedIntent = new()
                {
                    Capability = intent.Capability,
                    Operation = intent.Operation,
                    Inputs = intent.Inputs is null
                        ? null
                        : new Dictionary<string, long>(intent.Inputs, StringComparer.Ordinal),
                    Options = CapabilitySpecOps.MergeIntentDefaults(intent.Options, cached.Defaults),
                };
                CapabilityMatch productMatch = CapabilitySpecOps.MatchCapability(cached.ProductSpec, resolvedIntent);
                if (!productMatch.Matched)
                {
                    continue;
                }
                available = false;
                if (coverageValid)
                {
                    foreach (CachedLogicalRoute route in cached.Routes)
                    {
                        if (route.Route.Enabled && route.Route.Weight > 0 && !LogicalRouteBlocked(route) &&
                            CapabilitySpecOps.MatchCapability(route.CapabilitySpec, resolvedIntent).Matched &&
                            (cached.Model.PricePolicy != "channel" ||
                             ModelSku.ChannelModelPriceTierForIntent(route.ChannelModel, resolvedIntent) is not null))
                        {
                            available = true;
                            break;
                        }
                    }
                }
            }
            result.Add(PublicLogicalModel(cached, available));
        }
        return result;
    }

    /// <summary>对应 Go: <c>publicLogicalModel</c>。</summary>
    private static PublicLogicalModelDto PublicLogicalModel(CachedLogicalModel cached, bool available)
    {
        LogicalModel item = cached.Model;
        List<CapabilitySpec> routeSpecs = cached.Routes
            .Select(route => route.CapabilitySpec)
            .ToList();
        CapabilitySpec productSpec = CapabilitySpecPresets.WithRoutePresets(cached.ProductSpec, routeSpecs);

        List<CapabilitySpec> profiles = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (CachedLogicalRoute route in cached.Routes)
        {
            if (!route.Route.Enabled || route.Route.Weight <= 0)
            {
                continue;
            }
            string key = CapabilitySpecOps.CapabilityFingerprint(route.CapabilitySpec);
            if (seen.Add(key))
            {
                profiles.Add(route.CapabilitySpec);
            }
        }

        List<PublicLogicalModelPriceTierDto> priceTiers = PublicLogicalModelPriceTiers(cached);
        (string pricingMode, long? displayPrice, string priceLabel) =
            ComputeModelPriceDisplay(item, priceTiers);

        return new PublicLogicalModelDto
        {
            ID = item.ID,
            Code = item.Code,
            Name = item.Name,
            Icon = item.Icon,
            Description = item.Description,
            Capability = item.Capability,
            SortOrder = item.SortOrder,
            PricePolicy = item.PricePolicy,
            PricingMode = pricingMode,
            DisplayPrice = displayPrice,
            PriceLabel = priceLabel,
            BillingMode = item.BillingMode,
            UnitPriceMicrocredits = item.UnitPriceMicrocredits,
            InputPriceMicrocredits = item.InputPriceMicrocredits,
            OutputPriceMicrocredits = item.OutputPriceMicrocredits,
            CachedPriceMicrocredits = item.CachedPriceMicrocredits,
            PriceTiers = priceTiers,
            LegacyModelIDs = DecodeLegacyModelIDs(item.LegacyModelIDsJSON),
            CapabilitySpec = productSpec,
            CapabilityProfiles = profiles,
            DefaultOptions = cached.Defaults,
            Available = available,
        };
    }

    /// <summary>对应 Go: <c>publicLogicalModelPriceTiers</c>。</summary>
    private static List<PublicLogicalModelPriceTierDto> PublicLogicalModelPriceTiers(CachedLogicalModel cached)
    {
        if (cached.Model.PricePolicy != "channel")
        {
            return [];
        }
        List<PublicLogicalModelPriceTierDto> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (CachedLogicalRoute route in cached.Routes)
        {
            if (!route.Route.Enabled || route.Route.Weight <= 0)
            {
                continue;
            }
            foreach (ChannelModelPriceTier tier in route.ChannelModel.PriceTiers)
            {
                if (!tier.Enabled || !tier.PriceConfigured)
                {
                    continue;
                }
                Dictionary<string, string> selector = ModelSku.SkuSelectorForTier(tier);
                (Dictionary<string, string> canonicalSelector, string selectorKey) =
                    ModelSku.CanonicalSkuSelector(selector);
                string key =
                    $"{selectorKey}:{tier.BillingMode}:{tier.UnitPriceMicrocredits}:{tier.InputTokenPriceMicrocredits}:{tier.OutputTokenPriceMicrocredits}:{tier.CachedTokenPriceMicrocredits}";
                if (!seen.Add(key))
                {
                    continue;
                }
                result.Add(new PublicLogicalModelPriceTierDto
                {
                    Selector = canonicalSelector,
                    Resolution = ModelCapabilityConfigOps.NormalizeChannelModelTierResolution(tier.Resolution),
                    VideoSeconds = tier.VideoSeconds,
                    BillingMode = tier.BillingMode,
                    UnitPriceMicrocredits = tier.UnitPriceMicrocredits,
                    InputTokenPriceMicrocredits = tier.InputTokenPriceMicrocredits,
                    OutputTokenPriceMicrocredits = tier.OutputTokenPriceMicrocredits,
                    CachedTokenPriceMicrocredits = tier.CachedTokenPriceMicrocredits,
                });
            }
        }
        return result;
    }

    /// <summary>对应 Go: <c>decodeLegacyModelIDs</c>。损坏 JSON 返回空数组。</summary>
    private static IReadOnlyList<string> DecodeLegacyModelIDs(string raw)
    {
        List<string>? values;
        try
        {
            values = JsonSerializer.Deserialize<List<string>>(raw, CapabilityJson.ReadOptions);
        }
        catch (JsonException)
        {
            return [];
        }
        return NormalizeLegacyModelIDs(values);
    }

    /// <summary>对应 Go: <c>normalizeLegacyModelIDs</c>。</summary>
    private static IReadOnlyList<string> NormalizeLegacyModelIDs(IEnumerable<string>? values)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string raw in values ?? [])
        {
            string value = raw.Trim();
            if (value.Length == 0 || !seen.Add(value))
            {
                continue;
            }
            result.Add(value);
        }
        return result;
    }

    // ------------------------------------------------------------ 结构可用性

    /// <summary>对应 Go: <c>availableCachedRouteSpecs</c>。</summary>
    private static List<CapabilitySpec> AvailableCachedRouteSpecs(IReadOnlyList<CachedLogicalRoute> routes)
    {
        List<CapabilitySpec> result = [];
        foreach (CachedLogicalRoute route in routes)
        {
            if (route.Route.Enabled && route.Route.Weight > 0)
            {
                result.Add(route.CapabilitySpec);
            }
        }
        return result;
    }

    /// <summary>对应 Go: <c>hasHealthyCachedRoute</c>。</summary>
    private bool HasHealthyCachedRoute(IReadOnlyList<CachedLogicalRoute> routes)
    {
        foreach (CachedLogicalRoute route in routes)
        {
            if (route.Route.Enabled && route.Route.Weight > 0 && !LogicalRouteBlocked(route))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>对应 Go: <c>logicalModelCapabilityCovered</c>。校验失败视为不覆盖，不向上抛。</summary>
    private static bool LogicalModelCapabilityCovered(CapabilitySpec product, IReadOnlyList<CapabilitySpec> routeSpecs)
    {
        if (routeSpecs.Count == 0)
        {
            return false;
        }
        try
        {
            CapabilitySpecOps.ValidateProductSpecWithinRoutes(product, routeSpecs);
            return true;
        }
        catch (AppError)
        {
            return false;
        }
    }

    /// <summary>对应 Go: <c>channelModelHasActivePriceTier</c>。</summary>
    private static bool ChannelModelHasActivePriceTier(ChannelModel channelModel)
    {
        foreach (ChannelModelPriceTier tier in channelModel.PriceTiers)
        {
            if (tier.Enabled && tier.PriceConfigured)
            {
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------ 渠道模型能力投影

    /// <summary>
    /// 渠道模型 → 路由能力规格。对应 Go: <c>channelModelCapabilitySpec</c>。
    /// 要求 <paramref name="channelModel"/> 的价格档已附着。
    /// </summary>
    internal static CapabilitySpec ChannelModelCapabilitySpec(ChannelModel channelModel)
    {
        string capability = CapabilitySpecOps.NormalizeCapability(channelModel.Capability);
        ModelCapabilityConfig? config;
        try
        {
            config = ModelCapabilityConfigOps.DecodeModelCapabilityConfig(channelModel.CapabilityConfigJSON);
        }
        catch (AppError)
        {
            throw AppError.BadAuthRequest("渠道模型能力配置无效，请先修复渠道模型");
        }
        if (config is not null)
        {
            ModelCapabilityConfig? normalized = ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
                capability,
                channelModel.Protocol,
                FirstNonEmpty(channelModel.ProviderModelKey, channelModel.ModelKey),
                config);
            config = normalized;
        }

        CapabilitySpec spec = ModelCapabilityConfigOps.CapabilitySpecFromModelCapabilityConfig(config, capability);
        return CapabilitySpecPresets.WithPriceTiers(spec, channelModel);
    }

    /// <summary>对应 Go: <c>kernel.FirstNonEmpty</c>。取第一个非空白值并修剪。</summary>
    internal static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (value.Trim().Length > 0)
            {
                return value.Trim();
            }
        }
        return "";
    }

    // ------------------------------------------------------------ 价格展示

    /// <summary>对应 Go: <c>computeModelPriceDisplay</c>。返回 (pricingMode, displayPrice, priceLabel)。</summary>
    private static (string PricingMode, long? DisplayPrice, string PriceLabel) ComputeModelPriceDisplay(
        LogicalModel model, IReadOnlyList<PublicLogicalModelPriceTierDto> priceTiers)
    {
        if (model.PricePolicy == "channel")
        {
            // 跟随渠道价格
            if (priceTiers.Count == 0)
            {
                return ("provider", null, "未配置");
            }
            // 检查是否所有价格档都相同（单档且价格 > 0 时直接展示价格）
            if (priceTiers.Count == 1)
            {
                long price = GetTierDisplayPrice(priceTiers[0]);
                if (price > 0)
                {
                    return ("provider", price, "");
                }
            }
            // 多个价格档或价格为 0，显示"按渠道规格计费"
            return ("provider", null, "按渠道规格计费");
        }

        // 统一定价模式
        if (model.BillingMode == "fixed_request" && model.UnitPriceMicrocredits > 0)
        {
            return ("unified", model.UnitPriceMicrocredits, "");
        }
        if (model.BillingMode == "per_second" && model.UnitPriceMicrocredits > 0)
        {
            return ("unified", model.UnitPriceMicrocredits, "按秒");
        }
        if (model.BillingMode == "token")
        {
            // Token 计费显示输入/输出价格
            if (model.InputPriceMicrocredits > 0 || model.OutputPriceMicrocredits > 0)
            {
                return ("unified", null, "按 Token");
            }
        }

        return ("unified", null, "未配置");
    }

    /// <summary>对应 Go: <c>getTierDisplayPrice</c>。</summary>
    private static long GetTierDisplayPrice(PublicLogicalModelPriceTierDto tier)
    {
        if (tier.BillingMode is "fixed_request" or "per_second")
        {
            return tier.UnitPriceMicrocredits;
        }
        // Token 计费返回输出价格（如果有）
        if (tier.OutputTokenPriceMicrocredits > 0)
        {
            return tier.OutputTokenPriceMicrocredits;
        }
        if (tier.InputTokenPriceMicrocredits > 0)
        {
            return tier.InputTokenPriceMicrocredits;
        }
        return 0;
    }
}

/// <summary>目录快照缓存条目。对应 Go: <c>cachedLogicalModel</c> / <c>routeCatalogSnapshot</c>。</summary>
public sealed class RouteCatalogSnapshot
{
    public DateTime LoadedAt { get; init; }

    public long CatalogVersion { get; init; }

    public Dictionary<string, CachedLogicalModel> Models { get; init; } = new(StringComparer.Ordinal);

    public List<string> Ordered { get; init; } = [];
}

public sealed class CachedLogicalModel
{
    public LogicalModel Model { get; init; } = new();

    public LogicalModelRevision Revision { get; init; } = new();

    public CapabilitySpec ProductSpec { get; set; } = new();

    public Dictionary<string, JsonElement> Defaults { get; set; } = new();

    public List<CachedLogicalRoute> Routes { get; } = [];
}

public sealed class CachedLogicalRoute
{
    public LogicalModelRoute Route { get; init; } = new();

    public CapabilitySpec CapabilitySpec { get; init; } = new();

    public ChannelModel ChannelModel { get; init; } = new();
}

/// <summary>
/// 能力规格的路由预设修复与价格档收窄。
/// 对应 Go: <c>capabilitySpecWithRoutePresets</c> / <c>mergeCapabilityImageSize</c> /
/// <c>capabilitySpecWithPriceTiers</c>（logical_models.go）。
/// </summary>
public static class CapabilitySpecPresets
{
    /// <summary>
    /// 修复旧前台模型快照只存 `*` 的自定义尺寸：通配符保留用于匹配自定义值，
    /// 同时从供应线路恢复预设值供管理与创作端选择器展示。
    /// </summary>
    public static CapabilitySpec WithRoutePresets(CapabilitySpec spec, IReadOnlyList<CapabilitySpec> routes)
    {
        CapabilitySpec result = spec.Clone();
        result.Options = CapabilitySpecOps.SortedMap<OptionConstraint>();
        foreach ((string name, OptionConstraint constraint) in spec.Options ?? [])
        {
            if (!CapabilitySpecOps.IsWildcardOptionConstraint(constraint))
            {
                result.Options[name] = constraint;
                continue;
            }
            List<JsonElement> values = constraint.Values is null ? [] : [.. constraint.Values];
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (JsonElement value in values)
            {
                seen.Add(CapabilitySpecOps.NormalizedScalar(value));
            }
            foreach (CapabilitySpec route in routes)
            {
                if (route.Options is null ||
                    !route.Options.TryGetValue(name, out OptionConstraint? routeConstraint))
                {
                    continue;
                }
                foreach (JsonElement value in routeConstraint.Values ?? [])
                {
                    string key = CapabilitySpecOps.NormalizedScalar(value);
                    if (key.Length > 0 && seen.Add(key))
                    {
                        values.Add(value);
                    }
                }
            }
            result.Options[name] = new OptionConstraint { Values = values.Count == 0 ? null : values };
        }

        if (result.ImageSize is null || result.ImageSize.Parameter.Length == 0 ||
            (result.ImageSize.Presets?.Count ?? 0) == 0)
        {
            List<CapabilitySpec> merged = [spec, .. routes];
            CapabilityImageSize? imageSize = MergeCapabilityImageSize(merged);
            if (imageSize is not null)
            {
                if (result.ImageSize is null)
                {
                    result.ImageSize = imageSize;
                }
                else
                {
                    CapabilityImageSize restored = new()
                    {
                        Parameter = result.ImageSize.Parameter,
                        AllowCustom = result.ImageSize.AllowCustom,
                        Presets = result.ImageSize.Presets,
                    };
                    if (restored.Parameter.Length == 0)
                    {
                        restored.Parameter = imageSize.Parameter;
                    }
                    if (!restored.AllowCustom)
                    {
                        restored.AllowCustom = imageSize.AllowCustom;
                    }
                    if (restored.Presets is null || restored.Presets.Count == 0)
                    {
                        restored.Presets = imageSize.Presets;
                    }
                    result.ImageSize = restored;
                }
            }
        }
        return result;
    }

    /// <summary>对应 Go: <c>mergeCapabilityImageSize</c>。</summary>
    private static CapabilityImageSize? MergeCapabilityImageSize(IReadOnlyList<CapabilitySpec> specs)
    {
        CapabilityImageSize? result = null;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (CapabilitySpec spec in specs)
        {
            CapabilityImageSize? part = spec.ImageSize;
            if (part is null)
            {
                continue;
            }
            if (result is null)
            {
                result = new CapabilityImageSize { Parameter = part.Parameter, AllowCustom = part.AllowCustom };
            }
            else
            {
                if (result.Parameter != "aspect_ratio" && part.Parameter == "aspect_ratio")
                {
                    result.Parameter = "aspect_ratio";
                }
                else if (result.Parameter.Length == 0)
                {
                    result.Parameter = part.Parameter;
                }
                result.AllowCustom = result.AllowCustom || part.AllowCustom;
            }
            foreach (CapabilityImageSizePreset preset in part.Presets ?? [])
            {
                string key = preset.Tier + ":" + preset.Ratio + ":" + preset.Size;
                if (key == "::" || !seen.Add(key))
                {
                    continue;
                }
                (result.Presets ??= []).Add(preset);
            }
        }
        return result;
    }

    /// <summary>
    /// 只让创作端选择已启用且可结算的视频规格。
    /// 对应 Go: <c>capabilitySpecWithPriceTiers</c>。
    /// </summary>
    public static CapabilitySpec WithPriceTiers(CapabilitySpec spec, ChannelModel channelModel)
    {
        if (CapabilitySpecOps.NormalizeCapability(spec.Capability) != "video" ||
            channelModel.PriceTiers.Count == 0)
        {
            return spec;
        }

        List<ChannelModelPriceTier> tiers = channelModel.PriceTiers
            .Where(tier => tier.Enabled && tier.PriceConfigured)
            .ToList();
        if (tiers.Count == 0)
        {
            return spec;
        }

        CapabilitySpec result = spec.Clone();
        result.Options = CapabilitySpecOps.SortedMap<OptionConstraint>();
        foreach ((string name, OptionConstraint option) in spec.Options ?? [])
        {
            result.Options[name] = option;
        }

        bool hasResolutionWildcard = false;
        bool hasDurationWildcard = false;
        List<JsonElement> resolutions = [];
        List<JsonElement> durations = [];
        HashSet<string> seenResolutions = new(StringComparer.Ordinal);
        HashSet<long> seenDurations = [];
        foreach (ChannelModelPriceTier? tier in tiers)
        {
            string resolution = ModelCapabilityConfigOps.NormalizeChannelModelTierResolution(tier.Resolution);
            if (resolution == "*")
            {
                hasResolutionWildcard = true;
            }
            else if (seenResolutions.Add(resolution))
            {
                resolutions.Add(JsonSerializer.SerializeToElement(resolution));
            }
            if (tier.VideoSeconds == 0)
            {
                hasDurationWildcard = true;
            }
            else if (seenDurations.Add(tier.VideoSeconds))
            {
                durations.Add(JsonSerializer.SerializeToElement(tier.VideoSeconds));
            }
        }
        if (!hasResolutionWildcard && resolutions.Count > 0)
        {
            result.Options["vquality"] = new OptionConstraint { Values = resolutions };
        }
        if (!hasDurationWildcard && durations.Count > 0)
        {
            result.Options["videoSeconds"] = new OptionConstraint { Values = durations };
        }
        return result;
    }
}
