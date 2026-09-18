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
/// 画布工程 CRUD（列表/摘要/分页/upsert/删除）与同步校验（4MB、内嵌媒体、ID 一致性）、
/// 媒体资产守卫与结构化配额的端到端契约测试。
/// 对应 Go: <c>handler/user_data.go</c> canvas-projects 部分与 <c>internal/canvas</c>。
/// </summary>
public sealed class CanvasProjectEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public CanvasProjectEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-proj-{Guid.NewGuid():N}");
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

    /// <summary>PUT 画布（project 包裹，id 必须与路径一致）。</summary>
    private static StringContent CanvasBody(string id, string title, int nodeCount = 2)
    {
        var nodes = new List<object>();
        for (int index = 0; index < nodeCount; index++)
        {
            nodes.Add(new { id = $"n{index}", type = "text", title = $"节点{index}" });
        }
        object payload = new
        {
            id,
            title,
            createdAt = "2026-01-01T00:00:00Z",
            nodes,
        };
        string body = JsonSerializer.Serialize(new { project = payload });
        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 未登录访问画布列表返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/canvas-projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 画布upsert与读取全流程()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 空列表（摘要形态）。
        HttpResponseMessage empty = await admin.GetAsync("/api/canvas-projects");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal(0, (await ReadDataAsync(empty)).GetProperty("projects").GetArrayLength());

        // upsert。
        HttpResponseMessage saved = await admin.PutAsync("/api/canvas-projects/canvas-1", CanvasBody("canvas-1", "我的画布"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        JsonElement project = (await ReadDataAsync(saved)).GetProperty("project");
        Assert.Equal("canvas-1", project.GetProperty("id").GetString());
        Assert.Equal("我的画布", project.GetProperty("title").GetString());

        // 单读返回原始 payload。
        HttpResponseMessage detail = await admin.GetAsync("/api/canvas-projects/canvas-1");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        JsonElement detailProject = (await ReadDataAsync(detail)).GetProperty("project");
        Assert.Equal(2, detailProject.GetProperty("nodes").GetArrayLength());

        // 摘要列表。
        HttpResponseMessage summaries = await admin.GetAsync("/api/canvas-projects");
        JsonElement summaryList = (await ReadDataAsync(summaries)).GetProperty("projects");
        Assert.Equal(1, summaryList.GetArrayLength());
        Assert.Equal("我的画布", summaryList[0].GetProperty("title").GetString());

        // 分页形态（nodes 预览只挑 image/video 节点，text 节点不计）。
        HttpResponseMessage page = await admin.GetAsync("/api/canvas-projects?page=1&pageSize=40");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        JsonElement pageData = await ReadDataAsync(page);
        Assert.Equal(1, pageData.GetProperty("total").GetInt64());
        Assert.False(pageData.GetProperty("hasMore").GetBoolean());
        Assert.Equal(2, pageData.GetProperty("projects")[0].GetProperty("nodeCount").GetInt32());
        Assert.Equal(0, pageData.GetProperty("projects")[0].GetProperty("previewNodes").GetArrayLength());

        // 覆盖保存（同 ID 更新标题）。
        HttpResponseMessage updated = await admin.PutAsync(
            "/api/canvas-projects/canvas-1", CanvasBody("canvas-1", "改名画布"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(
            "改名画布",
            (await ReadDataAsync(updated)).GetProperty("project").GetProperty("title").GetString());
    }

    [Fact]
    public async Task 画布upsert_ID与路径不一致返回_400()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.PutAsync(
            "/api/canvas-projects/canvas-path", CanvasBody("canvas-other", "不一致"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "画布 ID 与请求路径不一致",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 画布upsert_拒绝内嵌媒体DataUrl()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        object payload = new
        {
            id = "canvas-data-url",
            title = "内嵌媒体",
            nodes = new[] { new { id = "n1", type = "image", content = "data:image/png;base64,AAAA" } },
        };
        string body = JsonSerializer.Serialize(new { project = payload });

        HttpResponseMessage response = await admin.PutAsync("/api/canvas-projects/canvas-data-url", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "画布数据包含内嵌媒体，请先上传到资源存储",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 画布upsert_未入库的媒体引用被守卫拒绝()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        object payload = new
        {
            id = "canvas-guard",
            title = "媒体守卫",
            nodes = new[]
            {
                new
                {
                    id = "n1",
                    type = "image",
                    metadata = new { assetId = "ASSET_1", storageKey = "resource:RES_1" },
                },
            },
        };
        string body = JsonSerializer.Serialize(new { project = payload });

        HttpResponseMessage response = await admin.PutAsync("/api/canvas-projects/canvas-guard", new StringContent(body, Encoding.UTF8, "application/json"));

        // 资源 RES_1 不存在（或未就绪）→ 拒绝同步。
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body_ = await response.Content.ReadAsStringAsync();
        Assert.True(
            body_.Contains("画布媒体对应的云端资源不存在", StringComparison.Ordinal) ||
            body_.Contains("画布媒体尚未进入素材库", StringComparison.Ordinal),
            body_);
    }

    [Fact]
    public async Task 删除画布后读取返回_404()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await admin.PutAsync("/api/canvas-projects/canvas-del", CanvasBody("canvas-del", "待删除"));

        HttpResponseMessage deleted = await admin.DeleteAsync("/api/canvas-projects/canvas-del");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal("canvas-del", (await ReadDataAsync(deleted)).GetProperty("id").GetString());

        HttpResponseMessage detail = await admin.GetAsync("/api/canvas-projects/canvas-del");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact]
    public async Task 分页_标题搜索与独立画布过滤()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await admin.PutAsync("/api/canvas-projects/canvas-a", CanvasBody("canvas-a", "搜索目标"));
        await admin.PutAsync("/api/canvas-projects/canvas-b", CanvasBody("canvas-b", "其他画布"));

        HttpResponseMessage search = await admin.GetAsync("/api/canvas-projects?page=1&pageSize=40&q=搜索");
        JsonElement searchData = await ReadDataAsync(search);
        Assert.Equal(1, searchData.GetProperty("total").GetInt64());
        Assert.Equal("搜索目标", searchData.GetProperty("projects")[0].GetProperty("title").GetString());

        HttpResponseMessage independent = await admin.GetAsync(
            "/api/canvas-projects?page=1&pageSize=40&projectId=independent");
        Assert.Equal(2, (await ReadDataAsync(independent)).GetProperty("total").GetInt64());
    }
}
