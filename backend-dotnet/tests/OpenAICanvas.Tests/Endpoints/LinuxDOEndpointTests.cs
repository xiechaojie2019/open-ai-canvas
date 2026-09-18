#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>LinuxDO OAuth 路由的端到端契约测试。</summary>
public sealed class LinuxDOEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public LinuxDOEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-linuxdo-{Guid.NewGuid():N}");
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

    private async Task RegisterFirstAdminAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "admin",
            email = "admin@example.com",
            password = "password123",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LinuxDO_无本地管理员时_start_返回_403()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/auth/linuxdo/start");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("请先创建本地管理员账号", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxDO_有管理员但未启用时_start_返回_403()
    {
        await RegisterFirstAdminAsync();

        HttpResponseMessage response = await _client.GetAsync("/api/auth/linuxdo/start");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("尚未启用", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxDO_缺少参数时_callback_重定向到登录页并带错误()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/auth/linuxdo/callback");

        // 缺少 state 和 code 时重定向到 /login?oauth_error=...
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        string? location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.StartsWith("/login?oauth_error=", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxDO_根级回调_缺少参数时同样重定向()
    {
        HttpResponseMessage response = await _client.GetAsync("/oauth/linuxdo/callback");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        string? location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.StartsWith("/login?oauth_error=", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxDO_start_受限流保护()
    {
        await RegisterFirstAdminAsync();

        // 快速请求超过限流阈值（10 分钟内 20 次）。
        HttpStatusCode last = HttpStatusCode.OK;
        for (int i = 0; i < 25; i++)
        {
            HttpResponseMessage r = await _client.GetAsync("/api/auth/linuxdo/start");
            last = r.StatusCode;
            if (last == HttpStatusCode.TooManyRequests)
            {
                break;
            }
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
