#nullable enable
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>创建角色请求。对应 Go: <c>app.CreateProjectCharacterRequest</c>。</summary>
public sealed class CreateProjectCharacterRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>角色设定。缺省或 null 归一化为 <c>{}</c>；非对象在绑定阶段被拒绝（400）。</summary>
    [JsonPropertyName("definition")]
    public Dictionary<string, JsonElement>? Definition { get; set; }
}

/// <summary>更新角色请求。对应 Go: <c>app.UpdateProjectCharacterRequest</c>。</summary>
public sealed class UpdateProjectCharacterRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("definition")]
    public Dictionary<string, JsonElement>? Definition { get; set; }
}

/// <summary>角色形象输入。对应 Go: <c>app.CharacterRepresentationInput</c>。</summary>
public sealed class CharacterRepresentationInput
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("resourceId")]
    public string ResourceID { get; set; } = "";

    /// <summary>任意元数据；缺省序列化为 <c>null</c>（Go 的 <c>any(nil)</c>）。</summary>
    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; set; }
}

/// <summary>整体替换角色形象请求。对应 Go: <c>app.ReplaceCharacterRepresentationsRequest</c>。</summary>
public sealed class ReplaceCharacterRepresentationsRequest
{
    [JsonPropertyName("representations")]
    public List<CharacterRepresentationInput>? Representations { get; set; }
}

/// <summary>绑定角色声音请求。对应 Go: <c>app.BindCharacterVoiceRequest</c>。</summary>
public sealed class BindCharacterVoiceRequest
{
    [JsonPropertyName("voiceProfileId")]
    public string VoiceProfileID { get; set; } = "";

    [JsonPropertyName("sampleResourceId")]
    public string SampleResourceID { get; set; } = "";

    [JsonPropertyName("voiceName")]
    public string VoiceName { get; set; } = "";

    [JsonPropertyName("instructions")]
    public string Instructions { get; set; } = "";
}

/// <summary>角色形象摘要。对应 Go: <c>app.CharacterRepresentationSummary</c>。</summary>
public sealed class CharacterRepresentationSummaryDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("resourceId")]
    public string ResourceID { get; init; } = "";

    [JsonPropertyName("mediaType")]
    public string MediaType { get; init; } = "";

    [JsonPropertyName("role")]
    public string Role { get; init; } = "";
}

/// <summary>声音档案摘要。对应 Go: <c>app.VoiceProfileSummary</c>。</summary>
public sealed class VoiceProfileSummaryDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "";

    [JsonPropertyName("voiceKey")]
    public string VoiceKey { get; init; } = "";

    [JsonPropertyName("language")]
    public string Language { get; init; } = "";

    [JsonPropertyName("timbre")]
    public string Timbre { get; init; } = "";

    [JsonPropertyName("sampleResourceId")]
    [GoOmitEmpty]
    public string SampleResourceID { get; init; } = "";

    [JsonPropertyName("compatibleModels")]
    public List<string> CompatibleModels { get; init; } = [];

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";
}

/// <summary>角色声音摘要。对应 Go: <c>app.CharacterVoiceSummary</c>。</summary>
public sealed class CharacterVoiceSummaryDto
{
    [JsonPropertyName("profile")]
    public VoiceProfileSummaryDto Profile { get; init; } = new();

    [JsonPropertyName("instructions")]
    public string Instructions { get; init; } = "";
}

/// <summary>角色卡摘要。对应 Go: <c>app.CharacterCardSummary</c>。</summary>
/// <remarks><c>definition</c> / <c>representations</c> 无 omitempty：空值输出 <c>{}</c> / <c>[]</c>。</remarks>
public sealed class CharacterCardSummaryDto
{
    [JsonPropertyName("versionId")]
    public string VersionID { get; init; } = "";

    [JsonPropertyName("version")]
    public long Version { get; init; }

    [JsonPropertyName("definition")]
    public JsonElement Definition { get; init; }

    [JsonPropertyName("representations")]
    public List<CharacterRepresentationSummaryDto> Representations { get; init; } = [];

    [JsonPropertyName("voice")]
    [GoOmitEmpty]
    public CharacterVoiceSummaryDto? Voice { get; init; }

