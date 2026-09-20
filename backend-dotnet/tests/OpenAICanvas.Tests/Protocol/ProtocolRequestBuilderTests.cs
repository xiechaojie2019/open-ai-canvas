#nullable enable
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OpenAICanvas.Protocol;
using Xunit;

namespace OpenAICanvas.Tests.Protocol;

/// <summary>
/// 声明式协议请求构建与签名的契约测试。
/// 对应 Go: <c>internal/app/provider_protocol.go</c> 的
/// <c>protocolRequestBody</c> / <c>protocolFormValues</c> / <c>safeProtocolFilename</c> /
/// <c>protocolRequestURL</c> / <c>appendProtocolQuery</c> / <c>signProtocolAWSV4</c> /
/// <c>signProtocolTC3</c>。
/// </summary>
public sealed class ProtocolRequestBuilderTests
{
    // ------------------------------------------------------------ 请求体

    [Fact]
    public void 请求体_JSON序列化()
    {
        Dictionary<string, object?> body = new() { ["model"] = "m1", ["n"] = 2 };
        (byte[]? payload, string contentType) = ProtocolRequestBuilder.BuildBody("application/json", body, null);

        Assert.Equal("application/json", contentType);
        Assert.NotNull(payload);
        using JsonDocument document = JsonDocument.Parse(payload!);
        Assert.Equal("m1", document.RootElement.GetProperty("model").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("n").GetInt32());
    }

    [Fact]
    public void 请求体_空ContentType按JSON处理()
    {
        (byte[]? payload, string contentType) =
            ProtocolRequestBuilder.BuildBody("", new Dictionary<string, object?> { ["a"] = 1 }, null);

        Assert.Equal("application/json", contentType);
        Assert.NotNull(payload);
    }

    [Fact]
    public void 请求体_无body且无files时为空()
    {
        (byte[]? payload, string contentType) = ProtocolRequestBuilder.BuildBody("application/json", null, null);
        Assert.Null(payload);
        Assert.Equal("", contentType);
    }

    [Fact]
    public void 请求体_表单编码按键排序()
    {
        Dictionary<string, object?> body = new()
        {
            ["zebra"] = "z",
            ["alpha"] = "a",
            ["multi"] = new List<object?> { "1", "2" },
        };
        (byte[]? payload, string contentType) =
            ProtocolRequestBuilder.BuildBody("application/x-www-form-urlencoded", body, null);

        Assert.Equal("application/x-www-form-urlencoded", contentType);
        Assert.Equal("alpha=a&multi=1&multi=2&zebra=z", Encoding.UTF8.GetString(payload!));
    }

    [Fact]
    public void 请求体_表单值类型转换与Go一致()
    {
        Assert.Equal(["true"], ProtocolRequestBuilder.FormValues(true));
        Assert.Equal(["false"], ProtocolRequestBuilder.FormValues(false));
        Assert.Equal(["42"], ProtocolRequestBuilder.FormValues(42));
        // 浮点用定点格式，不用科学计数法（对应 Go 的 FormatFloat 'f'）。
        Assert.Equal(["1.5"], ProtocolRequestBuilder.FormValues(1.5d));
        Assert.Equal(["1000000"], ProtocolRequestBuilder.FormValues(1000000d));
        Assert.Empty(ProtocolRequestBuilder.FormValues(null));
        // 嵌套数组被展平。
        Assert.Equal(["a", "b", "c"],
            ProtocolRequestBuilder.FormValues(new List<object?> { "a", new List<object?> { "b", "c" } }));
    }

