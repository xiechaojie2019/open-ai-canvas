#nullable enable
using System.Net;
using System.Text;
using OpenAICanvas.Protocol;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 声明式协议执行器的契约测试。
/// 对应 Go: <c>internal/app/provider_protocol.go</c> 的
/// <c>executeProtocolBinaryRequestWithConsumer</c> / <c>applyProtocolAuth</c> / <c>protocolRequestURL</c>。
/// </summary>
public sealed class ProviderProtocolExecutorTests
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

    private static Func<HttpClient> Client(StubHandler handler) => () => new HttpClient(handler);

    private static HttpResponseMessage Ok(string body = "{}", string contentType = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private static ProviderConfig Config(string apiFormat = "", string apiKey = "k", string secretKey = "") => new()
    {
        BaseURL = "https://api.example.com",
        APIKey = apiKey,
        SecretKey = secretKey,
        APIFormat = apiFormat,
    };

    // ------------------------------------------------------------ 规格校验

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("post")]
    public void 校验_支持的方法通过(string method)
    {
        // 不应抛异常。
        ProviderProtocolExecutor.ValidateSpec(new RequestSpec { Method = method, Path = "/x" });
    }

    [Fact]
    public void 校验_缺少方法报错()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderProtocolExecutor.ValidateSpec(new RequestSpec { Method = "  " }));

        Assert.Equal("协议请求缺少 method", error.Message);
    }

    [Theory]
    [InlineData("TRACE")]
    [InlineData("CONNECT")]
    [InlineData("EVIL")]
    public void 校验_未授权方法被拒绝(string method)
    {
        // 宿主白名单是安全边界：manifest 不能引入任意动词。
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderProtocolExecutor.ValidateSpec(new RequestSpec { Method = method }));

        Assert.Contains("不受支持", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 请求装配

    [Fact]
    public async Task 执行_JSON请求体与内容类型()
    {
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(),
            new RequestSpec
            {
                Method = "POST",
                Path = "/create",
                Body = new Dictionary<string, object?>(StringComparer.Ordinal) { ["prompt"] = "猫" },
            },
            null,
            null,
            null,
            CancellationToken.None,
            Client(handler));

        Assert.Contains("application/json", handler.LastContentType!, StringComparison.Ordinal);
        Assert.Contains("猫", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 执行_URL带版本前缀()
    {
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(),
            new RequestSpec { Method = "GET", Path = "/models" },
            null, null, null, CancellationToken.None, Client(handler));

        Assert.Equal("https://api.example.com/v1/models", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task 执行_OriginPath忽略BaseURL路径()
    {
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            new ProviderConfig { BaseURL = "https://api.example.com/v1/", APIKey = "k" },
            new RequestSpec { Method = "POST", Path = "/custom/root", OriginPath = true },
            null, null, null, CancellationToken.None, Client(handler));

        Assert.Equal("https://api.example.com/custom/root", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task 执行_查询参数被追加()
    {
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(),
            new RequestSpec
            {
                Method = "GET",
                Path = "/models",
                Query = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["limit"] = ["10", "20"],
                },
            },
            null, null, null, CancellationToken.None, Client(handler));

        string url = handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("limit=10", url, StringComparison.Ordinal);
        Assert.Contains("limit=20", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 执行_自定义请求头被写入()
    {
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(),
            new RequestSpec
            {
                Method = "GET",
                Path = "/x",
                Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Custom"] = "v" },
            },
            null, null, null, CancellationToken.None, Client(handler));

        Assert.Equal("v", handler.LastRequest!.Headers.GetValues("X-Custom").Single());
    }

    [Fact]
    public async Task 执行_渠道自定义头参与发送()
    {
        StubHandler handler = new(_ => Ok());
        ProviderConfig config = Config();
        config.Headers = [new OpenAICanvas.Outbound.OutboundHeader { Name = "X-Channel", Value = "c" }];

        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            config,
            new RequestSpec { Method = "GET", Path = "/x" },
            null, null, null, CancellationToken.None, Client(handler));

        Assert.Equal("c", handler.LastRequest!.Headers.GetValues("X-Channel").Single());
    }

    [Fact]
    public async Task 执行_流式时带Accept事件流()
    {
        StubHandler handler = new(_ => Ok("{}", "text/event-stream"));
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(),
            new RequestSpec { Method = "POST", Path = "/x" },
            consume: (_, _) => { },
            null, null, CancellationToken.None, Client(handler));

        Assert.Equal("text/event-stream", handler.LastRequest!.Headers.GetValues("Accept").Single());
    }

    [Fact]
    public async Task 执行_非2xx抛HTTP异常()
    {
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        await Assert.ThrowsAsync<ProviderHttpException>(
            () => ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
                Config(), new RequestSpec { Method = "POST", Path = "/x" },
                null, null, null, CancellationToken.None, Client(handler)));
    }

    // ------------------------------------------------------------ 鉴权

    [Fact]
    public async Task 鉴权_未声明类型时回落渠道默认()
    {
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(), new RequestSpec { Method = "GET", Path = "/x", Auth = new ManifestAuth() },
            null, null, null, CancellationToken.None, Client(handler));

        Assert.Equal("Bearer k", handler.LastRequest!.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task 鉴权_none不注入()
    {
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(), new RequestSpec { Method = "GET", Path = "/x", Auth = new ManifestAuth { Type = "none" } },
            null, null, null, CancellationToken.None, Client(handler));

        Assert.False(handler.LastRequest!.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task 鉴权_bearer用默认前缀()
    {
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(), new RequestSpec { Method = "GET", Path = "/x", Auth = new ManifestAuth { Type = "bearer" } },
            null, null, null, CancellationToken.None, Client(handler));

        Assert.Equal("Bearer k", handler.LastRequest!.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task 鉴权_bearer自定义头与前缀()
    {
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(),
            new RequestSpec
            {
                Method = "GET",
                Path = "/x",
                Auth = new ManifestAuth { Type = "bearer", Header = "X-Token", Prefix = "Token " },
            },
            null, null, null, CancellationToken.None, Client(handler));

        Assert.Equal("Token k", handler.LastRequest!.Headers.GetValues("X-Token").Single());
    }

    [Fact]
    public async Task 鉴权_apiKey缺少头名报错()
    {
        StubHandler handler = new(_ => Ok());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
                Config(),
                new RequestSpec { Method = "GET", Path = "/x", Auth = new ManifestAuth { Type = "api-key" } },
                null, null, null, CancellationToken.None, Client(handler)));

        Assert.Equal("插件 header 鉴权缺少 header 名称", error.Message);
    }

    [Fact]
    public async Task 鉴权_anthropic带固定版本()
    {
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(), new RequestSpec { Method = "POST", Path = "/x", Auth = new ManifestAuth { Type = "anthropic" } },
            null, null, null, CancellationToken.None, Client(handler));

        Assert.Equal("k", handler.LastRequest!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.LastRequest.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public async Task 鉴权_gemini用xGoogApiKey()
    {
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(), new RequestSpec { Method = "POST", Path = "/x", Auth = new ManifestAuth { Type = "gemini" } },
            null, null, null, CancellationToken.None, Client(handler));

        Assert.Equal("k", handler.LastRequest!.Headers.GetValues("x-goog-api-key").Single());
    }

    [Fact]
    public async Task 鉴权_query参数注入URL()
    {
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(),
            new RequestSpec
            {
                Method = "GET",
                Path = "/x",
                Auth = new ManifestAuth { Type = "query", Query = "api_key" },
            },
            null, null, null, CancellationToken.None, Client(handler));

        Assert.Contains("api_key=k", handler.LastRequest!.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 鉴权_query缺少参数名报错()
    {
        StubHandler handler = new(_ => Ok());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
                Config(),
                new RequestSpec { Method = "GET", Path = "/x", Auth = new ManifestAuth { Type = "query" } },
                null, null, null, CancellationToken.None, Client(handler)));

        Assert.Equal("插件 query 鉴权缺少参数名", error.Message);
    }

    [Fact]
    public async Task 鉴权_basic用用户名与密钥()
    {
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(apiKey: "user", secretKey: "pass"),
            new RequestSpec { Method = "GET", Path = "/x", Auth = new ManifestAuth { Type = "basic", SecretField = "secret" } },
            null, null, null, CancellationToken.None, Client(handler));

        string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"));
        Assert.Equal(expected, handler.LastRequest!.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task 鉴权_basic无用户名时用凭证()
    {
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(apiKey: "user", secretKey: "pass"),
            new RequestSpec
            {
                Method = "GET",
                Path = "/x",
                Auth = new ManifestAuth { Type = "basic", Username = "explicit", SecretField = "secret" },
            },
            null, null, null, CancellationToken.None, Client(handler));

        string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("explicit:pass"));
        Assert.Equal(expected, handler.LastRequest!.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task 鉴权_awsSigV4缺密钥报错()
    {
        StubHandler handler = new(_ => Ok());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
                Config(apiKey: ""),
                new RequestSpec { Method = "POST", Path = "/x", Auth = new ManifestAuth { Type = "aws-sigv4" } },
                null, null, null, CancellationToken.None, Client(handler)));

        Assert.Contains("AWS SigV4", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 鉴权_tc3缺密钥报错()
    {
        StubHandler handler = new(_ => Ok());

        // 注意：SecretField 为空时 CredentialField 会回落到 APIKey（Go 同此行为），
        // 因此必须让 APIKey 也为空，secret 才是真正的空值。
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
                Config(apiKey: ""),
                new RequestSpec { Method = "POST", Path = "/x", Auth = new ManifestAuth { Type = "tc3" } },
                null, null, null, CancellationToken.None, Client(handler)));

        Assert.Contains("TC3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 鉴权_空SecretField回落到ApiKey()
    {
        // 这是 Go 的 protocolCredentialField 默认分支行为：字段名不识别时返回 APIKey。
        // 因此 SecretField 未声明时，secret 与 credential 是同一个值（而非空）。
        StubHandler handler = new(_ => Ok());
        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(apiKey: "ak", secretKey: "sk"),
            new RequestSpec
            {
                Method = "GET",
                Path = "/x",
                Auth = new ManifestAuth { Type = "basic", Username = "u" },
            },
            null, null, null, CancellationToken.None, Client(handler));

        string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("u:ak"));
        Assert.Equal(expected, handler.LastRequest!.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task 鉴权_未知驱动被拒绝()
    {
        StubHandler handler = new(_ => Ok());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
                Config(),
                new RequestSpec { Method = "GET", Path = "/x", Auth = new ManifestAuth { Type = "magic" } },
                null, null, null, CancellationToken.None, Client(handler)));

        Assert.Contains("尚未启用的鉴权驱动", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 凭证字段映射

    [Fact]
    public void 凭证_默认取ApiKey()
    {
        ProviderCredentials credentials = new("ak", "sk");

        Assert.Equal("ak", ProtocolRequestBuilder.CredentialField(credentials, ""));
        Assert.Equal("ak", ProtocolRequestBuilder.CredentialField(credentials, "apiKey"));
    }

    [Theory]
    [InlineData("secretKey")]
    [InlineData("secret_key")]
    [InlineData("secret")]
    [InlineData("SECRET")]
    public void 凭证_密钥别名取SecretKey(string field) =>
        Assert.Equal("sk", ProtocolRequestBuilder.CredentialField(new ProviderCredentials("ak", "sk"), field));

    // ------------------------------------------------------------ 媒体加载

    [Fact]
    public async Task 执行_multipart携带文件()
    {
        StubHandler handler = new(_ => Ok());
        List<MediaReference> loaded = [];
        ProviderProtocolMediaLoader loader = reference =>
        {
            loaded.Add(reference);
            return (Encoding.UTF8.GetBytes("DATA"), "image/png");
        };

        await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
            Config(),
            new RequestSpec
            {
                Method = "POST",
                Path = "/upload",
                ContentType = "multipart/form-data",
                Body = new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "v" },
                Files =
                [
                    new RequestFilePart
                    {
                        Name = "file",
                        Filename = "a.png",
                        Reference = new MediaReference { URL = "https://x/a.png" },
                    },
                ],
            },
            null,
            loader,
            null,
            CancellationToken.None,
            Client(handler));

        Assert.Single(loaded);
        Assert.Contains("multipart/form-data", handler.LastContentType!, StringComparison.Ordinal);
        Assert.Contains("DATA", handler.LastBody!, StringComparison.Ordinal);
    }
}
