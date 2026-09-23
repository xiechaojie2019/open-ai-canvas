#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 系统性能与缓存清理路由的端到端契约测试。
/// 对应 Go: <c>handler/admin_system_performance.go</c>。
/// </summary>
public sealed class SystemPerformanceEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public SystemPerformanceEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-perf-{Guid.NewGuid():N}");
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
    public async Task 系统性能_指标与缓存清理()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 性能总览。
        HttpResponseMessage perf = await admin.GetAsync("/api/admin/system-performance");
        Assert.Equal(HttpStatusCode.OK, perf.StatusCode);
        JsonElement data = await ReadDataAsync(perf);
        Assert.Equal("healthy", data.GetProperty("status").GetString());
        Assert.Equal(Environment.MachineName, data.GetProperty("host").GetProperty("hostname").GetString());
        Assert.True(data.GetProperty("host").GetProperty("cpuCores").GetInt32() > 0);
        Assert.True(data.GetProperty("memory").GetProperty("totalBytes").GetInt64() > 0);
        Assert.True(data.GetProperty("disk").GetProperty("available").GetBoolean());
        Assert.Equal("sqlite", data.GetProperty("database").GetProperty("driver").GetString());
        Assert.True(data.GetProperty("database").GetProperty("connected").GetBoolean());
        Assert.Equal(15, data.GetProperty("database").GetProperty("schema").GetProperty("expected").GetInt64());
        Assert.Equal(15, data.GetProperty("database").GetProperty("schema").GetProperty("current").GetInt64());
        JsonElement databasePool = data.GetProperty("database").GetProperty("pool");
        Assert.Equal(0, databasePool.GetProperty("maxOpenConnections").GetInt64());
        Assert.Equal(0, databasePool.GetProperty("openConnections").GetInt64());
        Assert.Equal(0, databasePool.GetProperty("inUse").GetInt64());
        Assert.Equal(0, databasePool.GetProperty("idle").GetInt64());
        Assert.Equal(0, databasePool.GetProperty("waitCount").GetInt64());
        Assert.Equal(0, databasePool.GetProperty("waitDurationMs").GetInt64());
        Assert.False(data.GetProperty("database").TryGetProperty("postgres", out _));
        // Redis 协调器未移植 → 本地单实例模式。
        JsonElement redis = data.GetProperty("redis");
        Assert.False(redis.GetProperty("configured").GetBoolean());
        Assert.Equal("local", redis.GetProperty("mode").GetString());
        JsonElement redisPool = redis.GetProperty("pool");
        Assert.Equal(0, redisPool.GetProperty("hits").GetInt64());
        Assert.Equal(0, redisPool.GetProperty("totalConnections").GetInt64());
        Assert.Equal(1, redis.GetProperty("cacheGroups").GetArrayLength());

        // 缓存清理：scope 错误 → 400。
        HttpResponseMessage wrongScope = await admin.PostAsJsonAsync(
            "/api/admin/system-performance/cache/clear", new { scope = "all" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongScope.StatusCode);
        Assert.Equal("仅支持清理运行时缓存", await ReadMessageAsync(wrongScope));

        // 正确清理。
        HttpResponseMessage cleared = await admin.PostAsJsonAsync(
            "/api/admin/system-performance/cache/clear", new { scope = "runtime" });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        JsonElement clearData = await ReadDataAsync(cleared);
        Assert.Equal("runtime", clearData.GetProperty("scope").GetString());
        Assert.Equal(1, clearData.GetProperty("groups").GetArrayLength());
        Assert.Equal("rateLimits", clearData.GetProperty("groups")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task 未登录访问返回_401()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/admin/system-performance")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/admin/system-performance/cache/clear", new { scope = "runtime" })).StatusCode);
    }
}
