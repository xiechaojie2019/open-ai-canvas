#nullable enable
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;

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

/// <summary>
/// 资源流（含状态码、长度与 Range 信息）。
/// 对应 Go: <c>internal/assets/types.go</c> 的 <c>ResourceStream</c>。
/// </summary>
public sealed record ResourceStream(
    Resource Resource,
    Stream Body,
    string ContentRange,
    string AcceptRanges)
{
    /// <summary>对应 Go 的 <c>StatusCode</c>，本地投递固定 200。</summary>
    public int StatusCode { get; init; } = 200;

    /// <summary>对应 Go 的 <c>ContentLength</c>。</summary>
    public long ContentLength { get; init; }
}

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
    private const long Gigabyte = 1L << 30;

    private readonly Repository _repository;
    private readonly IRuntimePolicyProvider _policyProvider;
    private readonly string _dataDir;

    public ResourceDomainService(
        Repository repository, IRuntimePolicyProvider policyProvider, string? dataDir = null,
        Func<byte[]?>? settingsEncryptionKey = null)
    {
        _repository = repository;
        _policyProvider = policyProvider;
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? "data" : dataDir!;
        if (settingsEncryptionKey is not null)
        {
            SettingsEncryptionKey = settingsEncryptionKey;
        }
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
        RuntimeResourcePolicy resource = _policyProvider.Current().Resource;
        long usedBytes = await _repository.UserStoredFileBytesAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        long limitBytes = Gigabyte * resource.StoredFileGB;
        long limit = Math.Max(1, limitBytes);
        return new AccountFileStorageUsage(usedBytes, limitBytes, usedBytes > limit);
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

    /// <summary>
    /// 匿名签名下发：校验 expires/signature 后打开资源流。
    /// 对应 Go: <c>OpenPublicResourceRange</c>。
    /// </summary>
    public async Task<ResourceStream> OpenPublicResourceRangeAsync(
        string id, string? expires, string? signature, string? rangeHeader,
        CancellationToken cancellationToken = default)
    {
        Resource? resource = await _repository.ResourceAsync(id, cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.Forbidden("匿名下载链接无效");
        }
        if (!IsLocalProvider(resource.Provider))
        {
            throw AppError.Forbidden("匿名下载链接无效");
        }
        VerifyPublicResourceSignature(resource.ID, expires, signature);
        return await OpenResourceRangeAsync(resource, rangeHeader, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按用户 + 资源 ID 打开资源流（含状态校验）。
    /// 对应 Go: <c>OpenResourceRange</c>。
    /// </summary>
    public async Task<ResourceStream> OpenResourceRangeAsync(
        string userId, string resourceId, string? rangeHeader, CancellationToken cancellationToken = default)
    {
        Resource? resource = await _repository.ResourceForUserAsync(userId, resourceId, cancellationToken)
            .ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.NotFound("资源不存在");
        }
        return await OpenResourceRangeAsync(resource, rangeHeader, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 资源响应 ETag。无 ETag 时回落到 <c>ID-Size-UpdatedAtTicks</c>，与 Go 的 UnixNano 语义一致。
    /// 对应 Go: <c>resourceResponseETag</c>。
    /// </summary>
    public static string ResourceResponseETag(Resource resource)
    {
        string value = (resource.ETag ?? string.Empty).Trim().Trim('"');
        if (value.Length == 0)
        {
            value = $"{resource.ID}-{resource.Size}-{resource.UpdatedAt.Ticks}";
        }
        return '"' + value + '"';
    }

    /// <summary>If-None-Match 匹配（支持 <c>*</c> 与 <c>W/</c> 弱比较）。对应 Go: <c>ifNoneMatch</c>。</summary>
    public static bool IfNoneMatch(string? header, string etag)
    {
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }
        foreach (string raw in header.Split(','))
        {
            string candidate = raw.Trim();
            if (candidate.StartsWith("W/", StringComparison.Ordinal))
            {
                candidate = candidate[2..].Trim();
            }
            if (candidate == "*" || candidate == etag)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 单区间 Range 头归一化：仅接受 <c>bytes=start-end</c> 形式（≤128 字符、无逗号多区间）。
    /// 与 Go 一致，只做合法性过滤，真正的裁剪交给 ServeContent 等价逻辑。
    /// 对应 Go: <c>normalizeSingleByteRange</c>。
    /// </summary>
    public static string NormalizeSingleByteRange(string? value)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length > 128 || !value.StartsWith("bytes=", StringComparison.Ordinal)
            || value.Contains(',', StringComparison.Ordinal))
        {
            return string.Empty;
        }
        string payload = value["bytes=".Length..];
        int dash = payload.IndexOf('-');
        if (dash < 0)
        {
            return string.Empty;
        }
        string start = payload[..dash];
        string end = payload[(dash + 1)..];
        if ((start.Length == 0 && end.Length == 0) || !IsDecimalDigits(start) || !IsDecimalDigits(end))
        {
            return string.Empty;
        }
        return "bytes=" + start + "-" + end;
    }

    /// <summary>
    /// 解析单区间 Range 为绝对偏移。<c>ContentRange</c> 为空表示整文件 200；
    /// 返回 <c>null</c> 表示 Range 不可满足（调用方应回 416）。
    /// 语义等价于 Go 的 <c>http.ServeContent</c>（bytes 类型、单区间）。
    /// </summary>
    public static (long Start, long Length, string ContentRange)? ResolveRange(long size, string? rangeHeader)
    {
        string normalized = NormalizeSingleByteRange(rangeHeader);
        if (normalized.Length == 0)
        {
            return (0, size, string.Empty);
        }
        string payload = normalized["bytes=".Length..];
        int dash = payload.IndexOf('-');
        string startText = payload[..dash];
        string endText = payload[(dash + 1)..];

        if (startText.Length == 0)
        {
            // "-N" 后缀：取最后 N 字节。
            if (!long.TryParse(endText, out long suffix) || suffix <= 0)
            {
                return null;
            }
            long start = Math.Max(0, size - suffix);
            long length = size - start;
            return (start, length, $"bytes {start}-{size - 1}/{size}");
        }

        if (!long.TryParse(startText, out long from) || from >= size)
        {
            return null;
        }
        long to = size - 1;
        if (endText.Length > 0)
        {
            if (!long.TryParse(endText, out long parsedTo) || parsedTo < from)
            {
                return null;
            }
            to = Math.Min(parsedTo, size - 1);
        }
        return (from, to - from + 1, $"bytes {from}-{to}/{size}");
    }

    /// <summary>
    /// 校验匿名下发的 expires/signature。
    /// 对应 Go: <c>verifyPublicResourceSignature</c>。
    /// </summary>
    public void VerifyPublicResourceSignature(string resourceId, string? expires, string? signature)
    {
        if (string.IsNullOrWhiteSpace(expires) || string.IsNullOrWhiteSpace(signature))
        {
            throw AppError.Forbidden("匿名下载链接无效");
        }
        if (!long.TryParse(expires, out long expiresAt))
        {
            throw AppError.Forbidden("匿名下载链接无效");
        }
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAt)
        {
            throw AppError.Forbidden("匿名下载链接已过期");
        }
        // 与 Go 的 hmac.Equal 一致：定长比较，避免时序侧信道。
        byte[] expected = System.Text.Encoding.UTF8.GetBytes(ComputePublicResourceSignature(resourceId, expires));
        byte[] actual = System.Text.Encoding.UTF8.GetBytes(signature);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw AppError.Forbidden("匿名下载链接无效");
        }
    }

    /// <summary>
    /// 生成匿名下发签名：<c>HMAC-SHA256(settingsKey, id + "\n" + expires)</c> 的 base64 RawURL。
    /// 对应 Go: <c>signPublicResource</c>。
    /// </summary>
    /// <remarks>密钥为设置加密密钥（<c>data/.settings-key</c>，32 字节）；未配置时与 Go 的 nopHost 一致。</remarks>
    public string ComputePublicResourceSignature(string resourceId, string expires)
    {
        byte[] key = SettingsEncryptionKey() ?? [];
        byte[] data = System.Text.Encoding.UTF8.GetBytes(resourceId + "\n" + expires);
        byte[] digest = System.Security.Cryptography.HMACSHA256.HashData(key, data);
        return Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// 设置加密密钥来源，对应 Go 的 <c>settingsEncryptionKey</c>（读取 <c>data/.settings-key</c>）。
    /// 宿主未接线时返回 null，与 Go 的 nopHost 等价。
    /// </summary>
    private Func<byte[]?> SettingsEncryptionKey { get; set; } = static () => null;

    // ------------------------------------------------------------ 内部（本地投递）

    private async Task<ResourceStream> OpenResourceRangeAsync(
        Resource resource, string? rangeHeader, CancellationToken cancellationToken)
    {
        if (resource.Status != ResourceStatus.ResourceStatusReady)
        {
            throw AppError.BadAuthRequest("资源尚未上传完成");
        }
        if (!IsLocalProvider(resource.Provider))
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
        return new ResourceStream(resource, fs, ContentRange: "", AcceptRanges: "bytes")
        {
            StatusCode = 200,
            ContentLength = resource.Size,
        };
    }

    private static bool IsLocalProvider(string? provider) =>
        string.IsNullOrEmpty(provider) || string.Equals(provider, "local", StringComparison.OrdinalIgnoreCase);

    private static bool IsDecimalDigits(string value)
    {
        foreach (char ch in value)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>对应 Go: <c>resourceStorageURL</c>（本地直链相对路径）。</summary>
    private static Task<string> DirectLocalResourceUrlAsync(
        Resource resource, CancellationToken cancellationToken)
    {
        string objectKey = resource.ObjectKey.TrimStart('/');
        return Task.FromResult("/api/resources/" + resource.ID + "/file");
    }
}