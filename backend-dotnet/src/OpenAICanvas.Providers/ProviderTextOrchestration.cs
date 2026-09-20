#nullable enable
using System.Text.Json;

namespace OpenAICanvas.Providers;

/// <summary>
/// 文本协议的编排层（请求体构造、思考模式开关、结果整形、协议分发）。
/// 对应 Go: <c>internal/app/provider_text.go</c> 中不依赖真实网络的纯函数部分。
/// </summary>
/// <remarks>
/// 传输层（<c>postJSON</c> / <c>postStreamingBinary</c>）依赖渠道并发、熔断与计费审计，
/// 属 4.10，本类只负责<b>可独立断言的编排语义</b>。
/// </remarks>
public static class ProviderTextOrchestration
{
    public const string ChatCompletionProtocol = "chat-completion";
    public const string ResponsesProtocol = "responses";
    public const string ClaudeProtocol = "claude-api";

    /// <summary>
    /// 由渠道 <c>interfaceType</c> 归一为 wire 协议名。
    /// 对应 Go: <c>ChannelInterfaceOpenAIResponse</c> / <c>ChannelInterfaceClaudeAPI</c> 分支。
    /// </summary>
    /// <remarks>非文本协议（图/视频等）返回空串，由调用方走各自分支。</remarks>
    public static string ResolveProtocol(string? interfaceType) => (interfaceType ?? "").Trim() switch
    {
        ChatCompletionProtocol => ChatCompletionProtocol,
        "openai-response" => ResponsesProtocol,
        ClaudeProtocol => ClaudeProtocol,
        _ => "",
    };

    /// <summary>
    /// 思考模式开关：按协议写入对应的思考参数。
    /// 对应 Go: <c>applyTextThinking</c>。
    /// </summary>
    public static void ApplyTextThinking(
        IDictionary<string, object?> body, CanvasTextOptions options, string protocol)
    {
        if (!options.Thinking)
        {
            return;
        }
        switch (protocol)
        {
            case ResponsesProtocol:
                body["reasoning"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["effort"] = "medium",
                    ["summary"] = "auto",
                };
                break;
            case ChatCompletionProtocol:
                body["reasoning_effort"] = "medium";
                break;
            case ClaudeProtocol:
                body["thinking"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = 1024,
                };
                break;
        }
    }

    /// <summary>
    /// 归一化 Agent 工具选择：Chat Completions 在带工具时默认就是自动选择，
    /// 显式写 <c>auto</c> 只会降低兼容性（且思考模式与强制选择冲突）。
    /// 对应 Go: <c>normalizeAgentToolChoice</c>。
    /// </summary>
    public static void NormalizeAgentToolChoice(
        IDictionary<string, object?> body, CanvasTextOptions options, string protocol)
    {
        body.TryGetValue("tool_choice", out object? toolChoice);
        if (protocol == ChatCompletionProtocol
            && (options.Thinking || IsAutoAgentToolChoice(toolChoice)))
        {
            body.Remove("tool_choice");
        }
    }

    /// <summary>对应 Go: <c>isAutoAgentToolChoice</c>（必须是字符串 "auto"）。</summary>
    public static bool IsAutoAgentToolChoice(object? value) =>
        value is string text && text.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 输出长度上限：仅当上限为正时才写入指定字段。
    /// 对应 Go: <c>applyTextOutputLimit</c>。
    /// </summary>
    public static void ApplyTextOutputLimit(IDictionary<string, object?> body, int limit, string field)
    {
        if (limit > 0)
        {
            body[field] = limit;
        }
    }

    /// <summary>
    /// 为按 token 计费的流式 Chat Completions 请求开启最终 usage 块。
    /// 对应 Go: <c>ensureChatCompletionStreamUsage</c>。
    /// </summary>
    /// <remarks>
    /// <c>stream_options</c> 若存在但非对象，必须报错而不是覆盖 ——
    /// 调用方可能传了字符串或数组，静默覆盖会隐藏协议误用。
    /// </remarks>
    public static void EnsureChatCompletionStreamUsage(IDictionary<string, object?> body)
    {
        Dictionary<string, object?> options = new(StringComparer.Ordinal);
        if (body.TryGetValue("stream_options", out object? existing) && existing is not null)
        {
            if (existing is not Dictionary<string, object?> typed)
            {
                throw new InvalidOperationException("stream_options 必须是 JSON 对象");
            }
            options = typed;
        }
        options["include_usage"] = true;
        body["stream_options"] = options;
    }

