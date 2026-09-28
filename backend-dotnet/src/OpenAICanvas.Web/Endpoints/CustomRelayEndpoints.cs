#nullable enable
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Outbound;
using OpenAICanvas.Platform;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 用户自定义渠道中转：浏览器 → 后端 → 用户配置的上游。
/// 浏览器只携带目标 URL 与自己的 Key；后端做 SSRF 校验、方法/路径白名单、
/// 并发槽位、响应大小限制、流式密钥脱敏与错误文案拦截。
/// 对应 Go: <c>handler/custom_proxy.go</c>、<c>handler/security.go</c> 的
/// authorizeCustomRelay 与 <c>RegisterChannelModelRoutes</c> 的 /ai/models。
/// </summary>
public static class CustomRelayEndpoints
{
    private const long MaxErrorResponseBytes = 64 << 10;
    private const string CustomRelayHeadersHeader = "X-Canvas-Upstream-Headers";

    private static readonly ConcurrentDictionary<string, int> ActiveRelaySlots = new(StringComparer.Ordinal);
    private static readonly object SlotLock = new();

    private static readonly Regex CustomVideoTaskPath =
        new(@"(?:^|/)video/generations/[^/]+$", RegexOptions.Compiled);

    private static readonly Regex CustomXAIVideoTaskPath =
        new(@"(?:^|/)videos/[^/]+$", RegexOptions.Compiled);

    private static readonly Regex CustomVideoContentPath =
        new(@"(?:^|/)videos/[^/]+/content$", RegexOptions.Compiled);

    private static readonly Regex CustomArkVideoTaskPath =
        new(@"(?:^|/)contents/generations/tasks/[^/]+$", RegexOptions.Compiled);

    private static readonly Regex CustomGeminiRelayPath =
        new(@"(?:^|/)models/[^/:]+:(generateContent|streamGenerateContent|predictLongRunning)$", RegexOptions.Compiled);

    private static readonly Regex CustomGeminiOperationPath =
        new(@"(?:^|/)(?:models/[^/]+/)?operations/[^/]+$", RegexOptions.Compiled);

    private static readonly Regex CustomNovitaTaskResultPath =
        new(@"(?:^|/)async/task-result$", RegexOptions.Compiled);

    private static readonly Regex CustomMiniMaxTaskPath =
        new(@"(?:^|/)v2/query/video_generation/[^/]+$", RegexOptions.Compiled);

