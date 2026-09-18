#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 工作台读视图（core/overview）路由的端到端契约测试。
/// 对应 Go: <c>app/project_workbench_read.go</c> 的 ProjectCore / ProjectOverview。
/// </summary>
public sealed class ProjectWorkbenchEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    public ProjectWorkbenchEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-wb-{Guid.NewGuid():N}");
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

    private async Task<HttpClient> SignInAsync()
    {
        if (_userClient is not null)
        {
            return _userClient;
        }
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "watcher",
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

    [Fact]
    public async Task 项目核心与总览读视图()
    {
        HttpClient user = await SignInAsync();
        HttpResponseMessage projectCreated = await user.PostAsJsonAsync(
            "/api/projects", new { name = "总览项目" });
        projectCreated.EnsureSuccessStatusCode();
        using JsonDocument projectDoc = JsonDocument.Parse(await projectCreated.Content.ReadAsStringAsync());
        string projectId = projectDoc.RootElement.GetProperty("data").GetProperty("project").GetProperty("id").GetString()!;

        // 一个章节 + 两个字 + 一个镜头。
        HttpResponseMessage unitCreated = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/units", new { title = "第一集", sourceText = "你好世界", position = 0 });
        unitCreated.EnsureSuccessStatusCode();
        string unitId = (await ReadDataAsync(unitCreated)).GetProperty("unit").GetProperty("id").GetString()!;
        HttpResponseMessage shotCreated = await user.PostAsJsonAsync($"/api/projects/{projectId}/shots", new
        {
            unitId,
            title = "镜头一",
            description = "夜",
            durationMs = 1000L,
        });
        Assert.Equal(HttpStatusCode.OK, shotCreated.StatusCode);

        // core：返回项目本体。
        HttpResponseMessage core = await user.GetAsync($"/api/projects/{projectId}/core");
        Assert.Equal(HttpStatusCode.OK, core.StatusCode);
        JsonElement coreData = await ReadDataAsync(core);
        Assert.Equal("总览项目", coreData.GetProperty("project").GetProperty("name").GetString());

        // overview：指标与单元行。
        HttpResponseMessage overview = await user.GetAsync($"/api/projects/{projectId}/overview");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        JsonElement data = await ReadDataAsync(overview);
        JsonElement metrics = data.GetProperty("metrics");
        Assert.Equal(1, metrics.GetProperty("unitCount").GetInt64());
        Assert.Equal(0, metrics.GetProperty("completedUnitCount").GetInt64());
        Assert.Equal(4, metrics.GetProperty("totalWordCount").GetInt64());
        Assert.Equal(0, metrics.GetProperty("unitsWithoutText").GetInt64());
        // unitsWithoutShots 只统计非 draft 章节：本章仍为 draft，因此为 0。
        Assert.Equal(0, metrics.GetProperty("unitsWithoutShots").GetInt64());
        Assert.Equal(0, metrics.GetProperty("canvasCount").GetInt64());
        Assert.Equal(1, metrics.GetProperty("shotCount").GetInt64());
        Assert.Equal(0, metrics.GetProperty("pendingCandidateCount").GetInt64());
        Assert.Equal(0, metrics.GetProperty("renderSucceededCount").GetInt64());
        Assert.Equal(0, metrics.GetProperty("staleArtifactCount").GetInt64());
        Assert.True(metrics.TryGetProperty("readyStoryboardCount", out _));
        Assert.True(metrics.TryGetProperty("readyPrevizCount", out _));
        Assert.True(metrics.TryGetProperty("readyVideoCount", out _));
        Assert.True(metrics.TryGetProperty("assetCount", out _));

        JsonElement units = data.GetProperty("units");
        Assert.Equal(1, units.GetArrayLength());
        Assert.Equal("第一集", units[0].GetProperty("unit").GetProperty("title").GetString());
        Assert.Equal(1, units[0].GetProperty("shotCount").GetInt64());
        Assert.Equal(0, units[0].GetProperty("candidateCount").GetInt64());
        Assert.Equal(0, units[0].GetProperty("canvasCount").GetInt64());

        // 项目不存在 → 404 record not found。
        HttpResponseMessage missing = await user.GetAsync("/api/projects/missing/core");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        HttpResponseMessage missingOverview = await user.GetAsync("/api/projects/missing/overview");
        Assert.Equal(HttpStatusCode.NotFound, missingOverview.StatusCode);
    }

    [Fact]
    public async Task 未登录访问读视图返回_401()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/projects/p1/core")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/projects/p1/overview")).StatusCode);
    }
}
