#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// DELETE /assets/:id 的资源级联删除契约：被业务引用拒绝、
/// 无引用时同事务清理（版本/表现/链接/候选/绑定/资源）并生成删除任务、本地物理文件清理。
/// 对应 Go: <c>app/resource_delete.go</c>。
/// </summary>
public sealed class AssetDeleteTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public AssetDeleteTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-del-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

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
        _client.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_dataDir))
        {
            // 连接池释放有延迟，重试几次避免句柄占用导致清理失败。
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

    private async Task<HttpClient> SignInAsAdminAsync()
    {
        if (_adminClient is not null)
        {
            return _adminClient;
        }
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "admin",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        _adminClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _adminClient.DefaultRequestHeaders.Add("Cookie", cookie);
        return _adminClient;
    }

    private static StringContent AssetBody(string id, string title, string resourceRef) =>
        new(
            JsonSerializer.Serialize(new
            {
                asset = new
                {
                    id,
                    title,
                    kind = "image",
                    category = "other",
                    coverUrl = $"/api/resources/{resourceRef}/file",
                    tags = new[] { "x" },
                    data = new
                    {
                        storageKey = $"resource:{resourceRef}",
                        width = 8,
                        height = 8,
                        bytes = 64,
                        mimeType = "image/png",
                    },
                },
            }),
            Encoding.UTF8,
            "application/json");

    private string _userId = "";

    private async Task<Asset> SeedResourceAndAssetAsync(string resourceId, string assetId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        DateTime now = DateTime.UtcNow;
        User? user = await repository.UserByUsernameAsync("admin");
        Assert.NotNull(user);
        _userId = user.ID;

        // 资源记录（ready）+ 本地物理文件。
        await repository.CreateAsync(new Resource
        {
            ID = resourceId,
            UserID = user.ID,
            Kind = "image",
            Status = "ready",
            Provider = "local",
            ObjectKey = $"assets/{resourceId}.png",
            MimeType = "image/png",
            Size = 64,
            CreatedAt = now,
            UpdatedAt = now,
        });
        string objectPath = Path.Combine(_dataDir, "resources", $"assets/{resourceId}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        await File.WriteAllBytesAsync(objectPath, [1, 2, 3]);

        // 素材记录（直接调仓储，绕过路由的 5MB/限流）。
        HttpResponseMessage response = await _adminClient!.PutAsync(
            $"/api/assets/{assetId}",
            AssetBody(assetId, "待删素材", resourceId));
        response.EnsureSuccessStatusCode();
        string userId = _userId;
        Asset? asset = await repository.AssetForUserAsync(userId, assetId);
        Assert.NotNull(asset);
        return asset!;
    }

    [Fact]
    public async Task 删除无引用素材_资源同删且本地文件清理()
    {
        await SignInAsAdminAsync();
        await SeedResourceAndAssetAsync("RES_DEL1", "asset-del");

        HttpResponseMessage deleted = await _adminClient!.DeleteAsync("/api/assets/asset-del");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal("asset-del", (await ReadDataAsync(deleted)).GetProperty("id").GetString());

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        Assert.Null(await repository.AssetForUserAsync(_userId, "asset-del"));
        IReadOnlyList<Resource> remaining = await repository.ResourcesForUserIDsAsync("admin", ["RES_DEL1"]);
        Assert.Empty(remaining);

        // 本地物理文件已被 drain 清理。
        Assert.False(File.Exists(Path.Combine(_dataDir, "resources", "assets/RES_DEL1.png")));

        // 删除任务已完成。
        IReadOnlyList<ResourceDeletionJob> jobs = await repository.PendingResourceDeletionJobsAsync();
        Assert.All(jobs, job => Assert.NotEqual("RES_DEL1", job.ResourceID));
    }

    [Fact]
    public async Task 删除被画布引用的素材返回占用提示()
    {
        await SignInAsAdminAsync();
        await SeedResourceAndAssetAsync("RES_DEL2", "asset-del2");

        // 画布引用该素材（media 节点 assetId+storageKey）。
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            User? owner = await repository.UserByUsernameAsync("admin");
            Assert.NotNull(owner);
            DateTime now = DateTime.UtcNow;
            await repository.CreateAsync(new CanvasProject
            {
                ID = "canvas-block",
                UserID = owner.ID,
                Title = "引用画布",
                PayloadJSON = """
                    {"nodes":[{"id":"n1","type":"image","metadata":{"assetId":"asset-del2","storageKey":"resource:RES_DEL2"}}]}
                    """,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        HttpResponseMessage deleted = await _adminClient!.DeleteAsync("/api/assets/asset-del2");
        Assert.Equal(HttpStatusCode.BadRequest, deleted.StatusCode);
        string body = await deleted.Content.ReadAsStringAsync();
        Assert.Contains("素材仍被", body, StringComparison.Ordinal);

        // 素材未被删除。
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            Assert.NotNull(await repository.AssetForUserAsync(_userId, "asset-del2"));
        }
    }

    [Fact]
    public async Task 删除不存在的素材返回_500()
    {
        await SignInAsAdminAsync();

        HttpResponseMessage response = await _adminClient!.DeleteAsync("/api/assets/asset-nope");

        // Go 返回裸 gorm.ErrRecordNotFound → failService 500 固定文案。
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }
}
