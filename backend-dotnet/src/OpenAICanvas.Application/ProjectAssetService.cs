#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>素材来源：本地上传。对应 Go: <c>AssetSourceUploaded</c>。</summary>
public static class AssetSources
{
    public const string Uploaded = "uploaded";
    public const string Canvas = "canvas";
}

/// <summary>链接素材请求。对应 Go: <c>app.LinkProjectAssetRequest</c>。</summary>
public sealed class LinkProjectAssetRequest
{
    [JsonPropertyName("assetId")]
    public string AssetID { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("folderId")]
    public string? FolderID { get; set; }

    /// <summary>媒体导入场景由前端携带原始文件名；已有资产时忽略。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>仅媒体导入合成时生效：<c>uploaded</c>（默认）或 <c>canvas</c>。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";
}

/// <summary>
/// 更新素材请求。对应 Go: <c>app.UpdateProjectAssetRequest</c>。
/// </summary>
/// <remarks>两个字段都是指针：区分「未提交」与「提交空值」。</remarks>
public sealed class UpdateProjectAssetRequest
{
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("folderId")]
    public string? FolderID { get; set; }
}

/// <summary>创建素材版本请求。对应 Go: <c>app.CreateAssetVersionRequest</c>。</summary>
public sealed class CreateAssetVersionRequest
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("definitionJson")]
    public string DefinitionJSON { get; set; } = "";

    [JsonPropertyName("note")]
    public string Note { get; set; } = "";
}

/// <summary>
/// 项目素材摘要。对应 Go: <c>app.ProjectAssetSummary</c>。
/// </summary>
public sealed class ProjectAssetSummaryDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("mediaType")]
    public string MediaType { get; init; } = "";

    [JsonPropertyName("category")]
    public string Category { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("primaryVersionId")]
    [GoOmitEmpty]
    public string PrimaryVersionID { get; init; } = "";

    [JsonPropertyName("versionCount")]
    public long VersionCount { get; init; }

    [JsonPropertyName("usages")]
    public List<string> Usages { get; init; } = [];

    [JsonPropertyName("folderId")]
    [GoOmitEmpty]
    public string FolderID { get; init; } = "";

    [JsonPropertyName("position")]
    public long Position { get; init; }

    [JsonPropertyName("storageKey")]
    [GoOmitEmpty]
    public string StorageKey { get; init; } = "";

    [JsonPropertyName("durationMs")]
    [GoOmitEmpty]
    public long DurationMs { get; init; }

    [JsonPropertyName("previewText")]
    [GoOmitEmpty]
    public string PreviewText { get; init; } = "";

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    [JsonPropertyName("source")]
    [GoOmitEmpty]
    public string Source { get; init; } = "";

    /// <summary>
    /// 角色卡摘要。角色服务属阶段 6 后续节点，未移植前始终为 null
    /// （字段带 omitempty，不会出现在响应里）。
    /// </summary>
    [JsonPropertyName("character")]
    [GoOmitEmpty]
    public object? Character { get; init; }
}

/// <summary>素材列表过滤条件。对应 Go: <c>app.ProjectAssetFilter</c>。</summary>
public sealed class ProjectAssetFilter
{
    public string Category { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string Status { get; set; } = "";
    public string Usage { get; set; } = "";
    public string FolderId { get; set; } = "";
    public string Query { get; set; } = "";
}

/// <summary>
/// 角色卡摘要提供者。
/// </summary>
/// <remarks>
/// 角色与配音属阶段 6 的后续节点（<c>app/project_character.go</c>，585 行）。
/// 未移植前默认实现返回 null，摘要里就不带 <c>character</c> 字段。
/// </remarks>
public interface ICharacterCardProvider
{
    Task<object?> CharacterCardAsync(string userId, Asset asset, CancellationToken cancellationToken = default);
}

/// <summary>默认实现：角色服务未移植，始终不返回角色卡。</summary>
public sealed class NullCharacterCardProvider : ICharacterCardProvider
{
    public Task<object?> CharacterCardAsync(
        string userId, Asset asset, CancellationToken cancellationToken = default) =>
        Task.FromResult<object?>(null);
}

/// <summary>
/// 项目素材关联。对应 Go: <c>internal/app/project_asset.go</c>（不含候选确认）。
/// </summary>
public sealed class ProjectAssetService
{
    private readonly Repository _repository;
    private ICharacterCardProvider _characterCards;

