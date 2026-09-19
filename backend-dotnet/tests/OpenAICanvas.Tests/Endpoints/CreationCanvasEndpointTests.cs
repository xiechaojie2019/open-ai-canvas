#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 创作画布提交三路由的端到端契约测试。
/// 对应 Go: <c>app/creation_canvas.go</c>（canvas / canvas-snapshot / canvas-commit）。
/// </summary>
public sealed class CreationCanvasEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    public CreationCanvasEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-cc-{Guid.NewGuid():N}");
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
            username = "canvascreator",
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

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("msg").GetString() ?? "";
    }

    [Fact]
    public async Task 创作画布_创建快照与未批准提交拒绝()
    {
        using HttpClient user = await SignInAsync();

        // 创建运行 + claim。
        HttpResponseMessage created = await user.PostAsJsonAsync("/api/creation-runs", new
        {
            clientKey = "cc-1",
            state = new { },
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        string runId = (await ReadDataAsync(created)).GetProperty("run").GetProperty("id").GetString()!;
        HttpResponseMessage claim = await user.PostAsJsonAsync(
            $"/api/creation-runs/{runId}/claim", new { owner = "p1", expectedEpoch = 0 });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        JsonElement claimed = await ReadDataAsync(claim);
        long epoch = claimed.GetProperty("executionEpoch").GetInt64();

        // 未批准方案 → canvas 创建 409。
        HttpResponseMessage canvasBeforeApprove = await user.PostAsJsonAsync(
            $"/api/creation-runs/{runId}/canvas", new { owner = "p1", executionEpoch = epoch });
        Assert.Equal(HttpStatusCode.Conflict, canvasBeforeApprove.StatusCode);
        Assert.Equal("请先确认方案", await ReadMessageAsync(canvasBeforeApprove));

        // 提交方案（无 ops 的空方案也允许版本推进）。
        HttpResponseMessage approved = await user.PostAsJsonAsync(
            $"/api/creation-runs/{runId}/proposal-approve", new
            {
                owner = "p1",
                executionEpoch = epoch,
                revision = claimed.GetProperty("revision").GetInt64(),
                proposalVersion = 1,
                proposal = (object?)new { items = Array.Empty<object>() },
                ops = new[] { new { type = "select_nodes", ids = Array.Empty<string>() } },
            });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        // 再次创建画布（现在已批准）。
        HttpResponseMessage canvasCreated = await user.PostAsJsonAsync(
            $"/api/creation-runs/{runId}/canvas", new { owner = "p1", executionEpoch = epoch });
        Assert.Equal(HttpStatusCode.OK, canvasCreated.StatusCode);
        JsonElement canvasData = await ReadDataAsync(canvasCreated);
        string canvasId = canvasData.GetProperty("canvasId").GetString()!;
        Assert.NotEqual("", canvasId);

        // 快照。
        HttpResponseMessage snapshot = await user.GetAsync($"/api/creation-runs/{runId}/canvas-snapshot");
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        JsonElement snapshotData = await ReadDataAsync(snapshot);
        Assert.Equal(
            canvasId,
            snapshotData.GetProperty("document").GetProperty("id").GetString());
        string snapshotHash = snapshotData.GetProperty("snapshotHash").GetString()!;
        Assert.NotEqual("", snapshotHash);

        // 提交画布（原样提交 = 无变化）。
        HttpResponseMessage committed = await user.PostAsJsonAsync(
            $"/api/creation-runs/{runId}/canvas-commit", new
            {
                owner = "p1",
                executionEpoch = epoch,
                expectedSnapshotHash = snapshotHash,
                document = snapshotData.GetProperty("document"),
            });
        if (!committed.IsSuccessStatusCode)
        {
            Assert.Fail(await committed.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        Assert.NotEqual(
            "",
            (await ReadDataAsync(committed)).GetProperty("snapshotHash").GetString());

        // 错误快照哈希 → 409。
        HttpResponseMessage stale = await user.PostAsJsonAsync(
            $"/api/creation-runs/{runId}/canvas-commit", new
            {
                owner = "p1",
                executionEpoch = epoch,
                expectedSnapshotHash = "stale-hash",
                document = snapshotData.GetProperty("document"),
            });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("画布已变化，请重新读取后核对", await ReadMessageAsync(stale));
    }

    [Fact]
    public async Task 未登录访问返回_401()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/creation-runs/r1/canvas", new { })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/creation-runs/r1/canvas-snapshot")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/creation-runs/r1/canvas-commit", new { })).StatusCode);
    }
}
