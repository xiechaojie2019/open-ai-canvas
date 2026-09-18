#nullable enable
using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Outbound;
using OpenAICanvas.Platform;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>
/// 资源上传写路径：幂等键复用、媒体类型探测、配额预留与本地对象落盘。
/// 对应 Go: <c>app/resource.go</c> 的 <c>UploadResource</c> / <c>UploadResourceFile</c> /
/// <c>detectUploadedMimeType</c> / <c>storeResource</c> / <c>storeResourceObject</c> /
/// <c>writeLocalResourceObject</c> / <c>retryStoredResource</c>。
/// </summary>
/// <remarks>
/// 对象存储（OSS/COS/Kodo/S3）通道未移植，见 PENDING-CONFIRMATIONS.md；
/// 这里与 Go 的降级路径等价：始终以 local provider 落盘。
/// </remarks>
public sealed class ResourceUploadService
{
    private readonly Repository _repository;
    private readonly UploadQuota _quota;
    private readonly string _dataDir;

    private readonly IRuntimePolicyProvider? _policyProvider;

    public ResourceUploadService(
        Repository repository,
        UploadQuota quota,
        string? dataDir = null,
        IRuntimePolicyProvider? policyProvider = null)
    {
        _repository = repository;
        _quota = quota;
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? "data" : dataDir!;
        _policyProvider = policyProvider;
    }

    private IRuntimePolicyProvider PolicyProvider => _policyProvider ?? new DefaultRuntimePolicyProvider();