    public ProjectAssetService(Repository repository, ICharacterCardProvider? characterCards = null)
    {
        _repository = repository;
        _characterCards = characterCards ?? new NullCharacterCardProvider();
    }

    /// <summary>
    /// 构造完成后挂接角色卡提供者（角色服务依赖本服务的摘要构建，二者互相引用，
    /// 与 <c>CanvasAuthHost.Attach</c> 的解环方式一致）。
    /// </summary>
    public void AttachCharacterCardProvider(ICharacterCardProvider provider)
    {
        _characterCards = provider;
    }

    // ------------------------------------------------------------ 读取

    /// <summary>项目素材列表（可按分类/媒体类型/状态/用途过滤）。对应 Go: <c>ProjectAssets</c>。</summary>
    public async Task<List<ProjectAssetSummaryDto>> ProjectAssetsAsync(
        string userId, string projectId, ProjectAssetFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Asset> assets = await _repository
            .ProjectAssetsAsync(userId, projectId, cancellationToken).ConfigureAwait(false);

        List<ProjectAssetSummaryDto> result = new(assets.Count);
        foreach (Asset asset in assets)
        {
            ProjectAssetSummaryDto summary = await BuildSummaryAsync(userId, projectId, asset, cancellationToken)
                .ConfigureAwait(false);
            if (!Matches(summary, filter))
            {
                continue;
            }
            result.Add(summary);
        }
        return result;
    }

