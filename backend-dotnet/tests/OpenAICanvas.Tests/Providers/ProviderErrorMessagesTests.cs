#nullable enable
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Outbound;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// Provider 用户可见错误文案与工具函数的契约测试。
/// 对应 Go: <c>internal/app/provider.go</c> 的
/// <c>providerUserFacingErrorMessage</c> / <c>providerPayloadErrorCategory</c> /
/// <c>providerHTTPError.Error</c> / 各小工具函数。
/// </summary>
public sealed class ProviderErrorMessagesTests
{
    // ------------------------------------------------------------ HTTP 状态码文案

    [Theory]
    [InlineData(524, "上游网关超时（524）")]
    [InlineData(400, "模型服务拒绝了请求，请检查模型和参数")]
    [InlineData(422, "模型服务拒绝了请求，请检查模型和参数")]
    [InlineData(401, "模型服务鉴权失败")]
    [InlineData(403, "模型服务鉴权失败")]
    [InlineData(404, "模型或模型接口不存在")]
    [InlineData(408, "模型服务响应超时")]
    [InlineData(504, "模型服务响应超时")]
    [InlineData(429, "请求过于频繁或额度不足")]
    [InlineData(500, "模型服务暂时不可用（HTTP 500）")]
    [InlineData(503, "模型服务暂时不可用（HTTP 503）")]
    public void HTTP状态码映射为可行动文案(int statusCode, string expectedFragment) =>
        Assert.Contains(expectedFragment, ProviderHttpException.BuildMessage(statusCode), StringComparison.Ordinal);

    [Fact]
    public void HTTP状态码_未覆盖的4xx走通用文案()
    {
        string message = ProviderHttpException.BuildMessage(418);
        Assert.Equal("模型服务请求失败（HTTP 418）", message);
    }

    // ------------------------------------------------------------ 用户可见文案映射

    [Fact]
    public void 用户文案_空异常返回通用兜底()
    {
        Assert.Equal("模型服务请求失败", ProviderErrorMessages.UserFacing(null));
    }

    [Fact]
    public void 用户文案_取消与超时()
    {
        Assert.Equal("模型请求已取消", ProviderErrorMessages.UserFacing(new OperationCanceledException()));
        Assert.Equal("模型服务响应超时，请稍后重试", ProviderErrorMessages.UserFacing(new TimeoutException()));
    }

    [Fact]
    public void 用户文案_AppError优先透出()
    {
        AppError error = AppError.BadAuthRequest("渠道未配置");
        Assert.Equal("渠道未配置", ProviderErrorMessages.UserFacing(error));
    }

    [Fact]
    public void 用户文案_仅参数类状态码解析正文()
    {
        // 400/422 会尝试归类正文。
        Assert.Equal("模型服务额度不足，请检查渠道余额或配额",
            ProviderErrorMessages.UserFacing(new ProviderHttpException(400, "Bad Request", "insufficient balance", TimeSpan.Zero)));

        // 其他状态码不解析正文（正文可能含密钥或网关 HTML）。
        string message = ProviderErrorMessages.UserFacing(
            new ProviderHttpException(403, "Forbidden", "insufficient balance", TimeSpan.Zero));
        Assert.Contains("鉴权失败", message, StringComparison.Ordinal);
        Assert.DoesNotContain("额度不足", message, StringComparison.Ordinal);
    }

    [Fact]
    public void 用户文案_未知异常回落网络提示()
    {
        Assert.Equal("连接模型服务失败，请检查渠道地址和网络",
            ProviderErrorMessages.UserFacing(new InvalidOperationException("boom")));
    }

    // ------------------------------------------------------------ 正文归类