    [JsonPropertyName("visualStatus")]
    public string VisualStatus { get; init; } = "";

    [JsonPropertyName("voiceStatus")]
    public string VoiceStatus { get; init; } = "";
}

/// <summary>角色详情。对应 Go: <c>app.ProjectCharacterDetail</c>。</summary>
public sealed class ProjectCharacterDetailDto
{
    [JsonPropertyName("asset")]
    public ProjectAssetSummaryDto Asset { get; init; } = new();

    [JsonPropertyName("character")]
    public CharacterCardSummaryDto Character { get; init; } = new();
}

/// <summary>
/// 项目角色与配音。对应 Go: <c>internal/app/project_character.go</c>。
/// </summary>
/// <remarks>
/// 角色采用不可变版本链：任何修改都产生新版本并整体迁移表现与声音绑定，
/// 旧镜头继续引用历史版本。三视图任务收尾（finalize/reconcile）依赖任务域，
/// 属阶段「任务与创作」节点接入。
/// </remarks>
public sealed class ProjectCharacterService : ICharacterCardProvider
{
    /// <summary>内置 OpenAI 兼容声音。对应 Go: <c>builtinVoiceNames</c>。</summary>
    private static readonly string[] BuiltinVoiceNames =
    [
        "alloy", "ash", "ballad", "coral", "echo", "fable",
        "nova", "onyx", "sage", "shimmer", "verse", "marin", "cedar",
    ];

    private static readonly HashSet<string> ValidRepresentationRoles = new(StringComparer.Ordinal)
    {
        "primary", "front", "side", "back", "turnaround_sheet", "expression_sheet",
    };

    /// <summary>
    /// 设定/载荷 JSON 的序列化配置：转义规则对齐 Go 的 <c>encoding/json</c>
    /// （非 ASCII 原样输出、HTML 字符转义），键序由 <see cref="SortedElement"/> 手工排序。
    /// </summary>
    internal static readonly JsonSerializerOptions GoPayloadOptions = new()
    {
        Encoder = GoJsonEncoder.Instance,
    };

    private readonly Repository _repository;
    private readonly ProjectAssetService _projectAssets;

    public ProjectCharacterService(Repository repository, ProjectAssetService projectAssets)
    {
        _repository = repository;
        _projectAssets = projectAssets;
    }

    // ------------------------------------------------------------ 声音档案