    [Fact]
    public void 请求体_浮点不吃科学计数法()
    {
        // 大整数不得变成 1E+15 之类，否则上游签名/参数校验会失败。
        string text = ProtocolRequestBuilder.FormatFloatGo(1e15);
        Assert.DoesNotContain("E", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1000000000000000", text);

        // 极小值同样避免科学计数法。
        string small = ProtocolRequestBuilder.FormatFloatGo(0.000001);
        Assert.DoesNotContain("E", small, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("0.000001", small);
    }

    [Fact]
    public void 请求体_multipart含文本字段与文件()
    {
        Dictionary<string, object?> body = new() { ["model"] = "m1" };
        List<RequestFilePart> files =
        [
            new RequestFilePart
            {
                Name = "image",
                Filename = "a.png",
                MIMEType = "image/png",
                Reference = new MediaReference { ID = "r1", DataURL = "data:image/png;base64,AAAA" },
            },
        ];
        var loader = (MediaReference _) => (new byte[] { 0x01, 0x02 }, "image/png");

        (byte[]? payload, string contentType) =
            ProtocolRequestBuilder.BuildBody("multipart/form-data", body, files, loader);

        Assert.StartsWith("multipart/form-data; boundary=", contentType, StringComparison.Ordinal);
        string text = Encoding.UTF8.GetString(payload!);
        Assert.Contains("name=\"model\"", text, StringComparison.Ordinal);
        Assert.Contains("m1", text, StringComparison.Ordinal);
        Assert.Contains("filename=\"a.png\"", text, StringComparison.Ordinal);
        Assert.Contains("Content-Type: image/png", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 请求体_multipart使用嗅探MIME当未声明时()
    {
        List<RequestFilePart> files =
        [
            new RequestFilePart
            {
                Name = "file",
                Filename = "x.bin",
                Reference = new MediaReference { ID = "r1" },
            },
        ];
        var loader = (MediaReference _) => (new byte[] { 0x09 }, "video/mp4");

        (byte[]? payload, _) = ProtocolRequestBuilder.BuildBody("multipart/form-data", null, files, loader);

        Assert.Contains("Content-Type: video/mp4", Encoding.UTF8.GetString(payload!), StringComparison.Ordinal);
    }

    [Fact]
    public void 请求体_multipart缺加载器时报错()
    {
        List<RequestFilePart> files = [new RequestFilePart { Name = "f" }];

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProtocolRequestBuilder.BuildBody("multipart/form-data", null, files, null));
        Assert.Contains("读取 multipart 文件", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 请求体_二进制接受字节与字符串()
    {
        (byte[]? fromBytes, string type1) = ProtocolRequestBuilder.BuildBody(
            "application/octet-stream", new byte[] { 1, 2, 3 }, null);
        Assert.Equal(new byte[] { 1, 2, 3 }, fromBytes);
        Assert.Equal("application/octet-stream", type1);

        (byte[]? fromText, _) = ProtocolRequestBuilder.BuildBody(
            "application/octet-stream", "hello", null);
        Assert.Equal("hello", Encoding.UTF8.GetString(fromText!));
    }

    [Fact]
    public void 请求体_二进制dataURL解析MIME()
    {
        (byte[]? payload, string contentType) = ProtocolRequestBuilder.BuildBody(
            "application/octet-stream", "data:image/png;base64,AQID", null);

        Assert.Equal(new byte[] { 1, 2, 3 }, payload);
        // 显式声明的 MIME 优先于请求体声明的 contentType。
        Assert.Equal("image/png", contentType);
    }

    [Fact]
    public void 请求体_二进制非法类型报错()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProtocolRequestBuilder.BuildBody("application/octet-stream", 12345, null));
        Assert.Contains("二进制协议请求体必须是字节或字符串", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 请求体_不支持的ContentType报错()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProtocolRequestBuilder.BuildBody("application/xml", "x", null));
        Assert.Contains("暂不支持", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 文件名净化

    [Theory]
    [InlineData("a.png", "a.png")]
    [InlineData("  a.png  ", "a.png")]
    [InlineData("dir/sub/a.png", "a.png")]
    [InlineData("dir\\sub\\a.png", "a.png")]
    [InlineData("a\"b.png", "ab.png")]           // 双引号剔除
    [InlineData("", "upload.bin")]
    [InlineData("   ", "upload.bin")]
    [InlineData("dir/", "upload.bin")]
    public void 文件名净化(string input, string expected) =>
        Assert.Equal(expected, ProtocolRequestBuilder.SafeProtocolFilename(input));

    [Fact]
    public void 文件名净化_剔除控制字符()
    {
        string withControl = "a\u0001\u001Fb.png";
        Assert.Equal("ab.png", ProtocolRequestBuilder.SafeProtocolFilename(withControl));
    }

    // ------------------------------------------------------------ URL 拼接

    [Fact]
    public void URL_普通路径走apiUrlBuilder()
    {
        RequestSpec spec = new() { Path = "/chat/completions" };
        string url = ProtocolRequestBuilder.BuildUrl(
            "https://api.example.com", spec, (baseUrl, path) => baseUrl + "/v1" + path);

        Assert.Equal("https://api.example.com/v1/chat/completions", url);
    }

    [Fact]
    public void URL_默认拼接去掉重复斜杠()
    {
        RequestSpec spec = new() { Path = "/v1/models" };
        Assert.Equal("https://api.example.com/v1/models",
            ProtocolRequestBuilder.BuildUrl("https://api.example.com/", spec));
    }

    [Fact]
    public void URL_OriginPath用绝对路径替换base路径()
    {
        RequestSpec spec = new() { Path = "/custom/endpoint", OriginPath = true };
        string url = ProtocolRequestBuilder.BuildUrl("https://api.example.com/v1/base", spec);

        Assert.Equal("https://api.example.com/custom/endpoint", url);
    }

    [Fact]
    public void URL_OriginPath必须是绝对路径()
    {
        RequestSpec spec = new() { Path = "relative/path", OriginPath = true };
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProtocolRequestBuilder.BuildUrl("https://api.example.com", spec));
        Assert.Contains("必须使用绝对路径", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void URL_OriginPath要求有效BaseURL()
    {
        RequestSpec spec = new() { Path = "/x", OriginPath = true };
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProtocolRequestBuilder.BuildUrl("not-a-url", spec));
        Assert.Contains("Base URL 无效", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void URL_查询参数累加且保留原有()
    {
        RequestSpec spec = new()
        {
            Path = "/x",
            Query = new Dictionary<string, List<string>> { ["b"] = ["2"], ["c"] = ["3", "4"] },
        };
        string url = ProtocolRequestBuilder.BuildUrl(
            "https://api.example.com", spec, (baseUrl, path) => baseUrl + path);
        // 原始查询保留（按序拼接）。
        string withExisting = ProtocolRequestBuilder.AppendQuery(
            "https://api.example.com/x?a=1",
            new Dictionary<string, List<string>> { ["b"] = ["2"] });

        Assert.Contains("b=2", url, StringComparison.Ordinal);
        Assert.Contains("c=3", url, StringComparison.Ordinal);
        Assert.Contains("c=4", url, StringComparison.Ordinal);
        Assert.Contains("a=1", withExisting, StringComparison.Ordinal);
        Assert.Contains("b=2", withExisting, StringComparison.Ordinal);
    }

    [Fact]
    public void URL_空查询参数不改动()
    {
        Assert.Equal("https://api.example.com/x",
            ProtocolRequestBuilder.AppendQuery("https://api.example.com/x", null));
        Assert.Equal("https://api.example.com/x",
            ProtocolRequestBuilder.AppendQuery("https://api.example.com/x",
                new Dictionary<string, List<string>>()));
    }

    // ------------------------------------------------------------ TC3 签名

    [Fact]
    public void TC3签名_生成授权头与时间戳()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://hunyuan.tencentcloudapi.com/");
        request.Content = new StringContent("""{"a":1}""", Encoding.UTF8, "application/json");

        ProtocolRequestBuilder.SignTc3(
            request, "AKIDEXAMPLE", "SECRETKEY", new ManifestAuth { Service = "hunyuan" },
            Encoding.UTF8.GetBytes("""{"a":1}"""),
            DateTimeOffset.FromUnixTimeSeconds(1600000000));

        string authorization = request.Headers.GetValues("Authorization").First();
        Assert.StartsWith("TC3-HMAC-SHA256 Credential=AKIDEXAMPLE/", authorization, StringComparison.Ordinal);
        Assert.Contains("/hunyuan/tc3_request", authorization, StringComparison.Ordinal);
        Assert.Contains("SignedHeaders=content-type;host", authorization, StringComparison.Ordinal);
        Assert.Contains("Signature=", authorization, StringComparison.Ordinal);
        Assert.Equal("1600000000", request.Headers.GetValues("X-TC-Timestamp").First());
    }

    [Fact]
    public void TC3签名_确定性输出()
    {
        static string Sign()
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "https://svc.example.com/path");
            request.Content = new StringContent("body", Encoding.UTF8, "application/json");
            ProtocolRequestBuilder.SignTc3(
                request, "id", "key", new ManifestAuth { Service = "svc" },
                Encoding.UTF8.GetBytes("body"),
                DateTimeOffset.FromUnixTimeSeconds(1600000000));
            return request.Headers.GetValues("Authorization").First();
        }

        // 固定输入必须产生固定签名（否则说明掺入了时间等不确定因素）。
        Assert.Equal(Sign(), Sign());
    }

    [Fact]
    public void TC3签名_缺少凭证报错()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://svc.example.com/");
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProtocolRequestBuilder.SignTc3(request, "", "key", new ManifestAuth()));
        Assert.Contains("需要 SecretId 和 SecretKey", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TC3签名_区域写入XTCRegion()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://svc.example.com/");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        ProtocolRequestBuilder.SignTc3(
            request, "id", "key", new ManifestAuth { Service = "svc", Region = "ap-guangzhou" },
            Encoding.UTF8.GetBytes("{}"), DateTimeOffset.FromUnixTimeSeconds(1600000000));

        Assert.Equal("ap-guangzhou", request.Headers.GetValues("X-TC-Region").First());
    }

    // ------------------------------------------------------------ AWS SigV4

    [Fact]
    public void AwsV4签名_生成授权头与内容散列()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://bedrock-runtime.us-east-1.amazonaws.com/model/x/invoke");
        byte[] payload = Encoding.UTF8.GetBytes("""{"p":1}""");

        ProtocolRequestBuilder.SignAwsV4(
            request, "AKIDEXAMPLE", "SECRET",
            new ManifestAuth { Service = "bedrock", Region = "us-east-1" },
            payload,
            DateTimeOffset.FromUnixTimeSeconds(1600000000));

        string authorization = request.Headers.GetValues("Authorization").First();
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", authorization, StringComparison.Ordinal);
        Assert.Contains("/us-east-1/bedrock/aws4_request", authorization, StringComparison.Ordinal);
        Assert.Contains("SignedHeaders=", authorization, StringComparison.Ordinal);
        Assert.Contains("Signature=", authorization, StringComparison.Ordinal);

        // X-Amz-Content-Sha256 必须是正文的 SHA256 十六进制。
        string contentHash = request.Headers.GetValues("X-Amz-Content-Sha256").First();
        Assert.Equal(64, contentHash.Length);
        Assert.Equal(contentHash.ToLowerInvariant(), contentHash);
    }

    [Fact]
    public void AwsV4签名_从主机名推断region()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://bedrock-runtime.eu-west-1.amazonaws.com/x");
        ProtocolRequestBuilder.SignAwsV4(
            request, "ak", "sk", new ManifestAuth { Service = "bedrock", Region = "" },
            [], DateTimeOffset.FromUnixTimeSeconds(1600000000));

        Assert.Contains("/eu-west-1/bedrock/aws4_request",
            request.Headers.GetValues("Authorization").First(), StringComparison.Ordinal);
    }