    [Fact]
    public void 正文归类_真人肖像类目优先于安全审核()
    {
        // 同时含 privacyinformation 与 safety 时，肖像类目优先（更具体、更可行动）。
        Assert.True(ProviderErrorMessages.PayloadErrorCategory(
            "error: privacyinformation detected, safety check", out string message));
        Assert.Contains("疑似包含真人形象", message, StringComparison.Ordinal);

        Assert.True(ProviderErrorMessages.PayloadErrorCategory(
            "SensitiveContentDetected", out string upperMessage));
        Assert.Contains("疑似包含真人形象", upperMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void 正文归类_安全审核()
    {
        foreach (string raw in new[] { "safety", "moderation", "content policy", "blocked" })
        {
            Assert.True(ProviderErrorMessages.PayloadErrorCategory(raw, out string message));
            Assert.Contains("安全审核", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 正文归类_额度不足()
    {
        foreach (string raw in new[] { "quota exceeded", "insufficient funds", "balance low", "billing issue" })
        {
            Assert.True(ProviderErrorMessages.PayloadErrorCategory(raw, out string message));
            Assert.Contains("额度不足", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 正文归类_模型不存在或无权()
    {
        Assert.True(ProviderErrorMessages.PayloadErrorCategory("model not found", out string message));
        Assert.Contains("模型不存在", message, StringComparison.Ordinal);

        Assert.True(ProviderErrorMessages.PayloadErrorCategory("model permission denied", out string accessMessage));
        Assert.Contains("模型不存在", accessMessage, StringComparison.Ordinal);

        // 仅有 "model" 而无 not found/permission/access 时不归类。
        Assert.False(ProviderErrorMessages.PayloadErrorCategory("model xyz", out _));
    }

    [Fact]
    public void 正文归类_思考模式不支持强制工具调用()
    {
        Assert.True(ProviderErrorMessages.PayloadErrorCategory(
            "Thinking mode does not support this tool_choice", out string thinkingMessage));
        Assert.Contains("思考/推理模式", thinkingMessage, StringComparison.Ordinal);

        Assert.True(ProviderErrorMessages.PayloadErrorCategory(
            "reasoning model: tool_choice unsupported", out string reasoningMessage));
        Assert.Contains("思考/推理模式", reasoningMessage, StringComparison.Ordinal);

        // 仅提 tool_choice 而无 thinking/reasoning 或 not support/unsupported 时不归类。
        Assert.False(ProviderErrorMessages.PayloadErrorCategory("tool_choice required", out _));
    }

    [Fact]
    public void 正文归类_通用参数错误()
    {
        foreach (string raw in new[] { "invalid request", "parameter out of range", "bad argument" })
        {
            Assert.True(ProviderErrorMessages.PayloadErrorCategory(raw, out string message));
            Assert.Contains("请检查模型和参数", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 正文归类_无法归类时返回false()
    {
        Assert.False(ProviderErrorMessages.PayloadErrorCategory("", out _));
        Assert.False(ProviderErrorMessages.PayloadErrorCategory(null, out _));
        Assert.False(ProviderErrorMessages.PayloadErrorCategory("   ", out _));
        Assert.False(ProviderErrorMessages.PayloadErrorCategory("some unknown upstream failure", out _));
    }

    [Fact]
    public void 正文归类_不因回显提示词而误判为真人形象()
    {
        // 正文回显用户提示词时，"likeness" 单独出现不足以判定真人形象类目 ——
        // 必须命中供应商的稳定错误码（privacyinformation / sensitivecontentdetected）。
        // 该正文含 "invalid"，按 Go 会归到通用参数类目，而**不是**肖像类目。
        Assert.True(ProviderErrorMessages.PayloadErrorCategory(
            "invalid request: prompt contains likeness of a person", out string message));
        Assert.Contains("请检查模型和参数", message, StringComparison.Ordinal);
        Assert.DoesNotContain("真人形象", message, StringComparison.Ordinal);
    }

    [Fact]
    public void 载荷错误兜底文案()
    {
        Assert.Equal("模型服务额度不足，请检查渠道余额或配额",
            ProviderErrorMessages.PayloadError("insufficient balance"));
        Assert.Equal("模型服务返回失败，请检查请求内容或渠道配置",
            ProviderErrorMessages.PayloadError("unknown"));
    }

    // ------------------------------------------------------------ 异常类型

    [Fact]
    public void 载荷异常保留原文但不外露()
    {
        ProviderPayloadException error = new("secret-key-in-body", "模型服务额度不足");
        Assert.Equal("secret-key-in-body", error.Raw);
        // 对外文案不含原文。
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 熔断异常固定文案() =>
        Assert.Equal("当前渠道连续失败，已暂时熔断，请稍后重试", new ProviderCircuitOpenException().Message);

    [Fact]
    public void 状态待定异常含任务ID且保留内因()
    {
        InvalidOperationException cause = new("inner");
        ProviderStatePendingException error = new("task-1", cause);

        Assert.Contains("task-1", error.Message, StringComparison.Ordinal);
        Assert.Same(cause, error.InnerException);
    }

    [Fact]
    public void 解码异常保留内因()
    {
        System.Text.Json.JsonException cause = new("bad json");
        ProviderResponseDecodeException error = new(cause);

        Assert.Same(cause, error.InnerException);
        Assert.Contains("bad json", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 工具函数

    [Fact]
    public void 媒体URL判定_仅前缀()
    {
        Assert.True(ProviderHelpers.IsPublicMediaURL("http://x/a.png"));
        Assert.True(ProviderHelpers.IsPublicMediaURL("HTTPS://X/A.PNG"));
        Assert.False(ProviderHelpers.IsPublicMediaURL("data:image/png;base64,AA"));
        Assert.False(ProviderHelpers.IsPublicMediaURL("ftp://x/a.png"));
        Assert.False(ProviderHelpers.IsPublicMediaURL(""));
    }

    [Fact]
    public void 图片输入URL_接受data图片与公网URL()
    {
        Assert.Equal("data:image/png;base64,AA",
            ProviderHelpers.OpenAIImageInputURL(new ProviderMedia { DataURL = "data:image/png;base64,AA" }));
        Assert.Equal("https://cdn.example.com/a.png",
            ProviderHelpers.OpenAIImageInputURL(new ProviderMedia { URL = "https://cdn.example.com/a.png" }));
    }

    [Fact]
    public void 图片输入URL_拒绝非图片data与相对地址()
    {
        InvalidOperationException mimeError = Assert.Throws<InvalidOperationException>(
            () => ProviderHelpers.OpenAIImageInputURL(new ProviderMedia { DataURL = "data:video/mp4;base64,AA" }));
        Assert.Contains("MIME 类型无效", mimeError.Message, StringComparison.Ordinal);

        InvalidOperationException urlError = Assert.Throws<InvalidOperationException>(
            () => ProviderHelpers.OpenAIImageInputURL(new ProviderMedia { URL = "/local/a.png" }));
        Assert.Contains("需要公网 URL 或 base64 data URL", urlError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 视频输入URL_接受data视频与公网URL()
    {
        Assert.Equal("data:video/mp4;base64,AA",
            ProviderHelpers.OpenAIVideoInputURL(new ProviderMedia { DataURL = "data:video/mp4;base64,AA" }));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderHelpers.OpenAIVideoInputURL(new ProviderMedia { DataURL = "data:image/png;base64,AA" }));
        Assert.Contains("MIME 类型无效", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void dataURL编码_空MIME归一且只取主类型()
    {
        Assert.Equal("data:application/octet-stream;base64,AQID", ProviderHelpers.DataUrl("", [1, 2, 3]));
        Assert.Equal("data:image/png;base64,AQID", ProviderHelpers.DataUrl("image/png", [1, 2, 3]));
        Assert.Equal("data:image/png;base64,AQID", ProviderHelpers.DataUrl("image/png; charset=binary", [1, 2, 3]));
    }

    [Fact]
    public void 系统提示词拼接()
    {
        Assert.Equal("user prompt", ProviderHelpers.WithSystemPrompt("", "user prompt"));
        Assert.Equal("user prompt", ProviderHelpers.WithSystemPrompt("   ", "user prompt"));
        Assert.Equal("sys\n\nuser", ProviderHelpers.WithSystemPrompt("  sys  ", "user"));
    }

    [Fact]
    public void 渠道模型键_剥离models前缀()
    {
        Assert.Equal("gpt-4", ProviderHelpers.ChannelModelKey("gpt-4", "other"));
        Assert.Equal("gpt-4", ProviderHelpers.ChannelModelKey("", "gpt-4"));
        Assert.Equal("gpt-4", ProviderHelpers.ChannelModelKey("models/gpt-4", null));
        Assert.Equal("gpt-4", ProviderHelpers.ChannelModelKey("  models/gpt-4  ", null));
        Assert.Equal("", ProviderHelpers.ChannelModelKey(null, null));
    }

    [Fact]
    public void 首非空_跳过空白()
    {
        Assert.Equal("b", ProviderHelpers.FirstNonEmpty(null, "  ", "b", "c"));
        Assert.Equal("", ProviderHelpers.FirstNonEmpty(null, "  "));
    }

    [Fact]
    public void 状态待定判定_取末段且跳过保留段()
    {
        // 末段即渠道 ID。
        Assert.Equal("chan-1",
            ProviderHelpers.SystemChannelIdFromBaseUrl("https://x.com/api/ai/system/chan-1"));
        Assert.Equal("chan-2",
            ProviderHelpers.SystemChannelIdFromBaseUrl("https://x.com/api/chan-2"));
    }

    [Fact]
    public void 状态待定判定_末段后仍有路径时不识别()
    {
        // 与 Go 一致：marker 之后若还有 "/"，说明取到的不是末段，跳过该 marker。
        // "/api/ai/system/chan-1/v1" -> 取到 "chan-1/v1" 含 "/" -> 跳过 -> 无结果。
        Assert.Equal("",
            ProviderHelpers.SystemChannelIdFromBaseUrl("https://x.com/api/ai/system/chan-1/v1"));
    }

    [Fact]
    public void 状态待定判定_保留段与无效输入返回空()
    {
        Assert.Equal("", ProviderHelpers.SystemChannelIdFromBaseUrl("https://x.com/api/v1"));
        Assert.Equal("", ProviderHelpers.SystemChannelIdFromBaseUrl("https://x.com/api/ai"));
        Assert.Equal("", ProviderHelpers.SystemChannelIdFromBaseUrl("https://x.com/v1beta"));
        Assert.Equal("", ProviderHelpers.SystemChannelIdFromBaseUrl(""));
        Assert.Equal("", ProviderHelpers.SystemChannelIdFromBaseUrl(null));
    }

    [Fact]
    public void 状态待定判定_剥离查询串()
    {
        Assert.Equal("chan-3",
            ProviderHelpers.SystemChannelIdFromBaseUrl("https://x.com/api/ai/system/chan-3?debug=1"));
    }

    [Fact]
    public void 字符串字段_类型错误返回空而非抛错()
    {
        Dictionary<string, object?> payload = JsonFields.ParseObject("""{"a":"x","b":123}""")!;

        Assert.Equal("x", ProviderHelpers.StringField(payload, "a"));
        // 与 JsonFields.OptionalString 不同：这里类型错误静默返回空串。
        Assert.Equal("", ProviderHelpers.StringField(payload, "b"));
        Assert.Equal("", ProviderHelpers.StringField(payload, "missing"));
    }

    [Fact]
    public void 元数据字符串_取值并去空白()
    {
        Dictionary<string, object?> metadata = JsonFields.ParseObject("""{"k":"  v  ","n":1}""")!;

        Assert.Equal("v", ProviderHelpers.MetadataString(metadata, "k"));
        Assert.Equal("", ProviderHelpers.MetadataString(metadata, "n"));
    }

    [Fact]
    public void Atoi容错_非法输入为零()
    {
        Assert.Equal(5, ProviderHelpers.AtoiOrZero("5"));
        Assert.Equal(5, ProviderHelpers.AtoiOrZero(" 5 "));
        Assert.Equal(0, ProviderHelpers.AtoiOrZero("abc"));
        Assert.Equal(0, ProviderHelpers.AtoiOrZero(""));
        Assert.Equal(0, ProviderHelpers.AtoiOrZero(null));
        Assert.Equal(0, ProviderHelpers.AtoiOrZero("1.5"));
    }

    [Fact]
    public void 资源使用对象存储判定()
    {
        Assert.False(ProviderHelpers.ResourceUsesObjectStorage(null));
        Assert.False(ProviderHelpers.ResourceUsesObjectStorage(new OpenAICanvas.Domain.Entities.Resource
        {
            Provider = "local",
        }));
        Assert.True(ProviderHelpers.ResourceUsesObjectStorage(new OpenAICanvas.Domain.Entities.Resource
        {
            Provider = "aliyun",
        }));
    }
}
