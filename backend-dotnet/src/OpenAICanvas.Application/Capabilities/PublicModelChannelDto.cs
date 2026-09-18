#nullable enable
using System.Text.Json.Serialization;
using OpenAICanvas.Application.Capabilities;

namespace OpenAICanvas.Application;

/// <summary>系统渠道公开投影。对应 Go: <c>app.PublicModelChannel</c>。</summary>
public sealed class PublicModelChannelDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("userId")]
    public string UserID { get; init; } = "";

    [JsonPropertyName("scope")]
    public string Scope { get; init; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("publicAlias")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string PublicAlias { get; init; } = "";

    [JsonPropertyName("sortOrder")]
    public long SortOrder { get; init; }

    [JsonPropertyName("baseUrl")]
    public string BaseURL { get; init; } = "";

    [JsonPropertyName("apiKey")]
    public string APIKey { get; init; } = "";

    [JsonPropertyName("apiFormat")]
    public string APIFormat { get; init; } = "";

    [JsonPropertyName("concurrencyLimit")]
    public long ConcurrencyLimit { get; init; }

    [JsonPropertyName("models")]
    public List<string> Models { get; init; } = [];

    [JsonPropertyName("modelCosts")]
    public List<PublicChannelModelPriceDto> ModelCosts { get; init; } = [];

    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OutboundHeaderDto>? Headers { get; init; }

    [JsonPropertyName("hasApiKey")]
    public bool HasAPIKey { get; init; }

    [JsonPropertyName("hasSecretKey")]
    public bool HasSecretKey { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }
}

/// <summary>渠道模型价格公开投影。对应 Go: <c>app.PublicChannelModelPrice</c>。</summary>
public sealed class PublicChannelModelPriceDto
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "";

    [JsonPropertyName("capability")]
    public string Capability { get; init; } = "";

    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = "";

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

    [JsonPropertyName("capabilityConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelCapabilityConfig? CapabilityConfig { get; init; }
}

/// <summary>出站请求头。对应 Go: <c>outbound.OutboundHeader</c>。</summary>
public sealed class OutboundHeaderDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("value")]
    public string Value { get; init; } = "";
}
