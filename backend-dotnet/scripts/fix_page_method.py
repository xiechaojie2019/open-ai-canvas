# -*- coding: utf-8 -*-
import io

# 1. ProjectAssetFilter 加 FolderId/Query
path = r"src/OpenAICanvas.Application/ProjectAssetService.cs"
with io.open(path, encoding="utf-8") as f:
    content = f.read()

old = '''public sealed class ProjectAssetFilter
{
    public string Category { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string Status { get; set; } = "";
    public string Usage { get; set; } = "";
}'''
new = '''public sealed class ProjectAssetFilter
{
    public string Category { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string Status { get; set; } = "";
    public string Usage { get; set; } = "";
    public string FolderId { get; set; } = "";
    public string Query { get; set; } = "";
}'''
assert old in content, "filter class"
content = content.replace(old, new)

anchor = "    public async Task<ProjectAssetSummaryDto> LinkProjectAssetAsync("
page_method = '''    /// <summary>项目素材分页。对应 Go: <c>ProjectAssetsPage</c>。</summary>
    public async Task<object> ProjectAssetsPageAsync(
        string userId, string projectId, int page, int pageSize, ProjectAssetFilter filter,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Asset> assets = await _repository
            .ProjectAssetsAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectAssetLink> links = await _repository
            .ProjectAssetLinksOrderedAsync(projectId, cancellationToken).ConfigureAwait(false);
        Dictionary<string, ProjectAssetLink> linkByAsset = links.ToDictionary(l => l.AssetID, StringComparer.Ordinal);

        List<ProjectAssetSummaryDto> items = [];
        foreach (Asset asset in assets)
        {
            if (filter.Category.Length > 0 && asset.Category != filter.Category) continue;
            if (filter.MediaType.Length > 0 && asset.Kind != filter.MediaType) continue;
            if (filter.Status.Length > 0 && asset.Status != filter.Status) continue;
            if (filter.FolderId.Length > 0 &&
                (!linkByAsset.TryGetValue(asset.ID, out ProjectAssetLink? link) ||
                 link.FolderID != filter.FolderId)) continue;
            if (filter.Query.Length > 0 &&
                !asset.Title.Contains(filter.Query, StringComparison.OrdinalIgnoreCase) &&
                !asset.PayloadJSON.Contains(filter.Query, StringComparison.OrdinalIgnoreCase)) continue;
            items.Add(await BuildSummaryAsync(userId, projectId, asset, cancellationToken).ConfigureAwait(false));
        }

        long total = items.Count;
        List<ProjectAssetSummaryDto> paged = items
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return new
        {
            assets = (IReadOnlyList<ProjectAssetSummaryDto>)paged,
            total,
            page,
            pageSize,
        };
    }

''' + anchor
assert anchor in content, "page anchor"
content = content.replace(anchor, page_method, 1)
with io.open(path, "w", encoding="utf-8", newline="") as f:
    f.write(content)

# 2. CanvasService forward 修正
path = r"src/OpenAICanvas.Application/CanvasService.cs"
with io.open(path, encoding="utf-8") as f:
    content = f.read()
old2 = '''    public Task<object> ProjectAssetsPageAsync(
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
new2 = '''    public Task<object> ProjectAssetsPageAsync(
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
                FolderId = folderId ?? "",
                Query = query,
            },
            cancellationToken);'''
assert old2 in content, "canvas forward"
content = content.replace(old2, new2, 1)
with io.open(path, "w", encoding="utf-8", newline="") as f:
    f.write(content)
print("patched")