    [Fact]
    public void AwsV4签名_无法推断region时报错()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://example.com/x");
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProtocolRequestBuilder.SignAwsV4(
                request, "ak", "sk", new ManifestAuth { Service = "bedrock", Region = "" },
                [], DateTimeOffset.FromUnixTimeSeconds(1600000000)));
        Assert.Contains("无法从 Base URL 推断 region", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AwsV4签名_缺少凭证报错()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://bedrock-runtime.us-east-1.amazonaws.com/x");
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProtocolRequestBuilder.SignAwsV4(request, "", "", new ManifestAuth()));
        Assert.Contains("需要 Access Key ID 和 Secret Access Key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AwsV4签名_canonicalHeaders跳过易变头并折叠空白()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://h.example.com/p");
        request.Headers.TryAddWithoutValidation("User-Agent", "test-agent");
        request.Headers.TryAddWithoutValidation("X-Custom", "  a   b  ");
        request.Headers.TryAddWithoutValidation("Authorization", "should-be-skipped");
        request.Headers.TryAddWithoutValidation("Z-Test", "z");

        (string canonical, string signed) = ProtocolRequestBuilder.CanonicalHeaders(request);

        // host 恒在内；跳过 authorization 与 user-agent。
        Assert.Contains("host:h.example.com", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("should-be-skipped", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("test-agent", canonical, StringComparison.Ordinal);
        // 连续空白折叠为单空格。
        Assert.Contains("x-custom:a b", canonical, StringComparison.Ordinal);
        // signedHeaders 按字典序、分号连接。
        Assert.Equal("host;x-custom;z-test", signed);
    }

    // ------------------------------------------------------------ 凭证字段

    [Theory]
    [InlineData("secretKey", "SK")]
    [InlineData("secret_key", "SK")]
    [InlineData("secret", "SK")]
    [InlineData("SECRETKEY", "SK")]
    [InlineData("apiKey", "AK")]
    [InlineData("", "AK")]
    [InlineData("other", "AK")]
    public void 凭证字段解析(string field, string expected) =>
        Assert.Equal(expected, ProtocolRequestBuilder.CredentialField(new ProviderCredentials("AK", "SK"), field));

    [Fact]
    public void 凭证字段解析_去空白()
    {
        Assert.Equal("AK",
            ProtocolRequestBuilder.CredentialField(new ProviderCredentials("  AK  ", "  SK  "), "apiKey"));
        Assert.Equal("SK",
            ProtocolRequestBuilder.CredentialField(new ProviderCredentials("  AK  ", "  SK  "), "secret"));
    }

    // ------------------------------------------------------------ data URL

    [Fact]
    public void dataURL解析_MIME与字节()
    {
        (string mimeType, byte[] data) = ProtocolRequestBuilder.DecodeDataURL("data:image/png;base64,AQID");
        Assert.Equal("image/png", mimeType);
        Assert.Equal(new byte[] { 1, 2, 3 }, data);
    }

    [Fact]
    public void dataURL解析_无MIME时为空串()
    {
        (string mimeType, byte[] data) = ProtocolRequestBuilder.DecodeDataURL("data:;base64,AQID");
        Assert.Equal("", mimeType);
        Assert.Equal(new byte[] { 1, 2, 3 }, data);
    }

    [Fact]
    public void dataURL解析_非法格式报错()
    {
        Assert.Throws<InvalidOperationException>(() => ProtocolRequestBuilder.DecodeDataURL("https://x/a.png"));
        Assert.Throws<InvalidOperationException>(() => ProtocolRequestBuilder.DecodeDataURL("data:image/png;base64"));
        Assert.Throws<InvalidOperationException>(() => ProtocolRequestBuilder.DecodeDataURL("data:image/png;base64,!!!"));
    }
}
