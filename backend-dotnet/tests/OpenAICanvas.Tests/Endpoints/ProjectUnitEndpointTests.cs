#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 项目单元与画布链接路由的端到端契约测试。
/// 对应 Go: <c>handler/project.go</c> 的单元/链接部分与 <c>app/project.go</c>。
/// </summary>
public sealed class ProjectUnitEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public ProjectUnitEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-unit-{Guid.NewGuid():N}");
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
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        _adminClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _adminClient.DefaultRequestHeaders.Add("Cookie", cookie);
        return _adminClient;
    }

    private async Task<string> CreateProjectAsync(HttpClient admin, string name = "短剧项目")
    {
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/projects", new { name });
        created.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("project").GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 单元CRUD_含字数统计与摘要()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string projectId = await CreateProjectAsync(admin);

        // 创建（HTML 正文按去标签后的字符数统计）。
        HttpResponseMessage created = await admin.PostAsJsonAsync($"/api/projects/{projectId}/units", new
        {
            title = "第一集",
            sourceText = "<p>你好世界</p>",
            position = 0,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement unit = (await ReadDataAsync(created)).GetProperty("unit");
        string unitId = unit.GetProperty("id").GetString()!;
        Assert.Equal("chapter", unit.GetProperty("kind").GetString());
        Assert.Equal("draft", unit.GetProperty("status").GetString());
        Assert.Equal(4, unit.GetProperty("wordCount").GetInt64());

        // 摘要列表。
        HttpResponseMessage summaries = await admin.GetAsync($"/api/projects/{projectId}/units");
        Assert.Equal(HttpStatusCode.OK, summaries.StatusCode);
        JsonElement summaryData = await ReadDataAsync(summaries);
        Assert.Equal(1, summaryData.GetProperty("units").GetArrayLength());
        Assert.True(summaryData.TryGetProperty("canvasCounts", out _));

        // 单读含正文。
        HttpResponseMessage detail = await admin.GetAsync($"/api/projects/{projectId}/units/{unitId}");
        JsonElement detailUnit = (await ReadDataAsync(detail)).GetProperty("unit");
        Assert.Equal("<p>你好世界</p>", detailUnit.GetProperty("sourceText").GetString());

        // 更新（状态与正文）。
        HttpResponseMessage updated = await admin.PatchAsJsonAsync($"/api/projects/{projectId}/units/{unitId}", new
        {
            title = "第一集改名",
            sourceText = "新正文",
            status = "ready",
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement updatedUnit = (await ReadDataAsync(updated)).GetProperty("unit");
        Assert.Equal("第一集改名", updatedUnit.GetProperty("title").GetString());
        Assert.Equal("ready", updatedUnit.GetProperty("status").GetString());
        Assert.Equal(3, updatedUnit.GetProperty("wordCount").GetInt64());

        // 删除。
        HttpResponseMessage deleted = await admin.DeleteAsync($"/api/projects/{projectId}/units/{unitId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(0, (await ReadDataAsync(await admin.GetAsync($"/api/projects/{projectId}/units")))
            .GetProperty("units").GetArrayLength());
    }

    [Fact]
    public async Task 单元校验_类型与标题与状态()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string projectId = await CreateProjectAsync(admin);

        HttpResponseMessage badKind = await admin.PostAsJsonAsync($"/api/projects/{projectId}/units", new
        {
            kind = "scene",
            title = "x",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
        Assert.Contains("不支持的项目单元类型", await badKind.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage emptyTitle = await admin.PostAsJsonAsync($"/api/projects/{projectId}/units", new
        {
            title = "  ",
        });
        Assert.Equal(HttpStatusCode.BadRequest, emptyTitle.StatusCode);
        Assert.Contains("章节标题不能为空", await emptyTitle.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage created = await admin.PostAsJsonAsync($"/api/projects/{projectId}/units", new
        {
            title = "合法章节",
        });
        string unitId = (await ReadDataAsync(created)).GetProperty("unit").GetProperty("id").GetString()!;

        HttpResponseMessage badStatus = await admin.PatchAsJsonAsync($"/api/projects/{projectId}/units/{unitId}", new
        {
            title = "x",
            sourceText = "",
            status = "archived",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.Contains("不支持的章节状态", await badStatus.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 批量导入_数量校验与正文剥离()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string projectId = await CreateProjectAsync(admin);

        // 空导入 → 400。
        HttpResponseMessage empty = await admin.PostAsJsonAsync($"/api/projects/{projectId}/units/import", new
        {
            units = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains(
            "一次导入的章节数量必须在 1 到 2500 之间",
            await empty.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // 正常导入：响应剥离正文。
        HttpResponseMessage imported = await admin.PostAsJsonAsync($"/api/projects/{projectId}/units/import", new
        {
            units = new object[]
            {
                new { title = "第1章", sourceText = "正文一" },
                new { title = "第2章", sourceText = "正文二" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        JsonElement units = (await ReadDataAsync(imported)).GetProperty("units");
        Assert.Equal(2, units.GetArrayLength());
        Assert.Equal("", units[0].GetProperty("sourceText").GetString());

        // 位置按已有数量续排。
        JsonElement summaries = await ReadDataAsync(await admin.GetAsync($"/api/projects/{projectId}/units"));
        Assert.Equal(2, summaries.GetProperty("units").GetArrayLength());
        Assert.Equal(0, summaries.GetProperty("units")[0].GetProperty("position").GetInt64());
        Assert.Equal(1, summaries.GetProperty("units")[1].GetProperty("position").GetInt64());
    }

    [Fact]
    public async Task 重排_完整性与有效性校验()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string projectId = await CreateProjectAsync(admin);
        HttpResponseMessage first = await admin.PostAsJsonAsync($"/api/projects/{projectId}/units", new { title = "A" });
        HttpResponseMessage second = await admin.PostAsJsonAsync($"/api/projects/{projectId}/units", new { title = "B" });
        string firstId = (await ReadDataAsync(first)).GetProperty("unit").GetProperty("id").GetString()!;
        string secondId = (await ReadDataAsync(second)).GetProperty("unit").GetProperty("id").GetString()!;

        // 列表不完整 → 400。
        HttpResponseMessage incomplete = await admin.PatchAsJsonAsync(
            $"/api/projects/{projectId}/units/reorder", new { unitIds = new[] { firstId } });
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        Assert.Contains("章节排序列表不完整", await incomplete.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 无效 ID → 400。
        HttpResponseMessage invalid = await admin.PatchAsJsonAsync(
            $"/api/projects/{projectId}/units/reorder", new { unitIds = new[] { firstId, "UNIT_NOPE" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("章节排序包含无效章节", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 正常交换。
        HttpResponseMessage reordered = await admin.PatchAsJsonAsync(
            $"/api/projects/{projectId}/units/reorder", new { unitIds = new[] { secondId, firstId } });
        Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);

        JsonElement summaries = await ReadDataAsync(await admin.GetAsync($"/api/projects/{projectId}/units"));
        Assert.Equal("B", summaries.GetProperty("units")[0].GetProperty("title").GetString());
        Assert.Equal("A", summaries.GetProperty("units")[1].GetProperty("title").GetString());
    }

    [Fact]
    public async Task 画布链接_归属与解绑()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string projectId = await CreateProjectAsync(admin);
        HttpResponseMessage created = await admin.PostAsJsonAsync($"/api/projects/{projectId}/units", new { title = "第一集" });
        string unitId = (await ReadDataAsync(created)).GetProperty("unit").GetProperty("id").GetString()!;

        // 建画布。
        HttpResponseMessage canvas = await admin.PutAsync(
            "/api/canvas-projects/canvas-link-1",
            new StringContent(
                """{"project":{"id":"canvas-link-1","title":"画布","nodes":[]}}""",
                System.Text.Encoding.UTF8,
                "application/json"));
        canvas.EnsureSuccessStatusCode();

        // 链接。
        HttpResponseMessage linked = await admin.PostAsJsonAsync($"/api/projects/{projectId}/canvas-links", new
        {
            canvasId = "canvas-link-1",
            unitId,
            role = "primary",
        });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        JsonElement link = (await ReadDataAsync(linked)).GetProperty("link");
        Assert.Equal("canvas-link-1", link.GetProperty("canvasId").GetString());
        Assert.Equal(unitId, link.GetProperty("unitId").GetString());

        // 摘要含画布计数。
        JsonElement summaries = await ReadDataAsync(await admin.GetAsync($"/api/projects/{projectId}/units"));
        Assert.Equal(1, summaries.GetProperty("canvasCounts").GetProperty(unitId).GetInt64());

        // 空参数 → 400。
        HttpResponseMessage empty = await admin.PostAsJsonAsync($"/api/projects/{projectId}/canvas-links", new
        {
            canvasId = "",
            unitId = "",
        });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("画布和章节不能为空", await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 解绑。
        HttpResponseMessage unlinked = await admin.DeleteAsync(
            $"/api/projects/{projectId}/canvas-links/canvas-link-1/units/{unitId}");
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        JsonElement after = await ReadDataAsync(await admin.GetAsync($"/api/projects/{projectId}/units"));
        Assert.Equal(0, after.GetProperty("canvasCounts").EnumerateObject().Count());
    }

    [Fact]
    public async Task 项目不存在时单元接口返回_404()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/projects/PROJ_NOPE/units");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}