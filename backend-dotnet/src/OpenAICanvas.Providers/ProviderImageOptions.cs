#nullable enable
using System.Text.Json.Serialization;

namespace OpenAICanvas.Providers;

/// <summary>
/// 图片参数能力声明（尺寸/质量/透明背景/响应格式）。
/// 对应 Go: <c>internal/app/model_capability.go</c> 的 <c>ImageCapabilityConfig</c>。
/// </summary>
/// <remarks>
/// 与 <c>Application/Capabilities</c> 中的同名类型是<b>刻意的镜像</b>：
/// <c>Providers</c> 不能引用 <c>Application</c>（会形成循环依赖），
/// 而图片任务需要按能力裁剪请求体（不支持的参数必须省略，不能报错）。
/// 两者字段与 json tag 必须逐字一致，改动时需同步。
/// </remarks>
public sealed class ImageCapabilityConfig
{
    [JsonPropertyName("references")]
    public ImageReferenceConfig References { get; set; } = new();

    [JsonPropertyName("size")]
    public ImageSizeConfig Size { get; set; } = new();

    [JsonPropertyName("quality")]
    public ImageQualityConfig Quality { get; set; } = new();

    [JsonPropertyName("transparentBackground")]
    public VideoBooleanConfig TransparentBackground { get; set; } = new();

    [JsonPropertyName("responseFormat")]
    public ParameterSupport ResponseFormat { get; set; } = new();

    [JsonPropertyName("outputFormat")]
    public ParameterSupport OutputFormat { get; set; } = new();

    [JsonPropertyName("maxOutputs")]
    public int MaxOutputs { get; set; }
}

/// <summary>对应 Go: <c>ImageReferenceConfig</c>。</summary>
public sealed class ImageReferenceConfig
{
    [JsonPropertyName("promptMaxChars")]
    public int PromptMaxChars { get; set; }

    [JsonPropertyName("maxImages")]
    public int MaxImages { get; set; }

    [JsonPropertyName("maxImageBytes")]
    public long MaxImageBytes { get; set; }

    [JsonPropertyName("maskSupported")]
    public bool MaskSupported { get; set; }
}

/// <summary>对应 Go: <c>ImageSizeConfig</c>。</summary>
public sealed class ImageSizeConfig
{
    [JsonPropertyName("parameter")]
    public string Parameter { get; set; } = "";

    [JsonPropertyName("values")]
    public List<string> Values { get; set; } = [];

    [JsonPropertyName("default")]
    public string Default { get; set; } = "";

    [JsonPropertyName("allowCustom")]
    public bool AllowCustom { get; set; }

    [JsonPropertyName("presets")]
    public List<ImageSizePreset> Presets { get; set; } = [];
}

/// <summary>对应 Go: <c>ImageSizePreset</c>。</summary>
public sealed class ImageSizePreset
{
    [JsonPropertyName("tier")]
    public string Tier { get; set; } = "";

    [JsonPropertyName("ratio")]
    public string Ratio { get; set; } = "";

    [JsonPropertyName("size")]
    public string Size { get; set; } = "";

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

/// <summary>对应 Go: <c>ImageQualityConfig</c>。</summary>
public sealed class ImageQualityConfig
{
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    [JsonPropertyName("values")]
    public List<string> Values { get; set; } = [];

    [JsonPropertyName("default")]
    public string Default { get; set; } = "";
}

/// <summary>对应 Go: <c>ParameterSupport</c>。</summary>
public sealed class ParameterSupport
{
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }
}

/// <summary>对应 Go: <c>VideoBooleanConfig</c>（被图片的透明背景复用）。</summary>
public sealed class VideoBooleanConfig
{
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    [JsonPropertyName("default")]
    public bool Default { get; set; }
}
