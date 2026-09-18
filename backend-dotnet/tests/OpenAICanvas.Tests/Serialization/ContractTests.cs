using System.Text.Json;
using OpenAICanvas.Web.Contracts;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Web.Serialization;
using Xunit;

namespace OpenAICanvas.Tests.Serialization;

/// <summary>
/// 契约红线 C1 / C2 / C3 的回归测试：信封字段、字段顺序、reason 出现条件。
/// 期望值都是 Go 版 gin.H + encoding/json 的真实输出。
/// </summary>
public class EnvelopeContractTests
{
    private static string Serialize(object value) => JsonSerializer.Serialize(value, CanvasJson.WriteOptions);

    [Fact]
    public void 成功响应_输出_code_data_msg_且顺序为字典序()
    {
        string json = Serialize(new ApiEnvelope
        {
            Code = 0,
            Data = new { id = "task-1" },
            Msg = "ok",
        });

        // Go 的 gin.H 是 map，encoding/json 按字典序输出键。
        Assert.Equal("{\"code\":0,\"data\":{\"id\":\"task-1\"},\"msg\":\"ok\"}", json);
    }

    [Fact]
    public void 失败响应_data_必须输出为_null_不能被省略()
    {
        string json = Serialize(new ApiEnvelope
        {
            Code = 404,
            Data = null,
            Msg = "任务不存在",
            Reason = "not_found",
        });

        Assert.Equal("{\"code\":404,\"data\":null,\"msg\":\"任务不存在\",\"reason\":\"not_found\"}", json);
    }

    [Fact]
    public void 失败响应_reason_为空时整个字段消失()
    {
        string json = Serialize(new ApiEnvelope
        {
            Code = 503,
            Data = new { status = "starting" },
            Msg = "服务仍在启动",
        });

        Assert.Equal("{\"code\":503,\"data\":{\"status\":\"starting\"},\"msg\":\"服务仍在启动\"}", json);
        Assert.DoesNotContain("reason", json);
    }
}

/// <summary>
/// Go omitempty 语义的回归测试（本次迁移的最高风险项）。
/// Go 的判定：false、0、nil、以及 len == 0 的数组/切片/map/字符串。
/// </summary>
public class GoOmitEmptyTests
{
    private sealed class Payload
    {
        [GoOmitEmpty]
        public string Text { get; init; } = string.Empty;

        [GoOmitEmpty]
        public int Count { get; init; }

        [GoOmitEmpty]
        public bool Flag { get; init; }

        [GoOmitEmpty]
        public string[] Items { get; init; } = [];

        [GoOmitEmpty]
        public string? Maybe { get; init; }

        // 没有标注的属性必须始终输出，即使为默认值。
        public string Always { get; init; } = string.Empty;
    }

    private static string Serialize(Payload payload) =>
        JsonSerializer.Serialize(payload, CanvasJson.WriteOptions);

    [Fact]
    public void 零值字段全部被省略()
    {
        string json = Serialize(new Payload());

        // 只剩未标注 omitempty 的 always 字段。
        Assert.Equal("{\"always\":\"\"}", json);
    }

    [Fact]
    public void 空集合与空字符串被省略_这一点_STJ_默认行为做不到()
    {
        string json = Serialize(new Payload { Text = "", Items = [], Maybe = null });

        Assert.DoesNotContain("\"text\"", json);
        Assert.DoesNotContain("\"items\"", json);
        Assert.DoesNotContain("\"maybe\"", json);
    }

    [Fact]
    public void 非零值字段全部保留()
    {
        string json = Serialize(new Payload
        {
            Text = "hello",
            Count = 3,
            Flag = true,
            Items = ["a"],
            Maybe = "x",
        });

        Assert.Equal(
            "{\"text\":\"hello\",\"count\":3,\"flag\":true,\"items\":[\"a\"],\"maybe\":\"x\",\"always\":\"\"}",
            json);
    }

    [Fact]
    public void 假值被省略_与_Go_的_false_零值一致()
    {
        string json = Serialize(new Payload { Flag = false, Count = 1 });

        Assert.DoesNotContain("\"flag\"", json);
        Assert.Contains("\"count\":1", json);
    }
}

/// <summary>
/// Go time.Time 的 RFC3339Nano 输出契约。
/// </summary>
public class GoTimeContractTests
{
    private static string Serialize(object value) => JsonSerializer.Serialize(value, CanvasJson.WriteOptions);

    private sealed record Holder(DateTimeOffset Value);

    [Fact]
    public void UTC_偏移输出_Z()
    {
        DateTimeOffset value = new(2026, 9, 16, 15, 22, 25, TimeSpan.Zero);
        Assert.Equal("{\"value\":\"2026-09-16T15:22:25Z\"}", Serialize(new Holder(value)));
    }

    [Fact]
    public void 非零偏移输出正负时分()
    {
        DateTimeOffset value = new(2026, 9, 16, 23, 22, 25, TimeSpan.FromHours(8));
        Assert.Equal("{\"value\":\"2026-09-16T23:22:25+08:00\"}", Serialize(new Holder(value)));
    }

    [Fact]
    public void 小数秒裁剪末尾零()
    {
        DateTimeOffset value = new DateTimeOffset(2026, 9, 16, 23, 22, 25, TimeSpan.FromHours(8))
            .AddTicks(5_000_000); // 0.5 秒
        Assert.Equal("{\"value\":\"2026-09-16T23:22:25.5+08:00\"}", Serialize(new Holder(value)));
    }

    [Fact]
    public void 七位小数秒完整保留()
    {
        DateTimeOffset value = new DateTimeOffset(2026, 9, 16, 23, 22, 25, TimeSpan.FromHours(8))
            .AddTicks(1_234_567);
        Assert.Equal("{\"value\":\"2026-09-16T23:22:25.1234567+08:00\"}", Serialize(new Holder(value)));
    }

    [Fact]
    public void 零值时间输出_Go_的零时间()
    {
        DateTimeOffset value = new(1, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("{\"value\":\"0001-01-01T00:00:00Z\"}", Serialize(new Holder(value)));
    }
}

/// <summary>
/// Go encoding/json 字符串转义契约。
/// </summary>
public class GoJsonEscapingTests
{
    private sealed record Holder(string Value);

    private static string Serialize(string value) =>
        JsonSerializer.Serialize(new Holder(value), CanvasJson.WriteOptions);

    [Fact]
    public void HTML_敏感字符转义且使用小写十六进制()
    {
        // Go 只转义 < > & 和引号反斜杠；注意是 \u003c 而不是 \u003C。
        Assert.Equal(
            "{\"value\":\"\\u003cb\\u003e\\u0026\\u003c/b\\u003e\"}",
            Serialize("<b>&</b>"));
    }

    [Fact]
    public void 加号与单引号不转义_与_STJ_默认行为不同()
    {
        Assert.Equal("{\"value\":\"a+b'c`d\"}", Serialize("a+b'c`d"));
    }

    [Fact]
    public void 中文原样输出_不做_unicode_转义()
    {
        Assert.Equal("{\"value\":\"影策工作台\"}", Serialize("影策工作台"));
    }

    [Fact]
    public void 控制字符与行分隔符被转义()
    {
        Assert.Equal("{\"value\":\"a\\nb\\u0001c\\u2028d\"}", Serialize("a\nb\u0001c\u2028d"));
    }

    [Fact]
    public void 反斜杠与引号被正确转义()
    {
        Assert.Equal("{\"value\":\"a\\\\b\\\"c\"}", Serialize("a\\b\"c"));
    }
}
