#nullable enable
using System.Net;
using System.Text;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// Provider 出站传输安全边界的契约测试。
/// 对应 Go: <c>internal/app/provider_http_client.go</c> 的
/// <c>doBinaryWithConsumer</c> / <c>doJSON</c> / <c>applyProviderAuth</c> /
/// <c>ApplyDefaultOutboundHeaders</c> / <c>apiURLWithDefaultPrefix</c> / <c>formatStorageLimit</c>。
/// </summary>
public sealed class ProviderTransportTests
{
    // ------------------------------------------------------------ URL 拼接

    [Theory]
    // 无版本前缀：补默认 /v1。
    [InlineData("https://api.example.com", "/chat/completions", "https://api.example.com/v1/chat/completions")]
    // base 已带 /v1：不重复。
    [InlineData("https://api.example.com/v1", "/chat/completions", "https://api.example.com/v1/chat/completions")]
    // base 带尾斜杠。
    [InlineData("https://api.example.com/v1/", "/chat/completions", "https://api.example.com/v1/chat/completions")]
    // path 显式版本优先于 base 残留版本。
    [InlineData("https://api.example.com/v1", "/v2/chat", "https://api.example.com/v2/chat")]
    // path 自带版本时 base 版本被替换。
    [InlineData("https://api.example.com", "/v3/models", "https://api.example.com/v3/models")]
    public void 渠道URL拼接_版本前缀归一(string baseUrl, string path, string expected) =>
        Assert.Equal(expected, ProviderTransport.ChannelApiUrl(baseUrl, path));

    [Fact]
    public void 渠道URL拼接_base带api前缀时保留()
    {
        // /api/v1 是 channelAPIPrefixes 中的前缀，base 保留且不再补 /v1。
        Assert.Equal(
            "https://api.example.com/api/v1/chat/completions",
            ProviderTransport.ChannelApiUrl("https://api.example.com/api/v1", "/chat/completions"));
    }

    [Fact]
    public void 渠道URL拼接_空路径返回base()
    {
        Assert.Equal(
            "https://api.example.com",
            ProviderTransport.ChannelApiUrl("https://api.example.com", ""));
    }

    // ------------------------------------------------------------ formatStorageLimit

    [Theory]
    [InlineData(1L << 30, "1GB")]
    [InlineData(2L << 30, "2GB")]
    [InlineData(64L << 20, "64MB")]
    [InlineData(1L << 20, "1MB")]
    public void 大小文案_整GB用GB否则MB(long value, string expected) =>
        Assert.Equal(expected, ProviderTransport.FormatStorageLimit(value));

    // ------------------------------------------------------------ 鉴权头

    [Fact]
    public void 鉴权_openai走Bearer()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://api.example.com/v1/x");
        ProviderTransport.ApplyProviderAuth(request, new ProviderConfig { APIKey = "sk-1" });

        Assert.Equal("Bearer sk-1", request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public void 鉴权_claude走xApiKey并带固定版本()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://api.example.com/v1/x");
        ProviderTransport.ApplyProviderAuth(
            request, new ProviderConfig { APIKey = "sk-1", APIFormat = "claude" });

        Assert.Equal("sk-1", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
        Assert.False(request.Headers.Contains("Authorization"));
    }

    [Fact]
    public void 鉴权_gemini走xGoogApiKey()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://api.example.com/v1/x");
        ProviderTransport.ApplyProviderAuth(
            request, new ProviderConfig { APIKey = "sk-1", APIFormat = "gemini" });

        Assert.Equal("sk-1", request.Headers.GetValues("x-goog-api-key").Single());
    }

    // ------------------------------------------------------------ 默认头

    [Fact]
    public void 默认头_未设置时补UserAgent()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://api.example.com/v1/x");
        ProviderTransport.ApplyDefaultHeaders(request);

        // 注意：.NET 会把 UA 按空格拆成多个 product token，线格式以空格拼回，
        // 因此断言要比较拼接后的整串（与 Go 的整串语义一致）。
        Assert.Equal(ProviderTransport.DefaultUserAgent, ProviderTransport.ReadUserAgent(request));
    }

    [Fact]
    public void 默认头_已设置时不覆盖()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://api.example.com/v1/x");
        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", "custom/1.0");
        ProviderTransport.ApplyDefaultHeaders(request);

        Assert.Equal("custom/1.0", ProviderTransport.ReadUserAgent(request));
    }

    [Fact]
    public void 默认头_空值视为未设置()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://api.example.com/v1/x");
        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", "   ");
        ProviderTransport.ApplyDefaultHeaders(request);

        Assert.Equal(ProviderTransport.DefaultUserAgent, ProviderTransport.ReadUserAgent(request));
    }

    // ------------------------------------------------------------ 请求装配

    [Fact]
    public void 请求装配_JSON内容类型为utf8()
    {
        using HttpRequestMessage request = ProviderTransport.BuildJsonPost(
            new ProviderConfig { BaseURL = "https://api.example.com", APIKey = "k" },
            "/chat/completions",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["model"] = "m" });

        Assert.Equal("https://api.example.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", request.Content.Headers.ContentType.CharSet);
    }

    [Fact]
    public void 请求装配_流式时带Accept事件流()
    {
        using HttpRequestMessage request = ProviderTransport.BuildJsonPost(
            new ProviderConfig { BaseURL = "https://api.example.com", APIKey = "k" },
            "/chat/completions",
            new Dictionary<string, object?>(StringComparer.Ordinal),
            streaming: true);

        Assert.Equal("text/event-stream", request.Headers.GetValues("Accept").Single());
    }

    [Fact]
    public void 请求装配_非流式不带Accept事件流()
    {
        using HttpRequestMessage request = ProviderTransport.BuildJsonPost(
            new ProviderConfig { BaseURL = "https://api.example.com", APIKey = "k" },
            "/chat/completions",
            new Dictionary<string, object?>(StringComparer.Ordinal));

        Assert.False(request.Headers.Contains("Accept"));
    }

    // ------------------------------------------------------------ JSON 解析

    [Fact]
    public void JSON解析_合法对象()
    {
        Dictionary<string, object?>? payload =
            ProviderTransport.ParseObject(Encoding.UTF8.GetBytes("""{"a":1,"b":"x"}"""));

        Assert.NotNull(payload);
        Assert.Equal(1d, payload["a"]);
        Assert.Equal("x", payload["b"]);
    }

    [Fact]
    public void JSON解析_数组返回null() =>
        Assert.Null(ProviderTransport.ParseObject(Encoding.UTF8.GetBytes("[1,2]")));

    [Fact]
    public void JSON解析_空字节返回null() =>
        Assert.Null(ProviderTransport.ParseObject([]));

    [Fact]
    public void JSON合法性_区分合法与非法()
    {
        Assert.True(ProviderTransport.IsValidJson(Encoding.UTF8.GetBytes("""{"a":1}""")));
        Assert.False(ProviderTransport.IsValidJson(Encoding.UTF8.GetBytes("{not json")));
    }

    [Fact]
    public void 超限文案_含可读大小()
    {
        Assert.Equal("上游响应超过 64MB 限制", ProviderTransport.OversizeMessage(64L << 20));
        Assert.Equal("上游响应超过 1GB 限制", ProviderTransport.OversizeMessage(1L << 30));
    }
}
