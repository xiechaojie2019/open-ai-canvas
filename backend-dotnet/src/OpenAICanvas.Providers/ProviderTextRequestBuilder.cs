#nullable enable
using System.Text.Json;
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Providers;

/// <summary>参考素材输入（文本多模态）。对应 Go 的 <c>ReferenceImages</c> / <c>ReferenceVideos</c>。</summary>
public sealed class TextTaskInput
{
    public string Prompt { get; set; } = "";
    public ProviderConfig Config { get; set; } = new();
    public List<ProviderMedia> ReferenceImages { get; set; } = [];
    public List<ProviderMedia> ReferenceVideos { get; set; } = [];
    public List<ProviderTextMessage> TextHistory { get; set; } = [];
    public CanvasTextOptions TextOptions { get; set; } = new();
    public int MaxOutputTokens { get; set; }
}

/// <summary>
/// 三种文本协议的<b>请求体构造</b>与内容元素拼装。
/// 对应 Go: <c>provider_text.go</c> 的
/// <c>textResponseInput</c> / <c>textResponseContent</c> / <c>textChatContent</c> / <c>claudeTextContent</c>。
/// </summary>
public static class ProviderTextRequestBuilder
{
    /// <summary>
    /// Responses 协议的 <c>input</c>。
    /// 对应 Go: <c>textResponseInput</c>。
    /// </summary>
    /// <remarks>
    /// 无历史、无多模态素材时退化为单个系统+用户拼接的纯文本字符串 ——
    /// 这是与 Go 行为一致的关键分支，不要为"统一结构"而改成消息数组。
    /// </remarks>
    public static object TextResponseInput(TextTaskInput input)
    {
        if (input.TextHistory.Count == 0
            && input.ReferenceImages.Count == 0
            && input.ReferenceVideos.Count == 0)
        {
            return ProviderHelpers.WithSystemPrompt(input.Config.SystemPrompt, input.Prompt);
        }

