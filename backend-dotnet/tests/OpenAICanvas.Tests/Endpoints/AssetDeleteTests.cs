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
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using Xunit;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

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
    public async Task 删除已完成任务历史输出引用的素材()
    {
        await SignInAsAdminAsync();
        await SeedResourceAndAssetAsync("RES_DEL3", "asset-del3");

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            DateTime now = DateTime.UtcNow;
            string output = "{\"url\":\"/api/resources/RES_DEL3/file\"}";
            await repository.CreateAsync(new TaskEntity
            {
                ID = "task-del-complete",
                UserID = _userId,
                Status = TaskStatus.TaskStatusSucceeded,
                Prompt = "已完成生成",
                ResultJSON = output,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await repository.CreateAsync(new TaskLog
            {
                ID = "task-log-del-complete",
                UserID = _userId,
                TaskID = "task-del-complete",
                Message = "已完成",
                Payload = output,
                CreatedAt = now,
            });
            await repository.CreateAsync(new Result
            {
                ID = "result-del-complete",
                UserID = _userId,
                TaskID = "task-del-complete",
                Kind = "image",
                URL = "/api/resources/RES_DEL3/file",
                Payload = output,
                CreatedAt = now,
            });
        }

        HttpResponseMessage deleted = await _adminClient!.DeleteAsync("/api/assets/asset-del3");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        await using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
        Repository verifyRepository = verifyScope.ServiceProvider.GetRequiredService<Repository>();
        Assert.Null(await verifyRepository.AssetForUserAsync(_userId, "asset-del3"));
    }

    [Fact]
    public async Task 删除被项目链接引用的素材返回占用提示()
    {
        await SignInAsAdminAsync();
        await SeedResourceAndAssetAsync("RES_DEL_PROJECT", "asset-del-project");

        HttpResponseMessage project = await _adminClient!.PostAsJsonAsync("/api/projects", new { name = "引用项目" });
        project.EnsureSuccessStatusCode();
        string projectId = (await ReadDataAsync(project)).GetProperty("project").GetProperty("id").GetString()!;
        HttpResponseMessage linked = await _adminClient!.PostAsJsonAsync(
            $"/api/projects/{projectId}/assets",
            new { assetId = "asset-del-project", category = "other" });
        linked.EnsureSuccessStatusCode();

        HttpResponseMessage deleted = await _adminClient!.DeleteAsync("/api/assets/asset-del-project");
        Assert.Equal(HttpStatusCode.BadRequest, deleted.StatusCode);
        Assert.Contains("项目", await deleted.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        Assert.NotNull(await repository.AssetForUserAsync(_userId, "asset-del-project"));
    }

    [Fact]
    public async Task 删除其他用户素材不泄露归属并保留记录()
    {
        await SignInAsAdminAsync();
        User other = new()
        {
            ID = "user-other",
            Username = "other-user",
            DisplayName = "其他用户",
            Email = "other@example.com",
            Role = UserRole.UserRoleUser,
            Status = UserStatus.UserStatusActive,
            PasswordHash = OpenAICanvas.Auth.AuthService.HashPassword("password123"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            await repository.CreateAsync(other);
            await repository.CreateAsync(new Asset
            {
                ID = "asset-other-owner",
                UserID = other.ID,
                Kind = "image",
                Category = "other",
                Status = "confirmed",
                Title = "其他用户素材",
                PayloadJSON = "{\"id\":\"asset-other-owner\"}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        HttpResponseMessage deleted = await _adminClient!.DeleteAsync("/api/assets/asset-other-owner");
        Assert.Equal(HttpStatusCode.InternalServerError, deleted.StatusCode);
        Assert.DoesNotContain("其他用户素材", await deleted.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
        Repository verifyRepository = verifyScope.ServiceProvider.GetRequiredService<Repository>();
        Assert.NotNull(await verifyRepository.AssetForUserAsync(other.ID, "asset-other-owner"));
    }

    [Fact]
    public async Task 删除路径逃逸资源不会删除资源记录或外部文件()
    {
        await SignInAsAdminAsync();
        await using (AsyncServiceScope ownerScope = _factory.Services.CreateAsyncScope())
        {
            Repository ownerRepository = ownerScope.ServiceProvider.GetRequiredService<Repository>();
            User? owner = await ownerRepository.UserByUsernameAsync("admin");
            Assert.NotNull(owner);
            _userId = owner.ID;
        }
        string outsidePath = Path.Combine(_dataDir, "outside-delete-guard.txt");
        await File.WriteAllTextAsync(outsidePath, "keep");

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            await repository.CreateAsync(new Resource
            {
                ID = "RES_DEL_ESCAPE",
                UserID = _userId,
                Kind = "image",
                Status = "ready",
                Provider = "local",
                ObjectKey = "../outside-delete-guard.txt",
                MimeType = "image/png",
                Size = 4,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await repository.CreateAsync(new Asset
            {
                ID = "asset-del-escape",
                UserID = _userId,
                Kind = "image",
                Category = "other",
                Status = "confirmed",
                Title = "路径保护素材",
                PayloadJSON = "{\"data\":{\"storageKey\":\"resource:RES_DEL_ESCAPE\"}}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        HttpResponseMessage deleted = await _adminClient!.DeleteAsync("/api/assets/asset-del-escape");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.True(File.Exists(outsidePath));
        Assert.Equal("keep", await File.ReadAllTextAsync(outsidePath));
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
