#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Platform;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 钱包、兑换码与账单路由。对应 Go: <c>handler/finance.go</c>
/// 中不依赖支付 SDK 与结算引擎的部分。
/// </summary>
/// <remarks>
/// 未实现（见 PENDING-CONFIRMATIONS）：
/// <c>POST /admin/billing-orders/:id/resolve</c> 与 <c>batch-resolve</c>（依赖结算/退款）、
/// <c>/payments/**</c>（依赖支付 SDK 与独立 RPC 插件进程）。
/// </remarks>
public static class FinanceEndpoints
{
    /// <summary>创建批次请求体上限 64KB。对应 Go: <c>64&lt;&lt;10</c>。</summary>
    private const long BatchBodyLimit = 64L << 10;

    public static void MapFinanceRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        IRateLimiter limiter,
        IRuntimePolicyProvider policyProvider)
    {
        // ------------------------------------------------------------ 钱包

        api.MapGet("/wallet", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            if (!TryParsePagination(context, fallbackPageSize: 30, out int page, out int pageSize, out string? error))
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, new InvalidOperationException(error));
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                WalletSummaryDto wallet = await service.Finance.WalletAsync(
                    actor, context.Request.Query["type"].ToString(), page, pageSize, cancellationToken)
                    .ConfigureAwait(false);

                return ApiResults.Ok(wallet);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPost("/wallet/redeem", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                // 限流：每用户每小时 10 次（与 Go 一致）。
                if (!await EnforceRateLimitAsync(context, limiter, "redeem:" + actor.ID, 10, TimeSpan.FromHours(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }

                RedeemRequest? request = await ReadJsonAsync<RedeemRequest>(
                    context, BatchBodyLimit, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }

                CreditAccount account = await service.Finance.RedeemCreditsAsync(
                    actor, request.Code, ClientIp(context), cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { account });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPost("/wallet/checkin", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                (CreditAccount account, bool granted) = await service.Finance
                    .CheckinCreditsAsync(actor, cancellationToken).ConfigureAwait(false);

                if (!granted)
                {
                    // 已签到：409（不是 400），与 Go 一致。
                    return ApiResults.Fail(
                        StatusCodes.Status409Conflict,
                        AppError.BadAuthRequest("今天已经签到过了"));
                }

                return ApiResults.Ok(new { account, granted = true });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        // ------------------------------------------------------------ 兑换码批次（管理端）

        api.MapGet("/admin/redeem-batches", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            if (!TryParsePagination(context, fallbackPageSize: 20, out int page, out int pageSize, out string? error))
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, new InvalidOperationException(error));
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                RedeemBatchPageDto result = await service.Finance.AdminRedeemBatchPageAsync(
                    actor,
                    context.Request.Query["keyword"].ToString(),
                    context.Request.Query["validity"].ToString(),
                    page, pageSize, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPost("/admin/redeem-batches", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                // 限流：每管理员每小时 20 次（与 Go 一致）。
                if (!await EnforceRateLimitAsync(context, limiter, "redeem-batch-create:" + actor.ID, 20, TimeSpan.FromHours(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }

                CreateRedeemBatchRequest? request = await ReadJsonAsync<CreateRedeemBatchRequest>(
                    context, BatchBodyLimit, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }

                CreateRedeemBatchResultDto result = await service.Finance
                    .AdminCreateRedeemBatchAsync(actor, request, cancellationToken).ConfigureAwait(false);

                // 响应含兑换码明文，禁止任何缓存。
                context.Response.Headers["Cache-Control"] = "no-store";
                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/admin/redeem-batches/{id}/codes", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            if (!TryParsePagination(context, fallbackPageSize: 50, out int page, out int pageSize, out string? error))
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, new InvalidOperationException(error));
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                AdminRedeemCodePageDto result = await service.Finance.AdminRedeemCodePageAsync(
                    actor, id, context.Request.Query["status"].ToString(), page, pageSize, cancellationToken)
                    .ConfigureAwait(false);

                context.Response.Headers["Cache-Control"] = "no-store";
                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPost("/admin/redeem-batches/{id}/disable", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                long count = await service.Finance
                    .AdminDisableRedeemBatchAsync(actor, id, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { disabledCount = count });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPost("/admin/redeem-batches/{id}/codes/{codeId}/disable", async (HttpContext context, string id, string codeId, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                await service.Finance
                    .AdminDisableRedeemCodeAsync(actor, id, codeId, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        // ------------------------------------------------------------ 调账与账单

        api.MapPost("/admin/users/{id}/credits/adjust", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            AdminCreditAdjustmentRequest? request = await ReadJsonAsync<AdminCreditAdjustmentRequest>(
                context, BatchBodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                CreditAccount account = await service.Finance
                    .AdminAdjustCreditsAsync(actor, id, request, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { account });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/admin/billing-orders", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            if (!TryParsePagination(context, fallbackPageSize: 20, out int page, out int pageSize, out string? error))
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, new InvalidOperationException(error));
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                // status 默认 review（待人工核对），与 Go 的 DefaultQuery 一致。
                string status = context.Request.Query["status"].ToString();
                if (status.Length == 0)
                {
                    status = "review";
                }

                BillingOrderPageDto result = await service.Finance.AdminBillingOrderPageAsync(
                    actor, status, context.Request.Query["keyword"].ToString(),
                    page, pageSize, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        // ------------------------------------------------------------ 账单人工核对

        // 注意：字面量段必须与 :id 区分；ASP.NET 路由按字面量优先匹配，顺序不敏感。
        api.MapPost("/admin/billing-orders/batch-resolve", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            ResolveBillingBatchRequest? request = await ReadJsonAsync<ResolveBillingBatchRequest>(
                context, BatchBodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                ResolveBillingBatchResultDto result = await service.Finance
                    .ResolveBillingOrdersAsync(actor, request, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPost("/admin/billing-orders/{id}/resolve", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            ResolveBillingRequest? request = await ReadJsonAsync<ResolveBillingRequest>(
                context, BatchBodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                BillingOrder order = await service.Finance
                    .ResolveBillingOrderAsync(actor, id, request, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(order);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });
    }

    // ------------------------------------------------------------ 辅助

    private static async Task<bool> TryRequireUserAsync(
        HttpContext context, CanvasService service, CancellationToken cancellationToken)
    {
        try
        {
            await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            IResult result = ApiResults.FailService(error, context);
            await result.ExecuteAsync(context).ConfigureAwait(false);
            return false;
        }
    }

    private static async Task<bool> TryRequireAdminAsync(
        HttpContext context, CanvasService service, CancellationToken cancellationToken)
    {
        try
        {
            User actor = await service.CurrentUserAsync(
                SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
            CanvasService.RequireAdmin(actor);
            return true;
        }
        catch (Exception error)
        {
            IResult result = ApiResults.FailService(error, context);
            await result.ExecuteAsync(context).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// 解析 page / pageSize。对应 Go: <c>parsePaginationQuery</c>。
    /// 缺省取 fallback；显式传入非正整数视为请求错误。
    /// </summary>
    private static bool TryParsePagination(
        HttpContext context, int fallbackPageSize, out int page, out int pageSize, out string? error)
    {
        page = 1;
        pageSize = fallbackPageSize;
        error = null;

        if (!TryParsePositive(context.Request.Query["page"], 1, "page", out page, out error))
        {
            return false;
        }

        return TryParsePositive(context.Request.Query["pageSize"], fallbackPageSize, "pageSize", out pageSize, out error);
    }

    private static bool TryParsePositive(
        string? raw, int fallback, string name, out int value, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(raw))
        {
            value = fallback;
            return true;
        }

        if (!int.TryParse(raw, out int parsed) || parsed < 1)
        {
            value = 0;
            error = $"{name}: query parameter must be a positive integer";
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// 限流检查。被限流时写出 429 + Retry-After 并返回 false。
    /// 对应 Go: <c>handler/security.go: enforceRateLimit</c>。
    /// </summary>
    private static async Task<bool> EnforceRateLimitAsync(
        HttpContext context, IRateLimiter limiter, string key, int limit, TimeSpan window)
    {
        if (limit <= 0 || limiter.AllowRequest(key, limit, window))
        {
            return true;
        }

        TimeSpan wait = limiter.RetryAfter(key, window);
        int seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        context.Response.Headers["Retry-After"] = seconds.ToString();

        IResult result = ApiResults.FailService(
            AppError.RateLimited($"请求次数已达上限，请在 {seconds} 秒后重试"), context);
        await result.ExecuteAsync(context).ConfigureAwait(false);
        return false;
    }

    /// <summary>客户端 IP。对应 Go: <c>c.ClientIP()</c>。</summary>
    private static string ClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "";

    private static async Task<T?> ReadJsonAsync<T>(
        HttpContext context, long maxBytes, CancellationToken cancellationToken)
        where T : class
    {
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maxBytes)
        {
            return null;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                context.Request.Body, CanvasJson.ReadOptions, cancellationToken).ConfigureAwait(false);
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

    /// <summary>兑换码核销请求体。对应 Go 的内联匿名结构 <c>struct{ Code string }</c>。</summary>
    private sealed class RedeemRequest
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = "";
    }
}
