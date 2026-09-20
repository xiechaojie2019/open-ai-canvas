#nullable enable
namespace OpenAICanvas.Providers;

/// <summary>
/// 图片/视频尺寸与质量的归一化与能力裁剪。
/// 对应 Go: <c>internal/app/provider_video_options.go</c> 的
/// <c>normalizeImageQuality</c> / <c>imageParameterSupported</c> / <c>imageQualitySupported</c> /
/// <c>imageTransparentBackgroundSupported</c> / <c>imageSizeParameter</c> /
/// <c>normalizeImageAspectRatio</c> / <c>imageDimensionGCD</c> / <c>normalizePixelSize</c> /
/// <c>normalizeVideoSize</c> / <c>normalizeVideoResolution</c>，
/// 以及 <c>provider_image.go</c> 的 <c>normalizeGrokImageResolution</c> /
/// <c>normalizeGrokImageAspectRatio</c> / <c>normalizeVolcengineArkImageSize</c>。
/// </summary>
/// <remarks>
/// <b>能力为 null 时一律视为"支持"</b>（对应 Go 的 <c>profile == nil</c> 分支）：
/// 未声明能力的渠道按全量参数发送，避免旧配置被静默降级。
/// </remarks>
public static class ProviderImageOptions
{
    /// <summary>即梦/方舟等渠道的画布比例到像素尺寸预设。对应 Go: <c>normalizePixelSize</c>。</summary>
    public static string NormalizePixelSize(string? value)
    {
        string trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0 || trimmed == "auto")
        {
            return "";
        }
        // 画布按比例保存常用预设；图片接口只接受像素尺寸，必须在请求边界完成转换。
        switch (trimmed)
        {
            case "1:1": return "1024x1024";
            case "3:2": return "1536x1024";
            case "2:3": return "1024x1536";
            case "4:3": return "1360x1024";
            case "3:4": return "1024x1360";
            case "16:9": return "1824x1024";
            case "9:16": return "1024x1824";
            case "21:9": return "2352x1008";
        }
        return trimmed.Contains('x') ? trimmed : "";
    }

    /// <summary>对应 Go: <c>normalizeImageQuality</c>（1k/2k/4k → low/medium/high）。</summary>
    public static string NormalizeImageQuality(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "1k" => "low",
        "2k" => "medium",
        "4k" => "high",
        _ => value ?? "",
    };

    /// <summary>对应 Go: <c>imageParameterSupported</c>。</summary>
    public static bool ImageParameterSupported(ImageCapabilityConfig? profile, string parameter)
    {
        if (profile is null)
        {
            return true;
        }
        return parameter == "response_format"
            ? profile.ResponseFormat.Supported
            : profile.OutputFormat.Supported;
    }

    /// <summary>对应 Go: <c>imageQualitySupported</c>。</summary>
    public static bool ImageQualitySupported(ImageCapabilityConfig? profile) =>
        profile is null || profile.Quality.Supported;

    /// <summary>对应 Go: <c>imageTransparentBackgroundSupported</c>。</summary>
    public static bool ImageTransparentBackgroundSupported(ImageCapabilityConfig? profile) =>
        profile is null || profile.TransparentBackground.Supported;

    /// <summary>
    /// 决定尺寸参数名与取值。返回空 key 表示不发送该参数。
    /// 对应 Go: <c>imageSizeParameter</c>。
    /// </summary>
    public static (string Key, string Value) ImageSizeParameter(ImageCapabilityConfig? profile, string? value)
    {
        if (profile is null)
        {
            return ("size", NormalizePixelSize(value));
        }
        string trimmed = (value ?? "").Trim();
        if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return ("", "");
        }
        if (trimmed.Length == 0)
        {
            trimmed = profile.Size.Default.Trim();
        }
        return profile.Size.Parameter switch
        {
            "size" => ("size", NormalizePixelSize(trimmed)),
            "aspect_ratio" => ("aspect_ratio", NormalizeImageAspectRatio(trimmed)),
            _ => ("", ""),
        };
    }

    /// <summary>对应 Go: <c>normalizeImageAspectRatio</c>（像素尺寸按最大公约数约简）。</summary>
    public static string NormalizeImageAspectRatio(string? value)
    {
        string trimmed = (value ?? "").Trim().ToLowerInvariant().Replace('×', 'x');
        if (trimmed.Contains(':'))
        {
            return trimmed;
        }
        string[] parts = trimmed.Split('x');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int width)
            || !int.TryParse(parts[1], out int height)
            || width <= 0 || height <= 0)
        {
            return "";
        }
        int divisor = ImageDimensionGcd(width, height);
        return (width / divisor) + ":" + (height / divisor);
    }

    /// <summary>对应 Go: <c>imageDimensionGCD</c>（左值小于 1 时返回 1）。</summary>
    public static int ImageDimensionGcd(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }
        return left < 1 ? 1 : left;
    }

    /// <summary>对应 Go: <c>normalizeGrokImageResolution</c>（xAI 图片最高 2k，超出夹到 2k）。</summary>
    public static string NormalizeGrokImageResolution(string? quality) =>
        (quality ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "auto" => "",
            "1k" or "low" or "standard" => "1k",
            "2k" or "medium" or "hd" or "high" or "4k" => "2k",
            _ => "",
        };

    /// <summary>grok2api / xAI 接受的画布比例白名单。对应 Go 的冒号分支 switch。</summary>
    private static readonly HashSet<string> GrokAspectRatioWhitelist = new(StringComparer.Ordinal)
    {
        "1:1", "3:4", "4:3", "9:16", "16:9", "2:3", "3:2", "9:19.5", "19.5:9", "1:2", "2:1",
    };

    /// <summary>
    /// 把画布 size（如 <c>1280x720</c> / <c>9:16</c>）转成 grok2api / xAI 接受的 aspect_ratio。
    /// 对应 Go: <c>normalizeGrokImageAspectRatio</c>。
    /// </summary>
    /// <remarks>
    /// 比例判定有<b>精确等式</b>与<b>容差区间</b>两条路径，且顺序敏感：
    /// 像素尺寸路径必须显式覆盖 2:3 / 3:2 / 1:2 / 2:1，否则只靠 <c>w&gt;h</c> 兜底会把
    /// 768x1152（2:3）错标成 9:16、1152x768（3:2）错标成 16:9，上游会按错比例裁切。
    /// </remarks>
    public static string NormalizeGrokImageAspectRatio(string? size)
    {
        string raw = (size ?? "").Trim().ToLowerInvariant().Replace('×', 'x');
        if (raw.Length == 0 || raw == "auto")
        {
            return "";
        }
        if (raw.Contains(':'))
        {
            return GrokAspectRatioWhitelist.Contains(raw) ? raw : "";
        }

        string[] parts = raw.Split('x');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int w)
            || !int.TryParse(parts[1], out int h)
            || w <= 0 || h <= 0)
        {
            return "";
        }
        if (w == h)
        {
            return "1:1";
        }

        double ratio = (double)w / h;
        if (w * 9 == h * 16 || (ratio >= 1.7 && ratio <= 1.8))
        {
            return "16:9";
        }
        if (h * 9 == w * 16 || (ratio > 0 && ratio <= 1.0 / 1.7 && ratio >= 1.0 / 1.8))
        {
            return "9:16";
        }
        if (w * 3 == h * 4 || (ratio > 1.2 && ratio < 1.4))
        {
            return "4:3";
        }
        if (h * 3 == w * 4 || (ratio > 0.7 && ratio < 0.85))
        {
            return "3:4";
        }
        if (w * 3 == h * 2 || (ratio >= 0.6 && ratio < 0.72))
        {
            return "2:3";
        }
        if (w * 2 == h * 3 || (ratio > 1.35 && ratio < 1.6))
        {
            return "3:2";
        }
        if (h == w * 2 || (ratio > 0.45 && ratio < 0.55))
        {
            return "1:2";
        }
        if (w == h * 2 || (ratio > 1.85 && ratio < 2.2))
        {
            return "2:1";
        }
        return w > h ? "16:9" : "9:16";
    }

    /// <summary>对应 Go: <c>normalizeVideoSize</c>（竖屏比例回落 720x1280，其余 1280x720）。</summary>
    public static string NormalizeVideoSize(string? value)
    {
        string trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0 || trimmed == "auto")
        {
            return "";
        }
        if (trimmed.Contains('x'))
        {
            return trimmed;
        }
        return trimmed is "9:16" or "2:3" or "3:4" ? "720x1280" : "1280x720";
    }

    /// <summary>
    /// 归一化视频分辨率。对应 Go: <c>normalizeVideoResolution</c>。
    /// </summary>
    /// <remarks>
    /// <b>默认值是 720p 而非空串</b>：空、<c>auto</c>、<c>medium</c>、<c>high</c> 都映射到 720p。
    /// 且未识别的值会补 <c>p</c> 后缀而非丢弃 —— 与"不支持的参数就省略"的图片逻辑不同。
    /// </remarks>
    public static string NormalizeVideoResolution(string? value)
    {
        string trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0 || trimmed == "auto" || trimmed == "medium" || trimmed == "high")
        {
            return "720p";
        }
        if (trimmed == "low")
        {
            return "480p";
        }
        if (trimmed.Equals("4k", StringComparison.OrdinalIgnoreCase))
        {
            return "2160p";
        }
        if (trimmed.Equals("2k", StringComparison.OrdinalIgnoreCase))
        {
            return "1440p";
        }
        return trimmed.EndsWith('p') ? trimmed : trimmed + "p";
    }

    // ------------------------------------------------------------ 火山方舟图片尺寸

    /// <summary>方舟图片像素下限。对应 Go: <c>volcengineArkImageMinPixels</c>。</summary>
    public const long VolcengineArkImageMinPixels = 3_686_400;

    /// <summary>方舟图片像素上限。对应 Go: <c>volcengineArkImageMaxPixels</c>。</summary>
    public const long VolcengineArkImageMaxPixels = 4_624_220;

    /// <summary>
    /// 把像素尺寸夹到方舟接受的像素区间（保持宽高比，宽高取偶）。
    /// 对应 Go: <c>normalizeVolcengineArkImageSize</c>。
    /// </summary>
    /// <remarks>
    /// 缩放后先按奇数/偶数对齐到 2 的倍数，再用 ±2 的循环做像素区间微调 ——
    /// 因为取偶会破坏缩放结果，单次缩放无法保证落在 [下限, 上限] 内。
    /// </remarks>
    public static string NormalizeVolcengineArkImageSize(string? value)
    {
        string size = NormalizePixelSize(value);
        string[] parts = size.ToLowerInvariant().Split('x');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int width)
            || !int.TryParse(parts[1], out int height)
            || width <= 0 || height <= 0)
        {
            return size;
        }

        long pixels = (long)width * height;
        if (pixels >= VolcengineArkImageMinPixels && pixels <= VolcengineArkImageMaxPixels)
        {
            return size;
        }

        long targetPixels = VolcengineArkImageMaxPixels;
        bool roundUp = false;
        if (pixels < VolcengineArkImageMinPixels)
        {
            targetPixels = VolcengineArkImageMinPixels;
            roundUp = true;
        }
        double scale = Math.Sqrt((double)targetPixels / pixels);
        width = (int)(roundUp ? Math.Ceiling(width * scale / 2) : Math.Floor(width * scale / 2)) * 2;
        height = (int)(roundUp ? Math.Ceiling(height * scale / 2) : Math.Floor(height * scale / 2)) * 2;

        while (width > 2 && height > 2 && (long)width * height < VolcengineArkImageMinPixels)
        {
            if (width >= height)
            {
                width += 2;
            }
            else
            {
                height += 2;
            }
        }
        while (width > 2 && height > 2 && (long)width * height > VolcengineArkImageMaxPixels)
        {
            if (width >= height)
            {
                width -= 2;
            }
            else
            {
                height -= 2;
            }
        }
        return width + "x" + height;
    }
}
