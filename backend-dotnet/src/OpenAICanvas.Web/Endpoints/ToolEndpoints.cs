using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 画布工具 API：查询、详情、收藏、新增、删除。
/// 对应 Go: <c>handler/tools.go</c> 的 RegisterToolRoutes。
/// </summary>
public static class ToolEndpoints
{
    public static void MapToolRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        ToolsService tools)
    {
        api.MapGet("/tools", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                (int page, int pageSize, string? error) = ParsePagination(context, 20);
                if (error is not null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, new InvalidOperationException(error));
                }
                ToolListDto result = await tools.ListAsync(user.ID, new ToolListRequest
                {
                    Page = page,
                    PageSize = pageSize,
                    Scope = context.Request.Query["scope"].Count > 0
                        ? context.Request.Query["scope"].ToString()
                        : "public",
                    Type = context.Request.Query["type"].ToString(),
                    Tag = context.Request.Query["tag"].ToString(),
                    Search = context.Request.Query["search"].ToString(),
                }, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/tools/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                if (!long.TryParse(id, out long toolID) || toolID <= 0)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException("工具 ID 无效"));
                }
                ToolItemDto item = await tools.DetailAsync(user.ID, toolID, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(item);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/tools", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ToolMutationRequest? request = await ReadToolJsonAsync<ToolMutationRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.FailService(
                        AppError.BadAuthRequest("请求体格式无效"), context);
                }
                ToolItemDto item = await tools.CreateAsync(user.ID, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(item);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/tools/{id}/favorite", async (HttpContext context, string id, CancellationToken cancellationToken) =>
            await SetFavoriteHandlerAsync(context, tools, service, id, favorite: true, cancellationToken)
                .ConfigureAwait(false));

        api.MapDelete("/tools/{id}/favorite", async (HttpContext context, string id, CancellationToken cancellationToken) =>
            await SetFavoriteHandlerAsync(context, tools, service, id, favorite: false, cancellationToken)
                .ConfigureAwait(false));

        api.MapDelete("/tools/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                if (!long.TryParse(id, out long toolID) || toolID <= 0)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException("工具 ID 无效"));
                }
                await tools.DeleteAsync(user.ID, toolID, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { ok = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    private static async Task<IResult> SetFavoriteHandlerAsync(
        HttpContext context,
        ToolsService tools,
        CanvasService service,
        string id,
        bool favorite,
        CancellationToken cancellationToken)
    {
        try
        {
            User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                .ConfigureAwait(false);
            if (!long.TryParse(id, out long toolID) || toolID <= 0)
            {
                return ApiResults.Fail(
                    StatusCodes.Status400BadRequest,
                    new InvalidOperationException("工具 ID 无效"));
            }
            ToolItemDto item = await tools.SetFavoriteAsync(user.ID, toolID, favorite, cancellationToken)
                .ConfigureAwait(false);
            return ApiResults.Ok(item);
        }
        catch (Exception error)
        {
            return ApiResults.FailService(error, context);
        }
    }

    private static async Task<T?> ReadToolJsonAsync<T>(
        HttpContext context,
        long maxBytes,
        CancellationToken cancellationToken)
        where T : class
    {
        if (maxBytes > 0 && context.Request.ContentLength is > 0 && context.Request.ContentLength > maxBytes)
        {
            return null;
        }
        try
        {
            return await System.Text.Json.JsonSerializer.DeserializeAsync<T>(
                context.Request.Body,
                Domain.Serialization.GoJson.ReadOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            return null;
        }
    }

    /// <summary>对应 Go: <c>parsePaginationQuery</c>。非法值返回错误信息。</summary>
    private static (int Page, int PageSize, string? Error) ParsePagination(
        HttpContext context, int fallbackPageSize)
    {
        int page = ParsePositiveQueryInt(context.Request.Query["page"].ToString(), 1, out string? pageError);
        if (pageError is not null)
        {
            return (0, 0, "page: " + pageError);
        }
        int pageSize = ParsePositiveQueryInt(
            context.Request.Query["pageSize"].ToString(), fallbackPageSize, out string? sizeError);
        if (sizeError is not null)
        {
            return (0, 0, "pageSize: " + sizeError);
        }
        return (page, pageSize, null);
    }

    private static int ParsePositiveQueryInt(string raw, int fallback, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }
        if (int.TryParse(raw, out int parsed) && parsed > 0)
        {
            return parsed;
        }
        error = "must be a positive integer";
        return fallback;
    }
}
