#nullable enable
using System.Text.Json;
using OpenAICanvas.Application.Appearance;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;

namespace OpenAICanvas.Application;

/// <summary>
/// 孤儿资源清理：删除超过保留期且无任何业务引用的资源记录与物理对象。
/// 对应 Go: <c>app/resource_deletion_worker.go</c> 的 <c>cleanupDetachedResources</c> /
/// <c>cleanupDetachedUserResources</c>。
/// </summary>
/// <remarks>
/// 与用户手动删除刻意走**同一条强校验路径**：先做引用快照 + 外观引用检查，
/// 再以事务 + Outbox 删除，物理对象由 <see cref="ResourceDeleteService"/> 幂等清理。
/// 仓储的二次检查用于关闭「快照之后被重新挂载」的竞态。
/// </remarks>
public sealed class ResourceCleanupService
{
    /// <summary>未完成资源保留期。对应 Go: <c>incompleteResourceRetention</c>。</summary>
    public static readonly TimeSpan IncompleteRetention = TimeSpan.FromHours(1);

    /// <summary>就绪但无引用的资源保留期。对应 Go: <c>detachedReadyResourceRetention</c>。</summary>
    public static readonly TimeSpan DetachedReadyRetention = TimeSpan.FromHours(24);

    /// <summary>单轮清理上限。对应 Go 传入的 <c>500</c>。</summary>
    public const long CandidateLimit = 500;

    /// <summary>与 Go 一致的本地文件锁（避免与正在进行的上传/删除竞争）。</summary>
    private static readonly SemaphoreSlim StorageMutex = new(1, 1);

    private readonly Repository _repository;
    private readonly ResourceDeleteService _deletions;
    private readonly AppearanceService _appearance;
    private readonly AnnouncementService _announcements;
    private readonly OpenAICanvas.Platform.IRuntimePolicyProvider _policyProvider;

    public ResourceCleanupService(
        Repository repository,
        ResourceDeleteService deletions,
        AppearanceService appearance,
        AnnouncementService announcements,
        OpenAICanvas.Platform.IRuntimePolicyProvider policyProvider)
    {
        _repository = repository;
        _deletions = deletions;
        _appearance = appearance;
        _announcements = announcements;
        _policyProvider = policyProvider;
    }

    /// <summary>
    /// 一轮孤儿资源清理。返回本轮删除的资源数与生成的物理删除任务数，便于日志与测试断言。
    /// 对应 Go: <c>cleanupDetachedResources</c>（逐用户失败互不影响）。
    /// </summary>
    public async Task<(int RemovedResources, int DeletionJobs)> CleanupDetachedResourcesAsync(
        DateTime? now = null, CancellationToken cancellationToken = default)
    {
        DateTime reference = now ?? DateTime.UtcNow;
        IReadOnlyList<Resource> candidates = await _repository.ResourceCleanupCandidatesAsync(
            reference - IncompleteRetention,
            reference - DetachedReadyRetention,
            CandidateLimit,
            cancellationToken).ConfigureAwait(false);

        Dictionary<string, List<Resource>> byUser = new(StringComparer.Ordinal);
        foreach (Resource resource in candidates)
        {
            if (!byUser.TryGetValue(resource.UserID, out List<Resource>? bucket))
            {
                bucket = [];
                byUser[resource.UserID] = bucket;
            }
            bucket.Add(resource);
        }

        int removed = 0;
        int jobs = 0;
        foreach ((string userId, List<Resource> userResources) in byUser)
        {
            try
            {
                (int userRemoved, int userJobs) = await CleanupDetachedUserResourcesAsync(
                    userId, userResources, cancellationToken).ConfigureAwait(false);
                removed += userRemoved;
                jobs += userJobs;
            }
            catch (Exception)
            {
                // 与 Go 一致：单个用户失败只记日志，不阻断其他用户。
            }
        }
        return (removed, jobs);
    }

