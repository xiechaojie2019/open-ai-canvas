#nullable enable
using System.Text;
using OpenAICanvas.Outbound;
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Providers;

/// <summary>
/// 声明式协议请求的执行器：把 manifest 声明的 <see cref="RequestSpec"/> 变成真实出站请求。
/// 对应 Go: <c>internal/app/provider_protocol.go</c> 的
/// <c>executeProtocolRequest</c> / <c>executeProtocolBinaryRequest</c> /
/// <c>executeProtocolBinaryRequestWithConsumer</c> / <c>applyProtocolAuth</c>。
/// </summary>
/// <remarks>
/// <b>这是插件与宿主网络能力之间的边界。</b>
/// manifest 只能声明 method/path/body/auth；最终 URL 校验、凭证注入、SSRF 防护、超时、
/// 大小限制与审计一律由宿主执行 —— 插件无法通过自定义请求规格绕过这些约束。
/// </remarks>
public static class ProviderProtocolExecutor
{
    /// <summary>
    /// 执行声明式请求并返回响应体。
    /// 对应 Go: <c>executeProtocolRequest</c>。
    /// </summary>
    public static async Task<byte[]> ExecuteAsync(
        ProviderConfig config,
        RequestSpec spec,
        ProviderProtocolMediaLoader? mediaLoader = null,
        CancellationToken cancellationToken = default)
    {
        (byte[] data, _) = await ExecuteWithMimeTypeAsync(
            config, spec, null, mediaLoader, ProtocolCredentialsFor(config), cancellationToken).ConfigureAwait(false);
        return data;
    }

    /// <summary>
    /// 执行声明式请求，返回 (响应体, MIME 类型)。可传入分片观察回调用于 SSE 流式解析。
    /// 对应 Go: <c>executeProtocolBinaryRequestWithConsumer</c>。
    /// </summary>
    public static async Task<(byte[] Data, string MIMEType)> ExecuteWithMimeTypeAsync(
        ProviderConfig config,
        RequestSpec spec,
        Action<string, byte[]>? consume,
        ProviderProtocolMediaLoader? mediaLoader = null,
        ProviderCredentials? credentials = null,
        CancellationToken cancellationToken = default,
        Func<HttpClient>? clientFactory = null)
    {
        // 校验优先：method 必须已声明且受支持。
        ValidateSpec(spec);

        string method = spec.Method.Trim().ToUpperInvariant();
        Func<MediaReference, (byte[] Raw, string MIMEType)>? loader =
            mediaLoader is null ? null : (reference => mediaLoader(reference));
        (byte[]? payload, string contentType) = ProtocolRequestBuilder.BuildBody(
            spec.ContentType ?? "", spec.Body, spec.Files, loader);

        string requestUrl = ProtocolRequestBuilder.BuildUrl(config.BaseURL, spec, ApiUrlBuilder);

        HttpRequestMessage request = new(new HttpMethod(method), requestUrl);
        if (payload is not null)
        {
            request.Content = new ByteArrayContent(payload);
            if (contentType.Length > 0)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }
        foreach ((string name, string value) in spec.Headers ?? [])
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        OutboundHttpClient.ApplyHeaders(request, config.Headers);
        ApplyProtocolAuth(request, config, spec.Auth, credentials ?? ProtocolCredentialsFor(config), payload, contentType);

        using (request)
        {
            if (consume is not null)
            {
                request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            }
            ProviderTransport.OutboundResult result = await ProviderTransport
                .SendAsync(
                    request,
                    ProviderTransport.DefaultMaxResponseBytes,
                    consume,
                    cancellationToken,
                    clientFactory)
                .ConfigureAwait(false);
            return (result.Data, result.MIMEType);
        }
    }

    /// <summary>
    /// 校验请求规格。<c>method</c> 必须是已声明的受支持动词。
    /// 对应 Go: <c>RequestSpec.Validate</c>。
    /// </summary>
    public static void ValidateSpec(RequestSpec spec)
    {
        string method = (spec.Method ?? "").Trim().ToUpperInvariant();
        if (method.Length == 0)
        {
            throw new InvalidOperationException("协议请求缺少 method");
        }
        if (Array.IndexOf(SupportedMethods, method) < 0)
        {
            throw new InvalidOperationException($"协议请求 method {spec.Method} 不受支持");
        }
    }

