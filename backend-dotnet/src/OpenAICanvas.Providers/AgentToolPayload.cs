#nullable enable
using System.Text;
using System.Text.Json;
using OpenAICanvas.Outbound;
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Providers;

/// <summary>
/// 非流式 Agent 响应解析（三种 wire 协议的统一出参）。
/// 对应 Go: <c>internal/app/provider_text.go</c> 的 <c>parseAgentToolPayload</c>。
/// </summary>
/// <remarks>
/// 统一出参形态：<c>{ mode: "text", text, toolCalls: [], reasoning? }</c>。
/// </remarks>
public static class AgentToolPayload
{
    private const string ResponsesProtocol = "responses";
    private const string ClaudeProtocol = "claude-api";

    /// <summary>
    /// 解析上游响应为统一结果。
    /// 对应 Go: <c>parseAgentToolPayload</c>。
    /// </summary>
    public static Dictionary<string, object?> Parse(IReadOnlyDictionary<string, object?> payload, string protocol)
    {
        // 与 Go 一致：先做业务失败校验，再按协议解析。
        Exception? validation = StreamingAgentParser.ValidateTextPayload(payload);
        if (validation is not null)
        {
            throw validation;
        }
        if (protocol == ResponsesProtocol)
        {
            return ParseResponses(payload);
        }
        if (protocol == ClaudeProtocol)
        {
            return ParseClaude(payload);
        }
        return ParseChatCompletions(payload);
    }

