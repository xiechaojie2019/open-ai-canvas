#nullable enable
using System.Net;
using System.Text;
using OpenAICanvas.Protocol;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 音频任务的契约测试。
/// 对应 Go: <c>internal/app/provider_audio.go</c> 的
/// <c>runAudioTask</c> / <c>runAsyncAudioTask</c> / <c>validateGeneratedAudio</c>。
/// </summary>
public sealed class ProviderAudioTaskTests
{
    /// <summary>ID3v2 头：嗅探与魔数校验都依赖真实的 'ID3' 前缀。</summary>
    private static readonly byte[] Mp3 =
    [
        (byte)'I', (byte)'D', (byte)'3', 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    private static readonly byte[] Wav =
    [
        (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x01, 0x02, 0x03, 0x04,
        (byte)'W', (byte)'A', (byte)'V', (byte)'E',
    ];

    private static readonly byte[] Pcm = [0x01, 0x02, 0x03, 0x04];

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

    /// <summary>显式空注册表：等价 Go 的裸 ctx 之外再确认"空表不报错、走手写协议"。</summary>
    private sealed class EmptyRegistryContext : IProviderRequestContext
    {
        public long MaxResponseBytes => ProviderTransport.DefaultMaxResponseBytes;

        public ProtocolAdapterRegistry DeclarativeAdapter => new();
    }

    private static ProviderAudioTask NewTask(StubHandler handler) => new(null, () => new HttpClient(handler));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Binary(byte[] data, string? contentType = null)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        if (contentType is not null)
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        return response;
    }

    private static TextTaskInput Input(string interfaceType = "openai-audio", string format = "") => new()
    {
        Mode = "audio",
        Prompt = "念一段欢迎词",
        Config = new ProviderConfig
        {
            Model = "tts-1",
            BaseURL = "https://api.example.com",
            APIKey = "k",
            InterfaceType = interfaceType,
            AudioFormat = format,
        },
    };

    private static Dictionary<string, object?> Audio(Dictionary<string, object?> result) =>
        (Dictionary<string, object?>)result["audio"]!;

    // ------------------------------------------------------------ 同步 /audio/speech

    [Fact]
    public async Task 同步_请求体与结果()
    {
        StubHandler handler = new(_ => Binary(Mp3, "audio/mpeg"));
        TextTaskInput input = Input(format: "mp3");
        input.Config.AudioSpeed = "1.5";
        input.Config.AudioInstructions = "用温和的语气";

        Dictionary<string, object?> result = await NewTask(handler).RunAsync(input);

        Assert.Equal("https://api.example.com/v1/audio/speech", handler.Last!.RequestUri!.ToString());
        Assert.Contains("\"model\":\"tts-1\"", handler.LastBody);
        Assert.Contains("\"input\":\"念一段欢迎词\"", handler.LastBody);
        Assert.Contains("\"voice\":\"alloy\"", handler.LastBody);
        Assert.Contains("\"response_format\":\"mp3\"", handler.LastBody);
        Assert.Contains("\"speed\":1.5", handler.LastBody);
        Assert.Contains("\"instructions\":\"用温和的语气\"", handler.LastBody);

        Dictionary<string, object?> audio = Audio(result);
        Assert.Equal("audio/mpeg", audio["mimeType"]);
        Assert.Equal("mp3", audio["format"]);
        Assert.StartsWith("data:audio/mpeg;base64,", (string)audio["dataUrl"]!);
    }

    [Fact]
    public async Task 同步_默认语速恒为1()
    {
        StubHandler handler = new(_ => Binary(Mp3, "audio/mpeg"));
        await NewTask(handler).RunAsync(Input());
        Assert.Contains("\"speed\":1", handler.LastBody);
    }

    [Fact]
    public async Task 同步_嗅探修正错误的响应头()
    {
        StubHandler handler = new(_ => Binary(Mp3, "text/plain"));
        Dictionary<string, object?> result = await NewTask(handler).RunAsync(Input());
        Assert.Equal("audio/mpeg", Audio(result)["mimeType"]);
    }

    [Fact]
    public async Task 同步_非音频内容报错()
    {
        StubHandler handler = new(_ => Binary(Encoding.UTF8.GetBytes("{\"error\":\"no\"}"), "application/json"));
        Exception error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(Input()));
        Assert.Contains("上游返回了非音频内容", error.Message);
    }