    /// <summary>
    /// 接收已完整落盘的本地文件（分片上传合并后调用）。与
    /// <see cref="UploadResourceFromStreamAsync"/> 共享幂等、探测、配额与持久化语义，
    /// 唯一差异是单文件上限已在分片会话层校验。
    /// 对应 Go: <c>UploadResourceFile</c>。
    /// </summary>
    public async Task<Resource> UploadResourceFileAsync(
        string userId,
        string fileName,
        long size,
        string kind,
        int width,
        int height,
        long durationMs,
        Stream file,
        string? uploadIdentity = null,
        CancellationToken cancellationToken = default)
    {
        if (file is null || size <= 0)
        {
            throw AppError.BadAuthRequest("请选择要上传的文件");
        }
        string? uploadKey = NormalizedUploadKey(uploadIdentity);
        Resource? existing = await ResourceForUploadKeyAsync(userId, uploadKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Status == ResourceStatus.ResourceStatusReady)
        {
            return existing;
        }
        if (existing is not null && existing.Status == ResourceStatus.ResourceStatusPending)
        {
            throw UploadInProgress();
        }
        string mimeType = DetectUploadedMimeType(file, fileName, string.Empty);
        if (existing is not null)
        {
            return await RetryStoredResourceAsync(userId, existing, kind, mimeType, size, file, cancellationToken)
                .ConfigureAwait(false);
        }
        string day = await _quota.ReserveChunkedUploadQuotaAsync(userId, size, cancellationToken).ConfigureAwait(false);
        (Resource? resource, bool stored) = await StoreResourceAsync(
            userId, kind, fileName, mimeType, size, width, height, durationMs, file, uploadKey, cancellationToken)
            .ConfigureAwait(false);
        if (resource is null)
        {
            await _quota.ReleaseUserUploadQuotaAsync(userId, day, size, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("资源写入失败");
        }
        if (stored)
        {
            await _quota.CommitUserUploadQuotaAsync(userId, size, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _quota.ReleaseUserUploadQuotaAsync(userId, day, size, cancellationToken).ConfigureAwait(false);
        }
        return resource;
    }

    /// <summary>
    /// 从公网 URL 导入资源：SSRF 校验 → 限长下载 → 与本地上传共享
    /// 幂等、探测、配额与持久化语义。对应 Go: <c>ImportResourceURL</c>。
    /// </summary>
    public async Task<Resource> ImportResourceUrlAsync(
        string userId,
        string rawUrl,
        string kind,
        int width,
        int height,
        long durationMs,
        string? uploadIdentity = null,
        CancellationToken cancellationToken = default)
    {
        string? uploadKey = NormalizedUploadKey(uploadIdentity);
        Resource? existing = await ResourceForUploadKeyAsync(userId, uploadKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Status == ResourceStatus.ResourceStatusReady)
        {
            return existing;
        }
        if (existing is not null && existing.Status == ResourceStatus.ResourceStatusPending)
        {
            throw UploadInProgress();
        }
        long maxBytes = PolicyProvider.Current().Resource.ResourceUploadMB << 20;
        RemoteResourcePayload payload = await DownloadRemoteResourceAsync(
            rawUrl, maxBytes, cancellationToken).ConfigureAwait(false);
        kind = NormalizeResourceKind(kind, payload.MimeType);
        if (kind == "image" && (width <= 0 || height <= 0))
        {
            (int decodedWidth, int decodedHeight) = ImageDimensions(payload.Data);
            if (decodedWidth > 0 && decodedHeight > 0)
            {
                width = decodedWidth;
                height = decodedHeight;
            }
        }
        long size = payload.Data.Length;
        if (existing is not null)
        {
            using MemoryStream buffered = new(payload.Data);
            return await RetryStoredResourceAsync(
                userId, existing, kind, payload.MimeType, size, buffered, cancellationToken).ConfigureAwait(false);
        }
        string day = await _quota.ReserveUserUploadQuotaAsync(userId, size, cancellationToken).ConfigureAwait(false);
        Resource? resource;
        bool stored;
        using (MemoryStream buffered = new(payload.Data))
        {
            (resource, stored) = await StoreResourceAsync(
                userId, kind, payload.FileName, payload.MimeType, size, width, height, durationMs,
                buffered, uploadKey, cancellationToken).ConfigureAwait(false);
        }
        if (resource is null)
        {
            await _quota.ReleaseUserUploadQuotaAsync(userId, day, size, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("资源写入失败");
        }
        if (stored)
        {
            await _quota.CommitUserUploadQuotaAsync(userId, size, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _quota.ReleaseUserUploadQuotaAsync(userId, day, size, cancellationToken).ConfigureAwait(false);
        }
        return resource;
    }

    /// <summary>限长下载远程资源。对应 Go: <c>downloadRemoteResource</c>。</summary>
    private static async Task<RemoteResourcePayload> DownloadRemoteResourceAsync(
        string rawUrl, long maxBytes, CancellationToken cancellationToken)
    {
        Uri parsed = await OutboundGuard.ValidateOutboundUrlAsync(rawUrl).ConfigureAwait(false);
        using HttpClient client = OutboundHttpClient.Create(TimeSpan.FromSeconds(90));
        using HttpResponseMessage response = await client.GetAsync(
            parsed, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode < HttpStatusCode.OK || response.StatusCode >= HttpStatusCode.MultipleChoices)
        {
            int status = (int)response.StatusCode;
            string reason = response.ReasonPhrase ?? "";
            throw AppError.BadAuthRequest($"远程资源下载失败：{status} {reason}".TrimEnd());
        }
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length >= maxBytes)
            {
                throw AppError.BadAuthRequest($"远程资源必须小于 {UploadQuota.FormatStorageLimit(maxBytes)}");
            }
        }
        byte[] data = buffer.ToArray();
        string mimeType = (response.Content.Headers.ContentType?.MediaType ?? "").Trim();
        if (mimeType.Length == 0 || mimeType == "application/octet-stream")
        {
            mimeType = DetectUploadedMimeType(new MemoryStream(data), "", "");
        }
        string path = parsed.AbsolutePath;
        string fileName = path.Length > 0 ? Path.GetFileName(path) : "";
        if (fileName.Length == 0 || fileName == "." || fileName == "/" || !fileName.Contains('.'))
        {
            fileName = "resource." + ExtensionFromMimeType(mimeType);
        }
        return new RemoteResourcePayload(parsed.ToString(), parsed.Host, fileName, mimeType, data);
    }

    /// <summary>远程资源载荷。对应 Go: <c>remoteResourcePayload</c>。</summary>
    private sealed record RemoteResourcePayload(
        string Url, string Endpoint, string FileName, string MimeType, byte[] Data);

    /// <summary>
    /// 接收 multipart 表单上传（整传）。对应 Go: <c>UploadResource</c>。
    /// </summary>
    public async Task<Resource> UploadResourceFromStreamAsync(
        string userId,
        string fileName,
        long size,
        string kind,
        int width,
        int height,
        long durationMs,
        Stream file,
        string? declaredMimeType = null,
        string? uploadIdentity = null,
        bool forceLocal = false,
        CancellationToken cancellationToken = default)
    {
        string? uploadKey = NormalizedUploadKey(uploadIdentity);
        Resource? existing = await ResourceForUploadKeyAsync(userId, uploadKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Status == ResourceStatus.ResourceStatusReady)
        {
            return existing;
        }
        if (existing is not null && existing.Status == ResourceStatus.ResourceStatusPending)
        {
            throw UploadInProgress();
        }
        string mimeType = DetectUploadedMimeType(file, fileName, declaredMimeType ?? string.Empty);
        if (existing is not null)
        {
            return await RetryStoredResourceAsync(userId, existing, kind, mimeType, size, file, cancellationToken)
                .ConfigureAwait(false);
        }
        string day = await _quota.ReserveUserUploadQuotaAsync(userId, size, cancellationToken).ConfigureAwait(false);
        (Resource? resource, bool stored) = await StoreResourceAsync(
            userId, kind, fileName, mimeType, size, width, height, durationMs, file, uploadKey, cancellationToken,
            forceLocal).ConfigureAwait(false);
        if (resource is null)
        {
            await _quota.ReleaseUserUploadQuotaAsync(userId, day, size, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("资源写入失败");
        }
        if (stored)
        {
            await _quota.CommitUserUploadQuotaAsync(userId, size, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _quota.ReleaseUserUploadQuotaAsync(userId, day, size, cancellationToken).ConfigureAwait(false);
        }
        return resource;
    }

    /// <summary>
    /// 创建资源记录并写入物理对象。
    /// 返回 <c>(resource, stored)</c>：<c>stored=false</c> 表示复用已有幂等记录（未新落盘）。
    /// 对应 Go: <c>storeResource</c>（对象存储降级路径等价）。
    /// </summary>
    private async Task<(Resource? Resource, bool Stored)> StoreResourceAsync(
        string userId,
        string kind,
        string fileName,
        string mimeType,
        long size,
        int width,
        int height,
        long durationMs,
        Stream body,
        string? uploadKey,
        CancellationToken cancellationToken,
        bool forceLocal = false)
    {
        Resource? existing = await ResourceForUploadKeyAsync(userId, uploadKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Status == ResourceStatus.ResourceStatusReady)
            {
                return (existing, false);
            }
            throw UploadInProgress();
        }

        DateTime now = DateTime.UtcNow;
        kind = NormalizeResourceKind(kind, mimeType);
        Resource resource = new()
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            Kind = kind,
            Status = ResourceStatus.ResourceStatusPending,
            Provider = "local",
            ObjectKey = LocalObjectKey(userId, kind, fileName, mimeType, now),
            MimeType = mimeType,
            Size = size,
            Width = width,
            Height = height,
            DurationMs = durationMs,
            UploadKey = uploadKey,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            await _repository.CreateResourceAsync(resource, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 并发下唯一索引冲突：回查幂等记录，与 Go 一致。
            Resource? concurrent = await ResourceForUploadKeyAsync(userId, uploadKey, cancellationToken).ConfigureAwait(false);
            if (concurrent is not null)
            {
                if (concurrent.Status == ResourceStatus.ResourceStatusReady)
                {
                    return (concurrent, false);
                }
                throw UploadInProgress();
            }
            throw;
        }

        string etag;
        try
        {
            etag = await StoreResourceObjectAsync(resource, fileName, body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            resource.Status = ResourceStatus.ResourceStatusFailed;
            resource.Error = error.Message;
            await _repository.SaveResourceAsync(resource, cancellationToken).ConfigureAwait(false);
            throw;
        }

        resource.Status = ResourceStatus.ResourceStatusReady;
        resource.ETag = etag;
        resource.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _repository.SaveResourceAsync(resource, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // 与 Go 一致：先尝试清理物理对象，清理成功则删除记录并报"保存就绪状态失败"。
            try
            {
                DeleteStoredResourceObject(resource);
                await _repository.DeleteResourceAsync(userId, resource.ID, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"保存资源就绪状态失败：{error.Message}", error);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception cleanupError)
            {
                resource.Status = ResourceStatus.ResourceStatusFailed;
                resource.Error = $"保存资源就绪状态失败，物理对象清理失败：{cleanupError.Message}";
                await _repository.SaveResourceAsync(resource, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"清理已上传资源对象失败：{cleanupError.Message}", error);
            }
        }

        return (resource, true);
    }

    /// <summary>
    /// 写入资源物理对象。local 通道直接落盘；对象存储通道未移植，按 Go 的降级语义
    /// 统一改写为 local provider 后落盘，保证上传写路径不因外部存储缺席而整体失败。
    /// 对应 Go: <c>storeResourceObject</c>。
    /// </summary>
    private Task<string> StoreResourceObjectAsync(
        Resource resource, string fileName, Stream body, CancellationToken cancellationToken)
    {
        if (resource.Provider is "" or "local")
        {
            WriteLocalResourceObject(LocalObjectPath(resource.ObjectKey), body);
            return Task.FromResult(string.Empty);
        }

        resource.Provider = "local";
        resource.ObjectKey = LocalObjectKey(resource.UserID, resource.Kind, fileName, resource.MimeType, DateTime.UtcNow);
        resource.Endpoint = string.Empty;
        resource.Bucket = string.Empty;
        resource.StorageSettingID = string.Empty;
        resource.ETag = string.Empty;
        WriteLocalResourceObject(LocalObjectPath(resource.ObjectKey), body);
        return Task.FromResult(string.Empty);
    }

    /// <summary>
    /// 重试失败资源：抢占后重新落盘，仅重新预留当日上传额度（存储用量已计入）。
    /// 对应 Go: <c>retryStoredResource</c>。
    /// </summary>
    private async Task<Resource> RetryStoredResourceAsync(
        string userId,
        Resource resource,
        string kind,
        string mimeType,
        long size,
        Stream body,
        CancellationToken cancellationToken)
    {
        if (resource.Status == ResourceStatus.ResourceStatusReady)
        {
            return resource;
        }
        if (resource.Status != ResourceStatus.ResourceStatusFailed)
        {
            throw UploadInProgress();
        }
        kind = NormalizeResourceKind(kind, mimeType);
        bool identityMismatch = resource.Size != size
            || resource.Kind != kind
            || (!string.IsNullOrEmpty(resource.MimeType) && !string.IsNullOrEmpty(mimeType) && resource.MimeType != mimeType);
        if (identityMismatch)
        {
            throw AppError.New(409, "上传幂等标识已用于其他文件");
        }
        bool claimed = await _repository.ClaimFailedResourceUploadAsync(userId, resource.ID, cancellationToken)
            .ConfigureAwait(false);
        if (!claimed)
        {
            Resource? latest = await _repository.ResourceForUserAsync(userId, resource.ID, cancellationToken).ConfigureAwait(false);
            if (latest is not null && latest.Status == ResourceStatus.ResourceStatusReady)
            {
                return latest;
            }
            throw UploadInProgress();
        }
        resource.Status = ResourceStatus.ResourceStatusPending;
        resource.Error = string.Empty;
        resource.UpdatedAt = DateTime.UtcNow;

        string day;
        try
        {
            day = await _quota.ReserveRetryUploadQuotaAsync(userId, size, cancellationToken).ConfigureAwait(false);
        }
        catch (AppError)
        {
            resource.Status = ResourceStatus.ResourceStatusFailed;
            resource.Error = "上传额度不足";
            resource.UpdatedAt = DateTime.UtcNow;
            await _repository.SaveResourceAsync(resource, cancellationToken).ConfigureAwait(false);
            throw;
        }

        string etag;
        try
        {
            etag = await StoreResourceObjectAsync(resource, string.Empty, body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await _quota.ReleaseRetryUploadQuotaAsync(userId, day, size, cancellationToken).ConfigureAwait(false);
            resource.Status = ResourceStatus.ResourceStatusFailed;
            resource.Error = error.Message;
            resource.UpdatedAt = DateTime.UtcNow;
            await _repository.SaveResourceAsync(resource, cancellationToken).ConfigureAwait(false);
            throw;
        }
        resource.ETag = etag;
        resource.UpdatedAt = DateTime.UtcNow;
        resource.Status = ResourceStatus.ResourceStatusReady;
        try
        {
            await _repository.SaveResourceAsync(resource, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await _quota.ReleaseRetryUploadQuotaAsync(userId, day, size, cancellationToken).ConfigureAwait(false);
            resource.Status = ResourceStatus.ResourceStatusFailed;
            resource.Error = "保存资源重试就绪状态失败";
            await _repository.SaveResourceAsync(resource, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"保存资源重试就绪状态失败：{error.Message}", error);
        }
        return resource;
    }

    /// <summary>物理对象删除（仅本地通道）。对应 Go: <c>deleteStoredResourceObject</c> 的本地分支。</summary>
    public void DeleteStoredResourceObject(Resource resource)
    {
        if (resource.Provider is not ("" or "local"))
        {
            return;
        }
        string path = LocalObjectPath(resource.ObjectKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 探测上传文件 MIME：优先声明值（非 octet-stream），其次前 512 字节嗅探，最后按扩展名。
    /// 对应 Go: <c>detectUploadedMimeType</c>。
    /// </summary>
    public static string DetectUploadedMimeType(Stream file, string fileName, string declared)
    {
        declared = declared.Split(';')[0].Trim();
        if (declared.Length > 0 && declared != "application/octet-stream")
        {
            return declared;
        }

        long origin = file.CanSeek ? file.Position : 0;
        byte[] buffer = new byte[512];
        int read = 0;
        try
        {
            read = file.Read(buffer, 0, buffer.Length);
        }
        catch (Exception)
        {
            read = 0;
        }
        if (file.CanSeek)
        {
            file.Seek(origin, SeekOrigin.Begin);
        }

        string detected = SniffContentType(buffer.AsSpan(0, read));
        if (detected.Length > 0 && detected != "application/octet-stream")
        {
            return detected.Split(';')[0].Trim();
        }
        string fromExtension = MimeTypeFromExtension(Path.GetExtension(fileName));
        if (fromExtension.Length > 0)
        {
            return fromExtension.Split(';')[0].Trim();
        }
        return "application/octet-stream";
    }

    /// <summary>对应 Go: <c>normalizeResourceKind</c>。</summary>
    public static string NormalizeResourceKind(string kind, string mimeType)
    {
        kind = kind.Trim().ToLowerInvariant();
        if (kind is "image" or "video" or "audio" or "file")
        {
            return kind;
        }
        if (mimeType.StartsWith("image/", StringComparison.Ordinal))
        {
            return "image";
        }
        if (mimeType.StartsWith("video/", StringComparison.Ordinal))
        {
            return "video";
        }
        if (mimeType.StartsWith("audio/", StringComparison.Ordinal))
        {
            return "audio";
        }
        return "file";
    }

    /// <summary>对应 Go: <c>normalizedResourceUploadKey</c>（SHA-256 十六进制）。</summary>
    public static string? NormalizedUploadKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>对应 Go: <c>resourceUploadInProgress</c>（409 + retryable）。</summary>
    public static AppError UploadInProgress() =>
        new(409, "相同素材正在上传，请稍后重试", retryable: true);

    /// <summary>对应 Go: <c>localObjectKey</c>。</summary>
    public static string LocalObjectKey(string userId, string kind, string fileName, string mimeType, DateTime now)
    {
        string ext = ResourceFileExtension(fileName, mimeType, kind);
        string folder = now.ToString("yyyy/MM/dd");
        return string.Join('/', "users", SafeObjectSegment(userId), kind, folder, IdGenerator.NewId() + ext);
    }

    /// <summary>对应 Go: <c>resourceFileExtension</c>。</summary>
    public static string ResourceFileExtension(string fileName, string mimeType, string kind)
    {
        string ext = Path.GetExtension((fileName ?? string.Empty).Trim()).ToLowerInvariant();
        if (ext.Length > 0 && ext != ".")
        {
            return ext;
        }
        string mapped = ExtensionFromMimeType(mimeType.Split(';')[0].Trim());
        if (mapped.Length > 0)
        {
            return mapped.ToLowerInvariant();
        }
        return kind switch
        {
            "image" => ".png",
            "video" => ".mp4",
            "audio" => ".mp3",
            _ => ".bin",
        };
    }

    /// <summary>对应 Go: <c>safeObjectSegment</c>（非 [A-Za-z0-9-_] 归一为 '-'）。</summary>
    public static string SafeObjectSegment(string value)
    {
        StringBuilder builder = new();
        foreach (char ch in (value ?? string.Empty).Trim())
        {
            bool allowed = (ch >= 'a' && ch <= 'z')
                || (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9')
                || ch == '-' || ch == '_';
            builder.Append(allowed ? ch : '-');
        }
        return builder.ToString().Trim('-');
    }

    private async Task<Resource?> ResourceForUploadKeyAsync(
        string userId, string? uploadKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(uploadKey))
        {
            return null;
        }
        return await _repository.ResourceByUploadKeyAsync(userId, uploadKey, cancellationToken).ConfigureAwait(false);
    }

    private string LocalObjectPath(string objectKey) =>
        Path.Combine(_dataDir, "resources", objectKey.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>对应 Go: <c>writeLocalResourceObject</c>。</summary>
    private static void WriteLocalResourceObject(string filePath, Stream body)
    {
        string? parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        using FileStream file = new(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        body.CopyTo(file);
    }

    /// <summary>对应 Go 的 <c>http.DetectContentType</c>（仅覆盖 Go 嗅探表命中的常见类型）。</summary>
    private static string SniffContentType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01 && data[3] == 0x00)
        {
            return "image/x-icon";
        }
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "image/png";
        }
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (data.Length >= 6 && (data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8'))
        {
            return "image/gif";
        }
        if (data.Length >= 4 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
            && data.Length >= 12 && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
        {
            return "image/webp";
        }
        if (data.Length >= 5 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F' && data[4] == '-')
        {
            return "application/pdf";
        }
        if (data.Length >= 4 && data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
        {
            return "video/webm";
        }
        if (data.Length >= 12 && data[4] == 'f' && data[5] == 't' && data[6] == 'y' && data[7] == 'p')
        {
            return "video/mp4";
        }
        if (data.Length >= 4 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
        {
            return "audio/mpeg";
        }
        if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M')
        {
            return "image/bmp";
        }
        if (data.Length >= 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 0x03 && data[3] == 0x04)
        {
            return "application/zip";
        }
        if (data.Length >= 5 && data[0] == '<' && data[1] == '!' && data[2] == 'D' && data[3] == 'O' && data[4] == 'C')
        {
            return "text/html; charset=utf-8";
        }
        if (data.Length >= 4 && data[0] == 'S' && data[1] == 'Q' && data[2] == 'L' && data[3] == 'i')
        {
            return "application/vnd.sqlite3";
        }
        if (IsPlainText(data))
        {
            return "text/plain; charset=utf-8";
        }
        return "application/octet-stream";
    }

    private static bool IsPlainText(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return false;
        }
        foreach (byte value in data)
        {
            if (value <= 0x08 || (value >= 0x0B && value <= 0x0C) || (value >= 0x0E && value <= 0x1B) || value == 0x7F)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>对应 Go 的 <c>mime.TypeByExtension</c>（常用子集）。</summary>
    private static string MimeTypeFromExtension(string extension) =>
        extension.Trim().ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".pdf" => "application/pdf",
            ".json" => "application/json",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            _ => string.Empty,
        };

    /// <summary>对应 Go 的 <c>mime.ExtensionsByType</c>（常用子集）。</summary>
    /// <summary>
    /// 从文件头解码图片尺寸（PNG/GIF/JPEG，与 Go 注册的三种 DecodeConfig 格式一致）。
    /// 对应 Go: <c>imageDimensions</c>。
    /// </summary>
    internal static (int Width, int Height) ImageDimensions(byte[] data)
    {
        if (data.Length >= 24 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N'
            && data[3] == (byte)'G')
        {
            // PNG：IHDR 宽高为 big-endian uint32。
            int width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
            int height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
            return (width, height);
        }
        if (data.Length >= 10 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F')
        {
            // GIF：逻辑屏幕尺寸为 little-endian uint16。
            int width = data[6] | (data[7] << 8);
            int height = data[8] | (data[9] << 8);
            return (width, height);
        }
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            // JPEG：扫描 SOF0-SOF15 段（跳过 DHT/DAC 等非 SOF 标记）取精度/高度/宽度。
            int offset = 2;
            while (offset + 9 < data.Length)
            {
                if (data[offset] != 0xFF)
                {
                    offset++;
                    continue;
                }
                byte marker = data[offset + 1];
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    offset += 2;
                    continue;
                }
                if (offset + 4 > data.Length)
                {
                    break;
                }
                int segmentLength = (data[offset + 2] << 8) | data[offset + 3];
                bool isSof = marker >= 0xC0 && marker <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC;
                if (isSof && offset + 9 <= data.Length)
                {
                    int height = (data[offset + 5] << 8) | data[offset + 6];
                    int width = (data[offset + 7] << 8) | data[offset + 8];
                    return (width, height);
                }
                offset += 2 + segmentLength;
            }
        }
        return (0, 0);
    }

    private static string ExtensionFromMimeType(string mimeType) =>
        mimeType.Trim().ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "application/pdf" => ".pdf",
            "application/json" => ".json",
            "text/plain" => ".txt",
            _ => string.Empty,
        };
}
