using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace OpenAICanvas.Web.Middleware;

/// <summary>
/// 为每个请求生成服务端 requestId，并保留一次业务操作的 traceId。
/// </summary>
/// <remarks>
/// <para>
/// requestId 不能被客户端覆盖；traceId 只接受有限字符集，
/// 避免把任意请求头内容写入日志和诊断包。
/// </para>
/// <para>对应 Go: internal/handler/request-context.go</para>
/// </remarks>
public sealed partial class RequestCorrelationMiddleware
{
    public const string RequestIdHeader = "X-Request-ID";
    public const string TraceIdHeader = "X-Canvas-Trace-ID";
    public const string RequestIdKey = "canvas.request_id";
    public const string TraceIdKey = "canvas.trace_id";

    private readonly RequestDelegate _next;

    public RequestCorrelationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        string requestId = NewCorrelationId("req");
        string traceId = NormalizeCorrelationId(context.Request.Headers[TraceIdHeader].ToString());
        if (traceId.Length == 0)
        {
            traceId = NewCorrelationId("trace");
        }

        context.Items[RequestIdKey] = requestId;
        context.Items[TraceIdKey] = traceId;
        context.Response.Headers[RequestIdHeader] = requestId;
        context.Response.Headers[TraceIdHeader] = traceId;

        await _next(context);
    }

    public static string RequestId(HttpContext context) =>
        context.Items.TryGetValue(RequestIdKey, out object? value) && value is string text ? text : string.Empty;

    public static string TraceId(HttpContext context) =>
        context.Items.TryGetValue(TraceIdKey, out object? value) && value is string text ? text : string.Empty;

    private static string NormalizeCorrelationId(string value)
    {
        string trimmed = value.Trim();
        return CorrelationIdPattern().IsMatch(trimmed) ? trimmed : string.Empty;
    }

    private static string NewCorrelationId(string prefix)
    {
        Span<byte> raw = stackalloc byte[12];
        try
        {
            RandomNumberGenerator.Fill(raw);
        }
        catch (CryptographicException)
        {
            return prefix + "-unavailable";
        }

        return prefix + "-" + Convert.ToHexString(raw).ToLowerInvariant();
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,96}$")]
    private static partial Regex CorrelationIdPattern();
}
