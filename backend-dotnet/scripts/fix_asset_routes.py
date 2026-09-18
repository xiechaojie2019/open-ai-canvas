# -*- coding: utf-8 -*-
import io

path = r"src/OpenAICanvas.Application/CanvasService.cs"
with io.open(path, encoding="utf-8") as f:
    content = f.read()

old = '''    public Task<List<ProjectAssetSummaryDto>> ProjectAssetsAsync(
        string userId, string projectId, ProjectAssetFilter? filter = null,
        CancellationToken cancellationToken = default) =>
        ProjectAssets.ProjectAssetsAsync(userId, projectId, filter, cancellationToken);'''
new = '''    public Task<List<ProjectAssetSummaryDto>> ProjectAssetsAsync(
        string userId, string projectId, ProjectAssetFilter? filter = null,
        CancellationToken cancellationToken = default) =>
        ProjectAssets.ProjectAssetsAsync(userId, projectId, filter, cancellationToken);

    /// <summary>对应 Go: <c>Service.ProjectAssetsPage</c>。</summary>
    public Task<object> ProjectAssetsPageAsync(
        string userId, string projectId, int page, int pageSize,
        string category, string mediaType, string status, string? folderId, string query,
        CancellationToken cancellationToken = default) =>
        ProjectAssets.ProjectAssetsPageAsync(
            userId, projectId, page, pageSize,
            new ProjectAssetFilter
            {
                Category = category,
                MediaType = mediaType,
                Status = status,
                FolderId = folderId,
                Query = query,
            },
            cancellationToken);'''
assert old in content, "page forward anchor"
content = content.replace(old, new, 1)
with io.open(path, "w", encoding="utf-8", newline="") as f:
    f.write(content)
print("forwards ok")

# 路由：改成项目服务的实际签名
path = r"src/OpenAICanvas.Web/Endpoints/UserDataEndpoints.cs"
with io.open(path, encoding="utf-8") as f:
    content = f.read()
old2 = '''                if (context.Request.Query.ContainsKey("page") ||
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
                return ApiResults.Ok(new { assets });'''
new2 = '''                ProjectAssetFilter filter = new()
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
                return ApiResults.Ok(new { assets });'''
assert old2 in content, "route body"
content = content.replace(old2, new2, 1)
with io.open(path, "w", encoding="utf-8", newline="") as f:
    f.write(content)
print("routes ok")