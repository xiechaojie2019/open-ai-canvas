#nullable enable
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenAICanvas.Application;
using OpenAICanvas.Auth;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Outbound;
using OpenAICanvas.Platform;
using OpenAICanvas.Providers;
using OpenAICanvas.Web.Diagnostics;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 系统渠道服务端代理。浏览器只看到 /api/ai/system/{channelId}，真实地址、Key
/// 与渠道头只在服务端组装。对应 Go: handler/auth.go 的 system proxy。
/// </summary>
public static class SystemProxyEndpoints
{
    private const int MaxPathLength = 2048;
    private const int MaxLoggedPayloadChars = 128 << 10;
    private const long MaxLoggedPayloadSourceBytes = 1 << 20;
    private static readonly TimeSpan ChannelSlotTTL = TimeSpan.FromMinutes(36);
    private static readonly TimeSpan UpstreamTimeout = TimeSpan.FromMinutes(35);
    private static readonly string[] CopiedResponseHeaders =
    [
        "Cache-Control", "Content-Disposition", "ETag", "Last-Modified", "Retry-After"
    ];

    private static readonly HashSet<string> OpenAIPostEndpoints = new(StringComparer.Ordinal)
    {
        "/responses", "/chat/completions", "/images/generations", "/images/edits",
        "/audio/speech", "/messages",
    };