        List<Dictionary<string, object?>> messages = [];
        string systemPrompt = input.Config.SystemPrompt.Trim();
        if (systemPrompt.Length > 0)
        {
            messages.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = "system",
                ["content"] = systemPrompt,
            });
        }
        messages.AddRange(ProviderTextOrchestration.ValidatedTextHistory(input.TextHistory));
        messages.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = "user",
            ["content"] = TextResponseContent(input),
        });
        return messages;
    }

    /// <summary>Responses 协议的多模态内容块。对应 Go: <c>textResponseContent</c>。</summary>
    public static List<Dictionary<string, object?>> TextResponseContent(TextTaskInput input)
    {
        List<Dictionary<string, object?>> content =
        [
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "input_text",
                ["text"] = input.Prompt,
            },
        ];
        foreach (ProviderMedia image in input.ReferenceImages)
        {
            content.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "input_image",
                ["image_url"] = ProviderHelpers.OpenAIImageInputURL(image),
            });
        }
        foreach (ProviderMedia video in input.ReferenceVideos)
        {
            content.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "input_video",
                ["video_url"] = ProviderHelpers.OpenAIVideoInputURL(video),
            });
        }
        return content;
    }

    /// <summary>
    /// Chat Completions 的 user 消息内容：无多模态素材时是纯字符串。
    /// 对应 Go: <c>textChatContent</c>。
    /// </summary>
    public static object TextChatContent(TextTaskInput input)
    {
        if (input.ReferenceImages.Count == 0 && input.ReferenceVideos.Count == 0)
        {
            return input.Prompt;
        }
        List<Dictionary<string, object?>> content =
        [
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "text",
                ["text"] = input.Prompt,
            },
        ];
        foreach (ProviderMedia image in input.ReferenceImages)
        {
            content.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["url"] = ProviderHelpers.OpenAIImageInputURL(image),
                },
            });
        }
        foreach (ProviderMedia video in input.ReferenceVideos)
        {
            content.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "video_url",
                ["video_url"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["url"] = ProviderHelpers.OpenAIVideoInputURL(video),
                },
            });
        }
        return content;
    }

    /// <summary>
    /// Claude 的 user 消息内容：无参考图时是纯字符串。
    /// 对应 Go: <c>claudeTextContent</c>。
    /// </summary>
    public static object ClaudeTextContent(TextTaskInput input)
    {
        if (input.ReferenceImages.Count == 0)
        {
            return input.Prompt;
        }
        List<Dictionary<string, object?>> content =
        [
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "text",
                ["text"] = input.Prompt,
            },
        ];
        foreach (ProviderMedia image in input.ReferenceImages)
        {
            string value = ProviderHelpers.OpenAIImageInputURL(image);
            if (value.StartsWith("data:", StringComparison.Ordinal))
            {
                (string mimeType, string data, bool ok) = SplitDataUrl(value);
                if (!ok)
                {
                    throw new InvalidOperationException("Claude 参考图片 data URL 无效");
                }
                content.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "image",
                    ["source"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "base64",
                        ["media_type"] = mimeType,
                        ["data"] = data,
                    },
                });
            }
            else
            {
                content.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "image",
                    ["source"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "url",
                        ["url"] = value,
                    },
                });
            }
        }
        return content;
    }

    /// <summary>
    /// 拆分 <c>data:{mime};base64,{payload}</c>。
    /// 对应 Go: <c>splitDataURL</c>。
    /// </summary>
    /// <remarks>
    /// 必须要求 <c>;base64</c> 后缀且载荷非空；分隔符前的头部长度必须大于 <c>"data:"</c>，
    /// 否则 <c>data:,xxx</c> 这种无 MIME 的形态会被误认为合法。
    /// </remarks>
    public static (string MIMEType, string Data, bool Ok) SplitDataUrl(string value)
    {
        if (!value.StartsWith("data:", StringComparison.Ordinal))
        {
            return ("", "", false);
        }
        int separator = value.IndexOf(',');
        if (separator <= "data:".Length)
        {
            return ("", "", false);
        }
        string header = value["data:".Length..separator];
        if (!header.EndsWith(";base64", StringComparison.Ordinal))
        {
            return ("", "", false);
        }
        string data = value[(separator + 1)..];
        return (header[..^";base64".Length], data, data.Length > 0);
    }

    // ------------------------------------------------------------ 任务请求体

    /// <summary>Responses 协议请求体。对应 Go: <c>runResponsesTextTask</c> 的 body 构造。</summary>
    public static Dictionary<string, object?> ResponsesBody(TextTaskInput input)
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["model"] = input.Config.Model,
            ["input"] = TextResponseInput(input),
        };
        ProviderTextOrchestration.ApplyTextThinking(body, input.TextOptions, ProviderTextOrchestration.ResponsesProtocol);
        ProviderTextOrchestration.ApplyTextOutputLimit(body, input.MaxOutputTokens, "max_output_tokens");
        return body;
    }

    /// <summary>Chat Completions 协议请求体。对应 Go: <c>runChatCompletionsTextTask</c>。</summary>
    public static Dictionary<string, object?> ChatCompletionsBody(TextTaskInput input)
    {
        List<Dictionary<string, object?>> messages = [];
        string systemPrompt = input.Config.SystemPrompt.Trim();
        if (systemPrompt.Length > 0)
        {
            messages.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = "system",
                ["content"] = systemPrompt,
            });
        }
        messages.AddRange(ProviderTextOrchestration.ValidatedTextHistory(input.TextHistory));
        messages.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = "user",
            ["content"] = TextChatContent(input),
        });

        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["model"] = input.Config.Model,
            ["messages"] = messages,
        };
        ProviderTextOrchestration.ApplyTextThinking(body, input.TextOptions, ProviderTextOrchestration.ChatCompletionProtocol);
        ProviderTextOrchestration.ApplyTextOutputLimit(body, input.MaxOutputTokens, "max_tokens");
        return body;
    }

    /// <summary>Claude 协议请求体。对应 Go: <c>runClaudeTextTask</c>。</summary>
    public static Dictionary<string, object?> ClaudeBody(TextTaskInput input)
    {
        if (input.ReferenceVideos.Count > 0)
        {
            throw new InvalidOperationException("Claude API 当前不支持视频参考输入");
        }
        List<Dictionary<string, object?>> messages =
            [.. ProviderTextOrchestration.ValidatedTextHistory(input.TextHistory)];
        messages.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = "user",
            ["content"] = ClaudeTextContent(input),
        });

        // 与 Go 一致：默认 4096，显式上限为正时才覆盖。
        int maxTokens = 4096;
        if (input.MaxOutputTokens > 0)
        {
            maxTokens = input.MaxOutputTokens;
        }
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["model"] = input.Config.Model,
            ["max_tokens"] = maxTokens,
            ["messages"] = messages,
        };
        ProviderTextOrchestration.ApplyTextThinking(body, input.TextOptions, ProviderTextOrchestration.ClaudeProtocol);
        string systemPrompt = input.Config.SystemPrompt.Trim();
        if (systemPrompt.Length > 0)
        {
            body["system"] = systemPrompt;
        }
        return body;
    }

    /// <summary>按协议分发请求体构造。对应 Go: <c>runTextTask</c> 的三分支（不含声明式与 legacy 回落）。</summary>
    public static Dictionary<string, object?> BuildBody(TextTaskInput input, string protocol) => protocol switch
    {
        ProviderTextOrchestration.ResponsesProtocol => ResponsesBody(input),
        ProviderTextOrchestration.ClaudeProtocol => ClaudeBody(input),
        _ => ChatCompletionsBody(input),
    };

    /// <summary>把请求体序列化为 JSON 文本（用于出站发送）。</summary>
    public static string SerializeBody(Dictionary<string, object?> body) =>
        JsonSerializer.Serialize(body, ProtocolJson.WriteOptions);
}
