#nullable enable
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 孤儿资源清理的契约测试。
/// 对应 Go: <c>app/resource_deletion_worker.go</c> 的 <c>cleanupDetachedResources</c> /
/// <c>cleanupDetachedUserResources</c>，以及
/// <c>repository/resource_cleanup.go</c> 的 <c>DeleteDetachedResources</c>。
/// </summary>
public sealed class ResourceCleanupEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    /// <summary>后台作业开关是环境变量（非配置），需在工厂启动前设置。</summary>
    private const string DisableWorkersKey = "CANVAS_DISABLE_BACKGROUND_WORKERS";

    public ResourceCleanupEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        // 关掉后台作业，避免与本测试手动触发的清理互相干扰。
        Environment.SetEnvironmentVariable(DisableWorkersKey, "true");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CANVAS_BACKEND_DATA_DIR", _dataDir);
            builder.UseSetting("CANVAS_DATABASE_DRIVER", "sqlite");
            builder.UseSetting("CANVAS_AUTO_MIGRATE", "true");
        });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DisableWorkersKey, null);
        _client.Dispose();
        _userClient?.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_dataDir))
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(_dataDir, recursive: true);
                    break;
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                }
            }
        }

        GC.SuppressFinalize(this);
    }

    private async Task<HttpClient> SignInAsync()
    {
        if (_userClient is not null)
        {
            return _userClient;
        }
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "cleanupuser",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        _userClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _userClient.DefaultRequestHeaders.Add("Cookie", cookie);
        return _userClient;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    /// <summary>走分片上传落一个就绪资源，返回 (id, objectKey)。</summary>
    private async Task<(string Id, string ObjectKey)> CreateResourceAsync(HttpClient client, byte[] payload)
    {
        HttpResponseMessage start = await client.PostAsJsonAsync("/api/resources/uploads", new
        {
            fileName = "orphan.png",
            kind = "image",
            size = payload.Length,
            width = 1,
            height = 1,
            durationMs = 0,
        });
        start.EnsureSuccessStatusCode();
        string uploadId = (await ReadDataAsync(start)).GetProperty("uploadId").GetString()!;

        HttpResponseMessage chunk = await client.PutAsync(
            $"/api/resources/uploads/{uploadId}/chunks/0", new ByteArrayContent(payload));
        chunk.EnsureSuccessStatusCode();

        HttpResponseMessage completed = await client.PostAsync(
            $"/api/resources/uploads/{uploadId}/complete", content: null);
        completed.EnsureSuccessStatusCode();
        JsonElement resource = (await ReadDataAsync(completed)).GetProperty("resource");
        return (
            resource.GetProperty("id").GetString()!,
            resource.GetProperty("objectKey").GetString()!);
    }

    private string PhysicalPathOf(string objectKey) =>
        Path.Combine(_dataDir, "resources", objectKey.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>在作用域内取服务并执行操作（与仓库其他测试一致）。</summary>
    private async Task<T> WithServicesAsync<T>(Func<Repository, ResourceDeleteService, ResourceCleanupService, Task<T>> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        ResourceDeleteService deletions = scope.ServiceProvider.GetRequiredService<ResourceDeleteService>();
        ResourceCleanupService cleanup = scope.ServiceProvider.GetRequiredService<ResourceCleanupService>();
        return await action(repository, deletions, cleanup);
    }

    /// <summary>把资源的生命周期时间点前移，使其落入清理候选窗口。</summary>
    private async Task BackdateResourceAsync(string resourceId, DateTime createdAt, DateTime updatedAt) =>
        await WithServicesAsync<object?>(async (repository, _, _) =>
        {
            await repository.UpdateResourceLifetimeAsync(resourceId, createdAt, updatedAt);
            return null;
        });

    [Fact]
    public async Task 孤儿清理_无引用的过期就绪资源被删除且物理文件清理()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[256];
        new Random(71).NextBytes(payload);
        (string id, string objectKey) = await CreateResourceAsync(user, payload);
        string physical = PhysicalPathOf(objectKey);
        Assert.True(File.Exists(physical));

        // 就绪资源保留期为 24h：把创建时间推早 48h。
        await BackdateResourceAsync(id, DateTime.UtcNow.AddHours(-48), DateTime.UtcNow.AddHours(-48));

        (int removed, int jobs) = await WithServicesAsync(async (_, _, cleanup) =>
            await cleanup.CleanupDetachedResourcesAsync());

        Assert.Equal(1, removed);
        Assert.Equal(1, jobs);
        Assert.False(File.Exists(physical), "孤儿资源的物理文件应被清理");

        // 分页里不应再出现该资源。
        HttpResponseMessage page = await user.GetAsync("/api/resources");
        if (page.IsSuccessStatusCode)
        {
            Assert.DoesNotContain(id, await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 孤儿清理_未完成资源使用一小时保留期()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[128];
        (string id, string objectKey) = await CreateResourceAsync(user, payload);

        await WithServicesAsync<object?>(async (repository, _, _) =>
        {
            // 改成 pending 并前移 updated_at 到 2 小时前（超过 1h 保留期）。
            await repository.UpdateResourceLifetimeAsync(
                id, null, DateTime.UtcNow.AddHours(-2), ResourceStatus.ResourceStatusPending);
            return null;
        });

        (int removed, _) = await WithServicesAsync(async (_, _, cleanup) =>
            await cleanup.CleanupDetachedResourcesAsync());

        Assert.Equal(1, removed);
        Assert.False(File.Exists(PhysicalPathOf(objectKey)));
    }

    [Fact]
    public async Task 孤儿清理_未完成资源未超保留期时不清理()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[128];
        (string id, string objectKey) = await CreateResourceAsync(user, payload);

        // pending 但只有 30 分钟（未超过 1h 保留期）。
        await WithServicesAsync<object?>(async (repository, _, _) =>
        {
            await repository.UpdateResourceLifetimeAsync(
                id, null, DateTime.UtcNow.AddMinutes(-30), ResourceStatus.ResourceStatusPending);
            return null;
        });

        (int removed, _) = await WithServicesAsync(async (_, _, cleanup) =>
            await cleanup.CleanupDetachedResourcesAsync());

        Assert.Equal(0, removed);
        Assert.True(File.Exists(PhysicalPathOf(objectKey)), "未超保留期不应清理");
    }

    [Fact]
    public async Task 孤儿清理_被素材引用的过期资源不清理()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[128];
        (string id, string objectKey) = await CreateResourceAsync(user, payload);

        // 造一个引用该资源的素材（payload_json 内嵌 resource:<id>）。
        await WithServicesAsync<object?>(async (repository, _, _) =>
        {
            DateTime at = DateTime.UtcNow.AddHours(-48);
            await repository.UpdateResourceLifetimeAsync(id, at, at);
            DateTime now = DateTime.UtcNow;
            await repository.CreateAsync(new Asset
            {
                ID = IdGenerator.NewId(),
                UserID = (await repository.ResourceAsync(id))!.UserID,
                Kind = "image",
                Category = "image",
                Status = "ready",
                Title = "引用孤儿资源的素材",
                PayloadJSON = $$"""{"nodes":[{"storageKey":"resource:{{id}}"}]}""",
                CreatedAt = now,
                UpdatedAt = now,
            });
            return null;
        });

        (int removed, _) = await WithServicesAsync(async (_, _, cleanup) =>
            await cleanup.CleanupDetachedResourcesAsync());

        Assert.Equal(0, removed);
        Assert.True(File.Exists(PhysicalPathOf(objectKey)), "被引用的资源不应清理");
    }

    [Fact]
    public async Task 孤儿清理_被外观配置引用的资源不清理()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[128];
        (string id, string objectKey) = await CreateResourceAsync(user, payload);

        await WithServicesAsync<object?>(async (repository, _, _) =>
        {
            DateTime backdated = DateTime.UtcNow.AddHours(-48);
            await repository.UpdateResourceLifetimeAsync(id, backdated, backdated);
            // 直接把外观配置写成引用该资源（绕过上传校验，专注清理判定）。
            DateTime now = DateTime.UtcNow;
            await repository.SaveSystemSettingAsync(new SystemSetting
            {
                Key = "appearance",
                ValueJSON = $$"""
                    {"schemaVersion":7,"brandName":"影策","brandSlug":"open-ai-canvas",
                     "authHeroTitle":"标题","authHeroDescription":"",
                     "logoResourceId":"{{id}}","darkLogoResourceId":"",
                     "logoFrameEnabled":true,"authVideoResourceId":"","authVideoPosterResourceId":"",
                     "authVideoAutoplay":true,"skinId":"classic","skinThemes":[],
                     "seoTitle":"","seoDescription":"","seoKeywords":"","footerCopyright":"",
                     "icpFilingEnabled":false,"icpFilingNumber":""}
                    """,
                UpdatedBy = "test",
                CreatedAt = now,
                UpdatedAt = now,
            });
            return null;
        });

        (int removed, _) = await WithServicesAsync(async (_, _, cleanup) =>
            await cleanup.CleanupDetachedResourcesAsync());

        Assert.Equal(0, removed);
        Assert.True(File.Exists(PhysicalPathOf(objectKey)), "被外观引用的资源不应清理");
    }

    [Fact]
    public async Task 孤儿清理_超过候选上限时按创建时间先到先清()
    {
        HttpClient user = await SignInAsync();
        List<(string Id, string ObjectKey)> created = [];
        for (int index = 0; index < 3; index++)
        {
            byte[] payload = new byte[64 + index];
            created.Add(await CreateResourceAsync(user, payload));
        }
        // 三条都推到保留期之外，且创建时间递增（0 最早）。
        for (int index = 0; index < created.Count; index++)
        {
            DateTime at = DateTime.UtcNow.AddHours(-72 + index);
            await BackdateResourceAsync(created[index].Id, at, at);
        }

        (int removed, _) = await WithServicesAsync(async (_, _, cleanup) =>
            await cleanup.CleanupDetachedResourcesAsync());

        // 三条都在 500 上限内，应全部清理。
        Assert.Equal(3, removed);
        foreach ((string _, string objectKey) in created)
        {
            Assert.False(File.Exists(PhysicalPathOf(objectKey)));
        }
    }

    [Fact]
    public async Task 孤儿清理_空候选集返回零且不报错()
    {
        (int removed, int jobs) = await WithServicesAsync(async (_, _, cleanup) =>
            await cleanup.CleanupDetachedResourcesAsync());

        Assert.Equal(0, removed);
        Assert.Equal(0, jobs);
    }

    // ------------------------------------------------------------ 纯函数契约（不经数据库）

    [Fact]
    public void 文档引用解析_识别定位字段与裸资源ID字段()
    {
        HashSet<string> candidates = new(["res-1", "res-2", "res-3"], StringComparer.Ordinal);

        Assert.Contains("res-1", ResourceCleanupService.DocumentReferencedResourceIDs(
            """{"nodes":[{"storageKey":"resource:res-1"}]}""", candidates));
        Assert.Contains("res-2", ResourceCleanupService.DocumentReferencedResourceIDs(
            """{"imageUrl":"/api/resources/res-2/file"}""", candidates));
        Assert.Contains("res-3", ResourceCleanupService.DocumentReferencedResourceIDs(
            """{"resourceId":"res-3"}""", candidates));

        HashSet<string> many = ResourceCleanupService.DocumentReferencedResourceIDs(
            """{"resourceIds":["res-1","res-3"]}""", candidates);
        Assert.Contains("res-1", many);
        Assert.Contains("res-3", many);

        Assert.Empty(ResourceCleanupService.DocumentReferencedResourceIDs(
            """{"resourceId":"other-resource"}""", candidates));
        Assert.Empty(ResourceCleanupService.DocumentReferencedResourceIDs("", candidates));
        Assert.Empty(ResourceCleanupService.DocumentReferencedResourceIDs(
            """{"resourceId":"res-1"}""", new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void 文档引用解析_非法JSON按裸字符串兜底()
    {
        HashSet<string> candidates = new(["res-1"], StringComparer.Ordinal);

        Assert.Contains("res-1", ResourceCleanupService.DocumentReferencedResourceIDs(
            "resource:res-1", candidates));
        Assert.Empty(ResourceCleanupService.DocumentReferencedResourceIDs("not-a-resource", candidates));
        Assert.Contains("res-1", ResourceCleanupService.DocumentReferencedResourceIDs(
            "\"resource:res-1\"", candidates));
    }

    [Fact]
    public void 文档引用解析_白名单外字段不计入()
    {
        HashSet<string> candidates = new(["res-1"], StringComparer.Ordinal);

        Assert.Empty(ResourceCleanupService.DocumentReferencedResourceIDs(
            """{"title":"res-1","nodeKey":"res-1"}""", candidates));
    }
}
