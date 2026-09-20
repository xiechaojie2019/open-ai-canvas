#nullable enable
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenAICanvas.Outbound;
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Providers;

/// <summary>
/// Provider 出站响应的统一安全边界（大小上限 / 非 2xx / 媒体分片观察 / SSRF 与超时）。
/// 对应 Go: <c>internal/app/provider_http_client.go</c> 的
/// <c>doBinaryWithConsumer</c> / <c>doBinary</c> / <c>doJSON</c>。
/// </summary>
/// <remarks>
/// 这里是 JSON、SSE 与媒体下载<b>共同的收口点</b>：任何上游响应都必须先经过本类，
/// 才能进入协议解析。渠道并发槽、熔断与计费审计（Go 的 <c>providerAnalyticsContext</c>）
/// 属 4.10 的运行时接线，本类只保留可独立验证的传输语义。
/// </remarks>
public static class ProviderTransport
{
    /// <summary>上游响应字节上限默认值。对应 Go: <c>maxProviderResponseBytes</c>。</summary>
    public const long DefaultMaxResponseBytes = 64L << 20;

    /// <summary>读取分片大小。对应 Go 的 <c>32&lt;&lt;10</c>。</summary>
    private const int ChunkSize = 32 << 10;

    /// <summary>默认出站 User-Agent。对应 Go: <c>outbound.DefaultOutboundUserAgent</c>。</summary>
    public const string DefaultUserAgent = "InfiniteCanvas/1.0 (+https://github.com/ddcat-ai/open-ai-canvas)";

    /// <summary>
    /// 出站响应。对应 Go 的 <c>(data []byte, mimeType string, err error)</c> 三元组。
    /// </summary>
    public sealed record OutboundResult(byte[] Data, string MIMEType);

    /// <summary>
    /// 执行请求并读取完整响应体，是 Provider 出站的唯一收口。
    /// 对应 Go: <c>doBinaryWithConsumer</c>。
    /// </summary>
    /// <param name="onChunk">
    /// 分片观察回调（MIME, 已读分片）。<b>只观察</b>，不会绕过大小上限或错误判定 ——
    /// 与 Go 的注释一致：流式解析拿到的是已读片段，最终仍按完整响应判定。
    /// </param>
    /// <param name="clientFactory">
    /// 出站客户端工厂。<c>null</c> 时用 <see cref="OutboundHttpClient.Create"/>（生产路径）；
    /// 测试注入 <see cref="HttpClient"/> 以覆盖大小上限 / 非 2xx / 分片回调等分支。
    /// </param>
    public static async Task<OutboundResult> SendAsync(
        HttpRequestMessage request,
        long maxResponseBytes = DefaultMaxResponseBytes,
        Action<string, byte[]>? onChunk = null,
        CancellationToken cancellationToken = default,
        Func<HttpClient>? clientFactory = null)
    {
        using HttpClient client = (clientFactory ?? (() => OutboundHttpClient.Create()))();
        HttpResponseMessage response;
        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or SocketException or InvalidOperationException)
        {
            throw new ProviderTransportException(ProviderErrorMessages.NetworkFailure, error);
        }

        using (response)
        {
            string mimeType = response.Content.Headers.ContentType?.ToString() ?? "";

            long? declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength > maxResponseBytes)
            {
                throw OversizeError(maxResponseBytes);
            }

