#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Outbound;
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Providers;

/// <summary>
/// 图片任务的协议分发与实现。
/// 对应 Go: <c>internal/app/provider_image.go</c> 的
/// <c>runImageTask</c> / <c>runGeminiImageTask</c> / <c>runGrokImageTask</c> /
/// <c>runVolcengineArkImageTask</c> / <c>runVolcengineJiMengImageTask</c>。
/// </summary>
public sealed class ProviderImageTask
{
    private const string OpenAIImageProtocolPath = "/images/generations";

    private readonly Func<HttpClient>? _clientFactory;
    private readonly IProviderRequestContext? _context;
    private readonly VideoPollPolicy? _pollPolicy;

    public ProviderImageTask(
        IProviderRequestContext? context = null,
        Func<HttpClient>? clientFactory = null,
        VideoPollPolicy? pollPolicy = null)
    {
        _context = context;
        _clientFactory = clientFactory;
        _pollPolicy = pollPolicy;
    }

    /// <summary>
    /// 图片任务入口：按 <c>interfaceType</c> 分发。
    /// 对应 Go: <c>runImageTask</c>。
    /// </summary>
    public async Task<Dictionary<string, object?>> RunAsync(
        TextTaskInput input, CancellationToken cancellationToken = default)
    {
        string interfaceType = input.Config.InterfaceType ?? "";
        // 与 Go 一致：只查上下文注入的注册表。生产 ctx 携带官方插件包，测试裸 ctx 走手写协议。
        IProtocolAdapter? declarative = _context?.DeclarativeAdapter?.Resolve(interfaceType);
        if (declarative is not null)
        {
            return await new ProviderProtocolTask(_context, _clientFactory)
                .RunAsync(input, declarative, "", ProviderProtocolTask.DeclarativePollPolicy("image"), cancellationToken)
                .ConfigureAwait(false);
        }
        return interfaceType switch
        {
            ChannelInterfaceType.ChannelInterfaceGrokImage =>
                await RunGrokAsync(input, cancellationToken).ConfigureAwait(false),
            ChannelInterfaceType.ChannelInterfaceGeminiImage =>
                await RunGeminiAsync(input, cancellationToken).ConfigureAwait(false),
            ChannelInterfaceType.ChannelInterfaceVolcengineArkImage =>
                await RunVolcengineArkAsync(input, cancellationToken).ConfigureAwait(false),
            ChannelInterfaceType.ChannelInterfaceVolcengineJiMengImage =>
                await RunJiMengAsync(input, cancellationToken).ConfigureAwait(false),
            _ => await RunOpenAiAsync(input, cancellationToken).ConfigureAwait(false),
        };
    }

    // ------------------------------------------------------------ OpenAI Images

