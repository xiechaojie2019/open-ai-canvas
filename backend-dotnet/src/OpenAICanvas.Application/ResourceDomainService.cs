#nullable enable
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>资源投递选项。对应 Go: <c>service.ResourceDeliveryOptions</c>。</summary>
public sealed record ResourceDeliveryOptions(
    bool ForceDirect = false,
    bool ForceProxy = false);

/// <summary>资源投递结果。对应 Go: <c>assets.ResourceDelivery</c>。</summary>
public sealed record ResourceDelivery(
    Resource Resource,
    string RedirectURL,
    Stream? Stream,
    string ContentRange,
    string AcceptRanges);

/// <summary>账号文件存储用量。对应 Go: <c>service.AccountFileStorageUsage</c>。</summary>
public sealed record AccountFileStorageUsage(
    long UsedBytes, long LimitBytes, bool OverQuota);

/// <summary>
/// 资源域服务。对应 Go: <c>app/resource.go</c> 的 CRUD 与投递部分。
/// </summary>
/// <remarks>
/// multipart 上传与远端下载依赖文件系统 + 出站 HTTP 客户端（阶段 5 资源上传节点）；
/// 本轮落地 7 条 CRUD/投递路由（列表/详情/存储用量/OSS 直链/文件下发/ARK 同步/导入），
/// multipart 上传三件套（uploads/chunks/complete）留待资源节点一并接线。
/// </remarks>
public sealed class ResourceDomainService
{
    private readonly Repository _repository;
    private readonly string _dataDir;

    public ResourceDomainService(Repository repository, string? dataDir = null)
    {
        _repository = repository;
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? "data" : dataDir!;
    }

    /// <summary>用户资源列表（按更新时间倒序，剥离 publicURL）。对应 Go: <c>Resources</c>。</summary>
    public async Task<IReadOnlyList<Resource>> ListResourcesAsync(
        string userId, long limit, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Resource> resources = await _repository.ResourcesAsync(userId, limit, cancellationToken)
            .ConfigureAwait(false);
        foreach (Resource resource in resources)
        {
            resource.PublicURL = "";
        }
        return resources;
    }

    /// <summary>账号存储用量（按包策略上限）。对应 Go: <c>AccountFileStorageUsage</c>。</summary>
    public async Task<AccountFileStorageUsage> AccountFileStorageUsageAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        (long usedBytes, long limitBytes) = await _repository.UserStorageUsageAsync(
            userId, cancellationToken).ConfigureAwait(false);
        long limit = Math.Max(1, limitBytes);
        return new AccountFileStorageUsage(usedBytes, limitBytes, usedBytes > limitBytes);
    }

    /// <summary>单个资源（剥离 publicURL）。对应 Go: <c>Resource</c>。</summary>
    public async Task<Resource?> GetResourceAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        Resource? resource = await _repository.ResourceForUserAsync(userId, id, cancellationToken)
            .ConfigureAwait(false);
        if (resource is not null)
        {
            resource.PublicURL = "";
        }
        return resource;
    }

    /// <summary>同步到方舟私有资产。对应 Go: <c>SyncResourceToArkPrivateAsset</c>（依赖 #25 云 SDK，留待）。</summary>
    public Task SyncResourceToArkPrivateAssetAsync(
        string userId, string resourceId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Ark 私有资产同步依赖云 SDK（待确认 #25）");

    /// <summary>从公网 URL 导入资源（依赖出站下载，待接）。对应 Go: <c>ImportResourceURL</c>。</summary>
    public Task<Resource> ImportResourceUrlAsync(
        string userId, string url, string kind, int width, int height, long durationMs,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("URL 导入依赖出站 HTTP 客户端（阶段 5 资源节点）");

    /// <summary>短时签名直链（云 provider）。对应 Go: <c>DirectResourceURL</c>。</summary>
    public async Task<string> DirectResourceUrlAsync(
        string userId, string resourceId, CancellationToken cancellationToken = default)
    {
        Resource? resource = await _repository.ResourceForUserAsync(
            userId, resourceId, cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.NotFound("资源不存在");
        }
        return await DirectLocalResourceUrlAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>本地资源投递（含 Range 与 ETag）。对应 Go: <c>PrepareResourceDelivery</c>。</summary>
    public async Task<ResourceDelivery> PrepareResourceDeliveryAsync(
        string userId, string resourceId, ResourceDeliveryOptions options,
        CancellationToken cancellationToken = default)
    {
        Resource? resource = await _repository.ResourceForUserAsync(
            userId, resourceId, cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.NotFound("资源不存在");
        }
        if (resource.Status != ResourceStatus.ResourceStatusReady)
        {
            throw AppError.BadAuthRequest("资源尚未上传完成");
        }
        ResourceStream stream = await OpenResourceRangeAsync(resource, null, cancellationToken)
            .ConfigureAwait(false);
        return new ResourceDelivery(
            resource, RedirectURL: "", Stream: stream.Body, ContentRange: stream.ContentRange,
            AcceptRanges: stream.AcceptRanges);
    }

    /// <summary>资源 multipart 上传。对应 Go: <c>UploadResource</c>，留待资源上传节点。</summary>
    public Task<Resource> UploadResourceAsync(
        string userId, string fileName, byte[] data, string contentType,
        string kind, int width, int height, long durationMs,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("multipart 上传依赖文件系统 + Outbox（阶段 5 资源节点）");

    // ------------------------------------------------------------ 内部（本地投递）

    private async Task<ResourceStream> OpenResourceRangeAsync(
        Resource resource, string? rangeHeader, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(resource.Provider) &&
            !string.Equals(resource.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            // 云 provider 留待 #25
            throw new NotImplementedException("云资源投递依赖云 SDK（待确认 #25）");
        }
        string root = Path.GetFullPath(Path.Combine(_dataDir, "resources"));
        string path = Path.GetFullPath(Path.Combine(root,
            resource.ObjectKey.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path))
        {
            throw AppError.NotFound("资源文件不存在");
        }
        FileStream fs = File.OpenRead(path);
        return new ResourceStream(resource, fs, ContentRange: "", AcceptRanges: "bytes");
    }

    /// <summary>对应 Go: <c>resourceStorageURL</c>（本地直链相对路径）。</summary>
    private static Task<string> DirectLocalResourceUrlAsync(
        Resource resource, CancellationToken cancellationToken)
    {
        string objectKey = resource.ObjectKey.TrimStart('/');
        return Task.FromResult("/api/admin/resources/" + resource.ID + "/file");
    }
}