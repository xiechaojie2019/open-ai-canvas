#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>Eagle 素材连接器六条用户路由。对应 Go: <c>handler/plugin.go</c> Eagle 分支。</summary>
public static partial class PluginEndpoints
{
    /// <summary>
    /// 映射 Eagle library/items/file/thumbnail/items POST/folders POST 路由。
    /// Eagle 服务单独注入，保持本机协议与 CanvasService 解耦。
    /// </summary>
    public static void MapEagleRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        EagleService eagle)
    {
        api.MapGet("/plugins/eagle/library", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await RequireEagleAsync(context, service, cancellationToken).ConfigureAwait(false);
                _ = user;
                EagleLibraryDto library = await eagle.LibraryAsync(
                    context.Request.Query["baseUrl"].ToString(), cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { library });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/plugins/eagle/items", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await RequireEagleAsync(context, service, cancellationToken).ConfigureAwait(false);
                _ = user;
                int limit = ParseIntQuery(context, "limit", 60);
                int offset = ParseIntQuery(context, "offset", 0);
                List<EagleItemDto> items = await eagle.ItemsAsync(
                    context.Request.Query["baseUrl"].ToString(),
                    new EagleItemQueryDto
                    {
                        FolderID = context.Request.Query["folderId"].ToString(),
                        Keyword = context.Request.Query["keyword"].ToString(),
                        Limit = limit,
                        Offset = offset,
                    },
                    cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { items });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/plugins/eagle/items/{itemId}/file", async (
            HttpContext context, string itemId, CancellationToken cancellationToken) =>
        {
            try
            {
                await RequireEagleAsync(context, service, cancellationToken).ConfigureAwait(false);
                EagleFileDownload file = await eagle.OpenItemFileAsync(
                    context.Request.Query["baseUrl"].ToString(), itemId, cancellationToken).ConfigureAwait(false);
                context.Response.Headers["Cache-Control"] = "private, no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Disposition"] =
                    "attachment; filename=" + QuoteFileName(file.Name);
                return Results.Stream(file.Body, file.MimeType, enableRangeProcessing: false);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/plugins/eagle/items/{itemId}/thumbnail", async (
            HttpContext context, string itemId, CancellationToken cancellationToken) =>
        {
            try
            {
                await RequireEagleAsync(context, service, cancellationToken).ConfigureAwait(false);
                EagleFileDownload file = await eagle.OpenItemThumbnailAsync(
                    context.Request.Query["baseUrl"].ToString(), itemId, cancellationToken).ConfigureAwait(false);
                context.Response.Headers["Cache-Control"] = "private, max-age=60";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                return Results.Stream(file.Body, file.MimeType, enableRangeProcessing: false);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/plugins/eagle/items", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                await RequireEagleAsync(context, service, cancellationToken).ConfigureAwait(false);
                const long maxBytes = 160L << 20;
                if (context.Request.ContentLength is > maxBytes)
                {
                    throw OpenAICanvas.Domain.Kernel.AppError.BadAuthRequest("Eagle 素材请求超过 160MB 上限");
                }
                EagleAddItemRequestDto? request = await ReadJsonAsync<EagleAddItemRequestDto>(
                    context, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    throw OpenAICanvas.Domain.Kernel.AppError.BadAuthRequest("Eagle 素材数据格式无效");
                }
                EagleCreatedItemDto item = await eagle.AddItemAsync(
                    context.Request.Query["baseUrl"].ToString(), request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { item });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/plugins/eagle/folders", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                await RequireEagleAsync(context, service, cancellationToken).ConfigureAwait(false);
                const long maxBytes = 16L << 10;
                if (context.Request.ContentLength is > maxBytes)
                {
                    throw OpenAICanvas.Domain.Kernel.AppError.BadAuthRequest("Eagle 文件夹请求超过 16KB 上限");
                }
                EagleFolderCreateRequestDto? request = await ReadJsonAsync<EagleFolderCreateRequestDto>(
                    context, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    throw OpenAICanvas.Domain.Kernel.AppError.BadAuthRequest("Eagle 文件夹数据格式无效");
                }
                await eagle.CreateFolderAsync(
                    context.Request.Query["baseUrl"].ToString(), request.Name, request.ParentID, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { created = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    private static async Task<User> RequireEagleAsync(
        HttpContext context, CanvasService service, CancellationToken cancellationToken)
    {
        User user = await service.CurrentUserAsync(
            SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
        await service.PluginManagement.RequirePluginForUserAsync(
            user.ID, "eagle-asset-connector", cancellationToken).ConfigureAwait(false);
        return user;
    }

    private static int ParseIntQuery(HttpContext context, string name, int fallback)
    {
        string raw = context.Request.Query[name].ToString();
        return int.TryParse(raw, out int value) ? value : fallback;
    }

    private static string QuoteFileName(string value) =>
        "\"" + value.Replace("\\", "_", StringComparison.Ordinal).Replace("\"", "_", StringComparison.Ordinal) + "\"";

    private static async Task<T?> ReadJsonAsync<T>(
        HttpContext context, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                context.Request.Body,
                OpenAICanvas.Web.Serialization.CanvasJson.ReadOptions,
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

    private sealed class EagleFolderCreateRequestDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("parentId")] public string? ParentID { get; set; }
    }
}
