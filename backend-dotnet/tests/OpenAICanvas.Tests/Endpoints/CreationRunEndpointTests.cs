#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 创作运行路由的端到端契约测试。
/// 对应 Go: <c>handler/creation.go</c> + <c>app/creation.go</c>。
/// </summary>
public sealed class CreationRunEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    public CreationRunEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-crun-{Guid.NewGuid():N}");
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
            username = "creator2",
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
    public async Task 创作运行_创建claim保存与幂等()
    {
        using HttpClient user = await SignInAsync();

        // 缺 clientKey → 400。
        HttpResponseMessage missingKey = await user.PostAsJsonAsync("/api/creation-runs", new
        {
            state = new { foo = "bar" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        Assert.Equal("缺少稳定会话键", await ReadMessageAsync(missingKey));

        // 创建。
        HttpResponseMessage created = await user.PostAsJsonAsync("/api/creation-runs", new
        {
            clientKey = "sess-1",
            canvasId = "",
            state = new { topic = "科幻短剧" },
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement detail = await ReadDataAsync(created);
        JsonElement run = detail.GetProperty("run");
        string runId = run.GetProperty("id").GetString()!;
        Assert.Equal("idle", run.GetProperty("status").GetString());
        Assert.Equal(1, run.GetProperty("revision").GetInt64());
        Assert.Equal(0, detail.GetProperty("submissions").GetArrayLength());

        // 同键同内容 → 幂等返回同 ID。
        HttpResponseMessage again = await user.PostAsJsonAsync("/api/creation-runs", new
        {
            clientKey = "sess-1",
            canvasId = "",
            state = new { topic = "科幻短剧" },
        });
        Assert.Equal(
            runId,
            (await ReadDataAsync(again)).GetProperty("run").GetProperty("id").GetString());

        // 同键不同内容 → 409。
        HttpResponseMessage conflict = await user.PostAsJsonAsync("/api/creation-runs", new
        {
            clientKey = "sess-1",
            canvasId = "",
            state = new { topic = "别的" },
        });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        // claim。
        HttpResponseMessage claim = await user.PostAsJsonAsync($"/api/creation-runs/{runId}/claim", new
        {
            owner = "page-1",
            expectedEpoch = 0,
        });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        JsonElement claimed = await ReadDataAsync(claim);
        long epoch = claimed.GetProperty("executionEpoch").GetInt64();
        Assert.Equal(1, epoch);
        Assert.Equal("page-1", claimed.GetProperty("executionOwner").GetString());
        Assert.True(claimed.TryGetProperty("leaseExpiresAt", out _));

        // save（revision 递增后一致）。
        HttpResponseMessage saved = await user.PatchAsJsonAsync($"/api/creation-runs/{runId}", new
        {
            owner = "page-1",
            executionEpoch = epoch,
            revision = claimed.GetProperty("revision").GetInt64(),
            status = "running",
            state = new { topic = "科幻短剧", step = 2 },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        JsonElement savedRun = await ReadDataAsync(saved);
        Assert.Equal("running", savedRun.GetProperty("status").GetString());
        Assert.Equal(3, savedRun.GetProperty("revision").GetInt64());

        // heartbeat。
        HttpResponseMessage heartbeat = await user.PostAsJsonAsync(
            $"/api/creation-runs/{runId}/heartbeat", new { owner = "page-1", executionEpoch = epoch });
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        Assert.True((await ReadDataAsync(heartbeat)).GetProperty("leaseExpiresAt").GetString()!.Length > 0);

        // release。
        HttpResponseMessage release = await user.PostAsJsonAsync(
            $"/api/creation-runs/{runId}/release", new { owner = "page-1", executionEpoch = epoch });
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        Assert.True((await ReadDataAsync(release)).GetProperty("released").GetBoolean());

        // 列表。
        HttpResponseMessage list = await user.GetAsync("/api/creation-runs");
        Assert.Equal(1, (await ReadDataAsync(list)).GetProperty("runs").GetArrayLength());

        // 详情。
        HttpResponseMessage fetched = await user.GetAsync($"/api/creation-runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    [Fact]
    public async Task 创作JSON_密钥与内嵌媒体拒绝()
    {
        using HttpClient user = await SignInAsync();

        HttpResponseMessage withKey = await user.PostAsJsonAsync("/api/creation-runs", new
        {
            clientKey = "sess-key",
            state = new { config = new { apiKey = "sk-1" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, withKey.StatusCode);
        Assert.Equal("创作记录不能包含密钥、内嵌媒体或临时签名链接", await ReadMessageAsync(withKey));

        HttpResponseMessage withData = await user.PostAsJsonAsync("/api/creation-runs", new
        {
            clientKey = "sess-data",
            state = new { note = "data:image/png;base64,AAA" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, withData.StatusCode);
        Assert.Equal("创作记录不能包含密钥、内嵌媒体或临时签名链接", await ReadMessageAsync(withData));
    }

    [Fact]
    public async Task 未登录访问返回_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/creation-runs")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/creation-runs", new { })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/creation-runs/r1/submissions/prepare", new { })).StatusCode);
    }
}