    /// <summary>单个用户的孤儿资源清理。对应 Go: <c>cleanupDetachedUserResources</c>。</summary>
    public async Task<(int RemovedResources, int DeletionJobs)> CleanupDetachedUserResourcesAsync(
        string userId, IReadOnlyList<Resource> candidates, CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
        {
            return (0, 0);
        }
        await StorageMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<string> resourceIds = [.. candidates.Select(resource => resource.ID)];
            HashSet<string> candidateSet = new(resourceIds, StringComparer.Ordinal);

            ResourceReferenceSnapshot snapshot = await _repository.ResourceReferenceSnapshotAsync(
                userId, "", resourceIds, cancellationToken).ConfigureAwait(false);

            // 站点资产（外观）是全局引用，不属于用户的画布快照。
            HashSet<string> referenced = new(StringComparer.Ordinal);
            foreach (string resourceId in (await _appearance.ResourceReferencesAsync(
                         resourceIds, cancellationToken).ConfigureAwait(false)).Keys)
            {
                referenced.Add(resourceId);
            }
            foreach (ResourceDirectReference reference in snapshot.Direct)
            {
                if (candidateSet.Contains(reference.ResourceID))
                {
                    referenced.Add(reference.ResourceID);
                }
            }
            foreach (ResourceReferenceDocument document in snapshot.Documents)
            {
                foreach (string resourceId in DocumentReferencedResourceIDs(document.PrimaryJSON, candidateSet))
                {
                    referenced.Add(resourceId);
                }
                foreach (string resourceId in DocumentReferencedResourceIDs(document.SecondaryJSON, candidateSet))
                {
                    referenced.Add(resourceId);
                }
            }

            List<Resource> detached = [.. candidates.Where(resource => !referenced.Contains(resource.ID))];
            if (detached.Count == 0)
            {
                return (0, 0);
            }
            List<string> detachedIds = [.. detached.Select(resource => resource.ID)];

            // 仍被本批之外记录共享的物理对象不进删除队列。
            Dictionary<string, Resource> physicalObjects = new(StringComparer.Ordinal);
            foreach (Resource resource in detached)
            {
                if (resource.ObjectKey.Trim().Length == 0)
                {
                    continue;
                }
                long sharedCount = await _repository.ResourceStorageReferenceCountAsync(
                    resource, detachedIds, cancellationToken).ConfigureAwait(false);
                if (sharedCount == 0)
                {
                    physicalObjects[ResourceStorageIdentity(resource)] = resource;
                }
            }
            List<ResourceDeletionJob> deletionJobs = ResourceDeletionJobsForUser(userId, physicalObjects);

            try
            {
                await _repository.DeleteDetachedResourcesAsync(detached, deletionJobs, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException error)
                when (error.Message is Repository.ResourceCleanupSetChanged
                    or Repository.ResourceCleanupStillReferenced)
            {
                // 候选集或引用在事务内变化：本轮放弃该用户，下轮重试。
                return (0, 0);
            }

            if (deletionJobs.Count > 0)
            {
                await _deletions.DrainResourceDeletionJobsAsync(cancellationToken).ConfigureAwait(false);
            }
            return (detached.Count, deletionJobs.Count);
        }
        finally
        {
            StorageMutex.Release();
        }
    }

