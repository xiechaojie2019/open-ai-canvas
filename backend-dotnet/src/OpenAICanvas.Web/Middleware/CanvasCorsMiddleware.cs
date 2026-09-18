using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenAICanvas.Web.Contracts;
using OpenAICanvas.Web.Serialization;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Web.Middleware;

/// <summary>CORS 白名单策略，对应 Go 的 <c>corsPolicy</c>。</summary>
public sealed class CorsPolicy
{
    public HashSet<string> Origins { get; } = new(StringComparer.Ordinal);
    public bool AllowAny { get; set; }

    /// <summary>对应 Go: <c>parseCORSPolicy</c>。非法来源直接让进程启动失败。</summary>
    public static CorsPolicy Parse(string? raw)
    {
        CorsPolicy policy = new();
        foreach (string item in (raw ?? string.Empty).Split(','))
        {
            string value = item.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (value == "*")
            {
                policy.AllowAny = true;
                continue;
            }

            string normalized = NormalizeOrigin(value)
                ?? throw new InvalidOperationException(
                    $"CANVAS_CORS_ORIGINS contains invalid origin \"{value}\": origin must be an http or https origin");
            policy.Origins.Add(normalized);
        }

        return policy;
    }

    /// <summary>对应 Go: <c>normalizeCORSOrigin</c>。返回 null 表示非法。</summary>
    public static string? NormalizeOrigin(string raw)
    {
        string value = raw.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed))
        {
            return null;
        }

        if (parsed.Host.Length == 0 || parsed.UserInfo.Length > 0)
        {
            return null;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        // Go 校验的是 URL 原始串；Uri 会把 "/" 归一为空 Path，这里显式排除非根路径。
        if (parsed.AbsolutePath != "/" && parsed.AbsolutePath.Length != 0)
        {
            return null;
        }

        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            return null;
        }

        return $"{parsed.Scheme.ToLowerInvariant()}://{parsed.Authority.ToLowerInvariant()}";
    }

    /// <summary>对应 Go: <c>allowedOriginWithPolicy</c>。</summary>
    public bool Allows(HttpContext context, string origin)
    {
        string? normalizedOrigin = NormalizeOrigin(origin);
        if (normalizedOrigin is null)
        {
            return false;
        }

        if (!Uri.TryCreate(normalizedOrigin, UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        string requestHost = context.Request.Host.Value;
        string forwardedHost = context.Request.Headers["X-Forwarded-Host"].ToString();
        if (forwardedHost.Trim().Length > 0)
        {
            requestHost = forwardedHost.Split(',')[0].Trim();
        }

        if (string.Equals(parsed.Authority, requestHost.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (AllowAny)
        {
            return true;
        }

        if (Origins.Contains(normalizedOrigin))
        {
            return true;
        }

        if (Origins.Count > 0)
        {
            return false;
        }

        string host = parsed.Host.ToLowerInvariant();
        bool isLoopback = host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
        return isLoopback && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
    }
}

/// <summary>
/// 逐字复刻 Go 版 CORS 中间件，包括非法 Origin 直接 403 的行为。
/// </summary>
/// <remarks>对应 Go: cmd/server/main.go 的 <c>cors()</c>。</remarks>
public sealed class CanvasCorsMiddleware
{
    public const string AllowedHeaders =
        "Accept, Content-Type, Authorization, X-Requested-With, X-Canvas-Scene, X-Idempotency-Key, " +
        "X-Canvas-Trace-ID, X-Canvas-Upstream-URL, X-Canvas-Upstream-Format, X-Canvas-Upstream-Base-URL";

    public const string AllowedMethods = "GET, POST, PUT, PATCH, DELETE, OPTIONS";

    public const string ExposedHeaders =
        "X-Request-ID, X-Canvas-Trace-ID, X-Diagnostic-Bundle-ID, X-Diagnostic-Schema-Version";

    private readonly RequestDelegate _next;
    private readonly CorsPolicy _policy;

    public CanvasCorsMiddleware(RequestDelegate next, CorsPolicy policy)
    {
        _next = next;
        _policy = policy;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string origin = context.Request.Headers.Origin.ToString().Trim();
        if (origin.Length > 0 && !_policy.Allows(context, origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";
            // Go 这里用 gin.H 且不带 reason，因此响应体只有 code / data / msg。
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                new ApiEnvelope { Code = 403, Data = null, Msg = "不允许的跨域来源" },
                CanvasJson.WriteOptions,
                context.RequestAborted);
            return;
        }

        if (origin.Length > 0)
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = origin;
            context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
            context.Response.Headers["Vary"] = "Origin, Access-Control-Request-Method, Access-Control-Request-Headers";
        }

        context.Response.Headers["Access-Control-Allow-Headers"] = AllowedHeaders;
        context.Response.Headers["Access-Control-Expose-Headers"] = ExposedHeaders;
        context.Response.Headers["Access-Control-Allow-Methods"] = AllowedMethods;
        context.Response.Headers["Access-Control-Max-Age"] = "86400";

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await _next(context);
    }
}
