#nullable enable
using System.Net;
using System.Text;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// Provider 出站传输的线级契约测试（注入 mock handler，覆盖大小上限 / 非 2xx / 分片回调）。
/// 对应 Go: <c>internal/app/provider_http_client.go</c> 的 <c>doBinaryWithConsumer</c> / <c>doJSON</c>。
/// </summary>
public sealed class ProviderTransportWireTests
{
    /// <summary>可编排的假 handler：按请求返回固定响应。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_responder(request));
        }
    }

    private static Func<HttpClient> Client(StubHandler handler) => () => new HttpClient(handler);

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string contentType = "application/json") =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };

    private static HttpRequestMessage Request() => new(HttpMethod.Post, "https://api.example.com/v1/x");

    // ------------------------------------------------------------ 正常路径

    [Fact]
    public async Task 发送_成功时返回数据与媒体类型()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, """{"ok":true}"""));
        using HttpRequestMessage request = Request();

        ProviderTransport.OutboundResult result = await ProviderTransport.SendAsync(request, clientFactory: Client(handler));

        Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(result.Data));
        Assert.Contains("json", result.MIMEType, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 发送_分片回调收到全部字节()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, """{"a":1}"""));
        using HttpRequestMessage request = Request();
        List<byte> observed = [];

        await ProviderTransport.SendAsync(
            request, onChunk: (_, chunk) => observed.AddRange(chunk), clientFactory: Client(handler));

        Assert.Equal("""{"a":1}""", Encoding.UTF8.GetString(observed.ToArray()));
    }

    [Fact]
    public async Task 发送_分片回调拿到响应媒体类型()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, "x", "text/event-stream"));
        using HttpRequestMessage request = Request();
        string? seen = null;

        await ProviderTransport.SendAsync(
            request, onChunk: (mime, _) => seen = mime, clientFactory: Client(handler));

        Assert.NotNull(seen);
        Assert.Contains("event-stream", seen, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ 非 2xx

    [Fact]
    public async Task 发送_非2xx抛HTTP异常并保留状态码与正文()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.TooManyRequests, """{"error":"slow down"}"""));
        using HttpRequestMessage request = Request();

        ProviderHttpException error = await Assert.ThrowsAsync<ProviderHttpException>(
            () => ProviderTransport.SendAsync(request, clientFactory: Client(handler)));

        Assert.Equal(429, error.StatusCode);
        Assert.Equal("""{"error":"slow down"}""", error.Body);
    }

    [Fact]
    public async Task 发送_非2xx解析RetryAfter秒数()
    {
        StubHandler handler = new(_ =>
        {
            HttpResponseMessage response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.TryAddWithoutValidation("Retry-After", "7");
            return response;
        });
        using HttpRequestMessage request = Request();

        ProviderHttpException error = await Assert.ThrowsAsync<ProviderHttpException>(
            () => ProviderTransport.SendAsync(request, clientFactory: Client(handler)));

        Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
    }

    [Fact]
    public async Task 发送_非2xx异常文案按状态码映射()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.Unauthorized, "{}"));
        using HttpRequestMessage request = Request();

        ProviderHttpException error = await Assert.ThrowsAsync<ProviderHttpException>(
            () => ProviderTransport.SendAsync(request, clientFactory: Client(handler)));

        Assert.Contains("鉴权失败", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 大小上限

    [Fact]
    public async Task 发送_声明长度超限立即失败()
    {
        byte[] big = new byte[2048];
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(big),
        });
        using HttpRequestMessage request = Request();

        ProviderTransportException error = await Assert.ThrowsAsync<ProviderTransportException>(
            () => ProviderTransport.SendAsync(request, maxResponseBytes: 1024, clientFactory: Client(handler)));

        Assert.Contains("上游响应超过", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 发送_实际长度超限也失败()
    {
        // 声明长度未知（chunked），只能边读边判定。
        StubHandler handler = new(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(new byte[4096])),
            };
            response.Content.Headers.ContentLength = null;
            return response;
        });
        using HttpRequestMessage request = Request();

        ProviderTransportException error = await Assert.ThrowsAsync<ProviderTransportException>(
            () => ProviderTransport.SendAsync(request, maxResponseBytes: 1024, clientFactory: Client(handler)));

        Assert.Contains("上游响应超过", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 发送_超限文案含可读大小()
    {
        // 响应 2MB、上限 1MB —— 必须真正超限才会抛错。
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[2L << 20]),
        });
        using HttpRequestMessage request = Request();

        ProviderTransportException error = await Assert.ThrowsAsync<ProviderTransportException>(
            () => ProviderTransport.SendAsync(request, maxResponseBytes: 1L << 20, clientFactory: Client(handler)));

        Assert.Contains("1MB", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 网络失败

    [Fact]
    public async Task 发送_网络不可达映射为固定文案()
    {
        StubHandler handler = new(_ => throw new HttpRequestException("boom"));
        using HttpRequestMessage request = Request();

        ProviderTransportException error = await Assert.ThrowsAsync<ProviderTransportException>(
            () => ProviderTransport.SendAsync(request, clientFactory: Client(handler)));

        Assert.Equal(ProviderErrorMessages.NetworkFailure, error.Message);
    }

    [Fact]
    public async Task 发送_取消不被吞掉()
    {
        StubHandler handler = new(_ => throw new OperationCanceledException());
        using HttpRequestMessage request = Request();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProviderTransport.SendAsync(request, clientFactory: Client(handler)));
    }

    // ------------------------------------------------------------ SendJsonAsync

    [Fact]
    public async Task JSON发送_解析成功()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, """{"text":"hello"}"""));
        using HttpRequestMessage request = Request();

        Dictionary<string, object?> payload = await ProviderTransport.SendJsonAsync(request, clientFactory: Client(handler));

        Assert.Equal("hello", payload["text"]);
    }

    [Fact]
    public async Task JSON发送_非JSON媒体且内容非法时报解码错误()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, "<html>gateway</html>", "text/html"));
        using HttpRequestMessage request = Request();

        await Assert.ThrowsAsync<ProviderResponseDecodeException>(
            () => ProviderTransport.SendJsonAsync(request, clientFactory: Client(handler)));
    }

    [Fact]
    public async Task JSON发送_媒体类型是text但内容是JSON时仍解析()
    {
        // 有些上游把 JSON 标成 text/plain，只看头会误杀。
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, """{"text":"ok"}""", "text/plain"));
        using HttpRequestMessage request = Request();

        Dictionary<string, object?> payload = await ProviderTransport.SendJsonAsync(request, clientFactory: Client(handler));

        Assert.Equal("ok", payload["text"]);
    }

    [Fact]
    public async Task JSON发送_error对象含消息时抛业务异常()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, """{"error":{"message":"invalid api key"}}"""));
        using HttpRequestMessage request = Request();

        ProviderPayloadException error = await Assert.ThrowsAsync<ProviderPayloadException>(
            () => ProviderTransport.SendJsonAsync(request, clientFactory: Client(handler)));

        Assert.Equal("invalid api key", error.Raw);
    }

    [Fact]
    public async Task JSON发送_error对象无消息不算失败()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, """{"error":{},"text":"ok"}"""));
        using HttpRequestMessage request = Request();

        Dictionary<string, object?> payload = await ProviderTransport.SendJsonAsync(request, clientFactory: Client(handler));

        Assert.Equal("ok", payload["text"]);
    }

    [Fact]
    public async Task JSON发送_顶层数组报解码错误()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, "[1,2,3]"));
        using HttpRequestMessage request = Request();

        await Assert.ThrowsAsync<ProviderResponseDecodeException>(
            () => ProviderTransport.SendJsonAsync(request, clientFactory: Client(handler)));
    }
}
