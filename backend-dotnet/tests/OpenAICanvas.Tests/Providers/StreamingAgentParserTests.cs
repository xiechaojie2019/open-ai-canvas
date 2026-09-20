#nullable enable
using System.Text;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 三种 wire 协议的 SSE 流式解析契约测试。
/// 对应 Go: <c>internal/app/provider_text.go</c> 的
/// <c>streamingAgentParser</c> / <c>parseAgentToolPayload</c> /
/// <c>extractResponseText</c> / <c>extractResponseReasoning</c>。
/// </summary>
public sealed class StreamingAgentParserTests
{
    private const string EventStream = "text/event-stream";

    private static void Feed(StreamingAgentParser parser, params string[] chunks)
    {
        foreach (string chunk in chunks)
        {
            parser.Consume(EventStream, Encoding.UTF8.GetBytes(chunk));
        }
        parser.Flush();
    }

    private static string Frame(string data) => "data: " + data + "\n\n";

    // ------------------------------------------------------------ chat-completion

    [Fact]
    public void 聊天流式_拼接文本增量()
    {
        StreamingAgentParser parser = new("chat-completion");
        Feed(parser,
            Frame("""{"choices":[{"delta":{"content":"你"}}]}"""),
            Frame("""{"choices":[{"delta":{"content":"好"}}]}"""),
            Frame("[DONE]"));

        Dictionary<string, object?> result = parser.Result();
        Assert.Equal("text", result["mode"]);
        Assert.Equal("你好", result["text"]);
        Assert.Empty((List<object?>)result["toolCalls"]!);
    }

    [Fact]
    public void 聊天流式_推理增量单独累计()
    {
        StreamingAgentParser parser = new("chat-completion");
        List<string> reasoningDeltas = [];
        parser.EmitReasoning = reasoningDeltas.Add;

        Feed(parser,
            Frame("""{"choices":[{"delta":{"reasoning_content":"思"}}]}"""),
            Frame("""{"choices":[{"delta":{"reasoning_content":"考"}}]}"""),
            Frame("""{"choices":[{"delta":{"content":"答"}}]}"""));

        Dictionary<string, object?> result = parser.Result();
        Assert.Equal("思考", result["reasoning"]);
        Assert.Equal("答", result["text"]);
        Assert.Equal(["思", "考"], reasoningDeltas);
    }

    [Fact]
    public void 聊天流式_文本增量回调实时触发()
    {
        List<string> deltas = [];
        StreamingAgentParser parser = new("chat-completion", deltas.Add);
        parser.Consume(EventStream, Encoding.UTF8.GetBytes(
            "data: " + """{"choices":[{"delta":{"content":"A"}}]}""" + "\n\n"));

        parser.Flush();
        Assert.Equal(["A"], deltas);
    }

    [Fact]
    public void 聊天流式_段落数组内容被拼接()
    {
        StreamingAgentParser parser = new("chat-completion");
        Feed(parser, Frame("""{"choices":[{"delta":{"content":[{"text":"a"},{"text":"b"}]}}]}"""));

        Assert.Equal("ab", parser.Result()["text"]);
    }

