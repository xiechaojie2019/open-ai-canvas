#nullable enable
using System.Text;
using System.Text.Json;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 协议中立 Agent 请求到三协议请求体的展开契约测试。
/// 对应 Go: <c>app/provider_agent_protocol.go</c> 与 <c>claudeAgentBody</c>。
/// </summary>
public sealed class ProviderAgentProtocolTests
{
    private static CanonicalAgentRequestInput Sample()
    {
        Dictionary<string, JsonElement> Tool(string name) => new(StringComparer.Ordinal)
        {
            ["type"] = JsonSerializer.SerializeToElement("function"),
            ["function"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["description"] = name + " 工具",
                ["parameters"] = new Dictionary<string, object?> { ["type"] = "object" },
            }),
        };
        return new CanonicalAgentRequestInput
        {
            SystemPrompt = "你是画布助手",
            ToolChoice = JsonSerializer.SerializeToElement("auto"),
            Tools = [Tool("canvas_read")],
            Messages =
            [
                Msg("user", JsonSerializer.SerializeToElement("开始")),
                AssistantToolCalls(("call-1", "canvas_read", "{\"id\":\"node-1\"}")),
                MsgTool("call-1", "{\"ok\":true}"),
                Msg("assistant", JsonSerializer.SerializeToElement("完成")),
            ],
        };
    }

    private static Dictionary<string, JsonElement> Msg(string role, JsonElement content) => new(StringComparer.Ordinal)
    {
        ["role"] = JsonSerializer.SerializeToElement(role),
        ["content"] = content,
    };

    private static Dictionary<string, JsonElement> AssistantToolCalls(params (string ID, string Name, string Args)[] calls)
    {
        Dictionary<string, JsonElement> message = new(StringComparer.Ordinal)
        {
            ["role"] = JsonSerializer.SerializeToElement("assistant"),
            ["content"] = JsonSerializer.SerializeToElement(""),
        };
        List<object?> list = [];
        foreach ((string id, string name, string args) in calls)
        {
            list.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = name,
                    ["arguments"] = args,
                },
            });
        }
        message["tool_calls"] = JsonSerializer.SerializeToElement(list);
        return message;
    }

    /// <summary>
    /// 线上真实形态：运行时装配的 canonical 消息里 tool_calls 项<b>只有 id 与 function</b>，
    /// 没有协议判别字段 <c>type</c>。判别字段由投影阶段物化。
    /// </summary>
    private static Dictionary<string, JsonElement> AssistantBareToolCalls(params (string ID, string Name, string Args)[] calls)
    {
        Dictionary<string, JsonElement> message = new(StringComparer.Ordinal)
        {
            ["role"] = JsonSerializer.SerializeToElement("assistant"),
            ["content"] = JsonSerializer.SerializeToElement(""),
        };
        List<object?> list = [];
        foreach ((string id, string name, string args) in calls)
        {
            list.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = name,
                    ["arguments"] = args,
                },
            });
        }
        message["tool_calls"] = JsonSerializer.SerializeToElement(list);
        return message;
    }

    private static Dictionary<string, JsonElement> MsgTool(string callID, string content) => new(StringComparer.Ordinal)
    {
        ["role"] = JsonSerializer.SerializeToElement("tool"),
        ["tool_call_id"] = JsonSerializer.SerializeToElement(callID),
        ["content"] = JsonSerializer.SerializeToElement(content),
    };

    private static ProviderConfig ChatConfig() => new()
    {
        Model = "chat-model",
        BaseURL = "https://api.example.com",
        APIKey = "k",
        InterfaceType = "chat-completion",
    };

    [Fact]
    public void 展开_Chat_协议物化工具调用与选择()
    {
        AgentToolRequestsInput requests = ProviderAgentProtocol.ExpandCanonicalAgentRequest(
            Sample(), ChatConfig(), declarative: false);
        Assert.NotNull(requests.ChatCompletion);
        Assert.Null(requests.Responses);
        Dictionary<string, object?> body = requests.ChatCompletion!;
        Assert.True(body.ContainsKey("prompt_cache_key") == false);

        var messages = Assert.IsType<List<object?>>(body["messages"]);
        // 系统提示缺失时前置注入。
        var system = Assert.IsType<Dictionary<string, object?>>(messages[0]);
        Assert.Equal("system", system["role"]);
        Assert.Equal("你是画布助手", system["content"]);
        // assistant 的 tool_calls 原样带 type=function（系统提示前置后助手在 index 2）。
        var assistant = Assert.IsType<Dictionary<string, object?>>(messages[2]);
        var toolCalls = Assert.IsType<List<Dictionary<string, object?>>>(assistant["tool_calls"]);
        var call = toolCalls[0];
        Assert.Equal("function", call["type"]);
        var function = Assert.IsType<Dictionary<string, object?>>(call["function"]);
        Assert.Equal("canvas_read", function["name"]);
        // tool 消息带 tool_call_id。
        var tool = Assert.IsType<Dictionary<string, object?>>(messages[3]);
        Assert.Equal("tool", tool["role"]);
        Assert.Equal("call-1", tool["tool_call_id"]);
        Assert.Equal("auto", body["tool_choice"]);
        Assert.False((bool)body["parallel_tool_calls"]!);
    }

    [Fact]
    public void 展开_Responses_协议输出函数调用条目()
    {
        ProviderConfig responsesConfig = new()
        {
            Model = "resp-model",
            BaseURL = "https://api.example.com",
            APIKey = "k",
            InterfaceType = "openai-response",
        };
        AgentToolRequestsInput requests = ProviderAgentProtocol.ExpandCanonicalAgentRequest(
            Sample(), responsesConfig, declarative: false);
        Dictionary<string, object?> body = requests.Responses
            ?? throw new InvalidOperationException("responses body missing");
        var messages = Assert.IsType<List<object?>>(body["input"]);
        var kinds = new List<string>();
        foreach (object? item in messages)
        {
            var entry = Assert.IsType<Dictionary<string, object?>>(item);
            kinds.Add(entry.TryGetValue("type", out object? type) ? type as string ?? entry["role"] as string ?? "" : entry["role"] as string ?? "");
        }
        Assert.Contains("function_call", kinds);
        Assert.Contains("function_call_output", kinds);
        // 工具定义扁平化：function 字段展开为顶层 + type=function。
        var tools = Assert.IsType<List<object?>>(body["tools"]);
        var tool = Assert.IsType<Dictionary<string, object?>>(tools[0]);
        Assert.Equal("function", tool["type"]);
        Assert.Equal("canvas_read", tool["name"]);
    }

    [Fact]
    public void 展开_Claude_协议转写系统提示与工具块()
    {
        ProviderConfig config = new()
        {
            Model = "claude-model",
            BaseURL = "https://api.example.com",
            APIKey = "k",
            InterfaceType = "claude-api",
        };
        AgentToolRequestsInput requests = ProviderAgentProtocol.ExpandCanonicalAgentRequest(
            Sample(), config, declarative: false);
        Dictionary<string, object?> body = requests.Claude
            ?? throw new InvalidOperationException("claude body missing");
        Assert.Equal(4096, body["max_tokens"]);
        var system = Assert.IsType<List<object?>>(body["system"]);
        var systemText = Assert.IsType<Dictionary<string, object?>>(system[0]);
        Assert.Equal("你是画布助手", systemText["text"]);
        Assert.Equal("ephemeral", ((Dictionary<string, object?>)systemText["cache_control"]!)["type"]);

        var messages = Assert.IsType<List<object?>>(body["messages"]);
        // 系统提示折叠进 system 字段，消息从 user 开始。
        var first = Assert.IsType<Dictionary<string, object?>>(messages[0]);
        Assert.Equal("user", first["role"]);
        // tool 结果 → user 内容块 tool_result。
        var toolResult = Assert.IsType<Dictionary<string, object?>>(messages[2]);
        Assert.Equal("user", toolResult["role"]);
        var blocks = Assert.IsType<List<object?>>(toolResult["content"]);
        var block = Assert.IsType<Dictionary<string, object?>>(blocks[0]);
        Assert.Equal("tool_result", block["type"]);
        Assert.Equal("call-1", block["tool_use_id"]);

        // assistant tool_calls → tool_use 内容块。
        var assistant = Assert.IsType<Dictionary<string, object?>>(messages[1]);
        var useBlocks = Assert.IsType<List<object?>>(assistant["content"]);
        var use = Assert.IsType<Dictionary<string, object?>>(useBlocks[0]);
        Assert.Equal("tool_use", use["type"]);
        Assert.Equal("canvas_read", use["name"]);

        // 工具定义：input_schema + 末位 cache_control。
        var tools = Assert.IsType<List<object?>>(body["tools"]);
        var tool = Assert.IsType<Dictionary<string, object?>>(tools[0]);
        Assert.Equal("canvas_read", tool["name"]);
        Assert.NotNull(tool["input_schema"]);
        Assert.Equal("ephemeral", tool["cache_control"] as Dictionary<string, object?> is { } cc ? cc["type"] : null);
        // auto 选择 → {type: auto}。
        var choice = Assert.IsType<Dictionary<string, object?>>(body["tool_choice"]!);
        Assert.Equal("auto", choice["type"]);
    }

    [Fact]
    public void 展开_Responses_工具调用的call_id必须来自顶层id()
    {
        // 线上真实形状：tool_calls 项只有 id 与 function（无协议判别字段）。
        // 早先实现取 function["id"]（function 里根本没有 id）导致 call_id 恒为 null，
        // 上游随即拒绝整轮请求。
        CanonicalAgentRequestInput canonical = new()
        {
            SystemPrompt = "你是画布助手",
            ToolChoice = JsonSerializer.SerializeToElement("auto"),
            Tools =
            [
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["type"] = JsonSerializer.SerializeToElement("function"),
                    ["function"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                    {
                        ["name"] = "canvas_get_state",
                        ["parameters"] = new Dictionary<string, object?> { ["type"] = "object" },
                    }),
                },
            ],
            Messages =
            [
                AssistantBareToolCalls(("call_abc123", "canvas_get_state", "{}")),
                MsgTool("call_abc123", "{\"ok\":true}"),
                Msg("user", JsonSerializer.SerializeToElement("继续")),
            ],
        };
        ProviderConfig config = new()
        {
            Model = "glm-5.3-flash",
            BaseURL = "https://api.example.com",
            APIKey = "k",
            InterfaceType = "openai-response",
        };
        AgentToolRequestsInput requests = ProviderAgentProtocol.ExpandCanonicalAgentRequest(
            canonical, config, declarative: false);
        Dictionary<string, object?> body = requests.Responses
            ?? throw new InvalidOperationException("responses body missing");

        var input = Assert.IsType<List<object?>>(body["input"]);
        Dictionary<string, object?>? call = null;
        Dictionary<string, object?>? output = null;
        foreach (object? item in input)
        {
            var entry = Assert.IsType<Dictionary<string, object?>>(item);
            switch (entry.GetValueOrDefault("type") as string)
            {
                case "function_call":
                    call = entry;
                    break;
                case "function_call_output":
                    output = entry;
                    break;
            }
        }
        Assert.NotNull(call);
        Assert.NotNull(output);
        Assert.Equal("call_abc123", call!["call_id"]);
        Assert.Equal("canvas_get_state", call["name"]);
        // 回执与调用必须引用同一个调用标识，否则上游无法配对。
        Assert.Equal(call["call_id"], output!["call_id"]);
    }

    [Fact]
    public void 展开_Chat_出网工具调用必须物化type()
    {
        // 运行时 canonical 不带协议判别字段，出网体必须补齐 type=function，
        // 否则 dagent 类网关报「工具类型不能为空」。
        CanonicalAgentRequestInput canonical = new()
        {
            SystemPrompt = "你是画布助手",
            ToolChoice = JsonSerializer.SerializeToElement("auto"),
            Tools =
            [
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["type"] = JsonSerializer.SerializeToElement("function"),
                    ["function"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                    {
                        ["name"] = "canvas_get_state",
                        ["parameters"] = new Dictionary<string, object?> { ["type"] = "object" },
                    }),
                },
            ],
            Messages =
            [
                AssistantBareToolCalls(("call_abc123", "canvas_get_state", "{}")),
                MsgTool("call_abc123", "{\"ok\":true}"),
                Msg("user", JsonSerializer.SerializeToElement("继续")),
            ],
        };
        AgentToolRequestsInput requests = ProviderAgentProtocol.ExpandCanonicalAgentRequest(
            canonical, ChatConfig(), declarative: false);
        Dictionary<string, object?> body = requests.ChatCompletion
            ?? throw new InvalidOperationException("chat body missing");
        var messages = Assert.IsType<List<object?>>(body["messages"]);
        bool checkedCall = false;
        foreach (object? item in messages)
        {
            var message = Assert.IsType<Dictionary<string, object?>>(item);
            if (message.GetValueOrDefault("tool_calls") is not List<Dictionary<string, object?>> calls)
            {
                continue;
            }
            foreach (Dictionary<string, object?> call in calls)
            {
                Assert.Equal("function", call["type"]);
                Assert.Equal("call_abc123", call["id"]);
                checkedCall = true;
            }
        }
        Assert.True(checkedCall);
    }

    [Fact]
    public void 展开_声明式填充全部协议体()
    {
        AgentToolRequestsInput requests = ProviderAgentProtocol.ExpandCanonicalAgentRequest(
            Sample(), ChatConfig(), declarative: true);
        Assert.NotNull(requests.ChatCompletion);
        Assert.NotNull(requests.Responses);
        Assert.NotNull(requests.Claude);
        Assert.NotNull(requests.Gemini);
        Assert.Equal("chat-model", requests.Claude!["model"]);

        Dictionary<string, object?> gemini = requests.Gemini!;
        var contents = Assert.IsType<List<object?>>(gemini["contents"]);
        bool hasFunctionResponse = false;
        foreach (object? item in contents)
        {
            var content = Assert.IsType<Dictionary<string, object?>>(item);
            var parts = Assert.IsType<List<object?>>(content["parts"]);
            foreach (object? partRaw in parts)
            {
                var part = Assert.IsType<Dictionary<string, object?>>(partRaw);
                hasFunctionResponse |= part.ContainsKey("functionResponse");
            }
        }
        // 与 Go 一致：gemini 体只为 function_call 型历史条目生成 functionCall，
        // assistant 消息里的 tool_calls 数组不进入 gemini 内容；工具回执始终生成 functionResponse。
        Assert.True(hasFunctionResponse);
        Assert.NotNull(gemini["systemInstruction"]);
        Assert.NotNull(gemini["tools"]);
        Assert.NotNull(gemini["toolConfig"]);
    }

    [Fact]
    public void 校验_缺少工具选择与非法角色被拒绝()
    {
        CanonicalAgentRequestInput noChoice = new()
        {
            Messages = [Msg("user", JsonSerializer.SerializeToElement("hi"))],
            ToolChoice = default,
        };
        Assert.Throws<InvalidOperationException>(() => ProviderAgentProtocol.ExpandCanonicalAgentRequest(
            noChoice, ChatConfig(), declarative: false));

        CanonicalAgentRequestInput badRole = new()
        {
            Messages = [Msg("robot", JsonSerializer.SerializeToElement("hi"))],
            ToolChoice = JsonSerializer.SerializeToElement("auto"),
        };
        Assert.Throws<InvalidOperationException>(() => ProviderAgentProtocol.ExpandCanonicalAgentRequest(
            badRole, ChatConfig(), declarative: false));

        CanonicalAgentRequestInput empty = new()
        {
            Messages = [],
            ToolChoice = JsonSerializer.SerializeToElement("auto"),
        };
        Assert.Throws<InvalidOperationException>(() => ProviderAgentProtocol.ExpandCanonicalAgentRequest(
            empty, ChatConfig(), declarative: false));
    }

    [Fact]
    public void Agent执行_请求体带模型与并行关闭_响应解析工具调用()
    {
        string? lastBody = null;
        var handler = new StubHandler(request =>
        {
            if (request.Content is not null)
            {
                lastBody = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            return Json(System.Net.HttpStatusCode.OK, """
                {"choices":[{"message":{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"canvas_read","arguments":"{\"id\":\"n1\"}"}}]}}]}
                """);
        });
        ProviderTextTask task = new(null, () => new HttpClient(handler));
        AgentToolRequestsInput requests = ProviderAgentProtocol.ExpandCanonicalAgentRequest(
            Sample(), ChatConfig(), declarative: false);
        TextTaskInput input = new()
        {
            Mode = "text",
            Prompt = "开始",
            Config = ChatConfig(),
            TextOptions = new CanvasTextOptions { Stream = false },
            AgentRequests = requests,
        };
        Dictionary<string, object?> result = task.RunAgentToolAsync(input, stream: false)
            .GetAwaiter().GetResult();
        Assert.NotNull(lastBody);
        using JsonDocument sent = JsonDocument.Parse(lastBody!);
        Assert.Equal("chat-model", sent.RootElement.GetProperty("model").GetString());
        Assert.False(sent.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        // 非流式请求必须移除 stream（与 Go 一致）。
        Assert.False(sent.RootElement.TryGetProperty("stream", out _));

        Assert.Equal("text", result["mode"]);
        var calls = Assert.IsType<List<object?>>(result["toolCalls"]);
        var parsed = Assert.IsType<Dictionary<string, object?>>(calls[0]);
        Assert.Equal("c1", parsed["id"]);
    }

    [Fact]
    public void Agent执行_tool_choice兼容性错误时降级重试()
    {
        int attempts = 0;
        string? lastBody = null;
        var handler = new StubHandler(request =>
        {
            attempts++;
            if (request.Content is not null)
            {
                lastBody = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            if (attempts == 1)
            {
                return Json(System.Net.HttpStatusCode.BadRequest,
                    """{"error":{"message":"tool_choice is not supported"}}""");
            }
            return Json(System.Net.HttpStatusCode.OK,
                """{"choices":[{"message":{"role":"assistant","content":"好的"}}]}""");
        });
        ProviderTextTask task = new(null, () => new HttpClient(handler));
        CanonicalAgentRequestInput canonical = Sample();
        canonical.ToolChoice = JsonSerializer.SerializeToElement("required");
        AgentToolRequestsInput requests = ProviderAgentProtocol.ExpandCanonicalAgentRequest(
            canonical, ChatConfig(), declarative: false);
        TextTaskInput input = new()
        {
            Mode = "text",
            Prompt = "开始",
            Config = ChatConfig(),
            TextOptions = new CanvasTextOptions { Stream = false },
            AgentRequests = requests,
        };
        Dictionary<string, object?> result = task.RunAgentToolAsync(input, stream: false)
            .GetAwaiter().GetResult();
        Assert.True(attempts >= 2);
        Assert.Equal("好的", result["text"]);
        Assert.NotNull(lastBody);
    }

    private static HttpResponseMessage Json(System.Net.HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = responder(request);
            return Task.FromResult(response);
        }
    }
}
