#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>创建镜头请求。对应 Go: <c>app.CreateProjectShotRequest</c>。</summary>
public sealed class CreateProjectShotRequest
{
    /// <summary>客户端幂等 ID：非空时走 upsert 更新分支。</summary>
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("unitId")]
    public string UnitID { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("position")]
    public long Position { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("revision")]
    public ShotRevisionInput Revision { get; set; } = new();
}

/// <summary>分镜脚本版本输入。对应 Go: <c>app.ShotRevisionInput</c>。</summary>
public sealed class ShotRevisionInput
{
    [JsonPropertyName("plotDescription")]
    public string PlotDescription { get; set; } = "";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("dialogue")]
    public string Dialogue { get; set; } = "";

    [JsonPropertyName("shotSize")]
    public string ShotSize { get; set; } = "";

    [JsonPropertyName("cameraAngle")]
    public string CameraAngle { get; set; } = "";

    [JsonPropertyName("cameraMovement")]
    public string CameraMovement { get; set; } = "";

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("imagePrompt")]
    public string ImagePrompt { get; set; } = "";

    [JsonPropertyName("videoPrompt")]
    public string VideoPrompt { get; set; } = "";

    [JsonPropertyName("negativePrompt")]
    public string NegativePrompt { get; set; } = "";

    [JsonPropertyName("continuityNotes")]
    public string ContinuityNotes { get; set; } = "";

    [JsonPropertyName("actionBeats")]
    public Dictionary<string, JsonElement>[]? ActionBeats { get; set; }
}

/// <summary>章节分镜整体替换输入。对应 Go: <c>app.ReplaceProjectUnitShotInput</c>。</summary>
public sealed class ReplaceProjectUnitShotInput
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("unitId")]
    public string UnitID { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("position")]
    public long Position { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("revision")]
    public ShotRevisionInput Revision { get; set; } = new();

    [JsonPropertyName("assetVersionIds")]
    public List<string>? AssetVersionIDs { get; set; }
}

/// <summary>章节分镜整体替换请求。对应 Go: <c>app.ReplaceProjectUnitShotsRequest</c>。</summary>
public sealed class ReplaceProjectUnitShotsRequest
{
    [JsonPropertyName("shots")]
    public List<ReplaceProjectUnitShotInput> Shots { get; set; } = [];

    [JsonPropertyName("expectedShotIds")]
    public List<string>? ExpectedShotIDs { get; set; }
}

/// <summary>镜头资产关联请求。对应 Go: <c>app.LinkShotAssetRequest</c>。</summary>
public sealed class LinkShotAssetRequest
{
    [JsonPropertyName("assetVersionId")]
    public string AssetVersionID { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";
}

/// <summary>资产候选输入。对应 Go: <c>app.AssetCandidateInput</c>。</summary>
public sealed class AssetCandidateInput
{
    [JsonPropertyName("unitId")]
    public string UnitID { get; set; } = "";

    [JsonPropertyName("shotId")]
    public string ShotID { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("details")]
    public Dictionary<string, JsonElement>? Details { get; set; }
}

/// <summary>批量创建候选请求。对应 Go: <c>app.CreateAssetCandidatesRequest</c>。</summary>
public sealed class CreateAssetCandidatesRequest
{
    [JsonPropertyName("candidates")]
    public List<AssetCandidateInput> Candidates { get; set; } = [];

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";
}

/// <summary>确认候选请求。对应 Go: <c>app.ConfirmProjectAssetCandidateRequest</c>。</summary>
public sealed class ConfirmProjectAssetCandidateRequest
{
    [JsonPropertyName("assetId")]
    public string AssetID { get; set; } = "";
}

/// <summary>候选分页。对应 Go: <c>app.ProjectAssetCandidatePage</c>。</summary>
public sealed class ProjectAssetCandidatePageDto
{
    [JsonPropertyName("candidates")]
    public List<ProjectAssetCandidate> Candidates { get; init; } = [];

    [JsonPropertyName("page")]
    public long Page { get; init; }

