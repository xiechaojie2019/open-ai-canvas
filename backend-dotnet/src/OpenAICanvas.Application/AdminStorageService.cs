#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>管理端资源删除请求。对应 Go: <c>app.AdminResourceDeleteRequest</c>。</summary>
public sealed class AdminResourceDeleteRequest
{
    [JsonPropertyName("resourceIds")]
    public IReadOnlyList<string> ResourceIDs { get; init; } = [];
}

/// <summary>资源引用视图。对应 Go: <c>app.AdminResourceReferenceView</c>。</summary>
public sealed class AdminResourceReferenceView
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";
}

/// <summary>被阻塞的删除项。对应 Go: <c>app.AdminResourceDeleteBlocked</c>。</summary>
public sealed class AdminResourceDeleteBlocked
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";

    [JsonPropertyName("references")]
    public IReadOnlyList<AdminResourceReferenceView> References { get; init; } = [];
}

/// <summary>批量删除结果。对应 Go: <c>app.AdminResourceDeleteResult</c>。</summary>
public sealed class AdminResourceDeleteResult
{
    [JsonPropertyName("deleted")]
    public IReadOnlyList<string> Deleted { get; init; } = [];

    [JsonPropertyName("blocked")]
    public IReadOnlyList<AdminResourceDeleteBlocked> Blocked { get; init; } = [];
}

/// <summary>管理端资源查询条件。对应 Go: <c>app.AdminResourceQuery</c>。</summary>
public sealed record AdminResourceQuery(
    string Keyword, string Kind, string Status, string Provider, string UserID, long Page, long Limit);

/// <summary>
/// 管理端存储管理：资源分页、按 ID 删除（引用检查 + Outbox）、管理员直连下发。
/// 对应 Go: <c>app/admin_storage.go</c> 与 <c>app/admin_storage_delete.go</c>。
/// </summary>
/// <remarks>
/// 外观配置引用检查（Go 的 <c>appearanceResourceReferences</c>）依赖 7.4 外观设置模块，
/// 该模块尚未移植；接入后需在此补充，否则外观引用的资源可能被误删。
/// </remarks>
public sealed class AdminStorageService
{
    private const long MaxDeleteCount = 100;

    private readonly Repository _repository;
    private readonly ResourceDomainService _resources;
    private readonly ResourceDeleteService _deletions;
    private readonly OpenAICanvas.Application.Appearance.AppearanceService _appearance;

    public AdminStorageService(
        Repository repository,
        ResourceDomainService resources,
        ResourceDeleteService deletions,
        OpenAICanvas.Application.Appearance.AppearanceService appearance)
    {
        _repository = repository;
        _resources = resources;
        _deletions = deletions;
        _appearance = appearance;
    }

    /// <summary>资源分页。对应 Go: <c>AdminResourcePage</c>。</summary>
    public async Task<AdminResourcePageDto> AdminResourcePageAsync(
        User? actor, AdminResourceQuery query, long defaultLimit, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (AdminResourceFilter filter, long page, long limit) = NormalizeAdminResourceQuery(query, defaultLimit);

        (IReadOnlyList<Resource> resources, long total) = await _repository.AdminResourcesAsync(
            filter, cancellationToken).ConfigureAwait(false);

        List<string> userIds = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (Resource resource in resources)
        {
            if (seen.Add(resource.UserID))
            {
                userIds.Add(resource.UserID);
            }
        }
        Dictionary<string, User> users = await _repository.UsersByIDsAsync(userIds, cancellationToken)
            .ConfigureAwait(false);

        List<AdminStorageResourceViewDto> items = new(resources.Count);
        foreach (Resource resource in resources)
        {
            users.TryGetValue(resource.UserID, out User? owner);
            items.Add(new AdminStorageResourceViewDto
            {
                ID = resource.ID,
                UserID = resource.UserID,
                UserName = AdminResourceUserName(owner),
                Kind = resource.Kind,
                Status = resource.Status,
                Provider = NormalizedResourceProvider(resource.Provider),
                Bucket = resource.Bucket,
                ObjectKey = resource.ObjectKey,
                MimeType = resource.MimeType,
                Size = resource.Size,
                PhysicalBytes = resource.Status == ResourceStatus.ResourceStatusReady ? resource.Size : 0,
                Width = resource.Width,
                Height = resource.Height,
                DurationMs = resource.DurationMs,
                FileURL = "/api/admin/resources/" + resource.ID + "/file",
                CreatedAt = resource.CreatedAt,
                UpdatedAt = resource.UpdatedAt,
            });
        }
        return new AdminResourcePageDto { Items = items, Total = total, Page = page, PageSize = limit };
    }

