#nullable enable
using System.Net;
using System.Text;
using System.Text.Json;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Platform;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 对象存储通道的路由/降级与投递策略测试。
/// 对应 Go: <c>resource_test.go</c> 的上传降级与 <c>assets</c> 投递策略（无真实云依赖）。
/// </summary>
public sealed class ResourceObjectStorageTests : IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly string _dataDir;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly StorageSettingsService _settings;
    private readonly ResourceDomainService _domain;
    private readonly ResourceUploadService _upload;
    private readonly ResourceUploadService _uploadNoOSS;
    private readonly User _user = new() { ID = "u-oss", Username = "oss-user", Role = "user", Status = "active" };

    public ResourceObjectStorageTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-oss-{Guid.NewGuid():N}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-oss-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath};Pooling=False",
        });
        _repository = new Repository(_database);
        new SchemaMigrator(_database).MigrateAsync().GetAwaiter().GetResult();
        _repository.CreateAsync(_user).GetAwaiter().GetResult();
        _settings = new StorageSettingsService(_repository, _dataDir);
        _domain = new ResourceDomainService(
            _repository, new DefaultRuntimePolicyProvider(), _dataDir, storageSettings: _settings);
        _upload = new ResourceUploadService(
            _repository, new UploadQuota(_repository, new DefaultRuntimePolicyProvider()), _dataDir,
            storageSettings: _settings);
        _uploadNoOSS = new ResourceUploadService(
            _repository, new UploadQuota(_repository, new DefaultRuntimePolicyProvider()), _dataDir);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync().ConfigureAwait(false);
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // Windows 文件句柄释放延迟，测试环境可容忍。
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>平台存储设置：启用阿里云形态、指向不可达端点 + 公网 CDN（公开鉴权）。</summary>
    private void SeedPlatformOSS(string endpoint = "https://bucket.oss-cn-hangzhou.aliyuncs.com")
    {
        const string value = """
            {"enabled":true,"provider":"aliyun","region":"cn-hangzhou","endpoint":"{ENDPOINT}","cdnBaseUrl":"https://media.example.com","bucket":"bucket","accessKeyId":"test-id","accessKeySecret":"test-secret","publicBaseUrl":"","pathPrefix":"open-ai-canvas","s3Preset":"custom","pathStyle":false,"sessionToken":"","allowUserS3":false,"cdnAuthMode":"public","requireCDN":false,"allowPrivateProxy":true}
            """;
        _repository.SaveSystemSettingAsync(new SystemSetting
        {
            Key = "oss",
            ValueJSON = value.Replace("{ENDPOINT}", endpoint),
            UpdatedBy = "admin",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task 上传_云存储启用时路由到云_provider()
    {
        SeedPlatformOSS();
        // 源站不可达（端口 1 连接拒绝）：写入失败 → 降级 local；但 provider 已按配置改写。
        Resource resource = await _upload.UploadResourceFromStreamAsync(
            _user.ID, "a.png", 4, "image", 1, 1, 0,
            new MemoryStream([1, 2, 3, 4]), "image/png", "oss-route-1");
        // 降级后落本地可读，provider 回写 local。
        Assert.Equal("local", resource.Provider);
        Assert.True(resource.ObjectKey.StartsWith("open-ai-canvas/users/", StringComparison.Ordinal) is false);
        Assert.Equal(4, resource.Size);
        ResourceStream stream = await _domain.OpenResourceRangeAsync(
            resource.UserID, resource.ID, null);
        using MemoryStream buffer = new();
        await stream.Body.CopyToAsync(buffer);
        Assert.Equal([1, 2, 3, 4], buffer.ToArray());
    }

    [Fact]
    public async Task 上传_未注入存储服务时保持本地()
    {
        Resource resource = await _uploadNoOSS.UploadResourceFromStreamAsync(
            _user.ID, "b.png", 4, "image", 1, 1, 0,
            new MemoryStream([5, 6, 7, 8]), "image/png", "oss-route-2");
        Assert.Equal("local", resource.Provider);
    }

    [Fact]
    public async Task 投递_公开CDN_返回直链与无过期()
    {
        SeedPlatformOSS();
        // 直接落一条远程形态资源记录（模拟云上传成功的绑定）。
        Resource remote = SeedRemoteResource("open-ai-canvas/users/u-oss/image/2026/09/30/x.png");
        ResourceDelivery delivery = await _domain.PrepareResourceDeliveryAsync(_user.ID, remote.ID, new ResourceDeliveryOptions());
        Assert.False(string.IsNullOrEmpty(delivery.RedirectURL));
        Assert.StartsWith("https://media.example.com/open-ai-canvas/users/", delivery.RedirectURL, StringComparison.Ordinal);
        // 批量访问合同：CDN public → expiresAt 为空。
        IReadOnlyList<ResourceAccessResult> batch = await _domain.ResourceAccessBatchAsync(
            _user.ID, [new ResourceAccessRequest { ResourceID = remote.ID, Purpose = "display", Variant = "original" }]);
        Assert.Null(batch[0].Error);
        Assert.Equal("cdn", batch[0].Access!.Delivery);
        Assert.Null(batch[0].Access!.ExpiresAt);
    }

    [Fact]
    public async Task 投递_要求CDN未配置鉴权时显式失败()
    {
        SeedPlatformOSS();
        // 覆盖为 RequireCDN + 无 CDN 鉴权（auth mode 清空 → CDN 未启用）。
        SystemSetting? record = await _repository.SystemSettingAsync("oss");
        var stored = JsonDocument.Parse(record!.ValueJSON).RootElement.Clone();
        string updated = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["provider"] = "aliyun",
            ["region"] = "cn-hangzhou",
            ["endpoint"] = "https://bucket.oss-cn-hangzhou.aliyuncs.com",
            ["cdnBaseUrl"] = "https://media.example.com",
            ["bucket"] = "bucket",
            ["accessKeyId"] = "test-id",
            ["accessKeySecret"] = "test-secret",
            ["publicBaseUrl"] = "",
            ["pathPrefix"] = "open-ai-canvas",
            ["s3Preset"] = "custom",
            ["pathStyle"] = false,
            ["sessionToken"] = "",
            ["allowUserS3"] = false,
            ["cdnAuthMode"] = "",
            ["requireCDN"] = true,
            ["allowPrivateProxy"] = true,
        });
        await _repository.SaveSystemSettingAsync(new SystemSetting
        {
            Key = "oss", ValueJSON = updated, UpdatedBy = "admin",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        Resource remote = SeedRemoteResource("open-ai-canvas/users/u-oss/image/2026/09/30/y.png");
        IReadOnlyList<ResourceAccessResult> batch = await _domain.ResourceAccessBatchAsync(
            _user.ID, [new ResourceAccessRequest { ResourceID = remote.ID, Purpose = "display", Variant = "original" }]);
        Assert.NotNull(batch[0].Error);
        Assert.Equal(503, batch[0].Error!.Code);
        Assert.Equal("resource_cdn_unconfigured", batch[0].Error!.Reason);
    }

    [Fact]
    public async Task 投递_公网源站_返回签名直链()
    {
        // 无 CDN：配置公网 HTTPS 源站 → 签名源站直链。
        const string value = """
            {"enabled":true,"provider":"aliyun","region":"cn-hangzhou","endpoint":"https://bucket.oss-cn-hangzhou.aliyuncs.com","cdnBaseUrl":"","bucket":"bucket","accessKeyId":"test-id","accessKeySecret":"test-secret","publicBaseUrl":"","pathPrefix":"open-ai-canvas","s3Preset":"custom","pathStyle":false,"sessionToken":"","allowUserS3":false,"cdnAuthMode":"","requireCDN":false,"allowPrivateProxy":false}
            """;
        await _repository.SaveSystemSettingAsync(new SystemSetting
        {
            Key = "oss", ValueJSON = value, UpdatedBy = "admin",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        Resource remote = SeedRemoteResource("open-ai-canvas/users/u-oss/image/2026/09/30/z.png");
        ResourceDelivery delivery = await _domain.PrepareResourceDeliveryAsync(_user.ID, remote.ID, new ResourceDeliveryOptions());
        Assert.False(string.IsNullOrEmpty(delivery.RedirectURL));
        Assert.StartsWith("https://bucket.oss-cn-hangzhou.aliyuncs.com/open-ai-canvas/", delivery.RedirectURL, StringComparison.Ordinal);
        Assert.Contains("OSSAccessKeyId=test-id", delivery.RedirectURL, StringComparison.Ordinal);
        Assert.Contains("Signature=", delivery.RedirectURL, StringComparison.Ordinal);
        IReadOnlyList<ResourceAccessResult> batch = await _domain.ResourceAccessBatchAsync(
            _user.ID, [new ResourceAccessRequest { ResourceID = remote.ID, Purpose = "display", Variant = "original" }]);
        Assert.Equal("origin", batch[0].Access!.Delivery);
        // Go 语义：只有配置了 CDN 域名时才留“鉴权未配置”回退原因；无 CDN 时为空。
        Assert.Equal("", batch[0].Access!.FallbackReason);
    }

    [Fact]
    public async Task 代理_私有源站仅provider用途放行()
    {
        // HTTP 私网源站 + allowPrivateProxy：display 拒绝、provider-input 走平台代理。
        const string value = """
            {"enabled":true,"provider":"aliyun","region":"cn-hangzhou","endpoint":"http://storage.internal:9000","cdnBaseUrl":"","bucket":"bucket","accessKeyId":"test-id","accessKeySecret":"test-secret","publicBaseUrl":"https://example.com","pathPrefix":"open-ai-canvas","s3Preset":"custom","pathStyle":true,"sessionToken":"","allowUserS3":false,"cdnAuthMode":"","requireCDN":false,"allowPrivateProxy":true}
            """;
        await _repository.SaveSystemSettingAsync(new SystemSetting
        {
            Key = "oss", ValueJSON = value, UpdatedBy = "admin",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        Resource remote = SeedRemoteResource(
            "open-ai-canvas/users/u-oss/image/2026/09/30/p.png", "http://storage.internal:9000");
        IReadOnlyList<ResourceAccessResult> display = await _domain.ResourceAccessBatchAsync(
            _user.ID, [new ResourceAccessRequest { ResourceID = remote.ID, Purpose = "display", Variant = "original" }]);
        Assert.NotNull(display[0].Error);
        Assert.Equal("resource_origin_private", display[0].Error!.Reason);
        IReadOnlyList<ResourceAccessResult> provider = await _domain.ResourceAccessBatchAsync(
            _user.ID, [new ResourceAccessRequest { ResourceID = remote.ID, Purpose = "provider-input", Variant = "original" }]);
        Assert.Null(provider[0].Error);
        Assert.Equal("platform-proxy", provider[0].Access!.Delivery);
        Assert.Equal("private_origin", provider[0].Access!.FallbackReason);
        Assert.StartsWith("https://example.com/api/public/resources/", provider[0].Access!.Url, StringComparison.Ordinal);
    }

    private Resource SeedRemoteResource(string objectKey, string? endpoint = null, string bucket = "bucket")
    {
        Resource resource = new()
        {
            ID = IdGenerator.NewId(),
            UserID = _user.ID,
            Kind = "image",
            Status = ResourceStatus.ResourceStatusReady,
            Provider = "aliyun",
            ObjectKey = objectKey,
            Endpoint = endpoint ?? "https://bucket.oss-cn-hangzhou.aliyuncs.com",
            Bucket = bucket,
            MimeType = "image/png",
            Size = 1024,
            Width = 8,
            Height = 8,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            StorageSettingID = "loc-test",
        };
    _repository.CreateResourceAsync(resource).GetAwaiter().GetResult();
    return resource;
}
}
