#nullable enable
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Outbound;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

public sealed class OSSSettingRequest
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("region")] public string Region { get; set; } = "";
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
    [JsonPropertyName("cdnBaseUrl")] public string CdnBaseUrl { get; set; } = "";
    [JsonPropertyName("bucket")] public string Bucket { get; set; } = "";
    [JsonPropertyName("accessKeyId")] public string AccessKeyId { get; set; } = "";
    [JsonPropertyName("accessKeySecret")] public string AccessKeySecret { get; set; } = "";
    [JsonPropertyName("publicBaseUrl")] public string PublicBaseUrl { get; set; } = "";
    [JsonPropertyName("pathPrefix")] public string PathPrefix { get; set; } = "";
    [JsonPropertyName("s3Preset")] public string S3Preset { get; set; } = "";
    [JsonPropertyName("pathStyle")] public bool PathStyle { get; set; }
    [JsonPropertyName("sessionToken")] public string SessionToken { get; set; } = "";
    [JsonPropertyName("allowUserS3")] public bool AllowUserS3 { get; set; }
}

public sealed class PublicOSSSetting
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("provider")] public string Provider { get; init; } = "aliyun";
    [JsonPropertyName("region")] public string Region { get; init; } = "";
    [JsonPropertyName("endpoint")] public string Endpoint { get; init; } = "";
    [JsonPropertyName("cdnBaseUrl")] public string CdnBaseUrl { get; init; } = "";
    [JsonPropertyName("bucket")] public string Bucket { get; init; } = "";
    [JsonPropertyName("accessKeyId")] public string AccessKeyId { get; init; } = "";
    [JsonPropertyName("hasAccessKeySecret")] public bool HasAccessKeySecret { get; init; }
    [JsonPropertyName("publicBaseUrl")] public string PublicBaseUrl { get; init; } = "";
    [JsonPropertyName("pathPrefix")] public string PathPrefix { get; init; } = "open-ai-canvas";
    [JsonPropertyName("s3Preset")] public string S3Preset { get; init; } = "custom";
    [JsonPropertyName("pathStyle")] public bool PathStyle { get; init; }
    [JsonPropertyName("hasSessionToken")] public bool HasSessionToken { get; init; }
    [JsonPropertyName("storageLocationId")] public string? StorageLocationId { get; init; }
    [JsonPropertyName("testedAt")] public DateTime? TestedAt { get; init; }
    [JsonPropertyName("testedDigest")] public string? TestedDigest { get; init; }
    [JsonPropertyName("historyCount")] public long HistoryCount { get; init; }
    [JsonPropertyName("referencedResourceCount")] public long ReferencedResourceCount { get; init; }
    [JsonPropertyName("allowUserS3")] public bool AllowUserS3 { get; init; }
    [JsonPropertyName("updatedBy")] public string? UpdatedBy { get; init; }
    [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; init; }
    [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; init; }
}

public sealed class OSSConnectionTestResult
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("testedAt")] public DateTime TestedAt { get; init; }
    [JsonPropertyName("testedDigest")] public string TestedDigest { get; init; } = "";
}

/// <summary>平台和用户对象存储设置，敏感凭据仅以 AES-GCM 密文持久化。</summary>
public sealed class StorageSettingsService
{
    private const string PlatformScope = "platform";
    private const string UserScope = "user";
    private const string PlatformOwner = "";
    private const string PlatformSettingKey = "oss";
    private static readonly ConcurrentDictionary<string, byte> ActiveTests = new(StringComparer.Ordinal);
    private readonly Repository _repository;
    private readonly string _dataDir;

    public StorageSettingsService(Repository repository, string dataDir)
    {
        _repository = repository;
        _dataDir = dataDir;
    }

