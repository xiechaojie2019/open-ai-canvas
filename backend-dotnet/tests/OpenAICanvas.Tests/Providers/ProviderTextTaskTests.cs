#nullable enable
using System.Net;
using System.Text;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 文本任务出站执行的契约测试（请求 → 传输 → 解析）。
/// 对应 Go: <c>internal/app/provider_text.go</c> 的
/// <c>requestTextProvider</c> / <c>postStreamingTextResult</c> / <c>postStreamingAgent</c> /
/// <c>runLegacyTextTask</c>。
/// </summary>
public sealed class ProviderTextTaskTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return _responder(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string contentType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static TextTaskInput Input(bool stream = false) => new()
    {
        Prompt = "问题",
        Config = new ProviderConfig
        {
            Model = "m",
            BaseURL = "https://api.example.com",
            APIKey = "k",
            SystemPrompt = "你是助手",
        },
        TextOptions = new CanvasTextOptions { Stream = stream },
    };

    private static ProviderTextTask Task(StubHandler handler, long? maxBytes = null) =>
        new(maxBytes is null ? null : new FixedContext(maxBytes.Value), () => new HttpClient(handler));

    private sealed class FixedContext : IProviderRequestContext
    {
        public FixedContext(long maxResponseBytes) => MaxResponseBytes = maxResponseBytes;

        public long MaxResponseBytes { get; }
    }

    // ------------------------------------------------------------ 路径映射

    [Theory]
    [InlineData("responses", "/responses")]
    [InlineData("claude-api", "/messages")]
    [InlineData("chat-completion", "/chat/completions")]
    [InlineData("unknown", "/chat/completions")]
    public void 路径映射_按协议(string protocol, string expected) =>
        Assert.Equal(expected, ProviderTextTask.PathFor(protocol));

    // ------------------------------------------------------------ 非流式

    [Fact]
    public async Task 非流式_chat解析正文()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"你好"}}]}"""));

        Dictionary<string, object?> result = await Task(handler)
            .RunAsync(Input(), "chat-completion");

        Assert.Equal("text", result["mode"]);
        Assert.Equal("你好", result["text"]);
    }

    [Fact]
    public async Task 非流式_responses解析正文()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, """{"output_text":"回答"}"""));

        Dictionary<string, object?> result = await Task(handler)
            .RunAsync(Input(), "responses");

        Assert.Equal("回答", result["text"]);
    }

    [Fact]
    public async Task 非流式_claude解析正文()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK,
            """{"content":[{"type":"text","text":"Claude 回答"}]}"""));

        Dictionary<string, object?> result = await Task(handler)
            .RunAsync(Input(), "claude-api");

        Assert.Equal("Claude 回答", result["text"]);
    }

    [Fact]
    public async Task 非流式_请求带stream时被移除()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"x"}}]}"""));
        TextTaskInput input = Input();
        input.TextOptions.Stream = true;

        // 直接调用非流式协议分支（模拟分镜等调用方强制非流式）。
        await Task(handler).RequestAsync(input, "chat-completion", stream: false);

        // 非流式分支必须移除 stream，避免上游按 SSE 返回。
        Assert.DoesNotContain("\"stream\"", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 非流式_空正文报错()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":""}}]}"""));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task(handler).RunAsync(Input(), "chat-completion"));

        Assert.Equal("文本接口没有返回内容", error.Message);
    }

    [Fact]
    public async Task 非流式_请求URL按协议拼接()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, """{"output_text":"x"}"""));

        await Task(handler).RunAsync(Input(), "responses");

        Assert.Equal("https://api.example.com/v1/responses", handler.LastRequest!.RequestUri!.ToString());
    }

    // ------------------------------------------------------------ 流式

    [Fact]
    public async Task 流式_chat累积增量并回调()
    {
        StubHandler handler = new(_ => Sse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}\n\n" +
            "data: [DONE]\n\n"));
        List<string> deltas = [];

        Dictionary<string, object?> result = await Task(handler)
            .RunAsync(Input(stream: true), "chat-completion", onDelta: deltas.Add);

        Assert.Equal("你好", result["text"]);
        Assert.Equal(["你", "好"], deltas);
    }

    [Fact]
    public async Task 流式_请求开启用量块()
    {
        StubHandler handler = new(_ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\n"));

        await Task(handler).RunAsync(Input(stream: true), "chat-completion");

        Assert.Contains("\"stream\":true", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("include_usage", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 流式_responses只要DONE也保留增量()
    {
        StubHandler handler = new(_ => Sse(
            "event: response.output_text.delta\ndata: {\"delta\":\"部分\"}\n\n"));

        Dictionary<string, object?> result = await Task(handler)
            .RunAsync(Input(stream: true), "responses");

        Assert.Equal("部分", result["text"]);
    }

    [Fact]
    public async Task 流式_推理增量走独立回调()
    {
        StubHandler handler = new(_ => Sse(
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"想\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"答\"}}]}\n\n"));
        List<string> reasoning = [];

        await Task(handler).RunAsync(Input(stream: true), "chat-completion", onReasoningDelta: reasoning.Add);

        Assert.Equal(["想"], reasoning);
    }

    [Fact]
    public async Task 流式_发送Accept事件流()
    {
        StubHandler handler = new(_ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\n"));

        await Task(handler).RunAsync(Input(stream: true), "chat-completion");

        Assert.Equal("text/event-stream", handler.LastRequest!.Headers.GetValues("Accept").Single());
    }

    [Fact]
    public async Task 流式_上游忽略stream直接回JSON时按非流式解析()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"退化为JSON"}}]}"""));

        Dictionary<string, object?> result = await Task(handler)
            .RunAsync(Input(stream: true), "chat-completion");

        Assert.Equal("退化为JSON", result["text"]);
    }

    [Fact]
    public async Task 流式_空正文报流式专属文案()
    {
        StubHandler handler = new(_ => Sse("data: [DONE]\n\n"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task(handler).RunAsync(Input(stream: true), "chat-completion"));

        Assert.Equal("流式文本接口没有返回内容", error.Message);
    }

    // ------------------------------------------------------------ 错误映射

    [Fact]
    public async Task 错误_上游429抛HTTP异常()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.TooManyRequests, "{}"));

        ProviderHttpException error = await Assert.ThrowsAsync<ProviderHttpException>(
            () => Task(handler).RunAsync(Input(), "chat-completion"));

        Assert.Equal(429, error.StatusCode);
    }

    [Fact]
    public async Task 错误_响应超运行时上限()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"x"}}]}"""));

        // 上限 8 字节，响应体远超。
        ProviderTransportException error = await Assert.ThrowsAsync<ProviderTransportException>(
            () => Task(handler, maxBytes: 8).RunAsync(Input(), "chat-completion"));

        Assert.Contains("上游响应超过", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ legacy 回落

    [Fact]
    public async Task legacy_404时回落chat()
    {
        int calls = 0;
        StubHandler handler = new(_ =>
        {
            calls++;
            return calls == 1
                ? Json(HttpStatusCode.NotFound, "{}")
                : Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"回落成功"}}]}""");
        });

        Dictionary<string, object?> result = await Task(handler)
            .RunLegacyAsync(Input());

        Assert.Equal("回落成功", result["text"]);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task legacy_503时不回落只报一次错()
    {
        int calls = 0;
        StubHandler handler = new(_ =>
        {
            calls++;
            return Json(HttpStatusCode.ServiceUnavailable, "{}");
        });

        await Assert.ThrowsAsync<ProviderHttpException>(
            () => Task(handler).RunLegacyAsync(Input()));

        // 502/503/504 是瞬时故障，换协议修不好，不能触发第二次请求。
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task legacy_两次都失败时合并文案()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.NotFound, "{}"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task(handler).RunLegacyAsync(Input()));

        Assert.Contains("Responses API", error.Message, StringComparison.Ordinal);
        Assert.Contains("Chat Completions", error.Message, StringComparison.Ordinal);
    }
}
