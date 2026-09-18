#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 素材库路由（batch/分页/facets/upsert/快照/分类/移动）的端到端契约测试。
/// 对应 Go: <c>handler/user_data.go</c> 素材部分与 <c>app/asset_library.go</c>。
/// DELETE /assets/:id 的资源级联清理属独立节点，暂未接路由（见 CHECKLIST）。
/// </summary>
public sealed class AssetLibraryEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public AssetLibraryEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-assets-{Guid.NewGuid():N}");
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

    private static StringContent AssetBody(string id, string title, string folderId = "")
    {
        var data = new Dictionary<string, object?>
        {
            ["dataUrl"] = "/api/resources/res-1/file",
            ["width"] = 1024,
            ["height"] = 768,
            ["bytes"] = 12345,
            ["mimeType"] = "image/png",
        };
        var asset = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["title"] = title,
            ["kind"] = "image",
            ["category"] = "prop",
            ["coverUrl"] = "/api/resources/res-1/file",
            ["tags"] = new[] { "tag1" },
            ["data"] = data,
        };
        if (folderId.Length > 0)
        {
            asset["folderId"] = folderId;
        }
        string body = JsonSerializer.Serialize(new { asset });
        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 素材upsert补全契约与批量读取()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage saved = await admin.PutAsync("/api/assets/asset-1", AssetBody("asset-1", "测试素材"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        JsonElement summary = (await ReadDataAsync(saved)).GetProperty("asset");
        Assert.Equal("asset-1", summary.GetProperty("id").GetString());
        // 空 status 归一为 confirmed。
        Assert.Equal("confirmed", summary.GetProperty("status").GetString());

        // 单读补齐 tags/coverUrl/时间戳（upsert 已带，此处验证 data 尺寸回填路径不报错）。
        HttpResponseMessage detail = await admin.GetAsync("/api/assets/asset-1");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        JsonElement payload = (await ReadDataAsync(detail)).GetProperty("asset");
        Assert.Equal("image/png", payload.GetProperty("data").GetProperty("mimeType").GetString());
        Assert.Equal(1024, payload.GetProperty("data").GetProperty("width").GetInt32());

        // 批量读取。
        HttpResponseMessage batch = await admin.PostAsJsonAsync(
            "/api/assets/batch", new { ids = new[] { "asset-1" } });
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        Assert.Equal(1, (await ReadDataAsync(batch)).GetProperty("assets").GetArrayLength());
    }

    [Fact]
    public async Task 素材upsert校验_缺字段与未知类型()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage noCover = await admin.PutAsync(
            "/api/assets/asset-x",
            new StringContent(
                """{"asset":{"id":"asset-x","title":"t","kind":"image","data":{"width":1,"height":1,"bytes":1,"mimeType":"image/png"}}}""",
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, noCover.StatusCode);
        Assert.Contains("素材缺少 coverUrl 字段", await noCover.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage badKind = await admin.PutAsync(
            "/api/assets/asset-x",
            new StringContent(
                """{"asset":{"id":"asset-x","title":"t","kind":"zip","coverUrl":"u","data":{}}}""",
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
        Assert.Contains("不支持的素材类型", await badKind.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 素材分页_facets_与搜索()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage folder = await admin.PostAsJsonAsync(
            "/api/asset-folders", new { name = "角色立绘" });
        Assert.Equal(HttpStatusCode.OK, folder.StatusCode);
        string folderId = (await ReadDataAsync(folder)).GetProperty("folder").GetProperty("id").GetString()!;

        HttpResponseMessage saved = await admin.PutAsync(
            "/api/assets/asset-a", AssetBody("asset-a", "搜索目标", folderId));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        await admin.PutAsync("/api/assets/asset-b", AssetBody("asset-b", "另一个"));

        // 分页 + kind 过滤。
        HttpResponseMessage page = await admin.GetAsync("/api/assets?page=1&pageSize=40&kind=image");
        JsonElement pageData = await ReadDataAsync(page);
        Assert.Equal(2, pageData.GetProperty("total").GetInt64());
        Assert.Equal(2, pageData.GetProperty("kindCounts").GetProperty("image").GetInt64());

        // 搜索命中标题。
        HttpResponseMessage q = await admin.GetAsync("/api/assets?page=1&pageSize=40&q=搜索");
        Assert.Equal(1, (await ReadDataAsync(q)).GetProperty("total").GetInt64());

        // folderId 过滤。
        HttpResponseMessage byFolder = await admin.GetAsync($"/api/assets?page=1&pageSize=40&folderId={folderId}");
        Console.WriteLine("FOLDER_DEBUG=" + await byFolder.Content.ReadAsStringAsync());
        Assert.Equal(1, (await ReadDataAsync(byFolder)).GetProperty("total").GetInt64());

        // 未分页 → 摘要形态。
        HttpResponseMessage summaries = await admin.GetAsync("/api/assets");
        Assert.Equal(2, (await ReadDataAsync(summaries)).GetProperty("assets").GetArrayLength());
    }

    [Fact]
    public async Task 分类CRUD_重名拒绝_删除时素材移出()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage created = await admin.PostAsJsonAsync(
            "/api/asset-folders", new { name = "场景" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement folder = (await ReadDataAsync(created)).GetProperty("folder");
        string folderId = folder.GetProperty("id").GetString()!;
        Assert.Equal(0, folder.GetProperty("position").GetInt64());

        // 重名 → 400。
        HttpResponseMessage duplicate = await admin.PostAsJsonAsync(
            "/api/asset-folders", new { name = "场景" });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Contains("已存在同名素材分类", await duplicate.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 放一个素材进去再删分类 → 素材移出（folderId 清空）。
        await admin.PutAsync("/api/assets/asset-move", AssetBody("asset-move", "待移动", folderId));
        HttpResponseMessage removed = await admin.DeleteAsync($"/api/asset-folders/{folderId}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        HttpResponseMessage detail = await admin.GetAsync("/api/assets/asset-move");
        JsonElement payload = (await ReadDataAsync(detail)).GetProperty("asset");
        Assert.False(payload.TryGetProperty("folderId", out _) &&
                     payload.GetProperty("folderId").GetString() != "");

        // 删除不存在的分类 → 400。
        HttpResponseMessage missing = await admin.DeleteAsync("/api/asset-folders/folder-nope");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("素材分类不存在", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 批量移动素材到分类()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage folder = await admin.PostAsJsonAsync(
            "/api/asset-folders", new { name = "目标分类" });
        string folderId = (await ReadDataAsync(folder)).GetProperty("folder").GetProperty("id").GetString()!;

        await admin.PutAsync("/api/assets/asset-m1", AssetBody("asset-m1", "素材1"));
        await admin.PutAsync("/api/assets/asset-m2", AssetBody("asset-m2", "素材2"));

        HttpResponseMessage moved = await admin.PatchAsJsonAsync(
            "/api/assets/folder",
            new { assetIds = new[] { "asset-m1", "asset-m2" }, folderId });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        JsonElement result = await ReadDataAsync(moved);
        Assert.Equal(2, result.GetProperty("assetIds").GetArrayLength());
        Assert.Equal(folderId, result.GetProperty("folderId").GetString());

        // payload.folderId 已回写。
        HttpResponseMessage detail = await admin.GetAsync("/api/assets/asset-m1");
        JsonElement payload = (await ReadDataAsync(detail)).GetProperty("asset");
        Assert.Equal(folderId, payload.GetProperty("folderId").GetString());
    }

    [Fact]
    public async Task 快照返回素材与画布()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await admin.PutAsync("/api/canvas-projects/canvas-snap", CanvasBody("canvas-snap", "快照画布"));

        HttpResponseMessage snapshot = await admin.GetAsync("/api/user-data/snapshot");
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        JsonElement data = await ReadDataAsync(snapshot);
        Assert.Equal(1, data.GetProperty("projects").GetArrayLength());
        Assert.Equal(0, data.GetProperty("assets").GetArrayLength());
    }

    private static StringContent CanvasBody(string id, string title)
    {
        object payload = new
        {
            id,
            title,
            nodes = new[] { new { id = "n1", type = "text" } },
        };
        string body = JsonSerializer.Serialize(new { project = payload });
        return new StringContent(body, Encoding.UTF8, "application/json");
    }
}