    [JsonPropertyName("pageSize")]
    public long PageSize { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}

/// <summary>
/// 分镜与资产候选。对应 Go: <c>internal/app/project_shot.go</c> 与
/// <c>project_asset.go</c> 的候选确认部分。
/// </summary>
public sealed class ProjectShotService
{
    /// <summary>章节角色提取流程的候选来源。对应 Go: <c>assetCandidateSourceChapterCharacter</c>。</summary>
    private const string SourceChapterCharacter = "chapter_character_extract";

    private static readonly HashSet<string> ValidShotStatuses = new(StringComparer.Ordinal)
    {
        "draft", "ready", "running", "review", "completed", "failed",
    };

    private static readonly HashSet<string> ValidShotAssetRoles = new(StringComparer.Ordinal)
    {
        "reference", "start_frame", "end_frame", "keyframe", "storyboard", "output",
    };

    private static readonly string[] ValidCandidateCategories =
    [
        AssetCategory.AssetCategoryCharacter,
        AssetCategory.AssetCategoryEnvironment,
        AssetCategory.AssetCategoryProp,
        AssetCategory.AssetCategoryMaterial,
        AssetCategory.AssetCategoryOther,
    ];

    private readonly Repository _repository;
    private readonly ProjectCharacterService _projectCharacters;
    private readonly ProjectAssetService _projectAssets;

    public ProjectShotService(
        Repository repository,
        ProjectCharacterService projectCharacters,
        ProjectAssetService projectAssets)
    {
        _repository = repository;
        _projectCharacters = projectCharacters;
        _projectAssets = projectAssets;
    }

    // ------------------------------------------------------------ 镜头

    /// <summary>创建/更新镜头（带新版本）。对应 Go: <c>CreateProjectShot</c>。</summary>
    public async Task<Shot> CreateAsync(
        string userId, string projectId, CreateProjectShotRequest request, CancellationToken cancellationToken = default)
    {
        await ActiveProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        string unitId = request.UnitID.Trim();
        if (unitId.Length > 0)
        {
            await RequireUnitAsync(projectId, unitId, cancellationToken).ConfigureAwait(false);
        }
        string title = request.Title.Trim();
        if (title.Length == 0)
        {
            throw AppError.BadAuthRequest("镜头标题不能为空");
        }
        if (request.Position < 0 || request.DurationMs < 0)
        {
            throw AppError.BadAuthRequest("镜头顺序和时长不能为负数");
        }

        DateTime now = DateTime.UtcNow;
        string shotId = request.ID.Trim();
        bool create = shotId.Length == 0;
        string status = request.Status.Trim();
        if (create)
        {
            shotId = IdGenerator.NewId();
            if (status.Length == 0)
            {
                status = "draft";
            }
        }
        else
        {
            Shot existing = await RequireShotAsync(projectId, shotId, cancellationToken).ConfigureAwait(false);
            if (unitId.Length == 0)
            {
                unitId = existing.UnitID;
            }
            if (status.Length == 0)
            {
                status = existing.Status;
            }
            now = existing.CreatedAt;
        }
        if (!ValidShotStatuses.Contains(status))
        {
            throw AppError.BadAuthRequest("不支持的镜头状态");
        }

        DateTime updatedAt = DateTime.UtcNow;
        string description = request.Description.Trim();
        ShotRevision revision = NewRevision(
            userId, shotId, request.Revision, description, request.DurationMs, updatedAt);
        Shot shot = new()
        {
            ID = shotId,
            ProjectID = projectId,
            UnitID = unitId,
            CurrentRevisionID = revision.ID,
            Title = title,
            Description = revision.PlotDescription,
            Position = request.Position,
            DurationMs = revision.DurationMs,
            Status = status,
            CreatedAt = now,
            UpdatedAt = updatedAt,
        };
        await _repository.SaveShotWithRevisionAsync(shot, revision, create, cancellationToken).ConfigureAwait(false);
        return shot;
    }

