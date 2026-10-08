#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Providers;

/// <summary>协议中立 Agent 请求（浏览器/运行时持久化的形态）。对应 Go: <c>app.canonicalAgentRequest</c>。</summary>
public sealed class CanonicalAgentRequestInput
{
    [JsonPropertyName("messages")]
    public List<Dictionary<string, JsonElement>> Messages { get; set; } = [];

    [JsonPropertyName("tools")]
    public List<Dictionary<string, JsonElement>> Tools { get; set; } = [];

    /// <summary>缺省（Undefined）等价 Go 的 nil，展开校验会拒绝。</summary>
    [JsonPropertyName("toolChoice")]
    public JsonElement ToolChoice { get; set; }

    [JsonPropertyName("systemPrompt")]
    public string SystemPrompt { get; set; } = "";

    [JsonPropertyName("promptCacheKey")]
    public string PromptCacheKey { get; set; } = "";
}

/// <summary>Agent 协议请求集合。对应 Go: <c>app.agentToolRequests</c>（json 契约一致）。</summary>
public sealed class AgentToolRequestsInput
{
    [JsonPropertyName("canonical")]
    public CanonicalAgentRequestInput? Canonical { get; set; }

    [JsonPropertyName("responses")]
    public Dictionary<string, object?>? Responses { get; set; }

    [JsonPropertyName("chatCompletion")]
    public Dictionary<string, object?>? ChatCompletion { get; set; }

    [JsonPropertyName("claude")]
    public Dictionary<string, object?>? Claude { get; set; }

    [JsonPropertyName("gemini")]
    public Dictionary<string, object?>? Gemini { get; set; }
}

