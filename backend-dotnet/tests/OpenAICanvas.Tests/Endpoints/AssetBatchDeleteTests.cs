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

/// <summary>POST /assets/batch-delete 契约。对应 Go: <c>handler/user_data.go</c> 的批量彻底删除路由。</summary>
public sealed class AssetBatchDeleteTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;
    private string _userId = "";

    public AssetBatchDeleteTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-batch-del-{Guid.NewGuid():N}");
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
            acceptedTerms = true,
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        _adminClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _adminClient.DefaultRequestHeaders.Add("Cookie", cookie);
        return _adminClient;
    }

    private async Task<string> CurrentUserIdAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        User? user = await repository.UserByUsernameAsync("admin");
        Assert.NotNull(user);
        _userId = user.ID;
        return user.ID;
    }

    private async Task SeedAssetAsync(string assetId, string resourceId)
    {
        string userId = await CurrentUserIdAsync();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        DateTime now = DateTime.UtcNow;
        await repository.CreateAsync(new Resource
        {
            ID = resourceId,
            UserID = userId,
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
        await repository.CreateAsync(new Asset
        {
            ID = assetId,
            UserID = userId,
            Kind = "image",
            Category = "other",
            Status = "archived",
            Title = assetId,
            PayloadJSON = "{\"data\":{\"storageKey\":\"resource:" + resourceId + "\"}}",
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> PostBatchAsync(string json) =>
        _adminClient!.PostAsync("/api/assets/batch-delete", JsonBody(json));

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 未登录返回_401()
    {
        HttpResponseMessage response = await _client.PostAsync(
            "/api/assets/batch-delete", JsonBody("[\"asset-1\"]"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"ids\":[\"asset-1\"]}")]
    [InlineData("[1]")]
    [InlineData("[\"\"]")]
    public async Task 非法请求体返回_400(string body)
    {
        await SignInAsAdminAsync();
        HttpResponseMessage response = await PostBatchAsync(body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 超过上限返回_400()
    {
        await SignInAsAdminAsync();
        string body = JsonSerializer.Serialize(Enumerable.Range(0, 1001).Select(index => $"asset-{index}"));
        HttpResponseMessage response = await PostBatchAsync(body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 批量删除成功并返回去重后的_ID()
    {
        await SignInAsAdminAsync();
        await SeedAssetAsync("asset-b1", "RES_B1");
        await SeedAssetAsync("asset-b2", "RES_B2");

        HttpResponseMessage response = await PostBatchAsync("[\"asset-b1\",\" asset-b2 \",\"asset-b1\"]");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(new[] { "asset-b1", "asset-b2" }, data.GetProperty("ids").EnumerateArray()
            .Select(item => item.GetString()).ToArray());

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        Assert.Null(await repository.AssetForUserAsync(_userId, "asset-b1"));
        Assert.Null(await repository.AssetForUserAsync(_userId, "asset-b2"));
        Assert.Empty(await repository.ResourcesForUserIDsAsync(_userId, ["RES_B1", "RES_B2"]));
    }

    [Fact]
    public async Task 含有他人或不存在素材时整批保留()
    {
        await SignInAsAdminAsync();
        await SeedAssetAsync("asset-b3", "RES_B3");
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            User other = new()
            {
                ID = "user-batch-other",
                Username = "batch-other",
                DisplayName = "其他用户",
                Email = "batch-other@example.com",
                Role = UserRole.UserRoleUser,
                Status = UserStatus.UserStatusActive,
                PasswordHash = OpenAICanvas.Auth.AuthService.HashPassword("password123"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            await repository.CreateAsync(other);
            await repository.CreateAsync(new Asset
            {
                ID = "asset-batch-foreign",
                UserID = other.ID,
                Kind = "image",
                Category = "other",
                Status = "archived",
                Title = "他人素材",
                PayloadJSON = "{}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        HttpResponseMessage response = await PostBatchAsync(
            "[\"asset-b3\",\"asset-batch-foreign\"]");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
        Repository verify = verifyScope.ServiceProvider.GetRequiredService<Repository>();
        Assert.NotNull(await verify.AssetForUserAsync(_userId, "asset-b3"));
        Assert.NotNull(await verify.AssetForUserAsync("user-batch-other", "asset-batch-foreign"));
        Assert.Single(await verify.ResourcesForUserIDsAsync(_userId, ["RES_B3"]));
    }

    [Fact]
    public async Task 共享物理对象只删一次_全部素材删除后才清理文件()
    {
        await SignInAsAdminAsync();
        await SeedAssetAsync("asset-share-1", "RES_SHARE");
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            await repository.CreateAsync(new Asset
            {
                ID = "asset-share-2",
                UserID = _userId,
                Kind = "image",
                Category = "other",
                Status = "archived",
                Title = "共享素材",
                PayloadJSON = "{\"data\":{\"storageKey\":\"resource:RES_SHARE\"}}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        string objectPath = Path.Combine(_dataDir, "resources", "assets/RES_SHARE.png");

        HttpResponseMessage first = await PostBatchAsync("[\"asset-share-1\"]");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(File.Exists(objectPath));
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            Assert.Single(await repository.ResourcesForUserIDsAsync(_userId, ["RES_SHARE"]));
        }

        HttpResponseMessage second = await PostBatchAsync("[\"asset-share-2\"]");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(File.Exists(objectPath));
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            Assert.Empty(await repository.ResourcesForUserIDsAsync(_userId, ["RES_SHARE"]));
        }
    }
}