    /// <summary>管理员直连下发。对应 Go: <c>OpenResourceRangeAsAdmin</c>。</summary>
    public async Task<ResourceStream> OpenResourceRangeAsAdminAsync(
        User? actor, string id, string? rangeHeader, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        Resource? resource = await _repository.ResourceAsync(id.Trim(), cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.NotFound("资源不存在");
        }
        return await _resources.OpenResourceForOwnerAsync(resource, rangeHeader, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 批量删除资源：不可删项进入 blocked，其余进事务删除 + 物理清理队列。
    /// 对应 Go: <c>DeleteAdminResources</c>。
    /// </summary>
    public async Task<AdminResourceDeleteResult> DeleteAdminResourcesAsync(
        User? actor, AdminResourceDeleteRequest request, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        List<string> resourceIds = NormalizeAdminResourceDeleteIds(request.ResourceIDs);

        IReadOnlyList<Resource> resources = await _repository.AdminResourcesByIDsAsync(
            resourceIds, cancellationToken).ConfigureAwait(false);
        Dictionary<string, Resource> resourcesByID = new(StringComparer.Ordinal);
        Dictionary<string, List<Resource>> resourcesByUser = new(StringComparer.Ordinal);
        foreach (Resource resource in resources)
        {
            resourcesByID[resource.ID] = resource;
            if (!resourcesByUser.TryGetValue(resource.UserID, out List<Resource>? bucket))
            {
                bucket = [];
                resourcesByUser[resource.UserID] = bucket;
            }
            bucket.Add(resource);
        }

        Dictionary<string, AdminResourceDeleteBlocked> blockedByID = new(StringComparer.Ordinal);
        foreach (string resourceId in resourceIds)
        {
            if (!resourcesByID.ContainsKey(resourceId))
            {
                blockedByID[resourceId] = new AdminResourceDeleteBlocked
                {
                    ID = resourceId,
                    Reason = "资源不存在",
                    References = [],
                };
            }
        }

        foreach ((string userId, List<Resource> userResources) in resourcesByUser)
        {
            List<string> userResourceIds = [.. userResources.Select(resource => resource.ID)];
            ResourceReferenceSnapshot snapshot = await _repository.ResourceReferenceSnapshotAsync(
                userId, "", userResourceIds, cancellationToken).ConfigureAwait(false);
            foreach ((string resourceId, List<AdminResourceReferenceView> references) in
                     AdminResourceReferences(snapshot, userResources))
            {
                // 公告草稿及其资源绑定在删除事务内级联清理，不构成阻塞引用。
                List<AdminResourceReferenceView> blocking = [.. references.Where(reference => reference.Kind != "公告草稿")];
                if (blocking.Count == 0)
                {
                    continue;
                }
                blockedByID[resourceId] = new AdminResourceDeleteBlocked
                {
                    ID = resourceId,
                    Reason = "资源仍被业务数据引用",
                    References = blocking,
                };
            }
        }

        // 外观配置引用（Logo / 视频 / 封面）：配置不可读时 fail closed，避免误删。
        foreach ((string resourceId, List<AdminResourceReferenceView> references) in
                 await _appearance.ResourceReferencesAsync(resourceIds, cancellationToken).ConfigureAwait(false))
        {
            AdminResourceDeleteBlocked existing = blockedByID.TryGetValue(resourceId, out AdminResourceDeleteBlocked? found)
                ? found
                : new AdminResourceDeleteBlocked { ID = resourceId };
            IReadOnlyList<AdminResourceReferenceView> merged = existing.References;
            foreach (AdminResourceReferenceView reference in references)
            {
                merged = AppendUnique(merged, reference);
            }
            blockedByID[resourceId] = new AdminResourceDeleteBlocked
            {
                ID = resourceId,
                Reason = "资源仍被业务数据引用",
                References = merged,
            };
        }

        foreach (ResourceDirectReference reference in await _repository
                     .AnnouncementResourceReferencesAsync(resourceIds, cancellationToken).ConfigureAwait(false))
        {
            AdminResourceDeleteBlocked existing = blockedByID.TryGetValue(
                reference.ResourceID, out AdminResourceDeleteBlocked? found)
                ? found
                : new AdminResourceDeleteBlocked { ID = reference.ResourceID };
            blockedByID[reference.ResourceID] = new AdminResourceDeleteBlocked
            {
                ID = reference.ResourceID,
                Reason = "资源仍被业务数据引用",
                References = AppendUnique(
                    existing.References,
                    new AdminResourceReferenceView { Kind = reference.Kind, ID = reference.ID, Title = reference.Title }),
            };
        }

        List<Resource> deletable = [];
        List<string> deletableIds = [];
        foreach (string resourceId in resourceIds)
        {
            if (!resourcesByID.TryGetValue(resourceId, out Resource? resource))
            {
                continue;
            }
            if (blockedByID.ContainsKey(resourceId))
            {
                continue;
            }
            if (!SupportedResourceDeleteProvider(resource.Provider))
            {
                blockedByID[resourceId] = new AdminResourceDeleteBlocked
                {
                    ID = resourceId,
                    Reason = "资源使用了不支持的存储类型，无法安全删除",
                    References = [],
                };
                continue;
            }
            deletable.Add(resource);
            deletableIds.Add(resource.ID);
        }

        // 仍被同批删除范围外的记录共享的物理对象不进删除队列。
        Dictionary<string, Resource> physicalObjects = new(StringComparer.Ordinal);
        HashSet<string> checkedPhysical = new(StringComparer.Ordinal);
        foreach (Resource resource in deletable)
        {
            if (resource.ObjectKey.Trim().Length == 0)
            {
                continue;
            }
            string identity = ResourceStorageIdentity(resource);
            if (!checkedPhysical.Add(identity))
            {
                continue;
            }
            long sharedCount = await _repository.ResourceStorageReferenceCountAsync(
                resource, deletableIds, cancellationToken).ConfigureAwait(false);
            if (sharedCount == 0)
            {
                physicalObjects[identity] = resource;
            }
        }

        List<ResourceDeletionJob> deletionJobs = ResourceDeletionJobsForResources(physicalObjects);
        HashSet<string> queuedResourceIds = [.. deletionJobs.Select(job => job.ResourceID)];

        List<AdminAuditEvent> audits = new(deletable.Count);
        foreach (Resource resource in deletable)
        {
            audits.Add(NewResourceDeleteAuditEvent(actor!, resource, queuedResourceIds.Contains(resource.ID)));
        }

        try
        {
            await _repository.DeleteAdminResourcesAsync(deletable, deletionJobs, audits, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
            when (error.Message is Repository.AdminResourceDeleteChanged or Repository.AdminResourceStillReferenced)
        {
            throw AppError.BadAuthRequest("资源状态或引用已变化，请刷新后重试");
        }

        if (deletionJobs.Count > 0)
        {
            // Go 异步 drain；C# 同步清理本地对象（云 provider 任务保留 pending 由 worker 推进）。
            await _deletions.DrainResourceDeletionJobsAsync(cancellationToken).ConfigureAwait(false);
        }

        HashSet<string> deletedSet = [.. deletable.Select(resource => resource.ID)];
        List<string> deleted = [];
        List<AdminResourceDeleteBlocked> blocked = [];
        foreach (string resourceId in resourceIds)
        {
            if (deletedSet.Contains(resourceId))
            {
                deleted.Add(resourceId);
            }
            else if (blockedByID.TryGetValue(resourceId, out AdminResourceDeleteBlocked? entry))
            {
                blocked.Add(entry);
            }
        }
        return new AdminResourceDeleteResult { Deleted = deleted, Blocked = blocked };
    }

    // ------------------------------------------------------------ 内部

    private static (AdminResourceFilter Filter, long Page, long Limit) NormalizeAdminResourceQuery(
        AdminResourceQuery query, long defaultLimit)
    {
        (long page, long limit) = NormalizeAdminPage(query.Page, query.Limit, defaultLimit);
        AdminResourceFilter filter = new(
            Keyword: query.Keyword.Trim(),
            Kind: query.Kind.Trim().ToLowerInvariant(),
            Status: query.Status.Trim().ToLowerInvariant(),
            Provider: query.Provider.Trim().ToLowerInvariant(),
            UserID: query.UserID.Trim(),
            Limit: limit,
            Offset: (page - 1) * limit);

        if (filter.Kind.Length > 0 && filter.Kind is not ("image" or "video" or "audio" or "file"))
        {
            throw AppError.BadAuthRequest("资源类型筛选无效");
        }
        if (filter.Status.Length > 0 &&
            filter.Status is not ("pending" or "ready" or "failed" or "deleted"))
        {
            throw AppError.BadAuthRequest("资源状态筛选无效");
        }
        if (filter.Provider.Length > 0 &&
            filter.Provider is not ("local" or "aliyun" or "tencent" or "qiniu" or "s3"))
        {
            throw AppError.BadAuthRequest("资源存储位置筛选无效");
        }
        if (filter.Keyword.Length > 200 || filter.UserID.Length > 64)
        {
            throw AppError.BadAuthRequest("资源筛选条件过长");
        }
        return (filter, page, limit);
    }

    /// <summary>对应 Go: <c>normalizeAdminPage</c>（默认 20，上限 100，页码从 1 起）。</summary>
    private static (long Page, long Limit) NormalizeAdminPage(long page, long limit, long defaultLimit)
    {
        if (limit <= 0)
        {
            limit = defaultLimit;
        }
        if (limit > 100)
        {
            limit = 100;
        }
        if (page <= 0)
        {
            page = 1;
        }
        return (page, limit);
    }

    private static List<string> NormalizeAdminResourceDeleteIds(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            throw AppError.BadAuthRequest("请选择要删除的资源");
        }
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string value in values)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0 || trimmed.Length > 80)
            {
                throw AppError.BadAuthRequest("资源 ID 无效");
            }
            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }
        if (result.Count == 0 || result.Count > MaxDeleteCount)
        {
            throw AppError.BadAuthRequest("单次最多删除 100 个资源");
        }
        return result;
    }

