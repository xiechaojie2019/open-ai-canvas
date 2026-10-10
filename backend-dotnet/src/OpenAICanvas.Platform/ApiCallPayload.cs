#nullable enable
using System.Text;
using System.Text.Json;

namespace OpenAICanvas.Platform;

/// <summary>
/// API 调用日志的报文脱敏与限长。
/// 对应 Go: <c>internal/platform/api_call_payload.go</c> 的
/// <c>SanitizeAPICallPayload</c> / <c>RequestPayloadForLog</c>。
/// </summary>
/// <remarks>
/// 保留排障所需报文，同时阻止密钥和大段内嵌媒体进入日志库。与系统代理的
/// <c>SystemProxyEndpoints.SanitizePayload</c> 是两份实现：代理版本额外把渠道密钥
/// 原文字符串替换为 [REDACTED]（任务侧报文不含渠道密钥，无需该分支）。
/// </remarks>
public static class ApiCallPayload
{
    /// <summary>落库报文的目标上限。对应 Go: <c>maxAPICallPayloadBytes</c>。</summary>
    public const int MaxPayloadChars = 128 << 10;

    /// <summary>参与解析的源报文上限。对应 Go: <c>maxAPICallPayloadSourceBytes</c>。</summary>
    public const int MaxPayloadSourceBytes = 1 << 20;

    /// <summary>
    /// 触发解析克隆的上限。生图/生视频响应常内嵌数 MB 的 b64_json，对其做
    /// Parse+MarshalIndent 会造成秒级 CPU 克隆风暴（worker 执行线程 + 审计并发），
    /// 曾把任务租约续期的 timer 回调延迟到租约过期之后；超过该值直接存摘要。
    /// </summary>
    private const int MaxParseBytes = 128 << 10;

    private static readonly string[] SecretKeyMarkers =
        { "apikey", "accesstoken", "authorization", "password", "secret" };

