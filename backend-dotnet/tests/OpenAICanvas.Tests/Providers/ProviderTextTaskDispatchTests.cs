#nullable enable
using System.Net;
using System.Text;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 文本任务按 <c>interfaceType</c> 分发的契约测试。
/// 对应 Go: <c>provider_text.go</c> 的 <c>runTextTask</c> 分支判定。
/// </summary>
public sealed class ProviderTextTaskDispatchTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(_responder(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

    private static TextTaskInput Input(string interfaceType) => new()
    {
        Mode = "text",
        Prompt = "问题",
        Config = new ProviderConfig
        {
            InterfaceType = interfaceType,
            Model = "m",
            BaseURL = "https://api.example.com",
            APIKey = "k",
        },
    };

    // ------------------------------------------------------------ 分发

    [Fact]
    public async Task 分发_chatCompletion走chat路径()
    {
        StubHandler handler = new(_ => Json("""{"choices":[{"message":{"content":"c"}}]}"""));
        ProviderTextTask task = new(null, () => new HttpClient(handler));

        Dictionary<string, object?> result =
            await task.RunTextTaskAsync(Input("chat-completion"));

        Assert.Equal("c", result["text"]);
        Assert.Equal(["/v1/chat/completions"], handler.Paths);
    }

    [Fact]
    public async Task 分发_openaiResponse走responses路径()
    {
        StubHandler handler = new(_ => Json("""{"output_text":"r"}"""));
        ProviderTextTask task = new(null, () => new HttpClient(handler));

        Dictionary<string, object?> result =
            await task.RunTextTaskAsync(Input("openai-response"));

        Assert.Equal("r", result["text"]);
        Assert.Equal(["/v1/responses"], handler.Paths);
    }

    [Fact]
    public async Task 分发_claudeApi走messages路径()
    {
        StubHandler handler = new(_ => Json("""{"content":[{"type":"text","text":"a"}]}"""));
        ProviderTextTask task = new(null, () => new HttpClient(handler));

        Dictionary<string, object?> result =
            await task.RunTextTaskAsync(Input("claude-api"));

        Assert.Equal("a", result["text"]);
        Assert.Equal(["/v1/messages"], handler.Paths);
    }

    [Fact]
    public async Task 分发_未知类型走legacy并先试responses()
    {
        StubHandler handler = new(_ => Json("""{"output_text":"legacy"}"""));
        ProviderTextTask task = new(null, () => new HttpClient(handler));

        Dictionary<string, object?> result = await task.RunTextTaskAsync(Input("weird-type"));

        Assert.Equal("legacy", result["text"]);
        Assert.Equal(["/v1/responses"], handler.Paths);
    }

    [Fact]
    public async Task 分发_空类型走legacy()
    {
        StubHandler handler = new(_ => Json("""{"output_text":"legacy"}"""));
        ProviderTextTask task = new(null, () => new HttpClient(handler));

        Dictionary<string, object?> result = await task.RunTextTaskAsync(Input(""));

        Assert.Equal("legacy", result["text"]);
    }

    [Fact]
    public async Task 分发_类型带空格仍能识别()
    {
        StubHandler handler = new(_ => Json("""{"choices":[{"message":{"content":"c"}}]}"""));
        ProviderTextTask task = new(null, () => new HttpClient(handler));

        Dictionary<string, object?> result =
            await task.RunTextTaskAsync(Input("  chat-completion  "));

        Assert.Equal("c", result["text"]);
        Assert.Equal(["/v1/chat/completions"], handler.Paths);
    }

    [Fact]
    public async Task 分发_legacy遇404时回落chat()
    {
        StubHandler handler = new(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal)
                ? NotFound()
                : Json("""{"choices":[{"message":{"content":"fallback"}}]}"""));
        ProviderTextTask task = new(null, () => new HttpClient(handler));

        Dictionary<string, object?> result = await task.RunTextTaskAsync(Input(""));

        Assert.Equal("fallback", result["text"]);
        Assert.Equal(["/v1/responses", "/v1/chat/completions"], handler.Paths);
    }

    [Fact]
    public async Task 分发_显式chat不会回落()
    {
        StubHandler handler = new(_ => NotFound());
        ProviderTextTask task = new(null, () => new HttpClient(handler));

        await Assert.ThrowsAsync<ProviderHttpException>(
            () => task.RunTextTaskAsync(Input("chat-completion")));

        // 显式指定协议时不做 legacy 回落，只请求一次。
        Assert.Equal(["/v1/chat/completions"], handler.Paths);
    }

    // ------------------------------------------------------------ 流式取自 TextOptions

    [Fact]
    public async Task 流式_由TextOptions决定()
    {
        StubHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"s\"}}]}\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            });
        ProviderTextTask task = new(null, () => new HttpClient(handler));
        TextTaskInput input = Input("chat-completion");
        input.TextOptions.Stream = true;

        Dictionary<string, object?> result = await task.RunTextTaskAsync(input);

        Assert.Equal("s", result["text"]);
    }

    [Fact]
    public async Task 流式_默认非流式()
    {
        StubHandler handler = new(_ => Json("""{"choices":[{"message":{"content":"n"}}]}"""));
        ProviderTextTask task = new(null, () => new HttpClient(handler));
        TextTaskInput input = Input("chat-completion");
        input.TextOptions.Stream = null;

        Dictionary<string, object?> result = await task.RunTextTaskAsync(input);

        Assert.Equal("n", result["text"]);
    }
}