    public async Task<PublicOSSSetting> AdminOSSSettingAsync(User actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        SystemSetting? record = await _repository.SystemSettingAsync(PlatformSettingKey, cancellationToken).ConfigureAwait(false);
        StoredOSSSetting value = record is null ? Defaults() : ReadStored(record.ValueJSON);
        return await PublicAsync(value, PlatformScope, PlatformOwner, record?.UpdatedBy, record?.CreatedAt, record?.UpdatedAt,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PublicOSSSetting> UpdateAdminOSSSettingAsync(
        User actor, OSSSettingRequest request, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        SystemSetting? record = await _repository.SystemSettingAsync(PlatformSettingKey, cancellationToken).ConfigureAwait(false);
        StoredOSSSetting next = Merge(Normalize(request), record is null ? null : ReadStored(record.ValueJSON));
        ValidateForSave(next);
        await RequireSuccessfulTestWhenEnablingAsync(next, PlatformScope, PlatformOwner, cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        await _repository.SaveSystemSettingAsync(new SystemSetting
        {
            Key = PlatformSettingKey, ValueJSON = WriteStored(next), UpdatedBy = actor.ID,
            CreatedAt = record?.CreatedAt ?? now, UpdatedAt = now,
        }, cancellationToken).ConfigureAwait(false);
        return await PublicAsync(next, PlatformScope, PlatformOwner, actor.ID, record?.CreatedAt ?? now, now,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PublicOSSSetting> UserOSSSettingAsync(User actor, CancellationToken cancellationToken = default)
    {
        UserOSSSetting? record = await _repository.LatestUserOSSSettingAsync(actor.ID, cancellationToken).ConfigureAwait(false);
        StoredOSSSetting value = record is null ? Defaults() : ReadStored(record.ValueJSON);
        bool allowUserS3 = await PlatformAllowsUserS3Async(cancellationToken).ConfigureAwait(false);
        if (value.Provider == "s3" && !allowUserS3)
        {
            value.Enabled = false;
        }
        return await PublicAsync(value, UserScope, actor.ID, null, record?.CreatedAt, record?.UpdatedAt,
            cancellationToken, allowUserS3).ConfigureAwait(false);
    }

    public async Task<PublicOSSSetting> UpdateUserOSSSettingAsync(
        User actor, OSSSettingRequest request, CancellationToken cancellationToken = default)
    {
        UserOSSSetting? previous = await _repository.LatestUserOSSSettingAsync(actor.ID, cancellationToken).ConfigureAwait(false);
        StoredOSSSetting next = Merge(Normalize(request), previous is null ? null : ReadStored(previous.ValueJSON));
        bool allowUserS3 = await PlatformAllowsUserS3Async(cancellationToken).ConfigureAwait(false);
        if (next.Provider == "s3" && !allowUserS3)
        {
            throw AppError.Forbidden("平台管理员尚未允许个人 S3 兼容存储");
        }
        ValidateForSave(next);
        await RequireSuccessfulTestWhenEnablingAsync(next, UserScope, actor.ID, cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        await _repository.CreateUserOSSSettingAsync(new UserOSSSetting
        {
            ID = Guid.NewGuid().ToString("N"), UserID = actor.ID, Enabled = next.Enabled,
            ValueJSON = WriteStored(next), CreatedAt = now, UpdatedAt = now,
        }, cancellationToken).ConfigureAwait(false);
        return await PublicAsync(next, UserScope, actor.ID, null, now, now, cancellationToken, allowUserS3)
            .ConfigureAwait(false);
    }

    public async Task<OSSConnectionTestResult> TestAdminOSSSettingAsync(
        User actor, OSSSettingRequest request, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        SystemSetting? previous = await _repository.SystemSettingAsync(PlatformSettingKey, cancellationToken).ConfigureAwait(false);
        return await TestAsync(Merge(Normalize(request), previous is null ? null : ReadStored(previous.ValueJSON)),
            PlatformScope, PlatformOwner, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OSSConnectionTestResult> TestUserOSSSettingAsync(
        User actor, OSSSettingRequest request, CancellationToken cancellationToken = default)
    {
        if (Normalize(request).Provider == "s3" && !await PlatformAllowsUserS3Async(cancellationToken).ConfigureAwait(false))
        {
            throw AppError.Forbidden("平台管理员尚未允许个人 S3 兼容存储");
        }
        UserOSSSetting? previous = await _repository.LatestUserOSSSettingAsync(actor.ID, cancellationToken).ConfigureAwait(false);
        return await TestAsync(Merge(Normalize(request), previous is null ? null : ReadStored(previous.ValueJSON)),
            UserScope, actor.ID, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OSSConnectionTestResult> TestAsync(
        StoredOSSSetting value, string scope, string ownerId, CancellationToken cancellationToken)
    {
        ValidateForTest(value);
        if (value.Provider != "s3")
        {
            throw AppError.New(501, $"当前 .NET 后端尚未实现 {value.Provider} 对象存储连接测试，请使用通用 S3 兼容接口");
        }
        string mutexKey = scope + ":" + ownerId;
        if (!ActiveTests.TryAdd(mutexKey, 0))
        {
            throw AppError.New(409, "对象存储连接测试正在进行");
        }
        try
        {
            await OutboundGuard.ValidateOutboundUrlAsync(value.Endpoint).ConfigureAwait(false);
            AWSCredentials credentials = string.IsNullOrEmpty(value.SessionToken)
                ? new BasicAWSCredentials(value.AccessKeyId, value.AccessKeySecret)
                : new SessionAWSCredentials(value.AccessKeyId, value.AccessKeySecret, value.SessionToken);
            AmazonS3Config config = new()
            {
                ServiceURL = value.Endpoint,
                ForcePathStyle = value.PathStyle,
                AuthenticationRegion = string.IsNullOrEmpty(value.Region) ? "us-east-1" : value.Region,
            };
            string marker = "yingce-storage-test";
            string key = BuildObjectKey(value.PathPrefix, ".yingce-connection-test-" + Guid.NewGuid().ToString("N"));
            using AmazonS3Client client = new(credentials, config);
            await client.PutObjectAsync(new PutObjectRequest { BucketName = value.Bucket, Key = key, ContentBody = marker },
                cancellationToken).ConfigureAwait(false);
            try
            {
                using GetObjectResponse response = await client.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = value.Bucket, Key = key, ByteRange = new ByteRange(0, 3),
                }, cancellationToken).ConfigureAwait(false);
                byte[] head = new byte[4];
                int read = await response.ResponseStream.ReadAsync(head, cancellationToken).ConfigureAwait(false);
                if (read != 4 || Encoding.UTF8.GetString(head) != marker[..4])
                {
                    throw AppError.BadAuthRequest("对象存储连接测试读取校验失败");
                }
            }
            finally
            {
                await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = value.Bucket, Key = key }, cancellationToken)
                    .ConfigureAwait(false);
            }

            DateTime now = DateTime.UtcNow;
            string locationDigest = LocationDigest(value);
            string testedDigest = TestDigest(value);
            StorageLocation? location = await _repository.StorageLocationByDigestAsync(
                scope, ownerId, value.Provider, locationDigest, cancellationToken).ConfigureAwait(false);
            if (location is null)
            {
                location = new StorageLocation
                {
                    ID = Guid.NewGuid().ToString("N"), Scope = scope, OwnerID = ownerId, Provider = value.Provider,
                    LocationDigest = locationDigest, CreatedAt = now,
                };
                await _repository.CreateStorageLocationAsync(location, cancellationToken).ConfigureAwait(false);
            }
            location.ValueJSON = WriteStored(value);
            location.TestedDigest = testedDigest;
            location.TestedAt = now;
            location.Active = true;
            location.UpdatedAt = now;
            await _repository.SaveStorageLocationAsync(location, cancellationToken).ConfigureAwait(false);
            await _repository.ActivateStorageLocationAsync(scope, ownerId, location.ID, true, cancellationToken).ConfigureAwait(false);
            return new OSSConnectionTestResult { Ok = true, Message = "连接测试通过", TestedAt = now, TestedDigest = testedDigest };
        }
        catch (AppError)
        {
            throw;
        }
        catch (Exception error)
        {
            throw AppError.Wrap(400, "对象存储连接测试失败", error);
        }
        finally
        {
            ActiveTests.TryRemove(mutexKey, out _);
        }
    }

    private async Task RequireSuccessfulTestWhenEnablingAsync(
        StoredOSSSetting value, string scope, string ownerId, CancellationToken cancellationToken)
    {
        if (!value.Enabled || value.Provider != "s3") return;
        StorageLocation? location = await _repository.StorageLocationByDigestAsync(
            scope, ownerId, value.Provider, LocationDigest(value), cancellationToken).ConfigureAwait(false);
        if (location?.TestedAt is null || string.IsNullOrEmpty(location.TestedDigest))
        {
            throw AppError.BadAuthRequest("S3 关键配置尚未通过连接测试");
        }
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(location.TestedDigest), Encoding.UTF8.GetBytes(TestDigest(value))))
        {
            throw AppError.BadAuthRequest("S3 关键配置或凭据已变化，请重新连接测试");
        }
        await _repository.ActivateStorageLocationAsync(scope, ownerId, location.ID, true, cancellationToken).ConfigureAwait(false);
        value.StorageLocationId = location.ID;
    }

    private async Task<PublicOSSSetting> PublicAsync(StoredOSSSetting value, string scope, string ownerId,
        string? updatedBy, DateTime? createdAt, DateTime? updatedAt, CancellationToken cancellationToken,
        bool? allowUserS3 = null)
    {
        StorageLocation? location = await _repository.StorageLocationByDigestAsync(
            scope, ownerId, value.Provider, LocationDigest(value), cancellationToken).ConfigureAwait(false);
        long history = await _repository.StorageLocationHistoryCountAsync(scope, ownerId, cancellationToken).ConfigureAwait(false);
        long references = location is null ? 0 : await _repository.StorageLocationResourceCountAsync(location.ID, cancellationToken)
            .ConfigureAwait(false);
        return new PublicOSSSetting
        {
            Enabled = value.Enabled, Provider = value.Provider, Region = value.Region, Endpoint = value.Endpoint,
            CdnBaseUrl = value.CdnBaseUrl, Bucket = value.Bucket, AccessKeyId = value.AccessKeyId,
            HasAccessKeySecret = !string.IsNullOrEmpty(value.AccessKeySecret), PublicBaseUrl = value.PublicBaseUrl,
            PathPrefix = value.PathPrefix, S3Preset = value.S3Preset, PathStyle = value.PathStyle,
            HasSessionToken = !string.IsNullOrEmpty(value.SessionToken), StorageLocationId = value.StorageLocationId ?? location?.ID,
            TestedAt = location?.TestedAt, TestedDigest = location?.TestedDigest, HistoryCount = history,
            ReferencedResourceCount = references, AllowUserS3 = allowUserS3 ?? value.AllowUserS3,
            UpdatedBy = updatedBy, CreatedAt = createdAt, UpdatedAt = updatedAt,
        };
    }

    private async Task<bool> PlatformAllowsUserS3Async(CancellationToken cancellationToken)
    {
        SystemSetting? platform = await _repository.SystemSettingAsync(PlatformSettingKey, cancellationToken).ConfigureAwait(false);
        return platform is not null && ReadStored(platform.ValueJSON).AllowUserS3;
    }

    private StoredOSSSetting Merge(StoredOSSSetting next, StoredOSSSetting? previous)
    {
        if (previous is not null && previous.Provider == next.Provider)
        {
            if (string.IsNullOrEmpty(next.AccessKeySecret)) next.AccessKeySecret = previous.AccessKeySecret;
            if (string.IsNullOrEmpty(next.SessionToken)) next.SessionToken = previous.SessionToken;
        }
        return next;
    }

    private static StoredOSSSetting Normalize(OSSSettingRequest request)
    {
        string provider = (request.Provider ?? "").Trim().ToLowerInvariant();
        if (provider.Length == 0) provider = "aliyun";
        if (provider is not ("aliyun" or "tencent" or "qiniu" or "s3"))
            throw AppError.BadAuthRequest("不支持的对象存储提供方");
        string preset = (request.S3Preset ?? "").Trim().ToLowerInvariant();
        if (preset.Length == 0) preset = "custom";
        if (preset is not ("aws" or "r2" or "b2" or "rustfs" or "custom"))
            throw AppError.BadAuthRequest("不支持的 S3 预设");
        string endpoint = TrimUrl(request.Endpoint);
        string region = (request.Region ?? "").Trim();
        if (provider == "tencent" && endpoint.Length == 0 && region.Length > 0)
            endpoint = "https://cos." + region + ".myqcloud.com";
        return new StoredOSSSetting
        {
            Enabled = request.Enabled, Provider = provider, Region = region, Endpoint = endpoint,
            CdnBaseUrl = TrimUrl(request.CdnBaseUrl), Bucket = (request.Bucket ?? "").Trim(),
            AccessKeyId = (request.AccessKeyId ?? "").Trim(), AccessKeySecret = (request.AccessKeySecret ?? "").Trim(),
            PublicBaseUrl = TrimUrl(request.PublicBaseUrl), PathPrefix = TrimPath(request.PathPrefix),
            S3Preset = preset, PathStyle = request.PathStyle, SessionToken = (request.SessionToken ?? "").Trim(),
            AllowUserS3 = request.AllowUserS3,
        };
    }

    private static void ValidateForSave(StoredOSSSetting value)
    {
        if (!value.Enabled && string.IsNullOrEmpty(value.PublicBaseUrl))
            throw AppError.BadAuthRequest("服务器本地存储需要填写服务器访问地址");
    }

    private static void ValidateForTest(StoredOSSSetting value)
    {
        if (string.IsNullOrEmpty(value.Endpoint) || string.IsNullOrEmpty(value.Bucket) ||
            string.IsNullOrEmpty(value.AccessKeyId) || string.IsNullOrEmpty(value.AccessKeySecret))
            throw AppError.BadAuthRequest("连接测试需要填写 endpoint、bucket 和访问密钥");
    }

    private StoredOSSSetting ReadStored(string json)
    {
        StoredOSSSetting value;
        try { value = JsonSerializer.Deserialize<StoredOSSSetting>(json, JsonOptions) ?? Defaults(); }
        catch (JsonException) { throw new InvalidOperationException("OSS 设置数据格式无效"); }
        value = Normalize(value.ToRequest());
        value.AccessKeySecret = SettingsCrypto.DecryptSecret(value.AccessKeySecret, _dataDir);
        value.SessionToken = SettingsCrypto.DecryptSecret(value.SessionToken, _dataDir);
        return value;
    }

    private string WriteStored(StoredOSSSetting value)
    {
        StoredOSSSetting encrypted = value.Clone();
        encrypted.AccessKeySecret = SettingsCrypto.EncryptSecret(encrypted.AccessKeySecret, _dataDir);
        encrypted.SessionToken = SettingsCrypto.EncryptSecret(encrypted.SessionToken, _dataDir);
        return JsonSerializer.Serialize(encrypted, JsonOptions);
    }

    private static StoredOSSSetting Defaults() => new() { Provider = "aliyun", PathPrefix = "open-ai-canvas", S3Preset = "custom" };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static string TrimUrl(string? value) => (value ?? "").Trim().TrimEnd('/');
    private static string TrimPath(string? value) => (value ?? "").Trim().Trim('/').Length == 0 ? "open-ai-canvas" : (value ?? "").Trim().Trim('/');
    private static string BuildObjectKey(string prefix, string leaf) => string.IsNullOrEmpty(prefix) ? leaf : prefix + "/" + leaf;
    private static string LocationDigest(StoredOSSSetting value) => Digest(string.Join("\0", value.Provider, value.Endpoint, value.Bucket, value.Region, value.PathStyle, value.PathPrefix));
    private static string TestDigest(StoredOSSSetting value) => Digest(string.Join("\0", LocationDigest(value), value.AccessKeyId, value.AccessKeySecret, value.SessionToken));
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class StoredOSSSetting
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("provider")] public string Provider { get; set; } = "aliyun";
        [JsonPropertyName("region")] public string Region { get; set; } = "";
        [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
        [JsonPropertyName("cdnBaseUrl")] public string CdnBaseUrl { get; set; } = "";
        [JsonPropertyName("bucket")] public string Bucket { get; set; } = "";
        [JsonPropertyName("accessKeyId")] public string AccessKeyId { get; set; } = "";
        [JsonPropertyName("accessKeySecret")] public string AccessKeySecret { get; set; } = "";
        [JsonPropertyName("publicBaseUrl")] public string PublicBaseUrl { get; set; } = "";
        [JsonPropertyName("pathPrefix")] public string PathPrefix { get; set; } = "open-ai-canvas";
        [JsonPropertyName("s3Preset")] public string S3Preset { get; set; } = "custom";
        [JsonPropertyName("pathStyle")] public bool PathStyle { get; set; }
        [JsonPropertyName("sessionToken")] public string SessionToken { get; set; } = "";
        [JsonPropertyName("allowUserS3")] public bool AllowUserS3 { get; set; }
        [JsonPropertyName("storageLocationId")] public string? StorageLocationId { get; set; }
        public OSSSettingRequest ToRequest() => new() { Enabled = Enabled, Provider = Provider, Region = Region,
            Endpoint = Endpoint, CdnBaseUrl = CdnBaseUrl, Bucket = Bucket, AccessKeyId = AccessKeyId,
            AccessKeySecret = AccessKeySecret, PublicBaseUrl = PublicBaseUrl, PathPrefix = PathPrefix,
            S3Preset = S3Preset, PathStyle = PathStyle, SessionToken = SessionToken, AllowUserS3 = AllowUserS3 };
        public StoredOSSSetting Clone() => (StoredOSSSetting)MemberwiseClone();
    }
}
