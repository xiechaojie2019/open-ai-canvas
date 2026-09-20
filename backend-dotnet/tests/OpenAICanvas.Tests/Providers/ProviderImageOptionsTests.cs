#nullable enable
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 图片/视频尺寸与质量归一化、能力裁剪的契约测试。
/// 对应 Go: <c>internal/app/provider_video_options.go</c> 与 <c>provider_image.go</c> 的归一化函数。
/// </summary>
public sealed class ProviderImageOptionsTests
{
    // ------------------------------------------------------------ normalizePixelSize

    [Theory]
    [InlineData("1:1", "1024x1024")]
    [InlineData("3:2", "1536x1024")]
    [InlineData("2:3", "1024x1536")]
    [InlineData("4:3", "1360x1024")]
    [InlineData("3:4", "1024x1360")]
    [InlineData("16:9", "1824x1024")]
    [InlineData("9:16", "1024x1824")]
    [InlineData("21:9", "2352x1008")]
    public void 像素尺寸_比例预设(string value, string expected) =>
        Assert.Equal(expected, ProviderImageOptions.NormalizePixelSize(value));

    [Theory]
    [InlineData("1280x720", "1280x720")]
    [InlineData(" 100x200 ", "100x200")]
    public void 像素尺寸_已是像素时透传(string value, string expected) =>
        Assert.Equal(expected, ProviderImageOptions.NormalizePixelSize(value));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("auto")]
    [InlineData("nonsense")]
    public void 像素尺寸_无效值返回空(string value) =>
        Assert.Equal("", ProviderImageOptions.NormalizePixelSize(value));

    // ------------------------------------------------------------ normalizeImageQuality

    [Theory]
    [InlineData("1k", "low")]
    [InlineData("2k", "medium")]
    [InlineData("4k", "high")]
    [InlineData("1K", "low")]
    [InlineData("low", "low")]
    [InlineData("high", "high")]
    [InlineData("", "")]
    public void 图片质量_档位映射(string value, string expected) =>
        Assert.Equal(expected, ProviderImageOptions.NormalizeImageQuality(value));

    [Fact]
    public void 图片质量_未命中档位时保留原始大小写()
    {
        // Go 的 default 分支返回的是入参原值（switch 用的才是小写副本），不是小写形式。
        Assert.Equal("HIGH", ProviderImageOptions.NormalizeImageQuality("HIGH"));
        Assert.Equal(" Ultra ", ProviderImageOptions.NormalizeImageQuality(" Ultra "));
    }

    // ------------------------------------------------------------ 能力裁剪

    [Fact]
    public void 能力_为空时全部视为支持()
    {
        // 未声明能力的渠道按全量参数发送，避免旧配置被静默降级。
        Assert.True(ProviderImageOptions.ImageParameterSupported(null, "response_format"));
        Assert.True(ProviderImageOptions.ImageParameterSupported(null, "output_format"));
        Assert.True(ProviderImageOptions.ImageQualitySupported(null));
        Assert.True(ProviderImageOptions.ImageTransparentBackgroundSupported(null));
    }

    [Fact]
    public void 能力_按声明裁剪()
    {
        ImageCapabilityConfig profile = new()
        {
            ResponseFormat = new ParameterSupport { Supported = true },
            OutputFormat = new ParameterSupport { Supported = false },
            Quality = new ImageQualityConfig { Supported = false },
            TransparentBackground = new VideoBooleanConfig { Supported = false },
        };

        Assert.True(ProviderImageOptions.ImageParameterSupported(profile, "response_format"));
        Assert.False(ProviderImageOptions.ImageParameterSupported(profile, "output_format"));
        Assert.False(ProviderImageOptions.ImageQualitySupported(profile));
        Assert.False(ProviderImageOptions.ImageTransparentBackgroundSupported(profile));
    }

    // ------------------------------------------------------------ imageSizeParameter

    [Theory]
    [InlineData("1024x1024", "size", "1024x1024")]
    [InlineData("1:1", "size", "1024x1024")]
    public void 尺寸参数_无能力时按size(string value, string key, string expected)
    {
        (string actualKey, string actualValue) = ProviderImageOptions.ImageSizeParameter(null, value);

        Assert.Equal(key, actualKey);
        Assert.Equal(expected, actualValue);
    }

    [Fact]
    public void 尺寸参数_无能力时auto仍返回size键()
    {
        // 注意：Go 的 "auto" 短路只存在于 profile != nil 分支；
        // 无能力时恒返回 ("size", normalizePixelSize(value))，而 auto 会归一为空值。
        (string key, string value) = ProviderImageOptions.ImageSizeParameter(null, "auto");

        Assert.Equal("size", key);
        Assert.Equal("", value);
    }

    [Fact]
    public void 尺寸参数_有能力时auto完全不发送()
    {
        ImageCapabilityConfig profile = new() { Size = new ImageSizeConfig { Parameter = "size" } };

        (string key, string value) = ProviderImageOptions.ImageSizeParameter(profile, "auto");

        Assert.Equal("", key);
        Assert.Equal("", value);
    }

    [Fact]
    public void 尺寸参数_有能力时大小写不敏感识别auto()
    {
        ImageCapabilityConfig profile = new() { Size = new ImageSizeConfig { Parameter = "size" } };

        (string key, _) = ProviderImageOptions.ImageSizeParameter(profile, " AUTO ");

        Assert.Equal("", key);
    }

    [Fact]
    public void 尺寸参数_能力声明aspect_ratio时转比例()
    {
        ImageCapabilityConfig profile = new()
        {
            Size = new ImageSizeConfig { Parameter = "aspect_ratio" },
        };

        (string key, string value) = ProviderImageOptions.ImageSizeParameter(profile, "1024x1536");

        Assert.Equal("aspect_ratio", key);
        Assert.Equal("2:3", value);
    }

    [Fact]
    public void 尺寸参数_未声明参数类型时不发送()
    {
        ImageCapabilityConfig profile = new()
        {
            Size = new ImageSizeConfig { Parameter = "resolution" },
        };

        (string key, string value) = ProviderImageOptions.ImageSizeParameter(profile, "1024x1024");

        Assert.Equal("", key);
        Assert.Equal("", value);
    }

    [Fact]
    public void 尺寸参数_空值回落能力默认值()
    {
        ImageCapabilityConfig profile = new()
        {
            Size = new ImageSizeConfig { Parameter = "size", Default = "1:1" },
        };

        (string key, string value) = ProviderImageOptions.ImageSizeParameter(profile, "");

        Assert.Equal("size", key);
        Assert.Equal("1024x1024", value);
    }

    // ------------------------------------------------------------ normalizeImageAspectRatio

    [Fact]
    public void 比例_冒号形式直接透传()
    {
        Assert.Equal("16:9", ProviderImageOptions.NormalizeImageAspectRatio("16:9"));
        Assert.Equal("3:4", ProviderImageOptions.NormalizeImageAspectRatio(" 3:4 "));
    }

    [Theory]
    [InlineData("1024x1024", "1:1")]
    [InlineData("1536x1024", "3:2")]
    [InlineData("1920x1080", "16:9")]
    [InlineData("1360x1024", "85:64")]
    public void 比例_像素约简为最简整数比(string value, string expected) =>
        Assert.Equal(expected, ProviderImageOptions.NormalizeImageAspectRatio(value));

    [Fact]
    public void 比例_全角乘号等价于x()
    {
        Assert.Equal("1:1", ProviderImageOptions.NormalizeImageAspectRatio("1024×1024"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1024")]
    [InlineData("0x100")]
    [InlineData("100x0")]
    [InlineData("-10x20")]
    [InlineData("")]
    public void 比例_无效值返回空(string value) =>
        Assert.Equal("", ProviderImageOptions.NormalizeImageAspectRatio(value));

    [Fact]
    public void 比例_最大公约数为1时兜底()
    {
        // gcd(1,1)=1；gcd 传入 0 时必须回落 1 而不是抛除零。
        Assert.Equal(1, ProviderImageOptions.ImageDimensionGcd(1, 1));
        Assert.Equal(1, ProviderImageOptions.ImageDimensionGcd(0, 0));
        Assert.Equal(4, ProviderImageOptions.ImageDimensionGcd(8, 12));
    }

    // ------------------------------------------------------------ Grok 归一化

    [Theory]
    [InlineData("1k", "1k")]
    [InlineData("low", "1k")]
    [InlineData("standard", "1k")]
    [InlineData("2k", "2k")]
    [InlineData("medium", "2k")]
    [InlineData("hd", "2k")]
    [InlineData("high", "2k")]
    [InlineData("4k", "2k")]
    [InlineData("auto", "")]
    [InlineData("", "")]
    [InlineData("weird", "")]
    public void Grok分辨率_4k夹到2k(string value, string expected) =>
        Assert.Equal(expected, ProviderImageOptions.NormalizeGrokImageResolution(value));

    [Theory]
    [InlineData("1:1", "1:1")]
    [InlineData("9:19.5", "9:19.5")]
    [InlineData("16:9", "16:9")]
    [InlineData("3:5", "")]
    public void Grok比例_白名单内的冒号形式(string value, string expected) =>
        Assert.Equal(expected, ProviderImageOptions.NormalizeGrokImageAspectRatio(value));

    [Theory]
    [InlineData("1024x1024", "1:1")]
    [InlineData("1280x720", "16:9")]
    [InlineData("720x1280", "9:16")]
    [InlineData("1824x1024", "16:9")]
    [InlineData("auto", "")]
    [InlineData("", "")]
    [InlineData("abc", "")]
    public void Grok比例_像素形式(string value, string expected) =>
        Assert.Equal(expected, ProviderImageOptions.NormalizeGrokImageAspectRatio(value));

    [Theory]
    // 这两个是 Go 注释里点名的回归用例：只靠 w>h 兜底会把它们错标。
    [InlineData("768x1152", "2:3")]
    [InlineData("1152x768", "3:2")]
    public void Grok比例_像素路径显式覆盖2比3与3比2(string value, string expected) =>
        Assert.Equal(expected, ProviderImageOptions.NormalizeGrokImageAspectRatio(value));

    [Fact]
    public void Grok比例_全角乘号等价()
    {
        Assert.Equal("16:9", ProviderImageOptions.NormalizeGrokImageAspectRatio("1280×720"));
    }

    // ------------------------------------------------------------ 视频归一化

    [Theory]
    [InlineData("9:16", "720x1280")]
    [InlineData("2:3", "720x1280")]
    [InlineData("3:4", "720x1280")]
    [InlineData("16:9", "1280x720")]
    [InlineData("1:1", "1280x720")]
    [InlineData("1280x720", "1280x720")]
    [InlineData("auto", "")]
    [InlineData("", "")]
    public void 视频尺寸(string value, string expected) =>
        Assert.Equal(expected, ProviderImageOptions.NormalizeVideoSize(value));

    [Theory]
    [InlineData("", "720p")]
    [InlineData("auto", "720p")]
    [InlineData("medium", "720p")]
    [InlineData("high", "720p")]
    [InlineData("low", "480p")]
    [InlineData("4k", "2160p")]
    [InlineData("2k", "1440p")]
    [InlineData("1080p", "1080p")]
    [InlineData("720", "720p")]
    public void 视频分辨率_默认720p且未识别值补p(string value, string expected) =>
        Assert.Equal(expected, ProviderImageOptions.NormalizeVideoResolution(value));

    // ------------------------------------------------------------ 方舟尺寸夹取

    [Fact]
    public void 方舟尺寸_区间内不变()
    {
        // 3_686_400 <= 1920*1920=3_686_400 <= 4_624_220。
        Assert.Equal("1920x1920", ProviderImageOptions.NormalizeVolcengineArkImageSize("1920x1920"));
    }

    [Fact]
    public void 方舟尺寸_低于下限时放大到区间内()
    {
        string result = ProviderImageOptions.NormalizeVolcengineArkImageSize("1024x1024");

        (int width, int height) = ParseSize(result);
        long pixels = (long)width * height;
        Assert.InRange(pixels, ProviderImageOptions.VolcengineArkImageMinPixels,
            ProviderImageOptions.VolcengineArkImageMaxPixels);
        Assert.Equal(0, width % 2);
        Assert.Equal(0, height % 2);
    }

    [Fact]
    public void 方舟尺寸_高于上限时缩小到区间内()
    {
        string result = ProviderImageOptions.NormalizeVolcengineArkImageSize("4096x4096");

        (int width, int height) = ParseSize(result);
        long pixels = (long)width * height;
        Assert.InRange(pixels, ProviderImageOptions.VolcengineArkImageMinPixels,
            ProviderImageOptions.VolcengineArkImageMaxPixels);
    }

    [Fact]
    public void 方舟尺寸_保持宽高比()
    {
        string result = ProviderImageOptions.NormalizeVolcengineArkImageSize("1024x1824");

        (int width, int height) = ParseSize(result);
        double original = 1024.0 / 1824.0;
        double actual = (double)width / height;
        Assert.InRange(actual, original * 0.98, original * 1.02);
    }

    [Fact]
    public void 方舟尺寸_经比例预设转换后再夹取()
    {
        string result = ProviderImageOptions.NormalizeVolcengineArkImageSize("1:1");

        (int width, int height) = ParseSize(result);
        Assert.Equal(width, height);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    public void 方舟尺寸_无像素时原样返回(string value) =>
        Assert.Equal(ProviderImageOptions.NormalizePixelSize(value),
            ProviderImageOptions.NormalizeVolcengineArkImageSize(value));

    [Fact]
    public void 方舟像素常量与Go一致()
    {
        Assert.Equal(3_686_400, ProviderImageOptions.VolcengineArkImageMinPixels);
        Assert.Equal(4_624_220, ProviderImageOptions.VolcengineArkImageMaxPixels);
    }

    private static (int Width, int Height) ParseSize(string value)
    {
        string[] parts = value.Split('x');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }
}
