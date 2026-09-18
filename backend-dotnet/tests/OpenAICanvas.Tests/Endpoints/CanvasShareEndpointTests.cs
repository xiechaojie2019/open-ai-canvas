#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 画布分享路由（创建/状态/撤销/公开投影脱敏/公开资源放行）的端到端契约测试。
/// 对应 Go: <c>handler/canvas_share.go</c> 与 <c>internal/canvas/canvas_share.go</c>。
/// </summary>
public sealed class CanvasShareEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public CanvasShareEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-share-{Guid.NewGuid():N}");
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

    private async Task<string> CreateCanvasWithMediaAsync()
    {
        HttpClient admin = await SignInAsAdminAsync();

        // 媒体守卫要求 storageKey 指向已就绪资源：先落资源记录与本地文件。
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            OpenAICanvas.Persistence.Repositories.Repository repository =
                scope.ServiceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>();
            OpenAICanvas.Domain.Entities.User? owner = await repository.UserByUsernameAsync("admin");
            Assert.NotNull(owner);
            await repository.CreateAsync(new OpenAICanvas.Domain.Entities.Resource
            {
                ID = "RES_SHARE",
                UserID = owner.ID,
                Kind = "image",
                Status = "ready",
                Provider = "local",
                ObjectKey = "share/ok.png",
                MimeType = "image/png",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            string dir = Path.Combine(_dataDir, "resources", "share");
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, "ok.png"), new byte[] { 137, 80, 78, 71 });
        }

        // 守卫要求媒体节点 assetId 指向素材库记录，且素材 payload 引用同一资源。
        HttpResponseMessage assetSaved = await admin.PutAsync(
            "/api/assets/asset-share",
            new StringContent(
                """
                {"asset":{"id":"asset-share","title":"分享图","kind":"image","category":"other","coverUrl":"/api/resources/RES_SHARE/file","tags":[],"data":{"storageKey":"resource:RES_SHARE","width":10,"height":10,"bytes":4,"mimeType":"image/png"}}}
                """,
                Encoding.UTF8,
                "application/json"));
        assetSaved.EnsureSuccessStatusCode();

        HttpResponseMessage saved = await admin.PutAsync(
            "/api/canvas-projects/canvas-share-1",
            new StringContent(
                """
                {"project":{"id":"canvas-share-1","title":"分享画布","createdAt":"2026-01-01T00:00:00Z","nodes":[
                  {"id":"n1","type":"text","title":"说明","metadata":{"prompt":"内部提示词","taskId":"task-secret"}},
                  {"id":"n2","type":"image","title":"图片","position":{"x":1,"y":2},"width":100,"height":50,
                   "metadata":{"assetId":"asset-share","storageKey":"resource:RES_SHARE","mimeType":"image/png","apiKey":"sk-secret"}}
                ]}}
                """,
                Encoding.UTF8,
                "application/json"));
        Console.WriteLine("CANVAS_PUT_DEBUG=" + (saved.IsSuccessStatusCode ? "ok" : await saved.Content.ReadAsStringAsync()));
        saved.EnsureSuccessStatusCode();
        return "canvas-share-1";
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 分享生命周期_创建_读取_轮换_撤销()
    {
        string canvasId = await CreateCanvasWithMediaAsync();
        using HttpClient admin = await SignInAsAdminAsync();

        // 初始状态：未开启。
        HttpResponseMessage status = await admin.GetAsync($"/api/canvas-projects/{canvasId}/share");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.False((await ReadDataAsync(status)).GetProperty("share").GetProperty("enabled").GetBoolean());

        // 创建。
        HttpResponseMessage created = await admin.PostAsJsonAsync(
            $"/api/canvas-projects/{canvasId}/share", new { expiresDays = 7 });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement share = (await ReadDataAsync(created)).GetProperty("share");
        Assert.True(share.GetProperty("enabled").GetBoolean());
        string token = share.GetProperty("token").GetString()!;
        Assert.True(token.Length >= 32);
        Assert.NotNull(share.GetProperty("expiresAt"));

        // 状态回读。
        HttpResponseMessage reread = await admin.GetAsync($"/api/canvas-projects/{canvasId}/share");
        Assert.Equal(
            token,
            (await ReadDataAsync(reread)).GetProperty("share").GetProperty("token").GetString());

        // 撤销。
        HttpResponseMessage deleted = await admin.DeleteAsync($"/api/canvas-projects/{canvasId}/share");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        HttpResponseMessage after = await admin.GetAsync($"/api/canvas-projects/{canvasId}/share");
        Assert.False((await ReadDataAsync(after)).GetProperty("share").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task 分享创建校验_有效期越界()
    {
        string canvasId = await CreateCanvasWithMediaAsync();
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage invalid = await admin.PostAsJsonAsync(
            $"/api/canvas-projects/{canvasId}/share", new { expiresDays = 366 });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains(
            "分享有效期必须在 0 到 365 天之间",
            await invalid.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 公开投影_脱敏与媒体重写()
    {
        string canvasId = await CreateCanvasWithMediaAsync();
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage created = await admin.PostAsJsonAsync(
            $"/api/canvas-projects/{canvasId}/share", new { expiresDays = 0 });
        string token = (await ReadDataAsync(created)).GetProperty("share").GetProperty("token").GetString()!;

        // 无需登录即可访问公开投影。
        HttpResponseMessage publicShare = await _client.GetAsync($"/api/public/canvas-shares/{token}");
        Assert.Equal(HttpStatusCode.OK, publicShare.StatusCode);
        Assert.True(publicShare.Headers.CacheControl?.NoStore == true);
        string body = await publicShare.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement project = document.RootElement.GetProperty("data").GetProperty("project");
        Assert.Equal("分享画布", project.GetProperty("title").GetString());
        Assert.Equal(2, project.GetProperty("nodes").GetArrayLength());

        JsonElement textNode = project.GetProperty("nodes")[0];
        // 禁用键被剥离（prompt 是白名单键会保留；taskId 必须剥离）。
        Assert.False(textNode.GetProperty("metadata").TryGetProperty("taskId", out _));
        Assert.True(textNode.GetProperty("metadata").TryGetProperty("prompt", out _));

        JsonElement imageNode = project.GetProperty("nodes")[1];
        // 禁用键 apiKey 剥离；storageKey 不出现；content 重写为代理 URL。
        Assert.False(imageNode.GetProperty("metadata").TryGetProperty("apiKey", out _));
        Assert.False(imageNode.GetProperty("metadata").TryGetProperty("storageKey", out _));
        Assert.Equal(
            $"/api/public/canvas-shares/{token}/resources/RES_SHARE/file",
            imageNode.GetProperty("metadata").GetProperty("content").GetString());
    }

    [Fact]
    public async Task 公开资源_令牌放行与无令牌拒绝()
    {
        string canvasId = await CreateCanvasWithMediaAsync();
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage created = await admin.PostAsJsonAsync(
            $"/api/canvas-projects/{canvasId}/share", new { });
        string token = (await ReadDataAsync(created)).GetProperty("share").GetProperty("token").GetString()!;

        HttpResponseMessage file = await _client.GetAsync(
            $"/api/public/canvas-shares/{token}/resources/RES_SHARE/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/png", file.Content.Headers.ContentType?.MediaType);

        // 不在放行清单的资源 → 404。
        HttpResponseMessage blocked = await _client.GetAsync(
            $"/api/public/canvas-shares/{token}/resources/RES_OTHER/file");
        Assert.Equal(HttpStatusCode.NotFound, blocked.StatusCode);

        // 无效令牌 → 404。
        HttpResponseMessage badToken = await _client.GetAsync("/api/public/canvas-shares/not-a-real-token/resources/x/file");
        Assert.Equal(HttpStatusCode.NotFound, badToken.StatusCode);
    }

    [Fact]
    public async Task 撤销后公开链接失效()
    {
        string canvasId = await CreateCanvasWithMediaAsync();
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage created = await admin.PostAsJsonAsync(
            $"/api/canvas-projects/{canvasId}/share", new { });
        string token = (await ReadDataAsync(created)).GetProperty("share").GetProperty("token").GetString()!;

        await admin.DeleteAsync($"/api/canvas-projects/{canvasId}/share");

        HttpResponseMessage publicShare = await _client.GetAsync($"/api/public/canvas-shares/{token}");
        Assert.Equal(HttpStatusCode.NotFound, publicShare.StatusCode);
        Assert.Contains("分享链接无效或已失效", await publicShare.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
