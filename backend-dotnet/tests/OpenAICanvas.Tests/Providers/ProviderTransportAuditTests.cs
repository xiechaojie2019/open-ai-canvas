#nullable enable
using System.Net;
using System.Text;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// Provider 出站收口的调用审计埋点测试。
/// 对应 Go: <c>doBinaryWithConsumer</c> 各终态的 <c>recordProviderRequest</c>。
/// </summary>
public sealed class ProviderTransportAuditTests
{
    /// <summary>记录观测结果的桩上下文（其余成员走接口默认实现）。</summary>
    private sealed class RecordingContext : IProviderRequestContext
    {
        public long MaxResponseBytes => ProviderTransport.DefaultMaxResponseBytes;

        public ProviderCallAudit Audit { get; init; } =
            new("u1", "t1", "tr1", "r1", "", "ch1", "image", "image", "m1", 0);

        public List<ProviderCallObservation> Observations { get; } = [];

        public async Task RecordProviderCallAsync(
            ProviderCallObservation observation, CancellationToken cancellationToken)
        {
            Observations.Add(observation);
            await Task.CompletedTask;
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpRequestMessage snapshot = new(request.Method, request.RequestUri)
            {
                Content = request.Content is null
                    ? null
                    : new ByteArrayContent(
                        request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()),
            };
            foreach ((string name, IEnumerable<string> values) in request.Headers)
            {
                snapshot.Headers.TryAddWithoutValidation(name, values);
            }
            return Task.FromResult(_responder(snapshot));
        }
    }

    private static Func<HttpClient> Client(StubHandler handler) => () => new HttpClient(handler);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpRequestMessage Request(string body = """{"model":"m1"}""") => new(HttpMethod.Post, "https://api.example.com/v1/images/generations")
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task 成功响应_记录一条succeeded观测并携带请求响应体()
    {
        RecordingContext context = new();
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, """{"data":[]}"""));

        ProviderTransport.OutboundResult result = await ProviderTransport.SendAsync(
            Request(), clientFactory: Client(handler), context: context);

        ProviderCallObservation observation = Assert.Single(context.Observations);
        Assert.Null(observation.Failure);
        Assert.Equal(200, observation.StatusCode);
        Assert.Equal("POST", observation.Method);
        Assert.Equal("/v1/images/generations", observation.Path);
        Assert.Equal("https://api.example.com/v1/images/generations", observation.UpstreamURL);
        Assert.Equal("openai", observation.APIFormat);
        Assert.Equal("""{"data":[]}""", Encoding.UTF8.GetString(observation.ResponseBody));
        Assert.Equal("""{"model":"m1"}""", Encoding.UTF8.GetString(observation.RequestBody));
        Assert.StartsWith("application/json", observation.RequestContentType);
        Assert.True(observation.DurationMs >= 0);
        Assert.Equal("""{"data":[]}""", Encoding.UTF8.GetString(result.Data));
    }

    [Fact]
    public async Task 非2xx_记录failed观测后抛出HTTP异常()
    {
        RecordingContext context = new();
        StubHandler handler = new(_ => Json(HttpStatusCode.InternalServerError, "boom"));

        await Assert.ThrowsAsync<ProviderHttpException>(() =>
            ProviderTransport.SendAsync(Request(), clientFactory: Client(handler), context: context));

        ProviderCallObservation observation = Assert.Single(context.Observations);
        Assert.Equal(500, observation.StatusCode);
        Assert.NotNull(observation.Failure);
        Assert.IsType<ProviderHttpException>(observation.Failure);
        Assert.Equal("boom", Encoding.UTF8.GetString(observation.ResponseBody));
    }

    [Fact]
    public async Task 请求阶段连接失败_记录statusCode为0的failed观测()
    {
        RecordingContext context = new();
        StubHandler handler = new(_ => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<ProviderTransportException>(() =>
            ProviderTransport.SendAsync(Request(), clientFactory: Client(handler), context: context));

        ProviderCallObservation observation = Assert.Single(context.Observations);
        Assert.Equal(0, observation.StatusCode);
        Assert.IsType<ProviderTransportException>(observation.Failure);
    }

    [Fact]
    public async Task 取消_记录取消观测后保持取消语义()
    {
        RecordingContext context = new();
        using CancellationTokenSource cancellation = new();
        StubHandler handler = new(_ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return Json(HttpStatusCode.OK, "{}");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProviderTransport.SendAsync(Request(), clientFactory: Client(handler), context: context, cancellationToken: cancellation.Token));

        ProviderCallObservation observation = Assert.Single(context.Observations);
        Assert.Equal(0, observation.StatusCode);
        Assert.IsType<OperationCanceledException>(observation.Failure);
    }

    [Fact]
    public async Task 带谷歌鉴权头_协议格式识别为gemini()
    {
        RecordingContext context = new();
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, "{}"));
        HttpRequestMessage request = Request();
        request.Headers.TryAddWithoutValidation("x-goog-api-key", "g-key");

        await ProviderTransport.SendAsync(request, clientFactory: Client(handler), context: context);

        Assert.Equal("gemini", Assert.Single(context.Observations).APIFormat);
    }

    [Fact]
    public async Task 未接审计_不记录也不影响请求()
    {
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, """{"ok":true}"""));

        ProviderTransport.OutboundResult result = await ProviderTransport.SendAsync(
            Request(), clientFactory: Client(handler));

        Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(result.Data));
    }

    [Fact]
    public async Task 审计记录抛错_不影响请求结果()
    {
        RecordingContext context = new();
        StubHandler handler = new(_ => Json(HttpStatusCode.OK, """{"ok":true}"""));

        ProviderTransport.OutboundResult result = await ProviderTransport.SendAsync(
            Request(), clientFactory: Client(handler), context: context);

        Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(result.Data));
    }
}
