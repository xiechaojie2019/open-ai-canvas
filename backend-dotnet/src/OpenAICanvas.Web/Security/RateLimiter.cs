using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace OpenAICanvas.Web.Security;

/// <summary>
/// 请求限流。对应 Go: <c>platform.Service.AllowRequest</c> / <c>RequestRetryAfter</c>。
/// </summary>
/// <remarks>
/// 当前是单进程内存实现（固定窗口计数）。Go 侧由 <c>internal/platform</c> 承担，
/// 在多实例部署时通过 Redis 协调——那一层属于后续模块，接口保持一致以便替换。
/// </remarks>
public interface IRateLimiter
{
    /// <summary>是否允许本次请求。</summary>
    bool AllowRequest(string key, int limit, TimeSpan window);

    /// <summary>被限流时还需等待多久。</summary>
    TimeSpan RetryAfter(string key, TimeSpan window);
}

/// <summary>固定窗口内存限流器。</summary>
public sealed class InMemoryRateLimiter : IRateLimiter
{
    private sealed class Window
    {
        public long WindowStartTicks;
        public int Count;
    }

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public InMemoryRateLimiter(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public bool AllowRequest(string key, int limit, TimeSpan window)
    {
        if (limit <= 0)
        {
            // 上限为 0 表示不限制，与 Go 的 policy 语义一致。
            return true;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        Window entry = _windows.GetOrAdd(key, _ => new Window { WindowStartTicks = now.UtcTicks });

        lock (entry)
        {
            if (now.UtcTicks - entry.WindowStartTicks >= window.Ticks)
            {
                entry.WindowStartTicks = now.UtcTicks;
                entry.Count = 0;
            }

            if (entry.Count >= limit)
            {
                return false;
            }

            entry.Count++;
            return true;
        }
    }

    /// <summary>当前窗口数量（管理端缓存清理用）。</summary>
    public int CountWindows() => _windows.Count;

    /// <summary>清空全部窗口并返回清理数量（管理端缓存清理用）。</summary>
    public int ClearWindows()
    {
        int count = _windows.Count;
        _windows.Clear();
        return count;
    }

    public TimeSpan RetryAfter(string key, TimeSpan window)
    {
        if (!_windows.TryGetValue(key, out Window? entry))
        {
            return TimeSpan.Zero;
        }

        lock (entry)
        {
            long elapsed = _timeProvider.GetUtcNow().UtcTicks - entry.WindowStartTicks;
            long remaining = window.Ticks - elapsed;
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(remaining);
        }
    }
}

/// <summary>会话 Cookie 读写。对应 Go: <c>handler/auth.go</c> 的 setSessionCookie / clearSessionCookie。</summary>
public static class SessionCookie
{
    /// <summary>Cookie 名。对应 Go: <c>auth.SessionCookieName</c>。</summary>
    public const string Name = "open_ai_canvas_session";

    /// <summary>读取 Cookie 值；不存在返回空串。</summary>
    public static string Read(HttpContext context) =>
        context.Request.Cookies.TryGetValue(Name, out string? value) ? value : string.Empty;

    /// <summary>
    /// 写入会话 Cookie。
    /// </summary>
    /// <remarks>
    /// 手工拼 Set-Cookie 而不是用 <c>Response.Cookies.Append</c>：Go 的 <c>http.Cookie.String()</c>
    /// 输出 <c>Name=Value; Path=/; Max-Age=N; HttpOnly; SameSite=Lax</c>（属性顺序与大小写固定），
    /// ASP.NET 默认会输出 <c>max-age=</c> 小写并调整顺序，导致响应头不一致。
    /// </remarks>
    public static void Set(HttpContext context, string value, int maxAgeSeconds)
    {
        bool secure = IsSecure(context);
        context.Response.Headers.Append(
            "Set-Cookie",
            $"{Name}={value}; Path=/; Max-Age={maxAgeSeconds}; HttpOnly; SameSite=Lax" + (secure ? "; Secure" : string.Empty));
    }

    /// <summary>
    /// 清除会话 Cookie。对应 Go 的 <c>clearSessionCookie</c>。
    /// </summary>
    /// <remarks>Go 对 <c>MaxAge &lt; 0</c> 输出 <c>Max-Age=0</c>，不是 <c>-1</c>。</remarks>
    public static void Clear(HttpContext context)
    {
        bool secure = IsSecure(context);
        context.Response.Headers.Append(
            "Set-Cookie",
            $"{Name}=; Path=/; Max-Age=0; HttpOnly; SameSite=Lax" + (secure ? "; Secure" : string.Empty));
    }

    /// <summary>
    /// 是否标记 Secure：TLS 直连，或反向代理声明了 <c>X-Forwarded-Proto: https</c>。
    /// </summary>
    private static bool IsSecure(HttpContext context)
    {
        if (context.Request.IsHttps)
        {
            return true;
        }

        string forwarded = context.Request.Headers["X-Forwarded-Proto"].ToString().Trim();
        return string.Equals(forwarded, "https", StringComparison.OrdinalIgnoreCase);
    }
}
