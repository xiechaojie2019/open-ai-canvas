#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAICanvas.Providers;

/// <summary>
/// 供应商出站请求体定义。
/// 对应 Go: <c>internal/app/provider_request_types.go</c>。
/// </summary>
/// <remarks>
/// 这些是<b>发给上游</b>的载荷，字段名与 <c>omitempty</c> 必须逐字对齐 Go 的 json tag ——
/// 上游对多余字段常常直接报错。所有可选字段一律用 <c>JsonIgnore(WhenWritingNull)</c>
/// 模拟 Go 的 <c>omitempty</c>（注意：Go 的 omitempty 对空数组/空串同样省略，
/// 因此这些字段在赋值阶段就要保证"不适用时为 null"）。
/// </remarks>
public static class ProviderRequestTypes
{
    /// <summary>序列化选项：不转义非 ASCII、可选字段为 null 时省略。对应 Go 的 <c>json.Marshal</c>。</summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 把任意 DTO 转成 Go 风格的 <c>map[string]any</c>（供声明式协议改写字段）。
    /// 对应 Go: <c>requestAsMap</c>。
    /// </summary>
    public static Dictionary<string, object?>? AsMap<T>(T value)
    {
        string json = JsonSerializer.Serialize(value, WriteOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        return OpenAICanvas.Outbound.JsonFields.FromElement(document.RootElement)
            as Dictionary<string, object?>;
    }
}

/// <summary>Seedance 视频生成请求。对应 Go: <c>seedanceVideosRequest</c>。</summary>
public sealed class SeedanceVideosRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("aspect_ratio")]
    public string AspectRatio { get; set; } = "";

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("generate_audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? GenerateAudio { get; set; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageURL { get; set; }

    [JsonPropertyName("reference_image_urls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ReferenceImageURLs { get; set; }

    [JsonPropertyName("image_urls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ImageURLs { get; set; }

    [JsonPropertyName("reference_videos")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ReferenceVideos { get; set; }

    [JsonPropertyName("reference_audios")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ReferenceAudios { get; set; }
}

/// <summary>Grok（xAI）图片生成请求。对应 Go: <c>grokImageRequest</c>。</summary>
public sealed class GrokImageRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GrokImageInput? Image { get; set; }

    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("response_format")]
    public string ResponseFormat { get; set; } = "";

    [JsonPropertyName("aspect_ratio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AspectRatio { get; set; }

    /// <summary>对应 xAI / grok2api 的 resolution（常见 1k / 2k）。</summary>
    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Resolution { get; set; }
}

/// <summary>对应 Go: <c>grokImageInput</c>。</summary>
public sealed class GrokImageInput
{
    [JsonPropertyName("url")]
    public string URL { get; set; } = "";
}

/// <summary>Gemini 图片生成请求。对应 Go: <c>geminiImageRequest</c>。</summary>
public sealed class GeminiImageRequest
{
    [JsonPropertyName("contents")]
    public List<GeminiImageContent> Contents { get; set; } = [];

    [JsonPropertyName("systemInstruction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiImageContent? SystemInstruction { get; set; }

    [JsonPropertyName("generationConfig")]
    public GeminiImageGenerationConfig GenerationConfig { get; set; } = new();
}

/// <summary>对应 Go: <c>geminiImageContent</c>。</summary>
public sealed class GeminiImageContent
{
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    [JsonPropertyName("parts")]
    public List<GeminiImageContentPart> Parts { get; set; } = [];
}

/// <summary>对应 Go: <c>geminiImageContentPart</c>。</summary>
public sealed class GeminiImageContentPart
{
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("inlineData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiImageInlineData? InlineData { get; set; }
}

/// <summary>对应 Go: <c>geminiImageInlineData</c>。</summary>
public sealed class GeminiImageInlineData
{
    [JsonPropertyName("mimeType")]
    public string MIMEType { get; set; } = "";

    [JsonPropertyName("data")]
    public string Data { get; set; } = "";
}

/// <summary>对应 Go: <c>geminiImageGenerationConfig</c>。</summary>
public sealed class GeminiImageGenerationConfig
{
    [JsonPropertyName("responseModalities")]
    public List<string> ResponseModalities { get; set; } = [];

    [JsonPropertyName("imageConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiImageConfig? ImageConfig { get; set; }
}

/// <summary>对应 Go: <c>geminiImageConfig</c>。</summary>
public sealed class GeminiImageConfig
{
    [JsonPropertyName("aspectRatio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AspectRatio { get; set; }

    [JsonPropertyName("imageSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageSize { get; set; }
}

/// <summary>Seedance Agent 规划请求。对应 Go: <c>seedanceAgentPlanRequest</c>。</summary>
public sealed class SeedanceAgentPlanRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("content")]
    public List<Dictionary<string, object?>> Content { get; set; } = [];

    [JsonPropertyName("ratio")]
    public string Ratio { get; set; } = "";

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = "";

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("generate_audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? GenerateAudio { get; set; }

    [JsonPropertyName("watermark")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Watermark { get; set; }
}
