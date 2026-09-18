#nullable enable
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>公告创建/更新请求。对应 Go: <c>app.CreateAnnouncementRequest</c>（Update 为同类型别名）。</summary>
public sealed class AnnouncementRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("imageResourceId")]
    public string ImageResourceID { get; set; } = "";

    [JsonPropertyName("level")]
    public string Level { get; set; } = "";

    [JsonPropertyName("pinned")]
    public bool Pinned { get; set; }
}

/// <summary>管理端公告分页。对应 Go: <c>app.AnnouncementPage</c>。</summary>
public sealed class AnnouncementPageDto
{
    [JsonPropertyName("announcements")]
    public IReadOnlyList<Announcement> Announcements { get; init; } = [];

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("page")]
    public long Page { get; init; }

    [JsonPropertyName("pageSize")]
    public long PageSize { get; init; }
}

/// <summary>用户公告 feed。对应 Go: <c>app.UserAnnouncementFeed</c>。</summary>
public sealed class UserAnnouncementFeedDto
{
    [JsonPropertyName("announcements")]
    public IReadOnlyList<Announcement> Announcements { get; init; } = [];

    [JsonPropertyName("unreadCount")]
    public long UnreadCount { get; init; }
}

/// <summary>
/// 公告域服务。对应 Go: <c>app/announcement.go</c>。
/// </summary>
/// <remarks>
/// 配图上传（<c>UploadAnnouncementImage</c>）依赖资源上传链路（阶段 5 资源节点），
/// 暂未接线；其余（feed/已读/管理 CRUD/关闭/配图草稿消费与丢弃/图片下发）已完整移植。
/// </remarks>
public sealed class AnnouncementService
{
    /// <summary>公告配图上限 10MB。对应 Go: <c>AnnouncementImageMaxBytes</c>。</summary>
    public const long AnnouncementImageMaxBytes = 10 << 20;

    private static readonly HashSet<string> ValidLevels = new(StringComparer.Ordinal)
    {
        "info", "success", "warning", "critical",
    };

    private readonly Repository _repository;

    public AnnouncementService(Repository repository)
    {
        _repository = repository;
    }

    /// <summary>管理端公告分页。对应 Go: <c>AdminAnnouncementPage</c>。</summary>
    public async Task<AnnouncementPageDto> AdminAnnouncementPageAsync(
        User actor, string keyword, string status, long page, long limit,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (page, limit) = ChannelAdminService.NormalizeAdminPage(page, limit);
        (IReadOnlyList<Announcement> announcements, long total) = await _repository.AdminAnnouncementsAsync(
            keyword, status, limit, (page - 1) * limit, cancellationToken).ConfigureAwait(false);
        foreach (Announcement announcement in announcements)
        {
            Decorate(announcement);
        }
        return new AnnouncementPageDto
        {
            Announcements = announcements,
            Total = total,
            Page = page,
            PageSize = limit,
        };
    }

