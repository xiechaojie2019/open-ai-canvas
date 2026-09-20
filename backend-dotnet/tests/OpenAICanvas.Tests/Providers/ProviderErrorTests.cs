#nullable enable
using OpenAICanvas.Outbound;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 供应商业务错误识别的契约测试。
/// 对应 Go: <c>internal/app/provider_error.go</c> 及其 <c>provider_error_test.go</c>。
/// </summary>
public sealed class ProviderErrorTests
{
    // ------------------------------------------------------------ FailureDetails

    [Fact]
    public void 错误详情_优先取内层error对象()
    {
        // 外层 code 只是 HTTP 包装码，内层才是供应商业务错误 —— 内层优先。
        string json = """
            {"code":"500","message":"http wrapper","error":{"code":"rate_limit_exceeded","message":"too many requests"}}
            """;
        (string code, string message) = ProviderError.FailureDetails(JsonFields.ParseObject(json));

        Assert.Equal("rate_limit_exceeded", code);
        Assert.Equal("too many requests", message);
    }

    [Fact]
    public void 错误详情_回落到data与顶层()
    {
        // error 缺失时取 data。
        (string dataCode, string dataMessage) = ProviderError.FailureDetails(
            JsonFields.ParseObject("""{"data":{"code":"invalid_api_key","message":"bad key"}}"""));
        Assert.Equal("invalid_api_key", dataCode);
        Assert.Equal("bad key", dataMessage);

        // error/data 都缺失时取顶层。
        (string topCode, string topMessage) = ProviderError.FailureDetails(
            JsonFields.ParseObject("""{"code":"1001","msg":"参数错误"}"""));
        Assert.Equal("1001", topCode);
        Assert.Equal("参数错误", topMessage);
    }

    [Fact]
    public void 错误详情_msg作为message的回落()
    {
        string json = """{"code":"1002","msg":"配额不足"}""";
        (string code, string message) = ProviderError.FailureDetails(JsonFields.ParseObject(json));

        Assert.Equal("1002", code);
        Assert.Equal("配额不足", message);
    }

    [Fact]
    public void 错误详情_缺少code或message时为空()
    {
        (string code, string message) = ProviderError.FailureDetails(JsonFields.ParseObject("""{"foo":"bar"}"""));
        Assert.Equal("", code);
        Assert.Equal("", message);

        // 空载荷与 null 载荷都不应抛错。
        Assert.Equal(("", ""), ProviderError.FailureDetails(JsonFields.ParseObject("{}")));
        Assert.Equal(("", ""), ProviderError.FailureDetails(null));
    }