            byte[] data = await ReadBodyAsync(response, mimeType, maxResponseBytes, onChunk, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderHttpException(
                    (int)response.StatusCode,
                    $"{(int)response.StatusCode} {response.ReasonPhrase}",
                    Encoding.UTF8.GetString(data),
                    OutboundHttpClient.ParseRetryAfter(
                        response.Headers.RetryAfter?.ToString(), DateTimeOffset.UtcNow));
            }
            return new OutboundResult(data, mimeType);
        }
    }

    /// <summary>
    /// 读取响应体，边读边执行大小上限与分片回调。
    /// 对应 Go 的读循环（含"读满才判定超限"和"读完再复查一次"两道检查）。
    /// </summary>
    private static async Task<byte[]> ReadBodyAsync(
        HttpResponseMessage response,
        string mimeType,
        long maxResponseBytes,
        Action<string, byte[]>? onChunk,
        CancellationToken cancellationToken)
    {
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[ChunkSize];

        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                if (buffer.Length + read > maxResponseBytes)
                {
                    throw OversizeError(maxResponseBytes);
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                onChunk?.Invoke(mimeType, chunk[..read]);
            }
            if (read == 0)
            {
                break;
            }
        }
        return buffer.ToArray();
    }

    /// <summary>对应 Go 的 <c>fmt.Errorf("上游响应超过 %s 限制", formatStorageLimit(limit))</c>。</summary>
    public static string OversizeMessage(long limit) =>
        $"上游响应超过 {FormatStorageLimit(limit)} 限制";

    private static ProviderTransportException OversizeError(long limit) =>
        new(OversizeMessage(limit), null);

    /// <summary>
    /// 与 Go <c>formatStorageLimit</c> 同构：整 GB 用 GB，否则用 MB。
    /// 对应 <c>internal/app/upload_quota.go</c>（此处内联一份，避免 Providers → Application 的反向依赖）。
    /// </summary>
    public static string FormatStorageLimit(long value) =>
        value % (1L << 30) == 0
            ? $"{value >> 30}GB"
            : $"{value >> 20}MB";

    /// <summary>
    /// 读取并解析 JSON 响应，含媒体类型与结构校验。
    /// 对应 Go: <c>doJSON</c>。
    /// </summary>
    /// <remarks>
    /// <b>媒体类型不是 json 且内容也不是合法 JSON 时</b>才判定为"非 JSON 内容"：
    /// 有些上游把 JSON 标成 <c>text/plain</c>，只看头会误杀。
    /// </remarks>
    public static async Task<Dictionary<string, object?>> SendJsonAsync(
        HttpRequestMessage request,
        long maxResponseBytes = DefaultMaxResponseBytes,
        CancellationToken cancellationToken = default,
        Func<HttpClient>? clientFactory = null)
    {
        OutboundResult result = await SendAsync(request, maxResponseBytes, null, cancellationToken, clientFactory)
            .ConfigureAwait(false);

        string mimeType = result.MIMEType;
        if (!mimeType.Contains("json", StringComparison.OrdinalIgnoreCase)
            && !IsValidJson(result.Data))
        {
            throw new ProviderResponseDecodeException(
                new InvalidOperationException($"接口返回非 JSON 内容：{mimeType}"));
        }

        Dictionary<string, object?>? payload = ParseObject(result.Data);
        if (payload is null)
        {
            throw new ProviderResponseDecodeException(
                new InvalidOperationException("接口返回的 JSON 不是对象"));
        }

        // 与 Go 一致：JSON 解析成功后立刻做业务失败判定（error 对象含非空 message）。
        Dictionary<string, object?>? errorValue = JsonFields.NestedObject(payload, "error");
        if (errorValue is not null)
        {
            string message = JsonFields.StringField(errorValue, "message");
            if (message.Length > 0)
            {
                throw new ProviderPayloadException(message, ProviderErrorMessages.PayloadError(message));
            }
        }
        return payload;
    }

    /// <summary>把字节解析为 Go 风格载荷；非对象或非法 JSON 返回 <c>null</c>。</summary>
    public static Dictionary<string, object?>? ParseObject(byte[] data)
    {
        if (data.Length == 0)
        {
            return null;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return JsonFields.FromElement(document.RootElement) as Dictionary<string, object?>;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>对应 Go 的 <c>json.Valid(data)</c>。</summary>
    public static bool IsValidJson(byte[] data)
    {
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

    /// <summary>
    /// 按渠道鉴权方式附加请求头。
    /// 对应 Go: <c>applyProviderAuth</c>。
    /// </summary>
    /// <remarks>
    /// <c>claude</c> 用 <c>x-api-key</c> + 固定 <c>anthropic-version</c>，
    /// <c>gemini</c> 用 <c>x-goog-api-key</c>，其余走 <c>Authorization: Bearer</c>。
    /// </remarks>
    public static void ApplyProviderAuth(HttpRequestMessage request, ProviderConfig config)
    {
        string apiFormat = config.APIFormat;
        if (apiFormat == "claude")
        {
            request.Headers.TryAddWithoutValidation("x-api-key", config.APIKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            return;
        }
        if (apiFormat == "gemini")
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", config.APIKey);
            return;
        }
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + config.APIKey);
    }

    /// <summary>
    /// 补默认 User-Agent（仅在调用方未设置时）。
    /// 对应 Go: <c>ApplyDefaultOutboundHeaders</c>。
    /// </summary>
    /// <remarks>
    /// 不能用 <c>request.Headers.UserAgent.Count == 0</c> 判断空 —— .NET 的
    /// <see cref="HttpRequestMessage"/> 会预置一个默认 User-Agent，该集合恒不为空。
    /// 必须按 Go 的语义判断<b>头值是否为空字符串</b>。
    /// </remarks>
    public static void ApplyDefaultHeaders(HttpRequestMessage request)
    {
        bool hasCustom = request.Headers.TryGetValues("User-Agent", out IEnumerable<string>? values)
            && values.Any(value => value.Trim().Length > 0);
        if (!hasCustom)
        {
            // .NET 的 User-Agent 是强类型头：HttpRequestMessage 会预置一条空值，
            // 必须整条移除（Clear 不够）；写入时框架会按空格拆成多个 product token，
            // 发送出去后仍以空格拼回，与 Go 整串写入的线格式一致。
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        }
    }

    /// <summary>
    /// 读回首部 User-Agent 的线格式文本（多个 token 以空格拼接）。
    /// 用于断言与 Go 的整串语义一致。
    /// </summary>
    public static string ReadUserAgent(HttpRequestMessage request) =>
        string.Join(' ', request.Headers.TryGetValues("User-Agent", out IEnumerable<string>? values)
            ? values
            : []);

    /// <summary>
    /// 构造出站 JSON POST 请求（URL 拼接 + 鉴权 + 内容类型 + 自定义头）。
    /// 对应 Go: <c>postJSON</c> 的请求装配部分。
    /// </summary>
    public static HttpRequestMessage BuildJsonPost(
        ProviderConfig config,
        string path,
        object body,
        bool streaming = false)
    {
        string url = ChannelApiUrl(config.BaseURL, path);
        HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(ProtocolJson.Serialize(body), Encoding.UTF8, "application/json"),
        };
        ApplyProviderAuth(request, config);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        if (streaming)
        {
            request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        }
        ApplyDefaultHeaders(request);
        OutboundHttpClient.ApplyHeaders(request, config.Headers);
        return request;
    }

    /// <summary>
    /// 对应 Go: <c>ChannelAPIURL</c> / <c>apiURL</c>（默认版本前缀 <c>/v1</c>）。
    /// </summary>
    /// <remarks>
    /// 不能直接用 <c>ProtocolRequestBuilder.BuildUrl</c> 的默认拼接（那是"base+path"的朴素形态）：
    /// 渠道地址可能已带 <c>/v1</c>、<c>/v1/</c> 或显式 <c>/v2</c>，必须走版本前缀归一。
    /// </remarks>
    public static string ChannelApiUrl(string baseUrl, string path) =>
        ApiUrlWithDefaultPrefix(baseUrl, path, "/v1");

    /// <summary>对应 Go: <c>apiURLWithDefaultPrefix</c>（请求路径显式版本优先于 baseURL 残留版本）。</summary>
    public static string ApiUrlWithDefaultPrefix(string baseUrl, string path, string defaultPrefix)
    {
        string trimmedBase = (baseUrl ?? "").Trim().TrimEnd('/');
        string requestPath = (path ?? "").Trim();
        if (requestPath.Length == 0)
        {
            return trimmedBase;
        }
        if (!requestPath.StartsWith('/'))
        {
            requestPath = "/" + requestPath;
        }

        string requestPrefix = RequestApiPathPrefix(requestPath);
        string basePrefix = BaseApiPathPrefix(trimmedBase);
        if (requestPrefix.Length > 0)
        {
            if (basePrefix == requestPrefix)
            {
                return trimmedBase + requestPath[requestPrefix.Length..];
            }
            // 请求路径显式版本优先：base=/v1、path=/v2/... 时必须切到 /v2。
            return trimmedBase.EndsWith(basePrefix, StringComparison.Ordinal)
                ? trimmedBase[..^basePrefix.Length] + requestPath
                : trimmedBase + requestPath;
        }
        if (basePrefix.Length > 0)
        {
            return trimmedBase + requestPath;
        }
        return trimmedBase + defaultPrefix + requestPath;
    }

    /// <summary>对应 Go: <c>channelAPIPrefixes</c>（匹配顺序即声明顺序）。</summary>
    private static readonly string[] ChannelApiPrefixes =
        ["/api/plan/v3", "/api/v3", "/api/v1", "/v1beta", "/v1", "/v2", "/v3"];

    private static string RequestApiPathPrefix(string value)
    {
        string lower = value.ToLowerInvariant();
        foreach (string prefix in ChannelApiPrefixes)
        {
            if (lower == prefix || lower.StartsWith(prefix + "/", StringComparison.Ordinal) ||
                lower.StartsWith(prefix + "?", StringComparison.Ordinal) ||
                lower.StartsWith(prefix + "#", StringComparison.Ordinal))
            {
                return prefix;
            }
        }
        return "";
    }

    private static string BaseApiPathPrefix(string value)
    {
        string lower = value.TrimEnd('/').ToLowerInvariant();
        foreach (string prefix in ChannelApiPrefixes)
        {
            if (lower == prefix || lower.EndsWith(prefix, StringComparison.Ordinal))
            {
                return prefix;
            }
        }
        return "";
    }
}

/// <summary>
/// 传输层失败（网络不可达、响应超限等）。文案已是可对外展示的固定提示。
/// </summary>
public sealed class ProviderTransportException : Exception
{
    public ProviderTransportException(string message, Exception? cause)
        : base(message, cause)
    {
    }
}
