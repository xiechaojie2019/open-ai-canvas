using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 管理后台用户管理路由。对应 Go: <c>internal/handler/auth.go: RegisterAdminRoutes</c> 的用户部分。
/// </summary>
public static class AdminUserEndpoints
{
    /// <summary>请求体上限 64KB。对应 Go 的 <c>64&lt;&lt;10</c>。</summary>
    private const long BodyLimit = 64 * 1024;

    public static void MapAdminUserRoutes(this IEndpointRouteBuilder api, CanvasService service)
    {
        api.MapGet("/channels/system", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                IReadOnlyList<PublicModelChannelDto> channels = await service.Channels
                    .PublicSystemChannelsAsync(cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { channels });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/admin/users", async (HttpContext context, CancellationToken cancellationToken) =>
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
                AdminUserPageDto result = await service.AdminUsers.ListAsync(
                    await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false),
                    context.Request.Query["keyword"].ToString(),
                    context.Request.Query["role"].ToString(),
                    context.Request.Query["status"].ToString(),
                    page,
                    pageSize,
                    cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPost("/admin/users", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            CreateAdminUserRequest? request = await ReadJsonAsync<CreateAdminUserRequest>(
                context, BodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                AdminUserDto created = await service.AdminUsers
                    .CreateAsync(actor, request, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { user = created });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/admin/references", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                AdminReferenceDataDto data = await service.AdminUsers
                    .ReferencesAsync(actor, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(data);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/admin/users/bulk-disable", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            BulkDisableUsersRequest? request = await ReadJsonAsync<BulkDisableUsersRequest>(
                context, BodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                BulkDisableUsersResult result = await service.AdminUsers
                    .BulkDisableAsync(actor, request, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/admin/users/{id}/ledger", async (HttpContext context, string id, CancellationToken cancellationToken) =>
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

                WalletSummaryDto result = await service.AdminUsers
                    .LedgerAsync(actor, id, context.Request.Query["type"].ToString(), page, pageSize, cancellationToken)
                    .ConfigureAwait(false);

                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/admin/users/{id}/tasks", async (HttpContext context, string id, CancellationToken cancellationToken) =>
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

                AdminTaskPageDto result = await service.AdminUsers
                    .TasksAsync(actor, id, page, pageSize, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/admin/users/{id}/audit-events", async (HttpContext context, string id, CancellationToken cancellationToken) =>
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

                AdminAuditPageDto result = await service.AdminUsers
                    .AuditEventsAsync(actor, id, page, pageSize, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/admin/users/{id}/detail", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                AdminUserDetailDto result = await service.AdminUsers
                    .DetailAsync(actor, id, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPatch("/admin/users/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            UpdateUserRequest? request = await ReadJsonAsync<UpdateUserRequest>(
                context, BodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                User updated = await service.AdminUsers
                    .UpdateAsync(actor, id, request, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { user = updated });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/admin/users/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                await service.AdminUsers.DeleteAsync(actor, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { ok = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 解析分页参数。对应 Go: <c>parsePaginationQuery</c> + <c>parsePositiveQueryInt</c>。
    /// </summary>
    /// <remarks>非法值返回错误而不是静默回落到默认值——与 Go 一致。</remarks>
    private static bool TryParsePagination(
        HttpContext context,
        int fallbackPageSize,
        out int page,
        out int pageSize,
        out string? error)
    {
        page = 1;
        pageSize = fallbackPageSize;
        error = null;

        if (!TryParsePositive(context.Request.Query["page"].ToString(), 1, out page))
        {
            error = "page: 必须是非负整数";
            return false;
        }

        if (!TryParsePositive(context.Request.Query["pageSize"].ToString(), fallbackPageSize, out pageSize))
        {
            error = "pageSize: 必须是非负整数";
            return false;
        }

        return true;
    }

    private static bool TryParsePositive(string raw, int fallback, out int value)
    {
        value = fallback;
        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        return int.TryParse(trimmed, out value) && value > 0;
    }

    private static async Task<bool> TryRequireAdminAsync(
        HttpContext context,
        CanvasService service,
        CancellationToken cancellationToken)
    {
        try
        {
            User user = await service.CurrentUserAsync(
                SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
            CanvasService.RequireAdmin(user);
            return true;
        }
        catch (Exception error)
        {
            IResult result = ApiResults.FailService(error, context);
            await result.ExecuteAsync(context).ConfigureAwait(false);
            return false;
        }
    }

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
                context.Request.Body, CanvasJson.ReadOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