    /// <summary>
    /// 过滤在内存里做（与 Go 一致）：列表本身已按项目整体取出，
    /// 再逐条按需构建摘要，避免为每种过滤组合写一套 SQL。
    /// </summary>
    private static bool Matches(ProjectAssetSummaryDto summary, ProjectAssetFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.Category.Length > 0 && summary.Category != filter.Category)
        {
            return false;
        }
        if (filter.MediaType.Length > 0 && summary.MediaType != filter.MediaType)
        {
            return false;
        }
        if (filter.Status.Length > 0 && summary.Status != filter.Status)
        {
            return false;
        }
        if (filter.Usage.Length > 0 && !summary.Usages.Contains(filter.Usage, StringComparer.Ordinal))
        {
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------ 链接

    /// <summary>
    /// 把素材加入项目。对应 Go: <c>LinkProjectAsset</c>。
    /// </summary>
    /// <remarks>
    /// 素材可能还不存在：媒体导入只落 <c>resources</c> 表，首次链接时按资源元数据
    /// 合成资产记录，避免「资源存在但无资产记录」导致导入永远失败。
    /// </remarks>
    /// <summary>项目素材分页。对应 Go: <c>ProjectAssetsPage</c>。</summary>
    public async Task<object> ProjectAssetsPageAsync(
        string userId, string projectId, int page, int pageSize, ProjectAssetFilter filter,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Asset> assets = await _repository
            .ProjectAssetsAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectAssetLink> links = await _repository
            .ProjectAssetLinksAsync(projectId, cancellationToken).ConfigureAwait(false);
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

    public async Task<ProjectAssetSummaryDto> LinkProjectAssetAsync(
        string userId, string projectId, LinkProjectAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);

        string assetId = request.AssetID.Trim();
        string source = request.Source.Trim();
        if (source != AssetSources.Canvas)
        {
            source = AssetSources.Uploaded;
        }

        Asset? asset = await _repository.AssetForUserAsync(userId, assetId, cancellationToken)
            .ConfigureAwait(false);

        if (asset is null)
        {
            asset = await AssetFromUploadedResourceAsync(userId, assetId, request.Title, source, cancellationToken)
                .ConfigureAwait(false);
        }

        string category = request.Category.Trim();
        if (category.Length == 0)
        {
            category = asset.Category;
        }
        if (!ValidCategory(category))
        {
            throw AppError.BadAuthRequest("不支持的资产业务分类");
        }

        string folderId = await ResolveFolderAsync(projectId, request.FolderID, cancellationToken)
            .ConfigureAwait(false);

        if (await _repository.ProjectAssetLinkedAsync(projectId, asset.ID, cancellationToken).ConfigureAwait(false))
        {
            // 已链接：幂等返回当前状态。
            return await BuildSummaryAsync(userId, projectId, asset, cancellationToken).ConfigureAwait(false);
        }

        DateTime now = DateTime.UtcNow;
        asset.Category = category;
        if (asset.Status.Length == 0)
        {
            asset.Status = AssetVersionStatus.AssetVersionStatusConfirmed;
        }

        IReadOnlyList<AssetVersion> versions = await _repository
            .AssetVersionsAsync(asset.ID, cancellationToken).ConfigureAwait(false);

        AssetVersion? initialVersion = null;
        if (versions.Count == 0)
        {
            initialVersion = new AssetVersion
            {
                ID = IdGenerator.NewId(),
                AssetID = asset.ID,
                Version = 1,
                Status = asset.Status,
                DefinitionJSON = "{}",
                CreatedAt = now,
                UpdatedAt = now,
            };
            asset.PrimaryVersionID = initialVersion.ID;
        }

        asset.UpdatedAt = now;
        long position = await _repository
            .NextProjectAssetPositionAsync(projectId, folderId, cancellationToken).ConfigureAwait(false);

        ProjectAssetLink link = new()
        {
            ID = IdGenerator.NewId(),
            ProjectID = projectId,
            AssetID = asset.ID,
            FolderID = folderId,
            Position = position,
            CreatedAt = now,
        };

        bool created = await _repository
            .LinkProjectAssetAsync(asset, initialVersion, link, cancellationToken).ConfigureAwait(false);

        if (!created)
        {
            // 并发下被抢先链接：重新读库内资产，返回真实状态。
            Asset current = await _repository.AssetForUserAsync(userId, asset.ID, cancellationToken)
                .ConfigureAwait(false) ?? asset;
            return await BuildSummaryAsync(userId, projectId, current, cancellationToken).ConfigureAwait(false);
        }

        return await BuildSummaryAsync(userId, projectId, asset, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把媒体导入上传的资源合成为资产记录（内存构造，不落库）。
    /// 对应 Go: <c>assetFromUploadedResource</c>。
    /// </summary>
    /// <remarks>
    /// 刻意不在这里预落库：资产创建与项目链接由同一事务提交，
    /// 事务失败时一起回滚，避免留下「有资产无链接」的孤儿记录。
    /// </remarks>
    private async Task<Asset> AssetFromUploadedResourceAsync(
        string userId, string resourceId, string title, string source, CancellationToken cancellationToken)
    {
        Resource resource = await _repository.ResourceForUserAsync(userId, resourceId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("资源不存在");

        if (resource.Kind.Length == 0)
        {
            throw AppError.BadAuthRequest("资源缺少媒体类型，无法导入");
        }

        string payload = JsonSerializer.Serialize(new
        {
            data = new
            {
                storageKey = "resource:" + resource.ID,
                mimeType = resource.MimeType,
                kind = resource.Kind,
                size = resource.Size,
                width = resource.Width,
                height = resource.Height,
                durationMs = resource.DurationMs,
                status = resource.Status,
                mediaType = resource.Kind,
                source,
            },
        });

        title = title.Trim();
        if (title.Length == 0)
        {
            title = MediaTitleFallback(resource);
        }

        return new Asset
        {
            ID = resource.ID,
            UserID = userId,
            Kind = resource.Kind,
            Category = NormalizeCategory("", resource.Kind),
            Status = AssetVersionStatus.AssetVersionStatusConfirmed,
            Title = title,
            PayloadJSON = payload,
            CreatedAt = resource.CreatedAt,
            UpdatedAt = resource.UpdatedAt,
        };
    }

    /// <summary>资源无文件名时的展示标题。对应 Go: <c>mediaTitleFallback</c>。</summary>
    private static string MediaTitleFallback(Resource resource)
    {
        string baseName = "未命名" + resource.Kind;
        if (resource.Width > 0 && resource.Height > 0)
        {
            baseName += $" {resource.Width}x{resource.Height}";
        }
        return baseName;
    }

    // ------------------------------------------------------------ 解除链接

    /// <summary>
    /// 把素材移出项目。对应 Go: <c>UnlinkProjectAsset</c>。
    /// </summary>
    /// <remarks>
    /// 两种引用都会阻止移除：角色素材被项目画布引用（角色卡节点），
    /// 任意素材被镜头引用。必须先解除引用，否则前端会出现悬空引用。
    /// </remarks>
    public async Task UnlinkProjectAssetAsync(
        string userId, string projectId, string assetId, CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);

        Asset asset = await _repository.AssetForUserAsync(userId, assetId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("素材不存在");

        if (asset.Category == AssetCategory.AssetCategoryCharacter)
        {
            IReadOnlyList<CanvasProject> canvases = await _repository
                .ProjectCanvasDocumentsAsync(userId, projectId, cancellationToken).ConfigureAwait(false);

            foreach (CanvasProject canvas in canvases)
            {
                bool referenced;
                try
                {
                    referenced = CanvasReferencesCharacterAsset(canvas.PayloadJSON, assetId);
                }
                catch (JsonException)
                {
                    throw AppError.BadAuthRequest("画布数据格式错误，无法确认角色引用");
                }

                if (referenced)
                {
                    throw AppError.BadAuthRequest("角色仍被项目画布引用，请先删除对应角色卡节点");
                }
            }
        }

        long references = await _repository
            .ProjectAssetShotReferenceCountAsync(projectId, assetId, cancellationToken).ConfigureAwait(false);
        if (references > 0)
        {
            throw AppError.BadAuthRequest("素材仍被项目镜头引用，请先解除镜头用途");
        }

        await _repository.DeleteProjectAssetLinkAsync(projectId, assetId, cancellationToken).ConfigureAwait(false);
        await _repository.BumpProjectRevisionAsync(projectId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 画布是否引用了该角色素材。对应 Go: <c>canvasReferencesCharacterAsset</c>。
    /// </summary>
    /// <remarks>
    /// 只看 <c>workflowKind == "character"</c> 的节点，避免把普通素材节点误判为角色引用。
    /// </remarks>
    private static bool CanvasReferencesCharacterAsset(string payloadJson, string assetId)
    {
        using JsonDocument document = JsonDocument.Parse(payloadJson);

        if (!document.RootElement.TryGetProperty("nodes", out JsonElement nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("metadata", out JsonElement metadata)
                || metadata.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string kind = metadata.TryGetProperty("workflowKind", out JsonElement kindElement)
                ? kindElement.GetString() ?? ""
                : "";
            if (kind != "character")
            {
                continue;
            }

            string referenced = metadata.TryGetProperty("characterAssetId", out JsonElement idElement)
                ? idElement.GetString() ?? ""
                : "";
            if (referenced == assetId)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------ 更新

    /// <summary>
    /// 更新素材分类或所在文件夹。对应 Go: <c>UpdateProjectAsset</c>。
    /// </summary>
    /// <remarks>
    /// 两个字段<b>互斥</b>：一次只能改一个。分类改动会递增项目版本号，
    /// 移动文件夹则由仓储内部一并递增。
    /// </remarks>
    public async Task<ProjectAssetSummaryDto> UpdateProjectAssetAsync(
        string userId, string projectId, string assetId, UpdateProjectAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);

        Asset asset = await _repository.AssetForUserAsync(userId, assetId.Trim(), cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("素材不存在");

        if (!await _repository.ProjectAssetLinkedAsync(projectId, asset.ID, cancellationToken).ConfigureAwait(false))
        {
            throw AppError.BadAuthRequest("素材尚未加入当前项目");
        }

        if (request.Category is not null && request.FolderID is not null)
        {
            throw AppError.BadAuthRequest("一次只能修改素材分类或所在文件夹");
        }

        if (request.Category is not null)
        {
            string category = request.Category.Trim();
            if (!ValidCategory(category))
            {
                throw AppError.BadAuthRequest("不支持的资产业务分类");
            }

            asset.Category = category;
            asset.UpdatedAt = DateTime.UtcNow;
            await _repository.UpdateAssetDomainAsync(asset, cancellationToken).ConfigureAwait(false);
            await _repository.BumpProjectRevisionAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        else if (request.FolderID is not null)
        {
            string folderId = await ResolveFolderAsync(projectId, request.FolderID, cancellationToken)
                .ConfigureAwait(false);
            long position = await _repository
                .NextProjectAssetPositionAsync(projectId, folderId, cancellationToken).ConfigureAwait(false);

            await _repository.MoveProjectAssetAsync(projectId, asset.ID, folderId, position, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            throw AppError.BadAuthRequest("没有可更新的素材字段");
        }

        return await BuildSummaryAsync(userId, projectId, asset, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 版本

    /// <summary>
    /// 新建素材版本并置为主版本（状态回到草稿）。
    /// 对应 Go: <c>CreateProjectAssetVersion</c>。
    /// </summary>
    public async Task<AssetVersion> CreateProjectAssetVersionAsync(
        string userId, string projectId, string assetId, CreateAssetVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);

        Asset asset = await _repository.AssetForUserAsync(userId, assetId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("素材不存在");

        if (!await _repository.ProjectAssetLinkedAsync(projectId, assetId, cancellationToken).ConfigureAwait(false))
        {
            throw AppError.BadAuthRequest("素材尚未加入当前项目");
        }

        IReadOnlyList<AssetVersion> versions = await _repository
            .AssetVersionsAsync(assetId, cancellationToken).ConfigureAwait(false);

        // 列表按版本号倒序，首条即当前最高版本。
        long nextVersion = versions.Count > 0 ? versions[0].Version + 1 : 1;

        string definition = request.DefinitionJSON.Trim();
        if (definition.Length == 0)
        {
            definition = "{}";
        }
        try
        {
            using JsonDocument _ = JsonDocument.Parse(definition);
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("资产版本设定必须是有效 JSON");
        }

        DateTime now = DateTime.UtcNow;
        AssetVersion version = new()
        {
            ID = IdGenerator.NewId(),
            AssetID = assetId,
            Version = nextVersion,
            Status = AssetVersionStatus.AssetVersionStatusDraft,
            DefinitionJSON = definition,
            Prompt = request.Prompt.Trim(),
            Note = request.Note.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _repository.CreateAssetVersionAsync(version, cancellationToken).ConfigureAwait(false);

        asset.PrimaryVersionID = version.ID;
        asset.Status = AssetVersionStatus.AssetVersionStatusDraft;
        asset.UpdatedAt = now;
        await _repository.UpdateAssetDomainAsync(asset, cancellationToken).ConfigureAwait(false);
        await _repository.BumpProjectRevisionAsync(projectId, cancellationToken).ConfigureAwait(false);

        return version;
    }

    // ------------------------------------------------------------ 内部

    private async Task RequireProjectAsync(string userId, string projectId, CancellationToken cancellationToken)
    {
        if (await _repository.ProjectForUserAsync(userId, projectId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw AppError.NotFound("项目不存在");
        }
    }

    /// <summary>
    /// 解析目标文件夹：空值表示「根目录」（空串），否则必须属于当前项目。
    /// 对应 Go: <c>resolveProjectAssetFolderID</c>。
    /// </summary>
    private async Task<string> ResolveFolderAsync(
        string projectId, string? requested, CancellationToken cancellationToken)
    {
        if (requested is null || requested.Trim().Length == 0)
        {
            return "";
        }

        string folderId = requested.Trim();
        if (await _repository.ProjectAssetFolderAsync(projectId, folderId, cancellationToken).ConfigureAwait(false)
            is null)
        {
            throw AppError.BadAuthRequest("目标文件夹不存在或不属于当前素材库");
        }

        return folderId;
    }

    /// <summary>组装素材摘要。对应 Go: <c>projectAssetSummary</c>。</summary>
    /// <remarks>角色卡提供者抛错会向上传播（与 Go 的 projectAssetSummary 一致）。</remarks>
    public async Task<ProjectAssetSummaryDto> BuildSummaryAsync(
        string userId, string projectId, Asset asset, CancellationToken cancellationToken)
    {
        IReadOnlyList<AssetVersion> versions = await _repository
            .AssetVersionsAsync(asset.ID, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> usages = await _repository
            .ProjectAssetUsageRolesAsync(projectId, asset.ID, cancellationToken).ConfigureAwait(false);
        ProjectAssetLink? link = await _repository
            .ProjectAssetLinkAsync(projectId, asset.ID, cancellationToken).ConfigureAwait(false);

        (string storageKey, string previewText, string source, long durationMs) =
            ProjectAssetPreview(asset.PayloadJSON);

        object? character = null;
        if (asset.Category == AssetCategory.AssetCategoryCharacter && asset.PrimaryVersionID.Length > 0)
        {
            character = await _characterCards
                .CharacterCardAsync(userId, asset, cancellationToken).ConfigureAwait(false);
        }

        return new ProjectAssetSummaryDto
        {
            ID = asset.ID,
            Title = asset.Title,
            MediaType = asset.Kind,
            Category = asset.Category,
            Status = asset.Status,
            PrimaryVersionID = asset.PrimaryVersionID,
            VersionCount = versions.Count,
            Usages = usages.ToList(),
            FolderID = link?.FolderID ?? "",
            Position = link?.Position ?? 0,
            StorageKey = storageKey,
            PreviewText = previewText,
            DurationMs = durationMs,
            UpdatedAt = asset.UpdatedAt,
            Source = source,
            Character = character,
        };
    }

    /// <summary>
    /// 从资产载荷里取预览信息。对应 Go: <c>projectAssetPreview</c>。
    /// </summary>
    /// <remarks>
    /// 预览正文按 <b>rune</b> 截到 240 并追加省略号——中文按字符数算，
    /// 不能按 UTF-16 码元，否则会出现半个字符。
    /// </remarks>
    private static (string StorageKey, string PreviewText, string Source, long DurationMs) ProjectAssetPreview(
        string payloadJson)
    {
        JsonElement data;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("data", out JsonElement dataElement)
                || dataElement.ValueKind != JsonValueKind.Object)
            {
                return ("", "", "", 0);
            }

            data = dataElement.Clone();
        }
        catch (JsonException)
        {
            return ("", "", "", 0);
        }

        string storageKey = ReadString(data, "storageKey");
        string content = ReadString(data, "content").Trim();
        string source = ReadString(data, "source");
        long durationMs = data.TryGetProperty("durationMs", out JsonElement duration) && duration.ValueKind == JsonValueKind.Number
            ? duration.GetInt64()
            : 0;

        Rune[] preview = content.EnumerateRunes().ToArray();
        string previewText = preview.Length > 240
            ? string.Concat(preview.Take(240).Select(rune => rune.ToString())) + "…"
            : content;

        return (storageKey.Trim(), previewText, source.Trim(), durationMs);
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>资产业务分类白名单。对应 Go: <c>validAssetCategory</c>。</summary>
    private static bool ValidCategory(string category) => category is
        AssetCategory.AssetCategoryCharacter or
        AssetCategory.AssetCategoryEnvironment or
        AssetCategory.AssetCategoryProp or
        AssetCategory.AssetCategoryMaterial or
        AssetCategory.AssetCategoryOther;

    /// <summary>
    /// 归一化资产业务分类。对应 Go: <c>model.NormalizeAssetCategory</c>。
    /// </summary>
    /// <remarks>
    /// 两级回落：先按显式分类名（含历史别名 <c>wardrobe</c>/<c>weapon</c>/<c>accessory</c>/<c>style</c>），
    /// 再按媒体类型推导。两者都不匹配才落到 <c>other</c>。
    /// </remarks>
    private static string NormalizeCategory(string category, string kind)
    {
        switch (category.Trim().ToLowerInvariant())
        {
            case AssetCategory.AssetCategoryCharacter:
                return AssetCategory.AssetCategoryCharacter;
            case AssetCategory.AssetCategoryEnvironment:
                return AssetCategory.AssetCategoryEnvironment;
            case AssetCategory.AssetCategoryProp or "wardrobe" or "weapon" or "accessory":
                return AssetCategory.AssetCategoryProp;
            case AssetCategory.AssetCategoryMaterial or "style":
                return AssetCategory.AssetCategoryMaterial;
            case AssetCategory.AssetCategoryOther:
                return AssetCategory.AssetCategoryOther;
        }

        return kind.Trim().ToLowerInvariant() switch
        {
            "entity" => AssetCategory.AssetCategoryCharacter,
            "image" or "video" or "audio" or "model" => AssetCategory.AssetCategoryMaterial,
            _ => AssetCategory.AssetCategoryOther,
        };
    }
}
