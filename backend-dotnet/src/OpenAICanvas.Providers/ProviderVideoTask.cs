#nullable enable
using System.Text;
using System.Text.Json;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Providers;

/// <summary>
/// 视频生成任务的渠道实现（遗留手写协议）。
/// 对应 Go: <c>internal/app/provider_video.go</c> 的 <c>runVideoTask</c> /
/// <c>runVideoTaskWithPolicy</c> / <c>runSeedanceVideosTask</c> /
/// <c>runSeedanceAgentPlanVideoTask</c>。
/// </summary>
/// <remarks>
/// <b>已有官方声明式插件的接口类型不在此处理</b>：调用方应先查适配器注册表，
/// 未安装插件时直接失败，绝不能偷偷退回这里的手写协议（路由顺序是协议边界）。
/// </remarks>
public sealed class ProviderVideoTask
{
    private readonly Func<HttpClient>? _clientFactory;
    private readonly IProviderRequestContext? _context;

    public ProviderVideoTask(IProviderRequestContext? context = null, Func<HttpClient>? clientFactory = null)
    {
        _context = context;
        _clientFactory = clientFactory;
    }

    /// <summary>
    /// 视频任务入口。对应 Go: <c>runVideoTaskWithPolicy</c>。
    /// </summary>
    /// <param name="resumedProviderRequestId">
    /// 恢复任务时已有的上游任务 ID；非空时<b>只查询不重建</b>
    /// （对应 Go 的 <c>resumedProviderRequestID(ctx)</c>）。
    /// </param>
    public async Task<Dictionary<string, object?>> RunAsync(
        TextTaskInput input,
        string resumedProviderRequestId = "",
        VideoPollPolicy? pollPolicy = null,
        CancellationToken cancellationToken = default)
    {
        VideoPollPolicy policy = ProviderVideoPolling.Normalize(pollPolicy);
        if (input.Mode.Length == 0)
        {
            input.Mode = "video";
        }

        if (IsArkPlanVideo(input.Config))
        {
            return await RunSeedanceAgentPlanAsync(input, resumedProviderRequestId, policy, cancellationToken)
                .ConfigureAwait(false);
        }
        if (IsSeedanceVideo(input.Config))
        {
            return await RunSeedanceVideosAsync(input, resumedProviderRequestId, policy, cancellationToken)
                .ConfigureAwait(false);
        }
        if (input.ReferenceVideos.Count > 0 || input.ReferenceAudios.Count > 0)
        {
            throw new InvalidOperationException(
                "OpenAI 风格视频接口不支持参考视频或参考音频，请切换到 Seedance / Agent Plan 渠道");
        }
        return await RunOpenAiStyleAsync(input, resumedProviderRequestId, policy, cancellationToken)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------ OpenAI 风格

    /// <summary>对应 Go: <c>runVideoTaskWithPolicy</c> 的通用分支。</summary>
    private async Task<Dictionary<string, object?>> RunOpenAiStyleAsync(
        TextTaskInput input,
        string resumedProviderRequestId,
        VideoPollPolicy policy,
        CancellationToken cancellationToken)
    {
        string id = resumedProviderRequestId.Trim();
        Dictionary<string, object?> created = [];

        if (id.Length == 0)
        {
            if (IsGrokVideo(input.Config))
            {
                created = await PostJsonAsync(
                    input.Config, "/videos", GrokVideoBody(input), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                (byte[] payload, string contentType) = BuildNewApiVideoForm(input);
                created = await PostAsync(
                    input.Config, "/videos", payload, contentType, cancellationToken).ConfigureAwait(false);
            }
        }

        if (id.Length == 0)
        {
            (id, Exception? idError) = ExtractTaskId(created, "id", "request_id", "task_id");
            if (idError is not null)
            {
                throw new InvalidOperationException($"视频接口任务 ID 无效：{idError.Message}");
            }
        }
        if (id.Length == 0 && JsonFields.NestedObject(created, "data") is { } data)
        {
            (id, Exception? dataError) = ExtractTaskId(data, "id", "request_id", "task_id");
            if (dataError is not null)
            {
                throw new InvalidOperationException($"视频接口任务 ID 无效：{dataError.Message}");
            }
        }
        if (id.Length == 0)
        {
            throw new InvalidOperationException("视频接口没有返回任务 ID");
        }

        return await ProviderVideoPolling.RunPollLoopAsync(
            id,
            policy,
            async token =>
            {
                Dictionary<string, object?> state = await GetJsonAsync(
                    input.Config, "/videos/" + id, token).ConfigureAwait(false);
                if (JsonFields.NestedObject(state, "data") is { } nested)
                {
                    state = nested;
                }
                string status = JsonFields.StringField(state, "status").ToLowerInvariant();
                if (status is "completed" or "succeeded" or "success" or "done")
                {
                    string videoUrl = NewApiVideoResultUrl(state);
                    if (videoUrl.Length > 0)
                    {
                        (byte[] data, string mimeType) = await ProviderVideoPolling.RunDownloadAsync(
                            id, policy,
                            t => GetExternalBinaryAsync(input.Config, videoUrl, t),
                            token).ConfigureAwait(false);
                        return new VideoPollOutcome(true, VideoResult(mimeType, data));
                    }
                    (byte[] fallback, string fallbackMime) = await ProviderVideoPolling.RunDownloadAsync(
                        id, policy,
                        t => GetBinaryAsync(input.Config, "/videos/" + id + "/content", t),
                        token).ConfigureAwait(false);
                    return new VideoPollOutcome(true, VideoResult(fallbackMime, fallback));
                }
                if (status is "failed" or "cancelled")
                {
                    throw new InvalidOperationException("视频生成失败");
                }
                return new VideoPollOutcome(false, null);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 构造新 API 的视频 <c>multipart</c> 请求体。
    /// 对应 Go 的 <c>writeField</c> 序列 + <c>writeMediaPart</c>。
    /// </summary>
    private static (byte[] Payload, string ContentType) BuildNewApiVideoForm(TextTaskInput input)
    {
        const string boundary = "----OpenAICanvasVideoFormBoundary";
        using MemoryStream stream = new();

        void WriteField(string name, string value) =>
            ProviderMultipart.WriteField(stream, boundary, name, value);

        WriteField("model", input.Config.Model);
        WriteField("prompt", input.Prompt.Trim());
        WriteField("seconds", ProviderMediaCodec.DefaultString(input.Config.VideoSeconds, "6"));
        string size = ProviderImageOptions.NormalizeVideoSize(input.Config.Size);
        if (size.Length > 0)
        {
            WriteField("size", size);
        }
        string resolution = ProviderVideoOptions.VideoResolutionNameRequest(
            input.VideoCapability, input.Config.VQuality);
        if (resolution.Length > 0)
        {
            WriteField("resolution_name", resolution);
        }
        WriteField("preset", "normal");
        if (ProviderVideoOptions.ShouldSendNewApiVideoImages(input.Metadata, input.ReferenceImages))
        {
            foreach (ProviderMedia image in input.ReferenceImages)
            {
                ProviderMultipart.WriteMedia(stream, boundary, "input_reference[]", image);
            }
        }
        ProviderMultipart.Close(stream, boundary);
        return (stream.ToArray(), "multipart/form-data; boundary=" + boundary);
    }

    /// <summary>对应 Go: <c>newAPIVideoResultURL</c> / <c>nestedNewAPIVideoResultURL</c>。</summary>
    public static string NewApiVideoResultUrl(Dictionary<string, object?> state) =>
        NestedNewApiVideoResultUrl(state, allowResultUrl: false, depth: 0);

    private static string NestedNewApiVideoResultUrl(
        Dictionary<string, object?> payload, bool allowResultUrl, int depth)
    {
        // 只下钻两层：更深的结构属于上游私有扩展，猜测式递归会误取无关 URL。
        if (depth < 2)
        {
            foreach (string key in new[] { "data", "result", "video" })
            {
                if (JsonFields.NestedObject(payload, key) is { } nested
                    && NestedNewApiVideoResultUrl(nested, true, depth + 1) is { Length: > 0 } found)
                {
                    return found;
                }
            }
        }
        List<string> keys = ["video_url", "videoUrl", "url"];
        if (allowResultUrl)
        {
            keys.Add("result_url");
            keys.Add("resultUrl");
        }
        foreach (string key in keys)
        {
            string value = JsonFields.StringField(payload, key).Trim();
            if (ProviderHelpers.IsPublicMediaURL(value))
            {
                return value;
            }
        }
        return "";
    }

    /// <summary>对应 Go: <c>grokVideoBody</c>。</summary>
    public static Dictionary<string, object?> GrokVideoBody(TextTaskInput input)
    {
        string seconds = ProviderMediaCodec.DefaultString(input.Config.VideoSeconds, "6");
        int duration = int.TryParse(seconds, out int parsed) && parsed > 0 ? parsed : 6;

        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["model"] = input.Config.Model,
            ["prompt"] = input.Prompt.Trim(),
            ["duration"] = duration,
            ["seconds"] = duration.ToString(),
        };
        string size = ProviderImageOptions.NormalizeVideoSize(input.Config.Size);
        if (size.Length > 0)
        {
            body["size"] = size;
        }
        if (ProviderVideoOptions.ShouldSendNewApiVideoImages(input.Metadata, input.ReferenceImages)
            && input.ReferenceImages.Count > 0)
        {
            List<string> images = [.. input.ReferenceImages.Select(ProviderHelpers.OpenAIImageInputURL)];
            // 同时给出单个与数组两种形态，兼容不同上游的字段解析。
            body["image"] = images[0];
            body["images"] = images;
        }
        return body;
    }

    // ------------------------------------------------------------ Seedance /videos

    /// <summary>对应 Go: <c>runSeedanceVideosTask</c>。</summary>
    private async Task<Dictionary<string, object?>> RunSeedanceVideosAsync(
        TextTaskInput input,
        string resumedProviderRequestId,
        VideoPollPolicy policy,
        CancellationToken cancellationToken)
    {
        // 已有 provider ID 时只能继续查询，绝不能重新 create，否则会产生第二个计费任务。
        string id = resumedProviderRequestId.Trim();
        if (id.Length == 0)
        {
            SeedanceVideosRequest body = SeedanceVideosBody(input);
            Dictionary<string, object?> created = await PostJsonAsync(
                input.Config, "/videos", body, cancellationToken).ConfigureAwait(false);
            if (JsonFields.NestedObject(created, "data") is { } data)
            {
                created = data;
            }
            (id, Exception? error) = ExtractTaskId(created, "id", "task_id");
            if (error is not null)
            {
                throw new InvalidOperationException($"Seedance 接口任务 ID 无效：{error.Message}");
            }
        }
        if (id.Length == 0)
        {
            throw new InvalidOperationException("Seedance 接口没有返回任务 ID");
        }

        return await ProviderVideoPolling.RunPollLoopAsync(
            id,
            policy,
            async token =>
            {
                Dictionary<string, object?> state = await GetJsonAsync(
                    input.Config, "/videos/" + id, token).ConfigureAwait(false);
                if (JsonFields.NestedObject(state, "data") is { } nested)
                {
                    state = nested;
                }
                string status = JsonFields.StringField(state, "status").ToLowerInvariant();
                if (status is "completed" or "succeeded")
                {
                    string videoUrl = JsonFields.StringField(state, "video_url");
                    if (videoUrl.Length > 0)
                    {
                        (byte[] data, string mimeType) = await ProviderVideoPolling.RunDownloadAsync(
                            id, policy,
                            t => GetExternalBinaryAsync(input.Config, videoUrl, t),
                            token).ConfigureAwait(false);
                        return new VideoPollOutcome(true, VideoResult(mimeType, data));
                    }
                    // 任务成功但未给 URL 时走备用内容端点。
                    (byte[] fallback, string fallbackMime) = await ProviderVideoPolling.RunDownloadAsync(
                        id, policy,
                        t => GetBinaryAsync(input.Config, "/videos/" + id + "/content", t),
                        token).ConfigureAwait(false);
                    return new VideoPollOutcome(true, VideoResult(fallbackMime, fallback));
                }
                if (status is "failed" or "cancelled" or "expired")
                {
                    string message = ProviderVideoOptions.SeedanceErrorMessage(state);
                    throw new InvalidOperationException(
                        message.Length > 0 ? message : "Seedance 视频生成失败");
                }
                return new VideoPollOutcome(false, null);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 构造 Seedance <c>/videos</c> 请求体。对应 Go: <c>seedanceVideosRequestBody</c>。
    /// </summary>
    /// <remarks>
    /// 参考视频/音频必须与至少 1 张参考图同用（上游以此锚定画面风格），
    /// 单发视频或音频会被直接拒绝，这里提前拦截。
    /// </remarks>
    public static SeedanceVideosRequest SeedanceVideosBody(TextTaskInput input)
    {
        if ((input.ReferenceVideos.Count > 0 || input.ReferenceAudios.Count > 0)
            && input.ReferenceImages.Count == 0)
        {
            throw new InvalidOperationException(
                "Seedance 参考视频或参考音频需要同时连接至少 1 张主参考图");
        }

        SeedanceVideosRequest body = new()
        {
            Model = input.Config.Model,
            Prompt = input.Prompt.Trim(),
            AspectRatio = ProviderVideoOptions.NormalizeSeedanceVideosRatio(input.Config.Size),
            Duration = ProviderVideoOptions.NormalizeSeedanceDuration(input.Config.VideoSeconds),
        };
        if (ProviderVideoOptions.VideoCapabilitySupportsAudio(input.VideoCapability))
        {
            body.GenerateAudio = ProviderVideoOptions.ParseBool(input.Config.VideoGenerateAudio, true);
        }

        List<string> imageUrls = [.. input.ReferenceImages.Select(ProviderHelpers.OpenAIImageInputURL)];
        List<string> frameImageUrls = ProviderVideoOptions.VideoFrameImageUrls(
            input.Metadata, input.ReferenceImages, imageUrls);

        string operation = ProviderHelpers.MetadataString(input.Metadata, "videoEditOperation");
        if (operation == "reference_to_video")
        {
            body.ReferenceImageURLs = imageUrls;
        }
        else if (frameImageUrls.Count > 0)
        {
            body.ImageURLs = frameImageUrls;
        }
        else if (imageUrls.Count > 0)
        {
            body.ImageURL = imageUrls[0];
            if (imageUrls.Count > 1)
            {
                body.ReferenceImageURLs = imageUrls[1..];
            }
        }

        if (input.ReferenceVideos.Count > 0)
        {
            body.ReferenceVideos = [.. input.ReferenceVideos.Select(ProviderVideoOptions.SeedanceVideosMediaUrl)];
        }
        if (input.ReferenceAudios.Count > 0)
        {
            body.ReferenceAudios = [.. input.ReferenceAudios.Select(ProviderVideoOptions.SeedanceVideosMediaUrl)];
        }
        return body;
    }

    // ------------------------------------------------------------ Seedance Agent Plan

    /// <summary>对应 Go: <c>runSeedanceAgentPlanVideoTask</c>。</summary>
    private async Task<Dictionary<string, object?>> RunSeedanceAgentPlanAsync(
        TextTaskInput input,
        string resumedProviderRequestId,
        VideoPollPolicy policy,
        CancellationToken cancellationToken)
    {
        string providerName = input.Config.InterfaceType == ChannelInterfaceType.ChannelInterfaceVolcengineArkVideo
            ? "火山方舟"
            : "Seedance";

        string id = resumedProviderRequestId.Trim();
        if (id.Length == 0)
        {
            List<Dictionary<string, object?>> content = SeedanceContent(input);
            if (input.Config.InterfaceType == ChannelInterfaceType.ChannelInterfaceVolcengineArkVideo)
            {
                // 方舟要求显式角色标注，否则参考图会被当作普通附件。
                foreach (Dictionary<string, object?> item in content)
                {
                    if (item.TryGetValue("type", out object? type) && type as string == "image_url")
                    {
                        item["role"] = "reference_image";
                    }
                }
            }
            SeedanceAgentPlanRequest body = new()
            {
                Model = input.Config.Model,
                Content = content,
                Ratio = ProviderVideoOptions.NormalizeSeedanceRatio(input.Config.Size),
                Resolution = ProviderVideoOptions.NormalizeSeedanceResolution(
                    input.Config.VQuality, input.Config.Model),
                Duration = ProviderVideoOptions.NormalizeSeedanceDuration(input.Config.VideoSeconds),
            };
            if (ProviderVideoOptions.VideoCapabilitySupportsAudio(input.VideoCapability))
            {
                body.GenerateAudio = ProviderVideoOptions.ParseBool(input.Config.VideoGenerateAudio, true);
            }
            if (ProviderVideoOptions.VideoCapabilitySupportsWatermark(input.VideoCapability))
            {
                body.Watermark = ProviderVideoOptions.ParseBool(input.Config.VideoWatermark, false);
            }

            Dictionary<string, object?> created = await PostJsonAsync(
                input.Config, "/contents/generations/tasks", body, cancellationToken).ConfigureAwait(false);
            if (JsonFields.NestedObject(created, "data") is { } data)
            {
                created = data;
            }
            (id, Exception? error) = ExtractTaskId(created, "id");
            if (error is not null)
            {
                throw new InvalidOperationException($"{providerName}接口任务 ID 无效：{error.Message}");
            }
        }
        if (id.Length == 0)
        {
            throw new InvalidOperationException($"{providerName}接口没有返回任务 ID");
        }

        return await ProviderVideoPolling.RunPollLoopAsync(
            id,
            policy,
            async token =>
            {
                Dictionary<string, object?> state = await GetJsonAsync(
                    input.Config, "/contents/generations/tasks/" + id, token).ConfigureAwait(false);
                if (JsonFields.NestedObject(state, "data") is { } nested)
                {
                    state = nested;
                }
                string status = JsonFields.StringField(state, "status").Trim().ToLowerInvariant();
                if (status == "succeeded")
                {
                    string videoUrl = JsonFields.StringField(
                        JsonFields.NestedObject(state, "content"), "video_url");
                    if (videoUrl.Length == 0)
                    {
                        throw new InvalidOperationException($"{providerName}任务成功但没有返回视频 URL");
                    }
                    (byte[] data, string mimeType) = await ProviderVideoPolling.RunDownloadAsync(
                        id, policy,
                        t => GetExternalBinaryAsync(input.Config, videoUrl, t),
                        token).ConfigureAwait(false);
                    return new VideoPollOutcome(true, VideoResult(mimeType, data));
                }
                if (status is "failed" or "cancelled" or "expired")
                {
                    throw new InvalidOperationException($"{providerName}视频生成失败");
                }
                return new VideoPollOutcome(false, null);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 组装 Agent Plan 的 <c>content</c> 多模态数组。
    /// 对应 Go: <c>seedanceContent</c>。
    /// </summary>
    public static List<Dictionary<string, object?>> SeedanceContent(TextTaskInput input)
    {
        List<Dictionary<string, object?>> content = [];
        string text = input.Prompt.Trim();
        if (text.Length > 0)
        {
            content.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "text",
                ["text"] = text,
            });
        }
        foreach (ProviderMedia image in input.ReferenceImages)
        {
            content.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["url"] = ProviderVideoOptions.MediaReferenceUrl(image),
                },
                ["role"] = ProviderVideoOptions.VideoImageRoleOrDefault(
                    input.Metadata, image, "reference_image"),
            });
        }
        foreach (ProviderMedia video in input.ReferenceVideos)
        {
            content.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "video_url",
                ["video_url"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["url"] = ProviderVideoOptions.MediaReferenceUrl(video),
                },
                ["role"] = "reference_video",
            });
        }
        foreach (ProviderMedia audio in input.ReferenceAudios)
        {
            content.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "audio_url",
                ["audio_url"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["url"] = ProviderVideoOptions.MediaReferenceUrl(audio),
                },
                ["role"] = "reference_audio",
            });
        }
        if (content.Count == 0)
        {
            throw new InvalidOperationException("请输入视频提示词或连接参考素材");
        }
        return content;
    }

    // ------------------------------------------------------------ 渠道判定

    /// <summary>
    /// 是否 Seedance <c>/videos</c> 渠道。对应 Go: <c>isSeedanceVideoConfig</c>。
    /// </summary>
    /// <remarks>
    /// <b>按模型名判定，不是按 interfaceType</b>：模型名含 <c>seedance</c> 即走该协议，
    /// 且 Agent Plan 渠道也隐含满足此条件（与 Go 的 <c>||</c> 短路一致）。
    /// </remarks>
    public static bool IsSeedanceVideo(ProviderConfig config)
    {
        string model = (config.Model ?? "").ToLowerInvariant();
        return model.Contains("seedance", StringComparison.Ordinal)
            || model.Contains("doubao-seedance", StringComparison.Ordinal)
            || IsArkPlanVideo(config);
    }

    /// <summary>
    /// 是否 Agent Plan（<c>/api/plan/v3</c> 基址）。对应 Go: <c>isArkPlanVideoConfig</c>。
    /// </summary>
    /// <remarks>按 <c>baseUrl</c> 是否包含 <c>/api/plan/v3</c> 判定。</remarks>
    public static bool IsArkPlanVideo(ProviderConfig config) =>
        (config.BaseURL ?? "").ToLowerInvariant().Contains("/api/plan/v3", StringComparison.Ordinal);

    /// <summary>对应 Go: <c>isGrokVideoConfig</c>（按模型名含 <c>grok</c> 判定）。</summary>
    public static bool IsGrokVideo(ProviderConfig config) =>
        (config.Model ?? "").ToLowerInvariant().Trim().Contains("grok", StringComparison.Ordinal);

    // ------------------------------------------------------------ 工具

    /// <summary>
    /// 按序取第一个非空字符串字段。对应 Go: <c>firstJSONString</c>。
    /// </summary>
    /// <returns>ID 与错误；类型不符时返回错误。</returns>
    public static (string Id, Exception? Error) ExtractTaskId(
        Dictionary<string, object?> payload, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (!payload.TryGetValue(key, out object? value) || value is null)
            {
                continue;
            }
            // 严格类型：数字型 ID 属于上游协议误用，静默转字符串会掩盖契约问题。
            if (value is string text)
            {
                return (text.Trim(), null);
            }
            return ("", new InvalidOperationException($"字段 {key} 不是字符串"));
        }
        return ("", null);
    }

