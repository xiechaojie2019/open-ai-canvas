#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;

namespace OpenAICanvas.Application;

/// <summary>画布/素材摘要投影。对应 Go: <c>canvas.UserDataSummary</c>（字段顺序即输出顺序）。</summary>
public sealed class UserDataSummaryDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("folderId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string FolderID { get; init; } = "";

    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Kind { get; init; } = "";

    [JsonPropertyName("category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Category { get; init; } = "";

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Status { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }
}

/// <summary>创作端画布库分页条目。对应 Go: <c>canvas.CanvasLibrarySummary</c>。</summary>
public sealed class CanvasLibrarySummaryDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("projectId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string ProjectID { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    [JsonPropertyName("nodeCount")]
    public int NodeCount { get; init; }

    [JsonPropertyName("previewNodes")]
    public IReadOnlyList<Dictionary<string, object?>> PreviewNodes { get; init; } = [];
}

/// <summary>画布库分页。对应 Go: <c>canvas.CanvasLibraryPage</c>（字段顺序即输出顺序）。</summary>
public sealed class CanvasLibraryPageDto
{
    [JsonPropertyName("projects")]
    public IReadOnlyList<CanvasLibrarySummaryDto> Projects { get; init; } = [];

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}

/// <summary>
/// 用户数据（画布/素材）域服务。对应 Go: <c>internal/canvas</c> 的
/// <c>user_data.go</c> / <c>user_data_page.go</c> / <c>canvas_asset_guard.go</c>。
/// </summary>
/// <remarks>
/// 存储锁为进程内信号量（Go 为 Service 级互斥锁，同样进程内）；每日活跃记录与
/// 资源联动删除属后续节点（已记入 PENDING-CONFIRMATIONS.md）。
/// </remarks>
public sealed class UserDataService
{
    /// <summary>同步数据单条上限。对应 Go: <c>4&lt;&lt;20</c>。</summary>
    private const int MaxSyncedPayloadBytes = 4 << 20;

    private readonly Repository _repository;
    private readonly IRuntimePolicyProvider _runtimePolicy;
    private readonly SemaphoreSlim _storageLock = new(1, 1);

    public UserDataService(Repository repository, IRuntimePolicyProvider? runtimePolicy = null)
    {
        _repository = repository;
        _runtimePolicy = runtimePolicy ?? new DefaultRuntimePolicyProvider();
    }

    // ------------------------------------------------------------ 画布工程

    /// <summary>用户全部画布 payload。对应 Go: <c>UserCanvasProjects</c>。</summary>
    public async Task<List<JsonElement>> UserCanvasProjectsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CanvasProject> projects = await _repository.CanvasProjectsAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        List<JsonElement> result = [];
        foreach (CanvasProject project in projects)
        {
            if (string.IsNullOrWhiteSpace(project.PayloadJSON))
            {
                continue;
            }
            result.Add(JsonDocument.Parse(project.PayloadJSON).RootElement.Clone());
        }
        return result;
    }

    /// <summary>画布摘要列表。对应 Go: <c>UserCanvasProjectSummaries</c>。</summary>
    public async Task<List<UserDataSummaryDto>> UserCanvasProjectSummariesAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CanvasProject> projects = await _repository.CanvasProjectSummariesAsync(
            userId, cancellationToken).ConfigureAwait(false);
        return projects.Select(project => new UserDataSummaryDto
        {
            ID = project.ID,
            Title = project.Title,
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt,
        }).ToList();
    }

    /// <summary>单个画布 payload。对应 Go: <c>UserCanvasProject</c>（handler 层把错误映射 404）。</summary>
    public async Task<JsonElement> UserCanvasProjectAsync(
        string userId,
        string id,
        CancellationToken cancellationToken = default)
    {
        CanvasProject? project = await _repository.CanvasProjectForUserAsync(userId, id, cancellationToken)
            .ConfigureAwait(false);
        if (project is null)
        {
            throw new InvalidOperationException("record not found");
        }
        return JsonDocument.Parse(project.PayloadJSON).RootElement.Clone();
    }

    /// <summary>画布 upsert。对应 Go: <c>UpsertUserCanvasProject</c>。</summary>
    public async Task<UserDataSummaryDto> UpsertUserCanvasProjectAsync(
        string userId,
        JsonElement raw,
        CancellationToken cancellationToken = default)
    {
        string rawText = raw.GetRawText();
        CanvasProject project = CanvasProjectFromJSON(userId, rawText);
        await _storageLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateCanvasMediaAssetsAsync(userId, rawText, cancellationToken).ConfigureAwait(false);

            CanvasProject? existing = await _repository.CanvasProjectForUserAsync(userId, project.ID, cancellationToken)
                .ConfigureAwait(false);
            long existingBytes = existing is null ? 0 : Encoding.UTF8.GetByteCount(existing.PayloadJSON);
            await StructuredQuotaAsync(
                userId, "canvas", existing is null,
                Encoding.UTF8.GetByteCount(rawText) - existingBytes,
                cancellationToken).ConfigureAwait(false);

            await _repository.UpsertCanvasProjectAsync(project, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _storageLock.Release();
        }
        return new UserDataSummaryDto
        {
            ID = project.ID,
            Title = project.Title,
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt,
        };
    }

    /// <summary>删除画布。对应 Go: <c>DeleteUserCanvasProject</c>。</summary>
    public Task DeleteUserCanvasProjectAsync(
        string userId,
        string id,
        CancellationToken cancellationToken = default) =>
        _repository.DeleteCanvasProjectAsync(userId, id, cancellationToken);

    /// <summary>画布库分页。对应 Go: <c>UserCanvasProjectsPage</c>。</summary>
    public async Task<CanvasLibraryPageDto> UserCanvasProjectsPageAsync(
        string userId,
        int page,
        int pageSize,
        string projectId,
        string search,
        string sort,
        CancellationToken cancellationToken = default)
    {
        if (page < 1)
        {
            page = 1;
        }
        if (page > 1000000)
        {
            throw AppError.BadAuthRequest("页码超出范围");
        }
        if (pageSize < 1)
        {
            pageSize = 40;
        }
        if (pageSize > 50)
        {
            pageSize = 50;
        }

        (IReadOnlyList<CanvasProject> projects, long total) = await _repository.UserCanvasProjectsPageAsync(
            userId, page, pageSize, projectId, search, sort, cancellationToken).ConfigureAwait(false);

        List<CanvasLibrarySummaryDto> summaries = [];
        foreach (CanvasProject project in projects)
        {
            JsonElement document;
            try
            {
                document = JsonSerializer.Deserialize<JsonElement>(project.PayloadJSON);
            }
            catch (JsonException)
            {
                throw AppError.New(500, "画布数据解析失败");
            }

            List<Dictionary<string, object?>> preview = [];
            if (document.ValueKind == JsonValueKind.Object &&
                document.TryGetProperty("nodes", out JsonElement nodes) &&
                nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement node in nodes.EnumerateArray())
                {
                    if (preview.Count == 4)
                    {
                        break;
                    }
                    string? type = node.TryGetProperty("type", out JsonElement typeElement) &&
                                   typeElement.ValueKind == JsonValueKind.String
                        ? typeElement.GetString()
                        : null;
                    if (type is not ("image" or "video"))
                    {
                        continue;
                    }
                    Dictionary<string, object?> item = new(StringComparer.Ordinal)
                    {
                        ["position"] = new Dictionary<string, int>(StringComparer.Ordinal) { ["x"] = 0, ["y"] = 0 },
                    };
                    foreach (string key in new[] { "id", "type", "title" })
                    {
                        if (node.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                        {
                            string text = value.GetString() ?? "";
                            if (text.Length > 256)
                            {
                                text = text[..256];
                            }
                            item[key] = text;
                        }
                    }
                    foreach (string key in new[] { "width", "height" })
                    {
                        if (node.TryGetProperty(key, out JsonElement number) && number.ValueKind == JsonValueKind.Number)
                        {
                            item[key] = number.GetDouble();
                        }
                    }
                    Dictionary<string, object?> metadata = new(StringComparer.Ordinal);
                    if (node.TryGetProperty("metadata", out JsonElement metadataElement) &&
                        metadataElement.ValueKind == JsonValueKind.Object &&
                        metadataElement.TryGetProperty("storageKey", out JsonElement storageKey) &&
                        storageKey.ValueKind == JsonValueKind.String)
                    {
                        string key = storageKey.GetString() ?? "";
                        if (key.Length <= 512)
                        {
                            metadata["storageKey"] = key;
                        }
                    }
                    item["metadata"] = metadata;
                    preview.Add(item);
                }
            }

            summaries.Add(new CanvasLibrarySummaryDto
            {
                ID = project.ID,
                ProjectID = project.ProjectID,
                Title = project.Title,
                CreatedAt = project.CreatedAt,
                UpdatedAt = project.UpdatedAt,
                NodeCount = document.ValueKind == JsonValueKind.Object &&
                            document.TryGetProperty("nodes", out JsonElement nodesCount) &&
                            nodesCount.ValueKind == JsonValueKind.Array
                    ? nodesCount.GetArrayLength()
                    : 0,
                PreviewNodes = preview,
            });
        }

        return new CanvasLibraryPageDto
        {
            Projects = summaries,
            Page = page,
            PageSize = pageSize,
            Total = total,
            HasMore = (long)page * pageSize < total,
        };
    }

    // ------------------------------------------------------------ 素材库（节点 B）

    /// <summary>素材快照。对应 Go: <c>UserDataSnapshot</c>（assets/projects 字段顺序）。</summary>
    public async Task<(List<JsonElement> Assets, List<JsonElement> Projects)> UserDataSnapshotAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        List<JsonElement> assets = await UserAssetsAsync(userId, cancellationToken).ConfigureAwait(false);
        List<JsonElement> projects = await UserCanvasProjectsAsync(userId, cancellationToken).ConfigureAwait(false);
        return (assets, projects);
    }

    /// <summary>用户全部素材 payload。对应 Go: <c>UserAssets</c>。</summary>
    public async Task<List<JsonElement>> UserAssetsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Asset> assets = await _repository.AssetsAsync(userId, cancellationToken).ConfigureAwait(false);
        List<JsonElement> result = [];
        foreach (Asset asset in assets)
        {
            JsonElement? payload = AssetLibraryDomain.ClientAssetPayload(asset);
            if (payload is not null)
            {
                result.Add(payload.Value);
            }
        }
        return result;
    }

    /// <summary>素材摘要。对应 Go: <c>UserAssetSummaries</c>。</summary>
    public async Task<List<UserDataSummaryDto>> UserAssetSummariesAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Asset> assets = await _repository.AssetSummariesAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        return assets.Select(asset => new UserDataSummaryDto
        {
            ID = asset.ID,
            FolderID = asset.FolderID,
            Kind = asset.Kind,
            Category = asset.Category,
            Status = asset.Status,
            Title = asset.Title,
            CreatedAt = asset.CreatedAt,
            UpdatedAt = asset.UpdatedAt,
        }).ToList();
    }

    /// <summary>单个素材 payload。对应 Go: <c>UserAsset</c>（handler 层映射 404）。</summary>
    public async Task<JsonElement> UserAssetAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        Asset? asset = await _repository.AssetForUserAsync(userId, id, cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            throw new InvalidOperationException("record not found");
        }
        JsonElement? payload = AssetLibraryDomain.ClientAssetPayload(asset);
        return payload ?? JsonSerializer.SerializeToElement("");
    }

    /// <summary>按 ID 批量取素材（≤100）。对应 Go: <c>UserAssetsByIDs</c>。</summary>
    public async Task<List<JsonElement>> UserAssetsByIDsAsync(
        string userId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count > 100)
        {
            throw AppError.BadAuthRequest("每次最多读取 100 个素材");
        }
        List<string> unique = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string raw in ids)
        {
            string id = raw.Trim();
            if (id.Length == 0 || id.Length > 80)
            {
                throw AppError.BadAuthRequest("素材 ID 无效");
            }
            if (seen.Add(id))
            {
                unique.Add(id);
            }
        }
        IReadOnlyList<Asset> assets = await _repository.AssetsForUserIDsAsync(userId, unique, cancellationToken)
            .ConfigureAwait(false);
        List<JsonElement> result = [];
        foreach (Asset asset in assets)
        {
            JsonElement? payload = AssetLibraryDomain.ClientAssetPayload(asset);
            if (payload is not null)
            {
                result.Add(payload.Value);
            }
        }
        return result;
    }

    /// <summary>素材 upsert。对应 Go: <c>UpsertUserAsset</c>。</summary>
    public async Task<UserDataSummaryDto> UpsertUserAssetAsync(
        string userId,
        JsonElement raw,
        CancellationToken cancellationToken = default)
    {
        string rawText = raw.GetRawText();
        Asset asset = AssetLibraryDomain.AssetFromJSON(userId, rawText);
        await _storageLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (asset.FolderID.Length > 0 &&
                await _repository.AssetFolderForUserAsync(userId, asset.FolderID, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                throw AppError.BadAuthRequest("素材分类不存在");
            }
            Asset? existing = await _repository.AssetForUserAsync(userId, asset.ID, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null && existing.PayloadJSON != asset.PayloadJSON)
            {
                await ValidateAssetCanvasReferencesAsync(userId, asset, cancellationToken).ConfigureAwait(false);
            }
            long existingBytes = existing is null ? 0 : Encoding.UTF8.GetByteCount(existing.PayloadJSON);
            await StructuredQuotaAsync(
                userId, "asset", existing is null,
                Encoding.UTF8.GetByteCount(rawText) - existingBytes,
                cancellationToken).ConfigureAwait(false);
            await _repository.UpsertAssetAsync(asset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _storageLock.Release();
        }
        return new UserDataSummaryDto
        {
            ID = asset.ID,
            FolderID = asset.FolderID,
            Kind = asset.Kind,
            Category = asset.Category,
            Status = asset.Status,
            Title = asset.Title,
            CreatedAt = asset.CreatedAt,
            UpdatedAt = asset.UpdatedAt,
        };
    }

    /// <summary>素材分页与分面。对应 Go: <c>UserAssetsPage</c>。</summary>
    public async Task<UserAssetPageDto> UserAssetsPageAsync(
        string userId,
        int page,
        int pageSize,
        string kind,
        string category,
        string? folderId,
        bool uncategorized,
        string status,
        string query,
        CancellationToken cancellationToken = default)
    {
        (page, pageSize) = NormalizeAssetPage(page, pageSize);
        Repository.UserAssetPageFilter filter = new(kind, category, folderId, uncategorized, status, query);
        (IReadOnlyList<Asset> assets, long total) = await _repository.UserAssetsPageAsync(
            userId, page, pageSize, filter, cancellationToken).ConfigureAwait(false);

        List<JsonElement> payloads = [];
        foreach (Asset asset in assets)
        {
            JsonElement? payload = AssetLibraryDomain.ClientAssetPayload(asset);
            if (payload is not null)
            {
                payloads.Add(payload.Value);
            }
        }

        (IReadOnlyList<Repository.UserAssetFacetRow> kindRows,
         IReadOnlyList<Repository.UserAssetFacetRow> categoryRows,
         IReadOnlyList<Repository.UserAssetFacetRow> folderRows) =
            await _repository.UserAssetFacetsAsync(userId, status, cancellationToken).ConfigureAwait(false);

        return new UserAssetPageDto
        {
            Assets = payloads,
            KindCounts = FacetMap(kindRows),
            CategoryCounts = FacetMap(categoryRows),
            FolderCounts = FacetMap(folderRows),
            Page = page,
            PageSize = pageSize,
            Total = total,
            HasMore = (long)page * pageSize < total,
        };
    }

    private static Dictionary<string, long> FacetMap(IReadOnlyList<Repository.UserAssetFacetRow> rows)
    {
        Dictionary<string, long> result = new(StringComparer.Ordinal);
        foreach (Repository.UserAssetFacetRow row in rows)
        {
            result[row.Key] = row.Count;
        }
        return result;
    }

    private static (int Page, int PageSize) NormalizeAssetPage(int page, int pageSize)
    {
        if (page < 1)
        {
            page = 1;
        }
        if (page > 1000000)
        {
            throw AppError.BadAuthRequest("页码超出范围");
        }
        if (pageSize < 1)
        {
            pageSize = 40;
        }
        if (pageSize > 120)
        {
            pageSize = 120;
        }
        return (page, pageSize);
    }

    // ------------------------------------------------------------ 素材分类

    /// <summary>分类列表。对应 Go: <c>AssetFolders</c>。</summary>
    public async Task<IReadOnlyList<AssetFolder>> AssetFoldersAsync(
        string userId, CancellationToken cancellationToken = default) =>
        await _repository.AssetFoldersAsync(userId, cancellationToken).ConfigureAwait(false);

    /// <summary>创建分类。对应 Go: <c>CreateAssetFolder</c>。</summary>
    public async Task<AssetFolder> CreateAssetFolderAsync(
        string userId,
        string name,
        CancellationToken cancellationToken = default)
    {
        (string normalizedName, string nameKey) = NormalizeAssetFolderName(name);
        if (await _repository.AssetFolderNameExistsAsync(userId, nameKey, "", cancellationToken)
                .ConfigureAwait(false))
        {
            throw AppError.BadAuthRequest("已存在同名素材分类");
        }
        long position = await _repository.NextAssetFolderPositionAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        AssetFolder folder = new()
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            Name = normalizedName,
            NameKey = nameKey,
            Position = position,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _repository.CreateAssetFolderAsync(folder, cancellationToken).ConfigureAwait(false);
        return folder;
    }

    /// <summary>更新分类名。对应 Go: <c>UpdateAssetFolder</c>。</summary>
    public async Task<AssetFolder> UpdateAssetFolderAsync(
        string userId,
        string folderId,
        string name,
        CancellationToken cancellationToken = default)
    {
        AssetFolder? folder = await _repository.AssetFolderForUserAsync(
            userId, folderId.Trim(), cancellationToken).ConfigureAwait(false);
        if (folder is null)
        {
            throw AppError.BadAuthRequest("素材分类不存在");
        }
        (string normalizedName, string nameKey) = NormalizeAssetFolderName(name);
        if (await _repository.AssetFolderNameExistsAsync(userId, nameKey, folder.ID, cancellationToken)
                .ConfigureAwait(false))
        {
            throw AppError.BadAuthRequest("已存在同名素材分类");
        }
        folder.Name = normalizedName;
        folder.NameKey = nameKey;
        folder.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAssetFolderAsync(folder, cancellationToken).ConfigureAwait(false);
        return folder;
    }

    /// <summary>删除分类（素材先移出）。对应 Go: <c>DeleteAssetFolder</c>。</summary>
    public async Task DeleteAssetFolderAsync(
        string userId,
        string folderId,
        CancellationToken cancellationToken = default)
    {
        await _storageLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _repository.DeleteAssetFolderAsync(
                userId, folderId.Trim(), DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            throw AppError.BadAuthRequest("素材分类不存在");
        }
        finally
        {
            _storageLock.Release();
        }
    }

    /// <summary>批量移动素材到分类。对应 Go: <c>MoveUserAssetsToFolder</c>。</summary>
    public async Task<(IReadOnlyList<string> AssetIDs, string FolderID)> MoveUserAssetsToFolderAsync(
        string userId,
        IReadOnlyList<string>? assetIds,
        string folderId,
        CancellationToken cancellationToken = default)
    {
        List<string> ids = UniqueNonEmptyStrings(assetIds ?? []);
        if (ids.Count == 0)
        {
            throw AppError.BadAuthRequest("请选择要移动的素材");
        }
        if (ids.Count > 200)
        {
            throw AppError.BadAuthRequest("一次最多移动 200 个素材");
        }
        await _storageLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string trimmedFolderId = folderId.Trim();
            if (trimmedFolderId.Length > 0 &&
                await _repository.AssetFolderForUserAsync(userId, trimmedFolderId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                throw AppError.BadAuthRequest("目标素材分类不存在");
            }
            await _repository.MoveUserAssetsToFolderAsync(
                userId, ids, trimmedFolderId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
            return (ids, trimmedFolderId);
        }
        catch (InvalidOperationException)
        {
            throw AppError.BadAuthRequest("部分素材不存在或不属于当前用户");
        }
        finally
        {
            _storageLock.Release();
        }
    }

    /// <summary>对应 Go: <c>normalizeAssetFolderName</c>（≤40 字符）。</summary>
    private static (string Name, string NameKey) NormalizeAssetFolderName(string value)
    {
        string name = value.Trim();
        if (name.Length == 0)
        {
            throw AppError.BadAuthRequest("请输入素材分类名称");
        }
        if (name.Length > 40)
        {
            throw AppError.BadAuthRequest("素材分类名称不能超过 40 个字符");
        }
        return (name, name.ToLowerInvariant());
    }

    private static List<string> UniqueNonEmptyStrings(IEnumerable<string> values)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string value in values)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed))
            {
                continue;
            }
            result.Add(trimmed);
        }
        return result;
    }

    // ------------------------------------------------------------ 素材↔画布引用守卫

    /// <summary>
    /// 阻止把仍被画布引用的素材替换为其他云端资源。
    /// 对应 Go: <c>ValidateAssetCanvasReferences</c>。
    /// </summary>
    private async Task ValidateAssetCanvasReferencesAsync(
        string userId,
        Asset asset,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CanvasProject> canvases = await _repository.CanvasProjectsAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        foreach (CanvasProject canvas in canvases)
        {
            List<MediaAssetReference> references = MediaAssetReferences(canvas.PayloadJSON);
            foreach (MediaAssetReference reference in references)
            {
                if (reference.AssetID != asset.ID)
                {
                    continue;
                }
                HashSet<string> candidate = new(StringComparer.Ordinal) { reference.ResourceID };
                if (DocumentReferencedIDs(asset.PayloadJSON, candidate).Count == 0)
                {
                    throw AppError.BadAuthRequest("素材仍被画布引用，不能替换为其他云端资源");
                }
            }
        }
    }

    // ------------------------------------------------------------ 同步校验（Go canvas 包纯函数）

    /// <summary>对应 Go: <c>canvasProjectFromJSON</c>。</summary>
    internal static CanvasProject CanvasProjectFromJSON(string userId, string rawText)
    {
        ValidateSyncedPayload(rawText, "画布");
        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(rawText);
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("画布数据格式错误");
        }

        DateTime now = DateTime.UtcNow;
        string createdAt = PayloadString(payload, "createdAt");
        string updatedAt = PayloadString(payload, "updatedAt");
        DateTime parsedCreatedAt = ParseClientTime(createdAt, now);
        DateTime parsedUpdatedAt = ParseClientTime(updatedAt, parsedCreatedAt);

        string id = PayloadString(payload, "id").Trim();
        if (id.Length == 0)
        {
            id = IdGenerator.NewId();
        }
        return new CanvasProject
        {
            ID = id,
            UserID = userId,
            ProjectID = PayloadString(payload, "projectId").Trim(),
            Title = PayloadString(payload, "title").Trim(),
            PayloadJSON = rawText,
            CreatedAt = parsedCreatedAt,
            UpdatedAt = parsedUpdatedAt,
        };
    }

    /// <summary>对应 Go: <c>ValidateSyncedPayload</c>（4MB 上限 + 禁内嵌媒体）。</summary>
    internal static void ValidateSyncedPayload(string rawText, string label)
    {
        if (Encoding.UTF8.GetByteCount(rawText) > MaxSyncedPayloadBytes)
        {
            throw AppError.BadAuthRequest($"{label}数据超过 4MB，请先把媒体文件保存到资源存储");
        }
        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(rawText);
        }
        catch (JsonException)
        {
            return;
        }
        if (ContainsInlineMediaDataURL(payload))
        {
            throw AppError.BadAuthRequest($"{label}数据包含内嵌媒体，请先上传到资源存储");
        }
    }

    /// <summary>对应 Go: <c>ContainsInlineMediaDataURL</c>。递归检查 data:image|video|audio 前缀。</summary>
    public static bool ContainsInlineMediaDataURL(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                string text = (value.GetString() ?? "").Trim().ToLowerInvariant();
                return text.StartsWith("data:image/", StringComparison.Ordinal) ||
                       text.StartsWith("data:video/", StringComparison.Ordinal) ||
                       text.StartsWith("data:audio/", StringComparison.Ordinal);
            case JsonValueKind.Array:
                foreach (JsonElement child in value.EnumerateArray())
                {
                    if (ContainsInlineMediaDataURL(child))
                    {
                        return true;
                    }
                }
                break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (ContainsInlineMediaDataURL(property.Value))
                    {
                        return true;
                    }
                }
                break;
        }
        return false;
    }

    // ------------------------------------------------------------ 画布媒体守卫

    /// <summary>
    /// 画布同步的最终服务端不变量：持久化画布只能通过本用户素材指向已上传资源。
    /// 对应 Go: <c>ValidateCanvasMediaAssets</c>。
    /// </summary>
    /// <summary>创作画布提交的媒体资产守卫（公共入口）。</summary>
    public Task ValidateCanvasMediaAssetsPublicAsync(
        string userId, string rawText, CancellationToken cancellationToken = default) =>
        ValidateCanvasMediaAssetsAsync(userId, rawText, cancellationToken);

    private async Task ValidateCanvasMediaAssetsAsync(
        string userId,
        string rawText,
        CancellationToken cancellationToken)
    {
        List<MediaAssetReference> references = MediaAssetReferences(rawText);
        if (references.Count == 0)
        {
            return;
        }

        HashSet<string> assetIDSet = new(StringComparer.Ordinal);
        HashSet<string> resourceIDSet = new(StringComparer.Ordinal);
        foreach (MediaAssetReference reference in references)
        {
            if (reference.AssetID.Length == 0)
            {
                throw AppError.BadAuthRequest("画布媒体尚未进入素材库，请等待同步完成后重试");
            }
            assetIDSet.Add(reference.AssetID);
            resourceIDSet.Add(reference.ResourceID);
        }

        IReadOnlyList<Asset> ownedAssets = await _repository.AssetsForUserIDsAsync(
            userId, SortedIDs(assetIDSet), cancellationToken).ConfigureAwait(false);
        Dictionary<string, HashSet<string>> assetResources = new(StringComparer.Ordinal);
        foreach (Asset asset in ownedAssets)
        {
            assetResources[asset.ID] = DocumentReferencedIDs(asset.PayloadJSON, resourceIDSet);
        }

        IReadOnlyList<Resource> resources = await _repository.ResourcesForUserIDsAsync(
            userId, SortedIDs(resourceIDSet), cancellationToken).ConfigureAwait(false);
        HashSet<string> readyResources = new(StringComparer.Ordinal);
        foreach (Resource resource in resources)
        {
            if (resource.Status == ResourceStatus.ResourceStatusReady)
            {
                readyResources.Add(resource.ID);
            }
        }

        foreach (MediaAssetReference reference in references)
        {
            if (!readyResources.Contains(reference.ResourceID))
            {
                throw AppError.BadAuthRequest("画布媒体对应的云端资源不存在或尚未就绪，请重新上传");
            }
            if (!assetResources.TryGetValue(reference.AssetID, out HashSet<string>? resourceIDs))
            {
                throw AppError.BadAuthRequest("画布媒体尚未进入素材库，请等待同步完成后重试");
            }
            if (!resourceIDs.Contains(reference.ResourceID))
            {
                throw AppError.BadAuthRequest("画布媒体与素材库记录不一致，请重新同步");
            }
        }
    }

    // ------------------------------------------------------------ 存储配额

    /// <summary>对应 Go: <c>canvasHost.StructuredQuota</c> + <c>validateStructuredStorageQuotaWithPolicy</c>。</summary>
    private async Task StructuredQuotaAsync(
        string userId,
        string kind,
        bool creating,
        long deltaBytes,
        CancellationToken cancellationToken)
    {
        RuntimeResourcePolicy policy = _runtimePolicy.Current().Resource;
        Persistence.Repositories.UserStorageUsage usage = await _repository.UserStorageUsageAsync(
            userId, cancellationToken).ConfigureAwait(false);
        long structuredBytes = usage.AssetBytes + usage.CanvasBytes;
        if (structuredBytes + deltaBytes > Megabytes(policy.StructuredDataMB))
        {
            throw AppError.QuotaExceeded(
                $"账号画布和素材数据已达到 {policy.StructuredDataMB}MB 上限，请先删除不需要的内容");
        }
        if (!creating)
        {
            return;
        }
        switch (kind)
        {
            case "asset" when usage.AssetCount >= policy.AssetCount:
                throw AppError.QuotaExceeded($"账号素材数量已达到 {policy.AssetCount} 个上限");
            case "canvas" when usage.CanvasCount >= policy.CanvasCount:
                throw AppError.QuotaExceeded($"账号画布数量已达到 {policy.CanvasCount} 个上限");
        }
    }

    private static long Megabytes(long value) => value * 1024 * 1024;

    // ------------------------------------------------------------ 小工具

    internal static string PayloadString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out JsonElement element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : "";

    /// <summary>对应 Go: <c>parseClientTime</c>。RFC3339 解析失败回落。</summary>
    internal static DateTime ParseClientTime(string value, DateTime fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed))
        {
            return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
        }
        return fallback;
    }

    private static string[] SortedIDs(HashSet<string> values)
    {
        string[] result = [.. values];
        Array.Sort(result, StringComparer.Ordinal);
        return result;
    }

    /// <summary>画布媒体引用。对应 Go: <c>canvas.MediaAssetReference</c>。</summary>
    internal sealed record MediaAssetReference(string AssetID, string ResourceID);

    /// <summary>对应 Go: <c>MediaAssetReferences</c>（nodes + timeline.clips.directMedia）。</summary>
    internal static List<MediaAssetReference> MediaAssetReferences(string rawText)
    {
        List<MediaAssetReference> references = [];
        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(rawText);
        }
        catch (JsonException)
        {
            return references;
        }
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return references;
        }

        if (payload.TryGetProperty("nodes", out JsonElement nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement node in nodes.EnumerateArray())
            {
                string type = NodeString(node, "type");
                if (!IsCanvasMediaKind(type))
                {
                    continue;
                }
                string assetID = "";
                string storageKey = "";
                string content = "";
                if (node.TryGetProperty("metadata", out JsonElement metadata) &&
                    metadata.ValueKind == JsonValueKind.Object)
                {
                    assetID = NodeString(metadata, "assetId");
                    storageKey = NodeString(metadata, "storageKey");
                    content = NodeString(metadata, "content");
                }
                string resourceID = FirstCanvasResourceID(storageKey, content);
                if (resourceID.Length == 0)
                {
                    continue;
                }
                references.Add(new MediaAssetReference(assetID.Trim(), resourceID));
            }
        }

        if (payload.TryGetProperty("timeline", out JsonElement timeline) &&
            timeline.TryGetProperty("clips", out JsonElement clips) &&
            clips.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement clip in clips.EnumerateArray())
            {
                if (!clip.TryGetProperty("directMedia", out JsonElement media) ||
                    media.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string kind = NodeString(media, "kind");
                if (!IsCanvasMediaKind(kind))
                {
                    continue;
                }
                string resourceID = FirstCanvasResourceID(
                    NodeString(media, "storageKey"),
                    NodeString(media, "url"),
                    NodeString(media, "dataUrl"),
                    NodeString(media, "content"));
                if (resourceID.Length == 0)
                {
                    continue;
                }
                references.Add(new MediaAssetReference(NodeString(media, "assetId").Trim(), resourceID));
            }
        }
        return references;
    }

    private static string NodeString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static bool IsCanvasMediaKind(string kind) =>
        kind.Trim().ToLowerInvariant() is "image" or "video" or "audio";

    private static string FirstCanvasResourceID(params string[] values)
    {
        foreach (string value in values)
        {
            string resourceID = ResourceID(value);
            if (resourceID.Length > 0)
            {
                return resourceID;
            }
        }
        return "";
    }

    /// <summary>对应 Go: <c>assets.ResourceID</c>（resource: 前缀或 /api/resources/ 文件 URL）。</summary>
    internal static string ResourceID(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith("resource:", StringComparison.Ordinal))
        {
            return ValidID(trimmed["resource:".Length..]);
        }
        return ValidID(IDFromFileURL(trimmed));
    }

    /// <summary>对应 Go: <c>assets.ValidID</c>（≤80 位字母数字-_）。</summary>
    internal static string ValidID(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 80)
        {
            return "";
        }
        foreach (char ch in trimmed)
        {
            if (!(ch >= 'a' && ch <= 'z') && !(ch >= 'A' && ch <= 'Z') &&
                !(ch >= '0' && ch <= '9') && ch != '-' && ch != '_')
            {
                return "";
            }
        }
        return trimmed;
    }

    /// <summary>对应 Go: <c>assets.IDFromFileURL</c>（取 /api/resources/ 后的首段）。</summary>
    internal static string IDFromFileURL(string value)
    {
        const string prefix = "/api/resources/";
        string trimmed = value.Trim();
        int index = trimmed.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0)
        {
            return "";
        }
        string remainder = trimmed[(index + prefix.Length)..];
        if (remainder.Length == 0)
        {
            return "";
        }
        int end = remainder.IndexOfAny(['/', '?', '#']);
        if (end >= 0)
        {
            remainder = remainder[..end];
        }
        return remainder;
    }

    /// <summary>
    /// 素材 payload 引用的资源集合（与请求里的 resourceID 求交）。
    /// 对应 Go: <c>assets.DocumentReferencedIDs</c>（storageKey/url 等定位字段 + 裸 resourceId 字段）。
    /// </summary>
    internal static HashSet<string> DocumentReferencedIDs(string raw, HashSet<string> resourceIDs)
    {
        HashSet<string> matched = new(StringComparer.Ordinal);
        string trimmed = raw.Trim();
        if (trimmed.Length == 0 || resourceIDs.Count == 0)
        {
            return matched;
        }

        HashSet<string> found = new(StringComparer.Ordinal);
        JsonElement value;
        try
        {
            value = JsonSerializer.Deserialize<JsonElement>(trimmed);
        }
        catch (JsonException)
        {
            string scalar = ResourceID(trimmed);
            if (scalar.Length > 0)
            {
                found.Add(scalar);
            }
            return MatchFound(found, resourceIDs, matched);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string resourceID = ResourceID(value.GetString() ?? "");
            if (resourceID.Length > 0)
            {
                found.Add(resourceID);
            }
        }
        else
        {
            WalkReferenceDocument(value, "", found);
        }
        return MatchFound(found, resourceIDs, matched);
    }

    private static HashSet<string> MatchFound(
        HashSet<string> found, HashSet<string> resourceIDs, HashSet<string> matched)
    {
        foreach (string resourceID in found)
        {
            if (resourceIDs.Contains(resourceID))
            {
                matched.Add(resourceID);
            }
        }
        return matched;
    }

    private static void WalkReferenceDocument(JsonElement value, string parentKey, HashSet<string> resourceIDs)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    WalkReferenceDocument(property.Value, property.Name, resourceIDs);
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement child in value.EnumerateArray())
                {
                    WalkReferenceDocument(child, parentKey, resourceIDs);
                }
                break;
            case JsonValueKind.String:
                string item = value.GetString() ?? "";
                if (IsResourceLocatorField(parentKey))
                {
                    string resourceID = ResourceID(item);
                    if (resourceID.Length > 0)
                    {
                        resourceIDs.Add(resourceID);
                    }
                }
                if (IsBareResourceIDField(parentKey))
                {
                    string validID = ValidID(item);
                    if (validID.Length > 0)
                    {
                        resourceIDs.Add(validID);
                    }
                }
                break;
        }
    }

    private static bool IsBareResourceIDField(string field) =>
        field is "resourceId" or "resourceIds" or "sampleResourceId" or "referenceResourceId" or "referenceResourceIds";

    private static bool IsResourceLocatorField(string field) =>
        field is "storageKey" or "content" or "url" or "dataUrl" or "coverUrl" or "imageUrl" or "videoUrl" or
            "audioUrl" or "referenceUrl" or "referenceUrls" or "artifactRef" or "providerArtifactRef";
}
