#nullable enable
using System.Text.Json.Serialization;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Providers;

/// <summary>
/// 视频能力声明（分辨率、音频/水印开关、参考素材上下限）。
/// 对应 Go: <c>internal/app/model_capability.go</c> 的 <c>VideoCapabilityConfig</c>。
/// </summary>
/// <remarks>
/// 与 <c>Application/Capabilities</c> 的同名类型是刻意镜像（<c>Providers</c> 不能引用
/// <c>Application</c>），字段与 json tag 必须同步。
/// </remarks>
public sealed class VideoCapabilityConfig
{
    [JsonPropertyName("references")]
    public VideoReferenceConfig References { get; set; } = new();

    [JsonPropertyName("resolutions")]
    public List<string> Resolutions { get; set; } = [];

    [JsonPropertyName("generateAudio")]
    public VideoBooleanConfig GenerateAudio { get; set; } = new();

    [JsonPropertyName("watermark")]
    public VideoBooleanConfig Watermark { get; set; } = new();
}

/// <summary>对应 Go: <c>VideoReferenceConfig</c>（参考素材数量与大小上下限）。</summary>
public sealed class VideoReferenceConfig
{
    [JsonPropertyName("promptMaxChars")]
    public int PromptMaxChars { get; set; }

    [JsonPropertyName("minImages")]
    public int MinImages { get; set; }

    [JsonPropertyName("maxImages")]
    public int MaxImages { get; set; }

    [JsonPropertyName("maxVideos")]
    public int MaxVideos { get; set; }

    [JsonPropertyName("maxAudios")]
    public int MaxAudios { get; set; }

    [JsonPropertyName("maxImageBytes")]
    public long MaxImageBytes { get; set; }

    [JsonPropertyName("maxVideoBytes")]
    public long MaxVideoBytes { get; set; }

    [JsonPropertyName("maxAudioBytes")]
    public long MaxAudioBytes { get; set; }
}

/// <summary>
/// 视频请求参数归一化与能力裁剪。
/// 对应 Go: <c>provider_video_options.go</c> 的
/// <c>videoResolutionNameRequest</c> / <c>normalizeSeedanceDuration</c> /
/// <c>normalizeSeedanceRatio</c> / <c>normalizeSeedanceVideosRatio</c> /
/// <c>normalizeSeedanceResolution</c> / <c>parseBool</c> / <c>parseFloat</c>。
/// </summary>
public static class ProviderVideoOptions
{
    /// <summary>
    /// 把请求的分辨率名解析为能力声明中的"原名"；无法匹配时返回空串。
    /// 对应 Go: <c>videoResolutionNameRequest</c>。
    /// </summary>
    /// <remarks>
    /// 候选值包含原始值、归一化后的值，以及 4k ↔ 2160p 的互相转换 ——
    /// 渠道声明的写法（<c>4k</c> 还是 <c>2160p</c>）不可控，两种都要认。
    /// </remarks>
    public static string VideoResolutionNameRequest(VideoCapabilityConfig? profile, string? value)
    {
        string requested = (value ?? "").Trim().ToLowerInvariant();
        if (requested.Length == 0
            || requested is "auto" or "default" or "medium" or "high")
        {
            return "";
        }
        if (profile is null || profile.Resolutions.Count == 0)
        {
            return "";
        }

        List<string> candidates =
        [
            requested,
            ProviderImageOptions.NormalizeVideoResolution(requested).ToLowerInvariant(),
        ];
        if (requested == "4k")
        {
            candidates.Add("2160p");
        }
        if (requested is "2160" or "2160p")
        {
            candidates.Add("4k");
        }

        foreach (string supportedValue in profile.Resolutions)
        {
            string supported = supportedValue.Trim();
            foreach (string candidate in candidates)
            {
                // capacity 声明支持时保留原始写法（渠道可能要求 "4K" 这种大写）。
                if (string.Equals(supported, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return supported;
                }
            }
        }
        return "";
    }

    /// <summary>
    /// 固定分辨率渠道：能力只声明一个分辨率时直接改写请求值。
    /// 对应 Go: <c>applyFixedVideoResolution</c>。
    /// </summary>
    public static void ApplyFixedVideoResolution(ProviderConfig config, VideoCapabilityConfig? profile)
    {
        if (profile is null || profile.Resolutions.Count != 1)
        {
            return;
        }
        string resolution = VideoResolutionNameRequest(profile, profile.Resolutions[0]);
        if (resolution.Length > 0)
        {
            config.VQuality = resolution;
        }
    }

    /// <summary>
    /// Seedance 时长归一化：<c>-1</c> 原样保留（表示"由模型决定"），
    /// 非正数或非法值回落 <c>5</c>。
    /// 对应 Go: <c>normalizeSeedanceDuration</c>。
    /// </summary>
    public static int NormalizeSeedanceDuration(string? value)
    {
        string trimmed = (value ?? "").Trim();
        if (trimmed == "-1")
        {
            return -1;
        }
        return int.TryParse(trimmed, out int seconds) && seconds > 0 ? seconds : 5;
    }

    /// <summary>
    /// Seedance 画布比例：白名单外一律回落 <c>adaptive</c>。
    /// 对应 Go: <c>normalizeSeedanceRatio</c>。
    /// </summary>
    public static string NormalizeSeedanceRatio(string? value)
    {
        string trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0 || trimmed is "auto" or "adaptive")
        {
            return "adaptive";
        }
        return trimmed is "16:9" or "9:16" or "1:1" or "4:3" or "3:4" or "21:9"
            ? trimmed
            : "adaptive";
    }