    /// <summary>
    /// 从引用快照归集每个资源的阻塞引用（直接引用 + 文档内嵌引用），按 kind/id 排序。
    /// 对应 Go: <c>adminResourceReferences</c>。
    /// </summary>
    private static Dictionary<string, List<AdminResourceReferenceView>> AdminResourceReferences(
        ResourceReferenceSnapshot snapshot, IReadOnlyList<Resource> resources)
    {
        Dictionary<string, List<AdminResourceReferenceView>> result = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> seen = new(StringComparer.Ordinal);
        foreach (ResourceDirectReference reference in snapshot.Direct)
        {
            AppendAdminResourceReference(result, seen, reference.ResourceID,
                new AdminResourceReferenceView { Kind = reference.Kind, ID = reference.ID, Title = reference.Title });
        }
        foreach (Resource resource in resources)
        {
            HashSet<string> candidate = new([resource.ID], StringComparer.Ordinal);
            foreach (ResourceReferenceDocument document in snapshot.Documents)
            {
                if (DocumentReferencesResources(document.PrimaryJSON, candidate) ||
                    DocumentReferencesResources(document.SecondaryJSON, candidate))
                {
                    AppendAdminResourceReference(result, seen, resource.ID,
                        new AdminResourceReferenceView { Kind = document.Kind, ID = document.ID, Title = document.Title });
                }
            }
        }
        foreach (List<AdminResourceReferenceView> references in result.Values)
        {
            references.Sort((left, right) => left.Kind != right.Kind
                ? string.CompareOrdinal(left.Kind, right.Kind)
                : string.CompareOrdinal(left.ID, right.ID));
        }
        return result;
    }

