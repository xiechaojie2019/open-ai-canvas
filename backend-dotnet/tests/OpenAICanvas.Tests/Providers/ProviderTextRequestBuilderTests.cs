#nullable enable
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 文本协议请求体构造的契约测试。
/// 对应 Go: <c>internal/app/provider_text.go</c> 的
/// <c>textResponseInput</c> / <c>textResponseContent</c> / <c>textChatContent</c> /
/// <c>claudeTextContent</c> / <c>splitDataURL</c> 与
/// <c>runResponsesTextTask</c> / <c>runChatCompletionsTextTask</c> / <c>runClaudeTextTask</c>。
/// </summary>
public sealed class ProviderTextRequestBuilderTests
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

    // ------------------------------------------------------------ splitDataURL

    [Fact]
    public void 拆分dataURL_合法base64()
    {
        (string mimeType, string data, bool ok) = ProviderTextRequestBuilder.SplitDataUrl("data:image/png;base64,AAAA");

        Assert.True(ok);
        Assert.Equal("image/png", mimeType);
        Assert.Equal("AAAA", data);
    }

    [Fact]
    public void 拆分dataURL_非data前缀失败()
    {
        (_, _, bool ok) = ProviderTextRequestBuilder.SplitDataUrl("https://example.com/a.png");
        Assert.False(ok);
    }

    [Fact]
    public void 拆分dataURL_缺少逗号失败()
    {
        (_, _, bool ok) = ProviderTextRequestBuilder.SplitDataUrl("data:image/png;base64");
        Assert.False(ok);
    }

    [Fact]
    public void 拆分dataURL_无MIME的形态失败()
    {
        // 分隔符前只有 "data:"（长度不大于前缀），必须失败。
        (_, _, bool ok) = ProviderTextRequestBuilder.SplitDataUrl("data:,payload");
        Assert.False(ok);
    }

    [Fact]
    public void 拆分dataURL_缺少base64后缀失败()
    {
        (_, _, bool ok) = ProviderTextRequestBuilder.SplitDataUrl("data:image/png,AAAA");
        Assert.False(ok);
    }

    [Fact]
    public void 拆分dataURL_空载荷失败()
    {
        (_, _, bool ok) = ProviderTextRequestBuilder.SplitDataUrl("data:image/png;base64,");
        Assert.False(ok);
    }

    // ------------------------------------------------------------ textResponseInput

    [Fact]
    public void Responses输入_无历史无素材时是纯文本()
    {
        object result = ProviderTextRequestBuilder.TextResponseInput(Input("问题", "你是助手"));

        // 与 Go 一致：退化为系统提示词 + 提示词拼接的字符串，而非消息数组。
        string text = Assert.IsType<string>(result);
        Assert.Equal("你是助手\n\n问题", text);
    }

    [Fact]
    public void Responses输入_有历史时是消息数组()
    {
        TextTaskInput input = Input("问题", "你是助手");
        input.TextHistory.Add(new ProviderTextMessage { Role = "user", Content = "上一轮" });

        List<Dictionary<string, object?>> messages =
            Assert.IsType<List<Dictionary<string, object?>>>(ProviderTextRequestBuilder.TextResponseInput(input));

        Assert.Equal(3, messages.Count);
        Assert.Equal("system", messages[0]["role"]);
        Assert.Equal("你是助手", messages[0]["content"]);
        Assert.Equal("上一轮", messages[1]["content"]);
        Assert.Equal("user", messages[2]["role"]);
    }

    [Fact]
    public void Responses输入_有素材但无系统提示词时不插入system()
    {
        TextTaskInput input = Input("问题");
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://example.com/a.png" });

        List<Dictionary<string, object?>> messages =
            Assert.IsType<List<Dictionary<string, object?>>>(ProviderTextRequestBuilder.TextResponseInput(input));

        // 系统提示词为空时不插入 system 消息，只留下 user 一条。
        Assert.Single(messages);
        Assert.Equal("user", messages[0]["role"]);
    }

    // ------------------------------------------------------------ textResponseContent

    [Fact]
    public void Responses内容_文本加图片加视频()
    {
        TextTaskInput input = Input("提示");
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://example.com/a.png" });
        input.ReferenceVideos.Add(new ProviderMedia { URL = "https://example.com/v.mp4" });

        List<Dictionary<string, object?>> content = ProviderTextRequestBuilder.TextResponseContent(input);

        Assert.Equal(3, content.Count);
        Assert.Equal("input_text", content[0]["type"]);
        Assert.Equal("input_image", content[1]["type"]);
        Assert.Equal("https://example.com/a.png", content[1]["image_url"]);
        Assert.Equal("input_video", content[2]["type"]);
        Assert.Equal("https://example.com/v.mp4", content[2]["video_url"]);
    }

    // ------------------------------------------------------------ textChatContent

    [Fact]
    public void Chat内容_无素材时是纯字符串()
    {
        object result = ProviderTextRequestBuilder.TextChatContent(Input("提示"));

        Assert.Equal("提示", Assert.IsType<string>(result));
    }

    [Fact]
    public void Chat内容_有素材时是段落数组()
    {
        TextTaskInput input = Input("提示");
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://example.com/a.png" });
        input.ReferenceVideos.Add(new ProviderMedia { URL = "https://example.com/v.mp4" });

        List<Dictionary<string, object?>> content =
            Assert.IsType<List<Dictionary<string, object?>>>(ProviderTextRequestBuilder.TextChatContent(input));

        Assert.Equal(3, content.Count);
        Assert.Equal("text", content[0]["type"]);
        Dictionary<string, object?> imageUrl = Assert.IsType<Dictionary<string, object?>>(content[1]["image_url"]);
        Assert.Equal("https://example.com/a.png", imageUrl["url"]);
        Dictionary<string, object?> videoUrl = Assert.IsType<Dictionary<string, object?>>(content[2]["video_url"]);
        Assert.Equal("https://example.com/v.mp4", videoUrl["url"]);
    }

    [Fact]
    public void Chat内容_参考图不是公网URL时抛错()
    {
        TextTaskInput input = Input("提示");
        input.ReferenceImages.Add(new ProviderMedia { URL = "ftp://example.com/a.png" });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderTextRequestBuilder.TextChatContent(input));
        Assert.Contains("公网 URL", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ claudeTextContent

    [Fact]
    public void Claude内容_无参考图时是纯字符串()
    {
        object result = ProviderTextRequestBuilder.ClaudeTextContent(Input("提示"));

        Assert.Equal("提示", Assert.IsType<string>(result));
    }

    [Fact]
    public void Claude内容_dataURL走base64源()
    {
        TextTaskInput input = Input("提示");
        input.ReferenceImages.Add(new ProviderMedia { DataURL = "data:image/png;base64,QUJD" });

        List<Dictionary<string, object?>> content =
            Assert.IsType<List<Dictionary<string, object?>>>(ProviderTextRequestBuilder.ClaudeTextContent(input));

        Assert.Equal(2, content.Count);
        Assert.Equal("image", content[1]["type"]);
        Dictionary<string, object?> source = Assert.IsType<Dictionary<string, object?>>(content[1]["source"]);
        Assert.Equal("base64", source["type"]);
        Assert.Equal("image/png", source["media_type"]);
        Assert.Equal("QUJD", source["data"]);
    }

    [Fact]
    public void Claude内容_公网URL走url源()
    {
        TextTaskInput input = Input("提示");
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://example.com/a.png" });

        List<Dictionary<string, object?>> content =
            Assert.IsType<List<Dictionary<string, object?>>>(ProviderTextRequestBuilder.ClaudeTextContent(input));

        Dictionary<string, object?> source = Assert.IsType<Dictionary<string, object?>>(content[1]["source"]);
        Assert.Equal("url", source["type"]);
        Assert.Equal("https://example.com/a.png", source["url"]);
    }

    // ------------------------------------------------------------ ResponsesBody

    [Fact]
    public void Responses请求体_基础形态()
    {
        Dictionary<string, object?> body = ProviderTextRequestBuilder.ResponsesBody(Input("问题"));

        Assert.Equal("test-model", body["model"]);
        Assert.Equal("问题", body["input"]);
        Assert.False(body.ContainsKey("stream"));
        Assert.False(body.ContainsKey("max_output_tokens"));
    }

    [Fact]
    public void Responses请求体_写入输出上限与思考模式()
    {
        TextTaskInput input = Input("问题", maxOutputTokens: 512);
        input.TextOptions.Thinking = true;

        Dictionary<string, object?> body = ProviderTextRequestBuilder.ResponsesBody(input);

        Assert.Equal(512, body["max_output_tokens"]);
        Assert.True(body.ContainsKey("reasoning"));
    }

    // ------------------------------------------------------------ ChatCompletionsBody

    [Fact]
    public void Chat请求体_系统提示词与历史与用户消息()
    {
        TextTaskInput input = Input("问题", "你是助手");
        input.TextHistory.Add(new ProviderTextMessage { Role = "assistant", Content = "上一轮" });

        Dictionary<string, object?> body = ProviderTextRequestBuilder.ChatCompletionsBody(input);

        List<Dictionary<string, object?>> messages =
            Assert.IsType<List<Dictionary<string, object?>>>(body["messages"]);
        Assert.Equal(3, messages.Count);
        Assert.Equal("system", messages[0]["role"]);
        Assert.Equal("assistant", messages[1]["role"]);
        Assert.Equal("user", messages[2]["role"]);
        Assert.Equal("问题", messages[2]["content"]);
    }

    [Fact]
    public void Chat请求体_无系统提示词时不插入system()
    {
        Dictionary<string, object?> body = ProviderTextRequestBuilder.ChatCompletionsBody(Input("问题"));

        List<Dictionary<string, object?>> messages =
            Assert.IsType<List<Dictionary<string, object?>>>(body["messages"]);
        Assert.Single(messages);
        Assert.Equal("user", messages[0]["role"]);
    }

    [Fact]
    public void Chat请求体_上限写入max_tokens()
    {
        TextTaskInput input = Input("问题", maxOutputTokens: 256);

        Dictionary<string, object?> body = ProviderTextRequestBuilder.ChatCompletionsBody(input);

        Assert.Equal(256, body["max_tokens"]);
    }

    // ------------------------------------------------------------ ClaudeBody

    [Fact]
    public void Claude请求体_默认max_tokens为4096()
    {
        Dictionary<string, object?> body = ProviderTextRequestBuilder.ClaudeBody(Input("问题"));

        Assert.Equal(4096, body["max_tokens"]);
    }

    [Fact]
    public void Claude请求体_显式上限覆盖默认值()
    {
        Dictionary<string, object?> body = ProviderTextRequestBuilder.ClaudeBody(Input("问题", maxOutputTokens: 100));

        Assert.Equal(100, body["max_tokens"]);
    }

    [Fact]
    public void Claude请求体_系统提示词作为顶层字段()
    {
        Dictionary<string, object?> body = ProviderTextRequestBuilder.ClaudeBody(Input("问题", "你是助手"));

        Assert.Equal("你是助手", body["system"]);
        List<Dictionary<string, object?>> messages =
            Assert.IsType<List<Dictionary<string, object?>>>(body["messages"]);
        Assert.Single(messages);
        Assert.Equal("user", messages[0]["role"]);
    }

    [Fact]
    public void Claude请求体_视频参考直接报错()
    {
        TextTaskInput input = Input("问题");
        input.ReferenceVideos.Add(new ProviderMedia { URL = "https://example.com/v.mp4" });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderTextRequestBuilder.ClaudeBody(input));
        Assert.Equal("Claude API 当前不支持视频参考输入", error.Message);
    }

    // ------------------------------------------------------------ BuildBody 分发

    [Theory]
    [InlineData("responses", "max_output_tokens")]
    [InlineData("chat-completion", "max_tokens")]
    [InlineData("claude-api", "max_tokens")]
    public void 请求体分发_按协议选择上限字段(string protocol, string limitField)
    {
        TextTaskInput input = Input("问题", maxOutputTokens: 42);

        Dictionary<string, object?> body = ProviderTextRequestBuilder.BuildBody(input, protocol);

        Assert.Equal(42, body[limitField]);
    }

    [Fact]
    public void 请求体分发_claude未识别协议时回落chat()
    {
        Dictionary<string, object?> body = ProviderTextRequestBuilder.BuildBody(Input("问题"), "unknown");

        Assert.True(body.ContainsKey("messages"));
        Assert.Equal("test-model", body["model"]);
    }

    // ------------------------------------------------------------ 序列化

    [Fact]
    public void 序列化_中文不被转义()
    {
        Dictionary<string, object?> body = ProviderTextRequestBuilder.ChatCompletionsBody(Input("画一只猫"));

        string json = ProviderTextRequestBuilder.SerializeBody(body);

        Assert.Contains("画一只猫", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
    }
}
