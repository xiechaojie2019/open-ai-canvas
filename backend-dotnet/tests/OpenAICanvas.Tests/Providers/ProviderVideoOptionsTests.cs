#nullable enable
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 视频参数归一化与能力裁剪的契约测试。
/// 对应 Go: <c>provider_video_options.go</c> 与 <c>provider_video.go</c> 的归一化函数。
/// </summary>
public sealed class ProviderVideoOptionsTests
{
    // ------------------------------------------------------------ videoResolutionNameRequest

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("default")]
    [InlineData("medium")]
    [InlineData("high")]
    public void 分辨率名_自动档位返回空(string value)
    {
        VideoCapabilityConfig profile = new() { Resolutions = ["720p"] };

        Assert.Equal("", ProviderVideoOptions.VideoResolutionNameRequest(profile, value));
    }

    [Fact]
    public void 分辨率名_未声明能力返回空()
    {
        // 与图片的"未声明即支持"相反：视频分辨率必须由能力显式声明。
        Assert.Equal("", ProviderVideoOptions.VideoResolutionNameRequest(null, "1080p"));
    }

    [Fact]
    public void 分辨率名_能力无分辨率列表返回空()
    {
        VideoCapabilityConfig profile = new() { Resolutions = [] };

        Assert.Equal("", ProviderVideoOptions.VideoResolutionNameRequest(profile, "1080p"));
    }

    [Fact]
    public void 分辨率名_精确命中返回声明的写法()
    {
        VideoCapabilityConfig profile = new() { Resolutions = ["720P", "1080p"] };

        // 返回声明里的原始写法（渠道可能要求特定大小写）。
        Assert.Equal("1080p", ProviderVideoOptions.VideoResolutionNameRequest(profile, "1080p"));
        Assert.Equal("720P", ProviderVideoOptions.VideoResolutionNameRequest(profile, "720P"));
    }

    [Fact]
    public void 分辨率名_medium与high在早退列表内()
    {
        VideoCapabilityConfig profile = new() { Resolutions = ["720p"] };

        // "medium"/"high" 属于模糊档位，在归一化之前就被早退，不会去匹配声明列表。
        Assert.Equal("", ProviderVideoOptions.VideoResolutionNameRequest(profile, "medium"));
        Assert.Equal("", ProviderVideoOptions.VideoResolutionNameRequest(profile, "high"));
    }

    [Fact]
    public void 分辨率名_经归一化后命中()
    {
        VideoCapabilityConfig profile = new() { Resolutions = ["480p"] };

        // "low" 不在早退列表内，归一化为 480p 后与声明匹配。
        Assert.Equal("480p", ProviderVideoOptions.VideoResolutionNameRequest(profile, "low"));
    }

    [Fact]
    public void 分辨率名_4k与2160p互认()
    {
        VideoCapabilityConfig declared4k = new() { Resolutions = ["4k"] };
        VideoCapabilityConfig declared2160 = new() { Resolutions = ["2160p"] };

        // 渠道声明写法不可控，两个方向都要认。
        Assert.Equal("4k", ProviderVideoOptions.VideoResolutionNameRequest(declared4k, "2160p"));
        Assert.Equal("2160p", ProviderVideoOptions.VideoResolutionNameRequest(declared2160, "4k"));
    }

    [Fact]
    public void 分辨率名_不在声明列表内返回空()
    {
        VideoCapabilityConfig profile = new() { Resolutions = ["720p"] };

        Assert.Equal("", ProviderVideoOptions.VideoResolutionNameRequest(profile, "1080p"));
    }

    [Fact]
    public void 固定分辨率_只声明一个时改写请求值()
    {
        ProviderConfig config = new() { VQuality = "1080p" };
        VideoCapabilityConfig profile = new() { Resolutions = ["720p"] };

        ProviderVideoOptions.ApplyFixedVideoResolution(config, profile);

        Assert.Equal("720p", config.VQuality);
    }

    [Fact]
    public void 固定分辨率_多个声明时不改写()
    {
        ProviderConfig config = new() { VQuality = "1080p" };
        VideoCapabilityConfig profile = new() { Resolutions = ["720p", "1080p"] };

        ProviderVideoOptions.ApplyFixedVideoResolution(config, profile);

        Assert.Equal("1080p", config.VQuality);
    }

    // ------------------------------------------------------------ Seedance 时长

    [Theory]
    [InlineData("-1", -1)]      // -1 表示"由模型决定"，必须原样保留
    [InlineData("5", 5)]
    [InlineData("12", 12)]
    [InlineData(" 8 ", 8)]
    [InlineData("0", 5)]
    [InlineData("-5", 5)]       // 除 -1 外的负数回落
    [InlineData("abc", 5)]
    [InlineData("", 5)]
    [InlineData(null, 5)]
    [InlineData("1.5", 5)]
    public void Seedance时长(string? value, int expected) =>
        Assert.Equal(expected, ProviderVideoOptions.NormalizeSeedanceDuration(value));

    // ------------------------------------------------------------ Seedance 比例

    [Theory]
    [InlineData("16:9", "16:9")]
    [InlineData("9:16", "9:16")]
    [InlineData("1:1", "1:1")]
    [InlineData("4:3", "4:3")]
    [InlineData("3:4", "3:4")]
    [InlineData("21:9", "21:9")]
    [InlineData("", "adaptive")]
    [InlineData("auto", "adaptive")]
    [InlineData("adaptive", "adaptive")]
    [InlineData("1024x1024", "adaptive")]   // 像素尺寸不在白名单
    [InlineData("2:3", "adaptive")]         // 不在白名单
    public void Seedance比例_白名单外回落adaptive(string value, string expected) =>
        Assert.Equal(expected, ProviderVideoOptions.NormalizeSeedanceRatio(value));

    [Theory]
    [InlineData("16:9", "16:9")]
    [InlineData("auto", "16:9")]
    [InlineData("", "16:9")]
    [InlineData("2:3", "16:9")]
    public void SeedanceVideos比例_adaptive回落16比9(string value, string expected) =>
        Assert.Equal(expected, ProviderVideoOptions.NormalizeSeedanceVideosRatio(value));

    // ------------------------------------------------------------ Seedance 分辨率

    [Theory]
    [InlineData("480p", "480p")]
    [InlineData("720p", "720p")]
    [InlineData("1080p", "1080p")]
    [InlineData("2160p", "2160p")]
    [InlineData("720", "720p")]         // 无 p 后缀也认
    [InlineData("4k", "2160p")]
    [InlineData("4K", "2160p")]
    [InlineData("low", "480p")]
    [InlineData("high", "720p")]        // high 不在档位表 → 720p
    [InlineData("weird", "720p")]
    [InlineData("", "720p")]
    public void Seedance分辨率(string value, string expected) =>
        Assert.Equal(expected, ProviderVideoOptions.NormalizeSeedanceResolution(value, "seedance-1.0"));

    [Theory]
    [InlineData("1080p", "720p")]
    [InlineData("2160p", "720p")]
    [InlineData("4k", "720p")]
    [InlineData("720p", "720p")]
    [InlineData("480p", "480p")]
    public void Seedance分辨率_fast模型压回720p(string value, string expected) =>
        // fast 模型不支持 1080p 以上，上游会直接报错，这里提前修正以免浪费计费请求。
        Assert.Equal(expected, ProviderVideoOptions.NormalizeSeedanceResolution(value, "seedance-1.0-fast"));

    [Fact]
    public void Seedance分辨率_fast判定大小写不敏感() =>
        Assert.Equal("720p", ProviderVideoOptions.NormalizeSeedanceResolution("1080p", "Seedance-FAST"));

    // ------------------------------------------------------------ parseBool / parseFloat

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("", true)]          // 回落 fallback
    [InlineData("yes", true)]       // 不认 yes，回落 fallback
    [InlineData("1", true)]
    public void 三态布尔_只认真假字面量(string value, bool fallback)
    {
        Assert.Equal(value.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => fallback,
        }, ProviderVideoOptions.ParseBool(value, fallback));
    }

    [Fact]
    public void 三态布尔_默认值由调用方指定()
    {
        Assert.True(ProviderVideoOptions.ParseBool("", true));
        Assert.False(ProviderVideoOptions.ParseBool("", false));
        Assert.False(ProviderVideoOptions.ParseBool("yes", false));
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData(" 2 ", 2.0)]
    [InlineData("-3.25", -3.25)]
    public void 浮点解析_有效值(string value, double expected) =>
        Assert.Equal(expected, ProviderVideoOptions.ParseFloat(value, 9.0), 5);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]       // 0 也回落（Go 的 number == 0 判定）
    [InlineData("0.0")]
    [InlineData(null)]
    public void 浮点解析_非法或零回落(string? value) =>
        Assert.Equal(9.0, ProviderVideoOptions.ParseFloat(value, 9.0), 5);

    // ------------------------------------------------------------ 参考图发送判定

    [Fact]
    public void 发送参考图_无元信息时为真() =>
        Assert.True(ProviderVideoOptions.ShouldSendNewApiVideoImages(null, []));

    [Fact]
    public void 发送参考图_文生视频时为假()
    {
        Dictionary<string, object?> metadata = new(StringComparer.Ordinal)
        {
            ["videoEditOperation"] = "text_to_video",
        };

        Assert.False(ProviderVideoOptions.ShouldSendNewApiVideoImages(metadata, []));
    }

    [Theory]
    [InlineData("image_to_video")]
    [InlineData("reference_to_video")]
    [InlineData("")]
    public void 发送参考图_其它操作为真(string operation)
    {
        Dictionary<string, object?> metadata = new(StringComparer.Ordinal)
        {
            ["videoEditOperation"] = operation,
        };

        Assert.True(ProviderVideoOptions.ShouldSendNewApiVideoImages(metadata, []));
    }

    // ------------------------------------------------------------ 角色判定

    [Fact]
    public void 角色_默认回落()
    {
        ProviderMedia image = new() { ID = "n1" };

        Assert.Equal("reference_image",
            ProviderVideoOptions.VideoImageRoleOrDefault(null, image, "reference_image"));
    }

    [Fact]
    public void 角色_首帧尾帧()
    {
        Dictionary<string, object?> metadata = new(StringComparer.Ordinal)
        {
            ["videoStartFrameNodeId"] = "start",
            ["videoEndFrameNodeId"] = "end",
        };

        // 注意：Seedance 用 first_frame/last_frame（与声明式协议的 start_frame/end_frame 不同）。
        Assert.Equal("first_frame",
            ProviderVideoOptions.VideoImageRoleOrDefault(metadata, new ProviderMedia { ID = "start" }, "x"));
        Assert.Equal("last_frame",
            ProviderVideoOptions.VideoImageRoleOrDefault(metadata, new ProviderMedia { ID = "end" }, "x"));
        Assert.Equal("x",
            ProviderVideoOptions.VideoImageRoleOrDefault(metadata, new ProviderMedia { ID = "other" }, "x"));
    }

    [Fact]
    public void 角色_参考转视频时全部为referenceImage()
    {
        Dictionary<string, object?> metadata = new(StringComparer.Ordinal)
        {
            ["videoEditOperation"] = "reference_to_video",
            ["videoStartFrameNodeId"] = "start",
        };

        // reference_to_video 优先于首尾帧判定。
        Assert.Equal("reference_image",
            ProviderVideoOptions.VideoImageRoleOrDefault(metadata, new ProviderMedia { ID = "start" }, "x"));
    }

    // ------------------------------------------------------------ 帧序重排

    [Fact]
    public void 帧序_未配置首尾帧返回空()
    {
        List<string> result = ProviderVideoOptions.VideoFrameImageUrls(
            null, [new ProviderMedia { ID = "a" }], ["u1"]);

        Assert.Empty(result);
    }

    [Fact]
    public void 帧序_reference_to_video返回空()
    {
        Dictionary<string, object?> metadata = new(StringComparer.Ordinal)
        {
            ["videoEditOperation"] = "reference_to_video",
            ["videoStartFrameNodeId"] = "a",
        };

        List<string> result = ProviderVideoOptions.VideoFrameImageUrls(
            metadata, [new ProviderMedia { ID = "a" }], ["u1"]);

        Assert.Empty(result);
    }

    [Fact]
    public void 帧序_首尾帧排在最前()
    {
        Dictionary<string, object?> metadata = new(StringComparer.Ordinal)
        {
            ["videoStartFrameNodeId"] = "s",
            ["videoEndFrameNodeId"] = "e",
        };
        List<ProviderMedia> images =
        [
            new() { ID = "x" },
            new() { ID = "e" },
            new() { ID = "s" },
        ];

        List<string> result = ProviderVideoOptions.VideoFrameImageUrls(
            metadata, images, ["ux", "ue", "us"]);

        Assert.Equal(["us", "ue", "ux"], result);
    }

    [Fact]
    public void 帧序_只配首帧()
    {
        Dictionary<string, object?> metadata = new(StringComparer.Ordinal)
        {
            ["videoStartFrameNodeId"] = "s",
        };
        List<ProviderMedia> images = [new() { ID = "x" }, new() { ID = "s" }];

        List<string> result = ProviderVideoOptions.VideoFrameImageUrls(
            metadata, images, ["ux", "us"]);

        Assert.Equal(["us", "ux"], result);
    }

    [Fact]
    public void 帧序_节点ID找不到对应参考图时报错()
    {
        Dictionary<string, object?> metadata = new(StringComparer.Ordinal)
        {
            ["videoStartFrameNodeId"] = "missing",
        };

        // 静默丢帧会让用户以为首尾帧生效了，必须失败。
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderVideoOptions.VideoFrameImageUrls(
                metadata, [new ProviderMedia { ID = "x" }], ["ux"]));

        Assert.Equal("已配置的首帧参考图未包含在视频请求中", error.Message);
    }

    [Fact]
    public void 帧序_尾帧报错文案区分()
    {
        Dictionary<string, object?> metadata = new(StringComparer.Ordinal)
        {
            ["videoStartFrameNodeId"] = "s",
            ["videoEndFrameNodeId"] = "missing",
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderVideoOptions.VideoFrameImageUrls(
                metadata, [new ProviderMedia { ID = "s" }], ["us"]));

        Assert.Equal("已配置的尾帧参考图未包含在视频请求中", error.Message);
    }

    // ------------------------------------------------------------ 素材 URL

    [Fact]
    public void 素材URL_公网地址直接返回() =>
        Assert.Equal("https://x/a.png", ProviderVideoOptions.MediaReferenceUrl(
            new ProviderMedia { URL = "https://x/a.png" }));

    [Fact]
    public void 素材URL_asset协议被接受() =>
        // asset:// 是画布内部素材 ID，Seedance Agent Plan 支持。
        Assert.Equal("asset://abc", ProviderVideoOptions.MediaReferenceUrl(
            new ProviderMedia { URL = "asset://abc" }));

    [Fact]
    public void 素材URL_回落到dataURL() =>
        Assert.Equal("data:image/png;base64,AA", ProviderVideoOptions.MediaReferenceUrl(
            new ProviderMedia { DataURL = "data:image/png;base64,AA" }));

    [Fact]
    public void 素材URL_都不可用时报错()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderVideoOptions.MediaReferenceUrl(new ProviderMedia { URL = "ftp://x/a.png" }));

        Assert.Equal("参考素材需要公网 URL、asset:// 素材 ID 或 data URL", error.Message);
    }

    [Fact]
    public void SeedanceVideos素材URL_不接受asset协议()
    {
        // /videos 只认公网 URL 或 data URL，asset:// 无法被上游解析。
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderVideoOptions.SeedanceVideosMediaUrl(new ProviderMedia { URL = "asset://abc" }));

        Assert.Equal("Seedance /videos 参考素材需要公网 URL 或 data URL", error.Message);
    }

    [Fact]
    public void SeedanceVideos素材URL_dataURL优先()
    {
        Assert.Equal("data:video/mp4;base64,AA", ProviderVideoOptions.SeedanceVideosMediaUrl(
            new ProviderMedia { DataURL = "data:video/mp4;base64,AA", URL = "https://x/v.mp4" }));
    }

    // ------------------------------------------------------------ Seedance 错误文案

    [Fact]
    public void 错误文案_错误对象带码与消息()
    {
        Dictionary<string, object?> state = new(StringComparer.Ordinal)
        {
            ["error"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "InvalidParameter",
                ["message"] = "prompt too long",
            },
        };

        Assert.Equal("InvalidParameter：prompt too long", ProviderVideoOptions.SeedanceErrorMessage(state));
    }

    [Fact]
    public void 错误文案_只有消息()
    {
        Dictionary<string, object?> state = new(StringComparer.Ordinal)
        {
            ["error"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "bad" },
        };

        Assert.Equal("bad", ProviderVideoOptions.SeedanceErrorMessage(state));
    }

    [Fact]
    public void 错误文案_回落到error_code()
    {
        Dictionary<string, object?> state = new(StringComparer.Ordinal) { ["error_code"] = "E123" };

        Assert.Equal("E123", ProviderVideoOptions.SeedanceErrorMessage(state));
    }

    [Fact]
    public void 错误文案_都没有时为空() =>
        Assert.Equal("", ProviderVideoOptions.SeedanceErrorMessage(
            new Dictionary<string, object?>(StringComparer.Ordinal)));

    // ------------------------------------------------------------ 能力开关

    [Fact]
    public void 能力开关_未声明时视为支持()
    {
        Assert.True(ProviderVideoOptions.VideoCapabilitySupportsAudio(null));
        Assert.True(ProviderVideoOptions.VideoCapabilitySupportsWatermark(null));
    }

    [Fact]
    public void 能力开关_按声明裁剪()
    {
        VideoCapabilityConfig profile = new()
        {
            GenerateAudio = new VideoBooleanConfig { Supported = false },
            Watermark = new VideoBooleanConfig { Supported = true },
        };

        Assert.False(ProviderVideoOptions.VideoCapabilitySupportsAudio(profile));
        Assert.True(ProviderVideoOptions.VideoCapabilitySupportsWatermark(profile));
    }
}
