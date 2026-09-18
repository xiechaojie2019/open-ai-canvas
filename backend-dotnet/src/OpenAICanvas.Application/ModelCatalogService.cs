#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>目录响应。对应 Go: <c>app.ModelCatalogResponse</c>（两集合恒为数组）。</summary>
public sealed class ModelCatalogResponseDto
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("models")]
    public List<PublicLogicalModelDto> Models { get; set; } = [];

    [JsonPropertyName("channels")]
    public List<PublicChannelCatalogDto> Channels { get; set; } = [];
}

/// <summary>公开渠道目录。对应 Go: <c>app.PublicChannelCatalog</c>。</summary>
public sealed class PublicChannelCatalogDto
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("sortOrder")]
    public long SortOrder { get; set; }

    [JsonPropertyName("models")]
    public List<PublicChannelModelDto> Models { get; set; } = [];
}

/// <summary>公开渠道模型。对应 Go: <c>app.PublicChannelModel</c>。</summary>
public sealed class PublicChannelModelDto
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("modelKey")]
    public string ModelKey { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("sortOrder")]
    public long SortOrder { get; set; }

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    [JsonPropertyName("capabilityConfig")]
    [GoOmitEmpty]
    public Dictionary<string, JsonElement>? CapabilityConfig { get; set; }

    [JsonPropertyName("priceTiers")]
    public List<PublicChannelModelPriceTierDto> PriceTiers { get; set; } = [];

    [JsonPropertyName("pricingMode")]
    public string PricingMode { get; set; } = "";

    [JsonPropertyName("displayPrice")]
    [GoOmitEmpty]
    public long? DisplayPrice { get; set; }

    [JsonPropertyName("priceLabel")]
    public string PriceLabel { get; set; } = "";

    [JsonPropertyName("available")]
    public bool Available { get; set; }
}

/// <summary>公开价格档。对应 Go: <c>app.PublicChannelModelPriceTier</c>。</summary>
public sealed class PublicChannelModelPriceTierDto
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("selector")]
    [GoOmitEmpty]
    public Dictionary<string, string>? Selector { get; set; }

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = "";

    [JsonPropertyName("videoSeconds")]
    public long VideoSeconds { get; set; }

    [JsonPropertyName("billingMode")]
    public string BillingMode { get; set; } = "";

    [JsonPropertyName("unitPriceMicrocredits")]
    public long UnitPriceMicrocredits { get; set; }

    [JsonPropertyName("inputTokenPriceMicrocredits")]
    public long InputTokenPriceMicrocredits { get; set; }

    [JsonPropertyName("outputTokenPriceMicrocredits")]
    public long OutputTokenPriceMicrocredits { get; set; }

    [JsonPropertyName("cachedTokenPriceMicrocredits")]
    public long CachedTokenPriceMicrocredits { get; set; }
}

/// <summary>
/// 创作端模型目录。对应 Go: <c>internal/app/model_catalog.go</c>。
/// </summary>
public sealed class ModelCatalogService
{
    private readonly Repository _repository;
    private readonly OpenAICanvas.Platform.FeatureAvailabilityService _features;
    private readonly LogicalModelService _logicalModels;

    public ModelCatalogService(
        Repository repository,
        OpenAICanvas.Platform.FeatureAvailabilityService features,
        LogicalModelService logicalModels)
    {
        _repository = repository;
        _features = features;
        _logicalModels = logicalModels;
    }

