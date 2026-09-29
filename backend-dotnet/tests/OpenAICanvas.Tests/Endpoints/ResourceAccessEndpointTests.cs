#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 资源访问批量签发路由的契约测试。
/// 对应 Go: <c>handler/user_data.go</c> 的 <c>POST /resources/access</c> 与
/// <c>app/resource_access.go</c> 的 <c>ResourceAccessBatch</c> /
/// <c>signedResourceAccessURL</c>，以及 <c>assets/access.go</c> 的
/// <c>NormalizeAccessOptions</c> / <c>ResolveAccess</c> 本地路径。
/// </summary>
public sealed class ResourceAccessEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly Dictionary<string, HttpClient> _userClients = new();

    public ResourceAccessEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-access-{Guid.NewGuid():N}");
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
        foreach (HttpClient userClient in _userClients.Values)
        {
            userClient.Dispose();
        }
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

    private async Task<HttpClient> SignInAsync(string username)
    {
        if (_userClients.TryGetValue(username, out HttpClient? existing))
        {
            return existing;
        }
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username,
            password = "password123",
            acceptedTerms = true,
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        HttpClient user = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        user.DefaultRequestHeaders.Add("Cookie", cookie);
        _userClients[username] = user;
        return user;
    }

    /// <summary>公开注册默认关闭：除首个账号外，其余用户经仓储直插 + 登录获取会话。</summary>
    private async Task<HttpClient> CreateUserAndSignInAsync(string username)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        User stranger = new()
        {
            ID = IdGenerator.NewId(),
            Username = username,
            DisplayName = username,
            Email = username + "@example.com",
            Role = UserRole.UserRoleUser,
            Status = UserStatus.UserStatusActive,
            PasswordHash = OpenAICanvas.Auth.AuthService.HashPassword("password123"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await repository.CreateAsync(stranger);

        HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password = "password123",
        });
        login.EnsureSuccessStatusCode();
        string cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        HttpClient user = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        user.DefaultRequestHeaders.Add("Cookie", cookie);
        _userClients[username] = user;
        return user;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<JsonElement> ReadEnvelopeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>走完整分片上传流程落一个资源，返回 (id, userId, 字节内容)。</summary>
    private async Task<(string Id, string UserID, byte[] Payload)> CreateResourceAsync(
        HttpClient user, byte[] payload, string fileName = "sample.png", string kind = "image")
    {
        HttpResponseMessage start = await user.PostAsJsonAsync("/api/resources/uploads", new
        {
            fileName,
            kind,
            size = payload.Length,
            width = 4,
            height = 4,
            durationMs = 0,
        });
        start.EnsureSuccessStatusCode();
        string uploadId = (await ReadDataAsync(start)).GetProperty("uploadId").GetString()!;

        HttpResponseMessage chunk = await user.PutAsync(
            $"/api/resources/uploads/{uploadId}/chunks/0", new ByteArrayContent(payload));
        chunk.EnsureSuccessStatusCode();

        HttpResponseMessage completed = await user.PostAsync(
            $"/api/resources/uploads/{uploadId}/complete", content: null);
        completed.EnsureSuccessStatusCode();
        JsonElement resource = (await ReadDataAsync(completed)).GetProperty("resource");
        return (resource.GetProperty("id").GetString()!, resource.GetProperty("userId").GetString()!, payload);
    }

    /// <summary>仓储直插一个未上传完成的资源。对应 Go: status != ResourceStatusReady。</summary>
    private async Task<string> CreatePendingResourceAsync(string userId)
    {
        Repository repository = _factory.Services.GetRequiredService<Repository>();
        Resource resource = new()
        {
            ID = Guid.NewGuid().ToString("N"),
            UserID = userId,
            Kind = "image",
            Status = "uploading",
            Provider = "local",
            ObjectKey = $"pending/{Guid.NewGuid():N}.png",
            Size = 16,
            MimeType = "image/png",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await repository.CreateResourceAsync(resource);
        return resource.ID;
    }

    private async Task MarkPlaybackReadyAsync(string resourceId, byte[] playback)
    {
        Repository repository = _factory.Services.GetRequiredService<Repository>();
        Resource resource = (await repository.ResourceAsync(resourceId))!;
        string key = $"playback/{resourceId}.mp4";
        string path = Path.Combine(_dataDir, "resources", "playback", $"{resourceId}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, playback);
        resource.PlaybackStatus = "ready";
        resource.PlaybackObjectKey = key;
        resource.UpdatedAt = DateTime.UtcNow;
        await repository.SaveResourceAsync(resource);
    }

    private async Task<JsonElement> AccessAsync(
        HttpClient user, string resourceId, string purpose = "display", string variant = "original")
    {
        HttpResponseMessage response = await user.PostAsJsonAsync("/api/resources/access",
            new[] { new { resourceId, purpose, variant } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(0, (await ReadEnvelopeAsync(response)).GetProperty("code").GetInt32());
        JsonElement items = data.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        return items[0];
    }

    [Fact]
    public async Task 批量访问_签发平台本地访问与可匿名拉取的签名地址()
    {
        HttpClient user = await SignInAsync("owner");
        byte[] payload = new byte[2048];
        new Random(41).NextBytes(payload);
        (string id, string _, _) = await CreateResourceAsync(user, payload);

        JsonElement item = await AccessAsync(user, id, purpose: "browser-process");
        JsonElement access = item.GetProperty("access");
        Assert.Equal(id, access.GetProperty("resourceId").GetString());
        Assert.Equal("original", access.GetProperty("requestedVariant").GetString());
        Assert.Equal("original", access.GetProperty("actualVariant").GetString());
        Assert.Equal("platform-local", access.GetProperty("delivery").GetString());
        Assert.False(access.TryGetProperty("fallbackReason", out _));
        string url = access.GetProperty("url").GetString()!;
        Assert.StartsWith($"/api/public/resources/{id}/file?expires=", url, StringComparison.Ordinal);
        Assert.EndsWith("&variant=original", url, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(access.GetProperty("revision").GetString()));
        DateTime issuedAt = access.GetProperty("issuedAt").GetDateTime();
        DateTime expiresAt = access.GetProperty("expiresAt").GetDateTime();
        DateTime refreshAt = access.GetProperty("refreshAt").GetDateTime();
        Assert.True(expiresAt > issuedAt);
        Assert.True(refreshAt <= expiresAt);

        // 签名地址必须匿名可拉取且内容一致。
        HttpResponseMessage fetched = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(payload, await fetched.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task 批量访问_playback未就绪回退原件_就绪后下发副本()
    {
        HttpClient user = await SignInAsync("owner");
        byte[] original = new byte[256];
        new Random(42).NextBytes(original);
        (string id, string _, _) = await CreateResourceAsync(user, original, fileName: "clip.mp4", kind: "video");

        JsonElement fallback = await AccessAsync(user, id, purpose: "display", variant: "playback");
        JsonElement fallbackAccess = fallback.GetProperty("access");
        Assert.Equal("original", fallbackAccess.GetProperty("actualVariant").GetString());
        Assert.Equal("playback_not_ready", fallbackAccess.GetProperty("fallbackReason").GetString());
        Assert.Equal(original, await _client.GetByteArrayAsync(fallbackAccess.GetProperty("url").GetString()));

        byte[] playback = Enumerable.Range(1, 37).Select(index => (byte)index).ToArray();
        await MarkPlaybackReadyAsync(id, playback);

        JsonElement ready = await AccessAsync(user, id, purpose: "display", variant: "playback");
        JsonElement readyAccess = ready.GetProperty("access");
        Assert.Equal("playback", readyAccess.GetProperty("actualVariant").GetString());
        Assert.False(readyAccess.TryGetProperty("fallbackReason", out _));
        string url = readyAccess.GetProperty("url").GetString()!;
        Assert.EndsWith("&variant=playback", url, StringComparison.Ordinal);
        Assert.Equal(playback, await _client.GetByteArrayAsync(url));
    }

    [Fact]
    public async Task 批量访问_无效用途与变体逐项报错()
    {
        HttpClient user = await SignInAsync("owner");
        (string id, string _, _) = await CreateResourceAsync(user, new byte[32]);

        HttpResponseMessage response = await user.PostAsJsonAsync("/api/resources/access", new object[]
        {
            new { resourceId = id, purpose = "hax", variant = "original" },
            new { resourceId = id, purpose = "display", variant = "hax" },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement items = (await ReadDataAsync(response)).GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());

        Assert.Equal(400, items[0].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("资源访问用途无效", items[0].GetProperty("error").GetProperty("msg").GetString());
        Assert.Equal(400, items[1].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("资源变体无效", items[1].GetProperty("error").GetProperty("msg").GetString());
    }

    [Fact]
    public async Task 批量访问_他人资源与缺失资源逐项404()
    {
        HttpClient owner = await SignInAsync("owner");
        HttpClient stranger = await CreateUserAndSignInAsync("stranger");
        (string id, string _, _) = await CreateResourceAsync(owner, new byte[32]);

        JsonElement foreign = await AccessAsync(stranger, id);
        Assert.Equal(404, foreign.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("资源不存在或不可访问", foreign.GetProperty("error").GetProperty("msg").GetString());

        JsonElement missing = await AccessAsync(owner, "not-exist");
        Assert.Equal(404, missing.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("资源不存在或不可访问", missing.GetProperty("error").GetProperty("msg").GetString());
    }

    [Fact]
    public async Task 批量访问_未就绪资源逐项409()
    {
        HttpClient user = await SignInAsync("owner");
        (_, string ownerId, _) = await CreateResourceAsync(user, new byte[8]);
        string pendingId = await CreatePendingResourceAsync(ownerId);

        JsonElement item = await AccessAsync(user, pendingId);
        Assert.Equal(409, item.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("resource_not_ready", item.GetProperty("error").GetProperty("reason").GetString());
        Assert.Equal("资源尚未上传完成", item.GetProperty("error").GetProperty("msg").GetString());
    }

    [Fact]
    public async Task 批量访问_空列表与超限整体400()
    {
        HttpClient user = await SignInAsync("owner");

        HttpResponseMessage empty = await user.PostAsJsonAsync("/api/resources/access", Array.Empty<object>());
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("每批资源访问请求须为 1–100 项", await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage oversized = await user.PostAsJsonAsync("/api/resources/access",
            Enumerable.Range(0, 101).Select(index => new { resourceId = $"r{index}", purpose = "display" }));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Contains("每批资源访问请求须为 1–100 项", await oversized.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 批量访问_未登录返回401()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/resources/access",
            new[] { new { resourceId = "whatever", purpose = "display" } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 批量访问_providerInput未配置公网地址逐项报错()
    {
        HttpClient user = await SignInAsync("owner");
        (string id, string _, _) = await CreateResourceAsync(user, new byte[32]);

        JsonElement item = await AccessAsync(user, id, purpose: "provider-input");
        Assert.Equal(400, item.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("服务器本地存储尚未配置服务器访问地址",
            item.GetProperty("error").GetProperty("msg").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 批量访问_providerInput配置公网地址后签发绝对地址()
    {
        HttpClient user = await SignInAsync("owner");
        (string id, string _, _) = await CreateResourceAsync(user, new byte[32]);

        // provider-input 需要模型上游可读的公网 HTTPS 地址；localhost 走私网放行清单。
        Environment.SetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS", "localhost");
        try
        {
            Repository repository = _factory.Services.GetRequiredService<Repository>();
            await repository.SaveSystemSettingAsync(new SystemSetting
            {
                Key = "oss",
                ValueJSON = """{"enabled":true,"provider":"aliyun","publicBaseUrl":"https://localhost"}""",
            });

            JsonElement item = await AccessAsync(user, id, purpose: "provider-input");
            JsonElement access = item.GetProperty("access");
            string url = access.GetProperty("url").GetString()!;
            Assert.StartsWith("https://localhost/api/public/resources/", url, StringComparison.Ordinal);
            Assert.Contains("&variant=original", url, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS", null);
        }
    }
}
