#nullable enable
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 文本协议编排与请求体构造的契约测试。
/// 对应 Go: <c>internal/app/provider_text.go</c> 的
/// <c>applyTextThinking</c> / <c>normalizeAgentToolChoice</c> / <c>applyTextOutputLimit</c> /
/// <c>ensureChatCompletionStreamUsage</c> / <c>providerTextTaskResult</c> /
/// <c>shouldFallbackTextToChat</c> / <c>isAgentToolChoiceCompatibilityError</c> /
/// <c>validatedTextHistory</c> / <c>splitDataURL</c> / <c>textResponseInput</c> 等。
/// </summary>
public sealed class ProviderTextOrchestrationTests
{
    private static TextTaskInput Input(
        string prompt = "画一只猫",
        string? systemPrompt = null,
        int maxOutputTokens = 0) => new()
        {
            Prompt = prompt,
            Config = new ProviderConfig { Model = "test-model", SystemPrompt = systemPrompt ?? "" },
            MaxOutputTokens = maxOutputTokens,
        };

    // ------------------------------------------------------------ ResolveProtocol

    [Theory]
    [InlineData("chat-completion", "chat-completion")]
    [InlineData("openai-response", "responses")]
    [InlineData("claude-api", "claude-api")]
    [InlineData("openai-response ", "responses")]
    public void 协议归一_常见取值(string interfaceType, string expected) =>
        Assert.Equal(expected, ProviderTextOrchestration.ResolveProtocol(interfaceType));

    [Theory]
    [InlineData("openai-image")]
    [InlineData("gemini-veo")]
    [InlineData("")]
    [InlineData(null)]
    public void 协议归一_非文本协议为空(string? interfaceType) =>
        Assert.Equal("", ProviderTextOrchestration.ResolveProtocol(interfaceType));

    // ------------------------------------------------------------ applyTextThinking

