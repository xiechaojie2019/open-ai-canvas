# -*- coding: utf-8 -*-
import io

path = r"src/OpenAICanvas.Web/Endpoints/UserDataEndpoints.cs"
with io.open(path, encoding="utf-8") as f:
    content = f.read()

anchor = "    /// <summary>对应 Go: <c>hasUserAssetPageFilters</c>。</summary>"
new_map = '''    /// <summary>
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
                if (context.Request.Query.ContainsKey("page") ||
                    context.Request.Query.ContainsKey("pageSize"))
                {
                    (int page, int pageSize, string? pageError) = ParsePagination(context, 40);
                    if (pageError is not null)
                    {
                        return ApiResults.Fail(StatusCodes.Status400BadRequest, new InvalidOperationException(pageError));
                    }
                    ProjectAssetPageDto result = await service.ProjectAssetsPageAsync(
                        user.ID, id, page, pageSize,
                        context.Request.Query["category"].ToString(),
                        context.Request.Query["mediaType"].ToString(),
                        context.Request.Query["status"].ToString(),
                        context.Request.Query.ContainsKey("folderId")
                            ? context.Request.Query["folderId"].ToString()
                            : null,
                        context.Request.Query["q"].ToString(),
                        cancellationToken).ConfigureAwait(false);
                    return ApiResults.Ok(result);
                }
                List<ProjectAssetSummaryDto> assets = await service.ProjectAssetsAsync(
                    user.ID, id,
                    category: context.Request.Query["category"].ToString(),
                    mediaType: context.Request.Query["mediaType"].ToString(),
                    status: context.Request.Query["status"].ToString(),
                    usage: context.Request.Query["usage"].ToString(),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
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

''' + anchor
assert anchor in content, "asset link route anchor"
content = content.replace(anchor, new_map, 1)
with io.open(path, "w", encoding="utf-8", newline="") as f:
    f.write(content)
print("routes ok")