    /// <summary>对应 Go 中 <c>protocol == "responses"</c> 的分支。</summary>
    private static Dictionary<string, object?> ParseResponses(IReadOnlyDictionary<string, object?> payload)
    {
        Dictionary<string, object?> result = NewResult();
        result["text"] = ProviderHelpers.FirstNonEmpty(
            ProviderHelpers.StringField(payload, "output_text"), ExtractResponseText(payload));
        string reasoning = ExtractResponseReasoning(payload);
        if (reasoning.Length > 0)
        {
            result["reasoning"] = reasoning;
        }

        List<object?> calls = [];
        foreach (object? value in StreamingAgentParser.InterfaceSlice(payload, "output"))
        {
            if (value is not Dictionary<string, object?> item)
            {
                continue;
            }
            if (ProviderHelpers.StringField(item, "type") != "function_call")
            {
                continue;
            }
            calls.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = ProviderHelpers.FirstNonEmpty(
                    ProviderHelpers.StringField(item, "call_id"), ProviderHelpers.StringField(item, "id")),
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = ProviderHelpers.StringField(item, "name"),
                    ["arguments"] = ProviderHelpers.StringField(item, "arguments"),
                },
            });
        }
        result["toolCalls"] = calls;
        return result;
    }

    /// <summary>对应 Go 中 <c>protocol == "claude-api"</c> 的分支。</summary>
    private static Dictionary<string, object?> ParseClaude(IReadOnlyDictionary<string, object?> payload)
    {
        Dictionary<string, object?> result = NewResult();
        StringBuilder text = new();
        StringBuilder reasoning = new();
        List<object?> calls = [];

        foreach (object? value in StreamingAgentParser.InterfaceSlice(payload, "content"))
        {
            if (value is not Dictionary<string, object?> item)
            {
                continue;
            }
            switch (ProviderHelpers.StringField(item, "type"))
            {
                case "text":
                    text.Append(ProviderHelpers.StringField(item, "text"));
                    break;
                case "thinking":
                    reasoning.Append(ProviderHelpers.FirstNonEmpty(
                        ProviderHelpers.StringField(item, "thinking"),
                        ProviderHelpers.StringField(item, "text")));
                    break;
                case "tool_use":
                {
                    string arguments = "null";
                    if (item.TryGetValue("input", out object? input))
                    {
                        arguments = JsonSerializer.Serialize(input, ProtocolJson.WriteOptions);
                    }
                    calls.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"] = ProviderHelpers.StringField(item, "id"),
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["name"] = ProviderHelpers.StringField(item, "name"),
                            ["arguments"] = arguments,
                        },
                    });
                    break;
                }
            }
        }

        result["text"] = text.ToString();
        if (reasoning.Length > 0)
        {
            result["reasoning"] = reasoning.ToString();
        }
        result["toolCalls"] = calls;
        if (text.Length == 0 && calls.Count == 0)
        {
            throw new InvalidOperationException("Claude Agent 接口没有返回内容");
        }
        return result;
    }

    /// <summary>对应 Go 的 chat-completion 兜底分支。</summary>
    private static Dictionary<string, object?> ParseChatCompletions(IReadOnlyDictionary<string, object?> payload)
    {
        List<object?> choices = StreamingAgentParser.InterfaceSlice(payload, "choices");
        if (choices.Count == 0)
        {
            throw new InvalidOperationException("画布 Agent 接口没有返回 choices");
        }
        Dictionary<string, object?>? choice = choices[0] as Dictionary<string, object?>;
        Dictionary<string, object?>? message = JsonFields.NestedObject(choice, "message");
        if (message is null)
        {
            throw new InvalidOperationException("画布 Agent 接口没有返回 choices");
        }

        Dictionary<string, object?> result = NewResult();
        result["text"] = ProviderHelpers.StringField(message, "content");
        string reasoning = ProviderHelpers.FirstNonEmpty(
            ProviderHelpers.StringField(message, "reasoning_content"),
            ProviderHelpers.StringField(message, "reasoning"));
        if (reasoning.Length > 0)
        {
            result["reasoning"] = reasoning;
        }

        List<object?> calls = [];
        foreach (object? value in StreamingAgentParser.InterfaceSlice(message, "tool_calls"))
        {
            if (value is not Dictionary<string, object?> item)
            {
                continue;
            }
            Dictionary<string, object?>? function = JsonFields.NestedObject(item, "function");
            calls.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = ProviderHelpers.StringField(item, "id"),
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = ProviderHelpers.StringField(function, "name"),
                    ["arguments"] = ProviderHelpers.StringField(function, "arguments"),
                },
            });
        }
        result["toolCalls"] = calls;
        return result;
    }

    private static Dictionary<string, object?> NewResult() => new(StringComparer.Ordinal)
    {
        ["mode"] = "text",
        ["text"] = "",
        ["toolCalls"] = new List<object?>(),
    };

    /// <summary>
    /// 从 Responses 载荷提取文本：优先 <c>output_text</c>，否则遍历 <c>output</c> 的 message 段落。
    /// 对应 Go: <c>extractResponseText</c>。
    /// </summary>
    internal static string ExtractResponseText(IReadOnlyDictionary<string, object?>? payload)
    {
        if (payload is null)
        {
            return "";
        }
        StringBuilder builder = new();
        foreach (object? value in StreamingAgentParser.InterfaceSlice(payload, "output"))
        {
            if (value is not Dictionary<string, object?> item)
            {
                continue;
            }
            foreach (object? contentValue in StreamingAgentParser.InterfaceSlice(item, "content"))
            {
                if (contentValue is not Dictionary<string, object?> content)
                {
                    continue;
                }
                string type = ProviderHelpers.StringField(content, "type");
                if (type is "output_text" or "text")
                {
                    builder.Append(ProviderHelpers.StringField(content, "text"));
                }
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// 从 Responses 载荷提取推理内容（reasoning summary / content 段落）。
    /// 对应 Go: <c>extractResponseReasoning</c>。
    /// </summary>
    internal static string ExtractResponseReasoning(IReadOnlyDictionary<string, object?>? payload)
    {
        if (payload is null)
        {
            return "";
        }
        StringBuilder builder = new();
        foreach (object? value in StreamingAgentParser.InterfaceSlice(payload, "output"))
        {
            if (value is not Dictionary<string, object?> item)
            {
                continue;
            }
            if (ProviderHelpers.StringField(item, "type") != "reasoning")
            {
                continue;
            }
            foreach (object? summaryValue in StreamingAgentParser.InterfaceSlice(item, "summary"))
            {
                if (summaryValue is Dictionary<string, object?> summary)
                {
                    builder.Append(ProviderHelpers.StringField(summary, "text"));
                }
            }
            foreach (object? contentValue in StreamingAgentParser.InterfaceSlice(item, "content"))
            {
                if (contentValue is Dictionary<string, object?> content)
                {
                    builder.Append(ProviderHelpers.StringField(content, "text"));
                }
            }
        }
        return builder.ToString();
    }
}