    [Fact]
    public void 思考模式_关闭时不写入任何字段()
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal);
        ProviderTextOrchestration.ApplyTextThinking(
            body, new CanvasTextOptions { Thinking = false }, ProviderTextOrchestration.ResponsesProtocol);

        Assert.Empty(body);
    }

    [Fact]
    public void 思考模式_responses写入reasoning对象()
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal);
        ProviderTextOrchestration.ApplyTextThinking(
            body, new CanvasTextOptions { Thinking = true }, ProviderTextOrchestration.ResponsesProtocol);

        Dictionary<string, object?> reasoning = Assert.IsType<Dictionary<string, object?>>(body["reasoning"]);
        Assert.Equal("medium", reasoning["effort"]);
        Assert.Equal("auto", reasoning["summary"]);
    }

    [Fact]
    public void 思考模式_chat写入reasoning_effort()
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal);
        ProviderTextOrchestration.ApplyTextThinking(
            body, new CanvasTextOptions { Thinking = true }, ProviderTextOrchestration.ChatCompletionProtocol);

        Assert.Equal("medium", body["reasoning_effort"]);
    }

    [Fact]
    public void 思考模式_claude写入thinking预算()
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal);
        ProviderTextOrchestration.ApplyTextThinking(
            body, new CanvasTextOptions { Thinking = true }, ProviderTextOrchestration.ClaudeProtocol);

        Dictionary<string, object?> thinking = Assert.IsType<Dictionary<string, object?>>(body["thinking"]);
        Assert.Equal("enabled", thinking["type"]);
        Assert.Equal(1024, thinking["budget_tokens"]);
    }

    // ------------------------------------------------------------ normalizeAgentToolChoice

    [Fact]
    public void 工具选择_chat思考模式下移除tool_choice()
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal) { ["tool_choice"] = "required" };
        ProviderTextOrchestration.NormalizeAgentToolChoice(
            body, new CanvasTextOptions { Thinking = true }, ProviderTextOrchestration.ChatCompletionProtocol);

        Assert.False(body.ContainsKey("tool_choice"));
    }

    [Fact]
    public void 工具选择_chat显式auto时移除()
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal) { ["tool_choice"] = "auto" };
        ProviderTextOrchestration.NormalizeAgentToolChoice(
            body, new CanvasTextOptions(), ProviderTextOrchestration.ChatCompletionProtocol);

        Assert.False(body.ContainsKey("tool_choice"));
    }

    [Fact]
    public void 工具选择_chat显式required且非思考时保留()
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal) { ["tool_choice"] = "required" };
        ProviderTextOrchestration.NormalizeAgentToolChoice(
            body, new CanvasTextOptions { Thinking = false }, ProviderTextOrchestration.ChatCompletionProtocol);

        Assert.Equal("required", body["tool_choice"]);
    }

    [Fact]
    public void 工具选择_responses协议不处理()
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal) { ["tool_choice"] = "auto" };
        ProviderTextOrchestration.NormalizeAgentToolChoice(
            body, new CanvasTextOptions(), ProviderTextOrchestration.ResponsesProtocol);

        Assert.Equal("auto", body["tool_choice"]);
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("AUTO", true)]
    [InlineData(" auto ", true)]
    [InlineData("required", false)]
    [InlineData(1, false)]
    [InlineData(null, false)]
    public void 工具选择_auto判定(object? value, bool expected) =>
        Assert.Equal(expected, ProviderTextOrchestration.IsAutoAgentToolChoice(value));

    // ------------------------------------------------------------ applyTextOutputLimit

    [Fact]
    public void 输出上限_正数写入()
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal);
        ProviderTextOrchestration.ApplyTextOutputLimit(body, 100, "max_tokens");

        Assert.Equal(100, body["max_tokens"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 输出上限_非正数不写入(int limit)
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal);
        ProviderTextOrchestration.ApplyTextOutputLimit(body, limit, "max_tokens");

        Assert.False(body.ContainsKey("max_tokens"));
    }

    // ------------------------------------------------------------ ensureChatCompletionStreamUsage

    [Fact]
    public void 流式用量_新建stream_options()
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal);
        ProviderTextOrchestration.EnsureChatCompletionStreamUsage(body);

        Dictionary<string, object?> options = Assert.IsType<Dictionary<string, object?>>(body["stream_options"]);
        Assert.Equal(true, options["include_usage"]);
    }

    [Fact]
    public void 流式用量_保留既有选项()
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["stream_options"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["foo"] = "bar" },
        };
        ProviderTextOrchestration.EnsureChatCompletionStreamUsage(body);

        Dictionary<string, object?> options = Assert.IsType<Dictionary<string, object?>>(body["stream_options"]);
        Assert.Equal("bar", options["foo"]);
        Assert.Equal(true, options["include_usage"]);
    }

    [Fact]
    public void 流式用量_类型错误时抛错()
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal) { ["stream_options"] = "nope" };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderTextOrchestration.EnsureChatCompletionStreamUsage(body));
        Assert.Equal("stream_options 必须是 JSON 对象", error.Message);
    }

    // ------------------------------------------------------------ result shaping

    [Fact]
    public void 结果整形_无推理摘要时省略字段()
    {
        Dictionary<string, object?> payload = ProviderTextOrchestration.TextTaskResult(new ProviderTextResult("正文", ""));

        Assert.Equal("text", payload["mode"]);
        Assert.Equal("正文", payload["text"]);
        Assert.False(payload.ContainsKey("reasoning"));
    }

    [Fact]
    public void 结果整形_纯空白推理摘要也省略()
    {
        Dictionary<string, object?> payload = ProviderTextOrchestration.TextTaskResult(new ProviderTextResult("正文", "   "));

        Assert.False(payload.ContainsKey("reasoning"));
    }

    [Fact]
    public void 结果整形_有推理摘要时输出()
    {
        Dictionary<string, object?> payload = ProviderTextOrchestration.TextTaskResult(new ProviderTextResult("正文", "想了一下"));

        Assert.Equal("想了一下", payload["reasoning"]);
    }

    // ------------------------------------------------------------ RequireText

    [Fact]
    public void 正文校验_非流式空正文报错()
    {
        Dictionary<string, object?> parsed = new(StringComparer.Ordinal) { ["text"] = "" };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderTextOrchestration.RequireText(parsed, streaming: false));
        Assert.Equal("文本接口没有返回内容", error.Message);
    }

    [Fact]
    public void 正文校验_流式空正文报错文案不同()
    {
        Dictionary<string, object?> parsed = new(StringComparer.Ordinal) { ["text"] = "" };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderTextOrchestration.RequireText(parsed, streaming: true));
        Assert.Equal("流式文本接口没有返回内容", error.Message);
    }

    [Fact]
    public void 正文校验_有正文时返回结果()
    {
        Dictionary<string, object?> parsed = new(StringComparer.Ordinal)
        {
            ["text"] = "答案",
            ["reasoning"] = "推演",
        };
        ProviderTextResult result = ProviderTextOrchestration.RequireText(parsed, streaming: false);

        Assert.Equal("答案", result.Text);
        Assert.Equal("推演", result.Reasoning);
    }

    // ------------------------------------------------------------ shouldFallbackTextToChat

    [Theory]
    [InlineData(404, true)]
    [InlineData(405, true)]
    [InlineData(501, true)]
    [InlineData(502, false)]
    [InlineData(503, false)]
    [InlineData(504, false)]
    [InlineData(400, false)]
    public void Responses回落_仅路径不存在类状态码(int statusCode, bool expected)
    {
        // 502/503/504 是瞬时故障，换协议修不好，还会掩盖真实上游故障。
        Exception error = new ProviderHttpException(statusCode, $"{statusCode} X", "", TimeSpan.Zero);
        Assert.Equal(expected, ProviderTextOrchestration.ShouldFallbackTextToChat(error));
    }

    [Fact]
    public void Responses回落_非HTTP错误不回落() =>
        Assert.False(ProviderTextOrchestration.ShouldFallbackTextToChat(new InvalidOperationException("boom")));

    [Fact]
    public void Responses回落_空错误不回落() =>
        Assert.False(ProviderTextOrchestration.ShouldFallbackTextToChat(null));

    // ------------------------------------------------------------ isAgentToolChoiceCompatibilityError

    [Fact]
    public void 工具选择兼容性_消息命中下划线写法() =>
        Assert.True(ProviderTextOrchestration.IsAgentToolChoiceCompatibilityError(
            new InvalidOperationException("Thinking mode does not support this tool_choice")));

    [Fact]
    public void 工具选择兼容性_消息命中空格写法() =>
        Assert.True(ProviderTextOrchestration.IsAgentToolChoiceCompatibilityError(
            new InvalidOperationException("unsupported tool choice")));

    [Fact]
    public void 工具选择兼容性_正文命中连字符写法()
    {
        ProviderPayloadException error = new("bad tool-choice value", "归类文案");
        Assert.True(ProviderTextOrchestration.IsAgentToolChoiceCompatibilityError(error));
    }

    [Fact]
    public void 工具选择兼容性_HTTP正文命中thinking_mode()
    {
        ProviderHttpException error = new(400, "400 Bad Request", "THINKING MODE conflict", TimeSpan.Zero);
        Assert.True(ProviderTextOrchestration.IsAgentToolChoiceCompatibilityError(error));
    }

    [Fact]
    public void 工具选择兼容性_无关错误返回false() =>
        Assert.False(ProviderTextOrchestration.IsAgentToolChoiceCompatibilityError(
            new InvalidOperationException("模型服务额度不足")));

    [Fact]
    public void 工具选择兼容性_空错误返回false() =>
        Assert.False(ProviderTextOrchestration.IsAgentToolChoiceCompatibilityError(null));

    // ------------------------------------------------------------ validatedTextHistory

    [Fact]
    public void 历史过滤_角色与内容都归一()
    {
        List<Dictionary<string, object?>> result = ProviderTextOrchestration.ValidatedTextHistory(
        [
            new ProviderTextMessage { Role = " User ", Content = " 你好 " },
            new ProviderTextMessage { Role = "ASSISTANT", Content = "在的" },
        ]);

        Assert.Equal(2, result.Count);
        Assert.Equal("user", result[0]["role"]);
        Assert.Equal("你好", result[0]["content"]);
        Assert.Equal("assistant", result[1]["role"]);
    }

    [Fact]
    public void 历史过滤_非法角色被丢弃()
    {
        List<Dictionary<string, object?>> result = ProviderTextOrchestration.ValidatedTextHistory(
        [
            new ProviderTextMessage { Role = "system", Content = "系统" },
            new ProviderTextMessage { Role = "tool", Content = "结果" },
            new ProviderTextMessage { Role = "user", Content = "有效" },
        ]);

        Assert.Single(result);
        Assert.Equal("有效", result[0]["content"]);
    }

    [Fact]
    public void 历史过滤_空白内容被丢弃()
    {
        List<Dictionary<string, object?>> result = ProviderTextOrchestration.ValidatedTextHistory(
        [
            new ProviderTextMessage { Role = "user", Content = "   " },
            new ProviderTextMessage { Role = "user", Content = "有效" },
        ]);

        Assert.Single(result);
    }

    [Fact]
    public void 历史过滤_空输入返回空列表() =>
        Assert.Empty(ProviderTextOrchestration.ValidatedTextHistory(null));

    // ------------------------------------------------------------ BodyObject

    [Fact]
    public void 请求体对象_字典直接透传()
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal) { ["a"] = 1 };
        Assert.Same(body, ProviderTextOrchestration.BodyObject(body));
    }

    [Fact]
    public void 请求体对象_非对象返回null()
    {
        Assert.Null(ProviderTextOrchestration.BodyObject("字符串"));
        Assert.Null(ProviderTextOrchestration.BodyObject(null));
        Assert.Null(ProviderTextOrchestration.BodyObject(new List<object?>()));
    }
}