    private static void AppendAdminResourceReference(
        Dictionary<string, List<AdminResourceReferenceView>> result,
        Dictionary<string, HashSet<string>> seen,
        string resourceId,
        AdminResourceReferenceView reference)
    {
        if (resourceId.Length == 0)
        {
            return;
        }
        if (!seen.TryGetValue(resourceId, out HashSet<string>? keys))
        {
            keys = new HashSet<string>(StringComparer.Ordinal);
            seen[resourceId] = keys;
        }
        if (!keys.Add(reference.Kind + "\0" + reference.ID))
        {
            return;
        }
        if (!result.TryGetValue(resourceId, out List<AdminResourceReferenceView>? list))
        {
            list = [];
            result[resourceId] = list;
        }
        list.Add(reference);
    }

    private static IReadOnlyList<AdminResourceReferenceView> AppendUnique(
        IReadOnlyList<AdminResourceReferenceView> references, AdminResourceReferenceView reference)
    {
        foreach (AdminResourceReferenceView existing in references)
        {
            if (existing.Kind == reference.Kind && existing.ID == reference.ID)
            {
                return references;
            }
        }
        return [.. references, reference];
    }

    /// <summary>文档中是否引用了候选资源 ID 集合。对应 Go: <c>documentReferencesResources</c>。</summary>
    private static bool DocumentReferencesResources(string raw, HashSet<string> resourceIds)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0 || resourceIds.Count == 0)
        {
            return false;
        }
        try
        {
            return WalkForResources(JsonSerializer.Deserialize<JsonElement>(trimmed), resourceIds);
        }
        catch (JsonException)
        {
            // 非法 JSON 按 Go 语义视为不引用（与删除校验路径不同：那里 fail closed）。
            return false;
        }
    }

    private static bool WalkForResources(JsonElement value, HashSet<string> resourceIds)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (WalkForResources(property.Value, resourceIds))
                    {
                        return true;
                    }
                }
                return false;
            case JsonValueKind.Array:
                foreach (JsonElement child in value.EnumerateArray())
                {
                    if (WalkForResources(child, resourceIds))
                    {
                        return true;
                    }
                }
                return false;
            case JsonValueKind.String:
                return resourceIds.Contains((value.GetString() ?? "").Trim());
            default:
                return false;
        }
    }

    /// <summary>对应 Go: <c>supportedResourceDeleteProvider</c>。</summary>
    private static bool SupportedResourceDeleteProvider(string provider)
    {
        string normalized = NormalizedResourceProvider(provider);
        return normalized is "local" or "aliyun" or "tencent" or "qiniu" or "s3";
    }

    /// <summary>对应 Go: <c>normalizedResourceProvider</c>（空值归一为 local）。</summary>
    private static string NormalizedResourceProvider(string? provider)
    {
        string normalized = (provider ?? "").Trim().ToLowerInvariant();
        return normalized.Length == 0 ? "local" : normalized;
    }

    /// <summary>对应 Go: <c>adminResourceUserName</c>（displayName → username → id）。</summary>
    private static string AdminResourceUserName(User? user)
    {
        if (user is null)
        {
            return "";
        }
        string displayName = (user.DisplayName ?? "").Trim();
        if (displayName.Length > 0)
        {
            return displayName;
        }
        string username = (user.Username ?? "").Trim();
        if (username.Length > 0)
        {
            return username;
        }
        return user.ID;
    }

    /// <summary>对应 Go: <c>resourceStorageIdentity</c>。</summary>
    private static string ResourceStorageIdentity(Resource resource)
    {
        string provider = NormalizedResourceProvider(resource.Provider);
        return string.Join('\0', provider, resource.Endpoint, resource.Bucket, resource.ObjectKey);
    }

    /// <summary>对应 Go: <c>resourceDeletionJobsForResources</c>（按 userId 分组、身份排序）。</summary>
    private static List<ResourceDeletionJob> ResourceDeletionJobsForResources(
        Dictionary<string, Resource> physicalObjects)
    {
        Dictionary<string, Dictionary<string, Resource>> byUser = new(StringComparer.Ordinal);
        foreach ((string identity, Resource resource) in physicalObjects)
        {
            if (!byUser.TryGetValue(resource.UserID, out Dictionary<string, Resource>? bucket))
            {
                bucket = new Dictionary<string, Resource>(StringComparer.Ordinal);
                byUser[resource.UserID] = bucket;
            }
            bucket[identity] = resource;
        }
        List<string> userIds = [.. byUser.Keys];
        userIds.Sort(StringComparer.Ordinal);

        DateTime now = DateTime.UtcNow;
        List<ResourceDeletionJob> jobs = [];
        foreach (string userId in userIds)
        {
            List<string> keys = [.. byUser[userId].Keys];
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                Resource resource = byUser[userId][key];
                jobs.Add(new ResourceDeletionJob
                {
                    ID = IdGenerator.NewId(),
                    UserID = userId,
                    ResourceID = resource.ID,
                    Provider = resource.Provider,
                    Endpoint = resource.Endpoint,
                    Bucket = resource.Bucket,
                    StorageSettingID = resource.StorageSettingID,
                    ObjectKey = resource.ObjectKey,
                    Status = "pending",
                    NextAttemptAt = now,
                });
            }
        }
        return jobs;
    }

    /// <summary>对应 Go: <c>newAdminAuditEvent(actor, "resource.delete", ...)</c>。</summary>
    private static AdminAuditEvent NewResourceDeleteAuditEvent(User actor, Resource resource, bool physicalDeleteQueued)
    {
        string metadata = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["userId"] = resource.UserID,
            ["kind"] = resource.Kind,
            ["provider"] = NormalizedResourceProvider(resource.Provider),
            ["objectKey"] = resource.ObjectKey,
            ["physicalDeleteQueued"] = physicalDeleteQueued,
        });
        if (metadata.Length > 4000)
        {
            metadata = metadata[..4000];
        }
        return new AdminAuditEvent
        {
            ID = IdGenerator.NewId(),
            ActorUserID = actor.ID,
            Action = "resource.delete",
            TargetType = "resource",
            TargetID = resource.ID,
            Summary = "管理员删除存储资源",
            MetadataJSON = metadata,
            CreatedAt = DateTime.UtcNow,
        };
    }
}
