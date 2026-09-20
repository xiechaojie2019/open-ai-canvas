#nullable enable
using System.Net;
using System.Text;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 文本任务与运行时上下文（熔断 / 并发槽）的集成契约测试。
/// 对应 Go: <c>provider_http_client.go</c> 中
/// <c>Coordinator.CircuitOpen</c> → <c>AcquireChannelSlot</c> → 请求 → <c>RecordChannelResult</c> 的顺序。
/// </summary>
public sealed class ProviderTextTaskCircuitTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responder;

        public StubHandler(Func<HttpResponseMessage> responder) => _responder = responder;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_responder());
        }
    }

    /// <summary>记录调用顺序的上下文桩。</summary>
    private sealed class RecordingContext : IProviderRequestContext
    {
        public RecordingContext(bool circuitOpen = false) => CircuitOpen = circuitOpen;

        public bool CircuitOpen { get; set; }

        public long MaxResponseBytes { get; set; } = ProviderTransport.DefaultMaxResponseBytes;

        public List<string> Events { get; } = [];

        public List<(string ChannelId, bool Failed)> Results { get; } = [];

        public Task<bool> IsCircuitOpenAsync(string channelId, CancellationToken cancellationToken)
        {
            Events.Add("circuit-check");
            return Task.FromResult(CircuitOpen);
        }

        public Task<Func<ValueTask>?> AcquireChannelSlotAsync(
            string channelId, string fallbackScope, CancellationToken cancellationToken)
        {
            Events.Add("acquire-slot");
            return Task.FromResult<Func<ValueTask>?>(() =>
            {
                Events.Add("release-slot");
                return ValueTask.CompletedTask;
            });
        }

        public Task RecordChannelResultAsync(string channelId, bool failed, CancellationToken cancellationToken)
        {
            Events.Add(failed ? "record-failure" : "record-success");
            Results.Add((channelId, failed));
            return Task.CompletedTask;
        }
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static TextTaskInput Input() => new()
    {
        Prompt = "问题",
        Config = new ProviderConfig
        {
            ChannelID = "chan-1",
            Model = "m",
            BaseURL = "https://api.example.com",
            APIKey = "k",
        },
    };

    // ------------------------------------------------------------ 熔断

    [Fact]
    public async Task 熔断_打开时短路且不占槽不发请求()
    {
        StubHandler handler = new(() => Ok("""{"choices":[{"message":{"content":"x"}}]}"""));
        RecordingContext context = new(circuitOpen: true);
        ProviderTextTask task = new(context, () => new HttpClient(handler));

        await Assert.ThrowsAsync<ProviderCircuitOpenException>(
            () => task.RunAsync(Input(), "chat-completion"));

        Assert.Equal(0, handler.Calls);
        Assert.Equal(["circuit-check"], context.Events);
    }

    [Fact]
    public async Task 熔断_未打开时正常执行()
    {
        StubHandler handler = new(() => Ok("""{"choices":[{"message":{"content":"x"}}]}"""));
        RecordingContext context = new(circuitOpen: false);
        ProviderTextTask task = new(context, () => new HttpClient(handler));

        await task.RunAsync(Input(), "chat-completion");

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task 熔断_渠道为空时跳过检查()
    {
        StubHandler handler = new(() => Ok("""{"choices":[{"message":{"content":"x"}}]}"""));
        RecordingContext context = new(circuitOpen: true);
        ProviderTextTask task = new(context, () => new HttpClient(handler));
        TextTaskInput input = Input();
        input.Config.ChannelID = "";

        // 无渠道 ID 时没有熔断主体，应直接执行。
        await task.RunAsync(input, "chat-completion");

        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("circuit-check", context.Events);
    }

    // ------------------------------------------------------------ 并发槽

    [Fact]
    public async Task 并发槽_获取与释放成对出现()
    {
        StubHandler handler = new(() => Ok("""{"choices":[{"message":{"content":"x"}}]}"""));
        RecordingContext context = new();
        ProviderTextTask task = new(context, () => new HttpClient(handler));

        await task.RunAsync(Input(), "chat-completion");

        Assert.Equal(["circuit-check", "acquire-slot", "record-success", "release-slot"], context.Events);
    }

    [Fact]
    public async Task 并发槽_请求失败时仍释放()
    {
        StubHandler handler = new(() => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        RecordingContext context = new();
        ProviderTextTask task = new(context, () => new HttpClient(handler));

        await Assert.ThrowsAsync<ProviderHttpException>(
            () => task.RunAsync(Input(), "chat-completion"));

        Assert.Contains("release-slot", context.Events);
        Assert.Contains("record-failure", context.Events);
    }

    [Fact]
    public async Task 并发槽_为null时不影响执行()
    {
        StubHandler handler = new(() => Ok("""{"choices":[{"message":{"content":"x"}}]}"""));
        ProviderTextTask task = new(new NullSlotContext(), () => new HttpClient(handler));

        Dictionary<string, object?> result = await task.RunAsync(Input(), "chat-completion");

        Assert.Equal("x", result["text"]);
    }

    private sealed class NullSlotContext : IProviderRequestContext
    {
        public long MaxResponseBytes => ProviderTransport.DefaultMaxResponseBytes;
    }

    // ------------------------------------------------------------ 熔断记账

    [Fact]
    public async Task 记账_成功时上报成功()
    {
        StubHandler handler = new(() => Ok("""{"choices":[{"message":{"content":"x"}}]}"""));
        RecordingContext context = new();
        ProviderTextTask task = new(context, () => new HttpClient(handler));

        await task.RunAsync(Input(), "chat-completion");

        Assert.Equal([("chan-1", false)], context.Results);
    }

    [Fact]
    public async Task 记账_失败时上报失败()
    {
        StubHandler handler = new(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        RecordingContext context = new();
        ProviderTextTask task = new(context, () => new HttpClient(handler));

        await Assert.ThrowsAsync<ProviderHttpException>(
            () => task.RunAsync(Input(), "chat-completion"));

        Assert.Equal([("chan-1", true)], context.Results);
    }

    [Fact]
    public async Task 记账_空正文也算失败()
    {
        StubHandler handler = new(() => Ok("""{"choices":[{"message":{"content":""}}]}"""));
        RecordingContext context = new();
        ProviderTextTask task = new(context, () => new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => task.RunAsync(Input(), "chat-completion"));

        Assert.Equal([("chan-1", true)], context.Results);
    }

    [Fact]
    public async Task 记账_记账自身抛错不掩盖原始异常()
    {
        StubHandler handler = new(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        ProviderTextTask task = new(new ThrowingRecordContext(), () => new HttpClient(handler));

        // 熔断记账是旁路，其失败不能改变主流程的异常类型。
        ProviderHttpException error = await Assert.ThrowsAsync<ProviderHttpException>(
            () => task.RunAsync(Input(), "chat-completion"));

        Assert.Equal(429, error.StatusCode);
    }

    private sealed class ThrowingRecordContext : IProviderRequestContext
    {
        public long MaxResponseBytes => ProviderTransport.DefaultMaxResponseBytes;

        public Task RecordChannelResultAsync(string channelId, bool failed, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("记账失败");
    }

    // ------------------------------------------------------------ 响应上限来自上下文

    [Fact]
    public async Task 响应上限_取自上下文的生成文件限制()
    {
        StubHandler handler = new(() => Ok("""{"choices":[{"message":{"content":"x"}}]}"""));
        ProviderTextTask task = new(new SmallLimitContext(), () => new HttpClient(handler));

        ProviderTransportException error = await Assert.ThrowsAsync<ProviderTransportException>(
            () => task.RunAsync(Input(), "chat-completion"));

        Assert.Contains("上游响应超过", error.Message, StringComparison.Ordinal);
    }

    private sealed class SmallLimitContext : IProviderRequestContext
    {
        public long MaxResponseBytes => 8;
    }
}