    /// <summary>
    /// 清理超期的公告配图草稿（分批循环，直到无剩余或无可清理项）。
    /// 对应 Go: <c>cleanupStaleAnnouncementImageDrafts</c>。
    /// </summary>
    public async Task<int> CleanupStaleAnnouncementImageDraftsAsync(
        DateTime? now = null, CancellationToken cancellationToken = default)
    {
        DateTime cutoff = (now ?? DateTime.UtcNow) - AnnouncementImageDraftTtl;
        int total = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<AnnouncementImageDraft> drafts = await _repository.StaleAnnouncementImageDraftsAsync(
                cutoff, 50, cancellationToken).ConfigureAwait(false);
            if (drafts.Count == 0)
            {
                break;
            }
            int cleaned = 0;
            foreach (AnnouncementImageDraft draft in drafts)
            {
                try
                {
                    await _announcements.DiscardAnnouncementImageDraftCoreAsync(
                        draft.UserID, draft.ResourceID, cancellationToken).ConfigureAwait(false);
                    cleaned++;
                }
                catch (Exception)
                {
                    // 与 Go 一致：单条失败只记日志，继续处理其余。
                }
            }
            total += cleaned;
            if (drafts.Count < 50 || cleaned == 0)
            {
                break;
            }
        }
        return total;
    }

    /// <summary>
    /// 清理回收站中超过保留期的素材（自动清理与用户手动删除走同一条强校验路径）。
    /// 对应 Go: <c>cleanupExpiredArchivedAssets</c>。
    /// </summary>
    /// <remarks>保留天数取自运行时策略；≤0 表示不自动回收。</remarks>
    public async Task<int> CleanupExpiredArchivedAssetsAsync(
        DateTime? now = null, CancellationToken cancellationToken = default)
    {
        int retentionDays = _policyProvider.Current().Resource.RecycleBinRetentionDays;
        if (retentionDays <= 0)
        {
            return 0;
        }
        DateTime cutoff = (now ?? DateTime.UtcNow) - TimeSpan.FromDays(retentionDays);
        IReadOnlyList<Asset> expired = await _repository.ExpiredArchivedAssetsAsync(
            cutoff, 100, cancellationToken).ConfigureAwait(false);

        int deleted = 0;
        foreach (Asset asset in expired)
        {
            try
            {
                await _deletions.DeleteUserAssetWithResourcesAsync(asset.UserID, asset.ID, cancellationToken)
                    .ConfigureAwait(false);
                deleted++;
            }
            catch (Exception)
            {
                // 与 Go 一致：单条失败只记日志，继续处理其余。
            }
        }
        return deleted;
    }

    // ------------------------------------------------------------ 内部

    /// <summary>公告配图草稿的存活时长。对应 Go: <c>announcementImageDraftTTL</c>。</summary>
    private static readonly TimeSpan AnnouncementImageDraftTtl = TimeSpan.FromHours(24);

    /// <summary>对应 Go: <c>resourceDeletionJobs(userID, physicalObjects)</c>（按身份排序）。</summary>
    private static List<ResourceDeletionJob> ResourceDeletionJobsForUser(
        string userId, Dictionary<string, Resource> physicalObjects)
    {
        List<string> keys = [.. physicalObjects.Keys];
        keys.Sort(StringComparer.Ordinal);
        DateTime now = DateTime.UtcNow;
        List<ResourceDeletionJob> jobs = [];
        foreach (string key in keys)
        {
            Resource resource = physicalObjects[key];
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
        return jobs;
    }

    /// <summary>对应 Go: <c>resourceStorageIdentity</c>（空 provider 归一为 local）。</summary>
    private static string ResourceStorageIdentity(Resource resource)
    {
        string provider = resource.Provider.Trim().ToLowerInvariant();
        if (provider.Length == 0)
        {
            provider = "local";
        }
        return string.Join('\0', provider, resource.Endpoint, resource.Bucket, resource.ObjectKey);
    }

    /// <summary>
    /// 文档中引用的候选资源 ID。
    /// 对应 Go: <c>assets.DocumentReferencedIDs</c>（非法 JSON 按裸字符串兜底）。
    /// </summary>
    public static HashSet<string> DocumentReferencedResourceIDs(string raw, HashSet<string> resourceIds)
    {
        HashSet<string> matched = new(StringComparer.Ordinal);
        string trimmed = raw.Trim();
        if (trimmed.Length == 0 || resourceIds.Count == 0)
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
            foreach (string resourceId in found)
            {
                if (resourceIds.Contains(resourceId))
                {
                    matched.Add(resourceId);
                }
            }
            return matched;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string resourceId = ResourceID(value.GetString() ?? "");
            if (resourceId.Length > 0)
            {
                found.Add(resourceId);
            }
        }
        else
        {
            WalkReferenceDocument(value, "", found);
        }
        foreach (string resourceId in found)
        {
            if (resourceIds.Contains(resourceId))
            {
                matched.Add(resourceId);
            }
        }
        return matched;
    }

    /// <summary>对应 Go: <c>assets.CollectOwnedDocumentReferences</c> 的遍历规则。</summary>
    private static void WalkReferenceDocument(JsonElement value, string parentKey, HashSet<string> resourceIds)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    WalkReferenceDocument(property.Value, property.Name, resourceIds);
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement child in value.EnumerateArray())
                {
                    WalkReferenceDocument(child, parentKey, resourceIds);
                }
                break;
            case JsonValueKind.String:
                string item = value.GetString() ?? "";
                if (IsResourceLocatorField(parentKey))
                {
                    string resourceId = ResourceID(item);
                    if (resourceId.Length > 0)
                    {
                        resourceIds.Add(resourceId);
                    }
                }
                if (IsBareResourceIDField(parentKey))
                {
                    string valid = ValidID(item);
                    if (valid.Length > 0)
                    {
                        resourceIds.Add(valid);
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

    private static string ResourceID(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith("resource:", StringComparison.Ordinal))
        {
            return ValidID(trimmed["resource:".Length..]);
        }
        return ValidID(IDFromFileURL(trimmed));
    }

    private static string ValidID(string value)
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

    private static string IDFromFileURL(string value)
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
}
