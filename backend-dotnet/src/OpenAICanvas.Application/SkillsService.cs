#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>展示媒体。对应 Go: <c>skills.SkillShowcaseMedia</c>。</summary>
public sealed class SkillShowcaseMediaDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("showcaseUri")]
    public string ShowcaseURI { get; set; } = "";

    [JsonPropertyName("showcaseUrl")]
    public string ShowcaseURL { get; set; } = "";
}

/// <summary>技能作者的有效展示身份。对应 Go: <c>skills.SkillEffectiveUser</c>。</summary>
public sealed class SkillEffectiveUserDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("avatarUrl")]
    public string AvatarURL { get; set; } = "";

    [JsonPropertyName("uid")]
    public string UID { get; set; } = "";
}

/// <summary>
/// 技能条目。对应 Go: <c>skills.SkillItem</c>。
/// 字段顺序即 JSON 输出顺序，必须与 Go 声明一致。
/// </summary>
public sealed class SkillItemDto
{
    [JsonPropertyName("skillId")]
    public string SkillID { get; init; } = "";

    [JsonPropertyName("skillName")]
    public string SkillName { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    /// <summary>仅详情接口下发正文（列表不带，避免大字段拖慢列表）。</summary>
    [JsonPropertyName("instruction")]
    [GoOmitEmpty]
    public string Instruction { get; init; } = "";

    [JsonPropertyName("versionId")]
    public string VersionID { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; init; } = "";

    [JsonPropertyName("fileCount")]
    public long FileCount { get; init; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; init; }

    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = "";

    [JsonPropertyName("sourceUrl")]
    public string SourceURL { get; init; } = "";

    [JsonPropertyName("sourceRef")]
    public string SourceRef { get; init; } = "";

    [JsonPropertyName("sourceSubdir")]
    public string SourceSubdir { get; init; } = "";

    [JsonPropertyName("sourceCommit")]
    public string SourceCommit { get; init; } = "";

    [JsonPropertyName("syncStatus")]
    public string SyncStatus { get; init; } = "";

    [JsonPropertyName("syncError")]
    [GoOmitEmpty]
    public string SyncError { get; init; } = "";

    [JsonPropertyName("autoUpdate")]
    public bool AutoUpdate { get; init; }

    [JsonPropertyName("lastCheckedAt")]
    [GoOmitEmpty]
    public DateTime? LastCheckedAt { get; init; }

    [JsonPropertyName("lastSyncedAt")]
    [GoOmitEmpty]
    public DateTime? LastSyncedAt { get; init; }

    [JsonPropertyName("status")]
    public long Status { get; init; }

    [JsonPropertyName("markdownUrl")]
    public string MarkdownURL { get; init; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    [JsonPropertyName("source")]
    public long Source { get; init; }

    [JsonPropertyName("tag")]
    public string Tag { get; init; } = "";

    [JsonPropertyName("sortWeight")]
    public long SortWeight { get; init; }

    [JsonPropertyName("isPrivate")]
    public bool IsPrivate { get; init; }

    [JsonPropertyName("likeCount")]
    public long LikeCount { get; init; }

    [JsonPropertyName("isLike")]
    public bool IsLike { get; init; }

    [JsonPropertyName("ownerUid")]
    public string OwnerUID { get; init; } = "";

    [JsonPropertyName("effectiveUser")]
    public SkillEffectiveUserDto EffectiveUser { get; init; } = new();

    /// <summary>Go 是指针且无 omitempty：无值时输出 <c>null</c>。</summary>
    [JsonPropertyName("originalSkillId")]
    public string? OriginalSkillID { get; init; }

    [JsonPropertyName("showcaseMedia")]
    public List<SkillShowcaseMediaDto> ShowcaseMedia { get; init; } = [];

    [JsonPropertyName("addedCount")]
    public long AddedCount { get; init; }

    [JsonPropertyName("isTest")]
    public bool IsTest { get; init; }

    [JsonPropertyName("extraInfo")]
    public string ExtraInfo { get; init; } = "";

    [JsonPropertyName("isAdded")]
    public bool IsAdded { get; init; }

    [JsonPropertyName("isOwner")]
    public bool IsOwner { get; init; }
}

/// <summary>技能分类。对应 Go: <c>skills.SkillCategory</c>。</summary>
public sealed class SkillCategoryDto
{
    [JsonPropertyName("value")]
    public string Value { get; init; } = "";

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";
}

/// <summary>技能列表。对应 Go: <c>skills.SkillList</c>。</summary>
public sealed class SkillListDto
{
    [JsonPropertyName("skills")]
    public required IReadOnlyList<SkillItemDto> Skills { get; init; }

    [JsonPropertyName("totalCount")]
    public long TotalCount { get; init; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }

    [JsonPropertyName("nextOffset")]
    public int NextOffset { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    [JsonPropertyName("categories")]
    public required IReadOnlyList<SkillCategoryDto> Categories { get; init; }
}

/// <summary>
/// 技能库服务。对应 Go: <c>internal/skills/skills.go</c> 的读取与状态部分。
/// </summary>
/// <remarks>
/// 本轮覆盖列表 / 详情 / 已添加 / 加入 / 收藏 / 删除。
/// 创建与更新依赖技能包文件写入（<c>skill_packages.go</c>），另行实现。
/// </remarks>
public sealed class SkillsService
{
    /// <summary>技能状态：启用。对应 Go: <c>skillStatusEnabled</c>。</summary>
    private const long SkillStatusEnabled = 1;

    /// <summary>分类白名单。对应 Go: <c>skillCategoryLabels</c>。</summary>
    private static readonly Dictionary<string, string> CategoryLabels = new(StringComparer.Ordinal)
    {
        ["drama"] = "短剧影视",
        ["ecommerce"] = "电商营销",
        ["creative"] = "创意设计",
        ["social"] = "社媒内容",
        ["others"] = "其他",
    };

    private readonly Repository _repository;

    public SkillsService(Repository repository)
    {
        _repository = repository;
    }

    // ------------------------------------------------------------ 列表

    /// <summary>技能列表。对应 Go: <c>Skills</c>。</summary>
    public async Task<SkillListDto> SkillsAsync(
        string userId, int page, int pageSize, string scope, string search, string tag, string sort,
        CancellationToken cancellationToken = default)
    {
        (int normalizedPage, int normalizedPageSize, string normalizedScope, string normalizedTag, string normalizedSort) =
            NormalizeListRequest(page, pageSize, scope, tag, sort);
        string normalizedSearch = (search ?? "").Trim();

        (IReadOnlyList<Skill> rows, long total) = await _repository.SkillsAsync(new Repository.SkillListFilter
        {
            UserID = userId,
            Scope = normalizedScope,
            Search = normalizedSearch,
            Tag = normalizedTag,
            Sort = normalizedSort,
            Limit = normalizedPageSize,
            Offset = (normalizedPage - 1) * normalizedPageSize,
        }, cancellationToken).ConfigureAwait(false);

        List<SkillItemDto> items = await BuildItemsAsync(userId, rows, includeInstruction: false, cancellationToken)
            .ConfigureAwait(false);

        int nextOffset = normalizedPage * normalizedPageSize;
        if (nextOffset >= total)
        {
            // 没有下一页时归零，前端据此停止翻页。
            nextOffset = 0;
        }

        return new SkillListDto
        {
            Skills = items,
            TotalCount = total,
            HasMore = (long)(normalizedPage * normalizedPageSize) < total,
            NextOffset = nextOffset,
            Page = normalizedPage,
            PageSize = normalizedPageSize,
            Categories = Categories(),
        };
    }

    /// <summary>我加入的技能。对应 Go: <c>AddedSkills</c>。</summary>
    public async Task<List<SkillItemDto>> AddedSkillsAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        (IReadOnlyList<Skill> rows, _) = await _repository.SkillsAsync(new Repository.SkillListFilter
        {
            UserID = userId,
            Scope = "mine",
            Sort = "updated",
            Limit = -1,
        }, cancellationToken).ConfigureAwait(false);

        return await BuildItemsAsync(userId, rows, includeInstruction: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>技能详情（含正文）。对应 Go: <c>SkillDetail</c>。</summary>
    public async Task<SkillItemDto> SkillDetailAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        Skill skill = await VisibleSkillAsync(userId, id, cancellationToken).ConfigureAwait(false);
        List<SkillItemDto> items = await BuildItemsAsync(userId, [skill], includeInstruction: true, cancellationToken)
            .ConfigureAwait(false);
        return items[0];
    }

    // ------------------------------------------------------------ 状态

    /// <summary>加入 / 移出我的技能。对应 Go: <c>SetSkillAdded</c>。</summary>
    public async Task<SkillItemDto> SetSkillAddedAsync(
        string userId, string id, bool added, CancellationToken cancellationToken = default)
    {
        Skill skill = await VisibleSkillAsync(userId, id, cancellationToken).ConfigureAwait(false);

        // 自己创建的技能永远在「我的技能」里，不允许移除。
        if (skill.OwnerID == userId)
        {
            if (!added)
            {
                throw AppError.BadAuthRequest("自己创建的技能始终保留在我的技能中");
            }
            return await SkillDetailAsync(userId, id, cancellationToken).ConfigureAwait(false);
        }

        UserSkillState state = await SkillStateAsync(userId, id, cancellationToken).ConfigureAwait(false);
        state.Added = added;
        // 加入时记录当时的版本，后续用于判断是否需要同步。
        state.InstalledVersionID = skill.CurrentVersionID;
        state.AutoUpdate = skill.AutoUpdate;
        state.UpdatedAt = DateTime.UtcNow;

        await _repository.SetUserSkillAddedAsync(state, cancellationToken).ConfigureAwait(false);
        return await SkillDetailAsync(userId, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>收藏 / 取消收藏。对应 Go: <c>SetSkillLiked</c>。</summary>
    public async Task<SkillItemDto> SetSkillLikedAsync(
        string userId, string id, bool liked, CancellationToken cancellationToken = default)
    {
        await VisibleSkillAsync(userId, id, cancellationToken).ConfigureAwait(false);

        UserSkillState state = await SkillStateAsync(userId, id, cancellationToken).ConfigureAwait(false);
        state.Liked = liked;
        state.UpdatedAt = DateTime.UtcNow;

        await _repository.SetUserSkillLikedAsync(state, cancellationToken).ConfigureAwait(false);
        return await SkillDetailAsync(userId, id, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 删除

    /// <summary>
    /// 删除技能（仅作者）。对应 Go: <c>DeleteSkill</c>。
    /// </summary>
    public async Task DeleteSkillAsync(string userId, string id, CancellationToken cancellationToken = default)
    {
        Skill skill = await OwnedSkillAsync(userId, id, cancellationToken).ConfigureAwait(false);
        await _repository.DeleteSkillAsync(skill.ID, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 内部

    /// <summary>
    /// 可见性边界。所有详情与关系写入都先经过这里，
    /// 避免私有技能通过「加入 / 收藏」侧信道泄露正文。
    /// 对应 Go: <c>visibleSkill</c>。
    /// </summary>
    private async Task<Skill> VisibleSkillAsync(string userId, string id, CancellationToken cancellationToken)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0)
        {
            throw AppError.BadAuthRequest("技能 ID 不能为空");
        }

        Skill skill = await _repository.SkillAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.BadAuthRequest("技能不存在或已删除");

        if (skill.IsPrivate && skill.OwnerID != userId)
        {
            throw AppError.Forbidden("该技能未公开");
        }
        return skill;
    }

    /// <summary>作者边界。对应 Go: <c>ownedSkill</c>。</summary>
    private async Task<Skill> OwnedSkillAsync(string userId, string id, CancellationToken cancellationToken)
    {
        Skill skill = await VisibleSkillAsync(userId, id, cancellationToken).ConfigureAwait(false);
        if (skill.OwnerID != userId)
        {
            throw AppError.Forbidden("只有作者可以修改或删除该技能");
        }
        return skill;
    }

    /// <summary>取用户状态；不存在时返回一个待写入的新对象。对应 Go: <c>skillState</c>。</summary>
    private async Task<UserSkillState> SkillStateAsync(string userId, string skillId, CancellationToken cancellationToken)
    {
        UserSkillState? state = await _repository
            .UserSkillStateAsync(userId, skillId, cancellationToken).ConfigureAwait(false);

        return state ?? new UserSkillState
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            SkillID = skillId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>批量组装列表项（一次取状态、指标、作者，避免 N+1）。对应 Go: <c>skillItems</c>。</summary>
    private async Task<List<SkillItemDto>> BuildItemsAsync(
        string userId, IReadOnlyList<Skill> skills, bool includeInstruction, CancellationToken cancellationToken)
    {
        if (skills.Count == 0)
        {
            return [];
        }

        List<string> ids = skills.Select(skill => skill.ID).ToList();
        List<string> ownerIds = skills.Select(skill => skill.OwnerID).Distinct(StringComparer.Ordinal).ToList();

        IReadOnlyList<UserSkillState> states = await _repository
            .UserSkillStatesBySkillIDsAsync(userId, ids, cancellationToken).ConfigureAwait(false);
        Dictionary<string, Repository.SkillMetricRow> metrics = await _repository
            .SkillMetricsAsync(ids, cancellationToken).ConfigureAwait(false);
        Dictionary<string, User> owners = await _repository
            .SkillOwnersAsync(ownerIds, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> ownerAvatars = await _repository
            .SkillOwnerAvatarsAsync(ownerIds, cancellationToken).ConfigureAwait(false);

        Dictionary<string, UserSkillState> stateBySkill = states.ToDictionary(state => state.SkillID, StringComparer.Ordinal);

        List<SkillItemDto> items = new(skills.Count);
        foreach (Skill skill in skills)
        {
            List<SkillShowcaseMediaDto> showcaseMedia = ParseShowcaseMedia(skill.ShowcaseMediaJSON);

            owners.TryGetValue(skill.OwnerID, out User? owner);

            // 作者名优先级：库内作者昵称 → 作者当前昵称 → 作者用户名。
            string ownerName = skill.AuthorName.Trim();
            if (!string.IsNullOrWhiteSpace(owner?.DisplayName))
            {
                ownerName = owner.DisplayName.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(owner?.Username))
            {
                ownerName = owner.Username.Trim();
            }

            // 头像优先用第三方身份的最新头像（更可能有效），否则回落到库内快照。
            string ownerAvatarURL = skill.AuthorAvatarURL.Trim();
            if (ownerAvatars.TryGetValue(skill.OwnerID, out string? latestAvatar) && latestAvatar.Length > 0)
            {
                ownerAvatarURL = latestAvatar;
            }

            stateBySkill.TryGetValue(skill.ID, out UserSkillState? state);
            metrics.TryGetValue(skill.ID, out Repository.SkillMetricRow? metric);

            // 计数 = 内置初始值 + 实时用户行为。
            long likeCount = (metric?.LikeCount ?? 0) + skill.InitialLikeCount;
            long addedCount = (metric?.AddedCount ?? 0) + skill.InitialAddedCount;

            items.Add(new SkillItemDto
            {
                SkillID = skill.ID,
                SkillName = skill.Name,
                Description = skill.Description,
                Instruction = includeInstruction ? skill.Instruction : "",
                VersionID = skill.CurrentVersionID,
                Version = skill.VersionLabel,
                ContentHash = skill.ContentHash,
                FileCount = skill.FileCount,
                TotalBytes = skill.TotalBytes,
                SourceType = skill.SourceType,
                SourceURL = skill.SourceURL,
                SourceRef = skill.SourceRef,
                SourceSubdir = skill.SourceSubdir,
                SourceCommit = skill.SourceCommit,
                SyncStatus = skill.SyncStatus,
                SyncError = skill.SyncError,
                AutoUpdate = skill.AutoUpdate,
                LastCheckedAt = skill.LastCheckedAt,
                LastSyncedAt = skill.LastSyncedAt,
                Status = skill.Status,
                MarkdownURL = skill.MarkdownURL,
                CreatedAt = skill.CreatedAt,
                UpdatedAt = skill.UpdatedAt,
                Source = skill.Source,
                Tag = skill.Tag,
                SortWeight = skill.SortWeight,
                IsPrivate = skill.IsPrivate,
                LikeCount = likeCount,
                IsLike = state?.Liked ?? false,
                OwnerUID = skill.OwnerID,
                EffectiveUser = new SkillEffectiveUserDto
                {
                    Name = ownerName,
                    AvatarURL = ownerAvatarURL,
                    UID = skill.OwnerID,
                },
                OriginalSkillID = null,
                ShowcaseMedia = showcaseMedia,
                AddedCount = addedCount,
                IsTest = false,
                ExtraInfo = skill.ExtraInfo,
                // 自己创建的技能天然算「已加入」。
                IsAdded = (state?.Added ?? false) || skill.OwnerID == userId,
                IsOwner = skill.OwnerID == userId,
            });
        }

        return items;
    }

    /// <summary>
    /// 解析库内展示媒体 JSON。
    /// </summary>
    /// <remarks>
    /// 历史记录可能还是 snake_case（<c>showcase_uri</c> / <c>showcase_url</c>），
    /// 只在持久化边界转换；对外 API 一律 camelCase。
    /// 对应 Go: <c>parseShowcaseMedia</c>。
    /// </remarks>
    private static List<SkillShowcaseMediaDto> ParseShowcaseMedia(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0 || raw == "null")
        {
            return [];
        }

        List<SkillShowcaseMediaDto>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<SkillShowcaseMediaDto>>(raw);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("技能展示媒体数据格式错误");
        }

        if (items is null)
        {
            return [];
        }

        // 已有 camelCase 地址就直接用；否则尝试 legacy snake_case。
        if (HasShowcaseUrl(items))
        {
            return items;
        }

        List<LegacyShowcaseMedia>? legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<List<LegacyShowcaseMedia>>(raw);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("技能展示媒体数据格式错误");
        }

        return legacy is null
            ? []
            : legacy.Select(item => new SkillShowcaseMediaDto
            {
                Type = item.Type ?? "",
                ShowcaseURI = item.ShowcaseURI ?? "",
                ShowcaseURL = item.ShowcaseURL ?? "",
            }).ToList();
    }

    private static bool HasShowcaseUrl(IReadOnlyList<SkillShowcaseMediaDto> items)
    {
        foreach (SkillShowcaseMediaDto item in items)
        {
            if (item.ShowcaseURI.Trim().Length > 0 || item.ShowcaseURL.Trim().Length > 0)
            {
                return true;
            }
        }

        // 空数组也算「没有 legacy 内容」。
        return items.Count == 0;
    }

    private static (
        int Page, int PageSize, string Scope, string Tag, string Sort) NormalizeListRequest(
        int page, int pageSize, string scope, string tag, string sort)
    {
        if (page <= 0)
        {
            page = 1;
        }
        if (pageSize <= 0)
        {
            pageSize = 20;
        }
        if (pageSize > 80)
        {
            pageSize = 80;
        }

        scope = scope switch
        {
            "public" or "mine" or "created" or "favorites" => scope,
            _ => "public",
        };

        sort = sort switch
        {
            "popular" or "new" or "updated" => sort,
            _ => "popular",
        };

        tag = (tag ?? "").Trim();
        if (tag.Length > 0 && !CategoryLabels.ContainsKey(tag))
        {
            // 未知分类静默忽略，而不是报错——与 Go 一致。
            tag = "";
        }

        return (page, pageSize, scope, tag, sort);
    }

    private static List<SkillCategoryDto> Categories() =>
        CategoryLabels.Select(pair => new SkillCategoryDto { Value = pair.Key, Label = pair.Value }).ToList();

    /// <summary>历史 snake_case 展示媒体。仅用于反序列化兼容。</summary>
    private sealed class LegacyShowcaseMedia
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("showcase_uri")]
        public string? ShowcaseURI { get; set; }

        [JsonPropertyName("showcase_url")]
        public string? ShowcaseURL { get; set; }
    }
}
