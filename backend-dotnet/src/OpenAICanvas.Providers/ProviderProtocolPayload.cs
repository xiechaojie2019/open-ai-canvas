#nullable enable
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Providers;

/// <summary>
/// 生成请求（声明式协议的输入契约）。
/// 对应 Go: <c>internal/protocol</c> 的 <c>GenerationRequest</c>。
/// </summary>
public sealed class ProtocolGenerationRequest
{
    public string Capability { get; set; } = "";
    public string Model { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Instructions { get; set; } = "";
    public List<MediaReference> Images { get; set; } = [];
    public List<MediaReference> Videos { get; set; } = [];
    public List<MediaReference> Audios { get; set; } = [];
    public List<MediaReference> Inputs { get; set; } = [];
    public List<ProtocolMessage> Messages { get; set; } = [];
    public string AspectRatio { get; set; } = "";
    public string Resolution { get; set; } = "";
    public string Quality { get; set; } = "";
    public bool GenerateAudio { get; set; }
    public bool Watermark { get; set; }
    public string Operation { get; set; } = "";
    public int Duration { get; set; }
    public int ImageCount { get; set; }
    public Dictionary<string, object?> Extra { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, object?>> ProviderOptions { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>对应 Go: <c>protocol.Message</c>。</summary>
public sealed class ProtocolMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>
/// 把画布生成输入投影为声明式协议请求。
/// 对应 Go: <c>provider_protocol.go</c> 的
/// <c>protocolRequestFromInput</c> / <c>protocolImageReferences</c> /
/// <c>protocolMediaReferences</c> / <c>protocolMediaReference</c>。
/// </summary>
public static class ProviderProtocolPayload
{
    /// <summary>对应 Go: <c>protocolRequestFromInput</c>。</summary>
    public static ProtocolGenerationRequest FromInput(TextTaskInput input)
    {
        Dictionary<string, object?> metadata = input.Metadata;
        ProviderConfig config = input.Config;

        ProtocolGenerationRequest request = new()
        {
            Capability = input.Mode,
            Model = config.Model,
            Prompt = input.Prompt,
            Instructions = config.SystemPrompt.Trim(),
            Images = ImageReferences(input),
            Videos = MediaReferences(input.ReferenceVideos, "video"),
            Audios = MediaReferences(input.ReferenceAudios, "audio"),
            AspectRatio = config.Size,
            Resolution = config.VQuality.Trim(),
            Quality = config.Quality,
            GenerateAudio = ParseBool(config.VideoGenerateAudio),
            Watermark = ParseBool(config.VideoWatermark),
            Operation = ProviderHelpers.FirstNonEmpty(
                ProviderHelpers.MetadataString(metadata, "videoEditOperation"),
                ProviderHelpers.MetadataString(metadata, "videoOperation")),
            Extra = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["videoSeconds"] = config.VideoSeconds,
                ["audioVoice"] = config.AudioVoice,
                ["audioFormat"] = config.AudioFormat,
                ["count"] = config.Count,
            },
        };

        foreach (ProviderTextMessage message in input.TextHistory)
        {
            string role = message.Role.Trim().ToLowerInvariant();
            if (role is not ("user" or "assistant" or "system"))
            {
                continue;
            }
            string content = message.Content.Trim();
            if (content.Length > 0)
            {
                request.Messages.Add(new ProtocolMessage { Role = role, Content = content });
            }
        }

        request.Inputs.AddRange(request.Images);
        request.Inputs.AddRange(request.Videos);
        request.Inputs.AddRange(request.Audios);

        if (input.MaxOutputTokens > 0)
        {
            request.Extra["max_output_tokens"] = input.MaxOutputTokens;
            request.Extra["max_tokens"] = input.MaxOutputTokens;
        }
        // 与 Go 一致：用 Atoi 的严格语义，非数字或非正数都不写入。
        int duration = ProviderHelpers.AtoiOrZero(config.VideoSeconds);
        if (duration > 0)
        {
            request.Duration = duration;
        }
        int count = ProviderHelpers.AtoiOrZero(config.Count);
        if (count > 0)
        {
            request.ImageCount = count;
        }

        if (metadata.TryGetValue("providerOptions", out object? configured)
            && configured is Dictionary<string, object?> namespaces)
        {
            foreach ((string namespaceName, object? raw) in namespaces)
            {
                if (raw is Dictionary<string, object?> options)
                {
                    request.ProviderOptions[namespaceName.Trim()] = options;
                }
            }
        }
        return request;
    }

    /// <summary>对应 Go: <c>protocolImageReferences</c>。</summary>
    public static List<MediaReference> ImageReferences(TextTaskInput input)
    {
        if (input.Mode == "video")
        {
            return VideoImageReferences(input);
        }
        List<MediaReference> result = [];
        for (int index = 0; index < input.ReferenceImages.Count; index++)
        {
            MediaReference item = MediaReferenceOf(input.ReferenceImages[index], "image", index);
            item.Role = input.Mode == "image" ? "edit_source" : "reference_image";
            if (item.URL.Length > 0 || item.DataURL.Length > 0)
            {
                result.Add(item);
            }
        }
        if (input.Mask is not null)
        {
            MediaReference mask = MediaReferenceOf(input.Mask, "image", result.Count);
            mask.Role = "mask";
            if (mask.URL.Length > 0 || mask.DataURL.Length > 0)
            {
                result.Add(mask);
            }
        }
        return result;
    }

    /// <summary>
    /// 视频参考图的角色判定。对应 Go: <c>protocolVideoImageReferences</c>。
    /// </summary>
    private static List<MediaReference> VideoImageReferences(TextTaskInput input)
    {
        List<MediaReference> result = [];
        // 只有声明了首/尾帧节点时，未显式标注的图才回落为 reference_image。
        string fallbackRole = "";
        if (ProviderHelpers.MetadataString(input.Metadata, "videoStartFrameNodeId").Length > 0
            || ProviderHelpers.MetadataString(input.Metadata, "videoEndFrameNodeId").Length > 0)
        {
            fallbackRole = "reference_image";
        }
        for (int index = 0; index < input.ReferenceImages.Count; index++)
        {
            MediaReference item = MediaReferenceOf(input.ReferenceImages[index], "image", index);
            item.Role = VideoImageRoleOr(input, input.ReferenceImages[index], fallbackRole);
            if (item.URL.Length > 0 || item.DataURL.Length > 0)
            {
                result.Add(item);
            }
        }
        return result;
    }

    /// <summary>
    /// 按节点 ID 与首/尾帧元数据判定参考图角色。
    /// 对应 Go: <c>videoImageRoleOrDefault</c>。
    /// </summary>
    private static string VideoImageRoleOr(TextTaskInput input, ProviderMedia value, string fallback)
    {
        string startNode = ProviderHelpers.MetadataString(input.Metadata, "videoStartFrameNodeId");
        string endNode = ProviderHelpers.MetadataString(input.Metadata, "videoEndFrameNodeId");
        if (value.ID.Length > 0)
        {
            if (startNode.Length > 0 && value.ID == startNode)
            {
                return "start_frame";
            }
            if (endNode.Length > 0 && value.ID == endNode)
            {
                return "end_frame";
            }
        }
        return fallback;
    }

    /// <summary>对应 Go: <c>protocolMediaReferences</c>。</summary>
    public static List<MediaReference> MediaReferences(IEnumerable<ProviderMedia> values, string kind)
    {
        List<MediaReference> result = [];
        int index = 0;
        foreach (ProviderMedia value in values)
        {
            MediaReference item = MediaReferenceOf(value, kind, index++);
            if (kind == "video")
            {
                item.Role = "reference_video";
            }
            else if (kind == "audio")
            {
                item.Role = "reference_audio";
            }
            if (item.URL.Length > 0 || item.DataURL.Length > 0)
            {
                result.Add(item);
            }
        }
        return result;
    }

    /// <summary>对应 Go: <c>protocolMediaReference</c>。</summary>
    public static MediaReference MediaReferenceOf(ProviderMedia value, string kind, int order) => new()
    {
        ID = (value.ID ?? "").Trim(),
        URL = (value.URL ?? "").Trim(),
        DataURL = (value.DataURL ?? "").Trim(),
        Type = kind,
        MIMEType = ProviderHelpers.FirstNonEmpty((value.MIMEType ?? "").Trim(), (value.Type ?? "").Trim()),
        Name = (value.Name ?? "").Trim(),
        StorageKey = (value.StorageKey ?? "").Trim(),
        Bytes = value.Bytes,
        Width = value.Width,
        Height = value.Height,
        DurationMs = value.DurationMs,
        Order = order,
        Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["bytes"] = value.Bytes,
            ["width"] = value.Width,
            ["height"] = value.Height,
            ["durationMs"] = value.DurationMs,
            ["storageKey"] = (value.StorageKey ?? "").Trim(),
        },
    };

    /// <summary>
    /// 结果整形为任务载荷。对应 Go: <c>finishProtocolResult</c>。
    /// </summary>
    /// <remarks>
    /// 文本模式只输出 <c>text</c>（推理摘要非空时才加 <c>reasoning</c>）；
    /// 媒体模式输出 <c>dataUrl</c> 与 <c>mimeType</c>，视频/音频取首项（对应 Go 的 <c>items[0]</c>）。
    /// </remarks>
    public static Dictionary<string, object?> FinishResult(
        string mode, ProtocolResult result, IReadOnlyList<ProtocolMediaItem> mediaItems)
    {
        if (mode == "text")
        {
            Dictionary<string, object?> output = new(StringComparer.Ordinal)
            {
                ["mode"] = "text",
                ["text"] = result.Text,
            };
            if (result.Reasoning.Trim().Length > 0)
            {
                output["reasoning"] = result.Reasoning;
            }
            return output;
        }

        List<Dictionary<string, object?>> items =
            [.. mediaItems.Select(item => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["dataUrl"] = item.DataURL,
                ["mimeType"] = item.MIMEType,
            })];

        return mode switch
        {
            "image" => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mode"] = "image",
                ["images"] = items,
            },
            "video" => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mode"] = "video",
                ["video"] = items[0],
            },
            _ => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mode"] = "audio",
                ["audio"] = items[0],
            },
        };
    }

    /// <summary>结果是否已带输出。对应 Go: <c>protocolResultHasOutput</c>。</summary>
    public static bool ResultHasOutput(string mode, ProtocolResult? result) => mode switch
    {
        "text" => result is not null && result.Text.Trim().Length > 0,
        "image" => result is { Images.Count: > 0 },
        "video" => result is { Videos.Count: > 0 },
        "audio" => result is { Audios.Count: > 0 },
        _ => false,
    };

    /// <summary>对应 Go: <c>protocolResultError</c>。</summary>
    public static InvalidOperationException ResultError(string message, string taskID)
    {
        string text = message.Trim();
        if (text.Length == 0)
        {
            text = "上游返回失败状态";
        }
        return taskID.Length == 0
            ? new InvalidOperationException(text)
            : new InvalidOperationException($"声明式协议任务失败（任务 {taskID}）：{text}");
    }

    /// <summary>
    /// 对应 Go: <c>parseBool</c>（只认 true/1/yes/on，其余为 false）。
    /// </summary>
    public static bool ParseBool(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        _ => false,
    };
}

/// <summary>声明式协议解析出的任务结果。对应 Go: <c>protocol.Result</c>。</summary>
public sealed class ProtocolResult
{
    public string Text { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public List<MediaReference> Images { get; set; } = [];
    public List<MediaReference> Videos { get; set; } = [];
    public List<MediaReference> Audios { get; set; } = [];
}

/// <summary>下载后的媒体条目。对应 Go 的 <c>dataUrl</c>/<c>mimeType</c> 二元组。</summary>
public sealed record ProtocolMediaItem(string DataURL, string MIMEType);
