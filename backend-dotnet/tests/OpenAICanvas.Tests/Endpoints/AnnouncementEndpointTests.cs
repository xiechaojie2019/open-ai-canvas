#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 公告路由（feed/已读/管理 CRUD/关闭/配图下发/草稿丢弃）的端到端契约测试。
/// 对应 Go: <c>handler/announcement.go</c> 与 <c>app/announcement.go</c>。
/// </summary>
public sealed class AnnouncementEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;
    private string _userId = "";

    public AnnouncementEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-ann-{Guid.NewGuid():N}");
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
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        _userId = (await repository.UserByUsernameAsync("admin"))!.ID;
        return _adminClient;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 未登录访问公告返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/announcements");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 公告生命周期_创建_列表_已读_关闭()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/announcements", new
        {
            title = "系统维护通知",
            content = "今晚 22:00 维护",
            level = "warning",
            pinned = true,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement announcement = (await ReadDataAsync(created)).GetProperty("announcement");
        string id = announcement.GetProperty("id").GetString()!;
        Assert.Equal("active", announcement.GetProperty("status").GetString());
        Assert.True(announcement.GetProperty("pinned").GetBoolean());

        // 用户 feed：1 条 + 未读 1。
        HttpResponseMessage feed = await admin.GetAsync("/api/announcements");
        JsonElement feedData = await ReadDataAsync(feed);
        Assert.Equal(1, feedData.GetProperty("announcements").GetArrayLength());
        Assert.Equal(1, feedData.GetProperty("unreadCount").GetInt64());

        // 标记已读 → 未读归零。
        HttpResponseMessage read = await admin.PostAsJsonAsync(
            "/api/announcements/read", new { announcementIds = new[] { id } });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(0, (await ReadDataAsync(read)).GetProperty("unreadCount").GetInt64());

        // 管理分页。
        HttpResponseMessage page = await admin.GetAsync("/api/admin/announcements?page=1&pageSize=20");
        JsonElement pageData = await ReadDataAsync(page);
        Assert.Equal(1, pageData.GetProperty("total").GetInt64());
        Assert.Equal(20, pageData.GetProperty("pageSize").GetInt64());

        // 关闭 → feed 为空。
        HttpResponseMessage closed = await admin.PostAsJsonAsync($"/api/admin/announcements/{id}/close", new { });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal("closed", (await ReadDataAsync(closed)).GetProperty("announcement").GetProperty("status").GetString());

        HttpResponseMessage feedAfter = await admin.GetAsync("/api/announcements");
        Assert.Equal(0, (await ReadDataAsync(feedAfter)).GetProperty("announcements").GetArrayLength());

        // 重复关闭 → 400。
        HttpResponseMessage again = await admin.PostAsJsonAsync($"/api/admin/announcements/{id}/close", new { });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("公告已经关闭", await again.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 公告校验_标题与级别()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage emptyTitle = await admin.PostAsJsonAsync("/api/admin/announcements", new
        {
            title = "  ",
            content = "x",
            level = "info",
        });
        Assert.Equal(HttpStatusCode.BadRequest, emptyTitle.StatusCode);
        Assert.Contains("请填写公告标题", await emptyTitle.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage badLevel = await admin.PostAsJsonAsync("/api/admin/announcements", new
        {
            title = "标题",
            content = "正文",
            level = "urgent",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badLevel.StatusCode);
        Assert.Contains("公告级别无效", await badLevel.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 公告更新_重置已读并保留状态()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/announcements", new
        {
            title = "初版",
            content = "内容",
            level = "info",
        });
        string id = (await ReadDataAsync(created)).GetProperty("announcement").GetProperty("id").GetString()!;
        await admin.PostAsJsonAsync("/api/announcements/read", new { announcementIds = new[] { id } });
        Assert.Equal(0, (await ReadDataAsync(await admin.GetAsync("/api/announcements"))).GetProperty("unreadCount").GetInt64());

        // 更新后已读记录被清空（未读恢复）。
        HttpResponseMessage updated = await admin.PatchAsJsonAsync($"/api/admin/announcements/{id}", new
        {
            title = "改版",
            content = "新内容",
            level = "critical",
            pinned = false,
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("改版", (await ReadDataAsync(updated)).GetProperty("announcement").GetProperty("title").GetString());
        Assert.Equal(1, (await ReadDataAsync(await admin.GetAsync("/api/announcements"))).GetProperty("unreadCount").GetInt64());
    }

    [Fact]
    public async Task 配图草稿_校验与丢弃()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 未登记的草稿 → 400。
        HttpResponseMessage invalid = await admin.PostAsJsonAsync("/api/admin/announcements", new
        {
            title = "带图公告",
            content = "内容",
            level = "info",
            imageResourceId = "RES_NOPE",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains(
            "公告配图草稿不存在或不属于当前管理员",
            await invalid.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // 登记草稿 + 资源后可创建。
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            DateTime now = DateTime.UtcNow;
            await repository.CreateAsync(new Resource
            {
                ID = "RES_ANN",
                UserID = _userId,
                Kind = "image",
                Status = "ready",
                Provider = "local",
                ObjectKey = "ann/ok.png",
                MimeType = "image/png",
                CreatedAt = now,
                UpdatedAt = now,
            });
            await repository.CreateAnnouncementImageDraftAsync(new AnnouncementImageDraft
            {
                ResourceID = "RES_ANN",
                UserID = _userId,
                CreatedAt = now,
            });
        }
        string dir = Path.Combine(_dataDir, "resources", "ann");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "ok.png"), [137, 80, 78, 71]);

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/announcements", new
        {
            title = "带图公告",
            content = "内容",
            level = "info",
            imageResourceId = "RES_ANN",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement announcement = (await ReadDataAsync(created)).GetProperty("announcement");
        Assert.Equal(
            $"/api/announcements/{announcement.GetProperty("id").GetString()}/image",
            announcement.GetProperty("imageUrl").GetString());

        // 配图下发。
        HttpResponseMessage image = await admin.GetAsync(announcement.GetProperty("imageUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);

        // 丢弃未使用的草稿。
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            DateTime now = DateTime.UtcNow;
            await repository.CreateAsync(new Resource
            {
                ID = "RES_DRAFT",
                UserID = _userId,
                Kind = "image",
                Status = "ready",
                Provider = "local",
                ObjectKey = "ann/draft.png",
                MimeType = "image/png",
                CreatedAt = now,
                UpdatedAt = now,
            });
            await repository.CreateAnnouncementImageDraftAsync(new AnnouncementImageDraft
            {
                ResourceID = "RES_DRAFT",
                UserID = _userId,
                CreatedAt = now,
            });
        }
        HttpResponseMessage discarded = await admin.DeleteAsync("/api/admin/announcement-images/RES_DRAFT");
        Assert.Equal(HttpStatusCode.OK, discarded.StatusCode);

        // 丢弃后草稿不存在 → 404。
        HttpResponseMessage again = await admin.DeleteAsync("/api/admin/announcement-images/RES_DRAFT");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }
}