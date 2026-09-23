#nullable enable
using System.Globalization;
using System.Text.Json;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Application.Capabilities;

/// <summary>
/// 渠道模型能力配置的解码、归一化与校验，以及到路由能力规格的投影。
/// 对应 Go: <c>model_capability.go</c>（读路径所需子集）与 <c>logical_models.go</c> 的
/// <c>channelModelCapabilitySpec</c> / <c>capabilitySpecWithPriceTiers</c>。
/// </summary>
public static class ModelCapabilityConfigOps
{
    /// <summary>对应 Go: <c>DecodeModelCapabilityConfig</c>。空串返回 null，损坏 JSON 抛错。</summary>
    public static ModelCapabilityConfig? DecodeModelCapabilityConfig(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ModelCapabilityConfig>(raw, CapabilityJson.ReadOptions);
        }
        catch (JsonException error)
        {
            throw AppError.New(500, "解析渠道模型能力配置失败：" + error.Message);
        }
    }

    /// <summary>对应 Go: <c>NormalizeModelCapabilityConfig</c>。</summary>
    public static ModelCapabilityConfig? NormalizeModelCapabilityConfig(
        string capability, string protocol, ModelCapabilityConfig? input) =>
        NormalizeModelCapabilityConfigForModel(capability, protocol, "", input);

    /// <summary>
    /// 对应 Go: <c>NormalizeModelCapabilityConfigForModel</c>。
    /// 能力不是 text/image/video 时返回 null（Go 返回 (nil, nil)），由调用方按未配置处理。
    /// </summary>
    public static ModelCapabilityConfig? NormalizeModelCapabilityConfigForModel(
        string capability, string protocol, string modelName, ModelCapabilityConfig? input)
    {
        capability = CapabilitySpecOps.NormalizeCapability(capability);
        if (capability is not ("text" or "image" or "video"))
        {
            return null;
        }

        if (capability == "text")
        {
            if (input?.Text is null)
            {
                throw AppError.BadAuthRequest("请配置文本模型能力参数");
            }
            TextCapabilityConfig text = ShallowCopy(input.Text);
            text.Streaming ??= true;
            ModelCapabilityConfig value = new() { Version = 1, Text = text };
            ValidateTextCapabilityConfig(value.Text);
            return value;
        }

        if (capability == "image")
        {
            if (input?.Image is null)
            {
                throw AppError.BadAuthRequest("请配置图片模型能力参数");
            }
            ModelCapabilityConfig value = new() { Version = 1, Image = input.Image };
            value.Image = ValidateImageCapabilityConfig(value.Image);
            return value;
        }

        if (input?.Video is null)
        {
            throw AppError.BadAuthRequest("请配置视频模型能力参数");
        }
        ModelCapabilityConfig videoValue = new()
        {
            Version = 1,
            Video = ApplyModelSpecificVideoCapability(input.Video, protocol, modelName),
        };
        // ApplyModelSpecificVideoCapability 在无专项覆盖时原样返回入参，可能为 null。
        if (videoValue.Video is not null)
        {
            ValidateVideoCapabilityConfig(videoValue.Video);
        }
        return videoValue;
    }

    private static TextCapabilityConfig ShallowCopy(TextCapabilityConfig source) => new()
    {
        Streaming = source.Streaming,
        References = source.References,
    };

    /// <summary>对应 Go: <c>applyModelSpecificVideoCapability</c>（Agnes 专项覆盖）。</summary>
    public static VideoCapabilityConfig? ApplyModelSpecificVideoCapability(
        VideoCapabilityConfig? profile, string protocol, string modelName)
    {
        if (profile is null)
        {
            return profile;
        }

        if (protocol.Trim() == ChannelInterfaceTypes.ChannelInterfaceVolcengineArkVideo)
        {
            // 方舟 Seedance 的历史能力 JSON 可能只保存了文本/图片生成；
            // 与 Go 默认能力保持一致，补齐参考视频/音频的准入声明。
            VideoCapabilityConfig arkValue = profile;
            arkValue.References.MaxVideos = 3;
            arkValue.References.MaxAudios = 3;
            arkValue.References.MaxVideoBytes = 200 * 1024 * 1024;
            arkValue.References.MaxVideoDuration = 15;
            arkValue.References.MaxAudioBytes = 15 * 1024 * 1024;
            arkValue.References.MaxAudioDuration = 15;
            arkValue.Operations = ["text_to_video", "image_to_video", "reference_to_video", "audio_to_video"];
            arkValue.GenerateAudio = new VideoBooleanConfig { Supported = true, Default = true };
            arkValue.Watermark = new VideoBooleanConfig { Supported = true, Default = false };
            return arkValue;
        }

        if (protocol.Trim() != ChannelInterfaceTypes.ChannelInterfaceAgnesVideo)
        {
            return profile;
        }
        string normalizedModel = modelName.Trim().ToLowerInvariant();
        if (normalizedModel is not ("agnes-video-2.5" or "agnes-video-2.5-flash"))
        {
            return profile;
        }

        VideoCapabilityConfig value = profile;
        bool flash = normalizedModel == "agnes-video-2.5-flash";
        value.References.MaxImages = flash ? 5 : 9;
        value.References.MaxVideos = flash ? 0 : 3;
        value.References.MaxAudios = 3;
        value.References.MaxVideoBytes = flash ? 0 : 200 * 1024 * 1024;
        value.References.MaxVideoDuration = flash ? 0 : 15;
        value.References.MaxAudioBytes = 15 * 1024 * 1024;
        value.References.MaxAudioDuration = 15;
        value.Duration = new VideoDurationConfig { Selection = "range", Min = 4, Max = 12, Step = 1, Default = 5 };
        value.Ratios = ["21:9", "16:9", "4:3", "1:1", "3:4", "9:16"];
        value.DefaultRatio = "16:9";
        value.Resolutions = flash ? ["720P"] : ["720P", "960P", "2K"];
        value.DefaultResolution = "720P";
        value.GenerateAudio = new VideoBooleanConfig { Supported = false, Default = false };
        value.Watermark = new VideoBooleanConfig { Supported = false, Default = false };
        value.Operations = ["text_to_video", "image_to_video", "reference_to_video", "audio_to_video"];
        value.DefaultOperation = "text_to_video";
        return value;
    }

    /// <summary>
    /// 将渠道模型的真实供应能力投影为路由能力规格。
    /// 对应 Go: <c>CapabilitySpecFromModelCapabilityConfig</c>。
    /// </summary>
    public static CapabilitySpec CapabilitySpecFromModelCapabilityConfig(
        ModelCapabilityConfig? config, string capability)
    {
        capability = CapabilitySpecOps.NormalizeCapability(capability);
        CapabilitySpec spec = new()
        {
            Version = 1,
            Capability = capability,
            Inputs = CapabilitySpecOps.SortedMap<InputConstraint>(),
            Options = CapabilitySpecOps.SortedMap<OptionConstraint>(),
        };

        // 音频模型当前没有可编辑的渠道能力 JSON，使用空能力规格表示“无额外路由约束”。
        // Go 的空 map 会被 omitempty 整体省略，因此这里输出 null 而不是空字典。
        if (capability == "audio")
        {
            spec.Inputs = null;
            spec.Options = null;
            return spec;
        }

        if (config is null)
        {
            throw capability switch
            {
                "text" => AppError.BadAuthRequest("渠道文本模型尚未配置能力参数"),
                "image" => AppError.BadAuthRequest("渠道图片模型尚未配置能力参数"),
                "video" => AppError.BadAuthRequest("渠道视频模型尚未配置能力参数"),
                _ => AppError.BadAuthRequest("渠道模型尚未配置能力参数"),
            };
        }

        switch (capability)
        {
            case "text":
            {
                if (config.Text is null)
                {
                    throw AppError.BadAuthRequest("渠道文本模型尚未配置能力参数");
                }
                AddInputConstraint(spec.Inputs, "image", 0, config.Text.References.MaxImages);
                AddInputConstraint(spec.Inputs, "video", 0, config.Text.References.MaxVideos);
                break;
            }
            case "image":
            {
                if (config.Image is null)
                {
                    throw AppError.BadAuthRequest("渠道图片模型尚未配置能力参数");
                }
                ImageCapabilityConfig image = config.Image;
                AddInputConstraint(spec.Inputs, "image", 0, image.References.MaxImages);
                if (image.References.MaskSupported)
                {
                    AddInputConstraint(spec.Inputs, "mask", 0, 1);
                }
                if (image.Size.Parameter != "none")
                {
                    spec.Options["size"] = ImageSizeOptionConstraint(image.Size);
                    spec.ImageSize = CapabilityImageSizeFromConfig(image.Size);
                }
                if (image.Quality.Supported)
                {
                    spec.Options["quality"] = CapabilitySpecOps.AnyValues(image.Quality.Values);
                }
                spec.Options["transparentBackground"] = CapabilitySpecOps.BoolValues(image.TransparentBackground.Supported);
                spec.Options["count"] = CapabilitySpecOps.NumericRange(1, image.MaxOutputs, 1);
                break;
            }
            case "video":
            {
                if (config.Video is null)
                {
                    throw AppError.BadAuthRequest("渠道视频模型尚未配置能力参数");
                }
                VideoCapabilityConfig video = config.Video;
                spec.Operations = video.Operations.Count == 0 ? null : [.. video.Operations];
                AddInputConstraint(spec.Inputs, "image", video.References.MinImages, video.References.MaxImages);
                AddInputConstraint(spec.Inputs, "video", 0, video.References.MaxVideos);
                AddInputConstraint(spec.Inputs, "audio", 0, video.References.MaxAudios);
                if (video.Duration.Selection == "enum")
                {
                    List<JsonElement> values = [];
                    foreach (int item in video.Duration.Values ?? [])
                    {
                        values.Add(JsonSerializer.SerializeToElement(item));
                    }
                    spec.Options["videoSeconds"] = new OptionConstraint { Values = values.Count == 0 ? null : values };
                }
                else
                {
                    spec.Options["videoSeconds"] = CapabilitySpecOps.NumericRange(
                        video.Duration.Min, video.Duration.Max, video.Duration.Step);
                }
                spec.Options["size"] = CapabilitySpecOps.AnyValues(video.Ratios);
                if (video.Resolutions.Count > 0)
                {
                    spec.Options["vquality"] = CapabilitySpecOps.AnyValues(video.Resolutions);
                }
                spec.Options["videoGenerateAudio"] = CapabilitySpecOps.BoolValues(video.GenerateAudio.Supported);
                spec.Options["videoWatermark"] = CapabilitySpecOps.BoolValues(video.Watermark.Supported);
                break;
            }
            default:
                throw AppError.BadAuthRequest("未知模型能力类型");
        }

        // 与 Go 的 omitempty 对齐：空 map 不输出。
        if (spec.Inputs is { Count: 0 })
        {
            spec.Inputs = null;
        }
        if (spec.Options is { Count: 0 })
        {
            spec.Options = null;
        }
        return spec;
    }

    /// <summary>
    /// 保留可见的标准尺寸/比例，同时用 * 表示允许自定义。
    /// 对应 Go: <c>imageSizeOptionConstraint</c>。
    /// </summary>
    private static OptionConstraint ImageSizeOptionConstraint(ImageSizeConfig size)
    {
        List<string> values = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string raw in size.Values)
        {
            string value = raw.Trim();
            if (value.Length == 0 || value == "*" || !seen.Add(value))
            {
                continue;
            }
            values.Add(value);
        }
        if (size.AllowCustom)
        {
            if (values.Count == 0)
            {
                foreach (string value in LegacyImageSizeValues())
                {
                    seen.Add(value);
                    values.Add(value);
                }
            }
            values.Add("*");
        }
        return CapabilitySpecOps.AnyValues(values);
    }

    /// <summary>对应 Go: <c>capabilityImageSizeFromConfig</c>。</summary>
    private static CapabilityImageSize? CapabilityImageSizeFromConfig(ImageSizeConfig size)
    {
        if (size.Parameter.Length == 0 || size.Parameter == "none")
        {
            return null;
        }
        CapabilityImageSize result = new() { Parameter = size.Parameter, AllowCustom = size.AllowCustom };
        foreach (ImageSizePreset preset in size.Presets ?? [])
        {
            string tier = preset.Tier.Trim().ToLowerInvariant();
            string ratio = preset.Ratio.Trim();
            string value = preset.Size.Trim();
            if (tier.Length == 0 || ratio.Length == 0 || value.Length == 0)
            {
                continue;
            }
            (result.Presets ??= []).Add(new CapabilityImageSizePreset
            {
                Size = value,
                Tier = tier,
                Ratio = ratio,
                Width = preset.Width,
                Height = preset.Height,
            });
        }
        return result;
    }

    private static void AddInputConstraint(Dictionary<string, InputConstraint> inputs, string name, long min, long max)
    {
        if (min <= 0 && max <= 0)
        {
            return;
        }
        inputs[name] = new InputConstraint { Min = min, Max = max };
    }

    // ------------------------------------------------------------ 校验

    /// <summary>对应 Go: <c>validateTextCapabilityConfig</c>。</summary>
    private static void ValidateTextCapabilityConfig(TextCapabilityConfig value)
    {
        if (value.References.PromptMaxChars is < 1 or > 1000000)
        {
            throw AppError.BadAuthRequest("提示词最大字符数必须在 1-1000000 之间");
        }
        foreach ((string name, int number) in
                 new[] { ("最大图片引用数", value.References.MaxImages), ("最大视频引用数", value.References.MaxVideos) })
        {
            if (number is < 0 or > 100)
            {
                throw AppError.BadAuthRequest(name + "必须在 0-100 之间");
            }
        }
        if (value.References.MaxImageBytes < 0 || value.References.MaxVideoBytes < 0)
        {
            throw AppError.BadAuthRequest("引用素材大小限制不能小于 0");
        }
    }

    /// <summary>对应 Go: <c>validateImageCapabilityConfig</c>。注意 Go 会就地修正部分字段。</summary>
    private static ImageCapabilityConfig ValidateImageCapabilityConfig(ImageCapabilityConfig value)
    {
        if (value.References.PromptMaxChars is < 1 or > 1000000)
        {
            throw AppError.BadAuthRequest("提示词最大字符数必须在 1-1000000 之间");
        }
        if (value.References.MaxImages is < 0 or > 100 || value.References.MaxImageBytes < 0)
        {
            throw AppError.BadAuthRequest("图片引用限制无效");
        }
        if (value.MaxOutputs is < 1 or > 100)
        {
            throw AppError.BadAuthRequest("单次图片数量必须在 1-100 之间");
        }

        switch (value.Size.Parameter)
        {
            case "none":
                value.Size.Values = [];
                value.Size.Presets = null;
                value.Size.Default = "auto";
                value.Size.AllowCustom = false;
                break;
            case "size" or "aspect_ratio":
                if (string.IsNullOrWhiteSpace(value.Size.Default))
                {
                    throw AppError.BadAuthRequest("请配置默认图片尺寸或比例");
                }
                if (!value.Size.AllowCustom && !ContainsCapabilityString(value.Size.Values, value.Size.Default))
                {
                    throw AppError.BadAuthRequest("默认图片尺寸必须属于支持值");
                }
                break;
            default:
                throw AppError.BadAuthRequest("尺寸参数仅支持不发送、size 或 aspect_ratio");
        }

        HashSet<string> seenPresets = new(StringComparer.Ordinal);
        foreach (ImageSizePreset preset in value.Size.Presets ?? [])
        {
            if (preset.Tier is not ("1k" or "2k" or "4k"))
            {
                throw AppError.BadAuthRequest("图片分辨率档位仅支持 1K、2K、4K");
            }
            string[] parts = preset.Ratio.Split(':');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ||
                w <= 0 || h <= 0 || w > 100000 || h > 100000 || Math.Max(w, h) > Math.Min(w, h) * 3)
            {
                throw AppError.BadAuthRequest("图片预设比例无效");
            }
            if (preset.Width <= 0 || preset.Height <= 0 || Math.Max(preset.Width, preset.Height) > 3840 ||
                Math.Max(preset.Width, preset.Height) > Math.Min(preset.Width, preset.Height) * 3 ||
                (long)preset.Width * preset.Height < 655360 || (long)preset.Width * preset.Height > 8294400 ||
                preset.Size != string.Create(CultureInfo.InvariantCulture, $"{preset.Width}x{preset.Height}"))
            {
                throw AppError.BadAuthRequest("图片预设像素尺寸无效");
            }

            // 容许像素取整误差，但不能将横屏尺寸标记成竖屏或其他比例。
            long difference = Math.Abs((long)preset.Width * h - (long)preset.Height * w);
            if (difference * 1000 > (long)preset.Height * w * 25)
            {
                throw AppError.BadAuthRequest("图片预设像素尺寸与宽高比不一致");
            }

            int a = w;
            int b = h;
            while (b != 0)
            {
                (a, b) = (b, a % b);
            }
            string key = $"{preset.Tier}:{w / a}:{h / a}";
            if (!seenPresets.Add(key))
            {
                throw AppError.BadAuthRequest("图片尺寸预设重复");
            }

            string requestValue = value.Size.Parameter == "aspect_ratio" ? preset.Ratio : preset.Size;
            if (!ContainsCapabilityString(value.Size.Values, requestValue))
            {
                throw AppError.BadAuthRequest("图片预设必须包含在尺寸支持值中");
            }
        }

        if (value.Quality.Supported)
        {
            if (value.Quality.Values.Count == 0 || string.IsNullOrWhiteSpace(value.Quality.Default) ||
                !ContainsCapabilityString(value.Quality.Values, value.Quality.Default))
            {
                throw AppError.BadAuthRequest("请配置图片质量支持值和默认值");
            }
        }
        else
        {
            value.Quality.Values = [];
            value.Quality.Default = "auto";
        }

        try
        {
            ValidateImagePresetSelection(value, value.Quality.Default, value.Size.Default);
        }
        catch (AppError)
        {
            throw AppError.BadAuthRequest("默认图片分辨率与宽高比不在已配置的组合中");
        }

        if (!value.TransparentBackground.Supported)
        {
            value.TransparentBackground.Default = false;
        }
        return value;
    }

    /// <summary>对应 Go: <c>validateVideoCapabilityConfig</c>。</summary>
    private static void ValidateVideoCapabilityConfig(VideoCapabilityConfig value)
    {
        if (value.References.PromptMaxChars is < 1 or > 1000000)
        {
            throw AppError.BadAuthRequest("提示词最大字符数必须在 1-1000000 之间");
        }
        foreach ((string name, int number) in new[]
                 {
                     ("最少图片引用数", value.References.MinImages),
                     ("最大图片引用数", value.References.MaxImages),
                     ("最大视频引用数", value.References.MaxVideos),
                     ("最大音频引用数", value.References.MaxAudios),
                 })
        {
            if (number is < 0 or > 100)
            {
                throw AppError.BadAuthRequest(name + "必须在 0-100 之间");
            }
        }
        if (value.References.MinImages > value.References.MaxImages)
        {
            throw AppError.BadAuthRequest("最少图片引用数不能超过最大图片引用数");
        }
        if (value.References.MaxImageBytes < 0 || value.References.MaxVideoBytes < 0 ||
            value.References.MaxAudioBytes < 0 || value.References.MaxVideoDuration < 0 ||
            value.References.MaxAudioDuration < 0)
        {
            throw AppError.BadAuthRequest("引用素材限制不能小于 0");
        }

        ValidateVideoDuration(value.Duration);

        if (value.Ratios.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(value.DefaultRatio))
            {
                throw AppError.BadAuthRequest("未配置画面比例时不能设置默认比例");
            }
        }
        else if (string.IsNullOrWhiteSpace(value.DefaultRatio) || !ContainsCapabilityString(value.Ratios, value.DefaultRatio))
        {
            throw AppError.BadAuthRequest("默认画面比例必须属于支持值");
        }

        if (value.Resolutions.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(value.DefaultResolution))
            {
                throw AppError.BadAuthRequest("未配置输出分辨率时不能设置默认分辨率");
            }
        }
        else if (string.IsNullOrWhiteSpace(value.DefaultResolution) ||
                 !ContainsCapabilityString(value.Resolutions, value.DefaultResolution))
        {
            throw AppError.BadAuthRequest("默认输出分辨率必须属于支持值");
        }

        if (value.Operations.Count == 0 || string.IsNullOrWhiteSpace(value.DefaultOperation) ||
            !ContainsCapabilityString(value.Operations, value.DefaultOperation))
        {
            throw AppError.BadAuthRequest("请至少配置一个生成模式，并选择默认模式");
        }
    }

    /// <summary>对应 Go: <c>validateVideoDuration</c>。</summary>
    private static void ValidateVideoDuration(VideoDurationConfig value)
    {
        switch (value.Selection)
        {
            case "range":
                if (value.Min < 1 || value.Max < value.Min || value.Max > 3600 || value.Step < 1 ||
                    value.Default < value.Min || value.Default > value.Max ||
                    (value.Default - value.Min) % value.Step != 0)
                {
                    throw AppError.BadAuthRequest("视频时长范围或默认值无效");
                }
                break;
            case "enum":
            {
                List<int> values = value.Values ?? [];
                if (values.Count == 0 || values.Count > 100)
                {
                    throw AppError.BadAuthRequest("视频固定时长至少需要一个选项");
                }
                List<int> sorted = [.. values];
                sorted.Sort();
                for (int index = 0; index < sorted.Count; index++)
                {
                    if (sorted[index] is < 1 or > 3600 || (index > 0 && sorted[index - 1] == sorted[index]))
                    {
                        throw AppError.BadAuthRequest("视频固定时长选项无效或重复");
                    }
                }
                if (!values.Contains(value.Default))
                {
                    throw AppError.BadAuthRequest("视频默认时长必须属于固定时长选项");
                }
                break;
            }
            default:
                throw AppError.BadAuthRequest("视频时长选择方式仅支持范围或固定值");
        }
    }

    /// <summary>对应 Go: <c>containsCapabilityString</c>。</summary>
    public static bool ContainsCapabilityString(IReadOnlyList<string> values, string target)
    {
        foreach (string value in values)
        {
            if (string.Equals(value.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>对应 Go: <c>validateImagePresetSelection</c>。</summary>
    private static void ValidateImagePresetSelection(ImageCapabilityConfig profile, string quality, string ratio)
    {
        if (profile.Size.Parameter != "aspect_ratio" || profile.Size.AllowCustom ||
            (profile.Size.Presets?.Count ?? 0) == 0 || ratio == "auto")
        {
            return;
        }
        string tier = ImageResolutionTier(quality);
        if (tier.Length == 0)
        {
            return;
        }
        foreach (ImageSizePreset preset in profile.Size.Presets!)
        {
            if (preset.Tier == tier && preset.Ratio == ratio)
            {
                return;
            }
        }
        throw AppError.BadAuthRequest("当前分辨率不支持所选图片宽高比");
    }

    /// <summary>对应 Go: <c>imageResolutionTier</c>。</summary>
    private static string ImageResolutionTier(string quality) => quality.Trim().ToLowerInvariant() switch
    {
        "1k" or "low" => "1k",
        "2k" or "medium" => "2k",
        "4k" or "high" => "4k",
        _ => "",
    };

    /// <summary>对应 Go: <c>supportsTokenBilling</c>（channel_models.go）。</summary>
    public static bool SupportsTokenBilling(string capability, string protocol) =>
        capability == "text" ||
        (capability == "video" && protocol == ChannelInterfaceTypes.ChannelInterfaceVolcengineArkVideo);

    /// <summary>对应 Go: <c>normalizeChannelModelTierResolution</c>（channel_models.go）。</summary>
    public static string NormalizeChannelModelTierResolution(string? raw)
    {
        string value = (raw ?? "").Trim();
        if (value.Length == 0 || value == "*" || string.Equals(value, "any", StringComparison.OrdinalIgnoreCase))
        {
            return "*";
        }
        JsonElement normalized = CapabilitySpecOps.NormalizeModelRequestOption("vquality", JsonSerializer.SerializeToElement(value));
        return CapabilitySpecOps.NormalizedScalar(normalized);
    }

    /// <summary>对应 Go: <c>defaultImageSizeValues</c>。仅 imageSizeOptionConstraint 回退路径使用。</summary>
    private static string[] LegacyImageSizeValues() =>
    [
        "1:1", "3:2", "2:3", "4:3", "3:4", "16:9", "21:9", "9:16",
        "1024x1024", "1536x1024", "1024x1536",
    ];
}

/// <summary>渠道接口类型常量别名，避免各处引 Entities 命名空间。</summary>
internal static class ChannelInterfaceTypes
{
    public const string ChannelInterfaceVolcengineArkVideo = "volcengine-ark-video";
    public const string ChannelInterfaceAgnesVideo = "agnes-video";
}