    /// <summary>宿主允许的 HTTP 动词白名单。对应 Go: <c>protocol</c> 包中的方法集合。</summary>
    public static readonly string[] SupportedMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];

    /// <summary>
    /// 声明式鉴权注入。对应 Go: <c>applyProtocolAuth</c>。
    /// </summary>
    /// <remarks>
    /// <c>auth.type</c> 为空时回落到渠道默认鉴权（<see cref="ProviderTransport.ApplyProviderAuth"/>），
    /// 这样未声明 auth 的 manifest 不会变成"无鉴权裸请求"。
    /// </remarks>
    public static void ApplyProtocolAuth(
        HttpRequestMessage request,
        ProviderConfig config,
        ManifestAuth? auth,
        ProviderCredentials credentials,
        byte[]? payload = null,
        string contentType = "")
    {
        string typeName = (auth?.Type ?? "").Trim().ToLowerInvariant();
        if (typeName.Length == 0)
        {
            ProviderTransport.ApplyProviderAuth(request, config);
            return;
        }

        string credential = ProtocolRequestBuilder.CredentialField(credentials, auth!.Field);
        switch (typeName)
        {
            case "none":
                return;
            case "bearer":
            {
                string header = DefaultString(auth.Header.Trim(), "Authorization");
                string prefix = auth.Prefix.Length == 0 ? "Bearer " : auth.Prefix;
                SetHeader(request, header, prefix + credential);
                return;
            }
            case "header":
            case "api-key":
            case "apikey":
            {
                string header = auth.Header.Trim();
                if (header.Length == 0)
                {
                    throw new InvalidOperationException("插件 header 鉴权缺少 header 名称");
                }
                SetHeader(request, header, auth.Prefix + credential);
                return;
            }
            case "query":
            {
                string name = DefaultString(auth.Query.Trim(), auth.Field.Trim());
                if (name.Length == 0)
                {
                    throw new InvalidOperationException("插件 query 鉴权缺少参数名");
                }
                AddQueryParameter(request, name, auth.Prefix + credential);
                return;
            }
            case "basic":
            {
                string username = auth.Username.Length == 0 ? credential : auth.Username;
                string password = ProtocolRequestBuilder.CredentialField(credentials, auth.SecretField);
                string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
                SetHeader(request, "Authorization", "Basic " + token);
                return;
            }
            case "anthropic":
                SetHeader(request, DefaultString(auth.Header.Trim(), "x-api-key"), credential);
                if (!request.Headers.Contains("anthropic-version"))
                {
                    SetHeader(request, "anthropic-version", "2023-06-01");
                }
                return;
            case "google-api-key":
            case "gemini":
                SetHeader(request, DefaultString(auth.Header.Trim(), "x-goog-api-key"), credential);
                return;
            case "aws-sigv4":
            {
                string secret = ProtocolRequestBuilder.CredentialField(credentials, auth.SecretField);
                ProtocolRequestBuilder.SignAwsV4(request, credential, secret, auth, payload);
                return;
            }
            case "tc3":
            {
                string secret = ProtocolRequestBuilder.CredentialField(credentials, auth.SecretField);
                // SignTc3 的 content-type 取自请求本身，与 Go 一致（签名覆盖该头）。
                ProtocolRequestBuilder.SignTc3(request, credential, secret, auth, payload);
                return;
            }
            default:
                throw new InvalidOperationException($"插件声明了尚未启用的鉴权驱动 {auth.Type}");
        }
    }

    /// <summary>把渠道配置投影为签名所需的凭证。</summary>
    public static ProviderCredentials ProtocolCredentialsFor(ProviderConfig config) =>
        new(config.APIKey, config.SecretKey);

    /// <summary>
    /// 渠道 URL 拼接（含 <c>/v1</c> 版本前缀归一）。
    /// 对应 Go: <c>apiURL</c>（通过 <c>apiUrlBuilder</c> 注入 <c>ProtocolRequestBuilder.BuildUrl</c>）。
    /// </summary>
    public static string ApiUrlBuilder(string baseUrl, string path) =>
        ProviderTransport.ChannelApiUrl(baseUrl, path);

    /// <summary>对应 Go: <c>defaultString</c>。</summary>
    private static string DefaultString(string value, string fallback) =>
        value.Length == 0 ? fallback : value;

    private static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }

    private static void AddQueryParameter(HttpRequestMessage request, string name, string value)
    {
        Uri uri = request.RequestUri!;
        string existing = uri.Query.TrimStart('?');
        string appended = Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
        string query = existing.Length == 0 ? appended : existing + "&" + appended;
        UriBuilder builder = new(uri) { Query = query };
        request.RequestUri = builder.Uri;
    }
}

/// <summary>
/// 参考素材字节加载器（multipart 文件部分需要）。
/// 对应 Go 的 <c>protocolMediaBytes(ctx, config, reference)</c>。
/// </summary>
/// <param name="reference">素材引用。</param>
/// <returns>原始字节与嗅探/声明的 MIME。</returns>
public delegate (byte[] Raw, string MIMEType) ProviderProtocolMediaLoader(MediaReference reference);
