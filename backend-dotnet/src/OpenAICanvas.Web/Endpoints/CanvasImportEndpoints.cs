#nullable enable
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Serialization;
using OpenAICanvas.Web.Security;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>LibTV/TapNow 画布导入路由。请求体只承载外部 ID，服务层负责权限与受控出站。</summary>
public static class CanvasImportEndpoints
{
    private const int RequestBodyLimit = 16 << 10;

    public static void MapCanvasImportRoutes(
        this IEndpointRouteBuilder api, CanvasService service, CanvasImportService imports)
    {
        api.MapPost("/canvas-projects/{id}/import/libtv", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                LibTVImportRequestDto? request = await ReadJsonAsync<LibTVImportRequestDto>(
                    context, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                LibTVImportResultDto result = await imports.ImportLibTVAsync(
                    user.ID, id, (request.UUID ?? "").Trim(), cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/canvas-projects/{id}/import/tapnow", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                TapNowImportRequestDto? request = await ReadJsonAsync<TapNowImportRequestDto>(
                    context, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                TapNowImportResultDto result = await imports.ImportTapNowAsync(
                    user.ID, id, (request.ShareID ?? "").Trim(), cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext context, CancellationToken cancellationToken)
        where T : class
    {
        if (context.Request.ContentLength is > RequestBodyLimit)
        {
            return null;
        }

        using MemoryStream body = new();
        byte[] buffer = new byte[4096];
        while (body.Length <= RequestBodyLimit)
        {
            int read = await context.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            body.Write(buffer, 0, read);
            if (body.Length > RequestBodyLimit)
            {
                return null;
            }
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body.ToArray(), CanvasJson.ReadOptions);
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
