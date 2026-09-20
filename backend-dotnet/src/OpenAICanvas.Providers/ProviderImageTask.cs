#nullable enable
using System.Text;
using System.Text.Json;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Outbound;
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Providers;

/// <summary>
/// 图片任务的协议分发与实现。
/// 对应 Go: <c>internal/app/provider_image.go</c> 的
/// <c>runImageTask</c> / <c>runGeminiImageTask</c> / <c>runGrokImageTask</c> /
/// <c>runVolcengineArkImageTask</c>。
/// </summary>
/// <remarks>
/// 即梦（JiMeng）分支依赖任务轮询与下载（4.8 的轮询循环），本类未实现 ——
/// 遇到该 interfaceType 会明确报错，而不是静默走错协议。
/// </remarks>
public sealed class ProviderImageTask
{
    private const string OpenAIImageProtocolPath = "/images/generations";

    private readonly Func<HttpClient>? _clientFactory;
    private readonly IProviderRequestContext? _context;

    public ProviderImageTask(IProviderRequestContext? context = null, Func<HttpClient>? clientFactory = null)
    {
        _context = context;
        _clientFactory = clientFactory;
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
                throw new InvalidOperationException(
                    "即梦图片协议依赖任务轮询与下载（4.8 未完成），暂不可用"),
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
