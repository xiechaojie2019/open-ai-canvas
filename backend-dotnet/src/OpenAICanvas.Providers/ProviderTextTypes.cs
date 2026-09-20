#nullable enable
using System.Text.Json.Serialization;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Providers;

/// <summary>
/// 文本生成的出站渠道配置。
/// 对应 Go: <c>internal/app/provider.go</c> 的 <c>providerConfig</c>。
/// </summary>
/// <remarks>
/// 只保留文本协议实际读取的字段；图片/视频/工作流专属字段（size / quality /
/// workflowId 等）随各自模块（4.7–4.12）落地时再补。
/// </remarks>
public sealed class ProviderConfig
{
    [JsonPropertyName("channelId")]
    public string ChannelID { get; set; } = "";

    [JsonPropertyName("channelModelKey")]
    public string ChannelModelKey { get; set; } = "";

    [JsonPropertyName("priceTierId")]
    public string PriceTierID { get; set; } = "";

    [JsonPropertyName("providerModelKey")]
    public string ProviderModelKey { get; set; } = "";

    [JsonPropertyName("apiFormat")]
    public string APIFormat { get; set; } = "";

    [JsonPropertyName("interfaceType")]
    public string InterfaceType { get; set; } = "";

    [JsonPropertyName("baseUrl")]
    public string BaseURL { get; set; } = "";

    [JsonPropertyName("apiKey")]
    public string APIKey { get; set; } = "";

    [JsonPropertyName("secretKey")]
    public string SecretKey { get; set; } = "";

    [JsonPropertyName("headers")]
    public List<OutboundHeader> Headers { get; set; } = [];

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("systemPrompt")]
    public string SystemPrompt { get; set; } = "";
}

/// <summary>文本历史消息。对应 Go: <c>providerTextMessage</c>。</summary>
public sealed class ProviderTextMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

/// <summary>
/// 文本任务结果（正文 + 推理摘要）。
/// 对应 Go: <c>providerTextResult</c>。
/// </summary>
public sealed record ProviderTextResult(string Text, string Reasoning);

/// <summary>文本任务的思考模式开关。对应 Go: <c>canvasTextOptions</c>。</summary>
public sealed class CanvasTextOptions
{
    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("thinking")]
    public bool Thinking { get; set; }
}
