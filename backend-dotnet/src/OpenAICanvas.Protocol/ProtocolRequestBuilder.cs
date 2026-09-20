#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Protocol;

/// <summary>
/// 声明式协议的请求体构建、URL 拼接与鉴权签名。
/// 对应 Go: <c>internal/app/provider_protocol.go</c> 的
/// <c>protocolRequestBody</c> / <c>protocolFormValues</c> / <c>safeProtocolFilename</c> /
/// <c>protocolRequestURL</c> / <c>appendProtocolQuery</c> /
/// <c>signProtocolAWSV4</c> / <c>signProtocolTC3</c> 及其散列辅助函数。
/// </summary>
/// <remarks>
/// 这些是 4.7–4.10 所有声明式协议共用的传输底座：签名算法逐字节敏感，
/// 任何一处（如 canonical header 的排序、空格折叠、路径转义）出错都只会表现为上游 403，
/// 极难定位，因此实现上严格对齐 Go。
/// </remarks>
public static class ProtocolRequestBuilder
{
    /// <summary>
    /// 按 ContentType 构建请求体，返回 (内容, 实际 Content-Type)。
    /// 内容为 <c>null</c> 表示无请求体。
    /// 对应 Go: <c>protocolRequestBody</c>。
    /// </summary>
    /// <param name="mediaLoader">
    /// 读取参考素材字节，返回 (原始字节, 嗅探到的 MIME)。multipart 文件部分需要。
    /// </param>
    public static (byte[]? Payload, string ContentType) BuildBody(
        string contentType,
        object? body,
        IReadOnlyList<RequestFilePart>? files,
        Func<MediaReference, (byte[] Raw, string MIMEType)>? mediaLoader = null)
    {
        string normalized = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (body is null && (files is null || files.Count == 0))
        {
            return (null, "");
        }

        switch (normalized)
        {
            case "":
            case "application/json":
            {
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(body, ProtocolJson.WriteOptions);
                return (data, "application/json");
            }
            case "application/x-www-form-urlencoded":
            {
                Dictionary<string, object?> payload = BodyObject(body);
                List<string> pairs = [];
                foreach ((string key, object? value) in payload)
                {
                    foreach (string item in FormValues(value))
                    {
                        pairs.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(item));
                    }
                }
                // 与 Go 的 url.Values.Encode() 一致：按键排序（Go 按 key 排序，同键保持插入序）。
                pairs.Sort(StringComparer.Ordinal);
                return (Encoding.UTF8.GetBytes(string.Join('&', pairs)), normalized);
            }
            case "multipart/form-data":
            {
                Dictionary<string, object?> payload = BodyObject(body);
                using MemoryStream buffer = new();
                string boundary = "----OpenAICanvasBoundary" + Guid.NewGuid().ToString("N");
                byte[] boundaryBytes = Encoding.UTF8.GetBytes("--" + boundary + "\r\n");

                // 与 Go 一致：文本字段按键排序写入。
                List<string> keys = [.. payload.Keys];
                keys.Sort(StringComparer.Ordinal);
                foreach (string key in keys)
                {
                    foreach (string item in FormValues(payload[key]))
                    {
                        buffer.Write(boundaryBytes);
                        WriteAscii(buffer, $"Content-Disposition: form-data; name=\"{key}\"\r\n\r\n");
                        buffer.Write(Encoding.UTF8.GetBytes(item));
                        buffer.Write("\r\n"u8);
                    }
                }

                foreach (RequestFilePart file in files ?? [])
                {
                    if (mediaLoader is null)
                    {
                        throw new InvalidOperationException(
                            $"读取 multipart 文件 {file.Name} 失败：未提供媒体加载器");
                    }
                    (byte[] data, string detectedMime) = mediaLoader(file.Reference);
                    string filename = SafeProtocolFilename(file.Filename);
                    string mimeType = file.MIMEType.Trim();
                    if (mimeType.Length == 0)
                    {
                        mimeType = detectedMime;
                    }

                    buffer.Write(boundaryBytes);
                    WriteAscii(buffer,
                        $"Content-Disposition: form-data; name=\"{file.Name}\"; filename=\"{filename}\"\r\n");
                    WriteAscii(buffer, "Content-Type: " +
                        (mimeType.Length > 0 ? mimeType : "application/octet-stream") + "\r\n\r\n");
                    buffer.Write(data);
                    buffer.Write("\r\n"u8);
                }

                buffer.Write(Encoding.UTF8.GetBytes("--" + boundary + "--\r\n"));
                return (buffer.ToArray(), "multipart/form-data; boundary=" + boundary);
            }
            case "application/octet-stream":
            {
                switch (body)
                {
                    case byte[] bytes:
                        return (bytes, normalized);
                    case string text when text.StartsWith("data:", StringComparison.Ordinal):
                    {
                        (string mimeType, byte[] data) = DecodeDataURL(text);
                        return (data, mimeType.Length > 0 ? mimeType : normalized);
                    }
                    case string text:
                        return (Encoding.UTF8.GetBytes(text), normalized);
                    default:
                        throw new InvalidOperationException("二进制协议请求体必须是字节或字符串");
                }
            }
            default:
                throw new InvalidOperationException($"声明式协议暂不支持 {contentType} 请求体");
        }
    }

    /// <summary>对应 Go: <c>protocolBodyObject</c>（非对象一律视为空）。</summary>
    public static Dictionary<string, object?> BodyObject(object? value)
    {
        if (value is null)
        {
            return [];
        }
        if (value is Dictionary<string, object?> map)
        {
            return map;
        }
        // 反序列化后的 JsonElement 也接受。
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            return JsonFields.FromElement(element) as Dictionary<string, object?> ?? [];
        }
        return [];
    }

    /// <summary>
    /// 把单个值展平为字符串列表。对应 Go: <c>protocolFormValues</c>。
    /// </summary>
    /// <remarks>
    /// 数字用 <c>'f'</c> 定点格式（与 Go 的 <c>strconv.FormatFloat(v, 'f', -1, 64)</c> 一致，
    /// <b>不使用科学计数法</b>）—— 这一点对上游签名校验很关键。
    /// </remarks>
    public static List<string> FormValues(object? value)
    {
        switch (value)
        {
            case null:
                return [];
            case string text:
                return [text];
            case bool flag:
                return [flag ? "true" : "false"];
            case double number:
                return [FormatFloatGo(number)];
            case float number:
                return [FormatFloatGo(number)];
            case int number:
                return [number.ToString(CultureInfo.InvariantCulture)];
            case long number:
                return [number.ToString(CultureInfo.InvariantCulture)];
            case decimal number:
                return [number.ToString(CultureInfo.InvariantCulture)];
            case JsonElement element when element.ValueKind == JsonValueKind.Array:
            {
                List<string> result = [];
                foreach (JsonElement child in element.EnumerateArray())
                {
                    result.AddRange(FormValues(JsonFields.FromElement(child)));
                }
                return result;
            }
            case IEnumerable<object?> items:
            {
                List<string> result = [];
                foreach (object? item in items)
                {
                    result.AddRange(FormValues(item));
                }
                return result;
            }
            default:
            {
                // 其余类型按 JSON 序列化（与 Go 的 json.Marshal 兜底一致）。
                try
                {
                    return [JsonSerializer.Serialize(value, ProtocolJson.WriteOptions)];
                }
                catch (JsonException)
                {
                    return [];
                }
            }
        }
    }

    /// <summary>
    /// 文件名净化：取最后一段路径、剔除控制字符与双引号，空则回落 <c>upload.bin</c>。
    /// 对应 Go: <c>safeProtocolFilename</c>。
    /// </summary>
    public static string SafeProtocolFilename(string value)
    {
        string trimmed = (value ?? "").Trim();
        int index = trimmed.LastIndexOfAny(['/', '\\']);
        if (index >= 0)
        {
            trimmed = trimmed[(index + 1)..];
        }
        StringBuilder builder = new();
        foreach (char ch in trimmed)
        {
            if (ch < 32 || ch == 127 || ch == '"')
            {
                continue;
            }
            builder.Append(ch);
        }
        string result = builder.ToString();
        return result.Length == 0 ? "upload.bin" : result;
    }

    /// <summary>
    /// 拼接请求 URL：默认走 <c>/{v1 前缀}/{path}</c>，<c>OriginPath</c> 时以 spec.Path 作为根路径。
    /// 对应 Go: <c>protocolRequestURL</c>。
    /// </summary>
    public static string BuildUrl(string baseURL, RequestSpec spec, Func<string, string, string>? apiUrlBuilder = null)
    {
        string withPath;
        if (!spec.OriginPath)
        {
            apiUrlBuilder ??= DefaultApiUrl;
            withPath = apiUrlBuilder(baseURL, spec.Path);
        }
        else
        {
            if (!Uri.TryCreate((baseURL ?? "").Trim(), UriKind.Absolute, out Uri? baseUri)
                || string.IsNullOrEmpty(baseUri.Scheme) || string.IsNullOrEmpty(baseUri.Host))
            {
                throw new InvalidOperationException("协议根路径请求的 Base URL 无效");
            }
            string rawPath = spec.Path ?? "";
            if (!rawPath.StartsWith('/'))
            {
                throw new InvalidOperationException("协议根路径请求必须使用绝对路径");
            }
            // 注意：不能对 "/x" 用 new Uri(path, UriKind.RelativeOrAbsolute) —— 得到的是
            // 相对 Uri，访问 AbsolutePath 会抛 InvalidOperationException。
            // 这里拆出路径与查询串后直接交给 UriBuilder。
            string pathOnly = rawPath;
            string queryOnly = "";
            int queryIndex = rawPath.IndexOf('?');
            if (queryIndex >= 0)
            {
                pathOnly = rawPath[..queryIndex];
                queryOnly = rawPath[(queryIndex + 1)..];
            }

            UriBuilder builder = new(baseUri)
            {
                Path = pathOnly,
                Query = queryOnly,
                Fragment = "",
            };
            withPath = builder.Uri.ToString();
        }
        return AppendQuery(withPath, spec.Query);
    }

    /// <summary>
    /// 追加查询参数（同名累加，保留既有参数）。
    /// 对应 Go: <c>appendProtocolQuery</c>。
    /// </summary>
    public static string AppendQuery(string rawURL, IReadOnlyDictionary<string, List<string>>? values)
    {
        if (values is null || values.Count == 0)
        {
            return rawURL;
        }
        if (!Uri.TryCreate(rawURL, UriKind.Absolute, out Uri? parsed))
        {
            throw new InvalidOperationException("请求 URL 无效");
        }

        List<string> pairs = [];
        // 保留原有查询（Go 的 parsed.Query() 已按 key 排序）。
        string existing = parsed.Query.TrimStart('?');
        if (existing.Length > 0)
        {
            pairs.Add(existing);
        }
        foreach ((string key, List<string> items) in values)
        {
            foreach (string item in items)
            {
                pairs.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(item));
            }
        }

        UriBuilder builder = new(parsed)
        {
            Query = string.Join('&', pairs.Where(pair => pair.Length > 0)),
        };
        return builder.Uri.ToString();
    }

    /// <summary>默认 <c>/{path}</c> 与 BaseURL 的拼接（<c>/v1</c> 前缀由调用方通过 <c>apiUrlBuilder</c> 注入）。</summary>
    private static string DefaultApiUrl(string baseURL, string path)
    {
        string trimmedBase = (baseURL ?? "").TrimEnd('/');
        string trimmedPath = (path ?? "").TrimStart('/');
        return trimmedBase + "/" + trimmedPath;
    }

    // ------------------------------------------------------------ 签名

    /// <summary>
    /// AWS SigV4 签名。对应 Go: <c>signProtocolAWSV4</c>。
    /// </summary>
    /// <param name="timestamp">用于测试注入；传 <c>null</c> 取当前 UTC。</param>
    public static void SignAwsV4(
        HttpRequestMessage request,
        string accessKey,
        string secretKey,
        ManifestAuth auth,
        byte[]? payload = null,
        DateTimeOffset? timestamp = null)
    {
        if (accessKey.Trim().Length == 0 || secretKey.Trim().Length == 0)
        {
            throw new InvalidOperationException("AWS SigV4 鉴权需要 Access Key ID 和 Secret Access Key");
        }
        string serviceName = auth.Service.Trim().Length > 0 ? auth.Service.Trim() : "bedrock";
        string region = auth.Region.Trim();
        if (region.Length == 0)
        {
            string host = request.RequestUri?.Host ?? "";
            string[] parts = host.ToLowerInvariant().Split('.');
            for (int index = 0; index < parts.Length; index++)
            {
                if (parts[index].StartsWith(serviceName, StringComparison.Ordinal) && index + 1 < parts.Length)
                {
                    region = parts[index + 1];
                    break;
                }
            }
        }
        if (region.Length == 0)
        {
            throw new InvalidOperationException(
                "AWS SigV4 鉴权无法从 Base URL 推断 region，请使用包含区域的 Bedrock Runtime 地址");
        }

        byte[] body = payload ?? [];
        DateTimeOffset now = timestamp ?? DateTimeOffset.UtcNow;
        string amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string payloadHash = Sha256Hex(body);

        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        request.Headers.TryAddWithoutValidation("X-Amz-Content-Sha256", payloadHash);

        (string canonicalHeaders, string signedHeaders) = CanonicalHeaders(request);
        string canonicalRequest = string.Join('\n',
            request.Method.Method,
            EscapedPath(request.RequestUri),
            CanonicalQuery(request.RequestUri),
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        string scope = string.Join('/', dateStamp, region, serviceName, "aws4_request");
        string stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256", amzDate, scope, Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));

        byte[] dateKey = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        byte[] regionKey = HmacSha256(dateKey, region);
        byte[] serviceKey = HmacSha256(regionKey, serviceName);
        byte[] signingKey = HmacSha256(serviceKey, "aws4_request");
        string signature = Convert.ToHexString(HmacSha256(signingKey, stringToSign)).ToLowerInvariant();

        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    /// <summary>
    /// 腾讯云 TC3 签名。对应 Go: <c>signProtocolTC3</c>。
    /// </summary>
    public static void SignTc3(
        HttpRequestMessage request,
        string secretID,
        string secretKey,
        ManifestAuth auth,
        byte[]? payload = null,
        DateTimeOffset? timestamp = null)
    {
        if (secretID.Trim().Length == 0 || secretKey.Trim().Length == 0)
        {
            throw new InvalidOperationException("腾讯云 TC3 鉴权需要 SecretId 和 SecretKey");
        }
        string serviceName = auth.Service.Trim().Length > 0 ? auth.Service.Trim() : "hunyuan";
        byte[] body = payload ?? [];
        DateTimeOffset now = timestamp ?? DateTimeOffset.UtcNow;
        long unix = now.ToUnixTimeSeconds();
        string dateStamp = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        string contentType = request.Content?.Headers.ContentType?.ToString() ?? "";
        if (contentType.Length == 0)
        {
            contentType = "application/json";
        }
        request.Content?.Headers.Remove("Content-Type");
        // 与 Go 一致：Content-Type 头参与签名，且以 host 头为第二项。
        request.Headers.TryAddWithoutValidation("X-TC-Timestamp", unix.ToString(CultureInfo.InvariantCulture));
        if (auth.Region.Trim().Length > 0)
        {
            request.Headers.TryAddWithoutValidation("X-TC-Region", auth.Region.Trim());
        }

        string host = (request.RequestUri?.Host ?? "").ToLowerInvariant();
        string canonicalHeaders =
            "content-type:" + contentType.Trim().ToLowerInvariant() + "\n" +
            "host:" + host + "\n";
        const string signedHeaders = "content-type;host";
        string canonicalRequest = string.Join('\n',
            request.Method.Method,
            EscapedPath(request.RequestUri),
            CanonicalQuery(request.RequestUri),
            canonicalHeaders,
            signedHeaders,
            Sha256Hex(body));

        string scope = dateStamp + "/" + serviceName + "/tc3_request";
        string stringToSign = string.Join('\n',
            "TC3-HMAC-SHA256", unix.ToString(CultureInfo.InvariantCulture), scope,
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));

        byte[] secretDate = HmacSha256(Encoding.UTF8.GetBytes("TC3" + secretKey), dateStamp);
        byte[] secretService = HmacSha256(secretDate, serviceName);
        byte[] secretSigning = HmacSha256(secretService, "tc3_request");
        string signature = Convert.ToHexString(HmacSha256(secretSigning, stringToSign)).ToLowerInvariant();

        request.Headers.TryAddWithoutValidation("Authorization",
            $"TC3-HMAC-SHA256 Credential={secretID}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    /// <summary>
    /// 构造 SigV4 的 canonical headers 与 signed headers。
    /// 对应 Go: <c>protocolCanonicalHeaders</c>。
    /// </summary>
    /// <remarks>
    /// 顺序敏感：host 固定在内；跳过 authorization/user-agent/content-length/expect；
    /// 多值以逗号连接；值内连续空白折叠为单个空格；键小写后按序排列。
    /// </remarks>
    public static (string Canonical, string Signed) CanonicalHeaders(HttpRequestMessage request)
    {
        SortedDictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["host"] = (request.RequestUri?.Host ?? "").ToLowerInvariant(),
        };
        foreach ((string name, IEnumerable<string> entries) in request.Headers)
        {
            string lower = name.Trim().ToLowerInvariant();
            if (lower is "authorization" or "user-agent" or "content-length" or "expect")
            {
                continue;
            }
            List<string> cleaned = [];
            foreach (string entry in entries)
            {
                cleaned.Add(CollapseWhitespace(entry));
            }
            values[lower] = string.Join(',', cleaned);
        }
        if (request.Content is not null)
        {
            foreach ((string name, IEnumerable<string> entries) in request.Content.Headers)
            {
                string lower = name.Trim().ToLowerInvariant();
                if (lower is "authorization" or "user-agent" or "content-length" or "expect")
                {
                    continue;
                }
                List<string> cleaned = [];
                foreach (string entry in entries)
                {
                    cleaned.Add(CollapseWhitespace(entry));
                }
                values[lower] = string.Join(',', cleaned);
            }
        }

        StringBuilder canonical = new();
        foreach ((string key, string value) in values)
        {
            canonical.Append(key).Append(':').Append(value).Append('\n');
        }
        return (canonical.ToString(), string.Join(';', values.Keys));
    }

    /// <summary>对应 Go 的 <c>strings.Join(strings.Fields(s), " ")</c>（所有空白折叠为单空格）。</summary>
    private static string CollapseWhitespace(string value)
    {
        StringBuilder builder = new();
        bool pendingSpace = false;
        foreach (char ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(ch);
        }
        return builder.ToString();
    }

    /// <summary>对应 Go 的 <c>req.URL.EscapedPath()</c>（空则回 "/"）。</summary>
    private static string EscapedPath(Uri? uri)
    {
        string path = uri?.AbsolutePath ?? "";
        return path.Length == 0 ? "/" : path;
    }

    /// <summary>对应 Go 的 <c>req.URL.Query().Encode()</c>。</summary>
    private static string CanonicalQuery(Uri? uri)
    {
        string query = uri?.Query.TrimStart('?') ?? "";
        if (query.Length == 0)
        {
            return "";
        }
        // 按 key 排序后重组，等价 Go 的 url.Values.Encode()。
        List<(string Key, string Value)> pairs = [];
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            string key = equals >= 0 ? part[..equals] : part;
            string value = equals >= 0 ? part[(equals + 1)..] : "";
            pairs.Add((Uri.UnescapeDataString(key), Uri.UnescapeDataString(value)));
        }
        pairs.Sort((left, right) =>
        {
            int byKey = string.CompareOrdinal(left.Key, right.Key);
            return byKey != 0 ? byKey : string.CompareOrdinal(left.Value, right.Value);
        });
        return string.Join('&', pairs.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    }

    /// <summary>对应 Go: <c>protocolHMAC</c>。</summary>
    internal static byte[] HmacSha256(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    /// <summary>对应 Go: <c>sha256Hex</c>。</summary>
    internal static string Sha256Hex(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    /// <summary>
    /// 按凭证字段名取凭证：secret 系列名取 SecretKey，其余取 APIKey。
    /// 对应 Go: <c>protocolCredentialField</c>。
    /// </summary>
    public static string CredentialField(ProviderCredentials credentials, string field) =>
        field.Trim().ToLowerInvariant() switch
        {
            "secretkey" or "secret_key" or "secret" => credentials.SecretKey.Trim(),
            _ => credentials.APIKey.Trim(),
        };

    /// <summary>解析 <c>data:</c> URL 为 (MIME, 字节)。对应 Go: <c>decodeProviderDataURL</c>。</summary>
    public static (string MIMEType, byte[] Data) DecodeDataURL(string value)
    {
        if (!value.StartsWith("data:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("data URL 格式错误");
        }
        int comma = value.IndexOf(',');
        if (comma < 0)
        {
            throw new InvalidOperationException("data URL 格式错误");
        }
        string header = value["data:".Length..comma];
        string declared = header;
        int semicolon = declared.IndexOf(';');
        if (semicolon >= 0)
        {
            declared = declared[..semicolon];
        }
        declared = declared.Trim();
        try
        {
            return (declared, Convert.FromBase64String(value[(comma + 1)..]));
        }
        catch (FormatException error)
        {
            throw new InvalidOperationException($"data URL base64 解码失败：{error.Message}", error);
        }
    }

    /// <summary>
    /// 等价 Go 的 <c>strconv.FormatFloat(v, 'f', -1, 64)</c>：定点、无多余尾零、不用科学计数法。
    /// </summary>
    public static string FormatFloatGo(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
        // "R" 保证往返精度；定点格式化后去掉多余的尾零。
        string text = value.ToString("0.##################################", CultureInfo.InvariantCulture);
        if (text.Contains('E') || text.Contains('e'))
        {
            text = value.ToString("F99", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        }
        return text.Length == 0 ? "0" : text;
    }

    private static void WriteAscii(Stream stream, string text) =>
        stream.Write(Encoding.ASCII.GetBytes(text));
}

/// <summary>声明式协议共用的 JSON 选项。对应 Go 的 <c>json.Marshal</c> 默认行为。</summary>
public static class ProtocolJson
{
    /// <summary>不转义非 ASCII（Go 默认输出 UTF-8 原字符），可选字段为 null 时省略。</summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 序列化为 JSON 文本，与 Go 的 <c>json.Marshal</c> 语义对齐：
    /// 非 ASCII 原样输出、<c>null</c> 值省略、map 键按字典序（STJ 对字典亦然）。
    /// </summary>
    public static string Serialize(object? value) =>
        JsonSerializer.Serialize(value, WriteOptions);
}