    /// <summary>脱敏一份请求/响应报文。</summary>
    public static string Sanitize(byte[] data, string contentType)
    {
        if (data.Length == 0)
        {
            return "";
        }
        string mediaType = MediaType(contentType);
        if (mediaType == "multipart/form-data")
        {
            return SanitizeMultipart(data, contentType);
        }
        if (data.Length > MaxPayloadSourceBytes)
        {
            return $"[报文过大，已省略，共 {data.Length} 字节]";
        }
        if (data.Length <= MaxParseBytes && IsValidJson(data))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(data);
                object? sanitized = SanitizeJson(document.RootElement.Clone(), "");
                string formatted = JsonSerializer.Serialize(sanitized, JsonIndentOptions);
                return Truncate(formatted);
            }
            catch (JsonException)
            {
                // 落到末尾的文本分支。
            }
        }
        if (mediaType.StartsWith("image/", StringComparison.Ordinal)
            || mediaType.StartsWith("video/", StringComparison.Ordinal)
            || mediaType.StartsWith("audio/", StringComparison.Ordinal)
            || mediaType == "application/octet-stream")
        {
            string shown = mediaType.Length == 0 ? "未知类型" : mediaType;
            return $"[{shown} 二进制报文，共 {data.Length} 字节]";
        }
        return Truncate(Encoding.UTF8.GetString(data));
    }

    /// <summary>
    /// 提取请求报文用于日志。对应 Go: <c>platform.RequestPayloadForLog(req)</c>。
    /// 仅缓冲型 Content（JSON/字节）可提取；流式内容返回空串。
    /// </summary>
    public static string RequestPayload(byte[] body, string contentType)
    {
        if (body.Length > MaxPayloadSourceBytes)
        {
            return $"[请求报文过大，已省略，超过 {MaxPayloadSourceBytes} 字节]";
        }
        return Sanitize(body, contentType);
    }

    private static string MediaType(string contentType)
    {
        string value = (contentType ?? "").Trim();
        if (value.Length == 0)
        {
            return "";
        }
        int separator = value.IndexOf(';', StringComparison.Ordinal);
        string mediaType = (separator < 0 ? value : value[..separator]).Trim().ToLowerInvariant();
        return mediaType;
    }

    private static bool IsMultipart(string contentType) =>
        MediaType(contentType) == "multipart/form-data";

    private static string SanitizeMultipart(byte[] data, string contentType)
    {
        // 简易 boundary 切分（Go 用 mime/multipart；这里字段数与大小都有硬上限，风险可控）。
        string boundary = BoundaryOf(contentType);
        if (boundary.Length == 0)
        {
            return "[multipart 请求报文无法解析]";
        }
        Dictionary<string, object?> fields = new(StringComparer.Ordinal);
        byte[] delimiter = Encoding.UTF8.GetBytes("--" + boundary);
        int offset = Array.IndexOf(data, delimiter[0]) >= 0 ? IndexOf(data, delimiter, 0) : -1;
        int parts = 0;
        while (offset >= 0 && parts < 100)
        {
            int partStart = offset + delimiter.Length;
            if (partStart + 1 < data.Length && data[partStart] == (byte)'-' && data[partStart + 1] == (byte)'-')
            {
                break;
            }
            // 跳过 CRLF
            if (partStart + 1 < data.Length && data[partStart] == (byte)'\r' && data[partStart + 1] == (byte)'\n')
            {
                partStart += 2;
            }
            int next = IndexOf(data, delimiter, partStart);
            int partEnd = (next < 0 ? data.Length : next) - 2; // 去掉尾部 CRLF
            if (partEnd > partStart)
            {
                parts++;
                if (!AddMultipartPart(fields, data[partStart..partEnd]))
                {
                    return "[multipart 请求报文无法解析]";
                }
            }
            offset = next;
        }
        string formatted = JsonSerializer.Serialize(fields, JsonIndentOptions);
        return Truncate(formatted);
    }

    private static bool AddMultipartPart(Dictionary<string, object?> fields, byte[] part)
    {
        int headerEnd = IndexOf(part, "\r\n\r\n"u8.ToArray(), 0);
        if (headerEnd < 0)
        {
            return false;
        }
        string headers = Encoding.UTF8.GetString(part[..headerEnd]);
        byte[] content = part[(headerEnd + 4)..];
        string name = "";
        string? fileName = null;
        string fileContentType = "";
        foreach (string line in headers.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase))
            {
                name = DispositionParameter(trimmed, "name");
                fileName = DispositionParameter(trimmed, "filename");
            }
            else if (trimmed.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
            {
                fileContentType = trimmed["Content-Type:".Length..].Trim();
            }
        }
        if (name.Length == 0)
        {
            name = $"part{fields.Count}";
        }
        if (!string.IsNullOrEmpty(fileName))
        {
            fields[name] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fileName"] = fileName,
                ["contentType"] = fileContentType,
                ["size"] = content.Length,
            };
        }
        else
        {
            // 与 Go 一致：非文件字段按字符串值走脱敏（键名参与密钥/内嵌编码判定）。
            fields[name] = SanitizeString(Encoding.UTF8.GetString(content), NormalizeKey(name));
        }
        return true;
    }

    private static string BoundaryOf(string contentType)
    {
        foreach (string segment in contentType.Split(';'))
        {
            string trimmed = segment.Trim();
            if (trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
            {
                string value = trimmed["boundary=".Length..].Trim().Trim('"');
                return value;
            }
        }
        return "";
    }

    private static string DispositionParameter(string header, string name)
    {
        foreach (string segment in header.Split(';'))
        {
            string trimmed = segment.Trim();
            if (trimmed.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(name.Length + 1)..].Trim().Trim('"');
            }
        }
        return "";
    }

    private static object? SanitizeJson(JsonElement value, string key)
    {
        string normalizedKey = NormalizeKey(key);
        foreach (string marker in SecretKeyMarkers)
        {
            if (normalizedKey.Contains(marker, StringComparison.Ordinal))
            {
                return "[REDACTED]";
            }
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                Dictionary<string, object?> result = new(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    result[property.Name] = SanitizeJson(property.Value, property.Name);
                }
                return result;
            case JsonValueKind.Array:
                List<object?> items = [];
                foreach (JsonElement item in value.EnumerateArray())
                {
                    items.Add(SanitizeJson(item, key));
                }
                return items;
            case JsonValueKind.String:
                return SanitizeString(value.GetString() ?? "", normalizedKey);
            case JsonValueKind.Number:
                return value.GetDecimal();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    private static object? SanitizeString(string text, string normalizedKey)
    {
        if (text.StartsWith("data:", StringComparison.Ordinal))
        {
            string mediaType = text.Split(';', 2)[0]["data:".Length..].Trim();
            string shown = mediaType.Length == 0 ? "未知类型" : mediaType;
            return $"[内嵌媒体 {shown}，共 {text.Length} 字符]";
        }
        if (normalizedKey.Contains("base64", StringComparison.Ordinal)
            || normalizedKey.Contains("b64", StringComparison.Ordinal))
        {
            return $"[内嵌编码数据，共 {text.Length} 字符]";
        }
        string? sanitizedUrl = SanitizeUrl(text);
        return sanitizedUrl ?? text;
    }

    private static string? SanitizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || parsed.Host.Length == 0)
        {
            return null;
        }
        if (parsed.Query.Length <= 1)
        {
            return null;
        }
        List<string> pairs = [];
        bool changed = false;
        foreach (string pair in parsed.Query[1..].Split('&'))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string key = equals < 0 ? pair : pair[..equals];
            string normalized = NormalizeKey(Uri.UnescapeDataString(key));
            if (normalized.Contains("token", StringComparison.Ordinal)
                || normalized.Contains("signature", StringComparison.Ordinal)
                || normalized.Contains("apikey", StringComparison.Ordinal)
                || normalized == "key")
            {
                pairs.Add($"{key}=[REDACTED]");
                changed = true;
            }
            else
            {
                pairs.Add(pair);
            }
        }
        return changed
            ? $"{parsed.Scheme}://{parsed.Authority}{parsed.AbsolutePath}?{string.Join("&", pairs)}"
            : null;
    }

    private static string NormalizeKey(string key) => key
        .Replace("_", "", StringComparison.Ordinal)
        .Replace("-", "", StringComparison.Ordinal)
        .ToLowerInvariant();

    private static bool IsValidJson(byte[] data)
    {
        if (data.Length == 0)
        {
            return false;
        }
        try
        {
            using JsonDocument _ = JsonDocument.Parse(data);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Truncate(string value)
    {
        if (value.Length <= MaxPayloadChars)
        {
            return value;
        }
        return value[..MaxPayloadChars] + $"\n[报文已截断，原始长度 {Encoding.UTF8.GetByteCount(value)} 字节]";
    }

    private static int IndexOf(byte[] source, byte[] pattern, int start)
    {
        if (pattern.Length == 0 || source.Length < pattern.Length)
        {
            return -1;
        }
        for (int offset = Math.Max(start, 0); offset <= source.Length - pattern.Length; offset++)
        {
            bool match = true;
            for (int index = 0; index < pattern.Length; index++)
            {
                if (source[offset + index] != pattern[index])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return offset;
            }
        }
        return -1;
    }

    // 与 Go 的 json.MarshalIndent 对齐：非 ASCII 原样输出（不转义 \uXXXX），
    // HTML 字符也不转义——报文是排障用日志内容，不进 HTML 上下文。
    private static readonly JsonSerializerOptions JsonIndentOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
