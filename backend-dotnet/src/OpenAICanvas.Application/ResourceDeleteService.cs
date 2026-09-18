#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>
/// 素材删除与资源级联清理。
/// 对应 Go: <c>app/resource_delete.go</c> 的 <c>deleteUserAssetWithResources</c> /
/// <c>resourceOccupiedMessage</c> / <c>resourceDeletionJobs</c> / 本地物理删除与 drain worker。
/// </summary>
/// <remarks>
/// 删除走 Outbox 模式：业务记录与删除任务同事务提交；事务失败物理文件完全不动；
/// 提交成功后由 <see cref="DrainResourceDeletionJobsAsync"/> 幂等清理。
/// 本地 provider 内联删除；云 provider（OSS/COS/Kodo/S3）的任务保留 pending
/// （云 SDK 未移植，见 PENDING-CONFIRMATIONS.md #25）。
/// </remarks>
public sealed class ResourceDeleteService
{
    private readonly Repository _repository;
    private readonly string _dataDir;

    public ResourceDeleteService(Repository repository, string? dataDir = null)
    {
        _repository = repository;
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? "data" : dataDir!;
    }

    /// <summary>删除素材并级联清理资源。对应 Go: <c>deleteUserAssetWithResources</c>。</summary>
    public async Task DeleteUserAssetWithResourcesAsync(
        string userId,
        string assetId,
        CancellationToken cancellationToken = default)
    {
        Asset? asset = await _repository.AssetForUserAsync(userId, assetId, cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            // Go 返回裸 gorm.ErrRecordNotFound → handler failService 500。素材不存在的语义在
            // 路由层由调用方保证；这里保持一致向上抛。
            throw new InvalidOperationException("record not found");
        }
        IReadOnlyList<ResourceDirectReference> assetReferences = await _repository.AssetBusinessReferencesAsync(
            userId, assetId, cancellationToken).ConfigureAwait(false);
        (IReadOnlyList<AssetVersion> versions, IReadOnlyList<AssetRepresentation> representations) =
            await _repository.AssetResourceRecordsAsync(assetId, cancellationToken).ConfigureAwait(false);

        HashSet<string> resourceIDs = new(StringComparer.Ordinal);
        CollectOwnedDocumentReferencesOrThrow(asset.PayloadJSON, resourceIDs, "素材");
        foreach (AssetVersion version in versions)
        {
            CollectOwnedDocumentReferencesOrThrow(version.DefinitionJSON, resourceIDs, "素材版本");
        }
        foreach (AssetRepresentation representation in representations)
        {
            string resourceID = ValidCanvasResourceID(representation.ResourceID);
            if (resourceID.Length > 0)
            {
                resourceIDs.Add(resourceID);
            }
            CollectOwnedDocumentReferencesOrThrow(representation.MetadataJSON, resourceIDs, "素材表现");
        }

        string[] candidateIDs = SortedReferenceIDs(resourceIDs);
        IReadOnlyList<Resource> resources = await _repository.ResourcesForUserIDsAsync(
            userId, candidateIDs, cancellationToken).ConfigureAwait(false);
        List<string> ownedIDs = resources.Select(resource => resource.ID).ToList();
        HashSet<string> ownedIDSet = ownedIDs.ToHashSet(StringComparer.Ordinal);

        List<(string Kind, string ID, string Title)> usages = assetReferences
            .Select(reference => (reference.Kind, reference.ID, reference.Title))
            .ToList();
        if (ownedIDs.Count > 0)
        {
            ResourceReferenceSnapshot snapshot = await _repository.ResourceReferenceSnapshotAsync(
                userId, assetId, ownedIDs, cancellationToken).ConfigureAwait(false);
            HashSet<string> sharedAssetResourceIDs = new(StringComparer.Ordinal);
            foreach (ResourceDirectReference reference in snapshot.Direct)
            {
                if (!ownedIDSet.Contains(reference.ResourceID))
                {
                    continue;
                }
                if (reference.Kind == "素材")
                {
                    sharedAssetResourceIDs.Add(reference.ResourceID);
                    continue;
                }
                usages.Add((reference.Kind, reference.ID, reference.Title));
            }
            foreach (ResourceReferenceDocument document in snapshot.Documents)
            {
                HashSet<string> referencedIDs = DocumentReferencedResourceIDs(document.PrimaryJSON, ownedIDSet);
                foreach (string resourceID in DocumentReferencedResourceIDs(document.SecondaryJSON, ownedIDSet))
                {
                    referencedIDs.Add(resourceID);
                }
                if (referencedIDs.Count == 0)
                {
                    continue;
                }
                if (document.Kind == "素材")
                {
                    foreach (string resourceID in referencedIDs)
                    {
                        sharedAssetResourceIDs.Add(resourceID);
                    }
                    continue;
                }
                usages.Add((document.Kind, document.ID, document.Title));
            }
            if (sharedAssetResourceIDs.Count > 0)
            {
                // 仍被其他素材共享的物理对象不进入本素材的删除队列。
                ownedIDs = ownedIDs.Where(resourceID => !sharedAssetResourceIDs.Contains(resourceID)).ToList();
            }
        }
        string? occupied = ResourceOccupiedMessage(usages);
        if (occupied is not null)
        {
            throw AppError.BadAuthRequest(occupied);
        }

        // 所有引用校验必须先完成；仍被其他资源记录共享的物理对象不会进入删除队列。
        Dictionary<string, Resource> physicalObjects = new(StringComparer.Ordinal);
        foreach (Resource resource in resources)
        {
            if (!ownedIDSet.Contains(resource.ID))
            {
                continue;
            }
            long sharedCount = await _repository.ResourceStorageReferenceCountAsync(
                resource, ownedIDs, cancellationToken).ConfigureAwait(false);
            if (sharedCount > 0)
            {
                continue;
            }
            physicalObjects[ResourceStorageIdentity(resource)] = resource;
        }
        List<ResourceDeletionJob> deletionJobs = ResourceDeletionJobs(userId, physicalObjects);
        try
        {
            await _repository.DeleteAssetAndResourcesAsync(
                userId, assetId, ownedIDs, deletionJobs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not AppError)
        {
            throw AppError.New(500, "素材记录删除失败，请重试：" + error.Message);
        }
        if (deletionJobs.Count > 0)
        {
            // Go 异步 drain；C# 同步清理本地对象（云 provider 任务保留 pending 由后续 worker 处理）。
            await DrainResourceDeletionJobsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 幂等清理删除任务：本地对象直接删除；云 provider 任务保持 pending。
    /// 对应 Go: <c>drainResourceDeletionJobs</c>（云 provider 清理待接，见待确认 #25）。
    /// </summary>
    public async Task DrainResourceDeletionJobsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ResourceDeletionJob> jobs = await _repository.PendingResourceDeletionJobsAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (ResourceDeletionJob job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.ObjectKey))
            {
                await _repository.UpdateResourceDeletionJobAsync(
                    job.ID, "failed", "资源存储路径为空", DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
                continue;
            }
            string provider = job.Provider.Trim().ToLowerInvariant();
            if (provider.Length == 0 || provider == "local")
            {
                string? error = DeleteLocalResourceObject(job.ObjectKey);
                if (error is null)
                {
                    await _repository.UpdateResourceDeletionJobAsync(
                        job.ID, "done", "", DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _repository.UpdateResourceDeletionJobAsync(
                        job.ID, "pending", error, DateTime.UtcNow.AddSeconds(30), cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                // 云 provider：任务保留 pending，等待云 SDK 接入后的 worker 推进。
            }
        }
    }

    /// <summary>删除本地物理对象（含符号链接逃逸校验）。对应 Go: <c>deleteLocalResourceObject</c>。</summary>
    /// <returns>成功返回 null，失败返回错误文案。</returns>
    private string? DeleteLocalResourceObject(string objectKey)
    {
        try
        {
            string root = Path.GetFullPath(Path.Combine(_dataDir, "resources"));
            string target = Path.GetFullPath(Path.Combine(root, objectKey.TrimStart('/', '\\')));
            string relative = Path.GetRelativePath(root, target);
            if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            {
                return "本地资源路径超出允许目录";
            }
            if (!File.Exists(target))
            {
                // 目录存在时按 Go 语义拒绝。
                if (Directory.Exists(target))
                {
                    return "本地资源路径指向目录，已停止删除";
                }
                return null;
            }
            if (Directory.Exists(target))
            {
                return "本地资源路径指向目录，已停止删除";
            }
            string resolvedRoot = Path.GetFullPath(root);
            string resolvedTarget = ResolveWithSymlinks(target);
            string resolvedRelative = Path.GetRelativePath(resolvedRoot, resolvedTarget);
            if (resolvedRelative == "." || resolvedRelative == ".." ||
                resolvedRelative.StartsWith(".." + Path.DirectorySeparatorChar))
            {
                return "本地资源真实路径超出允许目录";
            }
            File.Delete(target);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "删除服务器本地文件失败：" + error.Message;
        }
    }

    private static string ResolveWithSymlinks(string path)
    {
        // .NET 无 EvalSymlinks；沿路径逐段解析链接的最近存在父目录。
        string? resolvedParent = Path.GetDirectoryName(path);
        if (resolvedParent is not null && Directory.Exists(resolvedParent))
        {
            try
            {
                FileInfo link = new(path);
                if (link.LinkTarget is { } target && Path.IsPathRooted(target))
                {
                    return target;
                }
            }
            catch (IOException)
            {
            }
            return resolvedParent;
        }
        return path;
    }

    private static void CollectOwnedDocumentReferencesOrThrow(
        string raw, HashSet<string> resourceIDs, string label)
    {
        try
        {
            CollectOwnedDocumentReferences(raw, resourceIDs);
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest($"{label}数据无法解析，已停止删除以避免误删文件");
        }
    }

    /// <summary>对应 Go: <c>assets.CollectOwnedDocumentReferences</c>（定位字段 + 裸 resourceId 字段，无交集求交）。</summary>
    private static void CollectOwnedDocumentReferences(string raw, HashSet<string> resourceIDs)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }
        JsonElement value;
        try
        {
            value = JsonSerializer.Deserialize<JsonElement>(trimmed);
        }
        catch (JsonException)
        {
            throw new JsonException("invalid document");
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            string resourceID = ResourceID(value.GetString() ?? "");
            if (resourceID.Length > 0)
            {
                resourceIDs.Add(resourceID);
            }
            return;
        }
        WalkReferenceDocument(value, "", resourceIDs);
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

    /// <summary>对应 Go: <c>validCanvasResourceID</c>。</summary>
    private static string ValidCanvasResourceID(string value) => ResourceID(value);

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

    private static string[] SortedReferenceIDs(HashSet<string> values)
    {
        string[] result = [.. values];
        Array.Sort(result, StringComparer.Ordinal);
        return result;
    }

    private static HashSet<string> DocumentReferencedResourceIDs(string raw, HashSet<string> resourceIDs)
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
            foreach (string resourceID in found)
            {
                if (resourceIDs.Contains(resourceID))
                {
                    matched.Add(resourceID);
                }
            }
            return matched;
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
        foreach (string resourceID in found)
        {
            if (resourceIDs.Contains(resourceID))
            {
                matched.Add(resourceID);
            }
        }
        return matched;
    }

    /// <summary>单个资源的删除任务（供公告等复用）。对应 Go: <c>resourceDeletionJobs</c> 的单元素场景。</summary>
    public static ResourceDeletionJob BuildDeletionJob(Resource resource) => new()
    {
        ID = IdGenerator.NewId(),
        UserID = resource.UserID,
        ResourceID = resource.ID,
        Provider = resource.Provider,
        Endpoint = resource.Endpoint,
        Bucket = resource.Bucket,
        StorageSettingID = resource.StorageSettingID,
        ObjectKey = resource.ObjectKey,
        Status = "pending",
        NextAttemptAt = DateTime.UtcNow,
    };

    /// <summary>对应 Go: <c>resourceDeletionJobs</c>。按存储身份排序生成 pending 任务。</summary>
    private static List<ResourceDeletionJob> ResourceDeletionJobs(
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

    /// <summary>对应 Go: <c>resourceStorageIdentity</c>。</summary>
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
    /// 占用提示。对应 Go: <c>resourceOccupiedMessage</c>（去重、标题截 32、最多 3 条 + 等计数）。
    /// </summary>
    internal static string? ResourceOccupiedMessage(
        IReadOnlyList<(string Kind, string ID, string Title)> usages)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> labels = [];
        foreach ((string kind, string id, string title) in usages)
        {
            string key = kind + "\0" + id;
            if (!seen.Add(key))
            {
                continue;
            }
            string displayTitle = title.Trim();
            if (displayTitle.Length == 0)
            {
                displayTitle = id;
            }
            if (displayTitle.Length > 32)
            {
                displayTitle = displayTitle[..32];
            }
            labels.Add(kind + "「" + displayTitle + "」");
        }
        if (labels.Count == 0)
        {
            return null;
        }
        labels.Sort(StringComparer.Ordinal);
        if (labels.Count > 3)
        {
            labels = [.. labels.Take(3), $"等 {labels.Count} 处"];
        }
        return "素材仍被" + string.Join("、", labels) + "引用，请先在对应画布、任务或业务记录中解除引用后再删除";
    }
}