    /// <summary>章节分镜整体替换。对应 Go: <c>ReplaceProjectUnitShots</c>。</summary>
    public async Task<List<Shot>> ReplaceUnitShotsAsync(
        string userId,
        string projectId,
        string unitId,
        ReplaceProjectUnitShotsRequest request,
        CancellationToken cancellationToken = default)
    {
        await ActiveProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        unitId = unitId.Trim();
        await RequireUnitAsync(projectId, unitId, cancellationToken).ConfigureAwait(false);
        if (request.Shots.Count == 0 || request.Shots.Count > 200)
        {
            throw AppError.BadAuthRequest("章节分镜数量必须在 1 到 200 之间");
        }

        DateTime now = DateTime.UtcNow;
        List<Shot> shots = new(request.Shots.Count);
        List<ShotRevision> revisions = new(request.Shots.Count);
        List<ShotAssetReference> references = [];
        for (int position = 0; position < request.Shots.Count; position++)
        {
            ReplaceProjectUnitShotInput input = request.Shots[position];
            string title = input.Title.Trim();
            string description = input.Description.Trim();
            if (title.Length == 0 || description.Length == 0 || input.DurationMs < 0)
            {
                throw AppError.BadAuthRequest("分镜标题、描述或时长无效");
            }
            string shotId = IdGenerator.NewId();
            ShotRevision revision = NewRevision(
                userId, shotId, input.Revision, description, input.DurationMs, now);
            revision.Version = 1;
            shots.Add(new Shot
            {
                ID = shotId,
                ProjectID = projectId,
                UnitID = unitId,
                CurrentRevisionID = revision.ID,
                Title = title,
                Description = revision.PlotDescription,
                Position = position,
                DurationMs = revision.DurationMs,
                Status = "draft",
                CreatedAt = now,
                UpdatedAt = now,
            });
            revisions.Add(revision);

            HashSet<string> seenVersions = new(StringComparer.Ordinal);
            foreach (string rawVersionId in input.AssetVersionIDs ?? [])
            {
                string versionId = rawVersionId.Trim();
                if (versionId.Length == 0 || !seenVersions.Add(versionId))
                {
                    continue;
                }
                if (seenVersions.Count > 6)
                {
                    throw AppError.BadAuthRequest("单个分镜最多引用 6 个资产版本");
                }
                if (await _repository.AssetVersionForProjectAsync(projectId, versionId, cancellationToken)
                        .ConfigureAwait(false) is null)
                {
                    // Go 返回原始 not-found 错误 → failInternal（500）。
                    throw new InvalidOperationException("record not found");
                }
                references.Add(new ShotAssetReference
                {
                    ID = IdGenerator.NewId(),
                    ShotID = shotId,
                    AssetVersionID = versionId,
                    Role = "reference",
                    Status = "linked",
                    CreatedAt = now,
                });
            }
        }

        await _repository.ReplaceProjectUnitShotsAsync(
            projectId, unitId, shots, revisions, references, request.ExpectedShotIDs, cancellationToken)
            .ConfigureAwait(false);
        return shots;
    }

