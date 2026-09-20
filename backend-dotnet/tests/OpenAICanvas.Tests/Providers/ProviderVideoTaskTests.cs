#nullable enable
using System.Net;
using System.Text;
using OpenAICanvas.Protocol;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 视频任务的契约测试。
/// 对应 Go: <c>internal/app/provider_video.go</c> 的
/// <c>runVideoTaskWithPolicy</c> / <c>runSeedanceVideosTask</c> /
/// <c>runSeedanceAgentPlanVideoTask</c> 与 <c>grokVideoBody</c> / <c>seedanceContent</c>。
/// </summary>
public sealed class ProviderVideoTaskTests
{
    /// <summary>
    /// 带 <c>ftyp</c> 盒的 MP4 头。嗅探需要真实的 ISO-BMFF 结构，
    /// 否则 <c>NormalizedMediaMimeType</c> 会回落到 octet-stream。
    /// </summary>
    private static readonly byte[] Mp4 =
    [
        0x00, 0x00, 0x00, 0x18,                     // box size
        0x66, 0x74, 0x79, 0x70,                     // 'ftyp'
        0x69, 0x73, 0x6F, 0x6D,                     // major brand 'isom'
        0x00, 0x00, 0x02, 0x00,                     // minor version
        0x69, 0x73, 0x6F, 0x6D, 0x6D, 0x70, 0x34, 0x32,  // compatible brands
    ];

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        public HttpRequestMessage? Last => Requests.Count > 0 ? Requests[^1] : null;