    /// <summary>
    /// <c>/videos</c> 接口不接受 <c>adaptive</c>，回落 <c>16:9</c>。
    /// 对应 Go: <c>normalizeSeedanceVideosRatio</c>。
    /// </summary>
    public static string NormalizeSeedanceVideosRatio(string? value)
    {
        string ratio = NormalizeSeedanceRatio(value);
        return ratio == "adaptive" ? "16:9" : ratio;
    }

    /// <summary>
    /// Seedance 分辨率归一化。对应 Go: <c>normalizeSeedanceResolution</c>。
    /// </summary>
    /// <remarks>
    /// <b>fast 模型不支持 1080p/2160p</b>，会被压回 720p —— 上游会直接报错，
    /// 这里提前修正以免浪费一次计费请求。
    /// </remarks>
    public static string NormalizeSeedanceResolution(string? value, string? model)
    {
        string raw = (value ?? "").Trim();
        string resolution = raw.EndsWith('p') ? raw[..^1] : raw;
        if (resolution.Equals("4k", StringComparison.OrdinalIgnoreCase))
        {
            resolution = "2160";
        }
        if (resolution is not ("480" or "720" or "1080" or "2160"))
        {
            resolution = raw == "low" ? "480" : "720";
        }
        string lowerModel = (model ?? "").ToLowerInvariant();
        if (lowerModel.Contains("fast", StringComparison.Ordinal) && resolution is "1080" or "2160")
        {
            resolution = "720";
        }
        return resolution + "p";
    }

