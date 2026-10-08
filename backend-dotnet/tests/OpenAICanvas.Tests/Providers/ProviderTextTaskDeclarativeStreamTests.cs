#nullable enable
using System.Net;
using System.Text;
using OpenAICanvas.Protocol;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 声明式文本路径的流式契约测试。
/// 对应 Go: <c>internal/app/provider_declarative_text_stream_test.go</c> 的
/// <c>TestDeclarativeTextStreaming</c>（<c>openai-response</c> 分支）
/// 与 <c>provider_text.go</c> 的 <c>executeProtocolCreateRequest</c>。
/// </summary>
/// <remarks>
/// 这条链路此前是回归的重灾区：声明式分支只实现了「拿最终 JSON 再读清单 textPaths」，
/// 而 <c>openai-responses</c> 清单只声明了 <c>output_text</c>（OpenAI 官方 SDK 的派生字段），
/// 上游真实返回的是 <c>output[].content[].text</c> ⇒ 文本与「识别图片文字」任务
/// 必然抛「声明式文本接口没有返回内容」并被错报成连接失败。
/// </remarks>
public sealed class ProviderTextTaskDeclarativeStreamTests
{
    private const string ProviderID = "openai-response";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Paths { get; } = [];

        public List<string> Bodies { get; } = [];

        public string LastBody => Bodies.Count > 0 ? Bodies[^1] : "";

        public string? LastAccept =>
            Requests.Count > 0 && Requests[^1].Headers.TryGetValues("Accept", out IEnumerable<string>? values)
                ? string.Join(",", values)
                : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responder(request);
        }
    }

    /// <summary>注入真实插件包解析出的声明式适配器快照（等价 Go 的 <c>withProtocolRegistry</c>）。</summary>
    private sealed class PluginContext : IProviderRequestContext
    {
        private readonly ProtocolAdapterRegistry _registry;

        public PluginContext(string providerID)
        {
            IProtocolAdapter adapter = ProtocolManifestCodec
                .LoadInstalledProviders(PluginPackages.Read("openai-responses"), null)
                .Single();
            _registry = new ProtocolAdapterRegistry(adapter);
            Assert.Equal(providerID, adapter.Metadata().ID);
            Assert.Equal("declarative", adapter.Metadata().Execution);
            Assert.True(adapter.Metadata().Enabled);
            Assert.Contains(ProtocolCapability.Text, adapter.Metadata().Categories);
        }

        public long MaxResponseBytes => ProviderTransport.DefaultMaxResponseBytes;

        public ProtocolAdapterRegistry DeclarativeAdapter => _registry;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// SSE 响应：mediaType 只能给裸类型（带 <c>; charset=</c> 会被 <see cref="StringContent"/> 拒绝），
    /// charset 由 <see cref="Encoding"/> 自动补成 <c>text/event-stream; charset=utf-8</c>。
    /// </summary>
    private static HttpResponseMessage Sse(string frames) =>
        new(HttpStatusCode.OK) { Content = new StringContent(frames, Encoding.UTF8, "text/event-stream") };

    private static TextTaskInput Input(bool? stream) => new()
    {
        Mode = "text",
        Prompt = "这张图里有什么",
        Config = new ProviderConfig
        {
            InterfaceType = ProviderID,
            Model = "test-model",
            BaseURL = "https://api.example.com",
            APIKey = "test-key",
        },
        TextOptions = new CanvasTextOptions { Stream = stream },
    };

    private static ProviderTextTask NewTask(StubHandler handler, IProviderRequestContext context) =>
        new(context, () => new HttpClient(handler));

    // ------------------------------------------------------------ 流式

    [Fact]
    public async Task 声明式_流式_发出stream并解析SSE增量()
    {
        StubHandler handler = new(_ => Sse(
            "event: response.output_text.delta\ndata: {\"delta\":\"hello\"}\n\n"));
        List<string> deltas = [];

        Dictionary<string, object?> result = await NewTask(handler, new PluginContext(ProviderID))
            .RunTextTaskAsync(Input(true), onDelta: deltas.Add);

        Assert.Equal(["hello"], deltas);
        Assert.Equal("hello", result["text"]);
        // 渠道地址已带 /v1 时不能再叠一层；清单 create.path 是 /responses。
        Assert.Equal(["/v1/responses"], handler.Paths);
        Assert.Contains("\"stream\":true", handler.LastBody);
        Assert.Contains("\"model\":\"test-model\"", handler.LastBody);
        Assert.Equal("text/event-stream", handler.LastAccept);
    }

    [Fact]
    public async Task 声明式_流式_上游忽略stream回JSON时回落清单解析()
    {
        // 与 Go 一致：上游不认 stream 直接回 JSON 时，复用本次响应体走 adapter.ParseCreate，
        // 不重复请求，也不能把可用的结果当成失败。
        StubHandler handler = new(_ => Json("""{"output_text":"hello"}"""));

        Dictionary<string, object?> result = await NewTask(handler, new PluginContext(ProviderID))
            .RunTextTaskAsync(Input(true));

        Assert.Equal("hello", result["text"]);
        Assert.Single(handler.Paths);
        Assert.Contains("\"stream\":true", handler.LastBody);
    }

    // ------------------------------------------------------------ 非流式

    [Fact]
    public async Task 声明式_非流式_不发stream()
    {
        StubHandler handler = new(_ => Json("""{"output_text":"plain"}"""));

        Dictionary<string, object?> result = await NewTask(handler, new PluginContext(ProviderID))
            .RunTextTaskAsync(Input(false));

        Assert.Equal("plain", result["text"]);
        Assert.DoesNotContain("\"stream\":true", handler.LastBody);
        Assert.Null(handler.LastAccept);
    }

    [Fact]
    public async Task 声明式_未声明stream按非流式处理()
    {
        // 与同文件内置协议路径保持一致（TextOptions.Stream ?? false）：
        // 未声明即非流式，避免对不支持 SSE 的渠道误发 stream 后死等。
        StubHandler handler = new(_ => Json("""{"output_text":"plain"}"""));

        Dictionary<string, object?> result = await NewTask(handler, new PluginContext(ProviderID))
            .RunTextTaskAsync(Input(null));

        Assert.Equal("plain", result["text"]);
        Assert.DoesNotContain("\"stream\":true", handler.LastBody);
    }

    // ------------------------------------------------------------ 失败

    [Fact]
    public async Task 声明式_流式无内容时报流式专用错误()
    {
        StubHandler handler = new(_ => Sse("event: response.completed\ndata: {\"response\":{}}\n\n"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTask(handler, new PluginContext(ProviderID)).RunTextTaskAsync(Input(true)));

        Assert.Contains("流式文本接口没有返回内容", error.Message);
    }

    /// <summary>定位仓库根下的插件包；测试输出目录到仓库根的深度随框架版本变动，逐级向上找。</summary>
    private static class PluginPackages
    {
        public static byte[] Read(string package)
        {
            string? current = AppContext.BaseDirectory;
            for (int depth = 0; depth < 10 && current is not null; depth++)
            {
                string directory = Path.Combine(current, "plugin-packages", package);
                string manifest = Path.Combine(directory, "manifest.json");
                if (File.Exists(manifest))
                {
                    return File.ReadAllBytes(manifest);
                }
                current = Path.GetDirectoryName(current);
            }
            throw new InvalidOperationException($"找不到插件包 {package}");
        }
    }
}
