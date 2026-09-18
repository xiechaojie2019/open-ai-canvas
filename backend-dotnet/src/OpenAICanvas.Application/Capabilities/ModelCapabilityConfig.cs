#nullable enable
using System.Text.Json.Serialization;

namespace OpenAICanvas.Application.Capabilities;

/// <summary>模型能力配置根对象。对应 Go: <c>model_capability.ModelCapabilityConfig</c>。</summary>
public sealed class ModelCapabilityConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextCapabilityConfig? Text { get; set; }

    [JsonPropertyName("image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageCapabilityConfig? Image { get; set; }

    [JsonPropertyName("video")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VideoCapabilityConfig? Video { get; set; }
}

public sealed class TextCapabilityConfig
{
    [JsonPropertyName("streaming")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Streaming { get; set; }

    [JsonPropertyName("references")]
    public TextReferenceConfig References { get; set; } = new();
}

public sealed class TextReferenceConfig
{
    [JsonPropertyName("promptMaxChars")]
    public int PromptMaxChars { get; set; }

    [JsonPropertyName("maxImages")]
    public int MaxImages { get; set; }

    [JsonPropertyName("maxImageBytes")]
    public long MaxImageBytes { get; set; }

    [JsonPropertyName("maxVideos")]
    public int MaxVideos { get; set; }

    [JsonPropertyName("maxVideoBytes")]
    public long MaxVideoBytes { get; set; }
}

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ImageSizePreset>? Presets { get; set; }
}

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

public sealed class ImageQualityConfig
{
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    [JsonPropertyName("values")]
    public List<string> Values { get; set; } = [];

    [JsonPropertyName("default")]
    public string Default { get; set; } = "";
}

public sealed class ParameterSupport
{
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }
}

public sealed class VideoCapabilityConfig
{
    [JsonPropertyName("references")]
    public VideoReferenceConfig References { get; set; } = new();

    [JsonPropertyName("duration")]
    public VideoDurationConfig Duration { get; set; } = new();

    [JsonPropertyName("durationSupported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DurationSupported { get; set; }

    [JsonPropertyName("ratios")]
    public List<string> Ratios { get; set; } = [];

    [JsonPropertyName("defaultRatio")]
    public string DefaultRatio { get; set; } = "";

    [JsonPropertyName("resolutions")]
    public List<string> Resolutions { get; set; } = [];

    [JsonPropertyName("defaultResolution")]
    public string DefaultResolution { get; set; } = "";

    [JsonPropertyName("generateAudio")]
    public VideoBooleanConfig GenerateAudio { get; set; } = new();

    [JsonPropertyName("watermark")]
    public VideoBooleanConfig Watermark { get; set; } = new();

    [JsonPropertyName("operations")]
    public List<string> Operations { get; set; } = [];

    [JsonPropertyName("defaultOperation")]
    public string DefaultOperation { get; set; } = "";
}

public sealed class VideoReferenceConfig
{
    [JsonPropertyName("promptMaxChars")]
    public int PromptMaxChars { get; set; }

    [JsonPropertyName("minImages")]
    public int MinImages { get; set; }

    [JsonPropertyName("maxImages")]
    public int MaxImages { get; set; }

    [JsonPropertyName("maxImageBytes")]
    public long MaxImageBytes { get; set; }

    [JsonPropertyName("maxVideos")]
    public int MaxVideos { get; set; }

    [JsonPropertyName("maxVideoBytes")]
    public long MaxVideoBytes { get; set; }

    [JsonPropertyName("maxVideoDurationSeconds")]
    public int MaxVideoDuration { get; set; }

    [JsonPropertyName("maxAudios")]
    public int MaxAudios { get; set; }

    [JsonPropertyName("maxAudioBytes")]
    public long MaxAudioBytes { get; set; }

    [JsonPropertyName("maxAudioDurationSeconds")]
    public int MaxAudioDuration { get; set; }
}

public sealed class VideoDurationConfig
{
    [JsonPropertyName("selection")]
    public string Selection { get; set; } = "";

    [JsonPropertyName("min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Min { get; set; }

    [JsonPropertyName("max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Max { get; set; }

    [JsonPropertyName("step")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Step { get; set; }

    [JsonPropertyName("values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Values { get; set; }

    [JsonPropertyName("default")]
    public int Default { get; set; }
}

public sealed class VideoBooleanConfig
{
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    [JsonPropertyName("default")]
    public bool Default { get; set; }
}