    /// <summary>
    /// 把文本结果整形为任务载荷：<c>{ mode:"text", text, reasoning? }</c>。
    /// 对应 Go: <c>providerTextTaskResult</c>。
    /// </summary>
    /// <remarks>推理摘要为空（或仅空白）时不输出该字段，与 Go 的 <c>strings.TrimSpace</c> 判定一致。</remarks>
    public static Dictionary<string, object?> TextTaskResult(ProviderTextResult result)
    {
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["mode"] = "text",
            ["text"] = result.Text,
        };
        if (result.Reasoning.Trim().Length > 0)
        {
            payload["reasoning"] = result.Reasoning;
        }
        return payload;
    }

    /// <summary>
    /// 从统一解析结果提取文本结果，并做"空正文"校验。
    /// 对应 Go: <c>requestTextProvider</c> 的非流式分支。
    /// </summary>
    public static ProviderTextResult RequireText(Dictionary<string, object?> parsed, bool streaming)
    {
        ProviderTextResult result = new(
            ProviderHelpers.StringField(parsed, "text"),
            ProviderHelpers.StringField(parsed, "reasoning"));
        if (result.Text.Length == 0)
        {
            throw new InvalidOperationException(
                streaming ? "流式文本接口没有返回内容" : "文本接口没有返回内容");
        }
        return result;
    }

    /// <summary>
    /// 是否应把 Responses 回落为 Chat Completions。
    /// 对应 Go: <c>shouldFallbackTextToChat</c>。
    /// </summary>
    /// <remarks>
    /// 只在上游明确说路径不存在/不允许时才回落。<b>502/503/504 是瞬时故障</b>，
    /// 换协议修不好，反而会把真实上游故障伪装成"能力缺失后的第二次失败"。
    /// </remarks>
    public static bool ShouldFallbackTextToChat(Exception? error) =>
        error is ProviderHttpException { StatusCode: 404 or 405 or 501 };

    /// <summary>
    /// Agent 工具选择兼容性错误判定：正文或异常消息里出现 tool_choice / thinking mode 相关标识。
    /// 对应 Go: <c>isAgentToolChoiceCompatibilityError</c>。
    /// </summary>
    public static bool IsAgentToolChoiceCompatibilityError(Exception? error)
    {
        if (error is null)
        {
            return false;
        }
        string message = error.Message.ToLowerInvariant();
        if (error is ProviderPayloadException payloadError)
        {
            message += " " + payloadError.Raw.ToLowerInvariant();
        }
        if (error is ProviderHttpException httpError)
        {
            message += " " + httpError.Body.ToLowerInvariant();
        }
        return message.Contains("tool_choice", StringComparison.Ordinal)
            || message.Contains("tool choice", StringComparison.Ordinal)
            || message.Contains("tool-choice", StringComparison.Ordinal)
            || message.Contains("thinking mode", StringComparison.Ordinal);
    }

    /// <summary>
    /// 文本历史的过滤与归一：角色只允许 user/assistant，内容去空白后非空。
    /// 对应 Go: <c>validatedTextHistory</c>。
    /// </summary>
    public static List<Dictionary<string, object?>> ValidatedTextHistory(
        IEnumerable<ProviderTextMessage>? history)
    {
        List<Dictionary<string, object?>> result = [];
        foreach (ProviderTextMessage message in history ?? [])
        {
            string role = message.Role.Trim().ToLowerInvariant();
            string content = message.Content.Trim();
            if (role is not ("user" or "assistant") || content.Length == 0)
            {
                continue;
            }
            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = role,
                ["content"] = content,
            });
        }
        return result;
    }

    /// <summary>
    /// 从 JSON 文本解析出请求体对象；非对象返回 <c>null</c>。
    /// 对应 Go 的 <c>protocolBodyObject</c> 在文本场景的最小形态。
    /// </summary>
    public static Dictionary<string, object?>? BodyObject(object? body)
    {
        if (body is Dictionary<string, object?> typed)
        {
            return typed;
        }
        if (body is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            return OpenAICanvas.Outbound.JsonFields.FromElement(element) as Dictionary<string, object?>;
        }
        return null;
    }
}
