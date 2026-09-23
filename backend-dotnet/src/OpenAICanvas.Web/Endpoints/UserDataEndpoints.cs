using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Application;
using OpenAICanvas.Application.Prompts;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Platform;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 画布工程 CRUD 路由。对应 Go: <c>internal/handler/user_data.go</c> 的 canvas-projects 部分。
/// </summary>
public static class UserDataEndpoints
{
    /// <summary>画布保存请求体上限 5MB。对应 Go: <c>5&lt;&lt;20</c>。</summary>
    private const long CanvasBodyLimit = 5 << 20;

    public static void MapUserDataRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        IRateLimiter limiter,
        IRuntimePolicyProvider policyProvider)
    {
        // ------------------------------------------------------------ 项目（阶段 6 节点 A）

        api.MapGet("/projects", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                string pageParam = context.Request.Query["page"].ToString();
                string pageSizeParam = context.Request.Query["pageSize"].ToString();
                if (string.IsNullOrEmpty(pageParam) && string.IsNullOrEmpty(pageSizeParam))
                {
                    IReadOnlyList<ProjectSummaryDto> projectSummaries = await service.ListProjectsAsync(
                        user.ID, cancellationToken).ConfigureAwait(false);
                    return ApiResults.Ok(new { projects = projectSummaries });
                }
                int page = ParsePositiveQueryInt(pageParam, 1, out string? pageError);
                if (pageError is not null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, new InvalidOperationException("page: " + pageError));
                }
                int pageSize = ParsePositiveQueryInt(pageSizeParam, 50, out string? sizeError);
                if (sizeError is not null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, new InvalidOperationException("pageSize: " + sizeError));
                }
                ProjectListPageDto pageResult = await service.ListProjectsPageAsync(
                    user.ID, page, pageSize, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(pageResult);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/projects", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CreateProjectRequest? request = await ReadJsonAsync<CreateProjectRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Project project = await service.CreateProjectAsync(
                    user.ID, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { project });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/projects/{id}/core", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ProjectCoreDto core = await service.ProjectWorkbench.CoreAsync(
                    user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(core);
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/projects/{id}/overview", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ProjectOverviewDto overview = await service.ProjectWorkbench.OverviewAsync(
                    user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(overview);
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/projects/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                UpdateProjectRequest? request = await ReadJsonAsync<UpdateProjectRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Project project = await service.UpdateProjectAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { project });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/projects/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.DeleteProjectAsync(user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { id });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/assets/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.DeleteUserAssetAsync(user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { id });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/canvas-projects", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(context.Request.Query["page"]))
                {
                    (int page, int pageSize, string? error) = ParsePagination(context, 40);
                    if (error is not null)
                    {
                        return ApiResults.Fail(StatusCodes.Status400BadRequest, new InvalidOperationException(error));
                    }
                    CanvasLibraryPageDto result = await service.UserCanvasProjectsPageAsync(
                        user.ID,
                        page,
                        pageSize,
                        context.Request.Query["projectId"].ToString(),
                        context.Request.Query["q"].ToString(),
                        context.Request.Query["sort"].ToString(),
                        cancellationToken).ConfigureAwait(false);
                    return ApiResults.Ok(result);
                }
                List<UserDataSummaryDto> projects = await service.UserCanvasProjectSummariesAsync(
                    user.ID, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { projects });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/canvas-projects/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                JsonElement project = await service.UserCanvasProjectAsync(user.ID, id, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { project });
            }
            catch (Exception error)
            {
                // Go: fail(c, 404, err)——不存在时 HTTP 404 + 原始错误文案。
                if (error is InvalidOperationException)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status404NotFound,
                        new InvalidOperationException("record not found"));
                }
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPut("/canvas-projects/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                RuntimeRequestPolicy policy = policyProvider.Current().Request;
                if (!await AuthEndpoints.EnforceRateLimitAsync(
                        context, limiter, "canvas-write:" + user.ID,
                        policy.CanvasWritePerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }

                JsonElement? request = await ReadJsonAsync(context, CanvasBodyLimit, cancellationToken)
                    .ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                // project 字段里的 id 必须与路径一致。
                if (!request.Value.TryGetProperty("project", out JsonElement projectElement) ||
                    !projectElement.TryGetProperty("id", out JsonElement idElement) ||
                    idElement.GetString() != id)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        AppError.BadAuthRequest("画布 ID 与请求路径不一致"));
                }

                UserDataSummaryDto project = await service.UpsertUserCanvasProjectAsync(
                    user.ID, projectElement, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { project });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // ------------------------------------------------------------ 素材库（节点 B）

        api.MapPost("/assets/batch", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                List<string>? ids = await ReadStringListAsync(context, "ids", maxBytes: 16 << 10, cancellationToken)
                    .ConfigureAwait(false);
                if (ids is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                List<JsonElement> assets = await service.UserAssetsByIDsAsync(
                    user.ID, ids, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { assets });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/assets", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                bool paged = !string.IsNullOrEmpty(context.Request.Query["page"]) ||
                             HasUserAssetPageFilters(context);
                if (paged)
                {
                    (int page, int pageSize, string? pageError) = ParsePagination(context, 40);
                    if (pageError is not null)
                    {
                        return ApiResults.Fail(
                            StatusCodes.Status400BadRequest,
                            new InvalidOperationException(pageError));
                    }
                    string? folderId = context.Request.Query.ContainsKey("folderId")
                        ? context.Request.Query["folderId"].ToString()
                        : null;
                    UserAssetPageDto assets = await service.UserAssetsPageAsync(
                        user.ID,
                        page,
                        pageSize,
                        context.Request.Query["kind"].ToString(),
                        context.Request.Query["category"].ToString(),
                        folderId,
                        context.Request.Query["uncategorized"].ToString() == "1",
                        context.Request.Query["status"].ToString(),
                        context.Request.Query["q"].ToString(),
                        cancellationToken).ConfigureAwait(false);
                    return ApiResults.Ok(assets);
                }
                List<UserDataSummaryDto> summaries = await service.UserAssetSummariesAsync(
                    user.ID, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { assets = summaries });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/asset-folders", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyList<AssetFolder> folders = await service.AssetFoldersAsync(
                    user.ID, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { folders });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/asset-folders", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                JsonElement? request = await ReadJsonAsync(context, 0, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                string name = request.Value.TryGetProperty("name", out JsonElement nameElement) &&
                              nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? ""
                    : "";
                AssetFolder folder = await service.CreateAssetFolderAsync(
                    user.ID, name, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { folder });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/asset-folders/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                JsonElement? request = await ReadJsonAsync(context, 0, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                string name = request.Value.TryGetProperty("name", out JsonElement nameElement) &&
                              nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? ""
                    : "";
                AssetFolder folder = await service.UpdateAssetFolderAsync(
                    user.ID, id, name, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { folder });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/asset-folders/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.DeleteAssetFolderAsync(user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { id });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/assets/folder", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                JsonElement? request = await ReadJsonAsync(context, 0, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                List<string> assetIds = [];
                if (request.Value.TryGetProperty("assetIds", out JsonElement idsElement) &&
                    idsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in idsElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            assetIds.Add(item.GetString() ?? "");
                        }
                    }
                }
                string folderId = request.Value.TryGetProperty("folderId", out JsonElement folderElement) &&
                                  folderElement.ValueKind == JsonValueKind.String
                    ? folderElement.GetString() ?? ""
                    : "";
                (IReadOnlyList<string> movedIds, string movedFolderId) = await service
                    .MoveUserAssetsToFolderAsync(user.ID, assetIds, folderId, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { assetIds = movedIds, folderId = movedFolderId });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/user-data/snapshot", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                (List<JsonElement> assets, List<JsonElement> projects) = await service.UserDataSnapshotAsync(
                    user.ID, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { assets, projects });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/assets/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                JsonElement asset = await service.UserAssetAsync(user.ID, id, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { asset });
            }
            catch (Exception error)
            {
                if (error is InvalidOperationException)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status404NotFound,
                        new InvalidOperationException("record not found"));
                }
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPut("/assets/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                RuntimeRequestPolicy policy = policyProvider.Current().Request;
                if (!await AuthEndpoints.EnforceRateLimitAsync(
                        context, limiter, "assets-write:" + user.ID,
                        policy.AssetWritePerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }

                JsonElement? request = await ReadJsonAsync(context, CanvasBodyLimit, cancellationToken)
                    .ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                if (!request.Value.TryGetProperty("asset", out JsonElement assetElement) ||
                    !assetElement.TryGetProperty("id", out JsonElement idElement) ||
                    idElement.GetString() != id)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        AppError.BadAuthRequest("素材 ID 与请求路径不一致"));
                }
                UserDataSummaryDto asset = await service.UpsertUserAssetAsync(
                    user.ID, assetElement, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { asset });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/canvas-projects/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.DeleteUserCanvasProjectAsync(user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { id });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 画布分享路由。对应 Go: <c>handler.RegisterCanvasShareRoutes</c>。
    /// </summary>
    public static void MapCanvasShareRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        IRateLimiter limiter,
        string dataDir)
    {
        api.MapGet("/canvas-projects/{id}/share", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CanvasShareStatusDto share = await service.CanvasShareStatusAsync(
                    user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { share });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("画布不存在"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/canvas-projects/{id}/share", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                JsonElement? request = await ReadJsonAsync(context, maxBytes: 8 << 10, cancellationToken)
                    .ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                int expiresDays = request.Value.TryGetProperty("expiresDays", out JsonElement days) &&
                                  days.ValueKind == JsonValueKind.Number
                    ? days.GetInt32()
                    : 0;
                bool rotate = request.Value.TryGetProperty("rotate", out JsonElement rotateElement) &&
                              rotateElement.ValueKind == JsonValueKind.True;
                CanvasShareStatusDto share = await service.CreateCanvasShareAsync(
                    user.ID, id, expiresDays, rotate, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { share });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/canvas-projects/{id}/share", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.DeleteCanvasShareAsync(user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { id });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/public/canvas-shares/{token}", async (HttpContext context, string token, CancellationToken cancellationToken) =>
        {
            if (!await AuthEndpoints.EnforceRateLimitAsync(
                    context, limiter, "public-canvas:" + AuthEndpoints.ClientIp(context),
                    120, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
            {
                return Results.Empty;
            }
            try
            {
                PublicCanvasShareDto share = await service.PublicCanvasShareAsync(token, cancellationToken)
                    .ConfigureAwait(false);
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                return ApiResults.Ok(share);
            }
            catch
            {
                return ApiResults.Fail(
                    StatusCodes.Status404NotFound,
                    new InvalidOperationException("分享链接无效或已失效"));
            }
        });

        api.MapGet("/public/canvas-shares/{token}/resources/{resourceId}/file", async (
            HttpContext context,
            string token,
            string resourceId,
            CancellationToken cancellationToken) =>
        {
            if (!await AuthEndpoints.EnforceRateLimitAsync(
                    context, limiter, "public-canvas-resource:" + AuthEndpoints.ClientIp(context),
                    300, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
            {
                return Results.Empty;
            }
            try
            {
                (Domain.Entities.Resource resource, string path) = await service
                    .PrepareSharedCanvasResourceDeliveryAsync(token, resourceId, dataDir, cancellationToken)
                    .ConfigureAwait(false);

                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.Headers["Content-Security-Policy"] = "sandbox";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                context.Response.Headers["Accept-Ranges"] = "bytes";
                context.Response.ContentType = string.IsNullOrEmpty(resource.MimeType)
                    ? "application/octet-stream"
                    : resource.MimeType;
                await context.Response.SendFileAsync(path, cancellationToken).ConfigureAwait(false);
                return Results.Empty;
            }
            catch
            {
                return ApiResults.Fail(
                    StatusCodes.Status404NotFound,
                    new InvalidOperationException("分享资源不存在"));
            }
        });
    }

    /// <summary>
    /// 平台设置管理路由。对应 Go: <c>handler/finance.go</c> / <c>auth.go</c> 的设置部分。
    /// </summary>
    public static void MapAdminSettingsRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        api.MapGet("/admin/settings/registration", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                Auth.PublicRegistrationSetting setting = await service.AdminRegistrationSettingAsync(
                    actor, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/admin/settings/registration", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                Auth.RegistrationSettingRequest? request = await ReadJsonAsync<Auth.RegistrationSettingRequest>(
                    context, 16 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Auth.PublicRegistrationSetting setting = await service.UpdateRegistrationSettingAsync(
                    actor, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/admin/settings/email", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                Auth.PublicEmailSetting setting = await service.AdminEmailSettingAsync(
                    actor, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/admin/settings/email", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                Auth.EmailSettingRequest? request = await ReadJsonAsync<Auth.EmailSettingRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Auth.PublicEmailSetting setting = await service.UpdateEmailSettingAsync(
                    actor, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/admin/settings/linuxdo", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                Auth.PublicLinuxDOSetting setting = await service.AdminLinuxDOSettingAsync(
                    actor, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/admin/settings/linuxdo", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                Auth.LinuxDOSettingRequest? request = await ReadJsonAsync<Auth.LinuxDOSettingRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Auth.PublicLinuxDOSetting setting = await service.UpdateLinuxDOSettingAsync(
                    actor, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/admin/settings/credits", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CreditPolicy policy = await service.AdminCreditPolicyAsync(actor, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { policy });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/admin/settings/credits", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CreditPolicy? policy = await ReadJsonAsync<CreditPolicy>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (policy is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                CreditPolicy updated = await service.UpdateCreditPolicyAsync(
                    actor, policy, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { policy = updated });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 公告路由。对应 Go: <c>handler/announcement.go</c>（配图上传待资源节点）。
    /// </summary>
    public static void MapAnnouncementRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        string dataDir)
    {
        api.MapGet("/announcements", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                UserAnnouncementFeedDto feed = await service.UserAnnouncementsAsync(user, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(feed);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/announcements/read", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                JsonElement? request = await ReadJsonAsync(context, 64 << 10, cancellationToken)
                    .ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                List<string> ids = [];
                if (request.Value.TryGetProperty("announcementIds", out JsonElement idsElement) &&
                    idsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in idsElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            ids.Add(item.GetString() ?? "");
                        }
                    }
                }
                long unreadCount = await service.MarkAnnouncementsReadAsync(user, ids, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { unreadCount });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/announcements/{id}/image", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                Domain.Entities.Resource resource = await service.OpenAnnouncementImageAsync(
                    user, id, cancellationToken).ConfigureAwait(false);

                context.Response.Headers["Cache-Control"] = "private, max-age=3600";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["Accept-Ranges"] = "bytes";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.ContentType = string.IsNullOrEmpty(resource.MimeType)
                    ? "application/octet-stream"
                    : resource.MimeType;

                if (resource.Provider.Trim() is "" or "local")
                {
                    string relativeKey = resource.ObjectKey.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                    string root = Path.GetFullPath(Path.Combine(dataDir, "resources"));
                    string path = Path.GetFullPath(Path.Combine(root, relativeKey));
                    if (path.StartsWith(root, StringComparison.Ordinal) && File.Exists(path))
                    {
                        await context.Response.SendFileAsync(path, cancellationToken).ConfigureAwait(false);
                        return Results.Empty;
                    }
                }
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("公告配图不存在"));
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

        api.MapGet("/admin/announcements", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                (int page, int pageSize, string? error) = ParsePagination(context, 20);
                if (error is not null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, new InvalidOperationException(error));
                }
                AnnouncementPageDto result = await service.AdminAnnouncementPageAsync(
                    actor,
                    context.Request.Query["keyword"].ToString(),
                    context.Request.Query["status"].ToString(),
                    page,
                    pageSize,
                    cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/admin/announcements", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AnnouncementRequest? request = await ReadJsonAsync<AnnouncementRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Domain.Entities.Announcement announcement = await service.CreateAnnouncementAsync(
                    actor, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { announcement });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/admin/announcements/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AnnouncementRequest? request = await ReadJsonAsync<AnnouncementRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Domain.Entities.Announcement announcement = await service.UpdateAnnouncementAsync(
                    actor, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { announcement });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/admin/announcements/{id}/close", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                Domain.Entities.Announcement announcement = await service.CloseAnnouncementAsync(
                    actor, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { announcement });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/admin/announcement-images/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.DiscardAnnouncementImageAsync(actor, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { ok = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 管理端分析总览与模型价格路由。对应 Go: <c>handler/admin_analytics.go</c>。
    /// </summary>
    public static void MapAdminInsightRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        api.MapGet("/admin/analytics/overview", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AnalyticsOverviewDto result = await service.AdminAnalytics.OverviewAsync(
                    actor, AnalyticsQuery(context), cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/admin/analytics/models", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AnalyticsOverviewDto result = await service.AdminAnalytics.OverviewAsync(
                    actor, AnalyticsQuery(context), cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { models = result.Models });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/admin/analytics/users", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AnalyticsOverviewDto result = await service.AdminAnalytics.OverviewAsync(
                    actor, AnalyticsQuery(context), cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { users = result.Users, dau = result.KPI.DAU, wau = result.KPI.WAU, mau = result.KPI.MAU });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/admin/analytics/export.csv", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                byte[] data = await service.AdminAnalytics.AnalyticsCsvAsync(
                    actor, AnalyticsQuery(context), cancellationToken).ConfigureAwait(false);
                context.Response.Headers["Content-Disposition"] =
                    "attachment; filename=usage-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".csv";
                return Results.Text(Encoding.UTF8.GetString(data), "text/csv; charset=utf-8");
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/admin/model-pricings", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyList<ModelPricing> items = await service.AdminAnalytics.ModelPricingsAsync(
                    actor, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { pricings = items });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/admin/model-pricings", async (
            HttpContext context, CancellationToken cancellationToken) =>
            await SaveModelPricing(context, service, "", cancellationToken));

        api.MapPatch("/admin/model-pricings/{id}", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
            await SaveModelPricing(context, service, id, cancellationToken));

        api.MapDelete("/admin/model-pricings/{id}", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.AdminAnalytics.DeleteModelPricingAsync(
                    actor, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { ok = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>分析查询参数。对应 Go: <c>analyticsQuery(c)</c>。</summary>
    private static AnalyticsQueryDto AnalyticsQuery(HttpContext context) => new()
    {
        From = context.Request.Query["from"].ToString(),
        To = context.Request.Query["to"].ToString(),
        UserID = context.Request.Query["userId"].ToString(),
        Model = context.Request.Query["model"].ToString(),
        ChannelID = context.Request.Query["channelId"].ToString(),
        Capability = context.Request.Query["capability"].ToString(),
    };

    /// <summary>价格写路径公共体。对应 Go: <c>saveModelPricing</c>。</summary>
    private static async Task<IResult> SaveModelPricing(
        HttpContext context, CanvasService service, string id, CancellationToken cancellationToken)
    {
        try
        {
            User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                .ConfigureAwait(false);
            ModelPricingRequestDto? request = await ReadJsonAsync<ModelPricingRequestDto>(
                context, 0, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }
            ModelPricing pricing = await service.AdminAnalytics.SaveModelPricingAsync(
                actor, id, request, cancellationToken).ConfigureAwait(false);
            return ApiResults.Ok(new { pricing });
        }
        catch (Exception error)
        {
            return ApiResults.FailService(error, context);
        }
    }

    /// <summary>
    /// 用户诊断包路由。对应 Go: <c>handler/diagnostics.go</c>。
    /// </summary>
    public static void MapDiagnosticsRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        api.MapPost("/diagnostics/preview", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                DiagnosticExportRequestDto? request = await ReadJsonAsync<DiagnosticExportRequestDto>(
                    context, 4 << 20, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                DiagnosticPreviewDto preview = await service.Diagnostics.PreviewAsync(
                    user.ID, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(preview);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/diagnostics/export", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                DiagnosticExportRequestDto? request = await ReadJsonAsync<DiagnosticExportRequestDto>(
                    context, 4 << 20, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                DiagnosticBundleDto bundle = await service.Diagnostics.ExportAsync(
                    user.ID, request, cancellationToken).ConfigureAwait(false);
                context.Response.Headers["Cache-Control"] = "private, no-store";
                context.Response.Headers["Content-Disposition"] =
                    "attachment; filename=\"" + bundle.FileName + "\"";
                context.Response.Headers["X-Diagnostic-Bundle-ID"] = bundle.BundleID;
                context.Response.Headers["X-Diagnostic-Schema-Version"] = "1";
                context.Response.ContentType = "application/zip";
                await context.Response.Body.WriteAsync(bundle.Data, cancellationToken).ConfigureAwait(false);
                return Results.Empty;
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 系统性能与缓存清理路由。对应 Go: <c>handler/admin_system_performance.go</c>。
    /// </summary>
    public static void MapSystemPerformanceRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        OpenAICanvas.Web.Security.InMemoryRateLimiter limiter)
    {
        api.MapGet("/admin/system-performance", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AdminSystemPerformanceDto result = await service.SystemPerformance.PerformanceAsync(
                    actor, cancellationToken: cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/admin/system-performance/cache/clear", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AdminCacheClearRequestDto? request = await ReadJsonAsync<AdminCacheClearRequestDto>(
                    context, 4 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest,
                        new InvalidOperationException("缓存清理请求无效"));
                }
                int cleared = limiter.ClearWindows();
                bool catalogCleared = service.TryInvalidateRouteCatalog();
                AdminCacheClearResultDto result = service.SystemPerformance.ClearRuntimeCache(
                    actor, request.Scope, cleared, catalogCleared);
                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 管理端日志媒体路由。对应 Go: <c>GET /admin/api-logs/:id/media</c>。
    /// </summary>
    public static void MapAdminLogMediaRoute(
        this IEndpointRouteBuilder api,
        CanvasService service,
        ResourceDomainService resources)
    {
        api.MapGet("/admin/api-logs/{id}/media", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ResourceDelivery delivery = await service.AdminLogMedia.PrepareMediaDeliveryAsync(
                    actor, id, cancellationToken).ConfigureAwait(false);
                if (delivery.RedirectURL.Length > 0)
                {
                    return Results.Redirect(delivery.RedirectURL);
                }
                context.Response.Headers["Accept-Ranges"] = delivery.AcceptRanges;
                context.Response.ContentType = delivery.Resource.MimeType;
                await using (System.IO.Stream body = delivery.Stream!)
                {
                    await body.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
                }
                return Results.Empty;
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 管理后台日志与存储路由。对应 Go: <c>handler/auth.go</c> 日志部分与
    /// <c>handler/admin_storage.go</c> 统计部分。
    /// </summary>
    public static void MapAdminAnalyticsRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        api.MapGet("/admin/api-logs", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                (int page, int pageSize, string? error) = ParsePagination(context, 50);
                if (error is not null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, new InvalidOperationException(error));
                }
                OpenAICanvas.Persistence.Repositories.AnalyticsFilter analytics =
                    AdminAnalyticsService.NormalizeFilter(
                        context.Request.Query["from"].ToString(),
                        context.Request.Query["to"].ToString(),
                        context.Request.Query["userId"].ToString(),
                        context.Request.Query["model"].ToString(),
                        context.Request.Query["channelId"].ToString(),
                        context.Request.Query["capability"].ToString());
                ApiCallLogPageDto logs = await service.AdminApiCallLogsAsync(
                    actor,
                    new OpenAICanvas.Persistence.Repositories.ApiCallLogFilter(
                        analytics,
                        context.Request.Query["recordType"].ToString(),
                        context.Request.Query["keyword"].ToString(),
                        context.Request.Query["status"].ToString(),
                        page,
                        pageSize),
                    cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(logs);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/admin/api-logs/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                Domain.Entities.ApiCallLog log = await service.AdminApiCallLogAsync(actor, id, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { log });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/admin/api-logs-export.csv", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                OpenAICanvas.Persistence.Repositories.AnalyticsFilter analytics =
                    AdminAnalyticsService.NormalizeFilter(
                        context.Request.Query["from"].ToString(),
                        context.Request.Query["to"].ToString(),
                        context.Request.Query["userId"].ToString(),
                        context.Request.Query["model"].ToString(),
                        context.Request.Query["channelId"].ToString(),
                        context.Request.Query["capability"].ToString());
                string csv = await service.AdminApiCallLogsExportCsvAsync(
                    actor,
                    new OpenAICanvas.Persistence.Repositories.ApiCallLogFilter(
                        analytics,
                        context.Request.Query["recordType"].ToString(),
                        context.Request.Query["keyword"].ToString(),
                        context.Request.Query["status"].ToString(),
                        1,
                        5000),
                    cancellationToken).ConfigureAwait(false);
                return Results.Text(csv, "text/csv; charset=utf-8", System.Text.Encoding.UTF8);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/admin/storage/stats", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AdminStorageStatsDto stats = await service.AdminStorageStatsAsync(actor, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { stats });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 项目单元与画布链接路由。对应 Go: <c>handler/project.go</c> 的单元/链接部分。
    /// </summary>
    public static void MapProjectUnitRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        api.MapPost("/projects/{id}/units", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CreateProjectUnitRequest? request = await ReadJsonAsync<CreateProjectUnitRequest>(
                    context, 2 << 20, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Domain.Entities.ProjectUnit unit = await service.CreateProjectUnitAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { unit });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/projects/{id}/units", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ProjectUnitSummariesDto units = await service.ProjectUnitSummariesAsync(
                    user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(units);
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/projects/{id}/units/{unitId}", async (
            HttpContext context, string id, string unitId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                Domain.Entities.ProjectUnit unit = await service.GetProjectUnitAsync(
                    user.ID, id, unitId, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { unit });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/projects/{id}/units/import", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ImportProjectUnitsRequest? request = await ReadJsonAsync<ImportProjectUnitsRequest>(
                    context, 32 << 20, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                IReadOnlyList<Domain.Entities.ProjectUnit> units = await service.ImportProjectUnitsAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                // 导入响应只返回标识与摘要，正文不回传。
                foreach (Domain.Entities.ProjectUnit unit in units)
                {
                    unit.SourceText = "";
                }
                return ApiResults.Ok(new { units });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/projects/{id}/units/reorder", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ReorderProjectUnitsRequest? request = await ReadJsonAsync<ReorderProjectUnitsRequest>(
                    context, 256 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                await service.ReorderProjectUnitsAsync(user.ID, id, request, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { unitIds = request.UnitIDs ?? [] });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/projects/{id}/units/{unitId}", async (
            HttpContext context, string id, string unitId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                UpdateProjectUnitRequest? request = await ReadJsonAsync<UpdateProjectUnitRequest>(
                    context, 2 << 20, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Domain.Entities.ProjectUnit unit = await service.UpdateProjectUnitAsync(
                    user.ID, id, unitId, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { unit });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/projects/{id}/units/{unitId}", async (
            HttpContext context, string id, string unitId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.DeleteProjectUnitAsync(user.ID, id, unitId, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { id = unitId });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/projects/{id}/canvas-links", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                LinkCanvasUnitRequest? request = await ReadJsonAsync<LinkCanvasUnitRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Domain.Entities.CanvasUnitLink link = await service.LinkCanvasUnitAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { link });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/projects/{id}/canvas-links/{canvasId}/units/{unitId}", async (
            HttpContext context, string id, string canvasId, string unitId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.UnlinkCanvasUnitAsync(user.ID, id, canvasId, unitId, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { ok = true });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/projects/{id}/canvases/{canvasId}", async (
            HttpContext context, string id, string canvasId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.UnlinkCanvasProjectAsync(user.ID, id, canvasId, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { canvasId });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 项目素材文件夹路由。对应 Go: <c>handler/project.go</c> 的 asset-folders 部分。
    /// </summary>
    public static void MapProjectAssetFolderRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        api.MapGet("/projects/{id}/asset-folders", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyList<Domain.Entities.ProjectAssetFolder> folders = await service
                    .ProjectAssetFoldersAsync(user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { folders });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/projects/{id}/asset-folders", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CreateProjectAssetFolderRequest? request = await ReadJsonAsync<CreateProjectAssetFolderRequest>(
                    context, 32 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Domain.Entities.ProjectAssetFolder folder = await service.CreateProjectAssetFolderAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { folder });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/projects/{id}/asset-folders/{folderId}", async (
            HttpContext context, string id, string folderId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                UpdateProjectAssetFolderRequest? request = await ReadJsonAsync<UpdateProjectAssetFolderRequest>(
                    context, 32 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Domain.Entities.ProjectAssetFolder folder = await service.UpdateProjectAssetFolderAsync(
                    user.ID, id, folderId, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { folder });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/projects/{id}/asset-folders/{folderId}", async (
            HttpContext context, string id, string folderId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.DeleteProjectAssetFolderAsync(user.ID, id, folderId, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { id = folderId });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 风格档案与声音档案路由。对应 Go: <c>handler/style_profile.go</c> 与
    /// <c>handler/project.go</c> 的 voice-profiles。
    /// </summary>
    public static void MapStyleProfileRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        api.MapGet("/style-profiles", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyList<Domain.Entities.StyleProfile> profiles = await service
                    .ListStyleProfilesAsync(user.ID, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { profiles });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/style-profiles", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                StyleProfileRequest? request = await ReadJsonAsync<StyleProfileRequest>(
                    context, 320 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Domain.Entities.StyleProfile profile = await service.CreateStyleProfileAsync(
                    user.ID, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { profile });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/style-profiles/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                StyleProfileRequest? request = await ReadJsonAsync<StyleProfileRequest>(
                    context, 320 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Domain.Entities.StyleProfile profile = await service.UpdateStyleProfileAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { profile });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/style-profiles/{id}/favorite", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                StyleProfileFavoriteRequest? request = await ReadJsonAsync<StyleProfileFavoriteRequest>(
                    context, 16 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                await service.SetStyleProfileFavoriteAsync(user.ID, id, request.Favorite, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { id, favorite = request.Favorite });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/style-profiles/{id}/use", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.TouchStyleProfileAsync(user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { id });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/style-profiles/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.DeleteStyleProfileAsync(user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { id });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.Fail(StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/voice-profiles", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                List<VoiceProfileSummaryDto> profiles = await service.ProjectCharacters
                    .ListVoiceProfilesAsync(user.ID, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { profiles });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 项目素材关联路由。对应 Go: <c>handler/project.go</c> 的 assets 部分与版本创建。
    /// </summary>
    public static void MapProjectAssetLinkRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        api.MapGet("/projects/{id}/assets", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ProjectAssetFilter filter = new()
                {
                    Category = context.Request.Query["category"].ToString(),
                    MediaType = context.Request.Query["mediaType"].ToString(),
                    Status = context.Request.Query["status"].ToString(),
                    FolderId = context.Request.Query.ContainsKey("folderId")
                        ? context.Request.Query["folderId"].ToString()
                        : "",
                    Query = context.Request.Query["q"].ToString(),
                };
                List<ProjectAssetSummaryDto> assets = await service.ProjectAssetsAsync(
                    user.ID, id, filter, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { assets });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/projects/{id}/assets", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                LinkProjectAssetRequest? request = await ReadJsonAsync<LinkProjectAssetRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                ProjectAssetSummaryDto asset = await service.LinkProjectAssetAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { asset });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/projects/{id}/assets/{assetId}", async (
            HttpContext context, string id, string assetId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.UnlinkProjectAssetAsync(user.ID, id, assetId, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { id = assetId });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/projects/{id}/assets/{assetId}", async (
            HttpContext context, string id, string assetId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                UpdateProjectAssetRequest? request = await ReadJsonAsync<UpdateProjectAssetRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                ProjectAssetSummaryDto asset = await service.UpdateProjectAssetAsync(
                    user.ID, id, assetId, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { asset });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/projects/{id}/assets/{assetId}/versions", async (
            HttpContext context, string id, string assetId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CreateAssetVersionRequest? request = await ReadJsonAsync<CreateAssetVersionRequest>(
                    context, 256 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                AssetVersion version = await service.CreateProjectAssetVersionAsync(
                    user.ID, id, assetId, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { version });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 项目角色与配音路由。对应 Go: <c>handler/project.go</c> 的 characters 部分。
    /// </summary>
    /// <remarks>
    /// 与 Go 一致：该组路由不识别 not-found（<c>IsProjectNotFound</c>），
    /// 资产/项目缺失走 failInternal 的 500 信封。
    /// </remarks>
    public static void MapProjectCharacterRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        api.MapPost("/projects/{id}/characters", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CreateProjectCharacterRequest? request = await ReadJsonAsync<CreateProjectCharacterRequest>(
                    context, 256 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                ProjectCharacterDetailDto character = await service.ProjectCharacters.CreateAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(character);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/projects/{id}/characters/{assetId}", async (
            HttpContext context, string id, string assetId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ProjectCharacterDetailDto character = await service.ProjectCharacters.GetAsync(
                    user.ID, id, assetId, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(character);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/projects/{id}/characters/{assetId}", async (
            HttpContext context, string id, string assetId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                UpdateProjectCharacterRequest? request = await ReadJsonAsync<UpdateProjectCharacterRequest>(
                    context, 256 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                ProjectCharacterDetailDto character = await service.ProjectCharacters.UpdateAsync(
                    user.ID, id, assetId, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(character);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPut("/projects/{id}/characters/{assetId}/representations", async (
            HttpContext context, string id, string assetId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ReplaceCharacterRepresentationsRequest? request =
                    await ReadJsonAsync<ReplaceCharacterRepresentationsRequest>(
                        context, 128 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                ProjectCharacterDetailDto character = await service.ProjectCharacters.ReplaceRepresentationsAsync(
                    user.ID, id, assetId, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(character);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPut("/projects/{id}/characters/{assetId}/voice", async (
            HttpContext context, string id, string assetId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                BindCharacterVoiceRequest? request = await ReadJsonAsync<BindCharacterVoiceRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                ProjectCharacterDetailDto character = await service.ProjectCharacters.BindVoiceAsync(
                    user.ID, id, assetId, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(character);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/projects/{id}/characters/{assetId}/voice", async (
            HttpContext context, string id, string assetId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ProjectCharacterDetailDto character = await service.ProjectCharacters.UnbindVoiceAsync(
                    user.ID, id, assetId, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(character);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 分镜与资产候选路由。对应 Go: <c>handler/project.go</c> 的 shots/asset-candidates 部分。
    /// </summary>
    public static void MapProjectShotRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        api.MapPost("/projects/{id}/shots", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CreateProjectShotRequest? request = await ReadJsonAsync<CreateProjectShotRequest>(
                    context, 256 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Shot shot = await service.ProjectShots.CreateAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { shot });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPut("/projects/{id}/units/{unitId}/shots", async (
            HttpContext context, string id, string unitId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ReplaceProjectUnitShotsRequest? request = await ReadJsonAsync<ReplaceProjectUnitShotsRequest>(
                    context, 2 << 20, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                List<Shot> shots = await service.ProjectShots.ReplaceUnitShotsAsync(
                    user.ID, id, unitId, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { shots });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/projects/{id}/shots/{shotId}/revisions", async (
            HttpContext context, string id, string shotId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ShotRevisionInput? request = await ReadJsonAsync<ShotRevisionInput>(
                    context, 256 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                (Shot shot, ShotRevision revision) = await service.ProjectShots.CreateRevisionAsync(
                    user.ID, id, shotId, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { shot, revision });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/projects/{id}/shots/{shotId}", async (
            HttpContext context, string id, string shotId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.ProjectShots.DeleteAsync(user.ID, id, shotId, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { deleted = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/projects/{id}/shots/{shotId}/assets", async (
            HttpContext context, string id, string shotId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                LinkShotAssetRequest? request = await ReadJsonAsync<LinkShotAssetRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                ShotAssetReference reference = await service.ProjectShots.LinkAssetAsync(
                    user.ID, id, shotId, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { reference });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/projects/{id}/shots/{shotId}/assets/{referenceId}", async (
            HttpContext context, string id, string shotId, string referenceId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.ProjectShots.UnlinkAssetAsync(
                    user.ID, id, shotId, referenceId, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { unlinked = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/projects/{id}/asset-candidates", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CreateAssetCandidatesRequest? request = await ReadJsonAsync<CreateAssetCandidatesRequest>(
                    context, 512 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                List<ProjectAssetCandidate> candidates = await service.ProjectShots.CreateCandidatesAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { candidates });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/projects/{id}/asset-candidates", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                long page = ParsePositiveQueryInt(context.Request.Query["page"].ToString(), 1);
                if (page < 0)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                long pageSize = ParsePositiveQueryInt(context.Request.Query["pageSize"].ToString(), 100);
                if (pageSize < 0)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                ProjectAssetCandidatePageDto result = await service.ProjectShots.CandidatesPageAsync(
                    user.ID, id, page, pageSize,
                    context.Request.Query["unitId"].ToString(),
                    context.Request.Query["status"].ToString(),
                    context.Request.Query["category"].ToString(),
                    context.Request.Query["q"].ToString(),
                    cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/projects/{id}/asset-candidates/{candidateId}/confirm", async (
            HttpContext context, string id, string candidateId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ConfirmProjectAssetCandidateRequest? request = await ReadJsonAsync<ConfirmProjectAssetCandidateRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                ProjectAssetSummaryDto asset = await service.ProjectShots.ConfirmCandidateAsync(
                    user.ID, id, candidateId, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { asset });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 解析正整数查询参数；解析失败返回 -1（对应 Go: <c>parsePositiveQueryInt</c> 的 400 分支）。
    /// </summary>
    private static long ParsePositiveQueryInt(string value, long fallback)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }
        if (!long.TryParse(value, out long parsed))
        {
            return -1;
        }
        return parsed;
    }

    /// <summary>
    /// 用户提示词偏好路由。对应 Go: <c>handler/user_data.go</c> 的 settings/prompt-templates。
    /// </summary>
    public static void MapUserPromptPreferenceRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        api.MapGet("/settings/prompt-templates", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                List<UserPromptPreferenceDto> preferences = await service.PromptTemplates
                    .UserPromptPreferencesAsync(user, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { preferences });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/settings/prompt-templates/{operation}", async (
            HttpContext context, string operation, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                UserPromptCustomizationRequest? request = await ReadJsonAsync<UserPromptCustomizationRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                UserPromptCustomization customization = await service.PromptTemplates
                    .UpdateUserPromptCustomizationAsync(user, operation, request, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { customization });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/settings/prompt-templates/{operation}", async (
            HttpContext context, string operation, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.PromptTemplates.ResetUserPromptCustomizationAsync(
                    user, operation, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { ok = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 创作运行路由。对应 Go: <c>handler/creation.go</c>。
    /// 画布提交三路由（canvas/canvas-snapshot/canvas-commit）随下一批接入（PENDING #63）。
    /// </summary>
    public static void MapCreationRunRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        async Task<User> CurrentUserAsync(HttpContext context, CancellationToken ct) =>
            await service.CurrentUserAsync(SessionCookie.Read(context), ct).ConfigureAwait(false);

        api.MapGet("/creation-runs", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await CurrentUserAsync(context, cancellationToken).ConfigureAwait(false);
                List<CreationRunOutputDto> runs = await service.CreationRuns.ListAsync(
                    user.ID, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { runs });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/creation-runs", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await CurrentUserAsync(context, cancellationToken).ConfigureAwait(false);
                CreationRequestDto? request = await ReadJsonAsync<CreationRequestDto>(
                    context, 2 << 20, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                CreationDetailDto detail = await service.CreationRuns.CreateAsync(
                    user.ID, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(detail);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/creation-runs/{id}", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await CurrentUserAsync(context, cancellationToken).ConfigureAwait(false);
                CreationDetailDto detail = await service.CreationRuns.GetAsync(
                    user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(detail);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/creation-runs/{id}", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
            await CreationChangeHandler(context, service, id, "save", cancellationToken));

        foreach (string action in new[] { "claim", "heartbeat", "release", "proposal-approve", "proposal-invalidate" })
        {
            string captured = action;
            api.MapPost($"/creation-runs/{{id}}/{action}", async (
                HttpContext context, string id, CancellationToken cancellationToken) =>
                await CreationChangeHandler(context, service, id, captured, cancellationToken));
        }

        api.MapPost("/creation-runs/{id}/submissions/prepare", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await CurrentUserAsync(context, cancellationToken).ConfigureAwait(false);
                CreationRequestDto? request = await ReadJsonAsync<CreationRequestDto>(
                    context, 2 << 20, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                CreationSubmissionOutputDto output = await service.CreationRuns.PrepareSubmissionAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(output);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/creation-runs/{id}/submissions/approve", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await CurrentUserAsync(context, cancellationToken).ConfigureAwait(false);
                CreationRequestDto? request = await ReadJsonAsync<CreationRequestDto>(
                    context, 2 << 20, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                List<CreationSubmissionOutputDto> submissions = await service.CreationRuns.ApproveSubmissionsAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { submissions });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/creation-runs/{id}/submissions/refresh", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await CurrentUserAsync(context, cancellationToken).ConfigureAwait(false);
                CreationRequestDto? request = await ReadJsonAsync<CreationRequestDto>(
                    context, 2 << 20, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                CreationSubmissionOutputDto output = await service.CreationRuns.RefreshSubmissionAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(output);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/creation-runs/{id}/canvas", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await CurrentUserAsync(context, cancellationToken).ConfigureAwait(false);
                CreationRequestDto? request = await ReadJsonAsync<CreationRequestDto>(
                    context, 2 << 20, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Dictionary<string, JsonElement?> result = await service.CreationRuns.CreateCanvasAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/creation-runs/{id}/canvas-snapshot", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await CurrentUserAsync(context, cancellationToken).ConfigureAwait(false);
                Dictionary<string, JsonElement?> snapshot = await service.CreationRuns.CanvasSnapshotAsync(
                    user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(snapshot);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/creation-runs/{id}/canvas-commit", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await CurrentUserAsync(context, cancellationToken).ConfigureAwait(false);
                CreationRequestDto? request = await ReadJsonAsync<CreationRequestDto>(
                    context, 8 << 20, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Dictionary<string, JsonElement?> result = await service.CreationRuns.CommitCanvasAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/creation-runs/{id}/execute", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await CurrentUserAsync(context, cancellationToken).ConfigureAwait(false);
                CreationRequestDto? request = await ReadJsonAsync<CreationRequestDto>(
                    context, 2 << 20, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                TaskEntity task = await service.CreationRuns.ExecuteSubmissionAsync(
                    user.ID, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(task);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>变更分发公共体。对应 Go: <c>write("save"|action)</c>。</summary>
    private static async Task<IResult> CreationChangeHandler(
        HttpContext context,
        CanvasService service,
        string id,
        string action,
        CancellationToken cancellationToken)
    {
        try
        {
            User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                .ConfigureAwait(false);
            CreationRequestDto? request = await ReadJsonAsync<CreationRequestDto>(
                context, 2 << 20, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }
            object? result = await service.CreationRuns.ChangeAsync(
                user.ID, id, action, request, cancellationToken).ConfigureAwait(false);
            return ApiResults.Ok(result);
        }
        catch (Exception error)
        {
            return ApiResults.FailService(error, context);
        }
    }

    /// <summary>对应 Go: <c>hasUserAssetPageFilters</c>。</summary>
    private static bool HasUserAssetPageFilters(HttpContext context)
    {
        foreach (string key in new[] { "pageSize", "kind", "category", "folderId", "uncategorized", "status", "q" })
        {
            if (context.Request.Query.ContainsKey(key))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>读取 JSON 请求体（可选体上限），语法错误或超限返回 null。</summary>
    private static async Task<T?> ReadJsonAsync<T>(
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

    /// <summary>读取字符串数组字段（体上限生效）。对应 Go 的匿名绑定 struct{IDs []string}。</summary>
    private static async Task<List<string>?> ReadStringListAsync(
        HttpContext context, string field, long maxBytes, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maxBytes)
        {
            return null;
        }
        try
        {
            JsonElement? request = await JsonSerializer.DeserializeAsync<JsonElement>(
                context.Request.Body,
                CanvasJson.ReadOptions,
                cancellationToken).ConfigureAwait(false);
            if (request is null ||
                request.Value.ValueKind != JsonValueKind.Object ||
                !request.Value.TryGetProperty(field, out JsonElement ids) ||
                ids.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            List<string> result = [];
            foreach (JsonElement item in ids.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    result.Add(item.GetString() ?? "");
                }
            }
            return result;
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

    private static async Task<JsonElement?> ReadJsonAsync(
        HttpContext context,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maxBytes)
        {
            return null;
        }
        try
        {
            return await JsonSerializer.DeserializeAsync<JsonElement>(
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
}