/// <summary>
/// 浏览器/运行时只持久化协议中立会话；只有选中的供应商请求体才被物化，
/// 防止供应商请求体反向污染任务记录，也避免切换模型时复用错误协议。
/// 对应 Go: <c>app/provider_agent_protocol.go</c>。
/// </summary>
public static class ProviderAgentProtocol
{
    /// <summary>对应 Go: <c>expandCanonicalAgentRequest</c>。</summary>
    public static AgentToolRequestsInput ExpandCanonicalAgentRequest(
        CanonicalAgentRequestInput source, ProviderConfig config, bool declarative)
    {
        if (source.Messages.Count == 0)
        {
            throw new InvalidOperationException("画布 Agent 请求缺少会话内容");
        }
        foreach (Dictionary<string, JsonElement> message in source.Messages)
        {
            if (Str(message, "type") == "function_call")
            {
                if (Str(message, "call_id").Length == 0 || Str(message, "name").Length == 0)
                {
                    throw new InvalidOperationException("画布 Agent 工具调用缺少标识或名称");
                }
                if (!message.TryGetValue("arguments", out JsonElement arguments)
                    || arguments.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException("画布 Agent 工具调用参数无效");
                }
                continue;
            }
            if (Str(message, "type").Length != 0)
            {
                throw new InvalidOperationException("画布 Agent 会话条目类型无效");
            }
            switch (Str(message, "role"))
            {
                case "system" or "user" or "assistant":
                    break;
                case "tool":
                    if (Str(message, "tool_call_id").Length == 0)
                    {
                        throw new InvalidOperationException("画布 Agent 工具结果缺少调用标识");
                    }
                    if (!message.TryGetValue("content", out JsonElement content)
                        || content.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException("画布 Agent 工具结果内容无效");
                    }
                    break;
                default:
                    throw new InvalidOperationException("画布 Agent 会话角色无效");
            }
            ValidateCanonicalAgentContent(
                message.TryGetValue("content", out JsonElement value) ? value : default);
        }
        foreach (Dictionary<string, JsonElement> tool in source.Tools)
        {
            if (!tool.TryGetValue("function", out JsonElement function)
                || function.ValueKind != JsonValueKind.Object
                || Str(function.EnumerateObject(), "name").Length == 0)
            {
                throw new InvalidOperationException("画布 Agent 工具定义缺少名称");
            }
        }
        switch (source.ToolChoice.ValueKind)
        {
            case JsonValueKind.String:
                if (source.ToolChoice.GetString() is not ("auto" or "required"))
                {
                    throw new InvalidOperationException("画布 Agent 工具选择无效");
                }
                break;
            case JsonValueKind.Object:
                if (Str(source.ToolChoice.EnumerateObject(), "type") != "function"
                    || Str(source.ToolChoice.EnumerateObject(), "name").Length == 0)
                {
                    throw new InvalidOperationException("画布 Agent 工具选择缺少名称");
                }
                break;
            default:
                throw new InvalidOperationException("画布 Agent 请求缺少工具选择");
        }
        List<Dictionary<string, JsonElement>> messages = source.Messages;
        string prompt = source.SystemPrompt.Trim();
        if (prompt.Length > 0)
        {
            bool present = false;
            foreach (Dictionary<string, JsonElement> message in messages)
            {
                if (Str(message, "role") == "system" && Str(message, "content").Trim() == prompt)
                {
                    present = true;
                    break;
                }
            }
            if (!present)
            {
                messages =
                [
                    new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["role"] = JsonSerializer.SerializeToElement("system"),
                        ["content"] = JsonSerializer.SerializeToElement(prompt),
                    },
                    .. messages,
                ];
            }
        }
        CanonicalAgentRequestInput request = new()
        {
            Messages = messages,
            Tools = source.Tools,
            ToolChoice = source.ToolChoice,
            SystemPrompt = source.SystemPrompt,
            PromptCacheKey = source.PromptCacheKey,
        };
        AgentToolRequestsInput result = new();
        if (declarative)
        {
            result.ChatCompletion = CanonicalAgentChatBody(request, claude: false);
            result.Responses = CanonicalAgentResponsesBody(request);
            result.Claude = ClaudeAgentBody(CanonicalAgentChatBody(request, claude: true));
            result.Claude["model"] = config.Model;
            result.Gemini = CanonicalAgentGeminiBody(request);
            return result;
        }
        switch (config.InterfaceType)
        {
            case ChannelInterfaceType.ChannelInterfaceOpenAIResponse:
                result.Responses = CanonicalAgentResponsesBody(request);
                break;
            case ChannelInterfaceType.ChannelInterfaceClaudeAPI:
                result.Claude = ClaudeAgentBody(CanonicalAgentChatBody(request, claude: true));
                break;
            default:
                result.ChatCompletion = CanonicalAgentChatBody(request, claude: false);
                break;
        }
        return result;
    }

    /// <summary>对应 Go: <c>canonicalAgentChatBody</c>。</summary>
    public static Dictionary<string, object?> CanonicalAgentChatBody(
        CanonicalAgentRequestInput source, bool claude)
    {
        List<object?> messages = new(source.Messages.Count);
        string format = claude ? "claude" : "chat";
        int index = 0;
        while (index < source.Messages.Count)
        {
            Dictionary<string, JsonElement> message = source.Messages[index];
            if (Str(message, "type") == "function_call")
            {
                List<object?> calls = [];
                while (index < source.Messages.Count
                    && Str(source.Messages[index], "type") == "function_call")
                {
                    Dictionary<string, JsonElement> call = source.Messages[index];
                    object? arguments = Element(call, "arguments");
                    if (claude)
                    {
                        arguments = JSONValueToObject(arguments);
                    }
                    calls.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"] = Value(call, "call_id"),
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["name"] = Value(call, "name"),
                            ["arguments"] = arguments,
                        },
                    });
                    index++;
                }
                messages.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["role"] = "assistant",
                    ["content"] = null,
                    ["tool_calls"] = calls,
                });
                continue;
            }
            Dictionary<string, object?> converted = new(StringComparer.Ordinal)
            {
                ["role"] = Value(message, "role"),
                ["content"] = CanonicalAgentContent(Element(message, "content"), format),
            };
            List<Dictionary<string, object?>>? calls2 = CanonicalAgentToolCalls(Element(message, "tool_calls"));
            if (calls2 is { Count: > 0 })
            {
                // 运行时调用是协议中立的；在线判别字段在这里物化。
                foreach (Dictionary<string, object?> call in calls2)
                {
                    call["type"] = "function";
                }
                converted["tool_calls"] = calls2;
            }
            if (claude && Str(message, "role") == "system")
            {
                converted["content"] = CanonicalAgentText(Element(message, "content"));
            }
            if (Str(message, "role") == "tool")
            {
                converted["tool_call_id"] = Value(message, "tool_call_id");
            }
            messages.Add(converted);
            index++;
        }
        List<object?> tools = new(source.Tools.Count);
        foreach (Dictionary<string, JsonElement> tool in source.Tools)
        {
            tools.Add(JsonToObject(JsonSerializer.SerializeToElement(tool)));
        }
        object? choice = Value(source.ToolChoice);
        if (choice is Dictionary<string, object?> named)
        {
            choice = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = named.GetValueOrDefault("name"),
                },
            };
        }
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["messages"] = messages,
            ["tools"] = tools,
            ["tool_choice"] = choice,
            ["parallel_tool_calls"] = false,
        };
        if (!claude && source.PromptCacheKey.Length > 0)
        {
            body["prompt_cache_key"] = source.PromptCacheKey;
        }
        return body;
    }

    /// <summary>对应 Go: <c>canonicalAgentResponsesBody</c>。</summary>
    public static Dictionary<string, object?> CanonicalAgentResponsesBody(
        CanonicalAgentRequestInput source)
    {
        List<object?> messages = new(source.Messages.Count);
        foreach (Dictionary<string, JsonElement> message in source.Messages)
        {
            if (Str(message, "type") == "function_call")
            {
                messages.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "function_call",
                    ["call_id"] = Value(message, "call_id"),
                    ["name"] = Value(message, "name"),
                    ["arguments"] = Value(message, "arguments"),
                });
                continue;
            }
            if (Str(message, "role") == "tool")
            {
                messages.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = Value(message, "tool_call_id"),
                    ["output"] = Value(message, "content"),
                });
                continue;
            }
            messages.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = Value(message, "role"),
                ["content"] = CanonicalAgentContent(Element(message, "content"), "responses"),
            });
            // 与 Go 一致：assistant 消息里的 tool_calls 展开为独立的 function_call 条目。
            foreach (Dictionary<string, object?> call
                in CanonicalAgentToolCalls(Element(message, "tool_calls")) ?? [])
            {
                // call_id 必须取调用项的顶层 id（Go: call["id"]）。它就在 function 的兄弟位，
                // 不在 function 里面；早先写成 function["id"] 会让 call_id 恒为 null，
                // Responses 上游随即报「工具类型不能为空」。
                if (call.TryGetValue("function", out object? functionRaw)
                    && functionRaw is Dictionary<string, object?> function)
                {
                    messages.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "function_call",
                        ["call_id"] = call.GetValueOrDefault("id"),
                        ["name"] = function.GetValueOrDefault("name"),
                        ["arguments"] = function.GetValueOrDefault("arguments"),
                    });
                }
            }
        }
        List<object?> tools = new(source.Tools.Count);
        foreach (Dictionary<string, JsonElement> tool in source.Tools)
        {
            if (tool.TryGetValue("function", out JsonElement function)
                && JsonToObject(function) is Dictionary<string, object?> converted)
            {
                converted = new Dictionary<string, object?>(converted, StringComparer.Ordinal);
                converted["type"] = "function";
                tools.Add(converted);
            }
            else
            {
                tools.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "function" });
            }
        }
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["input"] = messages,
            ["tools"] = tools,
            ["tool_choice"] = Value(source.ToolChoice),
            ["parallel_tool_calls"] = false,
        };
        if (source.PromptCacheKey.Length > 0)
        {
            body["prompt_cache_key"] = source.PromptCacheKey;
        }
        return body;
    }

    /// <summary>对应 Go: <c>canonicalAgentGeminiBody</c>。</summary>
    public static Dictionary<string, object?> CanonicalAgentGeminiBody(
        CanonicalAgentRequestInput source)
    {
        List<object?> contents = [];
        List<string> system = [];
        Dictionary<string, string> callNames = new(StringComparer.Ordinal);
        foreach (Dictionary<string, JsonElement> message in source.Messages)
        {
            string role = "user";
            object? parts;
            if (Str(message, "type") == "function_call")
            {
                string id = Str(message, "call_id");
                string name = Str(message, "name");
                callNames[id] = name;
                Dictionary<string, object?> part = new(StringComparer.Ordinal)
                {
                    ["functionCall"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"] = id,
                        ["name"] = name,
                        ["args"] = JSONValueToObject(Element(message, "arguments")),
                    },
                };
                string signature = Str(message, "thoughtSignature");
                if (signature.Length > 0)
                {
                    part["thoughtSignature"] = signature;
                }
                role = "model";
                parts = new List<object?> { part };
            }
            else if (Str(message, "role") == "system")
            {
                string text = CanonicalAgentText(Element(message, "content"));
                if (text.Length > 0)
                {
                    system.Add(text);
                }
                continue;
            }
            else if (Str(message, "role") == "tool")
            {
                string id = Str(message, "tool_call_id");
                // Go 的 map 缺键返回 ""，等价于显式空串默认值。
                string name = callNames.GetValueOrDefault(id, "");
                if (name.Length == 0)
                {
                    name = "tool_result";
                }
                parts = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["functionResponse"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["id"] = id,
                            ["name"] = name,
                            ["response"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["result"] = JSONStringToAny(Element(message, "content")),
                            },
                        },
                    },
                };
            }
            else
            {
                if (Str(message, "role") == "assistant")
                {
                    role = "model";
                }
                parts = CanonicalAgentContent(Element(message, "content"), "gemini");
            }
            contents.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = role,
                ["parts"] = parts,
            });
        }
        Dictionary<string, object?> body = new(StringComparer.Ordinal) { ["contents"] = contents };
        if (system.Count > 0)
        {
            body["systemInstruction"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["parts"] = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = string.Join("\n\n", system) },
                },
            };
        }
        if (source.Tools.Count > 0)
        {
            List<object?> declarations = new(source.Tools.Count);
            foreach (Dictionary<string, JsonElement> tool in source.Tools)
            {
                if (tool.TryGetValue("function", out JsonElement function)
                    && JsonToObject(function) is Dictionary<string, object?> declaration)
                {
                    declaration = new Dictionary<string, object?>(declaration, StringComparer.Ordinal);
                    declaration.Remove("strict");
                    declarations.Add(declaration);
                }
                else
                {
                    declarations.Add(new Dictionary<string, object?>(StringComparer.Ordinal));
                }
            }
            Dictionary<string, object?> choice = new(StringComparer.Ordinal) { ["mode"] = "AUTO" };
            if (source.ToolChoice.ValueKind == JsonValueKind.Object)
            {
                if (JsonToObject(source.ToolChoice) is Dictionary<string, object?> named
                    && named.TryGetValue("name", out object? name))
                {
                    choice["mode"] = "ANY";
                    choice["allowedFunctionNames"] = new List<object?> { name };
                }
            }
            else if (source.ToolChoice.ValueKind == JsonValueKind.String
                && source.ToolChoice.GetString() == "required")
            {
                choice["mode"] = "ANY";
            }
            body["tools"] = new List<object?>
            {
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["functionDeclarations"] = declarations },
            };
            body["toolConfig"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["functionCallingConfig"] = choice,
            };
        }
        return body;
    }

    /// <summary>对应 Go: <c>canonicalAgentContent</c>。</summary>
    public static object? CanonicalAgentContent(JsonElement content, string format)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            if (content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                content = JsonSerializer.SerializeToElement("");
            }
            if (format == "gemini")
            {
                return new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = Value(content) },
                };
            }
            return Value(content);
        }
        List<object?> result = [];
        foreach (JsonElement itemElement in content.EnumerateArray())
        {
            if (JsonToObject(itemElement) is not Dictionary<string, object?> item)
            {
                continue;
            }
            string kind = item.TryGetValue("type", out object? typeRaw) ? typeRaw as string ?? "" : "";
            if (kind == "text")
            {
                Dictionary<string, object?> part = new(StringComparer.Ordinal) { ["text"] = item.GetValueOrDefault("text") };
                if (format != "gemini")
                {
                    part["type"] = "text";
                    if (format == "responses")
                    {
                        part["type"] = "input_text";
                    }
                }
                result.Add(part);
                continue;
            }
            Dictionary<string, object?> file =
                item.TryGetValue(kind, out object? raw) && raw is Dictionary<string, object?> fileMap
                    ? fileMap
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
            string url = file.TryGetValue("url", out object? urlRaw) ? urlRaw as string ?? "" : "";
            string mimeType = file.TryGetValue("mimeType", out object? mimeRaw) ? mimeRaw as string ?? "" : "";
            if (kind == "image_url")
            {
                mimeType = "image/png";
            }
            (string inlineMIME, string inlineData, bool inline) = CanonicalAgentDataURL(url);
            Dictionary<string, object?>? part2;
            switch (format)
            {
                case "responses":
                    part2 = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "input_image",
                        ["image_url"] = url,
                    };
                    if (kind == "file_url")
                    {
                        part2 = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "input_file",
                            ["filename"] = file.GetValueOrDefault("name"),
                        };
                        if (url.StartsWith("data:", StringComparison.Ordinal))
                        {
                            part2["file_data"] = url;
                        }
                        else
                        {
                            part2["file_url"] = url;
                        }
                    }
                    break;
                case "claude":
                    if (inline)
                    {
                        string blockType = "image";
                        if (kind == "file_url" && inlineMIME == "application/pdf")
                        {
                            blockType = "document";
                        }
                        part2 = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = blockType,
                            ["source"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["type"] = "base64",
                                ["media_type"] = inlineMIME,
                                ["data"] = inlineData,
                            },
                        };
                    }
                    else if (kind == "image_url")
                    {
                        part2 = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "image",
                            ["source"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["type"] = "url",
                                ["url"] = url,
                            },
                        };
                    }
                    else
                    {
                        part2 = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "text",
                            ["text"] = (file.GetValueOrDefault("name") as string ?? "") + ": " + url,
                        };
                    }
                    break;
                case "gemini":
                    if (inline)
                    {
                        part2 = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["inlineData"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["mimeType"] = inlineMIME,
                                ["data"] = inlineData,
                            },
                        };
                    }
                    else
                    {
                        part2 = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["fileData"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["fileUri"] = url,
                                ["mimeType"] = mimeType,
                            },
                        };
                    }
                    break;
                default:
                    part2 = new Dictionary<string, object?>(item, StringComparer.Ordinal);
                    if (kind == "file_url")
                    {
                        Dictionary<string, object?> converted = new(StringComparer.Ordinal)
                        {
                            ["filename"] = file.GetValueOrDefault("name"),
                        };
                        if (url.StartsWith("data:", StringComparison.Ordinal))
                        {
                            converted["file_data"] = url;
                        }
                        else
                        {
                            converted["file_url"] = url;
                        }
                        part2 = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "file",
                            ["file"] = converted,
                        };
                    }
                    break;
            }
            result.Add(part2);
        }
        return result;
    }

    /// <summary>对应 Go: <c>claudeAgentBody</c>：把 Chat 形态请求体改写为 Claude Messages 体。</summary>
    public static Dictionary<string, object?> ClaudeAgentBody(Dictionary<string, object?> request)
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal) { ["max_tokens"] = 4096 };
        if (request.TryGetValue("messages", out object? messagesRaw) && messagesRaw is List<object?> messages)
        {
            List<object?> claudeMessages = [];
            List<string> system = [];
            foreach (object? value in messages)
            {
                if (value is not Dictionary<string, object?> message)
                {
                    continue;
                }
                string role = (message.GetValueOrDefault("role") as string ?? "").Trim().ToLowerInvariant();
                if (role == "system")
                {
                    string content = ToStringOrEmpty(message.GetValueOrDefault("content")).Trim();
                    if (content.Length > 0)
                    {
                        system.Add(content);
                    }
                    continue;
                }
                if (role == "tool")
                {
                    claudeMessages.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["role"] = "user",
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = message.GetValueOrDefault("tool_call_id"),
                                ["content"] = ToStringOrEmpty(message.GetValueOrDefault("content")),
                            },
                        },
                    });
                    continue;
                }
                role = role == "assistant" ? "assistant" : "user";
                object? content2 = message.GetValueOrDefault("content") ?? "";
                // chat 体里的 tool_calls 实际是 List<Dictionary<...>>，泛型 List 不协变，
                // 必须按非泛型 IEnumerable 读取（与 Go 的 []interface{} 断言语义一致）。
                if (message.TryGetValue("tool_calls", out object? toolCallsRaw)
                    && toolCallsRaw is System.Collections.IEnumerable toolCallSequence
                    && toolCallsRaw is not string
                    && toolCallSequence.Cast<object?>().Any())
                {
                    List<object?> blocks = [];
                    foreach (object? callRaw in toolCallSequence.Cast<object?>())
                    {
                        if (callRaw is not Dictionary<string, object?> toolCall)
                        {
                            continue;
                        }
                        Dictionary<string, object?> function =
                            toolCall.TryGetValue("function", out object? fnRaw)
                                && fnRaw is Dictionary<string, object?> fn
                                ? fn
                                : new Dictionary<string, object?>(StringComparer.Ordinal);
                        blocks.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "tool_use",
                            ["id"] = toolCall.GetValueOrDefault("id"),
                            ["name"] = function.GetValueOrDefault("name"),
                            ["input"] = ClaudeToolInput(function.GetValueOrDefault("arguments")),
                        });
                    }
                    content2 = blocks;
                }
                claudeMessages.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["role"] = role,
                    ["content"] = content2,
                });
            }
            body["messages"] = claudeMessages;
            if (system.Count > 0)
            {
                body["system"] = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "text",
                        ["text"] = string.Join("\n\n", system),
                        ["cache_control"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "ephemeral" },
                    },
                };
            }
        }
        if (request.TryGetValue("tools", out object? toolsRaw) && toolsRaw is List<object?> tools && tools.Count > 0)
        {
            List<object?> claudeTools = [];
            foreach (object? value in tools)
            {
                if (value is not Dictionary<string, object?> tool
                    || !tool.TryGetValue("function", out object? fnRaw2)
                    || fnRaw2 is not Dictionary<string, object?> function2
                    || function2.Count == 0)
                {
                    continue;
                }
                claudeTools.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = function2.GetValueOrDefault("name"),
                    ["description"] = function2.GetValueOrDefault("description"),
                    ["input_schema"] = function2.GetValueOrDefault("parameters"),
                });
            }
            if (claudeTools.Count > 0)
            {
                if (claudeTools[^1] is Dictionary<string, object?> last)
                {
                    last["cache_control"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "ephemeral" };
                }
                body["tools"] = claudeTools;
            }
        }
        if (request.TryGetValue("tool_choice", out object? choiceRaw))
        {
            body["tool_choice"] = ClaudeToolChoice(choiceRaw);
        }
        return body;
    }

    /// <summary>对应 Go: <c>claudeToolInput</c>。</summary>
    public static object? ClaudeToolInput(object? value)
    {
        if (value is string raw)
        {
            try
            {
                JsonElement parsed = JsonSerializer.Deserialize<JsonElement>(raw);
                if (parsed.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    return JsonToObject(parsed);
                }
            }
            catch (JsonException)
            {
                // 解析失败按 Go 语义返回原值。
            }
        }
        return value ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>对应 Go: <c>claudeToolChoice</c>。</summary>
    public static object? ClaudeToolChoice(object? value)
    {
        switch (value)
        {
            case string choice:
                {
                    string normalized = choice.Trim().ToLowerInvariant();
                    return new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = normalized == "required" ? "any" : "auto",
                    };
                }
            case Dictionary<string, object?> choiceMap:
                {
                    if (choiceMap.TryGetValue("function", out object? fnRaw)
                        && fnRaw is Dictionary<string, object?> function
                        && function.TryGetValue("name", out object? functionName)
                        && (functionName as string ?? "").Length != 0)
                    {
                        return new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "tool",
                            ["name"] = functionName,
                        };
                    }
                    if (choiceMap.TryGetValue("name", out object? name) && (name as string ?? "").Length != 0)
                    {
                        return new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "tool",
                            ["name"] = name,
                        };
                    }
                    break;
                }
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "auto" };
    }

    /// <summary>对应 Go: <c>validateCanonicalAgentContent</c>。</summary>
    public static void ValidateCanonicalAgentContent(JsonElement content)
    {
        // Go: 字符串合法；nil 内容会在类型分支报"消息内容无效"。
        if (content.ValueKind == JsonValueKind.String)
        {
            return;
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("画布 Agent 消息内容无效");
        }
        foreach (JsonElement partElement in content.EnumerateArray())
        {
            if (partElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("画布 Agent 消息内容无效");
            }
            string kind = Str(partElement.EnumerateObject(), "type");
            switch (kind)
            {
                case "text":
                    if (!TryGetProperty(partElement, "text", out JsonElement text)
                        || text.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException("画布 Agent 文本内容无效");
                    }
                    break;
                case "image_url" or "file_url":
                    {
                        if (!TryGetProperty(partElement, kind, out JsonElement file)
                            || file.ValueKind != JsonValueKind.Object)
                        {
                            throw new InvalidOperationException("画布 Agent 消息内容无效");
                        }
                        if (Str(file.EnumerateObject(), "url").Length == 0)
                        {
                            throw new InvalidOperationException("画布 Agent 素材地址为空");
                        }
                        if (kind == "file_url"
                            && (Str(file.EnumerateObject(), "name").Length == 0
                                || Str(file.EnumerateObject(), "mimeType").Length == 0))
                        {
                            throw new InvalidOperationException("画布 Agent 文件缺少名称或类型");
                        }
                        break;
                    }
                default:
                    throw new InvalidOperationException("画布 Agent 消息内容类型无效");
            }
        }
    }

    /// <summary>对应 Go: <c>canonicalAgentDataURL</c>。</summary>
    public static (string MIMEType, string Data, bool Ok) CanonicalAgentDataURL(string value)
    {
        if (!value.StartsWith("data:", StringComparison.Ordinal))
        {
            return ("", "", false);
        }
        string rest = value["data:".Length..];
        int separator = rest.IndexOf(";base64,", StringComparison.Ordinal);
        if (separator < 0)
        {
            return ("", "", false);
        }
        string mimeType = rest[..separator];
        string data = rest[(separator + ";base64,".Length)..];
        return (mimeType, data, mimeType.Length > 0 && data.Length > 0);
    }

    /// <summary>对应 Go: <c>canonicalAgentText</c>。</summary>
    public static string CanonicalAgentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            return "";
        }
        List<string> texts = [];
        foreach (JsonElement partElement in content.EnumerateArray())
        {
            if (partElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string? type = null;
            JsonElement imageUrl = default;
            JsonElement fileUrl = default;
            foreach (JsonProperty property in partElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "type":
                        type = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                        break;
                    case "image_url" when property.Value.ValueKind == JsonValueKind.Object:
                        imageUrl = property.Value;
                        break;
                    case "file_url" when property.Value.ValueKind == JsonValueKind.Object:
                        fileUrl = property.Value;
                        break;
                }
            }
            if (type == "text")
            {
                if (partElement.TryGetProperty("text", out JsonElement text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    texts.Add(text.GetString() ?? "");
                }
            }
            else if (imageUrl.ValueKind == JsonValueKind.Object)
            {
                if (imageUrl.TryGetProperty("url", out JsonElement url)
                    && url.ValueKind == JsonValueKind.String)
                {
                    texts.Add(url.GetString() ?? "");
                }
            }
            else if (fileUrl.ValueKind == JsonValueKind.Object)
            {
                string name = fileUrl.TryGetProperty("name", out JsonElement n)
                    && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                string url2 = fileUrl.TryGetProperty("url", out JsonElement u)
                    && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
                texts.Add(name + ": " + url2);
            }
        }
        return string.Join("\n", texts);
    }

    /// <summary>字符串解析为 JSON 值；解析失败或非字符串原样返回。对应 Go: <c>canonicalAgentJSONValue</c>。</summary>
    public static object? JSONStringToAny(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            try
            {
                return JsonToObject(JsonSerializer.Deserialize<JsonElement>(value.GetString() ?? "null"));
            }
            catch (JsonException)
            {
                return value.GetString();
            }
        }
        return JsonToObject(value);
    }

    /// <summary>对应 Go: <c>canonicalAgentJSONObject</c>。</summary>
    public static Dictionary<string, object?> JSONValueToObject(object? value)
    {
        if (JSONStringToAny(AsElement(value)) is Dictionary<string, object?> parsed)
        {
            return parsed;
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>对应 Go: <c>canonicalAgentToolCalls</c>。</summary>
    public static List<Dictionary<string, object?>>? CanonicalAgentToolCalls(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        List<Dictionary<string, object?>> calls = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (JsonToObject(item) is Dictionary<string, object?> call)
            {
                calls.Add(call);
            }
            else
            {
                return null;
            }
        }
        return calls;
    }

    // ------------------------------------------------------------ JSON 工具

    /// <summary>JsonElement/CLR 混合值转 JsonElement（供递归转换入口使用）。</summary>
    private static JsonElement AsElement(object? value) => value switch
    {
        JsonElement element => element,
        string text => JsonSerializer.SerializeToElement(text),
        _ => default,
    };

    /// <summary>把任意 JsonElement 转成 Go map/slice 风格的 CLR 对象图。</summary>
    public static object? JsonToObject(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                // 保留原始字面量（Go 的 float64 会在整数 Token 数上丢精度）。
                return element.Clone();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Object:
                Dictionary<string, object?> result = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    result[property.Name] = JsonToObject(property.Value);
                }
                return result;
            case JsonValueKind.Array:
                List<object?> items = [];
                foreach (JsonElement item in element.EnumerateArray())
                {
                    items.Add(JsonToObject(item));
                }
                return items;
            default:
                return null;
        }
    }

    internal static string Str(Dictionary<string, JsonElement> message, string key) =>
        message.TryGetValue(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    internal static string Str(JsonElement.ObjectEnumerator enumerator, string key)
    {
        foreach (JsonProperty property in enumerator)
        {
            if (property.Name == key && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString() ?? "";
            }
        }
        return "";
    }

    private static bool TryGetProperty(JsonElement element, string key, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return element.TryGetProperty(key, out value);
        }
        value = default;
        return false;
    }

    internal static JsonElement Element(Dictionary<string, JsonElement> message, string key) =>
        message.TryGetValue(key, out JsonElement value) ? value : default;

    /// <summary>取字段并转为 CLR 值（字符串直接取，其余按 JsonElement 转换）。</summary>
    internal static object? Value(Dictionary<string, JsonElement> message, string key) =>
        JsonToObject(Element(message, key));

    internal static object? Value(JsonElement element) => JsonToObject(element);

    private static string ToStringOrEmpty(object? value) => value as string ?? "";
}