    private static Dictionary<string, object?> VideoResult(string mimeType, byte[] data)
    {
        string normalized = ProviderMediaCodec.NormalizedMediaMimeType(mimeType, data);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = "video",
            ["video"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["dataUrl"] = ProviderHelpers.DataUrl(normalized, data),
                ["mimeType"] = normalized,
            },
        };
    }

    private Task<Dictionary<string, object?>> PostJsonAsync(
        ProviderConfig config, string path, object body, CancellationToken cancellationToken) =>
        PostAsync(
            config, path,
            Encoding.UTF8.GetBytes(OpenAICanvas.Protocol.ProtocolJson.Serialize(body)),
            "application/json",
            cancellationToken);

    private Task<Dictionary<string, object?>> PostAsync(
        ProviderConfig config, string path, byte[] payload, string contentType, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(HttpMethod.Post, ProviderTransport.ChannelApiUrl(config.BaseURL, path))
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        ProviderTransport.ApplyProviderAuth(request, config);
        ProviderTransport.ApplyDefaultHeaders(request);
        OutboundHttpClient.ApplyHeaders(request, config.Headers);
        return SendJsonAsync(request, cancellationToken);
    }

    private Task<Dictionary<string, object?>> GetJsonAsync(
        ProviderConfig config, string path, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(HttpMethod.Get, ProviderTransport.ChannelApiUrl(config.BaseURL, path));
        ProviderTransport.ApplyProviderAuth(request, config);
        ProviderTransport.ApplyDefaultHeaders(request);
        OutboundHttpClient.ApplyHeaders(request, config.Headers);
        return SendJsonAsync(request, cancellationToken);
    }

    /// <summary>下载渠道自身的二进制内容。对应 Go: <c>getBinary</c>。</summary>
    private Task<(byte[] Data, string MIMEType)> GetBinaryAsync(
        ProviderConfig config, string path, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(HttpMethod.Get, ProviderTransport.ChannelApiUrl(config.BaseURL, path));
        ProviderTransport.ApplyProviderAuth(request, config);
        ProviderTransport.ApplyDefaultHeaders(request);
        OutboundHttpClient.ApplyHeaders(request, config.Headers);
        return SendBinaryAsync(request, cancellationToken);
    }

    /// <summary>
    /// 下载外部（可能是第三方 CDN）的结果。
    /// 对应 Go: <c>getProviderExternalBinary</c> / <c>getExternalBinary</c>。
    /// </summary>
    /// <remarks><b>跨源不带渠道鉴权</b>，避免把密钥泄露给第三方 CDN。</remarks>
    private Task<(byte[] Data, string MIMEType)> GetExternalBinaryAsync(
        ProviderConfig config, string rawUrl, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(HttpMethod.Get, rawUrl);
        if (ProviderHelpers.IsSameProviderOrigin(config.BaseURL, rawUrl))
        {
            ProviderTransport.ApplyProviderAuth(request, config);
            OutboundHttpClient.ApplyHeaders(request, config.Headers);
        }
        ProviderTransport.ApplyDefaultHeaders(request);
        return SendBinaryAsync(request, cancellationToken);
    }

    private async Task<Dictionary<string, object?>> SendJsonAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            return await ProviderTransport.SendJsonAsync(
                request,
                _context?.MaxResponseBytes ?? ProviderTransport.DefaultMaxResponseBytes,
                cancellationToken,
                _clientFactory).ConfigureAwait(false);
        }
    }

    private async Task<(byte[] Data, string MIMEType)> SendBinaryAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            ProviderTransport.OutboundResult result = await ProviderTransport.SendAsync(
                request,
                _context?.MaxResponseBytes ?? ProviderTransport.DefaultMaxResponseBytes,
                null,
                cancellationToken,
                _clientFactory).ConfigureAwait(false);
            return (result.Data, result.MIMEType);
        }
    }
}
