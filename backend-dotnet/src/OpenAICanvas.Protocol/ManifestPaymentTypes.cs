#nullable enable
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Protocol;

/// <summary>
/// 插件清单里与支付相关的部分。对应 Go: <c>internal/protocol/types.go</c>。
/// </summary>
/// <remarks>
/// 完整的 <c>Manifest</c> 还有 providers / workflows 等贡献点，属于阶段 10（插件系统）。
/// 这里只落地支付渠道当前需要的子集，避免提前引入整棵类型树。
/// </remarks>
public sealed class ManifestConfiguration
{
    [JsonPropertyName("fields")]
    [GoOmitEmpty]
    public List<ManifestField> Fields { get; set; } = [];
}

/// <summary>配置项。对应 Go: <c>protocol.ManifestField</c>。前端据此渲染设置表单。</summary>
public sealed class ManifestField
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("label")]
    [GoOmitEmpty]
    public string Label { get; set; } = "";

    [JsonPropertyName("required")]
    [GoOmitEmpty]
    public bool Required { get; set; }

    /// <summary>敏感字段：管理端只回 <c>secretConfigured</c>，不回明文。</summary>
    [JsonPropertyName("secret")]
    [GoOmitEmpty]
    public bool Secret { get; set; }

    [JsonPropertyName("default")]
    [GoOmitEmpty]
    public object? Default { get; set; }

    [JsonPropertyName("description")]
    [GoOmitEmpty]
    public string Description { get; set; } = "";

    [JsonPropertyName("values")]
    [GoOmitEmpty]
    public List<string> Values { get; set; } = [];
}

/// <summary>支付渠道贡献点。对应 Go: <c>protocol.ManifestPaymentProvider</c>。</summary>
public sealed class ManifestPaymentProvider
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("checkoutMode")]
    public string CheckoutMode { get; set; } = "";

    [JsonPropertyName("expiryPolicy")]
    public ManifestPaymentExpiryPolicy ExpiryPolicy { get; set; } = new();

    [JsonPropertyName("identityFields")]
    [GoOmitEmpty]
    public List<string> IdentityFields { get; set; } = [];

    /// <summary>Go 的 omitempty 对 struct 无效，这两个字段总会输出。</summary>
    [JsonPropertyName("notificationSuccess")]
    public ManifestPaymentResponse NotificationSuccess { get; set; } = new();

    [JsonPropertyName("notificationFailure")]
    public ManifestPaymentResponse NotificationFailure { get; set; } = new();
}

/// <summary>未支付订单的关闭时限。对应 Go: <c>protocol.ManifestPaymentExpiryPolicy</c>。</summary>
public sealed class ManifestPaymentExpiryPolicy
{
    [JsonPropertyName("defaultMinutes")]
    public int DefaultMinutes { get; set; }

    [JsonPropertyName("minMinutes")]
    public int MinMinutes { get; set; }

    [JsonPropertyName("maxMinutes")]
    public int MaxMinutes { get; set; }
}

/// <summary>回调应答。对应 Go: <c>protocol.ManifestPaymentResponse</c>。</summary>
public sealed class ManifestPaymentResponse
{
    [JsonPropertyName("status")]
    [GoOmitEmpty]
    public int Status { get; set; }

    [JsonPropertyName("contentType")]
    [GoOmitEmpty]
    public string ContentType { get; set; } = "";

    [JsonPropertyName("body")]
    [GoOmitEmpty]
    public string Body { get; set; } = "";
}