        public string? LastBody => Bodies.Count > 0 ? Bodies[^1] : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responder(request);
        }
    }

    /// <summary>测试策略：不等待，且把总时限压短避免无限重试。</summary>
    private static VideoPollPolicy Policy() => new()
    {
        InitialDelay = TimeSpan.FromMilliseconds(1),
        Interval = TimeSpan.FromMilliseconds(1),
        TotalTimeout = TimeSpan.FromSeconds(2),
        Sleep = (_, _) => Task.CompletedTask,
    };

    private static ProviderVideoTask NewTask(StubHandler handler) => new(null, () => new HttpClient(handler));

    /// <summary>显式空注册表：等价 Go 的 <c>withProtocolRegistry(ctx, emptyProtocolRegistry)</c>。</summary>
    private sealed class EmptyRegistryContext : IProviderRequestContext
    {
        public long MaxResponseBytes => ProviderTransport.DefaultMaxResponseBytes;

        public ProtocolAdapterRegistry DeclarativeAdapter => new();
    }

    private static ProviderVideoTask NewTask(StubHandler handler, IProviderRequestContext context) =>
        new(context, () => new HttpClient(handler));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Binary() =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(Mp4) };

    private static TextTaskInput Input(string model = "seedance-1.0", string baseUrl = "https://api.example.com")
        => new()
        {
            Mode = "video",
            Prompt = "一只猫在跳舞",
            Config = new ProviderConfig { Model = model, BaseURL = baseUrl, APIKey = "k" },
        };

    private static string VideoState(string status, string url) =>
        $$"""{"status":"{{status}}","video_url":"{{url}}"}""";

    private static string AgentState(string status, string url) =>
        $$"""{"status":"{{status}}","content":{"video_url":"{{url}}"},"id":"t1"}""";

    // ------------------------------------------------------------ 渠道判定

    [Fact]
    public void 判定_Seedance按模型名()
    {
        Assert.True(ProviderVideoTask.IsSeedanceVideo(new ProviderConfig { Model = "seedance-1.0" }));
        Assert.True(ProviderVideoTask.IsSeedanceVideo(new ProviderConfig { Model = "doubao-seedance-x" }));
        Assert.True(ProviderVideoTask.IsSeedanceVideo(new ProviderConfig { Model = "SEEDANCE-2" }));
        Assert.False(ProviderVideoTask.IsSeedanceVideo(new ProviderConfig { Model = "veo-3" }));
    }

    [Fact]
    public void 判定_AgentPlan按baseURL()
    {
        Assert.True(ProviderVideoTask.IsArkPlanVideo(
            new ProviderConfig { BaseURL = "https://ark.example.com/api/plan/v3" }));
        Assert.False(ProviderVideoTask.IsArkPlanVideo(
            new ProviderConfig { BaseURL = "https://ark.example.com/api/v3" }));
    }

    [Fact]
    public void 判定_Grok按模型名()
    {
        Assert.True(ProviderVideoTask.IsGrokVideo(new ProviderConfig { Model = "grok-video-1" }));
        Assert.False(ProviderVideoTask.IsGrokVideo(new ProviderConfig { Model = "seedance" }));
    }

    [Fact]
    public void 判定_AgentPlan渠道也算Seedance()
    {
        // Go 的 isSeedanceVideoConfig 用 || isArkPlanVideoConfig(config)，故 Plan 渠道隐含满足。
        Assert.True(ProviderVideoTask.IsSeedanceVideo(
            new ProviderConfig { Model = "other", BaseURL = "https://x/api/plan/v3" }));
    }

    // ------------------------------------------------------------ OpenAI 风格

    [Fact]
    public async Task OpenAI风格_multipart创建与轮询()
    {
        StubHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json("""{"id":"task-1"}""");
            }
            return request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                ? Binary()
                : Json(VideoState("completed", ""));
        });
        TextTaskInput input = Input("veo-3");

        Dictionary<string, object?> result = await NewTask(handler).RunAsync(input, "", Policy());

        Assert.Equal("video", result["mode"]);
        Assert.Contains("multipart/form-data", handler.Requests[0].Content!.Headers.ContentType!.ToString()!,
            StringComparison.Ordinal);
        Assert.Contains("一只猫在跳舞", handler.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAI风格_完成后用video_url下载()
    {
        StubHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json("""{"id":"task-1"}""");
            }
            if (request.RequestUri!.AbsolutePath.Contains("/videos/task-1", StringComparison.Ordinal)
                && request.RequestUri.Host == "api.example.com")
            {
                return Json(VideoState("completed", "https://api.example.com/out/v.mp4"));
            }
            return Binary();
        });
        Dictionary<string, object?> result = await NewTask(handler)
            .RunAsync(Input("veo-3"), "", Policy());

        Dictionary<string, object?> video = Assert.IsType<Dictionary<string, object?>>(result["video"]);
        Assert.StartsWith("data:video/mp4;base64,", (string)video["dataUrl"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAI风格_无URL时回落content端点()
    {
        StubHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json("""{"id":"task-1"}""");
            }
            return request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                ? Binary()
                : Json(VideoState("completed", ""));
        });
        Dictionary<string, object?> result = await NewTask(handler).RunAsync(Input("veo-3"), "", Policy());

        Assert.True(result.ContainsKey("video"));
        Assert.Contains(handler.Requests, r =>
            r.RequestUri!.AbsolutePath.EndsWith("/videos/task-1/content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenAI风格_失败状态报错()
    {
        StubHandler handler = new(request => request.Method == HttpMethod.Post
            ? Json("""{"id":"task-1"}""")
            : Json("""{"status":"failed"}"""));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(Input("veo-3"), "", Policy()));

        Assert.Equal("视频生成失败", error.Message);
    }

    [Fact]
    public async Task OpenAI风格_无任务ID时报错()
    {
        StubHandler handler = new(_ => Json("{}"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(Input("veo-3"), "", Policy()));

        Assert.Equal("视频接口没有返回任务 ID", error.Message);
    }

    [Fact]
    public async Task OpenAI风格_任务ID类型错误时报错()
    {
        StubHandler handler = new(_ => Json("""{"id":123}"""));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(Input("veo-3"), "", Policy()));

        Assert.Contains("视频接口任务 ID 无效", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAI风格_嵌套data里取ID()
    {
        StubHandler handler = new(request => request.Method == HttpMethod.Post
            ? Json("""{"data":{"request_id":"nested-1"}}""")
            : Json(VideoState("completed", "")));
        TextTaskInput input = Input("veo-3");

        await NewTask(handler).RunAsync(input, "", Policy());

        Assert.Contains(handler.Requests, r =>
            r.RequestUri!.AbsolutePath.Contains("/videos/nested-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenAI风格_支持参考视频时报错()
    {
        StubHandler handler = new(_ => Json("{}"));
        TextTaskInput input = Input("veo-3");
        input.ReferenceVideos.Add(new ProviderMedia { URL = "https://x/v.mp4" });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(input, "", Policy()));

        Assert.Contains("不支持参考视频或参考音频", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAI风格_恢复任务时不重新创建()
    {
        StubHandler handler = new(request => request.RequestUri!.AbsolutePath.EndsWith("/content",
            StringComparison.Ordinal)
            ? Binary()
            : Json(VideoState("completed", "")));

        await NewTask(handler).RunAsync(Input("veo-3"), "existing-1", Policy());

        // 恢复语义：绝不重新 POST，否则会产生第二个计费任务。
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public void 结果URL_顶层直接命中()
    {
        Dictionary<string, object?> state = new(StringComparer.Ordinal)
        {
            ["video_url"] = "https://x/v.mp4",
        };

        Assert.Equal("https://x/v.mp4", ProviderVideoTask.NewApiVideoResultUrl(state));
    }

    [Fact]
    public void 结果URL_嵌套data命中()
    {
        Dictionary<string, object?> state = new(StringComparer.Ordinal)
        {
            ["data"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["result_url"] = "https://x/v.mp4",
            },
        };

        // 嵌套层才允许 result_url。
        Assert.Equal("https://x/v.mp4", ProviderVideoTask.NewApiVideoResultUrl(state));
    }

    [Fact]
    public void 结果URL_顶层不接受result_url()
    {
        Dictionary<string, object?> state = new(StringComparer.Ordinal)
        {
            ["result_url"] = "https://x/v.mp4",
        };

        Assert.Equal("", ProviderVideoTask.NewApiVideoResultUrl(state));
    }

    [Fact]
    public void 结果URL_非公网地址被忽略()
    {
        Dictionary<string, object?> state = new(StringComparer.Ordinal) { ["url"] = "ftp://x/v.mp4" };

        Assert.Equal("", ProviderVideoTask.NewApiVideoResultUrl(state));
    }

    [Fact]
    public void 结果URL_只下钻两层()
    {
        Dictionary<string, object?> state = new(StringComparer.Ordinal)
        {
            ["data"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["result"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["video"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["video_url"] = "https://x/v.mp4",
                    },
                },
            },
        };

        // 第三层超出下钻深度，不该被取到。
        Assert.Equal("", ProviderVideoTask.NewApiVideoResultUrl(state));
    }

    [Fact]
    public void Grok请求体_JSON而非multipart()
    {
        TextTaskInput input = Input("grok-video-1");
        input.Config.Size = "16:9";
        input.Config.VideoSeconds = "8";

        Dictionary<string, object?> body = ProviderVideoTask.GrokVideoBody(input);

        Assert.Equal(8, body["duration"]);
        Assert.Equal("8", body["seconds"]);
        Assert.Equal("1280x720", body["size"]);
        Assert.False(body.ContainsKey("image"));
    }

    [Fact]
    public void Grok请求体_参考图同发单图与数组()
    {
        TextTaskInput input = Input("grok-video-1");
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/a.png" });
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/b.png" });

        Dictionary<string, object?> body = ProviderVideoTask.GrokVideoBody(input);

        // 兼容不同上游的字段解析：image 取首张，images 给全集。
        Assert.Equal("https://x/a.png", body["image"]);
        List<string> images = Assert.IsType<List<string>>(body["images"]);
        Assert.Equal(2, images.Count);
    }

    [Fact]
    public void Grok请求体_文生视频不发图()
    {
        TextTaskInput input = Input("grok-video-1");
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/a.png" });
        input.Metadata["videoEditOperation"] = "text_to_video";

        Dictionary<string, object?> body = ProviderVideoTask.GrokVideoBody(input);

        Assert.False(body.ContainsKey("image"));
    }

    [Fact]
    public void Grok请求体_时长非法回落6()
    {
        TextTaskInput input = Input("grok-video-1");
        input.Config.VideoSeconds = "abc";

        Dictionary<string, object?> body = ProviderVideoTask.GrokVideoBody(input);

        Assert.Equal(6, body["duration"]);
    }

    // ------------------------------------------------------------ Seedance /videos

    [Fact]
    public async Task Seedance_JSON创建与轮询()
    {
        StubHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json("""{"id":"sd-1"}""");
            }
            return request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                ? Binary()
                : Json("""{"status":"succeeded","video_url":"https://cdn.example.com/v.mp4"}""");
        });
        TextTaskInput input = Input("seedance-1.0");

        Dictionary<string, object?> result = await NewTask(handler).RunAsync(input, "", Policy());

        // Seedance 走 JSON 而非 multipart。
        Assert.Contains("application/json", handler.Requests[0].Content!.Headers.ContentType!.ToString()!,
            StringComparison.Ordinal);
        Assert.Equal("video", result["mode"]);
    }

    [Fact]
    public async Task Seedance_嵌套data里取ID()
    {
        StubHandler handler = new(request => request.Method == HttpMethod.Post
            ? Json("""{"data":{"task_id":"sd-nested"}}""")
            : Json("""{"status":"succeeded","video_url":"https://cdn.example.com/v.mp4"}"""));

        await NewTask(handler).RunAsync(Input("seedance-1.0"), "", Policy());

        Assert.Contains(handler.Requests, r =>
            r.RequestUri!.AbsolutePath.Contains("/videos/sd-nested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Seedance_无URL时回落content端点()
    {
        StubHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json("""{"id":"sd-1"}""");
            }
            return request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                ? Binary()
                : Json("""{"status":"succeeded"}""");
        });

        await NewTask(handler).RunAsync(Input("seedance-1.0"), "", Policy());

        Assert.Contains(handler.Requests, r =>
            r.RequestUri!.AbsolutePath.EndsWith("/videos/sd-1/content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Seedance_expired状态报错()
    {
        StubHandler handler = new(request => request.Method == HttpMethod.Post
            ? Json("""{"id":"sd-1"}""")
            : Json("""{"status":"expired"}"""));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(Input("seedance-1.0"), "", Policy()));

        Assert.Equal("Seedance 视频生成失败", error.Message);
    }

    [Fact]
    public async Task Seedance_轮询响应带顶层error时先被传输层拦截()
    {
        StubHandler handler = new(request => request.Method == HttpMethod.Post
            ? Json("""{"id":"sd-1"}""")
            : Json("""{"status":"failed","error":{"code":"E1","message":"内容不合规"}}"""));

        // SendJsonAsync 会对顶层 error.message 做业务失败判定（与 Go 的 doJSON 一致），
        // 因此这类响应在进入轮询状态机前就抛出 ProviderPayloadException。
        await Assert.ThrowsAsync<ProviderPayloadException>(
            () => NewTask(handler).RunAsync(Input("seedance-1.0"), "", Policy()));
    }

    [Fact]
    public async Task Seedance_失败状态用error_code文案()
    {
        // error_code 在顶层、不构成 error 对象，能穿过 doJSON 的判定进入状态机。
        StubHandler handler = new(request => request.Method == HttpMethod.Post
            ? Json("""{"id":"sd-1"}""")
            : Json("""{"status":"failed","error_code":"E1"}"""));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(Input("seedance-1.0"), "", Policy()));

        Assert.Equal("E1", error.Message);
    }

    [Fact]
    public async Task Seedance_失败状态无错误信息时用默认文案()
    {
        StubHandler handler = new(request => request.Method == HttpMethod.Post
            ? Json("""{"id":"sd-1"}""")
            : Json("""{"status":"failed"}"""));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(Input("seedance-1.0"), "", Policy()));

        Assert.Equal("Seedance 视频生成失败", error.Message);
    }

    [Fact]
    public async Task Seedance_恢复任务时不重新创建()
    {
        StubHandler handler = new(request => request.RequestUri!.AbsolutePath.EndsWith("/content",
            StringComparison.Ordinal)
            ? Binary()
            : Json("""{"status":"succeeded"}"""));

        await NewTask(handler).RunAsync(Input("seedance-1.0"), "sd-existing", Policy());

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Seedance_无任务ID时报错()
    {
        StubHandler handler = new(_ => Json("{}"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(Input("seedance-1.0"), "", Policy()));

        Assert.Equal("Seedance 接口没有返回任务 ID", error.Message);
    }

    // ------------------------------------------------------------ Seedance 请求体

    [Fact]
    public void Seedance请求体_单图走image_url()
    {
        TextTaskInput input = Input();
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/a.png" });

        SeedanceVideosRequest body = ProviderVideoTask.SeedanceVideosBody(input);

        Assert.Equal("https://x/a.png", body.ImageURL);
        Assert.Null(body.ReferenceImageURLs);
        Assert.Null(body.ImageURLs);
    }

    [Fact]
    public void Seedance请求体_多图首张为image_url其余为参考图()
    {
        TextTaskInput input = Input();
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/a.png" });
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/b.png" });

        SeedanceVideosRequest body = ProviderVideoTask.SeedanceVideosBody(input);

        Assert.Equal("https://x/a.png", body.ImageURL);
        Assert.Equal(["https://x/b.png"], body.ReferenceImageURLs);
    }

    [Fact]
    public void Seedance请求体_首尾帧走image_urls()
    {
        TextTaskInput input = Input();
        input.Metadata["videoStartFrameNodeId"] = "s";
        input.Metadata["videoEndFrameNodeId"] = "e";
        input.ReferenceImages.Add(new ProviderMedia { ID = "e", URL = "https://x/e.png" });
        input.ReferenceImages.Add(new ProviderMedia { ID = "s", URL = "https://x/s.png" });

        SeedanceVideosRequest body = ProviderVideoTask.SeedanceVideosBody(input);

        Assert.Equal(["https://x/s.png", "https://x/e.png"], body.ImageURLs);
        Assert.Null(body.ImageURL);
    }

    [Fact]
    public void Seedance请求体_参考转视频全部走reference_image_urls()
    {
        TextTaskInput input = Input();
        input.Metadata["videoEditOperation"] = "reference_to_video";
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/a.png" });
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/b.png" });

        SeedanceVideosRequest body = ProviderVideoTask.SeedanceVideosBody(input);

        Assert.Equal(["https://x/a.png", "https://x/b.png"], body.ReferenceImageURLs);
        Assert.Null(body.ImageURL);
        Assert.Null(body.ImageURLs);
    }

    [Fact]
    public void Seedance请求体_视频音频参考被透传()
    {
        TextTaskInput input = Input();
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/a.png" });
        input.ReferenceVideos.Add(new ProviderMedia { URL = "https://x/v.mp4" });
        input.ReferenceAudios.Add(new ProviderMedia { URL = "https://x/a.mp3" });

        SeedanceVideosRequest body = ProviderVideoTask.SeedanceVideosBody(input);

        Assert.Equal(["https://x/v.mp4"], body.ReferenceVideos);
        Assert.Equal(["https://x/a.mp3"], body.ReferenceAudios);
    }

    [Fact]
    public void Seedance请求体_视频参考缺主图时报错()
    {
        TextTaskInput input = Input();
        input.ReferenceVideos.Add(new ProviderMedia { URL = "https://x/v.mp4" });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderVideoTask.SeedanceVideosBody(input));

        Assert.Contains("需要同时连接至少 1 张主参考图", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seedance请求体_音频参考缺主图时报错()
    {
        TextTaskInput input = Input();
        input.ReferenceAudios.Add(new ProviderMedia { URL = "https://x/a.mp3" });

        Assert.Throws<InvalidOperationException>(() => ProviderVideoTask.SeedanceVideosBody(input));
    }

    [Fact]
    public void Seedance请求体_比例用Videos变体()
    {
        TextTaskInput input = Input();
        input.Config.Size = "2:3";   // 不在 Seedance 白名单

        SeedanceVideosRequest body = ProviderVideoTask.SeedanceVideosBody(input);

        // /videos 不接受 adaptive，回落 16:9。
        Assert.Equal("16:9", body.AspectRatio);
    }

    [Fact]
    public void Seedance请求体_能力声明支持音频时写入()
    {
        TextTaskInput input = Input();
        input.Config.VideoGenerateAudio = "false";
        input.VideoCapability = new VideoCapabilityConfig
        {
            GenerateAudio = new VideoBooleanConfig { Supported = true },
        };

        SeedanceVideosRequest body = ProviderVideoTask.SeedanceVideosBody(input);

        Assert.False(body.GenerateAudio);
    }

    [Fact]
    public void Seedance请求体_能力声明不支持音频时不写入()
    {
        TextTaskInput input = Input();
        input.Config.VideoGenerateAudio = "true";
        input.VideoCapability = new VideoCapabilityConfig
        {
            GenerateAudio = new VideoBooleanConfig { Supported = false },
        };

        SeedanceVideosRequest body = ProviderVideoTask.SeedanceVideosBody(input);

        Assert.Null(body.GenerateAudio);
    }

    // ------------------------------------------------------------ Agent Plan

    [Fact]
    public async Task AgentPlan_走contents路径()
    {
        StubHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json("""{"id":"plan-1"}""");
            }
            return request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                ? Binary()
                : Json(AgentState("succeeded", "https://cdn.example.com/v.mp4"));
        });
        TextTaskInput input = Input("seedance-1.0", "https://ark.example.com/api/plan/v3");

        Dictionary<string, object?> result = await NewTask(handler).RunAsync(input, "", Policy());

        Assert.StartsWith("/api/plan/v3/contents/generations/tasks", 
            handler.Requests[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("video", result["mode"]);
    }

    [Fact]
    public async Task AgentPlan_成功但无URL时报错()
    {
        StubHandler handler = new(request => request.Method == HttpMethod.Post
            ? Json("""{"id":"plan-1"}""")
            : Json("""{"status":"succeeded","content":{}}"""));
        TextTaskInput input = Input("seedance-1.0", "https://ark.example.com/api/plan/v3");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(input, "", Policy()));

        Assert.Equal("Seedance任务成功但没有返回视频 URL", error.Message);
    }

    // 说明：ark-video + /api/plan/v3 组合在任何注册表状态下都无法到达 ArkPlan 手写分支
    // （声明式命中走 manifest 协议；未安装报"插件未安装"），与 Go 路由一致，无对应测试。
    [Fact]
    public async Task 火山方舟视频_注入空注册表时报插件未安装()
    {
        StubHandler handler = new(_ => Json("{}"));
        TextTaskInput input = Input();
        input.Config.InterfaceType = "volcengine-ark-video";

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler, new EmptyRegistryContext()).RunAsync(input, "", Policy()));

        Assert.Contains("插件未安装", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void AgentPlan内容_文本加三类素材()
    {
        TextTaskInput input = Input();
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/a.png" });
        input.ReferenceVideos.Add(new ProviderMedia { URL = "https://x/v.mp4" });
        input.ReferenceAudios.Add(new ProviderMedia { URL = "https://x/a.mp3" });

        List<Dictionary<string, object?>> content = ProviderVideoTask.SeedanceContent(input);

        Assert.Equal(4, content.Count);
        Assert.Equal("text", content[0]["type"]);
        Assert.Equal("image_url", content[1]["type"]);
        Assert.Equal("reference_image", content[1]["role"]);
        Assert.Equal("video_url", content[2]["type"]);
        Assert.Equal("reference_video", content[2]["role"]);
        Assert.Equal("audio_url", content[3]["type"]);
        Assert.Equal("reference_audio", content[3]["role"]);
    }

    [Fact]
    public void AgentPlan内容_无提示词也无素材时报错()
    {
        TextTaskInput input = Input();
        input.Prompt = "   ";

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderVideoTask.SeedanceContent(input));

        Assert.Equal("请输入视频提示词或连接参考素材", error.Message);
    }

    [Fact]
    public void AgentPlan内容_首尾帧角色为first与last()
    {
        TextTaskInput input = Input();
        input.Metadata["videoStartFrameNodeId"] = "s";
        input.Metadata["videoEndFrameNodeId"] = "e";
        input.ReferenceImages.Add(new ProviderMedia { ID = "s", URL = "https://x/s.png" });
        input.ReferenceImages.Add(new ProviderMedia { ID = "e", URL = "https://x/e.png" });

        List<Dictionary<string, object?>> content = ProviderVideoTask.SeedanceContent(input);

        // 提示词非空时 text 占第 0 位，素材从第 1 位开始。
        Assert.Equal("text", content[0]["type"]);
        Assert.Equal("first_frame", content[1]["role"]);
        Assert.Equal("last_frame", content[2]["role"]);
    }

    [Fact]
    public void AgentPlan内容_asset协议被接受()
    {
        TextTaskInput input = Input();
        input.ReferenceImages.Add(new ProviderMedia { URL = "asset://abc" });

        List<Dictionary<string, object?>> content = ProviderVideoTask.SeedanceContent(input);

        // text 占第 0 位（提示词非空），素材是第 1 位。
        Assert.Equal("text", content[0]["type"]);
        Dictionary<string, object?> imageUrl =
            Assert.IsType<Dictionary<string, object?>>(content[1]["image_url"]);
        Assert.Equal("asset://abc", imageUrl["url"]);
    }

    // ------------------------------------------------------------ 任务 ID 提取

    [Fact]
    public void 提取ID_按序取第一个存在的()
    {
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["request_id"] = "r1",
            ["task_id"] = "t1",
        };

        (string id, Exception? error) = ProviderVideoTask.ExtractTaskId(payload, "id", "request_id", "task_id");

        Assert.Equal("r1", id);
        Assert.Null(error);
    }

    [Fact]
    public void 提取ID_空字符串字段直接返回不继续找()
    {
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["id"] = "",
            ["task_id"] = "t1",
        };

        // 与 Go 的 firstJSONString 一致：字段存在（即使是空串）就立即返回，
        // 不会跳过它去找下一个候选键。
        (string id, Exception? error) = ProviderVideoTask.ExtractTaskId(payload, "id", "task_id");

        Assert.Equal("", id);
        Assert.Null(error);
    }

    [Fact]
    public void 提取ID_缺失字段才继续找()
    {
        Dictionary<string, object?> payload = new(StringComparer.Ordinal) { ["task_id"] = "t1" };

        (string id, _) = ProviderVideoTask.ExtractTaskId(payload, "id", "task_id");

        Assert.Equal("t1", id);
    }

    [Fact]
    public void 提取ID_数字类型报错()
    {
        Dictionary<string, object?> payload = new(StringComparer.Ordinal) { ["id"] = 123 };

        (string id, Exception? error) = ProviderVideoTask.ExtractTaskId(payload, "id");

        Assert.Equal("", id);
        Assert.NotNull(error);
        Assert.Contains("不是字符串", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 提取ID_都没有时返回空且无错()
    {
        (string id, Exception? error) = ProviderVideoTask.ExtractTaskId(
            new Dictionary<string, object?>(StringComparer.Ordinal), "id");

        Assert.Equal("", id);
        Assert.Null(error);
    }

    // ------------------------------------------------------------ 外部下载鉴权

    [Fact]
    public async Task 外部下载_跨源不带渠道鉴权()
    {
        StubHandler handler = new(request =>
        {
            if (request.RequestUri!.Host == "api.example.com")
            {
                return Json("""{"status":"succeeded","video_url":"https://cdn.other.com/v.mp4"}""");
            }
            return Binary();
        });
        TextTaskInput input = Input("seedance-1.0");
        input.Config.APIKey = "secret";

        await NewTask(handler).RunAsync(input, "sd-1", Policy());

        HttpRequestMessage download = handler.Requests.First(r => r.RequestUri!.Host == "cdn.other.com");
        // 把渠道密钥发给第三方 CDN 等于凭据泄露。
        Assert.False(download.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task 外部下载_同源时带渠道鉴权()
    {
        StubHandler handler = new(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/videos/sd-1", StringComparison.Ordinal))
            {
                return Json("""{"status":"succeeded","video_url":"https://api.example.com/out/v.mp4"}""");
            }
            return Binary();
        });
        TextTaskInput input = Input("seedance-1.0");
        input.Config.APIKey = "secret";

        await NewTask(handler).RunAsync(input, "sd-1", Policy());

        HttpRequestMessage download = handler.Requests.First(r =>
            r.RequestUri!.AbsolutePath == "/out/v.mp4");
        Assert.Equal("Bearer secret", download.Headers.GetValues("Authorization").Single());
    }
}
