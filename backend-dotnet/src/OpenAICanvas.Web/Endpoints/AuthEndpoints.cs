using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Application;
using OpenAICanvas.Auth;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Platform;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 认证路由。对应 Go: <c>internal/handler/auth.go: RegisterAuthRoutes</c>。
/// </summary>
/// <remarks>
/// 路径、请求体大小上限、限流键、响应信封与 Set-Cookie 都与 Go 逐一对齐。
/// </remarks>
public static class AuthEndpoints
{
    /// <summary>注册与登录的请求体上限 64KB。对应 Go 的 <c>64&lt;&lt;10</c>。</summary>
    private const long CredentialBodyLimit = 64 * 1024;

    /// <summary>邮箱验证码请求体上限 16KB。对应 Go 的 <c>16&lt;&lt;10</c>。</summary>
    private const long EmailCodeBodyLimit = 16 * 1024;

    public static void MapAuthRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        IRateLimiter limiter,
        IRuntimePolicyProvider policyProvider)
    {
        api.MapGet("/auth/settings", async (CancellationToken cancellationToken) =>
        {
            PublicAuthSettingsDto settings =
                await service.PublicAuthSettingsAsync(cancellationToken).ConfigureAwait(false);
            return ApiResults.Ok(settings);
        });

        api.MapPost("/auth/register", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            RegisterRequest? request = await ReadJsonAsync<RegisterRequest>(
                context, CredentialBodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            RuntimeRequestPolicy policy = policyProvider.Current().Request;
            if (!await EnforceRateLimitAsync(
                    context, limiter, "register:" + ClientIp(context), policy.RegisterPerHour, TimeSpan.FromHours(1))
                .ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                AuthSessionResultDto result =
                    await service.RegisterAsync(request, cancellationToken).ConfigureAwait(false);

                SessionCookie.Set(context, result.Session, result.MaxAgeSecs);
                return ApiResults.Ok(new { user = result.User });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/auth/email-code", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            EmailCodeRequest? request = await ReadJsonAsync<EmailCodeRequest>(
                context, EmailCodeBodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            RuntimeRequestPolicy policy = policyProvider.Current().Request;
            if (!await EnforceRateLimitAsync(
                    context, limiter, "email-code:" + ClientIp(context), policy.EmailCodePerHour, TimeSpan.FromHours(1))
                .ConfigureAwait(false))
            {
                return Results.Empty;
            }

            if (!await EnforceRateLimitAsync(
                    context,
                    limiter,
                    "registration-email-account:" + PasswordResetRateLimitSubject(request.Email),
                    10,
                    TimeSpan.FromHours(1)).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                await service.SendRegistrationEmailCodeAsync(request.Email, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { sent = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/auth/password-reset-code", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            EmailCodeRequest? request = await ReadJsonAsync<EmailCodeRequest>(
                context, EmailCodeBodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            RuntimeRequestPolicy policy = policyProvider.Current().Request;
            if (!await EnforceRateLimitAsync(
                    context, limiter, "password-reset-code-ip:" + ClientIp(context),
                    policy.EmailCodePerHour, TimeSpan.FromHours(1)).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            if (!await EnforceRateLimitAsync(
                    context, limiter,
                    "password-reset-code-account:" + PasswordResetRateLimitSubject(request.Email),
                    policy.EmailCodePerHour, TimeSpan.FromHours(1)).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                await service.SendPasswordResetEmailCodeAsync(request.Email, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { sent = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/auth/password-reset", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            PasswordResetRequest? request = await ReadJsonAsync<PasswordResetRequest>(
                context, CredentialBodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            RuntimeRequestPolicy policy = policyProvider.Current().Request;
            if (!await EnforceRateLimitAsync(
                    context, limiter, "password-reset-ip:" + ClientIp(context),
                    policy.LoginIpPerTenMinutes, TimeSpan.FromMinutes(10)).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            if (!await EnforceRateLimitAsync(
                    context, limiter,
                    "password-reset-account:" + PasswordResetRateLimitSubject(request.Email),
                    policy.LoginAccountPerTenMinutes, TimeSpan.FromMinutes(10)).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                await service.ResetPasswordAsync(request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { reset = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/auth/login", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            LoginRequest? request = await ReadJsonAsync<LoginRequest>(
                context, CredentialBodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            RuntimeRequestPolicy policy = policyProvider.Current().Request;
            if (!await EnforceRateLimitAsync(
                    context, limiter, "login-ip:" + ClientIp(context),
                    policy.LoginIpPerTenMinutes, TimeSpan.FromMinutes(10)).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            // 账号维度限流键：IP + 小写去空的账号名，与 Go 完全一致。
            string account = request.Username.Trim().ToLowerInvariant();
            if (!await EnforceRateLimitAsync(
                    context, limiter, "login:" + ClientIp(context) + ":" + account,
                    policy.LoginAccountPerTenMinutes, TimeSpan.FromMinutes(10)).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                AuthSessionResultDto result =
                    await service.LoginAsync(request, cancellationToken).ConfigureAwait(false);

                SessionCookie.Set(context, result.Session, result.MaxAgeSecs);
                return ApiResults.Ok(new { user = result.User });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/auth/session", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                SessionDto session = await service.SessionAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                if (session.User is null)
                {
                    // 未登录时只返回 {"user": null}，其他字段不出现（与 Go 的 gin.H{"user": nil} 一致）。
                    return ApiResults.Ok(new { user = (AuthUserDto?)null });
                }

                return ApiResults.Ok(session);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/auth/logout", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            // Go 忽略登出错误：Cookie 无效也要正常清理并返回成功。
            await service.LogoutAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
            SessionCookie.Clear(context);
            return ApiResults.Ok(new { ok = true });
        });
        // ------------------------------------------------------------ LinuxDO OAuth

        api.MapGet("/auth/linuxdo/start", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            RuntimeRequestPolicy policy = policyProvider.Current().Request;
            if (!await EnforceRateLimitAsync(
                    context, limiter, "linuxdo-start:" + ClientIp(context),
                    20, TimeSpan.FromMinutes(10)).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                string target = await service.Auth.BeginLinuxDOLoginAsync(
                    context.Request.Query["next"].ToString(), cancellationToken).ConfigureAwait(false);
                return Results.Redirect(target);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/auth/linuxdo/callback", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            RuntimeRequestPolicy policy = policyProvider.Current().Request;
            if (!await EnforceRateLimitAsync(
                    context, limiter, "linuxdo-callback:" + ClientIp(context),
                    30, TimeSpan.FromMinutes(10)).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                AuthService.LinuxDOCallbackResult result = await service.Auth
                    .CompleteLinuxDOLoginAsync(
                        context.Request.Query["state"].ToString(),
                        context.Request.Query["code"].ToString(),
                        cancellationToken).ConfigureAwait(false);

                SessionCookie.Set(context, result.Session.Session, result.Session.MaxAgeSecs);
                return Results.Redirect(result.Next);
            }
            catch (Exception error)
            {
                string message = error is AppError ae ? ae.Message : "Linux.do 登录失败";
                return Results.Redirect("/login?oauth_error=" + Uri.EscapeDataString(message));
            }
        });

    /// <summary>邮箱验证码请求体。对应 Go 的内联匿名结构 <c>struct{ Email string }</c>。</summary>
    }

    /// <summary>
    /// 限流检查。对应 Go: <c>handler/security.go: enforceRateLimit</c>。
    /// </summary>
    /// <remarks>
    /// 被限流时返回 429 + <c>Retry-After</c> + <c>code=42901</c>，并且<b>不再继续处理</b>。
    /// 调用方在返回 false 时应直接返回空结果（响应已写好）。
    /// </remarks>
    internal static async Task<bool> EnforceRateLimitAsync(
        HttpContext context,
        IRateLimiter limiter,
        string key,
        int limit,
        TimeSpan window)
    {
        if (limiter.AllowRequest(key, limit, window))
        {
            return true;
        }

        TimeSpan wait = limiter.RetryAfter(key, window);
        int seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        context.Response.Headers["Retry-After"] = seconds.ToString();

        // 立即写出 429，返回 false 让调用方短路。
        IResult result = ApiResults.FailService(
            AppError.RateLimited($"请求次数已达上限，请在 {seconds} 秒后重试"), context);
        await result.ExecuteAsync(context).ConfigureAwait(false);
        return false;
    }

    /// <summary>客户端 IP。对应 Go: <c>c.ClientIP()</c>。</summary>
    internal static string ClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

    /// <summary>邮箱维度限流键的主体：小写去空。对应 Go: <c>passwordResetRateLimitSubject</c>。</summary>
    private static string PasswordResetRateLimitSubject(string? email) =>
        (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// 读取并反序列化 JSON 请求体，超过上限返回 null（调用方回 400）。
    /// 对应 Go 的 <c>http.MaxBytesReader</c> + <c>ShouldBindJSON</c>。
    /// </summary>
    private static async Task<T?> ReadJsonAsync<T>(
        HttpContext context,
        long maxBytes,
        CancellationToken cancellationToken)
        where T : class
    {
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maxBytes)
        {
            return null;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                context.Request.Body,
                CanvasJson.ReadOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (BadHttpRequestException)
        {
            return null;
        }
    }

    private sealed class EmailCodeRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;
    }
}