    /// <summary>
    /// 声音档案列表：先幂等播种 13 个内置声音，再返回全部启用档案。
    /// 对应 Go: <c>ListVoiceProfiles</c>。
    /// </summary>
    public async Task<List<VoiceProfileSummaryDto>> ListVoiceProfilesAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;
        List<VoiceProfile> builtins = new(BuiltinVoiceNames.Length);
        foreach (string key in BuiltinVoiceNames)
        {
            string name = char.ToUpperInvariant(key[0]) + key[1..];
            builtins.Add(new VoiceProfile
            {
                ID = IdGenerator.NewId(),
                UserID = userId,
                Name = name,
                Provider = "openai_compatible",
                VoiceKey = key,
                Language = "多语言",
                CompatibleModelsJSON = "[]",
                Status = "active",
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await _repository.EnsureVoiceProfilesAsync(builtins, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<VoiceProfile> stored = await RepositoryVoiceProfilesAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        return stored.Select(VoiceProfileSummary).ToList();
    }

    // ------------------------------------------------------------ 角色生命周期

    /// <summary>创建角色（资产 + 首版本 + 项目链接同事务）。对应 Go: <c>CreateProjectCharacter</c>。</summary>
    public async Task<ProjectCharacterDetailDto> CreateAsync(
        string userId,
        string projectId,
        CreateProjectCharacterRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        string name = request.Name.Trim();
        if (name.Length == 0)
        {
            throw AppError.BadAuthRequest("角色名称不能为空");
        }

        string definition = NormalizedDefinition(request.Definition);
        DateTime now = DateTime.UtcNow;
        string assetId = IdGenerator.NewId();
        string versionId = IdGenerator.NewId();
        string payload = CharacterAssetPayload(assetId, versionId, name, definition, now, now);

        Asset asset = new()
        {
            ID = assetId,
            UserID = userId,
            Kind = "entity",
            Category = AssetCategory.AssetCategoryCharacter,
            Status = AssetVersionStatus.AssetVersionStatusConfirmed,
            PrimaryVersionID = versionId,
            Title = name,
            PayloadJSON = payload,
            CreatedAt = now,
            UpdatedAt = now,
        };
        AssetVersion version = new()
        {
            ID = versionId,
            AssetID = assetId,
            Version = 1,
            Status = AssetVersionStatus.AssetVersionStatusConfirmed,
            DefinitionJSON = definition,
            CreatedAt = now,
            UpdatedAt = now,
        };

        string folderId = await ResolveFolderAsync(projectId, null, cancellationToken).ConfigureAwait(false);
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

        await _repository.CreateProjectCharacterAsync(projectId, asset, version, link, cancellationToken)
            .ConfigureAwait(false);
        return await DetailAsync(userId, projectId, asset, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取角色详情。对应 Go: <c>ProjectCharacter</c>。</summary>
    public async Task<ProjectCharacterDetailDto> GetAsync(
        string userId, string projectId, string assetId, CancellationToken cancellationToken = default)
    {
        Asset? asset = await CharacterAssetAsync(userId, projectId, assetId, cancellationToken).ConfigureAwait(false);
        return await DetailAsync(userId, projectId, asset, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>更新角色名称与设定（产生新版本）。对应 Go: <c>UpdateProjectCharacter</c>。</summary>
    public async Task<ProjectCharacterDetailDto> UpdateAsync(
        string userId,
        string projectId,
        string assetId,
        UpdateProjectCharacterRequest request,
        CancellationToken cancellationToken = default)
    {
        Asset asset = await CharacterAssetAsync(userId, projectId, assetId, cancellationToken).ConfigureAwait(false);
        string name = request.Name.Trim();
        if (name.Length == 0)
        {
            throw AppError.BadAuthRequest("角色名称不能为空");
        }

        string definition = NormalizedDefinition(request.Definition);
        await CreateNextVersionAsync(projectId, asset, name, definition, null, null, dropVoice: false, cancellationToken)
            .ConfigureAwait(false);
        return await GetAsync(userId, projectId, asset.ID, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>整体替换角色形象（产生新版本）。对应 Go: <c>ReplaceProjectCharacterRepresentations</c>。</summary>
    public async Task<ProjectCharacterDetailDto> ReplaceRepresentationsAsync(
        string userId,
        string projectId,
        string assetId,
        ReplaceCharacterRepresentationsRequest request,
        CancellationToken cancellationToken = default)
    {
        Asset asset = await CharacterAssetAsync(userId, projectId, assetId, cancellationToken).ConfigureAwait(false);
        List<CharacterRepresentationInput> inputs = request.Representations ?? [];
        if (inputs.Count == 0 || inputs.Count > 8)
        {
            throw AppError.BadAuthRequest("角色形象数量必须在 1 到 8 之间");
        }

        HashSet<string> roles = new(StringComparer.Ordinal);
        string operationId = IdGenerator.NewId();
        DateTime now = DateTime.UtcNow;
        List<AssetRepresentation> representations = new(inputs.Count);
        foreach (CharacterRepresentationInput input in inputs)
        {
            string role = input.Role.Trim();
            if (!ValidRepresentationRoles.Contains(role))
            {
                throw AppError.BadAuthRequest("不支持的角色形象视角");
            }

            if (!roles.Add(role))
            {
                throw AppError.BadAuthRequest("同一角色形象视角不能重复");
            }

            string resourceId = input.ResourceID.Trim();
            Resource? resource = await _repository
                .ResourceForUserAsync(userId, resourceId, cancellationToken).ConfigureAwait(false);
            if (resource is null
                || resource.Kind != "image"
                || resource.Status != ResourceStatus.ResourceStatusReady)
            {
                throw AppError.BadAuthRequest("角色形象资源不可用");
            }

            representations.Add(new AssetRepresentation
            {
                ID = IdGenerator.NewId(),
                TaskID = operationId,
                ResourceID = resourceId,
                MediaType = "image",
                Role = role,
                MetadataJSON = JsonSerializer.Serialize(input.Metadata, GoPayloadOptions),
                CreatedAt = now,
            });
        }

        await CreateNextVersionAsync(
                projectId, asset, asset.Title, "", representations, null, dropVoice: false, cancellationToken)
            .ConfigureAwait(false);
        return await GetAsync(userId, projectId, asset.ID, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>绑定角色声音（产生新版本）。对应 Go: <c>BindProjectCharacterVoice</c>。</summary>
    public async Task<ProjectCharacterDetailDto> BindVoiceAsync(
        string userId,
        string projectId,
        string assetId,
        BindCharacterVoiceRequest request,
        CancellationToken cancellationToken = default)
    {
        Asset asset = await CharacterAssetAsync(userId, projectId, assetId, cancellationToken).ConfigureAwait(false);
        VoiceProfile profile;
        string sampleResourceId = request.SampleResourceID.Trim();
        if (sampleResourceId.Length > 0)
        {
            Resource? resource = await _repository
                .ResourceForUserAsync(userId, sampleResourceId, cancellationToken).ConfigureAwait(false);
            if (resource is null
                || resource.Kind != "audio"
                || resource.Status != ResourceStatus.ResourceStatusReady
                || !IsSupportedVoiceSampleMimeType(resource.MimeType))
            {
                throw AppError.BadAuthRequest("请选择已上传完成的支持格式音频：MP3、WAV、M4A/AAC、FLAC、OGG/Opus 或 WebM");
            }

            profile = await _repository
                .VoiceProfileBySampleResourceAsync(userId, sampleResourceId, cancellationToken).ConfigureAwait(false);
            if (profile is null)
            {
                string voiceName = request.VoiceName.Trim();
                if (voiceName.Length == 0)
                {
                    voiceName = "上传声音 · " + sampleResourceId[..Math.Min(8, sampleResourceId.Length)];
                }

                DateTime now = DateTime.UtcNow;
                profile = new VoiceProfile
                {
                    ID = IdGenerator.NewId(),
                    UserID = userId,
                    Name = voiceName,
                    Provider = "user_upload",
                    VoiceKey = "sample:" + sampleResourceId,
                    Language = "按样本使用",
                    Timbre = "用户上传样本",
                    SampleResourceID = sampleResourceId,
                    CompatibleModelsJSON = "[]",
                    Status = "active",
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                await _repository.CreateVoiceProfileAsync(profile, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            VoiceProfile? existing = await _repository
                .VoiceProfileForUserAsync(userId, request.VoiceProfileID.Trim(), cancellationToken).ConfigureAwait(false);
            if (existing is null || existing.Status != "active")
            {
                throw AppError.BadAuthRequest("选择的声音素材不可用");
            }

            profile = existing;
        }

        if (profile.ID.Trim().Length == 0)
        {
            throw AppError.BadAuthRequest("声音素材不可用，请重新选择");
        }

        CharacterVoiceBinding binding = new()
        {
            ID = IdGenerator.NewId(),
            VoiceProfileID = profile.ID,
            Instructions = request.Instructions.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await CreateNextVersionAsync(
                projectId, asset, asset.Title, "", null, binding, dropVoice: false, cancellationToken)
            .ConfigureAwait(false);
        return await GetAsync(userId, projectId, asset.ID, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>解绑角色声音（产生新版本）。对应 Go: <c>UnbindProjectCharacterVoice</c>。</summary>
    public async Task<ProjectCharacterDetailDto> UnbindVoiceAsync(
        string userId, string projectId, string assetId, CancellationToken cancellationToken = default)
    {
        Asset asset = await CharacterAssetAsync(userId, projectId, assetId, cancellationToken).ConfigureAwait(false);
        await CreateNextVersionAsync(projectId, asset, asset.Title, "", null, null, dropVoice: true, cancellationToken)
            .ConfigureAwait(false);
        return await GetAsync(userId, projectId, asset.ID, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 角色卡（供素材摘要复用）

    /// <summary>构建角色卡摘要。对应 Go: <c>characterCard</c>（经 <c>projectAssetSummary</c> 内嵌）。</summary>
    async Task<object?> ICharacterCardProvider.CharacterCardAsync(
        string userId, Asset asset, CancellationToken cancellationToken) =>
        await CharacterCardAsync(userId, asset, cancellationToken).ConfigureAwait(false);

    /// <summary>构建角色卡摘要。对应 Go: <c>characterCard</c>。</summary>
    public async Task<CharacterCardSummaryDto?> CharacterCardAsync(
        string userId, Asset asset, CancellationToken cancellationToken = default)
    {
        AssetVersion? version = await _repository
            .AssetVersionAsync(asset.PrimaryVersionID, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            // Go 返回原始 not-found 错误 → failInternal（500）。
            throw new InvalidOperationException("record not found");
        }

        JsonElement definition;
        try
        {
            using JsonDocument document = JsonDocument.Parse(version.DefinitionJSON);
            definition = SortedElement(document.RootElement);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException(error.Message);
        }

        IReadOnlyList<AssetRepresentation> stored = await _repository
            .AssetRepresentationsAsync(version.ID, cancellationToken).ConfigureAwait(false);
        List<CharacterRepresentationSummaryDto> representations = new(stored.Count);
        HashSet<string> roles = new(StringComparer.Ordinal);
        foreach (AssetRepresentation representation in stored)
        {
            representations.Add(new CharacterRepresentationSummaryDto
            {
                ID = representation.ID,
                ResourceID = representation.ResourceID,
                MediaType = representation.MediaType,
                Role = representation.Role,
            });
            roles.Add(representation.Role);
        }

        string visualStatus = "missing";
        // 新角色只需要一张完整的三视图设定图；旧版本的三张视图仍按原规则兼容读取。
        if (roles.Contains("turnaround_sheet") ||
            (roles.Contains("front") && roles.Contains("side") && roles.Contains("back")))
        {
            visualStatus = "ready";
        }
        else if (representations.Count > 0)
        {
            visualStatus = "partial";
        }

        CharacterVoiceSummaryDto? voice = null;
        string voiceStatus = "missing";
        CharacterVoiceBinding? binding = await _repository
            .CharacterVoiceBindingAsync(version.ID, cancellationToken).ConfigureAwait(false);
        if (binding is not null)
        {
            VoiceProfile? profile = await _repository
                .VoiceProfileForUserAsync(userId, binding.VoiceProfileID, cancellationToken).ConfigureAwait(false);
            if (profile is not null && profile.Status == "active")
            {
                voice = new CharacterVoiceSummaryDto
                {
                    Profile = VoiceProfileSummary(profile),
                    Instructions = binding.Instructions,
                };
                voiceStatus = "ready";
            }
            else
            {
                voiceStatus = "unavailable";
            }
        }

        return new CharacterCardSummaryDto
        {
            VersionID = version.ID,
            Version = version.Version,
            Definition = definition,
            Representations = representations,
            Voice = voice,
            VisualStatus = visualStatus,
            VoiceStatus = voiceStatus,
        };
    }

    // ------------------------------------------------------------ 版本链

    /// <summary>
    /// 准备并持久化下一个角色版本：复制当前设定/表现/声音（除非整体替换），
    /// 重写资产域字段。对应 Go: <c>createNextCharacterVersion</c> / <c>prepareNextCharacterVersion</c>。
    /// </summary>
    private async Task<AssetVersion> CreateNextVersionAsync(
        string projectId,
        Asset asset,
        string name,
        string definitionJson,
        List<AssetRepresentation>? replacementRepresentations,
        CharacterVoiceBinding? replacementVoice,
        bool dropVoice,
        CancellationToken cancellationToken)
    {
        (Asset nextAsset, AssetVersion next, List<AssetRepresentation> representations, CharacterVoiceBinding? voice) =
            await PrepareNextVersionAsync(
                asset, name, definitionJson, replacementRepresentations, replacementVoice, dropVoice,
                cancellationToken).ConfigureAwait(false);

        await _repository
            .SaveCharacterVersionAsync(projectId, nextAsset, next, representations, voice, cancellationToken)
            .ConfigureAwait(false);
        asset.Title = nextAsset.Title;
        asset.PrimaryVersionID = nextAsset.PrimaryVersionID;
        asset.PayloadJSON = nextAsset.PayloadJSON;
        asset.UpdatedAt = nextAsset.UpdatedAt;
        return next;
    }

    /// <summary>
    /// 准备下一个角色版本（不落库）。候选确认需要把版本替换与候选状态放进同一事务，
    /// 因此准备与持久化分离。对应 Go: <c>prepareNextCharacterVersion</c>。
    /// </summary>
    public async Task<(Asset NextAsset, AssetVersion Next, List<AssetRepresentation> Representations, CharacterVoiceBinding? Voice)>
        PrepareNextVersionAsync(
            Asset asset,
            string name,
            string definitionJson,
            List<AssetRepresentation>? replacementRepresentations,
            CharacterVoiceBinding? replacementVoice,
            bool dropVoice,
            CancellationToken cancellationToken = default)
    {
        AssetVersion? current = await _repository
            .AssetVersionAsync(asset.PrimaryVersionID, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            throw AppError.BadAuthRequest("角色当前版本不可用");
        }

        IReadOnlyList<AssetVersion> versions = await _repository
            .AssetVersionsAsync(asset.ID, cancellationToken).ConfigureAwait(false);
        long nextNumber = versions.Count > 0 ? versions[0].Version + 1 : 1;
        if (definitionJson.Trim().Length == 0)
        {
            definitionJson = current.DefinitionJSON;
        }

        DateTime now = DateTime.UtcNow;
        AssetVersion next = new()
        {
            ID = IdGenerator.NewId(),
            AssetID = asset.ID,
            Version = nextNumber,
            Status = AssetVersionStatus.AssetVersionStatusConfirmed,
            DefinitionJSON = definitionJson,
            Prompt = current.Prompt,
            Note = current.Note,
            CreatedAt = now,
            UpdatedAt = now,
        };

        List<AssetRepresentation> representations;
        if (replacementRepresentations is null)
        {
            IReadOnlyList<AssetRepresentation> currentRepresentations = await _repository
                .AssetRepresentationsAsync(current.ID, cancellationToken).ConfigureAwait(false);
            string operationId = IdGenerator.NewId();
            representations = new List<AssetRepresentation>(currentRepresentations.Count);
            foreach (AssetRepresentation representation in currentRepresentations)
            {
                representations.Add(new AssetRepresentation
                {
                    ID = IdGenerator.NewId(),
                    TaskID = operationId,
                    AssetVersionID = next.ID,
                    ResourceID = representation.ResourceID,
                    MediaType = representation.MediaType,
                    Role = representation.Role,
                    MetadataJSON = representation.MetadataJSON,
                    CreatedAt = now,
                });
            }
        }
        else
        {
            representations = replacementRepresentations;
            foreach (AssetRepresentation representation in representations)
            {
                representation.AssetVersionID = next.ID;
            }
        }

        CharacterVoiceBinding? voice = replacementVoice;
        if (voice is null && !dropVoice)
        {
            CharacterVoiceBinding? currentVoice = await _repository
                .CharacterVoiceBindingAsync(current.ID, cancellationToken).ConfigureAwait(false);
            if (currentVoice is not null)
            {
                voice = new CharacterVoiceBinding
                {
                    VoiceProfileID = currentVoice.VoiceProfileID,
                    Instructions = currentVoice.Instructions,
                };
            }
        }

        if (voice is not null)
        {
            voice.ID = IdGenerator.NewId();
            voice.AssetVersionID = next.ID;
            voice.CreatedAt = now;
            voice.UpdatedAt = now;
        }

        string payload = CharacterAssetPayload(asset.ID, next.ID, name, definitionJson, asset.CreatedAt, now);
        Asset nextAsset = new()
        {
            ID = asset.ID,
            UserID = asset.UserID,
            Kind = "entity",
            Category = AssetCategory.AssetCategoryCharacter,
            Status = AssetVersionStatus.AssetVersionStatusConfirmed,
            PrimaryVersionID = next.ID,
            Title = name,
            PayloadJSON = payload,
            CreatedAt = asset.CreatedAt,
            UpdatedAt = now,
        };

        return (nextAsset, next, representations, voice);
    }

    // ------------------------------------------------------------ 内部

    private async Task<Asset> CharacterAssetAsync(
        string userId, string projectId, string assetId, CancellationToken cancellationToken)
    {
        Asset? asset = await _repository
            .ProjectCharacterAssetAsync(userId, projectId, assetId.Trim(), cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            // Go 的 First 未命中会带 not-found 错误进入 failInternal（500）。
            throw new InvalidOperationException("record not found");
        }

        return asset;
    }

    private async Task<ProjectCharacterDetailDto> DetailAsync(
        string userId, string projectId, Asset asset, CancellationToken cancellationToken)
    {
        ProjectAssetSummaryDto summary = await _projectAssets
            .BuildSummaryAsync(userId, projectId, asset, cancellationToken).ConfigureAwait(false);
        if (summary.Character is null)
        {
            throw AppError.BadAuthRequest("角色素材缺少角色设定");
        }

        return new ProjectCharacterDetailDto
        {
            Asset = summary,
            Character = (CharacterCardSummaryDto)summary.Character,
        };
    }

    private async Task RequireProjectAsync(
        string userId, string projectId, CancellationToken cancellationToken)
    {
        if (await _repository.ProjectForUserAsync(userId, projectId, cancellationToken).ConfigureAwait(false) is null)
        {
            // Go 的 ProjectForUser 未命中 → 原始 not-found 错误 → failInternal（500）。
            throw new InvalidOperationException("record not found");
        }
    }

    /// <summary>解析目标文件夹；角色固定落根目录。对应 Go: <c>resolveProjectAssetFolderID</c>。</summary>
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

    /// <summary>Go: <c>repo.VoiceProfiles</c>（仅启用档案，创建时间升序）。</summary>
    private Task<IReadOnlyList<VoiceProfile>> RepositoryVoiceProfilesAsync(
        string userId, CancellationToken cancellationToken) =>
        _repository.VoiceProfilesAsync(userId, cancellationToken);

    /// <summary>Go: <c>voiceProfileSummary</c>。</summary>
    private static VoiceProfileSummaryDto VoiceProfileSummary(VoiceProfile profile)
    {
        List<string> compatible = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(profile.CompatibleModelsJSON);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        compatible.Add(item.GetString() ?? "");
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Go 的 json.Unmarshal 失败时 compatible 保持空切片。
        }

        return new VoiceProfileSummaryDto
        {
            ID = profile.ID,
            Name = profile.Name,
            Provider = profile.Provider,
            VoiceKey = profile.VoiceKey,
            Language = profile.Language,
            Timbre = profile.Timbre,
            SampleResourceID = profile.SampleResourceID,
            CompatibleModels = compatible,
            Status = profile.Status,
        };
    }

    /// <summary>Go: <c>normalizedCharacterDefinition</c>。nil → <c>{}</c>，递归按键序（Ordinal）输出。</summary>
    private static string NormalizedDefinition(Dictionary<string, JsonElement>? definition)
    {
        if (definition is null || definition.Count == 0)
        {
            return "{}";
        }

        return JsonSerializer.Serialize(
            SortedElement(JsonSerializer.SerializeToElement(definition)), GoPayloadOptions);
    }

    /// <summary>
    /// Go map 序列化按字典序递归排序键；JsonElement 直接重排嵌套对象，
    /// 数字保留原文（已知偏差见 PENDING-CONFIRMATIONS）。
    /// </summary>
    internal static JsonElement SortedElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonElement>(
                element.EnumerateObject()
                    .Select(property => new KeyValuePair<string, JsonElement>(
                        property.Name, SortedElement(property.Value)))
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal),
                StringComparer.Ordinal)),
        JsonValueKind.Array => JsonSerializer.SerializeToElement(
            element.EnumerateArray().Select(SortedElement).ToList()),
        _ => element.Clone(),
    };

    /// <summary>Go: <c>characterAssetPayload</c>。载荷 JSON 与画布素材格式对齐（键序 Ordinal）。</summary>
    internal static string CharacterAssetPayload(
        string assetId,
        string versionId,
        string name,
        string definitionJson,
        DateTime createdAt,
        DateTime updatedAt)
    {
        JsonElement definition;
        try
        {
            using JsonDocument document = JsonDocument.Parse(definitionJson);
            definition = SortedElement(document.RootElement);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException(error.Message);
        }

        Dictionary<string, JsonElement> payload = new(StringComparer.Ordinal)
        {
            ["category"] = JsonSerializer.SerializeToElement(AssetCategory.AssetCategoryCharacter),
            ["coverUrl"] = JsonSerializer.SerializeToElement(""),
            ["createdAt"] = JsonSerializer.SerializeToElement(ProjectService.FormatRfc3339Nano(createdAt)),
            ["data"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["definition"] = definition,
            }),
            ["id"] = JsonSerializer.SerializeToElement(assetId),
            ["kind"] = JsonSerializer.SerializeToElement("entity"),
            ["primaryVersionId"] = JsonSerializer.SerializeToElement(versionId),
            ["status"] = JsonSerializer.SerializeToElement(AssetVersionStatus.AssetVersionStatusConfirmed),
            ["tags"] = JsonSerializer.SerializeToElement(Array.Empty<string>()),
            ["title"] = JsonSerializer.SerializeToElement(name),
            ["updatedAt"] = JsonSerializer.SerializeToElement(ProjectService.FormatRfc3339Nano(updatedAt)),
        };
        return JsonSerializer.Serialize(payload, GoPayloadOptions);
    }

    /// <summary>
    /// 角色候选设定并入当前设定：只填空缺字段（aliases 永远走合并），别名按大小写不敏感去重，
    /// 当前标题本身不进别名。对应 Go: <c>mergeCharacterCandidateDefinition</c>。
    /// </summary>
    public static string MergeCharacterCandidateDefinition(
        string currentJson, string candidateJson, string currentName, string candidateName)
    {
        Dictionary<string, JsonElement> current;
        try
        {
            using JsonDocument currentDoc = JsonDocument.Parse(currentJson);
            current = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in currentDoc.RootElement.EnumerateObject())
            {
                current[property.Name] = property.Value.Clone();
            }
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException(error.Message);
        }

        Dictionary<string, JsonElement> candidate;
        try
        {
            using JsonDocument candidateDoc = JsonDocument.Parse(candidateJson);
            candidate = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in candidateDoc.RootElement.EnumerateObject())
            {
                candidate[property.Name] = property.Value.Clone();
            }
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("角色候选设定格式无效");
        }

        foreach (KeyValuePair<string, JsonElement> pair in candidate)
        {
            if (pair.Key == "aliases" || !EmptyCharacterDefinitionValue(current.GetValueOrDefault(pair.Key)))
            {
                continue;
            }
            current[pair.Key] = pair.Value.Clone();
        }

        List<string> aliases = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        string normalizedCurrentName = currentName.Trim();
        void AppendAlias(string value)
        {
            string text = value.Trim();
            if (text.Length == 0
                || !seen.Add(text.ToLowerInvariant())
                || string.Equals(text, normalizedCurrentName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            aliases.Add(text);
        }
        foreach (JsonElement list in new[] { current.GetValueOrDefault("aliases"), candidate.GetValueOrDefault("aliases") })
        {
            if (list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    AppendAlias(item.GetString() ?? "");
                }
            }
        }
        AppendAlias(candidateName);
        current["aliases"] = JsonSerializer.SerializeToElement(aliases);
        return JsonSerializer.Serialize(SortedElement(JsonSerializer.SerializeToElement(current)), GoPayloadOptions);
    }

    /// <summary>Go: <c>emptyCharacterDefinitionValue</c>。null/空白串/空数组视为空缺，其余非空。</summary>
    private static bool EmptyCharacterDefinitionValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => true,
        JsonValueKind.String => value.GetString()?.Trim().Length == 0,
        JsonValueKind.Array => !value.EnumerateArray().Any(),
        _ => false,
    };

    /// <summary>Go: <c>isSupportedVoiceSampleMimeType</c>。取分号前的主类型，大小写不敏感。</summary>
    private static bool IsSupportedVoiceSampleMimeType(string value)
    {
        string mime = value.Split(';', 2)[0].Trim().ToLowerInvariant();
        return mime switch
        {
            "audio/mpeg" or "audio/mp3" or "audio/x-mpeg"
                or "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/vnd.wave" or "audio/x-pn-wav"
                or "audio/mp4" or "audio/x-m4a" or "audio/m4a" or "audio/aac" or "audio/aacp"
                or "audio/flac" or "audio/x-flac"
                or "audio/ogg" or "application/ogg" or "audio/opus" or "audio/webm" => true,
            _ => false,
        };
    }
}
