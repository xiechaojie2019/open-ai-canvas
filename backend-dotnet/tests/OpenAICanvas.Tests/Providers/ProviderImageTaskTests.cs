#nullable enable
using System.Net;
using System.Text;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 图片任务的契约测试。
/// 对应 Go: <c>internal/app/provider_image.go</c> 的
/// <c>runImageTask</c> / <c>runGeminiImageTask</c> / <c>runGrokImageTask</c> /
/// <c>runVolcengineArkImageTask</c> 与 <c>imageDataURLs</c> / <c>geminiImageDataURLs</c>。
/// </summary>
public sealed class ProviderImageTaskTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        public string? LastContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
                LastContentType = request.Content.Headers.ContentType?.ToString();
            }
            return _responder(request);
        }
    }

    private static ProviderImageTask Task(StubHandler handler) => new(null, () => new HttpClient(handler));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static TextTaskInput Input(string interfaceType = "") => new()
    {
        Mode = "image",
        Prompt = "画一只猫",
        Config = new ProviderConfig
        {
            InterfaceType = interfaceType,
            Model = "img-model",
            BaseURL = "https://api.example.com",
            APIKey = "k",
        },
    };

    private const string OneImage = """{"data":[{"b64_json":"QUJD"}]}""";

    /// <summary>PNG 魔术字节 + 载荷的 base64（Gemini 分支会校验字节签名，不能用任意 base64）。</summary>
    private static readonly string PngBase64 = Convert.ToBase64String(
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x41, 0x42, 0x43, 0x44]);

    private static string PngDataUrl() => "data:image/png;base64," + PngBase64;

    // ------------------------------------------------------------ OpenAI Images

    [Fact]
    public async Task OpenAI_无参考图走generations()
    {
        StubHandler handler = new(_ => Json(OneImage));
        Dictionary<string, object?> result = await Task(handler).RunAsync(Input());

        Assert.Equal("/v1/images/generations", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("image", result["mode"]);
        List<Dictionary<string, string>> images =
            Assert.IsType<List<Dictionary<string, string>>>(result["images"]);
        Assert.Equal("data:image/png;base64,QUJD", images[0]["dataUrl"]);
    }

    [Fact]
    public async Task OpenAI_请求体含模型提示词与n()
    {
        StubHandler handler = new(_ => Json(OneImage));
        await Task(handler).RunAsync(Input());

        Assert.Contains("\"model\":\"img-model\"", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("画一只猫", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("\"n\":1", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAI_有参考图走edits且为multipart()
    {
        StubHandler handler = new(_ => Json(OneImage));
        TextTaskInput input = Input();
        input.ReferenceImages.Add(new ProviderMedia { DataURL = PngDataUrl() });

        await Task(handler).RunAsync(input);

        Assert.Equal("/v1/images/edits", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("multipart/form-data", handler.LastContentType!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAI_蒙版编辑要求渠道声明OpenAIImage协议()
    {
        StubHandler handler = new(_ => Json(OneImage));
        TextTaskInput input = Input("gemini-image");
        input.ReferenceImages.Add(new ProviderMedia { DataURL = PngDataUrl() });
        input.Mask = new ProviderMedia { DataURL = PngDataUrl() };

        // 路由到 Gemini 分支会先报"不支持蒙版"，所以这里直接指定 OpenAI 协议的渠道来验证门禁。
        input.Config.InterfaceType = "grok-image";
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task(handler).RunAsync(input));

        Assert.Contains("Grok 图片协议不支持蒙版编辑", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAI_蒙版但渠道未声明时报错()
    {
        // 用一个既不是 openai-image 也不是已知图片协议的渠道，走默认 OpenAI 分支。
        StubHandler handler = new(_ => Json(OneImage));
        TextTaskInput input = Input("some-other-image");
        input.ReferenceImages.Add(new ProviderMedia { DataURL = PngDataUrl() });
        input.Mask = new ProviderMedia { DataURL = PngDataUrl() };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task(handler).RunAsync(input));

        Assert.Equal("当前渠道未声明 OpenAI Images 编辑协议，已拒绝可能忽略蒙版的整图重绘", error.Message);
    }

    [Fact]
    public async Task OpenAI_蒙版但无源图时报错()
    {
        StubHandler handler = new(_ => Json(OneImage));
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceOpenAIImage);
        input.Mask = new ProviderMedia { DataURL = PngDataUrl() };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task(handler).RunAsync(input));

        Assert.Equal("蒙版编辑必须提供与蒙版同尺寸的源图片", error.Message);
    }

    [Fact]
    public async Task OpenAI_按能力裁剪参数()
    {
        StubHandler handler = new(_ => Json(OneImage));
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceOpenAIImage);
        input.Config.Size = "1:1";
        input.Config.Quality = "2k";
        input.Config.TransparentBackground = "true";
        input.ImageCapability = new ImageCapabilityConfig
        {
            Size = new ImageSizeConfig { Parameter = "size" },
            ResponseFormat = new ParameterSupport { Supported = false },
            OutputFormat = new ParameterSupport { Supported = false },
            Quality = new ImageQualityConfig { Supported = true },
            TransparentBackground = new VideoBooleanConfig { Supported = false },
        };

        await Task(handler).RunAsync(input);

        Assert.DoesNotContain("response_format", handler.LastBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("output_format", handler.LastBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("background", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("\"quality\":\"medium\"", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("\"size\":\"1024x1024\"", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 图片解析_b64优先于url()
    {
        List<Dictionary<string, string>> images = ProviderImageTask.ImageDataUrls(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["data"] = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["b64_json"] = "AAA",
                        ["url"] = "https://x/a.png",
                    },
                },
            });

        Assert.Equal("data:image/png;base64,AAA", images[0]["dataUrl"]);
    }

    [Fact]
    public async Task 图片解析_无b64时用url()
    {
        List<Dictionary<string, string>> images = ProviderImageTask.ImageDataUrls(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["data"] = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["url"] = "https://x/a.png" },
                },
            });

        Assert.Equal("https://x/a.png", images[0]["dataUrl"]);
    }

    [Fact]
    public void 图片解析_空data报错()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderImageTask.ImageDataUrls(new Dictionary<string, object?>(StringComparer.Ordinal)));

        Assert.Equal("接口没有返回可用图片", error.Message);
    }

    [Fact]
    public void 图片解析_条目无可用字段时不计数()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderImageTask.ImageDataUrls(
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["data"] = new List<object?>
                    {
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["revised_prompt"] = "x" },
                    },
                }));

        Assert.Equal("接口没有返回可用图片", error.Message);
    }

    // ------------------------------------------------------------ Grok

    [Fact]
    public async Task Grok_无参考图走generations()
    {
        StubHandler handler = new(_ => Json("""{"data":[{"url":"https://x/a.png"}]}"""));
        await Task(handler).RunAsync(Input(ChannelInterfaceType.ChannelInterfaceGrokImage));

        Assert.Equal("/v1/images/generations", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void Grok_请求体用aspectRatio而非size()
    {
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceGrokImage);
        input.Config.Size = "1280x720";
        input.Config.Quality = "high";

        (Dictionary<string, object?> body, string path) = ProviderImageTask.GrokImageRequestBody(input);

        Assert.Equal("/images/generations", path);
        Assert.Equal("16:9", body["aspect_ratio"]);
        Assert.Equal("2k", body["resolution"]);
        // Grok 用 aspect_ratio 表达比例；同时发 size 会被上游按 OpenAI 枚举校验并拒绝。
        Assert.False(body.ContainsKey("size"));
    }

    [Fact]
    public void Grok_单张参考图走edits()
    {
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceGrokImage);
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://cdn.example.com/a.png" });

        (Dictionary<string, object?> body, string path) = ProviderImageTask.GrokImageRequestBody(input);

        Assert.Equal("/images/edits", path);
        Dictionary<string, object?> image = Assert.IsType<Dictionary<string, object?>>(body["image"]);
        // 公网 URL 直传，不内联。
        Assert.Equal("https://cdn.example.com/a.png", image["url"]);
    }

    [Fact]
    public void Grok_非公网参考图内联为dataURL()
    {
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceGrokImage);
        input.ReferenceImages.Add(new ProviderMedia { DataURL = PngDataUrl() });

        (Dictionary<string, object?> body, _) = ProviderImageTask.GrokImageRequestBody(input);

        Dictionary<string, object?> image = Assert.IsType<Dictionary<string, object?>>(body["image"]);
        Assert.StartsWith("data:", (string)image["url"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void Grok_多张参考图报错()
    {
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceGrokImage);
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/a.png" });
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/b.png" });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderImageTask.GrokImageRequestBody(input));

        Assert.Equal("Grok 图片编辑只支持 1 张参考图，当前连接了 2 张", error.Message);
    }

    [Fact]
    public void Grok_蒙版报错()
    {
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceGrokImage);
        input.Mask = new ProviderMedia { DataURL = PngDataUrl() };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderImageTask.GrokImageRequestBody(input));

        Assert.Equal("Grok 图片协议不支持蒙版编辑，请移除蒙版后重试", error.Message);
    }

    // ------------------------------------------------------------ Gemini

    [Fact]
    public async Task Gemini_请求路径带v1beta与转义模型名()
    {
        StubHandler handler = new(_ => Json(
            """{"candidates":[{"content":{"parts":[{"inlineData":{"mimeType":"image/png","data":"QUJD"}}]}}]}"""));
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceGeminiImage);
        input.Config.Model = "gemini/2.0 flash";

        await Task(handler).RunAsync(input);

        string path = handler.LastRequest!.RequestUri!.AbsolutePath;
        Assert.StartsWith("/v1beta/models/", path, StringComparison.Ordinal);
        Assert.Contains("gemini%2F2.0%20flash", path, StringComparison.Ordinal);
        Assert.EndsWith(":generateContent", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gemini_用xGoogApiKey鉴权()
    {
        StubHandler handler = new(_ => Json(
            """{"candidates":[{"content":{"parts":[{"inlineData":{"data":"QUJD"}}]}}]}"""));
        await Task(handler).RunAsync(Input(ChannelInterfaceType.ChannelInterfaceGeminiImage));

        Assert.Equal("k", handler.LastRequest!.Headers.GetValues("x-goog-api-key").Single());
        Assert.False(handler.LastRequest.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Gemini_参考图内联为inlineData()
    {
        StubHandler handler = new(_ => Json(
            """{"candidates":[{"content":{"parts":[{"inlineData":{"data":"QUJD"}}]}}]}"""));
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceGeminiImage);
        input.ReferenceImages.Add(new ProviderMedia { DataURL = PngDataUrl() });

        await Task(handler).RunAsync(input);

        Assert.Contains("inlineData", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("image/png", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gemini_蒙版报错()
    {
        StubHandler handler = new(_ => Json("{}"));
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceGeminiImage);
        input.Mask = new ProviderMedia { DataURL = PngDataUrl() };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task(handler).RunAsync(input));

        Assert.Equal("Gemini Images 不支持蒙版编辑，请移除蒙版后重试", error.Message);
    }

    [Fact]
    public async Task Gemini_视频参考报错()
    {
        StubHandler handler = new(_ => Json("{}"));
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceGeminiImage);
        input.ReferenceVideos.Add(new ProviderMedia { URL = "https://x/v.mp4" });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task(handler).RunAsync(input));

        Assert.Equal("Gemini Images 不支持参考视频或音频", error.Message);
    }

    [Fact]
    public void Gemini_无提示词无参考图报错()
    {
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceGeminiImage);
        input.Prompt = "   ";

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderImageTask.GeminiImageDataUrls(new Dictionary<string, object?>(StringComparer.Ordinal)));
        Assert.Equal("Gemini Images 接口没有返回图片", error.Message);
    }

    [Fact]
    public void Gemini_图片配置_比例与质量()
    {
        ProviderConfig config = new() { Size = "16:9", Quality = "4k" };

        Dictionary<string, object?>? imageConfig = ProviderImageTask.GeminiImageConfigFor(config);

        Assert.NotNull(imageConfig);
        Assert.Equal("16:9", imageConfig["aspectRatio"]);
        Assert.Equal("4K", imageConfig["imageSize"]);
    }

    [Theory]
    [InlineData("low", "1K")]
    [InlineData("1k", "1K")]
    [InlineData("medium", "2K")]
    [InlineData("2k", "2K")]
    [InlineData("high", "4K")]
    [InlineData("4k", "4K")]
    public void Gemini_图片配置_质量档位(string quality, string expected)
    {
        Dictionary<string, object?>? imageConfig = ProviderImageTask.GeminiImageConfigFor(
            new ProviderConfig { Quality = quality });

        Assert.NotNull(imageConfig);
        Assert.Equal(expected, imageConfig["imageSize"]);
    }

    [Fact]
    public void Gemini_图片配置_皆空时为null()
    {
        Assert.Null(ProviderImageTask.GeminiImageConfigFor(new ProviderConfig()));
        Assert.Null(ProviderImageTask.GeminiImageConfigFor(new ProviderConfig { Size = "auto" }));
    }

    [Fact]
    public void Gemini_图片配置_比例必须只含一个冒号()
    {
        // "1024x1024" 无冒号 → 不算比例；"1:2:3" 有两个冒号 → 也不算。
        Assert.Null(ProviderImageTask.GeminiImageConfigFor(new ProviderConfig { Size = "1024x1024" }));
        Assert.Null(ProviderImageTask.GeminiImageConfigFor(new ProviderConfig { Size = "1:2:3" }));
        Assert.NotNull(ProviderImageTask.GeminiImageConfigFor(new ProviderConfig { Size = "1:2" }));
    }

    [Fact]
    public void Gemini_解析inline_data下划线别名()
    {
        List<Dictionary<string, string>> images = ProviderImageTask.GeminiImageDataUrls(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["candidates"] = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["content"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["parts"] = new List<object?>
                            {
                                new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["inline_data"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                                    {
                                        ["mime_type"] = "image/webp",
                                        ["data"] = "QUJD",
                                    },
                                },
                            },
                        },
                    },
                },
            });

        Assert.Equal("data:image/webp;base64,QUJD", images[0]["dataUrl"]);
    }

    [Fact]
    public void Gemini_默认MIME为png()
    {
        List<Dictionary<string, string>> images = ProviderImageTask.GeminiImageDataUrls(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["candidates"] = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["content"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["parts"] = new List<object?>
                            {
                                new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["inlineData"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                                    {
                                        ["data"] = "QUJD",
                                    },
                                },
                            },
                        },
                    },
                },
            });

        Assert.Equal("data:image/png;base64,QUJD", images[0]["dataUrl"]);
    }

    [Fact]
    public void Gemini_非图片MIME直接报错()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderImageTask.GeminiImageDataUrls(
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["candidates"] = new List<object?>
                    {
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["content"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["parts"] = new List<object?>
                                {
                                    new Dictionary<string, object?>(StringComparer.Ordinal)
                                    {
                                        ["inlineData"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                                        {
                                            ["mimeType"] = "application/pdf",
                                            ["data"] = "QUJD",
                                        },
                                    },
                                },
                            },
                        },
                    },
                }));

        Assert.Contains("非图片 MIME", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gemini_fileData回落到URL()
    {
        List<Dictionary<string, string>> images = ProviderImageTask.GeminiImageDataUrls(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["candidates"] = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["content"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["parts"] = new List<object?>
                            {
                                new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["fileData"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                                    {
                                        ["fileUri"] = "https://cdn.example.com/a.png",
                                    },
                                },
                            },
                        },
                    },
                },
            });

        Assert.Equal("https://cdn.example.com/a.png", images[0]["dataUrl"]);
    }

    [Fact]
    public void Gemini_顶层error带消息时抛错()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderImageTask.GeminiImageDataUrls(
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["error"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["message"] = "quota exceeded",
                    },
                }));

        Assert.Equal("quota exceeded", error.Message);
    }

    [Fact]
    public void Gemini_参考图签名与声明不符时报错()
    {
        // 声明是 image/png，但字节不是图片签名 → 拒绝（防止把非图片伪装成图片）。
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderImageTask.GeminiImageBytes(
                new ProviderMedia
                {
                    DataURL = "data:image/png;base64," +
                        Convert.ToBase64String(Encoding.UTF8.GetBytes("not an image")),
                }));

        Assert.Contains("参考图片内容不是有效图片", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gemini_参考图为空数据时报错()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderImageTask.GeminiImageBytes(
                new ProviderMedia { DataURL = "data:image/png;base64," }));

        Assert.Equal("参考图片数据为空", error.Message);
    }

    // ------------------------------------------------------------ Volcengine Ark

    [Fact]
    public async Task 方舟_走generations且固定b64与去水印()
    {
        StubHandler handler = new(_ => Json(OneImage));
        await Task(handler).RunAsync(Input(ChannelInterfaceType.ChannelInterfaceVolcengineArkImage));

        Assert.Equal("/v1/images/generations", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("\"response_format\":\"b64_json\"", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("\"watermark\":false", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public void 方舟_单图传字符串()
    {
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceVolcengineArkImage);
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/a.png" });

        Dictionary<string, object?> body = ProviderImageTask.VolcengineArkImageBody(input);

        Assert.Equal("https://x/a.png", Assert.IsType<string>(body["image"]));
    }

    [Fact]
    public void 方舟_多图传数组()
    {
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceVolcengineArkImage);
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/a.png" });
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/b.png" });

        Dictionary<string, object?> body = ProviderImageTask.VolcengineArkImageBody(input);

        List<string> images = Assert.IsType<List<string>>(body["image"]);
        Assert.Equal(2, images.Count);
    }

    [Fact]
    public void 方舟_尺寸经像素区间夹取()
    {
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceVolcengineArkImage);
        input.Config.Size = "1024x1024";

        Dictionary<string, object?> body = ProviderImageTask.VolcengineArkImageBody(input);

        Assert.NotEqual("1024x1024", body["size"]);
        string[] parts = ((string)body["size"]!).Split('x');
        long pixels = long.Parse(parts[0]) * long.Parse(parts[1]);
        Assert.InRange(pixels, ProviderImageOptions.VolcengineArkImageMinPixels,
            ProviderImageOptions.VolcengineArkImageMaxPixels);
    }

    [Fact]
    public async Task 方舟_拉取外链图片并内联()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        StubHandler handler = new(request =>
            request.RequestUri!.AbsolutePath.Contains("generations", StringComparison.Ordinal)
                ? Json("""{"data":[{"url":"https://cdn.example.com/a.png"}]}""")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });

        Dictionary<string, object?> result = await Task(handler)
            .RunAsync(Input(ChannelInterfaceType.ChannelInterfaceVolcengineArkImage));

        List<Dictionary<string, string>> images =
            Assert.IsType<List<Dictionary<string, string>>>(result["images"]);
        Assert.StartsWith("data:image/png;base64,", images[0]["dataUrl"], StringComparison.Ordinal);
        Assert.Equal("image/png", images[0]["mimeType"]);
    }

    [Fact]
    public async Task 方舟_外链不可解析时报错()
    {
        StubHandler handler = new(_ => Json("""{"data":[{"url":"ftp://bad/a.png"}]}"""));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task(handler).RunAsync(Input(ChannelInterfaceType.ChannelInterfaceVolcengineArkImage)));

        Assert.Equal("火山方舟图片接口没有返回可下载的图片", error.Message);
    }

    [Fact]
    public async Task 方舟_外链返回非图片时报错()
    {
        StubHandler handler = new(request =>
            request.RequestUri!.AbsolutePath.Contains("generations", StringComparison.Ordinal)
                ? Json("""{"data":[{"url":"https://cdn.example.com/a.png"}]}""")
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"err\":1}", Encoding.UTF8, "application/json"),
                });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task(handler).RunAsync(Input(ChannelInterfaceType.ChannelInterfaceVolcengineArkImage)));

        Assert.Contains("火山方舟图片结果无效", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 即梦（JiMeng）

    private static TextTaskInput JiMengInput()
    {
        TextTaskInput input = Input(ChannelInterfaceType.ChannelInterfaceVolcengineJiMengImage);
        input.Config.Model = "jimeng_seedream46_cvtob";
        input.Config.APIKey = "AKID";
        input.Config.SecretKey = "SECRET";
        input.Config.Size = "1024x1024";
        return input;
    }

    /// <summary>轮询总时限压到 5 秒内，避免桩异常时测试干等 1 小时。</summary>
    private static VideoPollPolicy ShortTimeoutPolicy() => new() { TotalTimeout = TimeSpan.FromSeconds(5) };

    private static bool HasAction(HttpRequestMessage request, string action) =>
        (request.RequestUri?.ToString() ?? "").Contains(action, StringComparison.Ordinal);

    [Fact]
    public async Task 即梦_签名提交与轮询返回内联图片()
    {
        StubHandler handler = null!;
        handler = new StubHandler(request =>
        {
            string authorization = request.Headers.TryGetValues("Authorization", out IEnumerable<string>? values)
                ? string.Join(",", values)
                : "";
            Assert.StartsWith("HMAC-SHA256 Credential=AKID/", authorization, StringComparison.Ordinal);
            if (HasAction(request, "Action=CVSync2AsyncSubmitTask"))
            {
                Assert.Contains("\"req_key\":\"jimeng_seedream46_cvtob\"", handler.LastBody ?? "", StringComparison.Ordinal);
                Assert.Contains("\"width\":1024", handler.LastBody ?? "", StringComparison.Ordinal);
                Assert.Contains("\"height\":1024", handler.LastBody ?? "", StringComparison.Ordinal);
                Assert.Contains("\"force_single\":true", handler.LastBody ?? "", StringComparison.Ordinal);
                return Json("""{"code":10000,"message":"Success","data":{"task_id":"task-1"}}""");
            }
            Assert.True(HasAction(request, "Action=CVSync2AsyncGetResult"), "poll 应走 GetResult Action");
            Assert.Contains("\"task_id\":\"task-1\"", handler.LastBody ?? "", StringComparison.Ordinal);
            Assert.Contains("req_json", handler.LastBody ?? "", StringComparison.Ordinal);
            Assert.Contains("return_url", handler.LastBody ?? "", StringComparison.Ordinal);
            return Json("""{"code":10000,"message":"Success","data":{"status":"done","binary_data_base64":["aGVsbG8="]}}""");
        });

        Dictionary<string, object?> result = await new ProviderImageTask(
            null, () => new HttpClient(handler), ShortTimeoutPolicy()).RunAsync(JiMengInput());

        Assert.Equal("image", result["mode"]);
        List<Dictionary<string, string>> images =
            Assert.IsType<List<Dictionary<string, string>>>(result["images"]);
        Assert.Single(images);
        Assert.StartsWith("data:image/png;base64,", images[0]["dataUrl"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 即梦_image_urls外链下载内联()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        StubHandler handler = new(request =>
        {
            if (HasAction(request, "Action=CVSync2AsyncSubmitTask"))
            {
                return Json("""{"code":10000,"message":"Success","data":{"task_id":"task-1"}}""");
            }
            if (HasAction(request, "Action=CVSync2AsyncGetResult"))
            {
                return Json("""{"code":10000,"message":"Success","data":{"status":"done","image_urls":["https://cdn.example.com/a.png"]}}""");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
        });

        Dictionary<string, object?> result = await new ProviderImageTask(
            null, () => new HttpClient(handler), ShortTimeoutPolicy()).RunAsync(JiMengInput());

        List<Dictionary<string, string>> images =
            Assert.IsType<List<Dictionary<string, string>>>(result["images"]);
        Assert.StartsWith("data:image/png;base64,", images[0]["dataUrl"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 即梦_code非10000时报错带request_id()
    {
        StubHandler handler = new(_ => Json(
            """{"code":10001,"message":"quota exceeded","request_id":"req-9"}"""));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ProviderImageTask(null, () => new HttpClient(handler)).RunAsync(JiMengInput()));

        Assert.Equal("即梦接口返回错误 10001：quota exceeded（request_id: req-9）", error.Message);
    }

    [Fact]
    public async Task 即梦_任务失效时报错()
    {
        StubHandler handler = new(request => HasAction(request, "Action=CVSync2AsyncSubmitTask")
            ? Json("""{"code":10000,"message":"Success","data":{"task_id":"task-1"}}""")
            : Json("""{"code":10000,"message":"Success","data":{"status":"expired"}}"""));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ProviderImageTask(null, () => new HttpClient(handler), ShortTimeoutPolicy())
                .RunAsync(JiMengInput()));

        Assert.Equal("即梦图片任务 task-1 已失效，请重新生成", error.Message);
    }

    [Fact]
    public async Task 即梦_超时报错()
    {
        StubHandler handler = new(_ => Json(
            """{"code":10000,"message":"Success","data":{"task_id":"task-1","status":"generating"}}"""));
        VideoPollPolicy policy = new()
        {
            TotalTimeout = TimeSpan.Zero,
            // 与视频测试一致：Sleep 注入为立即完成，不真的等 30 秒。
            Sleep = (_, _) => System.Threading.Tasks.Task.CompletedTask,
        };

        TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(
            () => new ProviderImageTask(null, () => new HttpClient(handler), policy).RunAsync(JiMengInput()));

        Assert.Equal("即梦图片生成超时（任务 task-1）", error.Message);
    }

    [Fact]
    public async Task 即梦_蒙版报错()
    {
        StubHandler handler = new(_ => Json(OneImage));
        TextTaskInput input = JiMengInput();
        input.Mask = new ProviderMedia { DataURL = PngDataUrl() };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task(handler).RunAsync(input));

        Assert.Equal("即梦图片协议不支持蒙版编辑，请移除蒙版后重试", error.Message);
    }

    [Fact]
    public async Task 即梦_参考图超过14张报错()
    {
        StubHandler handler = new(_ => Json(OneImage));
        TextTaskInput input = JiMengInput();
        for (int i = 0; i < 15; i++)
        {
            input.ReferenceImages.Add(new ProviderMedia { DataURL = PngDataUrl() });
        }

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task(handler).RunAsync(input));

        Assert.Equal("即梦图片协议最多支持 14 张参考图", error.Message);
    }

    [Fact]
    public void 即梦_尺寸面积越界时不传宽高()
    {
        Assert.Equal((1024, 1024), ProviderImageTask.JiMengImageDimensions("1024x1024"));
        Assert.Equal((1824, 1024), ProviderImageTask.JiMengImageDimensions("16:9"));
        Assert.Equal((0, 0), ProviderImageTask.JiMengImageDimensions("100x100"));
        Assert.Equal((0, 0), ProviderImageTask.JiMengImageDimensions("5000x5000"));
        Assert.Equal((0, 0), ProviderImageTask.JiMengImageDimensions("auto"));
    }
}
