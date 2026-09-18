using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 管理端存储管理路由（资源分页 / 批量删除 / 文件下发）。
/// 对应 Go: <c>internal/handler/admin_storage.go</c> 的 <c>RegisterAdminStorageRoutes</c>。
/// </summary>
/// <remarks><c>/admin/storage/stats</c> 已在 UserDataEndpoints 注册，此处不重复。</remarks>
public static class AdminStorageEndpoints
{
    private const long DefaultPageLimit = 20;

    public static void MapAdminStorageRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        AdminStorageService storage)
    {
        // GET /api/admin/resources?keyword=&kind=&status=&provider=&userId=&page=&pageSize=
        api.MapGet("/admin/resources", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                (long page, long limit) = ParsePagination(context);
                AdminResourcePageDto result = await storage.AdminResourcePageAsync(
                    actor,
                    new AdminResourceQuery(
                        context.Request.Query["keyword"].ToString(),
                        context.Request.Query["kind"].ToString(),
                        context.Request.Query["status"].ToString(),
                        context.Request.Query["provider"].ToString(),
                        context.Request.Query["userId"].ToString(),
                        page,
                        limit),
                    DefaultPageLimit,
                    cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (AppError error)
            {
                return ApiResults.FailService(error, context);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // POST /api/admin/resources/delete  { resourceIds: [...] }
        api.MapPost("/admin/resources/delete", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AdminResourceDeleteRequest? request = await ReadJsonAsync<AdminResourceDeleteRequest>(
                    context, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.FailService(
                        AppError.BadAuthRequest("删除资源请求无效"), context);
                }
                AdminResourceDeleteResult result = await storage.DeleteAdminResourcesAsync(
                    actor, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (AppError error)
            {
                return ApiResults.FailService(error, context);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // GET /api/admin/resources/{id}/file?download=1
        api.MapGet("/admin/resources/{id}/file", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ResourceStream stream = await storage.OpenResourceRangeAsAdminAsync(
                    actor, id, context.Request.Headers["Range"].ToString(), cancellationToken).ConfigureAwait(false);

                await using (stream.Body.ConfigureAwait(false))
                {
                    string mimeType = string.IsNullOrEmpty(stream.Resource.MimeType)
                        ? "application/octet-stream"
                        : stream.Resource.MimeType;
                    context.Response.Headers["Cache-Control"] = "private, no-cache";
                    context.Response.Headers["Accept-Ranges"] = stream.AcceptRanges;
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";

                    // 与 Go 一致：管理端用 Range 头直出，不额外做单区间裁剪（ServeContent 语义）。
                    string requestedRange = context.Request.Headers["Range"].ToString();
                    (long Start, long Length, string ContentRange)? resolved =
                        ResourceDomainService.ResolveRange(stream.Resource.Size, requestedRange);
                    if (resolved is null)
                    {
                        context.Response.Headers["Content-Range"] = $"bytes */{stream.Resource.Size}";
                        return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
                    }
                    (long offset, long length, string contentRange) = resolved.Value;
                    if (contentRange.Length > 0)
                    {
                        context.Response.Headers["Content-Range"] = contentRange;
                        context.Response.StatusCode = StatusCodes.Status206PartialContent;
                    }
                    else
                    {
                        context.Response.StatusCode = StatusCodes.Status200OK;
                    }
                    if (context.Request.Query["download"] == "1")
                    {
                        context.Response.Headers["Content-Disposition"] = "attachment";
                    }
                    context.Response.ContentType = mimeType;
                    context.Response.ContentLength = length;

                    await CopyRangeAsync(stream.Body, context.Response.Body,
                        offset, length, cancellationToken).ConfigureAwait(false);
                    return Results.Empty;
                }
            }
            catch (AppError error)
            {
                return ApiResults.FailService(error, context);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    private static (long Page, long Limit) ParsePagination(HttpContext context)
    {
        long page = 1;
        long limit = DefaultPageLimit;
        string rawPage = context.Request.Query["page"].ToString();
        string rawLimit = context.Request.Query["pageSize"].ToString();
        if (rawLimit.Length == 0)
        {
            rawLimit = context.Request.Query["limit"].ToString();
        }
        if (rawPage.Length > 0 && long.TryParse(rawPage, out long parsedPage))
        {
            page = parsedPage;
        }
        if (rawLimit.Length > 0 && long.TryParse(rawLimit, out long parsedLimit))
        {
            limit = parsedLimit;
        }
        return (page, limit);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext context, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await System.Text.Json.JsonSerializer.DeserializeAsync<T>(
                context.Request.Body,
                OpenAICanvas.Web.Serialization.CanvasJson.ReadOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (BadHttpRequestException)
        {
            return null;
        }
    }

    private static async Task CopyRangeAsync(
        Stream source, Stream destination, long offset, long count, CancellationToken cancellationToken)
    {
        if (offset > 0)
        {
            if (source.CanSeek)
            {
                source.Seek(offset, SeekOrigin.Begin);
            }
            else
            {
                byte[] skip = new byte[64 << 10];
                long remaining = offset;
                while (remaining > 0)
                {
                    int want = (int)Math.Min(skip.Length, remaining);
                    int read = await source.ReadAsync(skip.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        return;
                    }
                    remaining -= read;
                }
            }
        }
        byte[] buffer = new byte[64 << 10];
        long left = count;
        while (left > 0)
        {
            int want = (int)Math.Min(buffer.Length, left);
            int read = await source.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            left -= read;
        }
    }
}