    /// <summary>
    /// OpenAI Images 协议（含蒙版编辑）。
    /// 对应 Go: <c>runImageTask</c> 的主分支。
    /// </summary>
    private async Task<Dictionary<string, object?>> RunOpenAiAsync(
        TextTaskInput input, CancellationToken cancellationToken)
    {
        if (input.Mask is not null)
        {
            // 蒙版编辑是强校验写路径：协议能力不明确时必须失败，不能静默退化为整图重绘。
            if ((input.Config.InterfaceType ?? "").Trim() != ChannelInterfaceType.ChannelInterfaceOpenAIImage)
            {
                throw new InvalidOperationException(
                    "当前渠道未声明 OpenAI Images 编辑协议，已拒绝可能忽略蒙版的整图重绘");
            }
            if (input.ReferenceImages.Count == 0)
            {
                throw new InvalidOperationException("蒙版编辑必须提供与蒙版同尺寸的源图片");
            }
        }

        ImageCapabilityConfig? capability = input.ImageCapability;
        bool edit = input.ReferenceImages.Count > 0 || input.Mask is not null;
        byte[] payload;
        string contentType;

        if (edit)
        {
            // multipart/form-data：字段顺序与 Go 一致（测试按字节断言时会用到）。
            payload = BuildEditMultipart(input, capability, out contentType);
        }
        else
        {
            Dictionary<string, object?> body = new(StringComparer.Ordinal)
            {
                ["model"] = input.Config.Model,
                ["prompt"] = ProviderHelpers.WithSystemPrompt(input.Config.SystemPrompt, input.Prompt),
                ["n"] = 1,
            };
            ApplyImageParameters(body, capability, input.Config);
            payload = Encoding.UTF8.GetBytes(OpenAICanvas.Protocol.ProtocolJson.Serialize(body));
            contentType = "application/json";
        }

        string path = edit ? "/images/edits" : OpenAIImageProtocolPath;
        Dictionary<string, object?> response = await PostAsync(
            input.Config, path, payload, contentType, cancellationToken).ConfigureAwait(false);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = "image",
            ["images"] = ImageDataUrls(response),
        };
    }

    /// <summary>按能力声明裁剪图片参数。对应 Go 主分支中连续的参数写入。</summary>
    private static void ApplyImageParameters(
        Dictionary<string, object?> body, ImageCapabilityConfig? capability, ProviderConfig config)
    {
        if (ProviderImageOptions.ImageParameterSupported(capability, "response_format"))
        {
            body["response_format"] = "b64_json";
        }
        if (ProviderImageOptions.ImageParameterSupported(capability, "output_format"))
        {
            body["output_format"] = "png";
        }
        if (ProviderImageOptions.ImageTransparentBackgroundSupported(capability)
            && config.TransparentBackground == "true")
        {
            body["background"] = "transparent";
        }
        string quality = config.Quality.Trim();
        if (ProviderImageOptions.ImageQualitySupported(capability)
            && quality.Length > 0
            && !quality.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            body["quality"] = ProviderImageOptions.NormalizeImageQuality(config.Quality);
        }
        (string key, string value) = ProviderImageOptions.ImageSizeParameter(capability, config.Size);
        if (value.Length > 0)
        {
            body[key] = value;
        }
    }

    /// <summary>
    /// 构造 <c>multipart/form-data</c> 编辑请求体。
    /// 对应 Go: <c>runImageTask</c> 的 multipart 分支 + <c>writeField</c> / <c>writeMediaPart</c>。
    /// </summary>
    private static byte[] BuildEditMultipart(
        TextTaskInput input, ImageCapabilityConfig? capability, out string contentType)
    {
        // Go 用 multipart.Writer 直接写 Buffer；这里手写以保持字段顺序与转义等价。
        const string boundary = "----OpenAICanvasFormBoundary";
        using MemoryStream stream = new();

        void WriteField(string name, string value) =>
            ProviderMultipart.WriteField(stream, boundary, name, value);

        void WriteMedia(string name, ProviderMedia media) =>
            ProviderMultipart.WriteMedia(stream, boundary, name, media);

        WriteField("model", input.Config.Model);
        WriteField("prompt", ProviderHelpers.WithSystemPrompt(input.Config.SystemPrompt, input.Prompt));
        WriteField("n", "1");
        if (ProviderImageOptions.ImageParameterSupported(capability, "response_format"))
        {
            WriteField("response_format", "b64_json");
        }
        if (ProviderImageOptions.ImageParameterSupported(capability, "output_format"))
        {
            WriteField("output_format", "png");
        }
        if (ProviderImageOptions.ImageTransparentBackgroundSupported(capability)
            && input.Config.TransparentBackground == "true")
        {
            WriteField("background", "transparent");
        }
        string quality = input.Config.Quality.Trim();
        if (ProviderImageOptions.ImageQualitySupported(capability)
            && quality.Length > 0
            && !quality.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            WriteField("quality", ProviderImageOptions.NormalizeImageQuality(input.Config.Quality));
        }
        (string key, string value) = ProviderImageOptions.ImageSizeParameter(capability, input.Config.Size);
        if (value.Length > 0)
        {
            WriteField(key, value);
        }
        foreach (ProviderMedia image in input.ReferenceImages)
        {
            WriteMedia("image", image);
        }
        if (input.Mask is not null)
        {
            WriteMedia("mask", input.Mask);
        }
        ProviderMultipart.Close(stream, boundary);

        contentType = "multipart/form-data; boundary=" + boundary;
        return stream.ToArray();
    }

    // ------------------------------------------------------------ Gemini Images

    /// <summary>
    /// Gemini Images 协议。对应 Go: <c>runGeminiImageTask</c>。
    /// </summary>
    private async Task<Dictionary<string, object?>> RunGeminiAsync(
        TextTaskInput input, CancellationToken cancellationToken)
    {
        if (input.Mask is not null)
        {
            throw new InvalidOperationException("Gemini Images 不支持蒙版编辑，请移除蒙版后重试");
        }
        if (input.ReferenceVideos.Count > 0 || input.ReferenceAudios.Count > 0)
        {
            throw new InvalidOperationException("Gemini Images 不支持参考视频或音频");
        }

        List<Dictionary<string, object?>> parts = [];
        string prompt = input.Prompt.Trim();
        if (prompt.Length > 0)
        {
            parts.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = prompt });
        }
        foreach (ProviderMedia image in input.ReferenceImages)
        {
            (byte[] raw, string mimeType) = GeminiImageBytes(image);
            parts.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["inlineData"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["mimeType"] = mimeType,
                    ["data"] = Convert.ToBase64String(raw),
                },
            });
        }
        if (parts.Count == 0)
        {
            throw new InvalidOperationException("Gemini Images 请求缺少提示词或参考图");
        }

        Dictionary<string, object?> generationConfig = new(StringComparer.Ordinal)
        {
            ["responseModalities"] = new List<string> { "TEXT", "IMAGE" },
        };
        Dictionary<string, object?>? imageConfig = GeminiImageConfigFor(input.Config);
        if (imageConfig is not null)
        {
            generationConfig["imageConfig"] = imageConfig;
        }
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["contents"] = new List<Dictionary<string, object?>>
            {
                new(StringComparer.Ordinal)
                {
                    ["role"] = "user",
                    ["parts"] = parts,
                },
            },
            ["generationConfig"] = generationConfig,
        };
        string systemPrompt = input.Config.SystemPrompt.Trim();
        if (systemPrompt.Length > 0)
        {
            body["systemInstruction"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["parts"] = new List<Dictionary<string, object?>>(
                [
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = systemPrompt },
                ]),
            };
        }

        // Gemini 走 /v1beta 前缀，且模型名需 URL 转义后拼进路径。
        string path = "/models/" + Uri.EscapeDataString(input.Config.Model) + ":generateContent";
        Dictionary<string, object?> response = await PostGeminiAsync(
            input.Config, path, body, cancellationToken).ConfigureAwait(false);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = "image",
            ["images"] = GeminiImageDataUrls(response),
        };
    }

    /// <summary>
    /// 由画布尺寸/质量推导 Gemini 的 <c>imageConfig</c>；两者皆空时返回 <c>null</c>。
    /// 对应 Go: <c>geminiImageConfigFor</c>。
    /// </summary>
    /// <remarks>比例必须含且仅含一个冒号；质量按 1k/2k/4k（或 low/medium/high）映射为大写档位。</remarks>
    public static Dictionary<string, object?>? GeminiImageConfigFor(ProviderConfig config)
    {
        string aspectRatio = "";
        string imageSize = "";
        string size = config.Size.Trim();
        if (size.Length > 0 && size != "auto" && size.Count(c => c == ':') == 1)
        {
            aspectRatio = size;
        }
        switch (config.Quality.Trim().ToLowerInvariant())
        {
            case "low" or "1k":
                imageSize = "1K";
                break;
            case "medium" or "2k":
                imageSize = "2K";
                break;
            case "high" or "4k":
                imageSize = "4K";
                break;
        }
        if (aspectRatio.Length == 0 && imageSize.Length == 0)
        {
            return null;
        }
        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        if (aspectRatio.Length > 0)
        {
            result["aspectRatio"] = aspectRatio;
        }
        if (imageSize.Length > 0)
        {
            result["imageSize"] = imageSize;
        }
        return result;
    }

    /// <summary>
    /// 读取参考图并校验 MIME 与字节签名一致。
    /// 对应 Go: <c>geminiImageBytes</c>。
    /// </summary>
    /// <remarks>
    /// <b>双向校验</b>：声明的 MIME 不是 image/ 时用嗅探结果兜底；
    /// 声明是 image/ 但嗅探不是 image/ 时直接失败 ——
    /// 否则错误的 data URL MIME 能把非图片内容伪装成图片发给上游。
    /// </remarks>
    public static (byte[] Raw, string MIMEType) GeminiImageBytes(ProviderMedia media)
    {
        (byte[] raw, string declared) = ProviderMediaCodec.Bytes(media);
        if (raw.Length == 0)
        {
            throw new InvalidOperationException("参考图片数据为空");
        }
        string detected = ContentTypeSniffer.Sniff(raw).Split(';')[0].Trim();
        string mimeType = declared;
        if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            if (!detected.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"参考图片 MIME 类型无效：{ProviderMediaCodec.DefaultString(mimeType, detected)}");
            }
            mimeType = detected;
        }
        else if (!detected.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"参考图片内容不是有效图片：{ProviderMediaCodec.DefaultString(detected, mimeType)}");
        }
        return (raw, mimeType);
    }

    /// <summary>
    /// 解析 Gemini 图片响应。对应 Go: <c>geminiImageDataURLs</c>。
    /// </summary>
    /// <remarks>
    /// 每个 part 先看 <c>inlineData</c>（兼容 <c>inline_data</c>），再看 <c>fileData</c>。
    /// 非图片 MIME 视为上游错误而非跳过 —— 静默跳过会让"上游返回了 PDF"表现为"没有返回图片"。
    /// </remarks>
    public static List<Dictionary<string, string>> GeminiImageDataUrls(Dictionary<string, object?> payload)
    {
        Dictionary<string, object?>? errorValue = JsonFields.NestedObject(payload, "error");
        if (errorValue is not null)
        {
            string message = JsonFields.StringField(errorValue, "message");
            if (message.Length > 0)
            {
                throw new InvalidOperationException(message);
            }
        }

        List<Dictionary<string, string>> images = [];
        foreach (object? candidateValue in JsonFields.InterfaceSlice(payload, "candidates"))
        {
            if (candidateValue is not Dictionary<string, object?> candidate)
            {
                continue;
            }
            Dictionary<string, object?>? content = JsonFields.NestedObject(candidate, "content");
            if (content is null)
            {
                continue;
            }
            foreach (object? partValue in JsonFields.InterfaceSlice(content, "parts"))
            {
                if (partValue is not Dictionary<string, object?> part)
                {
                    continue;
                }
                Dictionary<string, object?>? inlineData =
                    JsonFields.NestedObject(part, "inlineData") ?? JsonFields.NestedObject(part, "inline_data");
                if (inlineData is not null)
                {
                    string data = JsonFields.StringField(inlineData, "data").Trim();
                    string mimeType = ProviderHelpers.FirstNonEmpty(
                        JsonFields.StringField(inlineData, "mimeType"),
                        JsonFields.StringField(inlineData, "mime_type"));
                    if (data.Length == 0)
                    {
                        continue;
                    }
                    if (mimeType.Length == 0)
                    {
                        mimeType = "image/png";
                    }
                    if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Gemini Images 返回了非图片 MIME 类型：{mimeType}");
                    }
                    byte[] decoded;
                    try
                    {
                        decoded = Convert.FromBase64String(data);
                    }
                    catch (FormatException error)
                    {
                        throw new InvalidOperationException($"Gemini Images 返回的图片数据无效：{error.Message}");
                    }
                    images.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["dataUrl"] = ProviderHelpers.DataUrl(mimeType, decoded),
                    });
                    continue;
                }

                Dictionary<string, object?>? fileData = JsonFields.NestedObject(part, "fileData");
                if (fileData is null)
                {
                    continue;
                }
                string fileUrl = ProviderHelpers.FirstNonEmpty(
                    JsonFields.StringField(fileData, "fileUri"),
                    JsonFields.StringField(fileData, "file_uri"));
                if (fileUrl.Length > 0)
                {
                    images.Add(new Dictionary<string, string>(StringComparer.Ordinal) { ["dataUrl"] = fileUrl });
                }
            }
        }
        if (images.Count == 0)
        {
            throw new InvalidOperationException("Gemini Images 接口没有返回图片");
        }
        return images;
    }

    // ------------------------------------------------------------ Grok Images

    /// <summary>Grok 图片协议。对应 Go: <c>runGrokImageTask</c>。</summary>
    private async Task<Dictionary<string, object?>> RunGrokAsync(
        TextTaskInput input, CancellationToken cancellationToken)
    {
        (Dictionary<string, object?> body, string path) = GrokImageRequestBody(input);
        Dictionary<string, object?> response = await PostAsync(
            input.Config, path, Encoding.UTF8.GetBytes(OpenAICanvas.Protocol.ProtocolJson.Serialize(body)),
            "application/json", cancellationToken).ConfigureAwait(false);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = "image",
            ["images"] = ImageDataUrls(response),
        };
    }

    /// <summary>
    /// 构造 Grok 图片请求体与路径。对应 Go: <c>grokImageRequestBody</c>。
    /// </summary>
    /// <remarks>
    /// Grok 用 <c>aspect_ratio</c> 表达画布比例；<b>同时发送 <c>size</c> 会被上游按 OpenAI
    /// 枚举校验并拒绝</b>，所以这里绝不能复用 <c>ApplyImageParameters</c>。
    /// </remarks>
    public static (Dictionary<string, object?> Body, string Path) GrokImageRequestBody(TextTaskInput input)
    {
        if (input.Mask is not null)
        {
            throw new InvalidOperationException("Grok 图片协议不支持蒙版编辑，请移除蒙版后重试");
        }
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["model"] = input.Config.Model,
            ["prompt"] = ProviderHelpers.WithSystemPrompt(input.Config.SystemPrompt, input.Prompt),
            ["n"] = 1,
            ["response_format"] = "url",
            ["aspect_ratio"] = ProviderImageOptions.NormalizeGrokImageAspectRatio(input.Config.Size),
            ["resolution"] = ProviderImageOptions.NormalizeGrokImageResolution(input.Config.Quality),
        };
        if (input.ReferenceImages.Count == 0)
        {
            return (body, "/images/generations");
        }
        if (input.ReferenceImages.Count != 1)
        {
            throw new InvalidOperationException(
                $"Grok 图片编辑只支持 1 张参考图，当前连接了 {input.ReferenceImages.Count} 张");
        }
        // 公网 URL 直传；否则内联为 data URL。
        string trimmed = (input.ReferenceImages[0].URL ?? "").Trim();
        body["image"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["url"] = ProviderHelpers.IsPublicMediaURL(trimmed)
                ? trimmed
                : ProviderHelpers.OpenAIImageInputURL(input.ReferenceImages[0]),
        };
        return (body, "/images/edits");
    }

    // ------------------------------------------------------------ Volcengine Ark

    /// <summary>火山方舟图片协议。对应 Go: <c>runVolcengineArkImageTask</c>。</summary>
    private async Task<Dictionary<string, object?>> RunVolcengineArkAsync(
        TextTaskInput input, CancellationToken cancellationToken)
    {
        if (input.Mask is not null)
        {
            throw new InvalidOperationException("火山方舟图片协议不支持蒙版编辑，请移除蒙版后重试");
        }
        Dictionary<string, object?> body = VolcengineArkImageBody(input);
        Dictionary<string, object?> response = await PostAsync(
            input.Config, "/images/generations",
            Encoding.UTF8.GetBytes(OpenAICanvas.Protocol.ProtocolJson.Serialize(body)),
            "application/json", cancellationToken).ConfigureAwait(false);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = "image",
            ["images"] = await VolcengineArkImageDataUrlsAsync(input.Config, response, cancellationToken)
                .ConfigureAwait(false),
        };
    }

    /// <summary>
    /// 方舟图片请求体。对应 Go: <c>volcengineArkImageBody</c>。
    /// </summary>
    /// <remarks>方舟只接受像素尺寸，故 <c>size</c> 键需做像素区间夹取。</remarks>
    public static Dictionary<string, object?> VolcengineArkImageBody(TextTaskInput input)
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["model"] = input.Config.Model,
            ["prompt"] = ProviderHelpers.WithSystemPrompt(input.Config.SystemPrompt, input.Prompt),
            ["n"] = 1,
            ["response_format"] = "b64_json",
            ["watermark"] = false,
        };
        (string key, string value) = ProviderImageOptions.ImageSizeParameter(
            input.ImageCapability, input.Config.Size);
        if (value.Length > 0)
        {
            if (key == "size")
            {
                value = ProviderImageOptions.NormalizeVolcengineArkImageSize(value);
            }
            body[key] = value;
        }
        if (input.ReferenceImages.Count == 0)
        {
            return body;
        }
        List<string> images = [];
        foreach (ProviderMedia image in input.ReferenceImages)
        {
            images.Add(ProviderHelpers.OpenAIImageInputURL(image));
        }
        // 与 Go 一致：单图传字符串、多图传数组（上游按两种形态分别解析）。
        body["image"] = images.Count == 1 ? images[0] : images;
        return body;
    }

    /// <summary>
    /// 解析方舟图片响应，把临时 CDN 地址下载并内联为 data URL。
    /// 对应 Go: <c>volcengineArkImageDataURLs</c>。
    /// </summary>
    /// <remarks>
    /// 方舟默认返回临时 CDN 地址，必须由后端下载成内联结果：
    /// 后续资源持久化才能原子地写入服务器或用户配置的对象存储，
    /// 且不依赖浏览器跨域访问方舟 CDN。
    /// </remarks>
    public async Task<List<Dictionary<string, string>>> VolcengineArkImageDataUrlsAsync(
        ProviderConfig config,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken = default)
    {
        List<Dictionary<string, string>> images = ImageDataUrls(payload);
        foreach (Dictionary<string, string> image in images)
        {
            string value = image["dataUrl"].Trim();
            if (value.StartsWith("data:image/", StringComparison.Ordinal))
            {
                continue;
            }
            if (!ProviderHelpers.IsPublicMediaURL(value))
            {
                throw new InvalidOperationException("火山方舟图片接口没有返回可下载的图片");
            }
            (byte[] data, string mimeType) = await DownloadExternalAsync(config, value, cancellationToken)
                .ConfigureAwait(false);
            string detected = ContentTypeSniffer.Sniff(data).Split(';')[0].Trim().ToLowerInvariant();
            mimeType = ProviderMediaCodec.NormalizedMediaMimeType(mimeType, data).ToLowerInvariant();
            if (data.Length == 0
                || detected.Contains("json", StringComparison.Ordinal)
                || detected.StartsWith("text/", StringComparison.Ordinal)
                || !mimeType.StartsWith("image/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"火山方舟图片结果无效：{ProviderMediaCodec.DefaultString(detected, mimeType)}");
            }
            image["dataUrl"] = ProviderHelpers.DataUrl(mimeType, data);
            image["mimeType"] = mimeType;
        }
        return images;
    }

    /// <summary>
    /// 下载方舟结果。仅当目标与渠道同源时才带上渠道鉴权。
    /// 对应 Go: <c>getProviderExternalBinary</c>。
    /// </summary>
    /// <remarks>
    /// <b>跨源不带鉴权</b>：把渠道密钥发给第三方 CDN 等于凭据泄露。
    /// </remarks>
    private async Task<(byte[] Data, string MIMEType)> DownloadExternalAsync(
        ProviderConfig config, string rawUrl, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, rawUrl);
        if (ProviderHelpers.IsSameProviderOrigin(config.BaseURL, rawUrl))
        {
            ProviderTransport.ApplyProviderAuth(request, config);
            OutboundHttpClient.ApplyHeaders(request, config.Headers);
        }
        ProviderTransport.ApplyDefaultHeaders(request);

        ProviderTransport.OutboundResult result = await ProviderTransport.SendAsync(
            request, ProviderTransport.DefaultMaxResponseBytes, null, cancellationToken, _clientFactory)
            .ConfigureAwait(false);
        return (result.Data, result.MIMEType);
    }

    // ------------------------------------------------------------ 即梦（JiMeng）

    /// <summary>即梦异步协议的 Action 名。对应 Go: <c>jiMengSubmitAction</c>。</summary>
    private const string JiMengSubmitAction = "CVSync2AsyncSubmitTask";

    /// <summary>对应 Go: <c>jiMengResultAction</c>。</summary>
    private const string JiMengResultAction = "CVSync2AsyncGetResult";

    /// <summary>对应 Go: <c>jiMengAPIVersion</c>。</summary>
    private const string JiMengAPIVersion = "2022-08-31";

    /// <summary>即梦像素下限（1MP）。对应 Go: <c>jiMengImageMinPixels</c>。</summary>
    private const long JiMengMinPixels = 1_048_576;

    /// <summary>即梦像素上限（16MP）。对应 Go: <c>jiMengImageMaxPixels</c>。</summary>
    private const long JiMengMaxPixels = 16_777_216;

    /// <summary>即梦 API 要求的 JSON 载荷。对应 Go: <c>jiMengAPIJSONPayload</c>。</summary>
    private const string JiMengAPIJSONPayload = "{\"return_url\":true}";

    /// <summary>
    /// 即梦异步图片协议：JSON 提交 + 轮询 + 结果内联。
    /// 对应 Go: <c>runVolcengineJiMengImageTask</c>。
    /// </summary>
    /// <remarks>
    /// 返回的 <c>dataUrl</c> 键与 OpenAI / 方舟分支一致，供上层消费方统一读取。
    /// </remarks>
    public async Task<Dictionary<string, object?>> RunJiMengAsync(
        TextTaskInput input, CancellationToken cancellationToken = default)
    {
        if (input.Mask is not null)
        {
            throw new InvalidOperationException("即梦图片协议不支持蒙版编辑，请移除蒙版后重试");
        }

        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["req_key"] = input.Config.Model,
            ["prompt"] = ProviderHelpers.WithSystemPrompt(input.Config.SystemPrompt, input.Prompt),
            ["force_single"] = true,
        };
        (int width, int height) = JiMengImageDimensions(input.Config.Size);
        if (width > 0 && height > 0)
        {
            body["width"] = width;
            body["height"] = height;
        }
        if (input.ReferenceImages.Count > 14)
        {
            throw new InvalidOperationException("即梦图片协议最多支持 14 张参考图");
        }
        if (input.ReferenceImages.Count > 0)
        {
            List<string> images = new(input.ReferenceImages.Count);
            foreach (ProviderMedia image in input.ReferenceImages)
            {
                (byte[] raw, _) = ProviderMediaCodec.Bytes(image);
                images.Add(Convert.ToBase64String(raw));
            }
            body["binary_data_base64"] = images;
        }

        string taskID = await SubmitJiMengTaskAsync(input.Config, body, cancellationToken).ConfigureAwait(false);
        VideoPollPolicy policy = ProviderVideoPolling.Normalize(_pollPolicy);
        while (DateTimeOffset.UtcNow < JiMengPollingDeadline(policy))
        {
            Dictionary<string, object?> result = await PollJiMengTaskAsync(
                input.Config, taskID, cancellationToken).ConfigureAwait(false);
            JiMengResponse payload = ParseJiMengResponse(result);
            switch (payload.Data.Status.Trim().ToLowerInvariant())
            {
                case "done":
                    List<Dictionary<string, string>> dataUrls = await JiMengImageDataURLsAsync(
                        input.Config, payload, cancellationToken).ConfigureAwait(false);
                    if (dataUrls.Count == 0)
                    {
                        throw new InvalidOperationException("任务已完成但没有返回图片");
                    }
                    return new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["mode"] = "image",
                        ["images"] = dataUrls,
                    };
                case "not_found":
                case "expired":
                    throw new InvalidOperationException($"即梦图片任务 {taskID} 已失效，请重新生成");
            }
            await policy.Sleep(policy.Interval, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"即梦图片生成超时（任务 {taskID}）");
    }

    /// <summary>
    /// 轮询截止时间：测试注入的 <see cref="VideoPollPolicy.TotalTimeout"/> 优先，
    /// 生产回落 1 小时。对应 Go: <c>providerPollingDeadline(ctx)</c>。
    /// </summary>
    private static DateTimeOffset JiMengPollingDeadline(VideoPollPolicy policy) =>
        DateTimeOffset.UtcNow.Add(policy.TotalTimeout ?? VideoPollPolicy.PollTimeout);

    /// <summary>对应 Go: <c>jiMengResponse</c>。</summary>
    private sealed record JiMengResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; init; }

        [JsonPropertyName("message")]
        public string Message { get; init; } = "";

        [JsonPropertyName("request_id")]
        public string RequestID { get; init; } = "";

        [JsonPropertyName("data")]
        public JiMengResponseData Data { get; init; } = new();
    }

    private sealed record JiMengResponseData
    {
        [JsonPropertyName("task_id")]
        public string TaskID { get; init; } = "";

        [JsonPropertyName("status")]
        public string Status { get; init; } = "";

        [JsonPropertyName("binary_data_base64")]
        public List<string> BinaryDataBase64 { get; init; } = [];

        [JsonPropertyName("image_urls")]
        public List<string> ImageURLs { get; init; } = [];
    }

    /// <summary>把上游载荷投影到 <see cref="JiMengResponse"/>；非对象直接失败。</summary>
    private static JiMengResponse ParseJiMengResponse(Dictionary<string, object?> payload)
    {
        try
        {
            string json = OpenAICanvas.Protocol.ProtocolJson.Serialize(payload);
            JiMengResponse? parsed = JsonSerializer.Deserialize<JiMengResponse>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed ?? new JiMengResponse();
        }
        catch (JsonException error)
        {
            throw new ProviderResponseDecodeException(error);
        }
    }

    /// <summary>对应 Go: <c>submitJiMengTask</c>。</summary>
    private async Task<string> SubmitJiMengTaskAsync(
        ProviderConfig config, Dictionary<string, object?> body, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> payload = await PostJiMengAsync(
            config, JiMengSubmitAction, body, cancellationToken).ConfigureAwait(false);
        JiMengResponse parsed = ParseJiMengResponse(payload);
        ValidateJiMengResponse(parsed);
        string taskID = parsed.Data.TaskID.Trim();
        if (taskID.Length == 0)
        {
            throw new InvalidOperationException("即梦接口没有返回任务 ID");
        }
        return taskID;
    }

    /// <summary>对应 Go: <c>pollJiMengTask</c>。</summary>
    private async Task<Dictionary<string, object?>> PollJiMengTaskAsync(
        ProviderConfig config, string taskID, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["req_key"] = config.Model,
            ["task_id"] = taskID,
            ["req_json"] = JiMengAPIJSONPayload,
        };
        Dictionary<string, object?> payload = await PostJiMengAsync(
            config, JiMengResultAction, body, cancellationToken).ConfigureAwait(false);
        ValidateJiMengResponse(ParseJiMengResponse(payload));
        return payload;
      }

    /// <summary>对应 Go: <c>postJiMengJSON</c>（V4 签名 Service=cv、Region=cn-north-1）。</summary>
    private async Task<Dictionary<string, object?>> PostJiMengAsync(
        ProviderConfig config, string action, Dictionary<string, object?> body, CancellationToken cancellationToken)
    {
        byte[] data = Encoding.UTF8.GetBytes(OpenAICanvas.Protocol.ProtocolJson.Serialize(body));
        UriBuilder endpoint = new(BaseJiMengEndpoint(config.BaseURL));
        List<(string Key, string Value)> query = ParseQueryString(endpoint.Query);
        query.RemoveAll(item => item.Key == "Action" || item.Key == "Version");
        query.Add(("Action", action));
        query.Add(("Version", JiMengAPIVersion));
        endpoint.Query = string.Join("&", query.Select(item =>
            Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value)));

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint.Uri)
        {
            Content = new ByteArrayContent(data),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        OutboundHttpClient.ApplyHeaders(request, config.Headers);
        ProviderTransport.ApplyDefaultHeaders(request);
        ProtocolRequestBuilder.SignVolcV4(
            request, config.APIKey, config.SecretKey, "cv", "cn-north-1", data);

        using (request)
        {
            return await ProviderTransport.SendJsonAsync(
                request,
                _context?.MaxResponseBytes ?? ProviderTransport.DefaultMaxResponseBytes,
                cancellationToken,
                _clientFactory).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 即梦端点：解析 BaseURL 后路径固定为 <c>/</c>；非法/空 BaseURL 失败。
    /// 对应 Go: <c>url.Parse</c> + <c>endpoint.Path = "/"</c>。
    /// </summary>
    private static Uri BaseJiMengEndpoint(string baseURL)
    {
        string trimmed = baseURL.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("即梦渠道缺少 BaseURL");
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("即梦渠道 BaseURL 无效");
        }
        return parsed;
    }

    /// <summary>解析 <c>?a=b</c> 形式的查询串（保留原始顺序；值保持原样）。</summary>
    private static List<(string Key, string Value)> ParseQueryString(string query)
    {
        List<(string Key, string Value)> result = [];
        string trimmed = query.TrimStart('?');
        if (trimmed.Length == 0)
        {
            return result;
        }
        foreach (string segment in trimmed.Split('&'))
        {
            if (segment.Length == 0)
            {
                continue;
            }
            int equals = segment.IndexOf('=');
            if (equals < 0)
            {
                result.Add((Uri.UnescapeDataString(segment), ""));
            }
            else
            {
                result.Add((
                    Uri.UnescapeDataString(segment[..equals]),
                    Uri.UnescapeDataString(segment[(equals + 1)..])));
            }
        }
        return result;
    }

    /// <summary>对应 Go: <c>validateJiMengResponse</c>（code==10000 通过）。</summary>
    private static void ValidateJiMengResponse(JiMengResponse payload)
    {
        if (payload.Code == 10000)
        {
            return;
        }
        string message = payload.Message.Trim();
        if (message.Length == 0)
        {
            message = "未知错误";
        }
        throw new InvalidOperationException(
            payload.RequestID.Length > 0
                ? $"即梦接口返回错误 {payload.Code}：{message}（request_id: {payload.RequestID}）"
                : $"即梦接口返回错误 {payload.Code}：{message}");
    }

    /// <summary>对应 Go: <c>jiMengImageDataURLs</c>。</summary>
    private async Task<List<Dictionary<string, string>>> JiMengImageDataURLsAsync(
        ProviderConfig config, JiMengResponse payload, CancellationToken cancellationToken)
    {
        List<Dictionary<string, string>> images = [];
        foreach (string rawURL in payload.Data.ImageURLs)
        {
            (byte[] data, string mimeType) = await DownloadExternalAsync(
                config, rawURL, cancellationToken).ConfigureAwait(false);
            images.Add(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dataUrl"] = ProviderHelpers.DataUrl(
                    ProviderMediaCodec.NormalizedMediaMimeType(mimeType, data), data),
            });
        }
        foreach (string encoded in payload.Data.BinaryDataBase64)
        {
            byte[] data;
            try
            {
                data = Convert.FromBase64String(encoded.Trim());
            }
            catch (FormatException error)
            {
                throw new InvalidOperationException("即梦结果 base64 解码失败", error);
            }
            images.Add(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dataUrl"] = ProviderHelpers.DataUrl(
                    ProviderMediaCodec.NormalizedMediaMimeType("image/png", data), data),
            });
        }
        return images;
    }

    /// <summary>
    /// 即梦只接受像素尺寸，且总像素须在 [1MP, 16MP]；越界不发送 width/height。
    /// 对应 Go: <c>jiMengImageDimensions</c>。
    /// </summary>
    public static (int Width, int Height) JiMengImageDimensions(string? value)
    {
        string size = ProviderImageOptions.NormalizePixelSize(value).ToLowerInvariant();
        string[] parts = size.Split('x');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int width)
            || !int.TryParse(parts[1], out int height)
            || width <= 0 || height <= 0)
        {
            return (0, 0);
        }
        long area = (long)width * height;
        if (area < JiMengMinPixels || area > JiMengMaxPixels)
        {
            return (0, 0);
        }
        return (width, height);
    }

    // ------------------------------------------------------------ 通用响应解析

    /// <summary>
    /// 解析 <c>{ data: [ { b64_json | url } ] }</c> 响应。
    /// 对应 Go: <c>imageDataURLs</c>。
    /// </summary>
    /// <remarks>
    /// <c>b64_json</c> 优先于 <c>url</c>；MIME 固定为 <c>image/png</c>
    /// （接口未声明格式，与 Go 一致）。
    /// </remarks>
    public static List<Dictionary<string, string>> ImageDataUrls(Dictionary<string, object?> payload)
    {
        List<Dictionary<string, string>> images = [];
        foreach (object? itemValue in JsonFields.InterfaceSlice(payload, "data"))
        {
            if (itemValue is not Dictionary<string, object?> item)
            {
                continue;
            }
            string b64 = JsonFields.StringField(item, "b64_json");
            if (b64.Length > 0)
            {
                images.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["dataUrl"] = "data:image/png;base64," + b64,
                });
                continue;
            }
            string url = JsonFields.StringField(item, "url");
            if (url.Length > 0)
            {
                images.Add(new Dictionary<string, string>(StringComparer.Ordinal) { ["dataUrl"] = url });
            }
        }
        if (images.Count == 0)
        {
            throw new InvalidOperationException("接口没有返回可用图片");
        }
        return images;
    }

    // ------------------------------------------------------------ 传输

    private Task<Dictionary<string, object?>> PostAsync(
        ProviderConfig config, string path, byte[] payload, string contentType, CancellationToken cancellationToken)
    {
        string url = ProviderTransport.ChannelApiUrl(config.BaseURL, path);
        HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        ProviderTransport.ApplyProviderAuth(request, config);
        ProviderTransport.ApplyDefaultHeaders(request);
        OutboundHttpClient.ApplyHeaders(request, config.Headers);
        return SendJsonAsync(request, cancellationToken);
    }

    private Task<Dictionary<string, object?>> PostGeminiAsync(
        ProviderConfig config, string path, Dictionary<string, object?> body, CancellationToken cancellationToken)
    {
        string url = ProviderTransport.ApiUrlWithDefaultPrefix(config.BaseURL, path, "/v1beta");
        HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(
                OpenAICanvas.Protocol.ProtocolJson.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-goog-api-key", config.APIKey);
        ProviderTransport.ApplyDefaultHeaders(request);
        OutboundHttpClient.ApplyHeaders(request, config.Headers);
        return SendJsonAsync(request, cancellationToken);
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
}