    /// <summary>
    /// 三态布尔解析：只认 <c>true</c>/<c>false</c>，其余回落给定默认值。
    /// 对应 Go: <c>parseBool(value, fallback)</c>。
    /// </summary>
    /// <remarks>
    /// 与 <c>ProviderProtocolPayload.ParseBool</c>（只认真值、其余为 false）语义不同 ——
    /// 这里默认值由调用方指定。
    /// </remarks>
    public static bool ParseBool(string? value, bool fallback) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => fallback,
        };

    /// <summary>
    /// 浮点解析：非法值或 <c>0</c> 都回落默认值。对应 Go: <c>parseFloat</c>。
    /// </summary>
    public static double ParseFloat(string? value, double fallback) =>
        double.TryParse((value ?? "").Trim(), out double number) && number != 0 ? number : fallback;

    /// <summary>
    /// 是否应发送 OpenAI 风格视频接口的参考图。
    /// 对应 Go: <c>shouldSendNewAPIVideoImages</c>。
    /// </summary>
    /// <remarks>编辑操作是 <c>text_to_video</c> 时不发送参考图（纯文生视频）。</remarks>
    public static bool ShouldSendNewApiVideoImages(
        IReadOnlyDictionary<string, object?>? metadata, IReadOnlyList<ProviderMedia> referenceImages)
    {
        if (metadata is null)
        {
            return true;
        }
        return ProviderHelpers.MetadataString(metadata, "videoEditOperation") != "text_to_video";
    }

    /// <summary>
    /// 视频参考图的角色判定。对应 Go: <c>videoImageRoleOrDefault</c>。
    /// </summary>
    /// <remarks>
    /// 注意与 <c>ProviderProtocolPayload</c> 的 <c>start_frame</c>/<c>end_frame</c> 命名不同：
    /// 这里是 Seedance 的 <c>first_frame</c>/<c>last_frame</c>。
    /// </remarks>
    public static string VideoImageRoleOrDefault(
        IReadOnlyDictionary<string, object?>? metadata, ProviderMedia image, string fallback)
    {
        if (ProviderHelpers.MetadataString(metadata, "videoEditOperation") == "reference_to_video")
        {
            return "reference_image";
        }
        string id = ProviderHelpers.MetadataString(metadata, "videoStartFrameNodeId");
        if (id.Length > 0 && image.ID == id)
        {
            return "first_frame";
        }
        id = ProviderHelpers.MetadataString(metadata, "videoEndFrameNodeId");
        if (id.Length > 0 && image.ID == id)
        {
            return "last_frame";
        }
        return fallback;
    }

    /// <summary>
    /// 按"首帧、尾帧、普通参考图"重排 <c>image_urls</c>。
    /// 对应 Go: <c>videoFrameImageURLs</c>。
    /// </summary>
    /// <remarks>
    /// 未配置首尾帧时返回空列表（调用方据此走 <c>image_url</c> 单图分支）。
    /// 已配置的节点 ID 找不到对应参考图时<b>直接报错</b> ——
    /// 静默丢帧会让用户以为首尾帧生效了。
    /// </remarks>
    public static List<string> VideoFrameImageUrls(
        IReadOnlyDictionary<string, object?>? metadata,
        IReadOnlyList<ProviderMedia> referenceImages,
        IReadOnlyList<string> imageUrls)
    {
        if (ProviderHelpers.MetadataString(metadata, "videoEditOperation") == "reference_to_video")
        {
            return [];
        }
        string startFrameID = ProviderHelpers.MetadataString(metadata, "videoStartFrameNodeId");
        string endFrameID = ProviderHelpers.MetadataString(metadata, "videoEndFrameNodeId");
        if (startFrameID.Length == 0 && endFrameID.Length == 0)
        {
            return [];
        }

        List<string> ordered = [];
        bool[] used = new bool[imageUrls.Count];

        void AppendFrame(string frameID, string label)
        {
            if (frameID.Length == 0)
            {
                return;
            }
            for (int index = 0; index < referenceImages.Count; index++)
            {
                if (index >= imageUrls.Count || referenceImages[index].ID != frameID)
                {
                    continue;
                }
                ordered.Add(imageUrls[index]);
                used[index] = true;
                return;
            }
            throw new InvalidOperationException($"已配置的{label}参考图未包含在视频请求中");
        }

        AppendFrame(startFrameID, "首帧");
        AppendFrame(endFrameID, "尾帧");
        for (int index = 0; index < imageUrls.Count; index++)
        {
            if (!used[index])
            {
                ordered.Add(imageUrls[index]);
            }
        }
        return ordered;
    }

    /// <summary>
    /// 参考素材 URL 解析（允许 <c>asset://</c> 与 <c>data:</c>）。
    /// 对应 Go: <c>mediaReferenceURL</c>。
    /// </summary>
    public static string MediaReferenceUrl(ProviderMedia media)
    {
        string value = (media.URL ?? "").Trim();
        if (ProviderHelpers.IsPublicMediaURL(value)
            || value.StartsWith("asset://", StringComparison.Ordinal)
            || value.StartsWith("data:", StringComparison.Ordinal))
        {
            return value;
        }
        value = (media.DataURL ?? "").Trim();
        if (value.Length > 0)
        {
            return value;
        }
        throw new InvalidOperationException("参考素材需要公网 URL、asset:// 素材 ID 或 data URL");
    }

    /// <summary>
    /// Seedance <c>/videos</c> 的参考素材 URL（只接受 data URL 或公网 URL，
    /// <b>不接受 <c>asset://</c></b>）。对应 Go: <c>seedanceVideosMediaURL</c>。
    /// </summary>
    public static string SeedanceVideosMediaUrl(ProviderMedia media)
    {
        string value = (media.DataURL ?? "").Trim();
        if (value.StartsWith("data:", StringComparison.Ordinal))
        {
            return value;
        }
        value = (media.URL ?? "").Trim();
        if (value.StartsWith("data:", StringComparison.Ordinal) || ProviderHelpers.IsPublicMediaURL(value))
        {
            return value;
        }
        throw new InvalidOperationException("Seedance /videos 参考素材需要公网 URL 或 data URL");
    }

    /// <summary>
    /// 从 Seedance 任务状态里提取错误文案。对应 Go: <c>seedanceErrorMessage</c>。
    /// </summary>
    public static string SeedanceErrorMessage(Dictionary<string, object?> state)
    {
        Dictionary<string, object?>? errorValue = JsonFields.NestedObject(state, "error");
        if (errorValue is not null)
        {
            string message = JsonFields.StringField(errorValue, "message");
            string code = JsonFields.StringField(errorValue, "code");
            if (message.Length > 0 && code.Length > 0)
            {
                return code + "：" + message;
            }
            if (message.Length > 0)
            {
                return message;
            }
        }
        return JsonFields.StringField(state, "error_code");
    }

    /// <summary>
    /// 视频能力是否支持音频开关；未声明能力时视为支持（保留历史协议字段）。
    /// 对应 Go: <c>videoCapabilitySupportsAudio</c>。
    /// </summary>
    public static bool VideoCapabilitySupportsAudio(VideoCapabilityConfig? profile) =>
        profile is null || profile.GenerateAudio.Supported;

    /// <summary>对应 Go: <c>videoCapabilitySupportsWatermark</c>。</summary>
    public static bool VideoCapabilitySupportsWatermark(VideoCapabilityConfig? profile) =>
        profile is null || profile.Watermark.Supported;
}
