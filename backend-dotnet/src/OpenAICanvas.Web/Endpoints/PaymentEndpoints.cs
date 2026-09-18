#nullable enable
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 支付路由。对应 Go: <c>handler/payment.go</c> 的全部 21 条。
/// </summary>
/// <remarks>
/// 适配器注册表当前为空（内置微信/支付宝适配器尚未移植），所以
/// <c>GET /payments/providers</c> 返回空数组、下单报「未知支付渠道」。
/// 其余逻辑（商品、订单状态机、回调收件箱、入账）均已完整实现。
/// </remarks>
public static partial class PaymentEndpoints
{
    /// <summary>回调请求体上限 1MB。对应 Go: <c>paymentNotificationMaxBytes</c>。</summary>
    private const long NotificationBodyLimit = 1L << 20;

    /// <summary>订单 ID 形态：32 位小写十六进制。对应 Go: <c>paymentOrderIDPattern</c>。</summary>
    [GeneratedRegex("^[a-f0-9]{32}$")]
    private static partial Regex PaymentOrderIdPattern();

    public static void MapPaymentRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        IRateLimiter limiter)
    {
        // ------------------------------------------------------------ 用户侧：渠道与商品

        api.MapGet("/payments/providers", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                List<PaymentProviderView> providers = await service.Payments
                    .PaymentProvidersAsync(actor, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { providers });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/payments/products", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                IReadOnlyList<TopupProduct> products = await service.Payments
                    .TopupProductsAsync(actor, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { products });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        // ------------------------------------------------------------ 用户侧：订单

        api.MapPost("/payments/orders", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            CreatePaymentOrderRequest? request = await ReadJsonAsync<CreatePaymentOrderRequest>(
                context, NotificationBodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                PaymentOrderView order = await service.Payments
                    .CreatePaymentOrderAsync(actor, request, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { order });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/payments/orders/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                PaymentOrderView order = await service.Payments
                    .PaymentOrderAsync(actor, id, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { order });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPost("/payments/orders/{id}/query", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                PaymentOrderView order = await service.Payments
                    .QueryPaymentOrderAsync(actor, id, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { order });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPost("/payments/orders/{id}/close", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                PaymentOrderView order = await service.Payments
                    .ClosePaymentOrderAsync(actor, id, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { order });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/payments/orders/{id}/checkout", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                string target = await service.Payments
                    .PaymentCheckoutAsync(actor, id, cancellationToken).ConfigureAwait(false);

                // 302 跳转到渠道收银台。
                return Results.Redirect(target);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPost("/payments/orders/{id}/checkout/refresh", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                // 刷新会打到渠道接口，按「用户 + 订单」限流。
                if (!await EnforceRateLimitAsync(
                        context, limiter, "payment-checkout-refresh:" + actor.ID + ":" + id,
                        5, TimeSpan.FromHours(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }

                PaymentOrderView order = await service.Payments
                    .RefreshPaymentCheckoutAsync(actor, id, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { order });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        // ------------------------------------------------------------ 回调与返回页

        // 回调在应用层故意不鉴权：真实性由「固定的渠道配置 + 原始报文验签」保证，
        // 验签通过后才写入持久化收件箱。
        api.MapPost("/payments/notify/{providerId}/{configId}", async (HttpContext context, string providerId, string configId, CancellationToken cancellationToken) =>
        {
            if (context.Request.ContentLength is > 0 && context.Request.ContentLength > NotificationBodyLimit)
            {
                return WriteNotificationFailure(context, service, providerId, StatusCodes.Status400BadRequest);
            }

            byte[] rawBody;
            try
            {
                using MemoryStream buffer = new();
                await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (buffer.Length > NotificationBodyLimit)
                {
                    return WriteNotificationFailure(context, service, providerId, StatusCodes.Status400BadRequest);
                }
                rawBody = buffer.ToArray();
            }
            catch (Exception)
            {
                return WriteNotificationFailure(context, service, providerId, StatusCodes.Status400BadRequest);
            }

            // 请求头值在 ASP.NET 里是 string?[]；插件协议要的是 string[]，这里统一把 null 折成空串。
            Dictionary<string, string[]> headers = context.Request.Headers
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(value => value ?? "").ToArray(),
                    StringComparer.Ordinal);

            try
            {
                await service.Payments.AcceptPaymentNotificationAsync(
                    providerId, configId, headers, rawBody, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // 业务性失败（验签不通过、订单不匹配）回 400，其余回 500。
                int status = error is AppError appError && appError.Status < 500
                    ? StatusCodes.Status400BadRequest
                    : StatusCodes.Status500InternalServerError;
                return WriteNotificationFailure(context, service, providerId, status);
            }

            (int successStatus, string contentType, string body) = service.Payments.NotificationResponse(providerId, true);
            if (body.Length > 0)
            {
                context.Response.ContentType = contentType;
                context.Response.StatusCode = successStatus;
                await context.Response.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                return Results.Empty;
            }

            return Results.StatusCode(successStatus);
        });

        api.MapGet("/payments/return/{providerId}", (HttpContext context, string providerId) =>
        {
            // 渠道同步跳回：只做参数净化与前端路由转发，不在这里改订单状态。
            string orderId = context.Request.Query["orderId"].ToString().Trim().ToLowerInvariant();
            if (!PaymentOrderIdPattern().IsMatch(orderId))
            {
                return Results.Redirect("/wallet?payment=invalid");
            }

            return Results.Redirect("/wallet?paymentOrder=" + Uri.EscapeDataString(orderId));
        });

        // ------------------------------------------------------------ 管理端

        api.MapGet("/admin/payments/providers", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                List<AdminPaymentProviderView> providers = await service.Payments
                    .AdminPaymentProvidersAsync(actor, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { providers });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPut("/admin/payments/providers/{id}/config", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            UpdatePaymentProviderConfigRequest? request = await ReadJsonAsync<UpdatePaymentProviderConfigRequest>(
                context, NotificationBodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                AdminPaymentProviderView provider = await service.Payments
                    .UpdatePaymentProviderConfigAsync(actor, id, request, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { provider });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/admin/payments/products", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                IReadOnlyList<TopupProduct> products = await service.Payments
                    .AdminTopupProductsAsync(actor, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { products });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPost("/admin/payments/products", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            TopupProductRequest? request = await ReadJsonAsync<TopupProductRequest>(
                context, NotificationBodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                TopupProduct product = await service.Payments
                    .CreateTopupProductAsync(actor, request, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { product });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPut("/admin/payments/products/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            TopupProductRequest? request = await ReadJsonAsync<TopupProductRequest>(
                context, NotificationBodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                TopupProduct product = await service.Payments
                    .UpdateTopupProductAsync(actor, id, request, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { product });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/admin/payments/orders", async (HttpContext context, CancellationToken cancellationToken) =>
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

                AdminPaymentOrderPage result = await service.Payments.AdminPaymentOrderPageAsync(
                    actor,
                    context.Request.Query["status"].ToString(),
                    context.Request.Query["keyword"].ToString(),
                    page, pageSize, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPost("/admin/payments/orders/{id}/query", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                PaymentOrderView order = await service.Payments
                    .AdminQueryPaymentOrderAsync(actor, id, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { order });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPost("/admin/payments/orders/{id}/close", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                PaymentOrderView order = await service.Payments
                    .AdminClosePaymentOrderAsync(actor, id, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { order });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        // ------------------------------------------------------------ 管理端：对账

        api.MapPost("/admin/payments/reconciliations", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            RunPaymentReconciliationRequest? request = await ReadJsonAsync<RunPaymentReconciliationRequest>(
                context, NotificationBodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                PaymentReconciliationRun run = await service.Payments
                    .RunPaymentReconciliationAsync(actor, request, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { run });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/admin/payments/reconciliations", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
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

                AdminPaymentReconciliationPage result = await service.Payments.AdminPaymentReconciliationPageAsync(
                    actor,
                    context.Request.Query["providerId"].ToString(),
                    context.Request.Query["status"].ToString(),
                    page, pageSize, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/admin/payments/reconciliations/{id}/items", async (HttpContext context, string id, CancellationToken cancellationToken) =>
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

                AdminPaymentReconciliationItemPage result = await service.Payments
                    .AdminPaymentReconciliationItemsAsync(
                        actor, id, context.Request.Query["result"].ToString(),
                        page, pageSize, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });
    }

    // ------------------------------------------------------------ 辅助

    /// <summary>
    /// 回调失败应答。对应 Go: <c>writePaymentNotificationFailure</c>。
    /// </summary>
    /// <remarks>
    /// 渠道自定义了失败应答体就原样返回（支付宝要求回 <c>failure</c>），
    /// 否则回通用 JSON。注意这里用的是<b>渠道自定义的状态码</b>，不是入参 status。
    /// </remarks>
    private static IResult WriteNotificationFailure(
        HttpContext context, CanvasService service, string providerId, int status)
    {
        (int responseStatus, string contentType, string body) =
            service.Payments.NotificationFailureResponse(providerId, status);

        if (body.Length > 0)
        {
            context.Response.ContentType = contentType;
            return Results.Text(body, contentType, statusCode: responseStatus);
        }

        return Results.Json(
            new { code = "FAIL", message = ReasonPhrase(responseStatus) },
            statusCode: responseStatus);
    }

    /// <summary>与 Go 的 <c>http.StatusText</c> 对齐的最小映射。</summary>
    private static string ReasonPhrase(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "Unknown",
    };

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
}