    [Fact]
    public async Task 同步_魔数与声明不符报错()
    {
        StubHandler handler = new(_ => Binary(Wav, "audio/mpeg"));
        Exception error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(Input()));
        Assert.Contains("音频内容与格式不匹配：audio/mpeg", error.Message);
    }

    [Fact]
    public async Task 同步_空响应报错()
    {
        StubHandler handler = new(_ => Binary([], "audio/mpeg"));
        Exception error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(Input()));
        Assert.Contains("音频内容为空", error.Message);
    }

    [Fact]
    public async Task 声明式_空注册表回退手写()
    {
        StubHandler handler = new(_ => Binary(Mp3, "audio/mpeg"));
        TextTaskInput input = Input(interfaceType: "openai-audio");
        Dictionary<string, object?> result = await new ProviderAudioTask(
            new EmptyRegistryContext(), () => new HttpClient(handler)).RunAsync(input);
        Assert.Equal("https://api.example.com/v1/audio/speech", handler.Last!.RequestUri!.ToString());
        Assert.NotNull(result["audio"]);
    }

    // ------------------------------------------------------------ 异步 /audio/tasks

    [Fact]
    public async Task 异步_创建即完成_内联dataURL()
    {
        string dataUrl = "data:audio/mpeg;base64," + Convert.ToBase64String(Mp3);
        StubHandler handler = new(_ => Json(
            "{\"id\":\"t1\",\"status\":\"processing\",\"data\":{\"status\":\"completed\",\"audio_url\":\"" + dataUrl + "\"}}"));

        Dictionary<string, object?> result = await NewTask(handler).RunAsync(Input("async-audio"));

        Assert.Equal("https://api.example.com/v1/audio/tasks", handler.Requests[0].RequestUri!.ToString());
        Assert.Single(handler.Requests);
        Assert.Equal("audio/mpeg", Audio(result)["mimeType"]);
    }

    [Fact]
    public async Task 异步_轮询至完成并从content下载()
    {
        int polls = 0;
        StubHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json("""{"task_id":"t9","status":"queued"}""");
            }
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/audio/tasks/t9", StringComparison.Ordinal))
            {
                polls++;
                return polls >= 2
                    ? Json("""{"status":"completed","audio_url":"https://api.example.com/v1/audio/tasks/t9/content"}""")
                    : Json("""{"status":"running"}""");
            }
            return Binary(Mp3, "audio/mpeg");
        });

        Dictionary<string, object?> result = await NewTask(handler).RunAsync(Input("async-audio", "mp3"));

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("/v1/audio/tasks/t9/content", handler.Requests[3].RequestUri!.AbsolutePath);
        Dictionary<string, object?> audio = Audio(result);
        Assert.Equal("audio/mpeg", audio["mimeType"]);
        Assert.Equal("mp3", audio["format"]);
    }

    [Fact]
    public async Task 异步_失败状态带上游错误文案()
    {
        // transport 层对 2xx JSON 的 error.message 直接抛 ProviderPayloadException（对应 Go doJSON
        // 的 providerPayloadError，先于业务状态码判定）。改用 business code 触发 failed 分支。
        StubHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json("""{"id":"t2","status":"queued"}""");
            }
            return Json("""{"status":"failed","code":"UpstreamQuota","message":"语音合成配额不足"}""");
        });
        Exception error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(Input("async-audio")));
        Assert.Contains("异步音频生成失败（任务 t2）：语音合成配额不足", error.Message);
    }

    [Fact]
    public async Task 异步_没有任务ID报错()
    {
        StubHandler handler = new(_ => Json("""{"status":"queued"}"""));
        Exception error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(Input("async-audio")));
        Assert.Equal("异步音频接口没有返回任务 ID", error.Message);
    }

    [Fact]
    public async Task 异步_任务ID类型错误报错()
    {
        StubHandler handler = new(_ => Json("""{"id":123,"status":"queued"}"""));
        Exception error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler).RunAsync(Input("async-audio")));
        Assert.StartsWith("异步音频接口任务 ID 无效：", error.Message);
    }

    // ------------------------------------------------------------ 纯函数

    [Fact]
    public void 魔数_覆盖各格式()
    {
        Assert.True(ProviderAudioTask.AudioSignatureMatches("audio/mpeg", Mp3));
        Assert.True(ProviderAudioTask.AudioSignatureMatches("audio/mp3", Mp3));
        Assert.True(ProviderAudioTask.AudioSignatureMatches("audio/wav", Wav));
        Assert.True(ProviderAudioTask.AudioSignatureMatches("audio/l16", Pcm));
        Assert.True(ProviderAudioTask.AudioSignatureMatches("audio/ogg", [0x4F, 0x67, 0x67, 0x53, 0x00]));
        Assert.True(ProviderAudioTask.AudioSignatureMatches("audio/flac", [0x66, 0x4C, 0x61, 0x43]));
        Assert.True(ProviderAudioTask.AudioSignatureMatches("audio/aac", [0xFF, 0xF1]));
        Assert.False(ProviderAudioTask.AudioSignatureMatches("audio/mpeg", Wav));
        Assert.False(ProviderAudioTask.AudioSignatureMatches("audio/unknown", Mp3));
    }

    [Theory]
    [InlineData("wav", "audio/wav")]
    [InlineData(" opus ", "audio/opus")]
    [InlineData("aac", "audio/aac")]
    [InlineData("flac", "audio/flac")]
    [InlineData("pcm", "audio/pcm")]
    [InlineData("mp3", "audio/mpeg")]
    [InlineData("", "")]
    [InlineData("unknown", "")]
    public void 格式MIME_映射(string format, string expected) =>
        Assert.Equal(expected, ProviderAudioTask.AudioFormatMimeType(format));

    [Fact]
    public void DataURL解码_严格校验()
    {
        (string mimeType, byte[] data) = ProviderAudioTask.DecodeProviderDataURL(
            "data:audio/wav;base64," + Convert.ToBase64String(Wav));
        Assert.Equal("audio/wav", mimeType);
        Assert.Equal(Wav, data);

        Assert.Throws<InvalidOperationException>(() =>
            ProviderAudioTask.DecodeProviderDataURL("data:audio/wav," + Convert.ToBase64String(Wav)));
        Assert.Throws<InvalidOperationException>(() =>
            ProviderAudioTask.DecodeProviderDataURL("audio/wav;base64,AAAA"));
        Assert.Throws<InvalidOperationException>(() =>
            ProviderAudioTask.DecodeProviderDataURL("data:audio/wav;base64,!!!"));
    }

    [Fact]
    public void 结果URL_键序与下钻()
    {
        Assert.Equal(
            "https://cdn.example.com/b.mp3",
            ProviderAudioTask.AsyncAudioResultURL(new Dictionary<string, object?>
            {
                ["url"] = "https://cdn.example.com/a.mp3",
                ["audio_url"] = "https://cdn.example.com/b.mp3",
            }));
        Assert.Equal(
            "data:audio/mpeg;base64,QQ==",
            ProviderAudioTask.AsyncAudioResultURL(new Dictionary<string, object?>
            {
                ["data"] = "data:audio/mpeg;base64,QQ==",
            }));
        Assert.Equal(
            "",
            ProviderAudioTask.AsyncAudioResultURL(new Dictionary<string, object?>
            {
                ["url"] = "/relative/path.mp3",
            }));
    }

    [Fact]
    public void Payload展开_外层并入内层()
    {
        Dictionary<string, object?> nested = new(StringComparer.Ordinal) { ["status"] = "completed" };
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["id"] = "t1",
            ["status"] = "processing",
            ["data"] = nested,
        };

        Dictionary<string, object?> merged = ProviderAudioTask.AsyncAudioPayload(payload);

        Assert.Same(nested, merged);
        Assert.Equal("completed", merged["status"]);
        Assert.Equal("t1", merged["id"]);
        Assert.False(merged.ContainsKey("data"));
        Dictionary<string, object?> flat = new(StringComparer.Ordinal) { ["status"] = "running" };
        Assert.Same(flat, ProviderAudioTask.AsyncAudioPayload(flat));
    }

    [Fact]
    public void 成功态判定()
    {
        Assert.True(ProviderAudioTask.AsyncAudioSucceeded(new Dictionary<string, object?> { ["status"] = "Completed" }));
        Assert.True(ProviderAudioTask.AsyncAudioSucceeded(new Dictionary<string, object?> { ["done"] = true }));
        Assert.True(ProviderAudioTask.AsyncAudioSucceeded(new Dictionary<string, object?> { ["audio_url"] = "https://x/y.mp3" }));
        Assert.False(ProviderAudioTask.AsyncAudioSucceeded(new Dictionary<string, object?> { ["status"] = "processing" }));
        Assert.False(ProviderAudioTask.AsyncAudioSucceeded([]));
    }
}