    [Fact]
    public void 聊天流式_工具调用按index累积参数()
    {
        StreamingAgentParser parser = new("chat-completion");
        Feed(parser,
            Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"get_","arguments":"{\"a\""}}]}}]}"""),
            Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"weather","arguments":":1}"}}]}}]}"""));

        List<object?> calls = (List<object?>)parser.Result()["toolCalls"]!;
        Assert.Single(calls);
        Dictionary<string, object?> call = (Dictionary<string, object?>)calls[0]!;
        Assert.Equal("call_1", call["id"]);
        Dictionary<string, object?> function = (Dictionary<string, object?>)call["function"]!;
        Assert.Equal("get_weather", function["name"]);
        Assert.Equal("""{"a":1}""", function["arguments"]);
    }

    [Fact]
    public void 聊天流式_缺id或name的工具调用被跳过()
    {
        StreamingAgentParser parser = new("chat-completion");
        // 只有 name 没有 id。
        Feed(parser, Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"x","arguments":"{}"}}]}}]}"""));

        Assert.Empty((List<object?>)parser.Result()["toolCalls"]!);
    }

    [Fact]
    public void 聊天流式_空参数补为默认对象()
    {
        StreamingAgentParser parser = new("chat-completion");
        Feed(parser, Frame(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"f"}}]}}]}"""));

        List<object?> calls = (List<object?>)parser.Result()["toolCalls"]!;
        Dictionary<string, object?> function =
            (Dictionary<string, object?>)((Dictionary<string, object?>)calls[0]!)["function"]!;
        Assert.Equal("{}", function["arguments"]);
    }

    // ------------------------------------------------------------ responses

    [Fact]
    public void Responses流式_文本与推理增量()
    {
        StreamingAgentParser parser = new("responses");
        Feed(parser,
            "event: response.output_text.delta\ndata: " + """{"delta":"hi"}""" + "\n\n",
            "event: response.reasoning.delta\ndata: " + """{"delta":"why"}""" + "\n\n");

        Dictionary<string, object?> result = parser.Result();
        Assert.Equal("hi", result["text"]);
        Assert.Equal("why", result["reasoning"]);
    }

    [Fact]
    public void Responses流式_事件名缺失时用payload类型()
    {
        StreamingAgentParser parser = new("responses");
        Feed(parser, Frame("""{"type":"response.output_text.delta","delta":"x"}"""));

        Assert.Equal("x", parser.Result()["text"]);
    }

    [Fact]
    public void Responses流式_completed事件以最终响应为准()
    {
        StreamingAgentParser parser = new("responses");
        Feed(parser,
            "event: response.output_text.delta\ndata: " + """{"delta":"partial"}""" + "\n\n",
            "event: response.completed\ndata: " + """{"response":{"output_text":"final","output":[]}}""" + "\n\n");

        Dictionary<string, object?> result = parser.Result();
        // 流式文本覆盖 completed 解析出的文本（与 Go 一致）。
        Assert.Equal("partial", result["text"]);
    }

    [Fact]
    public void Responses流式_函数调用参数分片累积()
    {
        StreamingAgentParser parser = new("responses");
        Feed(parser,
            "event: response.output_item.added\ndata: " +
            """{"output_index":0,"item":{"type":"function_call","call_id":"c1","name":"f","arguments":""}}""" + "\n\n",
            "event: response.function_call_arguments.delta\ndata: " + """{"output_index":0,"delta":"{\"k\""}""" + "\n\n",
            "event: response.function_call_arguments.delta\ndata: " + """{"output_index":0,"delta":":1}"}""" + "\n\n");

        List<object?> calls = (List<object?>)parser.Result()["toolCalls"]!;
        Dictionary<string, object?> function =
            (Dictionary<string, object?>)((Dictionary<string, object?>)calls[0]!)["function"]!;
        Assert.Equal("""{"k":1}""", function["arguments"]);
    }

    [Fact]
    public void Responses流式_argumentsdone覆盖累积值()
    {
        StreamingAgentParser parser = new("responses");
        Feed(parser,
            "event: response.output_item.added\ndata: " +
            """{"output_index":0,"item":{"type":"function_call","call_id":"c1","name":"f","arguments":""}}""" + "\n\n",
            "event: response.function_call_arguments.delta\ndata: " + """{"output_index":0,"delta":"{\"a\""}""" + "\n\n",
            "event: response.function_call_arguments.done\ndata: " + """{"output_index":0,"arguments":"{\"a\":2}"}""" + "\n\n");

        Dictionary<string, object?> function =
            (Dictionary<string, object?>)((Dictionary<string, object?>)
                ((List<object?>)parser.Result()["toolCalls"]!)[0]!)["function"]!;
        Assert.Equal("""{"a":2}""", function["arguments"]);
    }

    // ------------------------------------------------------------ claude

    [Fact]
    public void Claude流式_文本块与增量()
    {
        StreamingAgentParser parser = new("claude-api");
        Feed(parser,
            Frame("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":"A"}}"""),
            Frame("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"B"}}"""));

        Assert.Equal("AB", parser.Result()["text"]);
    }

    [Fact]
    public void Claude流式_思考块()
    {
        StreamingAgentParser parser = new("claude-api");
        Feed(parser,
            Frame("""{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"hmm"}}"""),
            Frame("""{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"more"}}"""));

        Assert.Equal("hmmmore", parser.Result()["reasoning"]);
    }

    [Fact]
    public void Claude流式_工具调用input与partial_json()
    {
        StreamingAgentParser parser = new("claude-api");
        Feed(parser,
            Frame("""{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"t1","name":"f","input":{}}}"""),
            Frame("""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"n\":1}"}}"""));

        Dictionary<string, object?> function =
            (Dictionary<string, object?>)((Dictionary<string, object?>)
                ((List<object?>)parser.Result()["toolCalls"]!)[0]!)["function"]!;
        Assert.Equal("""{"n":1}""", function["arguments"]);
    }

    [Fact]
    public void Claude流式_非空input直接序列化为参数()
    {
        StreamingAgentParser parser = new("claude-api");
        Feed(parser, Frame(
            """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"t1","name":"f","input":{"x":1}}}"""));

        Dictionary<string, object?> function =
            (Dictionary<string, object?>)((Dictionary<string, object?>)
                ((List<object?>)parser.Result()["toolCalls"]!)[0]!)["function"]!;
        Assert.Equal("""{"x":1}""", function["arguments"]);
    }

    [Fact]
    public void Claude流式_error事件在通用校验阶段拦下()
    {
        // 先确认校验函数对该载荷确实返回异常（隔离分帧层的干扰）。
        Dictionary<string, object?> typed = OpenAICanvas.Outbound.JsonFields.ParseObject(
            """{"type":"error","error":{"message":"上游炸了"}}""")!;
        Assert.NotNull(StreamingAgentParser.ValidateTextPayload(typed));

        StreamingAgentParser parser = new("claude-api");
        parser.Consume(EventStream, Encoding.UTF8.GetBytes(
            Frame("""{"type":"error","error":{"message":"上游炸了"}}""")))
            ;
        parser.Flush();

        // 与 Go 一致：error.message 非空时由 validateTextPayload 拦下（先于协议分发），
        // 因此抛的是 ProviderPayloadException 而非协议分支里的普通异常。
        ProviderPayloadException error = Assert.Throws<ProviderPayloadException>(() => parser.Result());
        Assert.Equal("上游炸了", error.Raw);
    }

    [Fact]
    public void Claude流式_error无message时落到协议分支兜底()
    {
        StreamingAgentParser parser = new("claude-api");
        parser.Consume(EventStream, Encoding.UTF8.GetBytes(Frame("""{"type":"error","error":{}}""")))
            ;
        parser.Flush();

        // error 对象无 message 时不触发通用校验，走 Claude 专用分支的兜底文案。
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => parser.Result());
        Assert.Contains("Claude 上游返回失败", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 通用校验_code须为数字且非零()
    {
        // 数字非零 -> 失败。
        Dictionary<string, object?> numeric = OpenAICanvas.Outbound.JsonFields.ParseObject(
            """{"code":1001,"msg":"boom"}""")!;
        Assert.NotNull(StreamingAgentParser.ValidateTextPayload(numeric));

        // 数字零 -> 不失败。
        Assert.Null(StreamingAgentParser.ValidateTextPayload(
            OpenAICanvas.Outbound.JsonFields.ParseObject("""{"code":0}""")!));

        // 字符串 code 不参与判定（否则正常响应里的业务 code 会被误判）。
        Assert.Null(StreamingAgentParser.ValidateTextPayload(
            OpenAICanvas.Outbound.JsonFields.ParseObject("""{"code":"1001"}""")!));
    }

    [Fact]
    public void 通用校验_error须含非空message()
    {
        Assert.NotNull(StreamingAgentParser.ValidateTextPayload(
            OpenAICanvas.Outbound.JsonFields.ParseObject("""{"error":{"message":"x"}}""")!));

        // 空 message 不失败。
        Assert.Null(StreamingAgentParser.ValidateTextPayload(
            OpenAICanvas.Outbound.JsonFields.ParseObject("""{"error":{"message":""}}""")!));
        Assert.Null(StreamingAgentParser.ValidateTextPayload(
            OpenAICanvas.Outbound.JsonFields.ParseObject("""{"error":{}}""")!));
    }

    // ------------------------------------------------------------ 分帧与边界

    [Fact]
    public void 分帧_跨chunk的帧边界()
    {
        StreamingAgentParser parser = new("chat-completion");
        // 一帧被拆成三次网络分片投喂。
        parser.Consume(EventStream, Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\""));
        parser.Consume(EventStream, Encoding.UTF8.GetBytes(":{\"content\":\"split\"}}]}"));
        parser.Consume(EventStream, Encoding.UTF8.GetBytes("\n\n"));
        parser.Flush();

        Assert.Equal("split", parser.Result()["text"]);
    }

    [Fact]
    public void 分帧_支持CRLF与CR分隔()
    {
        StreamingAgentParser parser = new("chat-completion");
        Feed(parser,
            "data: " + """{"choices":[{"delta":{"content":"crlf"}}]}""" + "\r\n\r\n",
            "data: " + """{"choices":[{"delta":{"content":"cr"}}]}""" + "\r\r");

        Assert.Equal("crlfcr", parser.Result()["text"]);
    }

    [Fact]
    public void 分帧_flush消费残留的无边界尾帧()
    {
        StreamingAgentParser parser = new("chat-completion");
        // 没有结尾空行，靠 Flush 收尾。
        parser.Consume(EventStream, Encoding.UTF8.GetBytes(
            "data: " + """{"choices":[{"delta":{"content":"tail"}}]}"""));
        parser.Flush();

        Assert.Equal("tail", parser.Result()["text"]);
    }

    [Fact]
    public void 分帧_多行data以换行连接()
    {
        StreamingAgentParser parser = new("chat-completion");
        Feed(parser, "data: {\"choices\":[{\"delta\":\ndata: {\"content\":\"multi\"}}]}\n\n");

        Assert.Equal("multi", parser.Result()["text"]);
    }

    [Fact]
    public void 分帧_DONE与空帧被跳过()
    {
        StreamingAgentParser parser = new("chat-completion");
        Feed(parser,
            "data: [DONE]\n\n",
            ": comment only\n\n",
            Frame("""{"choices":[{"delta":{"content":"x"}}]}"""));

        Assert.Equal("x", parser.Result()["text"]);
    }

    [Fact]
    public void 分帧_非eventStream的MIME被忽略()
    {
        StreamingAgentParser parser = new("chat-completion");
        parser.Consume("application/json", Encoding.UTF8.GetBytes("""{"choices":[]}"""));
        parser.Flush();

        Assert.Equal("", parser.Result()["text"]);
    }

    [Fact]
    public void 分帧_非法JSON事件抛解析错误()
    {
        StreamingAgentParser parser = new("chat-completion");
        parser.Consume(EventStream, Encoding.UTF8.GetBytes("data: {not json}\n\n"));
        parser.Flush();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => parser.Result());
        Assert.Contains("Agent 流式事件解析失败", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 分帧_流内业务失败抛出归类文案()
    {
        StreamingAgentParser parser = new("chat-completion");
        // 与 Go 一致：code 必须是数字且非 0；错误正文取 msg（缺失时回落"请求失败"）。
        parser.Consume(EventStream, Encoding.UTF8.GetBytes(
            Frame("""{"code":1001,"msg":"insufficient balance"}""")));
        parser.Flush();

        ProviderPayloadException error = Assert.Throws<ProviderPayloadException>(() => parser.Result());
        Assert.Equal("insufficient balance", error.Raw);
        Assert.Contains("额度不足", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 分帧_数字code缺msg时用默认原因()
    {
        StreamingAgentParser parser = new("chat-completion");
        parser.Consume(EventStream, Encoding.UTF8.GetBytes(Frame("""{"code":500}""")));
        parser.Flush();

        ProviderPayloadException error = Assert.Throws<ProviderPayloadException>(() => parser.Result());
        Assert.Equal("请求失败", error.Raw);
    }

    [Fact]
    public void 工具调用_参数非完整JSON时报错()
    {
        StreamingAgentParser parser = new("chat-completion");
        Feed(parser, Frame(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"f","arguments":"{\"a\":"}}]}}]}"""));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => parser.Result());
        Assert.Contains("不是完整 JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 工具调用_按index升序输出()
    {
        StreamingAgentParser parser = new("chat-completion");
        Feed(parser, Frame(
            """{"choices":[{"delta":{"tool_calls":[{"index":2,"id":"c3","function":{"name":"third","arguments":"{}"}},{"index":0,"id":"c1","function":{"name":"first","arguments":"{}"}}]}}]}"""));

        List<object?> calls = (List<object?>)parser.Result()["toolCalls"]!;
        Assert.Equal(2, calls.Count);
        Assert.Equal("c1", ((Dictionary<string, object?>)calls[0]!)["id"]);
        Assert.Equal("c3", ((Dictionary<string, object?>)calls[1]!)["id"]);
    }

    // ------------------------------------------------------------ 非流式解析

    [Fact]
    public void 非流式_聊天补全解析()
    {
        Dictionary<string, object?> payload = OpenAICanvas.Outbound.JsonFields.ParseObject("""
            {"choices":[{"message":{"content":"hello","reasoning_content":"r",
             "tool_calls":[{"id":"c1","function":{"name":"f","arguments":"{}"}}]}}]}
            """)!;

        Dictionary<string, object?> result = AgentToolPayload.Parse(payload, "chat-completion");
        Assert.Equal("hello", result["text"]);
        Assert.Equal("r", result["reasoning"]);
        Assert.Single((List<object?>)result["toolCalls"]!);
    }

    [Fact]
    public void 非流式_聊天补全缺choices时报错()
    {
        Dictionary<string, object?> payload = OpenAICanvas.Outbound.JsonFields.ParseObject("{}")!;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => AgentToolPayload.Parse(payload, "chat-completion"));
        Assert.Contains("没有返回 choices", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 非流式_Responses提取文本与推理()
    {
        Dictionary<string, object?> payload = OpenAICanvas.Outbound.JsonFields.ParseObject("""
            {"output":[
              {"type":"reasoning","summary":[{"text":"because"}]},
              {"type":"message","content":[{"type":"output_text","text":"answer"}]},
              {"type":"function_call","call_id":"c1","name":"f","arguments":"{}"}
            ]}
            """)!;

        Dictionary<string, object?> result = AgentToolPayload.Parse(payload, "responses");
        Assert.Equal("answer", result["text"]);
        Assert.Equal("because", result["reasoning"]);
        Assert.Single((List<object?>)result["toolCalls"]!);
    }

    [Fact]
    public void 非流式_Responses优先output_text()
    {
        Dictionary<string, object?> payload = OpenAICanvas.Outbound.JsonFields.ParseObject("""
            {"output_text":"direct","output":[{"type":"message","content":[{"type":"output_text","text":"nested"}]}]}
            """)!;

        Assert.Equal("direct", AgentToolPayload.Parse(payload, "responses")["text"]);
    }

    [Fact]
    public void 非流式_Claude解析文本与工具()
    {
        Dictionary<string, object?> payload = OpenAICanvas.Outbound.JsonFields.ParseObject("""
            {"content":[
              {"type":"text","text":"hi"},
              {"type":"thinking","thinking":"hmm"},
              {"type":"tool_use","id":"t1","name":"f","input":{"k":1}}
            ]}
            """)!;

        Dictionary<string, object?> result = AgentToolPayload.Parse(payload, "claude-api");
        Assert.Equal("hi", result["text"]);
        Assert.Equal("hmm", result["reasoning"]);
        Dictionary<string, object?> function = (Dictionary<string, object?>)
            ((Dictionary<string, object?>)((List<object?>)result["toolCalls"]!)[0]!)["function"]!;
        Assert.Equal("""{"k":1}""", function["arguments"]);
    }

    [Fact]
    public void 非流式_Claude无内容时报错()
    {
        Dictionary<string, object?> payload = OpenAICanvas.Outbound.JsonFields.ParseObject("""{"content":[]}""")!;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => AgentToolPayload.Parse(payload, "claude-api"));
        Assert.Contains("没有返回内容", error.Message, StringComparison.Ordinal);
    }
}