    public static void MapSystemProxyRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        IRateLimiter rateLimiter,
        IRuntimePolicyProvider policyProvider,
        Coordinator coordinator)
    {
        // Catch-all 保证未知方法也进入统一 {code,data,msg,reason} 错误信封，
        // 而不是由 ASP.NET 先返回裸 405。
        api.Map("/ai/system/{channelId}/{**path}", async (
            HttpContext context, string channelId, string? path, CancellationToken cancellationToken) =>
        {
            return await HandleAsync(
                context, channelId, path, service, rateLimiter, policyProvider, coordinator,
                cancellationToken).ConfigureAwait(false);
        });
        api.Map("/ai/system/{channelId}", async (
            HttpContext context, string channelId, CancellationToken cancellationToken) =>
        {
            return await HandleAsync(
                context, channelId, null, service, rateLimiter, policyProvider, coordinator,
                cancellationToken).ConfigureAwait(false);
        });
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        string channelId,
        string? rawPath,
        CanvasService service,
        IRateLimiter rateLimiter,
        IRuntimePolicyProvider policyProvider,
        Coordinator coordinator,
        CancellationToken cancellationToken)
    {
        DateTime startedAt = DateTime.UtcNow;
        User user;
        ModelChannel channel;
        try
        {
            user = await service.CurrentUserAsync(
                SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
            channel = await service.SystemChannelAsync(channelId, cancellationToken).ConfigureAwait(false)
                ?? throw AppError.NotFound("系统渠道不存在或已停用");
        }
        catch (Exception error)
        {
            return ApiResults.FailService(error, context);
        }

        RuntimeRequestPolicy policy = policyProvider.Current().Request;
        if (!await AuthEndpoints.EnforceRateLimitAsync(
                context, rateLimiter, "system-proxy:" + user.ID,
                policy.SystemRelayPerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        string path;
        try
        {
            path = NormalizeProxyPath(rawPath);
        }
        catch (AppError error)
        {
            return ApiResults.FailService(error, context);
        }

        long requestLimit = checked(policy.SystemRelayRequestMB << 20);
        byte[] requestBody;
        try
        {
            requestBody = await ReadLimitedAsync(
                context.Request.Body, requestLimit, cancellationToken).ConfigureAwait(false);
        }
        catch (PayloadLimitException)
        {
            return ApiResults.Fail(StatusCodes.Status413RequestEntityTooLarge,
                new InvalidOperationException("系统渠道请求超过配置上限"));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (Exception error)
        {
            return ApiResults.Fail(StatusCodes.Status400BadRequest, error);
        }

        string modelName = ProxyRequestModelForPath(path, context.Request.ContentType, requestBody);
        if (modelName.Length == 0 && context.Request.Method == HttpMethods.Get && path == "/agnesapi")
        {
            modelName = context.Request.Query["model_name"].ToString().Trim();
        }

        ChannelModel? channelModel = null;
        string protocol = "";
        string capability = "text";
        try
        {
            if (!(context.Request.Method == HttpMethods.Get && path == "/models"))
            {
                if (context.Request.Method == HttpMethods.Get
                    && IsMiniMaxTaskPath(path) && modelName.Length == 0)
                {
                    if (!await service.SystemChannelHasProtocolAsync(
                            channel.ID, ChannelInterfaceType.ChannelInterfaceMiniMaxVideo,
                            cancellationToken).ConfigureAwait(false))
                    {
                        throw AppError.Forbidden("当前系统渠道未授权 MiniMax 视频协议");
                    }
                    protocol = ChannelInterfaceType.ChannelInterfaceMiniMaxVideo;
                    capability = "video";
                }
                else
                {
                    channelModel = await service.SystemChannelModelAsync(
                        channel.ID, modelName, cancellationToken).ConfigureAwait(false);
                    if (channelModel is null || channelModel.Protocol.Length == 0)
                    {
                        throw AppError.Forbidden("当前系统渠道未授权该模型或模型协议尚未配置");
                    }
                    protocol = channelModel.Protocol;
                    capability = string.IsNullOrWhiteSpace(channelModel.Capability)
                        ? "text"
                        : channelModel.Capability;
                }
            }

            string? authorizationError = AuthorizeSystemProxy(
                channel, protocol, context.Request.Method, path,
                context.Request.ContentType ?? "", requestBody);
            if (authorizationError is not null)
            {
                throw AppError.Forbidden(authorizationError);
            }
        }
        catch (Exception error)
        {
            return ApiResults.FailService(error, context);
        }

        if (channel.APIKey.Trim().Length == 0)
        {
            return ApiResults.FailService(
                AppError.BadAuthRequest("系统渠道未配置 API Key"), context);
        }

        Uri target;
        try
        {
            target = await BuildAndValidateTargetAsync(
                channel, protocol, path, context.Request.Query).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AppError or ArgumentException or InvalidOperationException)
        {
            return ApiResults.FailService(error, context);
        }

        List<OutboundHeader> channelHeaders;
        try
        {
            channelHeaders = OutboundGuard.ParseOutboundHeadersJson(channel.HeadersJSON);
        }
        catch (Exception error)
        {
            return ApiResults.FailService(error, context);
        }

        int concurrencyLimit = channel.ConcurrencyLimit > 0
            ? (int)channel.ConcurrencyLimit
            : Coordinator.EffectiveChannelConcurrencyLimit(policyProvider.Current().Task.ChannelConcurrency);
        if (concurrencyLimit is < Coordinator.MinChannelConcurrencyLimit or > Coordinator.MaxChannelConcurrencyLimit)
        {
            return ApiResults.FailInternal(StatusCodes.Status503ServiceUnavailable,
                new InvalidOperationException("渠道并发配置超出 1-999 范围"), context);
        }

        Func<ValueTask> release;
        try
        {
            release = await coordinator.AcquireWithWaitAsync(
                "channel:" + channel.ID, concurrencyLimit, ChannelSlotTTL, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (Exception error)
        {
            return ApiResults.FailInternal(StatusCodes.Status503ServiceUnavailable, error, context);
        }

        try
        {
            return await ProxyUpstreamAsync(
                context, service, user, channel, channelModel, protocol, capability,
                path, target, channelHeaders, requestBody, concurrencyLimit,
                policy.SystemRelayResponseMB << 20, startedAt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await release().ConfigureAwait(false);
        }
    }

    private static async Task<IResult> ProxyUpstreamAsync(
        HttpContext context,
        CanvasService service,
        User user,
        ModelChannel channel,
        ChannelModel? channelModel,
        string protocol,
        string capability,
        string path,
        Uri target,
        IReadOnlyList<OutboundHeader> channelHeaders,
        byte[] requestBody,
        int concurrencyLimit,
        long responseLimit,
        DateTime startedAt,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage upstream = new(new HttpMethod(context.Request.Method), target);
        if (requestBody.Length > 0 || !string.IsNullOrWhiteSpace(context.Request.ContentType))
        {
            upstream.Content = new ByteArrayContent(requestBody);
            if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
            {
                upstream.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }
        }
        if (!string.IsNullOrWhiteSpace(context.Request.Headers.Accept))
        {
            upstream.Headers.TryAddWithoutValidation("Accept", context.Request.Headers.Accept.ToString());
        }

        ProviderConfig providerConfig = new()
        {
            APIKey = AuthService.DecryptSecret(channel.APIKey),
            APIFormat = ProtocolAPIFormat(protocol),
        };
        OutboundHttpClient.ApplyHeaders(upstream, channelHeaders);
        ProviderTransport.ApplyProviderAuth(upstream, providerConfig);
        ProviderTransport.ApplyDefaultHeaders(upstream);

        HttpResponseMessage response;
        using HttpClient client = OutboundHttpClient.Create(
            UpstreamTimeout, allowAutoRedirect: false);
        try
        {
            // 不能使用 ProviderTransport.SendAsync：它把非 2xx 转成异常并丢弃响应头，
            // 系统代理必须保留上游状态、Retry-After 和 SSE 响应头。
            response = await client.SendAsync(
                upstream, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            await TryLogAsync(service, BuildLog(
                user, channel, channelModel, capability, protocol, context.Request.Method, path,
                target, requestBody, context.Request.ContentType ?? "", ApiCallStatus.ApiCallStatusFailed,
                0, startedAt, "request_cancelled", concurrencyLimit, null, "billing_pending"),
                cancellationToken).ConfigureAwait(false);
            return Results.Empty;
        }
        catch (Exception error)
        {
            await TryLogAsync(service, BuildLog(
                user, channel, channelModel, capability, protocol, context.Request.Method, path,
                target, requestBody, context.Request.ContentType ?? "", ApiCallStatus.ApiCallStatusFailed,
                0, startedAt, "upstream_connection_failed", concurrencyLimit, null, "billing_pending"),
                cancellationToken).ConfigureAwait(false);
            return ApiResults.FailInternal(StatusCodes.Status502BadGateway, error, context);
        }

        using (response)
        {
            int statusCode = (int)response.StatusCode;
            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            bool failed = statusCode < 200 || statusCode >= 300;
            if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                CopyResponseHeaders(context, response);
                context.Response.Headers["Cache-Control"] = "no-cache, no-store, no-transform";
                context.Response.Headers["X-Accel-Buffering"] = "no";
                context.Response.StatusCode = statusCode;
                List<byte> captured = [];
                try
                {
                    await CopyStreamAsync(
                        context.Response.Body, response.Content, responseLimit, captured, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (error is PayloadLimitException or IOException or HttpRequestException)
                {
                    await TryLogAsync(service, BuildLog(
                        user, channel, channelModel, capability, protocol, context.Request.Method, path,
                        target, requestBody, context.Request.ContentType ?? "",
                        ApiCallStatus.ApiCallStatusFailed, statusCode, startedAt,
                        "upstream_response_read_failed", concurrencyLimit, captured.ToArray(),
                        "billing_pending"), cancellationToken).ConfigureAwait(false);
                    // SSE 已经写出响应头，不能再改成结构化错误，只能结束连接。
                    return Results.Empty;
                }

                await TryLogAsync(service, BuildLog(
                    user, channel, channelModel, capability, protocol, context.Request.Method, path,
                    target, requestBody, context.Request.ContentType ?? "",
                    failed ? ApiCallStatus.ApiCallStatusFailed : ApiCallStatus.ApiCallStatusSucceeded,
                    statusCode, startedAt, failed ? "upstream_http_error" : "", concurrencyLimit,
                    captured.ToArray(), "billing_pending"), cancellationToken).ConfigureAwait(false);
                return Results.Empty;
            }

            byte[] responseBody;
            try
            {
                responseBody = await ReadLimitedAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    responseLimit, cancellationToken).ConfigureAwait(false);
            }
            catch (PayloadLimitException)
            {
                await TryLogAsync(service, BuildLog(
                    user, channel, channelModel, capability, protocol, context.Request.Method, path,
                    target, requestBody, context.Request.ContentType ?? "",
                    ApiCallStatus.ApiCallStatusFailed, statusCode, startedAt,
                    "upstream_response_too_large", concurrencyLimit, null, "billing_pending"),
                    cancellationToken).ConfigureAwait(false);
                return ApiResults.Fail(StatusCodes.Status502BadGateway,
                    new InvalidOperationException("系统渠道响应超过配置上限"));
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return Results.Empty;
            }

            await TryLogAsync(service, BuildLog(
                user, channel, channelModel, capability, protocol, context.Request.Method, path,
                target, requestBody, context.Request.ContentType ?? "",
                failed ? ApiCallStatus.ApiCallStatusFailed : ApiCallStatus.ApiCallStatusSucceeded,
                statusCode, startedAt, failed ? "upstream_http_error" : "", concurrencyLimit,
                responseBody, "billing_pending"), cancellationToken).ConfigureAwait(false);

            CopyResponseHeaders(context, response);
            context.Response.StatusCode = statusCode;
            if (!string.IsNullOrWhiteSpace(response.Content.Headers.ContentType?.ToString()))
            {
                context.Response.Headers["Content-Type"] = response.Content.Headers.ContentType!.ToString();
            }
            await context.Response.Body.WriteAsync(responseBody, cancellationToken).ConfigureAwait(false);
            return Results.Empty;
        }
    }

    private static async Task<Uri> BuildAndValidateTargetAsync(
        ModelChannel channel,
        string protocol,
        string path,
        IQueryCollection query)
    {
        string rawTarget = ProviderTransport.ChannelApiUrlForProtocol(channel.BaseURL, path, protocol);
        UriBuilder builder = new(rawTarget);
        List<string> pairs = [];
        foreach ((string key, Microsoft.Extensions.Primitives.StringValues values) in query)
        {
            if (IsCredentialQueryKey(key))
            {
                continue;
            }
            foreach (string value in values)
            {
                pairs.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));
            }
        }
        builder.Query = string.Join('&', pairs);
        builder.Fragment = "";
        return await OutboundGuard.ValidateOutboundUrlAsync(builder.Uri.ToString()).ConfigureAwait(false);
    }

    private static bool IsCredentialQueryKey(string key) => key.Trim().ToLowerInvariant() is
        "key" or "api_key" or "access_token" or "token";

    internal static string NormalizeProxyPath(string? value)
    {
        string raw = value ?? "";
        if (raw.Length > MaxPathLength)
        {
            throw AppError.BadAuthRequest("系统渠道请求路径过长");
        }
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException error)
        {
            throw AppError.BadAuthRequest("系统渠道请求路径无效").WithCause(error);
        }
        if (decoded.Contains('\\') || decoded.Contains('\0'))
        {
            throw AppError.BadAuthRequest("系统渠道请求路径无效");
        }
        string normalized = "/" + decoded.TrimStart('/');
        if (normalized.Length == 1)
        {
            return normalized;
        }
        foreach (string segment in normalized.Split('/'))
        {
            if (segment is "." or "..")
            {
                throw AppError.BadAuthRequest("系统渠道请求路径无效");
            }
        }
        return normalized;
    }

    internal static string? AuthorizeSystemProxy(
        ModelChannel channel,
        string protocol,
        string method,
        string requestPath,
        string contentType,
        byte[] body)
    {
        try
        {
            requestPath = NormalizeProxyPath(requestPath);
        }
        catch (AppError error)
        {
            return error.Message;
        }
        if (method == HttpMethods.Get && requestPath == "/models")
        {
            return null;
        }
        if (protocol == ChannelInterfaceType.ChannelInterfaceAgnesVideo)
        {
            if (method == HttpMethods.Get && requestPath == "/agnesapi")
            {
                return null;
            }
            if (method != HttpMethods.Post || requestPath != "/videos")
            {
                return "系统渠道不允许访问该上游接口";
            }
            if (!IsMediaType(contentType, "application/json"))
            {
                return "Agnes 视频生成请求必须使用 application/json";
            }
            return AuthorizeModel(channel, RequestModel(contentType, body));
        }
        if (protocol == ChannelInterfaceType.ChannelInterfaceMiniMaxVideo)
        {
            if (method == HttpMethods.Get && IsMiniMaxTaskPath(requestPath))
            {
                return null;
            }
            if (method != HttpMethods.Post || requestPath != "/v2/video_generation")
            {
                return "系统渠道不允许访问该上游接口";
            }
            if (!IsMediaType(contentType, "application/json"))
            {
                return "MiniMax 视频生成请求必须使用 application/json";
            }
            return AuthorizeModel(channel, RequestModel(contentType, body));
        }
        if (protocol is ChannelInterfaceType.ChannelInterfaceGeminiVeo
            or ChannelInterfaceType.ChannelInterfaceGeminiImage)
        {
            if (method != HttpMethods.Post
                || !TryGeminiModelPath(requestPath, out string modelName))
            {
                return "系统渠道不允许访问该上游接口";
            }
            return AuthorizeModel(channel, modelName);
        }
        if (method != HttpMethods.Post || !OpenAIPostEndpoints.Contains(requestPath))
        {
            return "系统渠道不允许访问该上游接口";
        }
        if (protocol.Length > 0 && !InterfaceAllowsProxyPath(protocol, requestPath))
        {
            return "当前接口类型不允许访问该上游接口";
        }
        return AuthorizeModel(channel, RequestModel(contentType, body));
    }

    private static string? AuthorizeModel(ModelChannel channel, string modelName)
    {
        string requested = NormalizeModel(modelName);
        if (requested.Length == 0)
        {
            return "当前系统渠道未授权该模型";
        }
        try
        {
            List<string>? configured = JsonSerializer.Deserialize<List<string>>(channel.ModelsJSON);
            if (configured is not null && configured.Any(value => NormalizeModel(value) == requested))
            {
                return null;
            }
        }
        catch (JsonException)
        {
            // 损坏的 ModelsJSON 等同于没有授权模型。
        }
        return "当前系统渠道未授权该模型";
    }

    private static bool InterfaceAllowsProxyPath(string protocol, string path) => protocol switch
    {
        ChannelInterfaceType.ChannelInterfaceChatCompletion => path == "/chat/completions",
        ChannelInterfaceType.ChannelInterfaceOpenAIResponse => path == "/responses",
        ChannelInterfaceType.ChannelInterfaceClaudeAPI => path == "/messages",
        ChannelInterfaceType.ChannelInterfaceOpenAIImage or ChannelInterfaceType.ChannelInterfaceGrokImage =>
            path is "/images/generations" or "/images/edits",
        ChannelInterfaceType.ChannelInterfaceVolcengineArkImage => path == "/images/generations",
        ChannelInterfaceType.ChannelInterfaceOpenAIAudio => path == "/audio/speech",
        ChannelInterfaceType.ChannelInterfaceAsyncAudio
            or ChannelInterfaceType.ChannelInterfaceNewAPIVideo
            or ChannelInterfaceType.ChannelInterfaceNewAPIChannel1
            or ChannelInterfaceType.ChannelInterfaceNewAPIChannel2
            or ChannelInterfaceType.ChannelInterfaceXAIVideo
            or ChannelInterfaceType.ChannelInterfaceVolcengineArkVideo
            or ChannelInterfaceType.ChannelInterfaceVolcengineJiMengImage
            or ChannelInterfaceType.ChannelInterfaceVolcengineJiMengVideo
            or ChannelInterfaceType.ChannelInterfaceNovitaVideo
            or ChannelInterfaceType.ChannelInterfaceMiniMaxVideo => false,
        _ => true,
    };

    private static string ProtocolAPIFormat(string protocol) => protocol switch
    {
        ChannelInterfaceType.ChannelInterfaceClaudeAPI => "claude",
        ChannelInterfaceType.ChannelInterfaceGeminiVeo or ChannelInterfaceType.ChannelInterfaceGeminiImage => "gemini",
        _ => "openai",
    };

    private static string RequestModel(string contentType, byte[] body)
    {
        if (contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                MediaTypeHeaderValue parsed = MediaTypeHeaderValue.Parse(contentType);
                string? boundary = parsed.Parameters.FirstOrDefault(
                    item => item.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');
                if (!string.IsNullOrWhiteSpace(boundary))
                {
                    string text = Encoding.UTF8.GetString(body);
                    string marker = "name=\"model\"";
                    int index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0)
                    {
                        int start = text.IndexOf("\r\n\r\n", index, StringComparison.Ordinal);
                        if (start >= 0)
                        {
                            start += 4;
                            int end = text.IndexOf("\r\n--", start, StringComparison.Ordinal);
                            if (end > start)
                            {
                                return text[start..end].Trim();
                            }
                        }
                    }
                }
            }
            catch (FormatException)
            {
                return "";
            }
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("model", out JsonElement model)
                && model.ValueKind == JsonValueKind.String
                ? model.GetString()?.Trim() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string ProxyRequestModelForPath(string path, string? contentType, byte[] body) =>
        TryGeminiModelPath(path, out string model) ? NormalizeModel(model) : RequestModel(contentType ?? "", body);

    private static bool TryGeminiModelPath(string path, out string model)
    {
        model = "";
        if (!path.StartsWith("/models/", StringComparison.Ordinal)
            || (!path.EndsWith(":generateContent", StringComparison.Ordinal)
                && !path.EndsWith(":streamGenerateContent", StringComparison.Ordinal)))
        {
            return false;
        }
        string value = path["/models/".Length..];
        int separator = value.LastIndexOf(':');
        if (separator <= 0 || value[..separator].Contains('/'))
        {
            return false;
        }
        model = Uri.UnescapeDataString(value[..separator]);
        return model.Length > 0;
    }

    private static bool IsMiniMaxTaskPath(string path) =>
        path.StartsWith("/v2/query/video_generation/", StringComparison.Ordinal)
        && path.Length > "/v2/query/video_generation/".Length
        && !path["/v2/query/video_generation/".Length..].Contains('/');

    private static string NormalizeModel(string value)
    {
        string normalized = value.Trim();
        return normalized.StartsWith("models/", StringComparison.Ordinal)
            ? normalized["models/".Length..]
            : normalized;
    }

    private static bool IsMediaType(string contentType, string expected)
    {
        try
        {
            return MediaTypeHeaderValue.Parse(contentType).MediaType.Equals(
                expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void CopyResponseHeaders(HttpContext context, HttpResponseMessage response)
    {
        foreach (string name in CopiedResponseHeaders)
        {
            if (response.Headers.TryGetValues(name, out IEnumerable<string>? values)
                || response.Content.Headers.TryGetValues(name, out values))
            {
                context.Response.Headers[name] = values.ToArray();
            }
        }
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!string.IsNullOrWhiteSpace(response.Content.Headers.ContentType?.ToString()))
        {
            context.Response.Headers["Content-Type"] = response.Content.Headers.ContentType!.ToString();
        }
    }

    private static async Task CopyStreamAsync(
        Stream destination,
        HttpContent content,
        long responseLimit,
        List<byte> captured,
        CancellationToken cancellationToken)
    {
        await using Stream source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[32 << 10];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, Math.Max(1, responseLimit - total + 1))),
                cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return;
            }
            total += read;
            if (total > responseLimit)
            {
                throw new PayloadLimitException();
            }
            if (captured.Count < MaxLoggedPayloadSourceBytes)
            {
                int copy = Math.Min(read, (int)MaxLoggedPayloadSourceBytes - captured.Count);
                captured.AddRange(buffer.AsSpan(0, copy).ToArray());
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(
        Stream source, long limit, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[32 << 10];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new PayloadLimitException();
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return buffer.ToArray();
    }

    private static ApiCallLog BuildLog(
        User user,
        ModelChannel channel,
        ChannelModel? channelModel,
        string capability,
        string protocol,
        string method,
        string path,
        Uri target,
        byte[] requestBody,
        string requestContentType,
        string status,
        int statusCode,
        DateTime startedAt,
        string error,
        int concurrencyLimit,
        byte[]? responseBody,
        string operation)
    {
        string requestKind = method == HttpMethods.Get
            ? (path.TrimEnd('/').EndsWith("/content", StringComparison.Ordinal) ? "download" : "poll")
            : "create";
        string model = channelModel?.ModelKey ?? RequestModel(requestContentType, requestBody);
        return new ApiCallLog
        {
            ID = IdGenerator.NewId(),
            UserID = user.ID,
            ChannelID = channel.ID,
            Source = "system-channel",
            Capability = capability,
            Operation = operation,
            RequestKind = requestKind,
            Billable = false,
            APIFormat = ProtocolAPIFormat(protocol),
            Method = method,
            Path = path,
            Model = model,
            Status = status,
            StatusCode = statusCode,
            DurationMs = Math.Max(0, (long)(DateTime.UtcNow - startedAt).TotalMilliseconds),
            Error = error,
            ConcurrencyLimit = concurrencyLimit,
            UpstreamURL = target.ToString(),
            RequestContentType = requestContentType,
            RequestBody = SanitizePayload(requestBody, requestContentType, channel.APIKey),
            ResponseBody = responseBody is null ? "" : SanitizePayload(
                responseBody, "", channel.APIKey),
            StartedAt = startedAt,
            CreatedAt = DateTime.UtcNow,
        };
    }

    private static async Task TryLogAsync(
        CanvasService service, ApiCallLog log, CancellationToken cancellationToken)
    {
        try
        {
            await service.Repository.CreateApiCallLogAsync(log, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            CanvasLog.Warn($"system proxy api log write failed: {error.GetType().Name}");
        }
    }

    internal static string SanitizePayload(byte[] body, string contentType, string secret)
    {
        if (body.Length == 0)
        {
            return "";
        }
        string text = Encoding.UTF8.GetString(body);
        if (secret.Length > 0)
        {
            text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }
        if (text.Length > MaxLoggedPayloadSourceBytes)
        {
            return $"[报文过大，已省略，共 {body.Length} 字节]";
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            object? sanitized = SanitizeJson(document.RootElement, "");
            string encoded = JsonSerializer.Serialize(sanitized, new JsonSerializerOptions { WriteIndented = true });
            return encoded.Length <= MaxLoggedPayloadChars
                ? encoded
                : encoded[..MaxLoggedPayloadChars] + "...";
        }
        catch (JsonException)
        {
            return text.Length <= MaxLoggedPayloadChars ? text : text[..MaxLoggedPayloadChars] + "...";
        }
    }

    private static object? SanitizeJson(JsonElement value, string key)
    {
        string normalizedKey = key.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        if (normalizedKey.Contains("apikey", StringComparison.Ordinal)
            || normalizedKey.Contains("accesstoken", StringComparison.Ordinal)
            || normalizedKey.Contains("authorization", StringComparison.Ordinal)
            || normalizedKey.Contains("password", StringComparison.Ordinal)
            || normalizedKey.Contains("secret", StringComparison.Ordinal))
        {
            return "[REDACTED]";
        }
        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(
                property => property.Name,
                property => SanitizeJson(property.Value, property.Name), StringComparer.Ordinal),
            JsonValueKind.Array => value.EnumerateArray().Select(item => SanitizeJson(item, key)).ToList(),
            JsonValueKind.String when value.GetString()?.StartsWith("data:", StringComparison.Ordinal) == true =>
                "[内嵌媒体已省略]",
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private sealed class PayloadLimitException : Exception;
}

internal static class AppErrorExtensions
{
    public static AppError WithCause(this AppError error, Exception cause) =>
        new(error.Status, error.Message, error.Code, error.Reason, error.Retryable, cause);
}