    /// <summary>为镜头追加新版本。对应 Go: <c>CreateShotRevision</c>。</summary>
    public async Task<(Shot Shot, ShotRevision Revision)> CreateRevisionAsync(
        string userId, string projectId, string shotId, ShotRevisionInput input, CancellationToken cancellationToken = default)
    {
        await ActiveProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        Shot shot = await RequireShotAsync(projectId, shotId.Trim(), cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        ShotRevision revision = NewRevision(
            userId, shot.ID, input, shot.Description, shot.DurationMs, now);
        shot.Description = revision.PlotDescription;
        shot.DurationMs = revision.DurationMs;
        shot.Status = "draft";
        shot.UpdatedAt = now;
        await _repository.SaveShotWithRevisionAsync(shot, revision, create: false, cancellationToken).ConfigureAwait(false);
        return (shot, revision);
    }

    /// <summary>删除镜头。对应 Go: <c>DeleteProjectShot</c>。</summary>
    public async Task DeleteAsync(
        string userId, string projectId, string shotId, CancellationToken cancellationToken = default)
    {
        await ActiveProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        shotId = shotId.Trim();
        if (shotId.Length == 0)
        {
            throw AppError.BadAuthRequest("镜头不能为空");
        }
        await RequireShotAsync(projectId, shotId, cancellationToken).ConfigureAwait(false);
        await _repository.DeleteProjectShotAsync(projectId, shotId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 镜头资产引用

    /// <summary>关联镜头资产版本。对应 Go: <c>LinkShotAsset</c>。</summary>
    public async Task<ShotAssetReference> LinkAssetAsync(
        string userId, string projectId, string shotId, LinkShotAssetRequest request, CancellationToken cancellationToken = default)
    {
        await ActiveProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        await RequireShotAsync(projectId, shotId, cancellationToken).ConfigureAwait(false);
        string versionId = request.AssetVersionID.Trim();
        if (await _repository.AssetVersionForProjectAsync(projectId, versionId, cancellationToken).ConfigureAwait(false)
            is null)
        {
            throw new InvalidOperationException("record not found");
        }
        string role = request.Role.Trim();
        if (!ValidShotAssetRoles.Contains(role))
        {
            throw AppError.BadAuthRequest("不支持的镜头素材用途");
        }
        DateTime now = DateTime.UtcNow;
        ShotAssetReference reference = new()
        {
            ID = IdGenerator.NewId(),
            ShotID = shotId,
            AssetVersionID = versionId,
            Role = role,
            Status = "linked",
            CreatedAt = now,
        };
        await _repository.UpsertShotAssetReferenceAndInvalidateAsync(projectId, reference, now, cancellationToken)
            .ConfigureAwait(false);
        return reference;
    }

    /// <summary>解除镜头资产引用。对应 Go: <c>UnlinkShotAsset</c>。</summary>
    public async Task UnlinkAssetAsync(
        string userId, string projectId, string shotId, string referenceId, CancellationToken cancellationToken = default)
    {
        await ActiveProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        await RequireShotAsync(projectId, shotId, cancellationToken).ConfigureAwait(false);
        referenceId = referenceId.Trim();
        if (referenceId.Length == 0)
        {
            throw AppError.BadAuthRequest("镜头资产引用不能为空");
        }
        bool deleted = await _repository.DeleteShotAssetReferenceAndInvalidateAsync(
            projectId, shotId, referenceId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            throw AppError.NotFound("镜头资产引用不存在");
        }
    }

    // ------------------------------------------------------------ 资产候选

    /// <summary>批量创建资产候选（按名称与别名去重）。对应 Go: <c>CreateProjectAssetCandidates</c>。</summary>
    public async Task<List<ProjectAssetCandidate>> CreateCandidatesAsync(
        string userId, string projectId, CreateAssetCandidatesRequest request, CancellationToken cancellationToken = default)
    {
        await ActiveProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        if (request.Candidates.Count == 0 || request.Candidates.Count > 100)
        {
            throw AppError.BadAuthRequest("资产候选数量必须在 1 到 100 之间");
        }
        string source = request.Source.Trim();
        IReadOnlyList<ProjectAssetCandidate> existingCandidates = await _repository
            .ProjectAssetCandidatesAsync(projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Asset> projectAssets = await _repository
            .ProjectAssetsAsync(userId, projectId, cancellationToken: cancellationToken).ConfigureAwait(false);
        HashSet<string> knownKeys = CandidateIdentityKeys(existingCandidates, projectAssets);

        DateTime now = DateTime.UtcNow;
        List<ProjectAssetCandidate> candidates = [];
        foreach (AssetCandidateInput input in request.Candidates)
        {
            string name = input.Name.Trim();
            string nameKey = AssetCandidateNameKey(name);
            string category = input.Category.Trim();
            if (name.Length == 0 || nameKey.Length == 0 || !ValidCandidateCategories.Contains(category))
            {
                throw AppError.BadAuthRequest("资产候选名称或分类无效");
            }
            if (category == AssetCategory.AssetCategoryCharacter && source != SourceChapterCharacter)
            {
                throw AppError.BadAuthRequest("角色候选只能从剧情章节的角色提取流程创建");
            }
            if (category == AssetCategory.AssetCategoryCharacter && input.UnitID.Trim().Length == 0)
            {
                throw AppError.BadAuthRequest("角色候选必须关联剧情章节");
            }
            if (input.UnitID.Trim().Length > 0)
            {
                await RequireUnitAsync(projectId, input.UnitID.Trim(), cancellationToken).ConfigureAwait(false);
            }
            if (input.ShotID.Trim().Length > 0)
            {
                await RequireShotAsync(projectId, input.ShotID.Trim(), cancellationToken).ConfigureAwait(false);
            }
            string detailsJson = MarshalDetails(input.Details);
            if (category == AssetCategory.AssetCategoryCharacter)
            {
                ValidateCharacterCandidateDetails(input.Details);
            }
            List<string> identityKeys = CandidateInputIdentityKeys(name, input.Details);
            if (CandidateIdentityExists(knownKeys, category, identityKeys))
            {
                continue;
            }
            ProjectAssetCandidate candidate = new()
            {
                ID = IdGenerator.NewId(),
                ProjectID = projectId,
                UnitID = input.UnitID.Trim(),
                ShotID = input.ShotID.Trim(),
                Name = name,
                NameKey = nameKey,
                Category = category,
                Status = "pending_confirmation",
                Source = source,
                DetailsJSON = detailsJson,
                CreatedAt = now,
                UpdatedAt = now,
            };
            bool inserted = await _repository.CreateProjectAssetCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (!inserted)
            {
                continue;
            }
            candidates.Add(candidate);
            AddCandidateIdentityKeys(knownKeys, category, identityKeys);
        }
        if (candidates.Count > 0)
        {
            await _repository.BumpProjectRevisionAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        return candidates;
    }

    /// <summary>候选分页。对应 Go: <c>ProjectAssetCandidatesPage</c>（service 层）。</summary>
    public async Task<ProjectAssetCandidatePageDto> CandidatesPageAsync(
        string userId,
        string projectId,
        long page,
        long pageSize,
        string unitId,
        string status,
        string category,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (await _repository.ProjectForUserAsync(userId, projectId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }
        (page, pageSize) = NormalizePage(page, pageSize, 200);
        (IReadOnlyList<ProjectAssetCandidate> candidates, long total) = await _repository
            .ProjectAssetCandidatesPageAsync(projectId, page, pageSize, unitId, status, category, query, cancellationToken)
            .ConfigureAwait(false);
        return new ProjectAssetCandidatePageDto
        {
            Candidates = candidates.ToList(),
            Page = page,
            PageSize = pageSize,
            Total = total,
            HasMore = page * pageSize < total,
        };
    }

    /// <summary>确认资产候选并落成正式素材。对应 Go: <c>ConfirmProjectAssetCandidate</c>。</summary>
    public async Task<ProjectAssetSummaryDto> ConfirmCandidateAsync(
        string userId,
        string projectId,
        string candidateId,
        ConfirmProjectAssetCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await _repository.ProjectForUserAsync(userId, projectId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }
        ProjectAssetCandidate? candidate = await _repository
            .ProjectAssetCandidateAsync(projectId, candidateId, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            throw new InvalidOperationException("record not found");
        }
        if (candidate.Status != "pending_confirmation")
        {
            throw AppError.BadAuthRequest("资产候选已处理");
        }

        DateTime now = DateTime.UtcNow;
        string assetId = request.AssetID.Trim();
        if (assetId.Length > 0 && candidate.Category == AssetCategory.AssetCategoryCharacter)
        {
            // 角色候选：把候选设定并入现有角色的下一个版本。
            Asset? asset = await _repository
                .ProjectCharacterAssetAsync(userId, projectId, assetId, cancellationToken).ConfigureAwait(false);
            if (asset is null)
            {
                throw new InvalidOperationException("record not found");
            }
            AssetVersion? current = await _repository
                .AssetVersionAsync(asset.PrimaryVersionID, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                throw new InvalidOperationException("record not found");
            }
            string definition = ProjectCharacterService.MergeCharacterCandidateDefinition(
                current.DefinitionJSON, candidate.DetailsJSON, asset.Title, candidate.Name);
            (Asset nextAsset, AssetVersion nextVersion, List<AssetRepresentation> representations, CharacterVoiceBinding? voice) =
                await _projectCharacters.PrepareNextVersionAsync(
                    asset, asset.Title, definition, null, null, dropVoice: false, cancellationToken).ConfigureAwait(false);
            candidate.Status = "confirmed";
            candidate.ResolvedAssetID = asset.ID;
            candidate.UpdatedAt = now;
            await _repository.ConfirmProjectCharacterCandidateAsync(
                candidate, nextAsset, nextVersion, representations, voice, cancellationToken).ConfigureAwait(false);
            return await _projectAssets.BuildSummaryAsync(userId, projectId, nextAsset, cancellationToken)
                .ConfigureAwait(false);
        }

        bool createAsset = assetId.Length == 0;
        Asset finalAsset;
        AssetVersion version = new();
        if (createAsset)
        {
            assetId = IdGenerator.NewId();
            string versionId = IdGenerator.NewId();
            string kind = "text";
            string payload;
            if (candidate.Category == AssetCategory.AssetCategoryCharacter)
            {
                kind = "entity";
                payload = ProjectCharacterService.CharacterAssetPayload(
                    assetId, versionId, candidate.Name, candidate.DetailsJSON, now, now);
            }
            else
            {
                Dictionary<string, JsonElement> data = new(StringComparer.Ordinal)
                {
                    ["content"] = JsonSerializer.SerializeToElement(""),
                };
                Dictionary<string, JsonElement> payloadMap = new(StringComparer.Ordinal)
                {
                    ["category"] = JsonSerializer.SerializeToElement(candidate.Category),
                    ["createdAt"] = JsonSerializer.SerializeToElement(ProjectService.FormatRfc3339Nano(now)),
                    ["data"] = JsonSerializer.SerializeToElement(data),
                    ["id"] = JsonSerializer.SerializeToElement(assetId),
                    ["kind"] = JsonSerializer.SerializeToElement(kind),
                    ["primaryVersionId"] = JsonSerializer.SerializeToElement(versionId),
                    ["status"] = JsonSerializer.SerializeToElement(AssetVersionStatus.AssetVersionStatusConfirmed),
                    ["tags"] = JsonSerializer.SerializeToElement(Array.Empty<string>()),
                    ["title"] = JsonSerializer.SerializeToElement(candidate.Name),
                    ["updatedAt"] = JsonSerializer.SerializeToElement(ProjectService.FormatRfc3339Nano(now)),
                };
                payload = JsonSerializer.Serialize(payloadMap, ProjectCharacterService.GoPayloadOptions);
            }
            finalAsset = new Asset
            {
                ID = assetId,
                UserID = userId,
                Kind = kind,
                Category = candidate.Category,
                Status = AssetVersionStatus.AssetVersionStatusConfirmed,
                PrimaryVersionID = versionId,
                Title = candidate.Name,
                PayloadJSON = payload,
                CreatedAt = now,
                UpdatedAt = now,
            };
            version = new AssetVersion
            {
                ID = versionId,
                AssetID = assetId,
                Version = 1,
                Status = AssetVersionStatus.AssetVersionStatusConfirmed,
                DefinitionJSON = candidate.DetailsJSON,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }
        else
        {
            Asset? existing = await _repository
                .AssetForUserAsync(userId, assetId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                throw new InvalidOperationException("record not found");
            }
            finalAsset = existing;
        }

        candidate.Status = "confirmed";
        candidate.ResolvedAssetID = assetId;
        candidate.UpdatedAt = now;
        string folderId = "";
        long position = await _repository
            .NextProjectAssetPositionAsync(projectId, folderId, cancellationToken).ConfigureAwait(false);
        ProjectAssetLink link = new()
        {
            ID = IdGenerator.NewId(),
            ProjectID = projectId,
            AssetID = assetId,
            FolderID = folderId,
            Position = position,
            CreatedAt = now,
        };
        await _repository.ConfirmProjectAssetCandidateAsync(
            candidate, finalAsset, version, link, createAsset, cancellationToken).ConfigureAwait(false);
        return await _projectAssets.BuildSummaryAsync(userId, projectId, finalAsset, cancellationToken)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 内部

    private async Task ActiveProjectAsync(string userId, string projectId, CancellationToken cancellationToken)
    {
        Project? project = await _repository
            .ProjectForUserAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            // Go 的 ProjectForUser 未命中 → 原始 not-found 错误 → failInternal（500）。
            throw new InvalidOperationException("record not found");
        }
        if (project.Status == ProjectStatus.ProjectStatusArchived)
        {
            throw AppError.BadAuthRequest("项目已归档，不能修改短剧生产数据");
        }
    }

    private async Task RequireUnitAsync(string projectId, string unitId, CancellationToken cancellationToken)
    {
        if (await _repository.ProjectUnitAsync(projectId, unitId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }
    }

    private async Task<Shot> RequireShotAsync(string projectId, string shotId, CancellationToken cancellationToken)
    {
        Shot? shot = await _repository.ShotForProjectAsync(projectId, shotId, cancellationToken).ConfigureAwait(false);
        if (shot is null)
        {
            throw new InvalidOperationException("record not found");
        }
        return shot;
    }

    /// <summary>构造新版本（版本号在仓储事务内取 MAX+1）。对应 Go: <c>newShotRevision</c>。</summary>
    private static ShotRevision NewRevision(
        string userId,
        string shotId,
        ShotRevisionInput input,
        string fallbackDescription,
        long fallbackDuration,
        DateTime now)
    {
        string plotDescription = input.PlotDescription.Trim();
        if (plotDescription.Length == 0)
        {
            plotDescription = fallbackDescription.Trim();
        }
        if (plotDescription.Length == 0)
        {
            throw AppError.BadAuthRequest("镜头画面描述不能为空");
        }
        long durationMs = input.DurationMs;
        if (durationMs == 0)
        {
            durationMs = fallbackDuration;
        }
        if (durationMs < 0)
        {
            throw AppError.BadAuthRequest("镜头时长不能为负数");
        }
        string actionBeatsJson = "[]";
        if (input.ActionBeats is not null)
        {
            actionBeatsJson = JsonSerializer.Serialize(
                ProjectCharacterService.SortedElement(
                    JsonSerializer.SerializeToElement(input.ActionBeats)),
                ProjectCharacterService.GoPayloadOptions);
        }
        return new ShotRevision
        {
            ID = IdGenerator.NewId(),
            ShotID = shotId,
            PlotDescription = plotDescription,
            Action = input.Action.Trim(),
            Dialogue = input.Dialogue.Trim(),
            ShotSize = input.ShotSize.Trim(),
            CameraAngle = input.CameraAngle.Trim(),
            CameraMovement = input.CameraMovement.Trim(),
            DurationMs = durationMs,
            ImagePrompt = input.ImagePrompt.Trim(),
            VideoPrompt = input.VideoPrompt.Trim(),
            NegativePrompt = input.NegativePrompt.Trim(),
            ContinuityNotes = input.ContinuityNotes.Trim(),
            ActionBeatsJSON = actionBeatsJson,
            CreatedBy = userId,
            CreatedAt = now,
        };
    }

    /// <summary>Go: <c>normalizeProjectPage</c>。page&lt;1→1；pageSize&lt;1→40；上限 max。</summary>
    private static (long Page, long PageSize) NormalizePage(long page, long pageSize, long max)
    {
        if (page < 1)
        {
            page = 1;
        }
        if (pageSize < 1)
        {
            pageSize = 40;
        }
        if (pageSize > max)
        {
            pageSize = max;
        }
        return (page, pageSize);
    }

    /// <summary>Go: <c>model.AssetCandidateNameKey</c>。保留字母与数字并转小写，其余字符剔除。</summary>
    internal static string AssetCandidateNameKey(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "";
        }
        return string.Concat(trimmed
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant));
    }

    /// <summary>候选详情序列化。对应 Go: <c>marshalProjectDetails</c>（nil → <c>{}</c>，键序 Ordinal）。</summary>
    private static string MarshalDetails(Dictionary<string, JsonElement>? details)
    {
        if (details is null)
        {
            return "{}";
        }
        return JsonSerializer.Serialize(
            ProjectCharacterService.SortedElement(JsonSerializer.SerializeToElement(details)),
            ProjectCharacterService.GoPayloadOptions);
    }

    /// <summary>角色候选详情画像校验。对应 Go: <c>validateCharacterCandidateDetails</c>。</summary>
    private static void ValidateCharacterCandidateDetails(Dictionary<string, JsonElement>? details)
    {
        string Text(string key) =>
            details is not null
            && details.TryGetValue(key, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? (value.GetString() ?? "").Trim()
                : "";

        int descriptiveCount = 0;
        foreach (string key in new[]
                 {
                     "appearance", "clothing", "physique", "personality", "consistencyPrompt", "multiViewPrompt",
                 })
        {
            if (Text(key).Length > 0)
            {
                descriptiveCount++;
            }
        }
        if (Text("role").Length == 0
            || descriptiveCount < 3
            || Text("voiceLanguage").Length == 0
            || Text("voiceAge").Length == 0
            || Text("voiceTimbre").Length == 0)
        {
            throw AppError.BadAuthRequest("角色候选必须包含剧情定位、稳定设定和声音画像");
        }
    }

    /// <summary>已知身份键集合（既有候选 + 项目素材）。对应 Go: <c>projectAssetCandidateIdentityKeys</c>。</summary>
    private static HashSet<string> CandidateIdentityKeys(
        IReadOnlyList<ProjectAssetCandidate> candidates, IReadOnlyList<Asset> assets)
    {
        HashSet<string> known = new(StringComparer.Ordinal);
        foreach (ProjectAssetCandidate candidate in candidates)
        {
            List<string> keys = [candidate.NameKey];
            if (candidate.DetailsJSON.Trim().Length > 0)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(candidate.DetailsJSON);
                    keys.AddRange(CandidateAliases(document.RootElement));
                }
                catch (JsonException)
                {
                    // Go 的 json.Unmarshal 失败时跳过别名。
                }
            }
            AddCandidateIdentityKeys(known, candidate.Category, keys);
        }
        foreach (Asset asset in assets)
        {
            List<string> keys = [AssetCandidateNameKey(asset.Title)];
            if (asset.Category == AssetCategory.AssetCategoryCharacter)
            {
                keys.AddRange(CharacterAssetAliasKeys(asset.PayloadJSON));
            }
            AddCandidateIdentityKeys(known, asset.Category, keys);
        }
        return known;
    }

    private static List<string> CandidateInputIdentityKeys(
        string name, Dictionary<string, JsonElement>? details) =>
        [AssetCandidateNameKey(name), .. CandidateAliasesElement(details)];

    /// <summary>details.aliases 的身份键。对应 Go: <c>assetCandidateAliases</c>。</summary>
    private static List<string> CandidateAliases(JsonElement details)
    {
        List<string> keys = [];
        if (details.ValueKind != JsonValueKind.Object
            || !details.TryGetProperty("aliases", out JsonElement values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return keys;
        }
        foreach (JsonElement value in values.EnumerateArray())
        {
            string printable = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => "",
            };
            string key = AssetCandidateNameKey(printable);
            if (key.Length > 0)
            {
                keys.Add(key);
            }
        }
        return keys;
    }

    private static List<string> CandidateAliasesElement(Dictionary<string, JsonElement>? details)
    {
        if (details is null)
        {
            return [];
        }
        return CandidateAliases(JsonSerializer.SerializeToElement(details));
    }

    /// <summary>角色素材载荷里的别名键。对应 Go: <c>characterAssetAliasKeys</c>。</summary>
    private static List<string> CharacterAssetAliasKeys(string payloadJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("data", out JsonElement data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("definition", out JsonElement definition))
            {
                return CandidateAliases(definition);
            }
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool CandidateIdentityExists(HashSet<string> known, string category, List<string> keys)
    {
        foreach (string key in keys)
        {
            if (known.Contains(category + ":" + key))
            {
                return true;
            }
        }
        return false;
    }

    private static void AddCandidateIdentityKeys(HashSet<string> known, string category, List<string> keys)
    {
        foreach (string key in keys)
        {
            if (key.Length > 0)
            {
                known.Add(category + ":" + key);
            }
        }
    }
}