    /// <summary>按前台模型开关返回互斥数据形状。对应 Go: <c>ModelCatalog</c>。</summary>
    public async Task<ModelCatalogResponseDto> CatalogAsync(
        ModelRequestIntent? intent, CancellationToken cancellationToken = default)
    {
        bool frontendEnabled = await _features
            .FeatureEnabledAsync(OpenAICanvas.Platform.FeatureNames.FrontendModels, cancellationToken)
            .ConfigureAwait(false);
        ModelCatalogResponseDto response = new();
        if (frontendEnabled)
        {
            IReadOnlyList<PublicLogicalModelDto> models = await _logicalModels
                .PublicLogicalModelsAsync(intent, cancellationToken).ConfigureAwait(false);
            response.Source = "frontend";
            response.Models = models.ToList();
            return response;
        }

        response.Source = "system";
        response.Channels = await PublicSystemChannelCatalogAsync(intent, cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <summary>系统渠道目录（脱敏 + 单模型隔离）。对应 Go: <c>publicSystemChannelCatalog</c>。</summary>
    private async Task<List<PublicChannelCatalogDto>> PublicSystemChannelCatalogAsync(
        ModelRequestIntent? intent, CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelChannel> channels = await _repository
            .SystemChannelsAsync(includeDisabled: true, cancellationToken).ConfigureAwait(false);
        List<PublicChannelCatalogDto> result = [];
        foreach (ModelChannel channel in channels)
        {
            if (!channel.Enabled)
            {
                continue;
            }
            IReadOnlyList<ChannelModel> channelModels = await _repository
                .ChannelModelsAsync(channel.ID, enabledOnly: false, cancellationToken).ConfigureAwait(false);
            List<PublicChannelModelDto> publicModels = [];
            foreach (ChannelModel channelModel in channelModels)
            {
                if (!channelModel.Enabled)
                {
                    continue;
                }
                // 目录是读路径：单个损坏模型隔离并记录诊断，不拖垮其余模型。
                if (intent is not null)
                {
                    (bool matched, bool valid) = await ChannelModelMatchesIntentAsync(channelModel, intent, cancellationToken)
                        .ConfigureAwait(false);
                    if (!valid)
                    {
                        Console.Error.WriteLine($"system channel model omitted from catalog id={channelModel.ID}: invalid capability");
                        continue;
                    }
                    if (!matched)
                    {
                        continue;
                    }
                }
                PublicChannelModelDto? publicModel = SanitizeChannelModel(channelModel);
                if (publicModel is null)
                {
                    Console.Error.WriteLine($"system channel model omitted from catalog id={channelModel.ID}");
                    continue;
                }
                publicModels.Add(publicModel);
            }
            if (publicModels.Count > 0)
            {
                result.Add(new PublicChannelCatalogDto
                {
                    ID = channel.ID,
                    Name = channel.Name,
                    DisplayName = channel.Name,
                    SortOrder = channel.SortOrder,
                    Models = publicModels,
                });
            }
        }
        return result;
    }

    /// <summary>渠道模型脱敏投影。对应 Go: <c>sanitizeChannelModel</c>。损坏返回 null 由上层隔离。</summary>
    private static PublicChannelModelDto? SanitizeChannelModel(ChannelModel cm)
    {
        List<PublicChannelModelPriceTierDto> publicTiers = [];
        foreach (ChannelModelPriceTier tier in cm.PriceTiers)
        {
            if (!tier.Enabled || !tier.PriceConfigured
                || !ValidatePriceTierPrice(tier, cm.Capability, cm.Protocol))
            {
                continue;
            }
            publicTiers.Add(new PublicChannelModelPriceTierDto
            {
                ID = tier.ID,
                Selector = ModelSku.DecodeSkuSelector(tier.SelectorJSON),
                Resolution = tier.Resolution,
                VideoSeconds = tier.VideoSeconds,
                BillingMode = tier.BillingMode,
                UnitPriceMicrocredits = tier.UnitPriceMicrocredits,
                InputTokenPriceMicrocredits = tier.InputTokenPriceMicrocredits,
                OutputTokenPriceMicrocredits = tier.OutputTokenPriceMicrocredits,
                CachedTokenPriceMicrocredits = tier.CachedTokenPriceMicrocredits,
            });
        }

        (string pricingMode, long? displayPrice, string priceLabel) =
            ComputePriceDisplay(publicTiers);

        Dictionary<string, JsonElement>? capabilityConfig;
        try
        {
            capabilityConfig = CapabilityConfigMap(cm);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return new PublicChannelModelDto
        {
            ID = cm.ID,
            ModelKey = cm.ModelKey,
            DisplayName = cm.DisplayName,
            SortOrder = cm.SortOrder,
            Icon = cm.Icon,
            Capability = cm.Capability,
            Protocol = cm.Protocol,
            CapabilityConfig = capabilityConfig,
            PriceTiers = publicTiers,
            PricingMode = pricingMode,
            DisplayPrice = displayPrice,
            PriceLabel = priceLabel,
            Available = publicTiers.Count > 0,
        };
    }

    /// <summary>展示价格与可用性从同一批有效档位派生。对应 Go: <c>computeChannelModelPriceDisplay</c>。</summary>
    private static (string PricingMode, long? DisplayPrice, string PriceLabel) ComputePriceDisplay(
        List<PublicChannelModelPriceTierDto> priceTiers)
    {
        if (priceTiers.Count == 0)
        {
            return ("provider", null, "未配置");
        }
        if (priceTiers.Count == 1)
        {
            PublicChannelModelPriceTierDto tier = priceTiers[0];
            long price = TierDisplayPrice(tier);
            if (price > 0)
            {
                return ("provider", price, "");
            }
        }
        return ("provider", null, "按渠道规格计费");
    }

    private static long TierDisplayPrice(PublicChannelModelPriceTierDto tier)
    {
        if (tier.BillingMode is "fixed_request" or "per_second")
        {
            return tier.UnitPriceMicrocredits;
        }
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

    /// <summary>目录过滤使用与任务 admission 相同的能力合同。对应 Go: <c>channelModelMatchesIntent</c>。</summary>
    private static async Task<(bool Matched, bool Valid)> ChannelModelMatchesIntentAsync(
        ChannelModel cm, ModelRequestIntent intent, CancellationToken cancellationToken)
    {
        if (CapabilitySpecOps.NormalizeCapability(intent.Capability).Length > 0
            && CapabilitySpecOps.NormalizeCapability(cm.Capability)
                != CapabilitySpecOps.NormalizeCapability(intent.Capability))
        {
            return (false, true);
        }
        if (CapabilitySpecOps.NormalizeCapability(cm.Capability) == "audio")
        {
            return (true, true);
        }
        ModelCapabilityConfig? config;
        try
        {
            config = NormalizedCapability(cm);
        }
        catch (InvalidOperationException)
        {
            return (false, false);
        }
        if (config is null)
        {
            return (true, true);
        }
        CapabilitySpec spec = ModelCapabilityConfigOps.CapabilitySpecFromModelCapabilityConfig(config, cm.Capability);
        CapabilityMatch match = CapabilitySpecOps.MatchCapability(spec, intent);
        await Task.CompletedTask.ConfigureAwait(false);
        return (match.Matched, true);
    }

    /// <summary>从持久化记录恢复权威能力合同。对应 Go: <c>normalizedChannelModelCapability</c>。</summary>
    private static ModelCapabilityConfig? NormalizedCapability(ChannelModel channelModel)
    {
        string capability = CapabilitySpecOps.NormalizeCapability(channelModel.Capability);
        if (capability == "audio")
        {
            return null;
        }
        if (capability is not ("text" or "image" or "video"))
        {
            throw new InvalidOperationException($"不支持的渠道模型能力：{channelModel.Capability}");
        }
        ModelCapabilityConfig? config;
        try
        {
            config = ModelCapabilityConfigOps.DecodeModelCapabilityConfig(channelModel.CapabilityConfigJSON);
        }
        catch (AppError error)
        {
            throw new InvalidOperationException($"解析渠道模型能力配置失败：{error.Message}");
        }
        return ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
            capability,
            channelModel.Protocol,
            LogicalModelService.FirstNonEmpty(channelModel.ProviderModelKey, channelModel.ModelKey),
            config);
    }

    /// <summary>能力配置转 JSON 对象。对应 Go: <c>modelCapabilityConfigToMap</c>。</summary>
    private static Dictionary<string, JsonElement>? CapabilityConfigMap(ChannelModel cm)
    {
        ModelCapabilityConfig? normalized = NormalizedCapability(cm);
        if (normalized is null)
        {
            return null;
        }
        JsonElement element = JsonSerializer.SerializeToElement(normalized);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return new Dictionary<string, JsonElement>(
            element.EnumerateObject().Select(property =>
                new KeyValuePair<string, JsonElement>(property.Name, property.Value.Clone())),
            StringComparer.Ordinal);
    }

    /// <summary>价格档价格自洽校验。对应 Go: <c>ValidatePriceTierPrice</c>。</summary>
    private static bool ValidatePriceTierPrice(
        ChannelModelPriceTier tier, string capability, string protocol)
    {
        switch (tier.BillingMode)
        {
            case "fixed_request":
            case "per_second":
                return tier.UnitPriceMicrocredits >= 0;
            case "token":
                if (capability == "video")
                {
                    return protocol == ChannelInterfaceType.ChannelInterfaceVolcengineArkVideo
                        && tier.InputTokenPriceMicrocredits >= 0
                        && tier.OutputTokenPriceMicrocredits >= 0
                        && tier.CachedTokenPriceMicrocredits >= 0;
                }
                if (capability != "" && capability != "text")
                {
                    return false;
                }
                return tier.InputTokenPriceMicrocredits >= 0
                    && tier.OutputTokenPriceMicrocredits >= 0
                    && tier.CachedTokenPriceMicrocredits >= 0;
            default:
                return false;
        }
    }
}