    /// <summary>创建公告。对应 Go: <c>CreateAnnouncement</c>。</summary>
    public async Task<Announcement> CreateAnnouncementAsync(
        User actor, AnnouncementRequest request, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (string title, string content, string level) = NormalizeInput(request);
        string imageResourceId = await ValidateImageDraftAsync(
            actor, request.ImageResourceID, cancellationToken).ConfigureAwait(false);

        DateTime now = DateTime.UtcNow;
        Announcement announcement = new()
        {
            ID = IdGenerator.NewId(),
            Title = title,
            Content = content,
            ImageResourceID = imageResourceId,
            Level = level,
            Pinned = request.Pinned,
            Status = "active",
            CreatedBy = actor.ID,
            PublishedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        try
        {
            await _repository.CreateAnnouncementWithImageAsync(announcement, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException error) when (error.Message.Contains("draft unavailable", StringComparison.Ordinal))
        {
            throw AppError.BadAuthRequest("公告配图草稿已失效，请重新上传");
        }
        Decorate(announcement);
        return announcement;
    }

    /// <summary>更新公告（含旧配图替换与清理）。对应 Go: <c>UpdateAnnouncement</c>。</summary>
    public async Task<Announcement> UpdateAnnouncementAsync(
        User actor, string id, AnnouncementRequest request, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        Announcement? announcement = await _repository.AnnouncementAsync(
            id.Trim(), cancellationToken).ConfigureAwait(false);
        if (announcement is null)
        {
            throw AppError.BadAuthRequest("公告不存在");
        }
        (string title, string content, string level) = NormalizeInput(request);

        string imageResourceId = request.ImageResourceID.Trim();
        string newDraftResourceId = "";
        if (imageResourceId != announcement.ImageResourceID && imageResourceId.Length > 0)
        {
            imageResourceId = await ValidateImageDraftAsync(actor, imageResourceId, cancellationToken)
                .ConfigureAwait(false);
            newDraftResourceId = imageResourceId;
        }

        Resource? oldResource = null;
        ResourceDeletionJob? deletionJob = null;
        if (announcement.ImageResourceID.Length > 0 && announcement.ImageResourceID != imageResourceId)
        {
            oldResource = await _repository.ResourceAsync(announcement.ImageResourceID, cancellationToken)
                .ConfigureAwait(false);
            if (oldResource is null)
            {
                throw AppError.BadAuthRequest("原公告配图资源不存在，已停止更新以避免数据不一致");
            }
            await EnsureResourceHasNoBusinessReferencesAsync(
                oldResource, ("公告", announcement.ID), cancellationToken).ConfigureAwait(false);
            long sharedCount = await _repository.ResourceStorageReferenceCountAsync(
                oldResource, [oldResource.ID], cancellationToken).ConfigureAwait(false);
            if (sharedCount == 0)
            {
                deletionJob = ResourceDeleteService.BuildDeletionJob(oldResource);
            }
        }

        DateTime now = DateTime.UtcNow;
        announcement.Title = title;
        announcement.Content = content;
        announcement.ImageResourceID = imageResourceId;
        announcement.Level = level;
        announcement.Pinned = request.Pinned;
        announcement.Status = "active";
        announcement.ClosedAt = null;
        announcement.PublishedAt = now;
        announcement.UpdatedAt = now;

        bool updated;
        try
        {
            updated = await _repository.UpdateAnnouncementWithImageAsync(
                announcement, actor.ID, newDraftResourceId, oldResource, deletionJob, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException error) when (error.Message.Contains("draft unavailable", StringComparison.Ordinal))
        {
            throw AppError.BadAuthRequest("公告配图草稿已失效，请重新上传");
        }
        if (!updated)
        {
            throw AppError.BadAuthRequest("原公告配图仍被其他公告引用，已停止更新");
        }
        Decorate(announcement);
        return announcement;
    }

    /// <summary>关闭公告。对应 Go: <c>CloseAnnouncement</c>。</summary>
    public async Task<Announcement> CloseAnnouncementAsync(
        User actor, string id, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        Announcement? announcement = await _repository.AnnouncementAsync(
            id.Trim(), cancellationToken).ConfigureAwait(false);
        if (announcement is null)
        {
            throw AppError.BadAuthRequest("公告不存在");
        }
        if (announcement.Status == "closed")
        {
            throw AppError.BadAuthRequest("公告已经关闭");
        }
        bool updated = await _repository.CloseAnnouncementAsync(
            announcement.ID, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (!updated)
        {
            throw AppError.BadAuthRequest("公告状态已变化，请刷新后重试");
        }
        Announcement? closed = await _repository.AnnouncementAsync(announcement.ID, cancellationToken)
            .ConfigureAwait(false);
        if (closed is null)
        {
            throw AppError.BadAuthRequest("公告不存在");
        }
        Decorate(closed);
        return closed;
    }

    /// <summary>用户公告 feed。对应 Go: <c>UserAnnouncements</c>。</summary>
    public async Task<UserAnnouncementFeedDto> UserAnnouncementsAsync(
        User user, CancellationToken cancellationToken = default)
    {
        (IReadOnlyList<Announcement> announcements, long unreadCount) = await _repository
            .AnnouncementFeedAsync(user.ID, cancellationToken).ConfigureAwait(false);
        foreach (Announcement announcement in announcements)
        {
            Decorate(announcement);
        }
        return new UserAnnouncementFeedDto { Announcements = announcements, UnreadCount = unreadCount };
    }

    /// <summary>标记公告已读。对应 Go: <c>MarkAnnouncementsRead</c>。</summary>
    public async Task<long> MarkAnnouncementsReadAsync(
        User user, IReadOnlyList<string>? announcementIds, CancellationToken cancellationToken = default)
    {
        List<string> ids = announcementIds is null ? [] : [.. announcementIds];
        if (ids.Count > 5000)
        {
            throw AppError.BadAuthRequest("单次已读公告数量过多");
        }
        List<string> unique = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string raw in ids)
        {
            string value = raw.Trim();
            if (value.Length == 0 || !seen.Add(value))
            {
                continue;
            }
            if (value.Length > 64)
            {
                throw AppError.BadAuthRequest("公告 ID 无效");
            }
            unique.Add(value);
        }
        await _repository.MarkAnnouncementsReadAsync(user.ID, unique, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        (_, long unreadCount) = await _repository.AnnouncementFeedAsync(user.ID, cancellationToken)
            .ConfigureAwait(false);
        return unreadCount;
    }

    /// <summary>丢弃配图草稿。对应 Go: <c>DiscardAnnouncementImage</c>。</summary>
    public async Task DiscardAnnouncementImageAsync(
        User actor, string resourceId, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        string id = resourceId.Trim();
        if (id.Length == 0 || id.Length > 64)
        {
            throw AppError.BadAuthRequest("公告配图资源 ID 无效");
        }
        if (await _repository.AnnouncementImageDraftForUserAsync(actor.ID, id, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            throw AppError.NotFound("公告配图草稿不存在");
        }
        Resource? resource = await _repository.ResourceForUserAsync(actor.ID, id, cancellationToken)
            .ConfigureAwait(false);
        if (resource is null)
        {
            await _repository.DeleteAnnouncementImageDraftAsync(actor.ID, id, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        await EnsureResourceHasNoBusinessReferencesAsync(
            resource, ("公告草稿", resource.ID), cancellationToken).ConfigureAwait(false);
        long sharedCount = await _repository.ResourceStorageReferenceCountAsync(
            resource, [resource.ID], cancellationToken).ConfigureAwait(false);
        ResourceDeletionJob? deletionJob = sharedCount == 0
            ? ResourceDeleteService.BuildDeletionJob(resource)
            : null;
        bool discarded = await _repository.DiscardAnnouncementImageDraftAsync(
            actor.ID, resource, deletionJob, cancellationToken).ConfigureAwait(false);
        if (!discarded)
        {
            throw AppError.BadAuthRequest("公告配图已经发布，不能按草稿删除");
        }
    }

    /// <summary>公告配图资源（供下发）。对应 Go: <c>OpenAnnouncementImage</c> 的校验部分。</summary>
    public async Task<Resource> OpenAnnouncementImageAsync(
        User actor, string announcementId, CancellationToken cancellationToken = default)
    {
        Announcement? announcement = await _repository.AnnouncementAsync(
            announcementId.Trim(), cancellationToken).ConfigureAwait(false);
        if (announcement is null)
        {
            throw AppError.NotFound("公告不存在");
        }
        if (actor.Role != UserRole.UserRoleAdmin && announcement.Status != "active")
        {
            throw AppError.Forbidden("公告不可访问");
        }
        if (announcement.ImageResourceID.Length == 0)
        {
            throw AppError.NotFound("公告配图不存在");
        }
        Resource? resource = await _repository.ResourceAsync(announcement.ImageResourceID, cancellationToken)
            .ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.NotFound("公告配图不存在");
        }
        if (resource.Kind != "image" || resource.Status != ResourceStatus.ResourceStatusReady)
        {
            throw AppError.BadAuthRequest("公告配图资源不可用");
        }
        return resource;
    }

    // ------------------------------------------------------------ 内部

    /// <summary>对应 Go: <c>decorateAnnouncement</c>。</summary>
    private static void Decorate(Announcement announcement)
    {
        if (announcement.ImageResourceID.Trim().Length == 0)
        {
            return;
        }
        announcement.ImageURL = "/api/announcements/" + announcement.ID + "/image";
    }

    /// <summary>对应 Go: <c>normalizeAnnouncementInput</c>。</summary>
    private static (string Title, string Content, string Level) NormalizeInput(AnnouncementRequest request)
    {
        string title = request.Title.Trim();
        string content = request.Content.Trim();
        if (title.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写公告标题");
        }
        if (title.Length > 120)
        {
            throw AppError.BadAuthRequest("公告标题不能超过 120 个字符");
        }
        if (content.Length > 4000)
        {
            throw AppError.BadAuthRequest("公告正文不能超过 4000 个字符");
        }
        if (!ValidLevels.Contains(request.Level))
        {
            throw AppError.BadAuthRequest("公告级别无效");
        }
        if (request.ImageResourceID.Trim().Length > 64)
        {
            throw AppError.BadAuthRequest("公告配图资源 ID 无效");
        }
        return (title, content, request.Level);
    }

    /// <summary>对应 Go: <c>validateAnnouncementImageDraft</c>。</summary>
    private async Task<string> ValidateImageDraftAsync(
        User actor, string resourceId, CancellationToken cancellationToken)
    {
        string id = resourceId.Trim();
        if (id.Length == 0)
        {
            return "";
        }
        if (id.Length > 64)
        {
            throw AppError.BadAuthRequest("公告配图资源 ID 无效");
        }
        if (await _repository.AnnouncementImageDraftForUserAsync(actor.ID, id, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            throw AppError.BadAuthRequest("公告配图草稿不存在或不属于当前管理员");
        }
        Resource? resource = await _repository.ResourceForUserAsync(actor.ID, id, cancellationToken)
            .ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.BadAuthRequest("公告配图资源不存在或不属于当前管理员");
        }
        if (resource.Kind != "image" || resource.Status != ResourceStatus.ResourceStatusReady ||
            !resource.MimeType.ToLowerInvariant().StartsWith("image/", StringComparison.Ordinal))
        {
            throw AppError.BadAuthRequest("公告配图必须是上传完成的图片");
        }
        return id;
    }

    /// <summary>对应 Go: <c>ensureResourceHasNoBusinessReferences</c>。</summary>
    private async Task EnsureResourceHasNoBusinessReferencesAsync(
        Resource resource, (string Kind, string ID) ignoredDirect, CancellationToken cancellationToken)
    {
        ResourceReferenceSnapshot snapshot = await _repository.ResourceReferenceSnapshotAsync(
            resource.UserID, "", [resource.ID], cancellationToken).ConfigureAwait(false);
        foreach (ResourceDirectReference direct in snapshot.Direct)
        {
            if (direct.Kind == ignoredDirect.Kind && direct.ID == ignoredDirect.ID)
            {
                continue;
            }
            throw AppError.BadAuthRequest("公告配图仍被其他业务数据引用，已停止删除");
        }
        HashSet<string> resourceIds = new(StringComparer.Ordinal) { resource.ID };
        foreach (ResourceReferenceDocument document in snapshot.Documents)
        {
            if (UserDataService.DocumentReferencedIDs(document.PrimaryJSON, resourceIds).Count > 0 ||
                UserDataService.DocumentReferencedIDs(document.SecondaryJSON, resourceIds).Count > 0)
            {
                throw AppError.BadAuthRequest("公告配图仍被其他业务数据引用，已停止删除");
            }
        }
    }
}