    [Fact]
    public void 错误详情_消息按rune截断到500()
    {
        // 600 个多字节字符：截断必须按 rune 而非字节，否则会出现乱码。
        string longMessage = new string('错', 600);
        string json = $$"""{"code":"x","message":"{{longMessage}}"}""";
        (_, string message) = ProviderError.FailureDetails(JsonFields.ParseObject(json));

        // 截断时追加省略号：500 个 rune + "..."，与 Go 的 kernel.TruncateRunes 一致。
        Assert.Equal(503, message.EnumerateRunes().Count());
        Assert.EndsWith("...", message, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', message);

        // 恰好 500 个 rune 不截断、不加省略号。
        (_, string exact) = ProviderError.FailureDetails(JsonFields.ParseObject(
            $$"""{"code":"x","message":"{{new string('错', 500)}}"}"""));
        Assert.Equal(500, exact.EnumerateRunes().Count());
        Assert.DoesNotContain("...", exact, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 业务失败判定

    [Fact]
    public void 业务失败_error对象存在且有内容即失败()
    {
        (string code, string message, bool failed) =
            ProviderError.ResponseBusinessFailure("""{"error":{"message":"bad"}}""");
        Assert.True(failed);
        Assert.Equal("bad", message);

        // error 为空对象时不构成失败。
        (_, _, bool emptyError) = ProviderError.ResponseBusinessFailure("""{"error":{}}""");
        Assert.False(emptyError);
    }

    [Fact]
    public void 业务失败_顶层code非成功值即失败()
    {
        (string code, _, bool failed) = ProviderError.ResponseBusinessFailure("""{"code":"1001","message":"boom"}""");
        Assert.True(failed);
        Assert.Equal("1001", code);

        // 字符串 success 视为成功。
        (_, _, bool ok) = ProviderError.ResponseBusinessFailure("""{"code":"success","message":""}""");
        Assert.False(ok);

        // 数字 0 视为成功。
        (_, _, bool zero) = ProviderError.ResponseBusinessFailure("""{"code":0}""");
        Assert.False(zero);
    }

    [Theory]
    [InlineData("""{"code":"0"}""", false)]
    [InlineData("""{"code":""}""", false)]
    [InlineData("""{"code":"ok"}""", false)]
    [InlineData("""{"code":"OK"}""", false)]
    [InlineData("""{"code":"succeeded"}""", false)]
    [InlineData("""{"code":"SUCCESS"}""", false)]
    [InlineData("""{"code":"1"}""", true)]
    [InlineData("""{"code":"failed"}""", true)]
    public void 业务失败_成功码白名单(string json, bool expectedFailed)
    {
        (_, _, bool failed) = ProviderError.ResponseBusinessFailure(json);
        Assert.Equal(expectedFailed, failed);
    }

    [Fact]
    public void 业务失败_非法JSON与非对象载荷不视为失败()
    {
        (_, _, bool broken) = ProviderError.ResponseBusinessFailure("{not json");
        Assert.False(broken);

        (_, _, bool array) = ProviderError.ResponseBusinessFailure("[1,2,3]");
        Assert.False(array);

        (_, _, bool empty) = ProviderError.ResponseBusinessFailure("");
        Assert.False(empty);

        (_, _, bool nullBody) = ProviderError.ResponseBusinessFailure(null);
        Assert.False(nullBody);
    }

    // ------------------------------------------------------------ 错误码归一化

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("0", "")]          // "0" 归一为空
    [InlineData("1001", "1001")]
    public void 错误码归一化_字符串(string input, string expected) =>
        Assert.Equal(expected, ProviderError.NormalizeErrorCode(input));

    [Fact]
    public void 错误码归一化_数字零为空其余按g格式()
    {
        Assert.Equal("", ProviderError.NormalizeErrorCode(0d));
        Assert.Equal("", ProviderError.NormalizeErrorCode(0));
        Assert.Equal("1001", ProviderError.NormalizeErrorCode(1001d));
        Assert.Equal("1001", ProviderError.NormalizeErrorCode(1001));
        // 与 Go 的 %g 一致：大整数不使用科学计数法以外的意外格式。
        Assert.Equal("1.5", ProviderError.NormalizeErrorCode(1.5d));
    }

    [Fact]
    public void 错误码归一化_null与超长()
    {
        Assert.Equal("", ProviderError.NormalizeErrorCode(null));

        // 超过 80 个 rune 时按 rune 截断（截断时追加 "..."，与 kernel.TruncateRunes 一致）。
        string longCode = new('代', 120);
        string truncated = ProviderError.NormalizeErrorCode(longCode);
        Assert.Equal(83, truncated.EnumerateRunes().Count());
        Assert.EndsWith("...", truncated, StringComparison.Ordinal);

        // 边界：恰好 80 个 rune 不截断。
        string exact = new('代', 80);
        Assert.Equal(80, ProviderError.NormalizeErrorCode(exact).EnumerateRunes().Count());
    }

    // ------------------------------------------------------------ 内容审核

    [Fact]
    public void 内容审核判定_大小写不敏感()
    {
        Assert.True(ProviderError.IsContentModerationFailure("sensitive_words_detected"));
        Assert.True(ProviderError.IsContentModerationFailure("SENSITIVE_WORDS_DETECTED"));
        Assert.True(ProviderError.IsContentModerationFailure(
            "error: Sensitive_Words_Detected (req 123)"));
        Assert.False(ProviderError.IsContentModerationFailure("rate_limit"));
        Assert.False(ProviderError.IsContentModerationFailure(""));
    }

    [Fact]
    public void 内容审核常量与Go一致()
    {
        Assert.Equal("sensitive_words_detected", ProviderError.ContentModerationErrorCode);
        Assert.Contains("内容审核未通过", ProviderError.ContentModerationRetryMessage, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ JSON 字段工具

    [Fact]
    public void 可选字符串_缺失与null为空但类型不符报错()
    {
        Dictionary<string, object?> payload = JsonFields.ParseObject("""{"a":"text","b":null,"c":123}""")!;

        Assert.Equal("text", JsonFields.OptionalString(payload, "a"));
        Assert.Equal("", JsonFields.OptionalString(payload, "b"));
        Assert.Equal("", JsonFields.OptionalString(payload, "missing"));
        // 存在但类型不符：必须报错而不是静默转成 "123"。
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => JsonFields.OptionalString(payload, "c"));
        Assert.Contains("expected string", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 必填字符串_去空白判空()
    {
        Dictionary<string, object?> payload = JsonFields.ParseObject("""{"id":"  x  ","blank":"   "}""")!;

        Assert.Equal("x", JsonFields.RequireString(payload, "id"));
        Assert.Throws<InvalidOperationException>(() => JsonFields.RequireString(payload, "blank"));
        Assert.Throws<InvalidOperationException>(() => JsonFields.RequireString(payload, "missing"));
    }

    [Fact]
    public void 首字符串_跳过缺失并容忍类型错误()
    {
        Dictionary<string, object?> payload = JsonFields.ParseObject(
            """{"bad":123,"blank":"  ","good":"value"}""")!;

        // bad 类型不符 → 记下错误；blank 为空跳过；good 命中。
        Assert.Equal("value", JsonFields.FirstString(payload, "bad", "blank", "good"));

        // 只有类型错误时抛出该错误。
        Assert.Throws<InvalidOperationException>(() => JsonFields.FirstString(payload, "bad", "missing"));

        // 全部缺失时返回空串。
        Assert.Equal("", JsonFields.FirstString(payload, "missing1", "missing2"));
    }

    [Fact]
    public void 数字统一按float64承载_与Go一致()
    {
        Dictionary<string, object?> payload = JsonFields.ParseObject("""{"n":1001}""")!;

        Assert.IsType<double>(payload["n"]);
        Assert.Equal(1001d, payload["n"]);
    }

    [Fact]
    public void 解析对象_非法JSON或非对象返回null()
    {
        Assert.Null(JsonFields.ParseObject("{oops"));
        Assert.Null(JsonFields.ParseObject("[1,2]"));
        Assert.Null(JsonFields.ParseObject(""));
        Assert.NotNull(JsonFields.ParseObject("{}"));
    }

    [Fact]
    public void 嵌套对象_非对象值为null()
    {
        Dictionary<string, object?> payload = JsonFields.ParseObject(
            """{"obj":{"k":1},"str":"x","arr":[1]}""")!;

        Assert.NotNull(JsonFields.NestedObject(payload, "obj"));
        Assert.Null(JsonFields.NestedObject(payload, "str"));
        Assert.Null(JsonFields.NestedObject(payload, "arr"));
        Assert.Null(JsonFields.NestedObject(payload, "missing"));
    }
}