    public static void MapCustomRelayRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        PlatformSettingsService settings,
        FeatureAvailabilityService features,
        IRateLimiter rateLimiter,
        IRuntimePolicyProvider policyProvider)
    {
        // 用户自定义渠道模型目录拉取。对应 Go: channel_models.go 的 /ai/models。
        api.MapPost("/ai/models", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await RequireCustomChannelsAsync(features, cancellationToken).ConfigureAwait(false);
                if (!await AuthEndpoints.EnforceRateLimitAsync(
                        context, rateLimiter, "channel-models:" + user.ID, 30, TimeSpan.FromMinutes(1))
                        .ConfigureAwait(false))
                {
                    return Results.Empty;
                }
                ChannelModelCatalogService.CatalogRequest? input = await ReadJsonAsync<
                    ChannelModelCatalogService.CatalogRequest>(context, cancellationToken)
                    .ConfigureAwait(false);
                if (input is null)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException("模型渠道参数格式错误"));
                }
                IReadOnlyList<ChannelModelCatalogItemDto> models = await new ChannelModelCatalogService()
                    .FetchChannelModelCatalogAsync(user, input, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["models"] = models,
                });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // 用户自定义渠道全量中转（含 SSE 流式）。对应 Go: custom_proxy.go 的 /ai/custom。
        api.Map("/ai/custom", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await RequireCustomChannelsAsync(features, cancellationToken).ConfigureAwait(false);
                RuntimePolicySetting policy = policyProvider.Current();
                if (!await AuthEndpoints.EnforceRateLimitAsync(
                        context, rateLimiter, "custom-relay:" + user.ID,
                        policy.Request.CustomRelayPerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }
                TimeSpan ttl = TimeSpan.FromMinutes(policy.Request.CustomRelayTimeoutMinutes + 1);
                (IDisposable Release, bool Acquired) slot = AcquireRelaySlot(user.ID, policy.Request.CustomRelayConcurrency, ttl);
                if (!slot.Acquired)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status429TooManyRequests,
                        AppError.RateLimited("自定义渠道并发请求过多，请等待已有请求完成"));
                }
                using (slot.Release)
                {
                    await ProxyCustomRelayAsync(
                        context, service, settings, policy.Request, cancellationToken).ConfigureAwait(false);
                }
                return Results.Empty;
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    private static async Task RequireCustomChannelsAsync(
        FeatureAvailabilityService features, CancellationToken cancellationToken)
    {
        if (features is not null)
        {
            await features.RequireFeatureAsync(
                OpenAICanvas.Platform.FeatureNames.CustomChannels, cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------ 并发槽位

    /// <summary>
    /// 进程内并发槽位（.NET 单实例运行面，Redis 协调为 PENDING #66 同族）。
    /// 对应 Go: <c>AcquireCustomRelaySlot</c>。
    /// </summary>
    private static (IDisposable Release, bool Acquired) AcquireRelaySlot(
        string userId, int limit, TimeSpan ttl)
    {
        if (limit <= 0)
        {
            return (Disposable.Empty, true);
        }
        // 简单加锁递增；ttl 只用于 Go 的 Redis 过期，本地计数随请求释放即可。
        lock (SlotLock)
        {
            int current = ActiveRelaySlots.GetValueOrDefault(userId, 0);
            if (current >= limit)
            {
                return (Disposable.Empty, false);
            }
            ActiveRelaySlots[userId] = current + 1;
            return (new RelaySlotRelease(userId), true);
        }
    }

    private sealed class RelaySlotRelease : IDisposable
    {
        private readonly string _userId;

        public RelaySlotRelease(string userId) => _userId = userId;

        public void Dispose()
        {
            if (ActiveRelaySlots.TryGetValue(_userId, out int count) && count <= 1)
            {
                ActiveRelaySlots.TryRemove(new KeyValuePair<string, int>(_userId, count));
            }
            else
            {
                ActiveRelaySlots.AddOrUpdate(_userId, 0, (_, value) => Math.Max(0, value - 1));
            }
        }
    }

    private sealed class Disposable : IDisposable
    {
        public static readonly Disposable Empty = new();
        public void Dispose() { }
    }

    // ------------------------------------------------------------ 代理主流程

    private static async Task ProxyCustomRelayAsync(
        HttpContext context,
        CanvasService service,
        PlatformSettingsService settings,
        RuntimeRequestPolicy policy,
        CancellationToken cancellationToken)
    {
        string rawUrl = context.Request.Headers["X-Canvas-Upstream-URL"].ToString();
        if (rawUrl.Trim().Length > 4096)
        {
            WriteFail(context, StatusCodes.Status400BadRequest, "自定义渠道地址过长");
            return;
        }
        Uri target;
        try
        {
            target = await OutboundGuard.ValidateOutboundUrlAsync(rawUrl.Trim()).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AppError or ArgumentException or InvalidOperationException)
        {
            WriteFail(context, StatusCodes.Status400BadRequest, "自定义渠道地址无效");
            return;
        }
        if (target.UserInfo is { Length: > 0 })
        {
            WriteFail(context, StatusCodes.Status400BadRequest, "自定义渠道地址不允许包含认证信息");
            return;
        }
        if (target.Fragment is { Length: > 0 })
        {
            WriteFail(context, StatusCodes.Status400BadRequest, "自定义渠道地址不允许包含片段");
            return;
        }
        string? apiFormat = context.Request.Headers["X-Canvas-Upstream-Format"].ToString().Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(apiFormat))
        {
            apiFormat = "openai";
        }
        string contentType = context.Request.Headers["Content-Type"].ToString();
        string? authorizeError = AuthorizeCustomRelay(context.Request.Method, target, apiFormat, contentType);
        if (authorizeError is not null)
        {
            WriteFail(context, StatusCodes.Status403Forbidden, authorizeError);
            return;
        }
        string authHeader = context.Request.Headers["Authorization"].ToString();
        string apiKey;
        try
        {
            apiKey = ParseRelayApiKey(authHeader);
        }
        catch (AppError error)
        {
            WriteFail(context, StatusCodes.Status401Unauthorized, error.Message);
            return;
        }
        List<OutboundHeader> headers;
        try
        {
            headers = DecodeRelayOutboundHeaders(
                context.Request.Headers[CustomRelayHeadersHeader].ToString());
        }
        catch (AppError error)
        {
            WriteFail(context, StatusCodes.Status400BadRequest, error.Message);
            return;
        }
        long requestLimit = policy.CustomRelayRequestMB << 20;
        if (context.Request.ContentLength > requestLimit)
        {
            WriteFail(context, StatusCodes.Status413RequestEntityTooLarge, "自定义渠道请求超过配置上限");
            return;
        }
        byte[] body;
        try
        {
            using MemoryStream buffer = new();
            await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            body = buffer.ToArray();
            if (body.LongLength > requestLimit)
            {
                WriteFail(context, StatusCodes.Status413RequestEntityTooLarge, "自定义渠道请求超过配置上限");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (context.Request.Method == HttpMethods.Get && body.Length != 0)
        {
            WriteFail(context, StatusCodes.Status400BadRequest, "模型列表请求不允许携带请求体");
            return;
        }

        using HttpRequestMessage upstream = new(new HttpMethod(context.Request.Method), target);
        if (contentType.Length > 0)
        {
            upstream.Content = new ByteArrayContent(body);
            upstream.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        else if (body.Length > 0)
        {
            upstream.Content = new ByteArrayContent(body);
        }
        string accept = context.Request.Headers["Accept"].ToString();
        upstream.Headers.TryAddWithoutValidation("Accept",
            accept.ToLowerInvariant().Contains("text/event-stream") ? "text/event-stream" : "application/json");
        OutboundHttpClient.ApplyHeaders(upstream, headers);
        ApplyDefaultOutboundHeaders(upstream);
        if (apiFormat == "gemini")
        {
            upstream.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        }
        else if (apiFormat == "claude")
        {
            upstream.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            upstream.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else
        {
            upstream.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        }

        using HttpClient client = OutboundHttpClient.Create(
            TimeSpan.FromMinutes(policy.CustomRelayTimeoutMinutes), allowAutoRedirect: false);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                upstream,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            WriteFail(context, StatusCodes.Status502BadGateway,
                UserFacingError(settings, "自定义渠道上游连接失败"));
            return;
        }
        using (response)
        {
            bool allowBinary = CustomVideoContentPath.IsMatch(target.AbsolutePath)
                || target.AbsolutePath.EndsWith("/audio/speech", StringComparison.Ordinal);
            await WriteRelayResponseAsync(
                context, settings, response, apiKey,
                policy.CustomRelayResponseMB << 20, allowBinary, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ApplyDefaultOutboundHeaders(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(request.Headers.UserAgent.ToString()))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Yingce-Canvas/1.0");
        }
    }

    private static async Task WriteRelayResponseAsync(
        HttpContext context,
        PlatformSettingsService settings,
        HttpResponseMessage response,
        string apiKey,
        long responseLimit,
        bool allowBinary,
        CancellationToken cancellationToken)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
        {
            await WriteRelayErrorAsync(
                context, settings, response, apiKey, mediaType, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Response.StatusCode = (int)response.StatusCode;
            await CopyRelayStreamAsync(
                context, response.Content, apiKey, responseLimit, cancellationToken).ConfigureAwait(false);
            return;
        }
        byte[] body;
        try
        {
            body = await ReadLimitedAsync(response.Content, responseLimit, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            WriteFail(context, StatusCodes.Status502BadGateway,
                UserFacingError(settings, "自定义渠道上游返回无效或过大的 JSON"));
            return;
        }
        if (allowBinary && (mediaType.StartsWith("video/", StringComparison.Ordinal)
            || mediaType.StartsWith("audio/", StringComparison.Ordinal)
            || mediaType == "application/octet-stream"))
        {
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.Headers["Content-Type"] = mediaType;
            await context.Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (mediaType != "application/json" && !mediaType.EndsWith("+json", StringComparison.Ordinal))
        {
            WriteFail(context, StatusCodes.Status502BadGateway,
                UserFacingError(settings, "自定义渠道上游返回了不支持的内容类型"));
            return;
        }
        body = RedactSecret(body, apiKey);
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.Headers["Content-Type"] = "application/json; charset=utf-8";
        await context.Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteRelayErrorAsync(
        HttpContext context,
        PlatformSettingsService settings,
        HttpResponseMessage response,
        string apiKey,
        string mediaType,
        CancellationToken cancellationToken)
    {
        byte[] body;
        try
        {
            body = await ReadLimitedAsync(response.Content, MaxErrorResponseBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            WriteFail(context, StatusCodes.Status502BadGateway,
                UserFacingError(settings, "自定义渠道上游请求失败"));
            return;
        }
        body = RedactSecret(body, apiKey);
        string rawMessage = Encoding.UTF8.GetString(body).Trim();
        string intercepted = await InterceptTextAsync(settings, rawMessage, cancellationToken).ConfigureAwait(false);
        if (intercepted != rawMessage)
        {
            WriteFail(context, (int)response.StatusCode, intercepted);
            return;
        }
        if ((mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal)))
        {
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.Headers["Content-Type"] = "application/json; charset=utf-8";
            await context.Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            return;
        }
        string snippet = rawMessage.Trim();
        if (snippet.Length > 200)
        {
            snippet = snippet[..200] + "...";
        }
        WriteFail(context, (int)response.StatusCode,
            UserFacingError(settings, $"自定义渠道上游请求失败（{response.StatusCode}）{snippet}"));
    }

    /// <summary>SSE 流式拷贝 + 密钥脱敏（跨块滑动窗口）。对应 Go: <c>copyCustomRelayStream</c>。</summary>
    private static async Task CopyRelayStreamAsync(
        HttpContext context, HttpContent content, string apiKey, long maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] secret = Encoding.UTF8.GetBytes(apiKey);
        List<byte> pending = new(32 << 10);
        byte[] buffer = new byte[32 << 10];
        long written = 0;
        await using System.IO.Stream source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        while (written < maxBytes)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - written + 1)),
                cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }
            pending.AddRange(buffer.AsMemory(0, read).Span.ToArray());
            if (secret.Length > 0)
            {
                RedactInPlace(pending, secret);
            }
            int keep = secret.Length > 0 ? RelaySecretPrefixSuffixLength(pending, secret) : 0;
            int cut = pending.Count - keep;
            if (cut > 0)
            {
                byte[] chunk = pending.Take(cut).ToArray();
                pending.RemoveRange(0, cut);
                await context.Response.Body.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            written += read;
        }
        if (pending.Count > 0)
        {
            byte[] tail = pending.ToArray();
            await context.Response.Body.WriteAsync(tail, cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RedactInPlace(List<byte> data, byte[] secret)
    {
        string text = Encoding.UTF8.GetString(data.ToArray());
        string replaced = text.Replace(Encoding.UTF8.GetString(secret), "[REDACTED]", StringComparison.Ordinal);
        if (replaced != text)
        {
            data.Clear();
            data.AddRange(Encoding.UTF8.GetBytes(replaced));
        }
    }

    private static int RelaySecretPrefixSuffixLength(List<byte> data, byte[] secret)
    {
        int limit = Math.Min(secret.Length - 1, data.Count);
        for (int length = limit; length > 0; length--)
        {
            bool match = true;
            for (int index = 0; index < length; index++)
            {
                if (data[data.Count - length + index] != secret[index])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return length;
            }
        }
        return 0;
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content, long limit, CancellationToken cancellationToken)
    {
        await using System.IO.Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[32 << 10];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidOperationException("response body is too large");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return buffer.ToArray();
    }

    private static byte[] RedactSecret(byte[] body, string apiKey)
    {
        if (apiKey.Length == 0)
        {
            return body;
        }
        return Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(body).Replace(apiKey, "[REDACTED]", StringComparison.Ordinal));
    }

    private static string UserFacingError(PlatformSettingsService settings, string fallback)
    {
        try
        {
            ResponseInterceptionSettingDto setting = settings.ReadInterceptionAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            if (!setting.Enabled)
            {
                return fallback;
            }
            string lower = fallback.ToLowerInvariant();
            foreach (ResponseInterceptionRuleDto rule in setting.Rules)
            {
                if (lower.Contains(rule.Contains?.ToLowerInvariant() ?? "", StringComparison.Ordinal))
                {
                    return rule.Replace ?? fallback;
                }
            }
            return fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static async Task<string> InterceptTextAsync(
        PlatformSettingsService settings, string raw, CancellationToken cancellationToken)
    {
        try
        {
            ResponseInterceptionSettingDto setting = await settings
                .ReadInterceptionAsync(cancellationToken).ConfigureAwait(false);
            if (!setting.Enabled)
            {
                return raw;
            }
            string lower = raw.ToLowerInvariant();
            foreach (ResponseInterceptionRuleDto rule in setting.Rules)
            {
                if (lower.Contains(rule.Contains?.ToLowerInvariant() ?? "", StringComparison.Ordinal))
                {
                    return rule.Replace ?? raw;
                }
            }
            return raw;
        }
        catch
        {
            return raw;
        }
    }

    private static void WriteFail(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.Headers["Content-Type"] = "application/json; charset=utf-8";
        string body = JsonSerializer.Serialize(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = status,
                ["data"] = null,
                ["msg"] = message,
                ["reason"] = status switch
                {
                    400 => "invalid_argument",
                    401 => "unauthorized",
                    403 => "forbidden",
                    413 => "payload_too_large",
                    429 => "rate_limited",
                    502 => "bad_gateway",
                    _ => "internal",
                },
            });
        context.Response.WriteAsync(body).GetAwaiter().GetResult();
    }

    private static readonly JsonSerializerOptions ReadOptions = new(GoJson.ReadOptions)    {
        PropertyNameCaseInsensitive = true,
    };

    private static async Task<T?> ReadJsonAsync<T>(HttpContext context, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(ReadOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------ 白名单授权

    private static string? AuthorizeCustomRelay(string method, Uri target, string apiFormat, string contentType)
    {
        string requestPath;
        try
        {
            requestPath = NormalizedCustomRelayPath(target.AbsolutePath);
        }
        catch (AppError error)
        {
            return error.Message;
        }
        var query = System.Web.HttpUtility.ParseQueryString(target.Query);
        foreach (string? key in query.AllKeys)
        {
            switch (key?.Trim().ToLowerInvariant())
            {
                case "key":
                case "api_key":
                case "access_token":
                case "token":
                    return "自定义渠道地址不允许在查询参数中携带密钥";
            }
        }
        apiFormat = apiFormat.Trim().ToLowerInvariant();
        if (apiFormat is not ("openai" or "gemini" or "claude"))
        {
            return "自定义渠道调用格式无效";
        }
        string Get(string key) => query[key] ?? "";
        if (method == HttpMethods.Get)
        {
            if (apiFormat == "openai" && requestPath == "/agnesapi")
            {
                if (query.Count == 2 && Get("video_id").Trim().Length > 0 && Get("model_name").Trim().Length > 0)
                {
                    return null;
                }
                return "Agnes 视频查询必须提供 video_id 和 model_name";
            }
            if (CustomNovitaTaskResultPath.IsMatch(requestPath))
            {
                if (query.Count == 1 && Get("task_id").Trim().Length > 0)
                {
                    return null;
                }
                return "自定义渠道不允许访问该上游接口";
            }
            bool allowed = requestPath == "/models" || requestPath.EndsWith("/models", StringComparison.Ordinal);
            if (apiFormat == "openai")
            {
                allowed = allowed
                    || CustomVideoTaskPath.IsMatch(requestPath)
                    || CustomXAIVideoTaskPath.IsMatch(requestPath)
                    || CustomVideoContentPath.IsMatch(requestPath)
                    || CustomArkVideoTaskPath.IsMatch(requestPath)
                    || CustomMiniMaxTaskPath.IsMatch(requestPath);
            }
            else if (apiFormat == "gemini")
            {
                allowed = allowed || CustomGeminiOperationPath.IsMatch(requestPath);
            }
            if (query.Count != 0 || !allowed)
            {
                return "自定义渠道不允许访问该上游接口";
            }
            return null;
        }
        if (method != HttpMethods.Post)
        {
            return "自定义渠道不允许使用该请求方法";
        }
        string mediaType = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (apiFormat == "openai")
        {
            bool multipartAllowed = mediaType == "multipart/form-data"
                && (requestPath.EndsWith("/images/edits", StringComparison.Ordinal)
                    || requestPath.EndsWith("/videos", StringComparison.Ordinal));
            bool jsonAllowed = mediaType == "application/json" && (
                requestPath.EndsWith("/responses", StringComparison.Ordinal)
                || requestPath.EndsWith("/chat/completions", StringComparison.Ordinal)
                || requestPath.EndsWith("/images/generations", StringComparison.Ordinal)
                || requestPath.EndsWith("/images/edits", StringComparison.Ordinal)
                || requestPath.EndsWith("/audio/speech", StringComparison.Ordinal)
                || requestPath.EndsWith("/video/generations", StringComparison.Ordinal)
                || requestPath.EndsWith("/videos/generations", StringComparison.Ordinal)
                || requestPath.EndsWith("/videos", StringComparison.Ordinal)
                || requestPath.EndsWith("/contents/generations/tasks", StringComparison.Ordinal)
                || requestPath.EndsWith("/video/create", StringComparison.Ordinal)
                || requestPath.EndsWith("/v2/video_generation", StringComparison.Ordinal));
            if (query.Count != 0 || (!multipartAllowed && !jsonAllowed))
            {
                return "自定义渠道不允许访问该上游接口";
            }
            return null;
        }
        if (apiFormat == "claude")
        {
            if (mediaType != "application/json"
                || !requestPath.EndsWith("/messages", StringComparison.Ordinal))
            {
                return "Claude 自定义渠道只允许 application/json 的 /messages 请求";
            }
            return null;
        }
        if (mediaType != "application/json")
        {
            return "Gemini 自定义渠道生成请求必须使用 application/json";
        }
        if (!CustomGeminiRelayPath.IsMatch(requestPath))
        {
            return "自定义渠道不允许访问该上游接口";
        }
        if (query.Count == 0)
        {
            return null;
        }
        if (query.Count == 1 && Get("alt") == "sse"
            && requestPath.EndsWith(":streamGenerateContent", StringComparison.Ordinal))
        {
            return null;
        }
        return "自定义渠道不允许使用该查询参数";
    }

    private static string NormalizedCustomRelayPath(string value)
    {
        if (value.Length > 2048)
        {
            throw AppError.BadAuthRequest("自定义渠道请求路径过长");
        }
        string decoded = Uri.UnescapeDataString(value);
        if (decoded.Contains('\\') || decoded.Contains('\0'))
        {
            throw AppError.BadAuthRequest("自定义渠道请求路径无效");
        }
        string cleaned = "/" + decoded.TrimStart('/');
        if (cleaned != decoded && cleaned != "/" + decoded.TrimStart('/'))
        {
            throw AppError.BadAuthRequest("自定义渠道请求路径无效");
        }
        return cleaned;
    }

    /// <summary>Bearer 密钥解析。对应 Go: <c>customRelayAPIKey</c>。</summary>
    private static string ParseRelayApiKey(string value)
    {
        value = value.Trim();
        int space = value.IndexOf(' ');
        string scheme = space > 0 ? value[..space] : value;
        string apiKey = space > 0 ? value[(space + 1)..].Trim() : "";
        if (!string.Equals(scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || apiKey.Length == 0 || apiKey.Length > 512
            || apiKey.Contains('\r') || apiKey.Contains('\n'))
        {
            throw AppError.Unauthorized("自定义渠道 API Key 无效");
        }
        return apiKey;
    }

    /// <summary>base64(JSON) 出站头解码。对应 Go: <c>DecodeRelayOutboundHeaders</c>。</summary>
    private static List<OutboundHeader> DecodeRelayOutboundHeaders(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return [];
        }
        if (encoded.Length > (16 << 10) * 2 + 1024)
        {
            throw AppError.BadAuthRequest("自定义请求头配置过大");
        }
        string json;
        try
        {
            json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            throw AppError.BadAuthRequest("自定义请求头编码无效");
        }
        return OutboundGuard.ParseOutboundHeadersJson(json);
    }
}
