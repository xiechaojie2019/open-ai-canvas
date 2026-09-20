#nullable enable
using System.Text;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Providers;

/// <summary>
/// 出站请求携带的参考素材（图片/视频/音频）。
/// 对应 Go: <c>internal/app/provider.go</c> 的 <c>providerMedia</c>。
/// </summary>
public sealed class ProviderMedia
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("dataUrl")]
    public string DataURL { get; set; } = "";

    [JsonPropertyName("url")]
    public string URL { get; set; } = "";

    [JsonPropertyName("storageKey")]
    public string StorageKey { get; set; } = "";

    [JsonPropertyName("mimeType")]
    public string MIMEType { get; set; } = "";

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }
}

/// <summary>
/// Provider 出站请求的媒体编码与 multipart 写入。
/// 对应 Go: <c>internal/app/provider_http_client.go</c> 的
/// <c>mediaBytes</c> / <c>writeMediaPart</c> / <c>providerMediaFilename</c>。
/// </summary>
public static class ProviderMediaCodec
{
    /// <summary>
    /// 解析素材字节与 MIME。仅接受 <c>data:</c> URL —— 后端任务队列不信任外部地址。
    /// 对应 Go: <c>mediaBytes</c>。
    /// </summary>
    public static (byte[] Raw, string MIMEType) Bytes(ProviderMedia media)
    {
        string value = media.DataURL.Length > 0 ? media.DataURL : media.URL;
        if (!value.StartsWith("data:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("后端任务队列需要 data URL 形式的本地参考素材");
        }
        int comma = value.IndexOf(',');
        if (comma < 0)
        {
            throw new InvalidOperationException("data URL 格式错误");
        }
        string header = value[..comma];
        string encoded = value[(comma + 1)..];

        // 取 "data:image/png;base64" 中的 image/png（去首尾空白）。
        string declared = header["data:".Length..];
        int semicolon = declared.IndexOf(';');
        if (semicolon >= 0)
        {
            declared = declared[..semicolon];
        }
        declared = declared.Trim();

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(encoded);
        }
        catch (FormatException error)
        {
            throw new InvalidOperationException($"参考素材 base64 解码失败：{error.Message}", error);
        }
        return (raw, NormalizedMediaMimeType(DefaultString(declared, media.Type), raw));
    }

    /// <summary>
    /// 归一化素材 MIME：优先声明值（非 octet-stream），否则按内容嗅探。
    /// 对应 Go: <c>normalizedMediaMimeType</c>。
    /// </summary>
    public static string NormalizedMediaMimeType(string declared, byte[] data)
    {
        declared = declared.Split(';')[0].Trim();
        if (declared.Length > 0 && declared != "application/octet-stream")
        {
            return declared;
        }
        string detected = SniffContentType(data).Split(';')[0].Trim();
        return detected.Length > 0 ? detected : "application/octet-stream";
    }

    /// <summary>
    /// 生成 multipart 文件名：<c>reference-{安全ID}{扩展名}</c>。
    /// ID 只保留 [A-Za-z0-9-_] 且最多 64 字符；扩展名由 MIME 推断，兜底 <c>.bin</c>。
    /// 对应 Go: <c>providerMediaFilename</c>。
    /// </summary>
    public static string MediaFilename(ProviderMedia media, string mimeType)
    {
        string source = media.ID.Trim();
        if (source.Length == 0)
        {
            source = "reference";
        }
        StringBuilder builder = new();
        foreach (char ch in source)
        {
            bool allowed = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_';
            if (!allowed)
            {
                continue;
            }
            builder.Append(ch);
            // 与 Go 的 builder.Len() 一致：按 UTF-16 长度计数（这些字符均单字节）。
            if (builder.Length >= 64)
            {
                break;
            }
        }
        string safe = builder.ToString();
        if (safe.Length == 0)
        {
            safe = "reference";
        }

        string extension = ExtensionFromMimeType(mimeType.Split(';')[0].Trim());
        if (extension.Length == 0)
        {
            extension = ".bin";
        }
        return "reference-" + safe + extension;
    }

    /// <summary>
    /// 把素材作为 part 写入 multipart（含 Content-Disposition 与 Content-Type）。
    /// 对应 Go: <c>writeMediaPart</c>。
    /// </summary>
    /// <remarks>
    /// 与 Go 的 <c>mime.FormatMediaType</c> 一致：文件名按 RFC 2231 编码，
    /// 非 ASCII 时降级为 <c>filename*=utf-8''...</c>（这里的文件名已过滤为 ASCII 安全字符）。
    /// </remarks>
    public static async Task WriteMediaPartAsync(
        MultipartFormDataContent form, string field, ProviderMedia media, CancellationToken cancellationToken = default)
    {
        (byte[] raw, string mimeType) = Bytes(media);
        string filename = MediaFilename(media, mimeType);
        ByteArrayContent part = new(raw);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
        form.Add(part, field, filename);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>对应 Go 的 <c>defaultString</c>。</summary>
    public static string DefaultString(string value, string fallback) =>
        value.Length > 0 ? value : fallback;

    /// <summary>
    /// 内容嗅探。对应 Go 的 <c>http.DetectContentType</c>（覆盖常见媒体类型）。
    /// 与资源上传模块共用同一张魔数表。
    /// </summary>
    private static string SniffContentType(ReadOnlySpan<byte> data) =>
        OpenAICanvas.Outbound.ContentTypeSniffer.Sniff(data);

    /// <summary>对应 Go 的 <c>mime.ExtensionsByType</c>（常用子集）。</summary>
    private static string ExtensionFromMimeType(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/quicktime" => ".mov",
        "audio/mpeg" => ".mp3",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/mp4" => ".m4a",
        "audio/ogg" => ".ogg",
        "application/pdf" => ".pdf",
        _ => "",
    };

    /// <summary>按 rune 截断（与 Go 的 <c>truncateRunes</c> 一致，用于错误文案）。</summary>
    public static string TruncateRunes(string value, int limit) => KernelUtil.TruncateRunes(value, limit);
}
