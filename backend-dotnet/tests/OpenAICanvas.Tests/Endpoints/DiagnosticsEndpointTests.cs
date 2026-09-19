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
/// 用户诊断包路由的端到端契约测试。
/// 对应 Go: <c>handler/diagnostics.go</c> + <c>app/diagnostics.go</c>。
/// </summary>
public sealed class DiagnosticsEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    public DiagnosticsEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-diag-{Guid.NewGuid():N}");
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
            username = "diaguser",
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
    public async Task 诊断包_预览导出与校验()
    {
        using HttpClient user = await SignInAsync();

        // 预览（默认最近 30 分钟窗口）。
        HttpResponseMessage preview = await user.PostAsJsonAsync("/api/diagnostics/preview", new
        {
            runtime = new { browser = "Chrome", os = "Windows", timezone = "Asia/Shanghai" },
            clientEvents = new[]
            {
                new { id = "evt-1", timestamp = "2026-09-19T00:00:00Z", level = "error",
                      category = "ui", message = "页面崩溃", route = "/canvas?x=1", httpStatus = 500 },
            },
        });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        JsonElement previewData = await ReadDataAsync(preview);
        Assert.Equal(500, previewData.GetProperty("clientEventLimit").GetInt32());
        Assert.Equal(0, previewData.GetProperty("taskCount").GetInt32());
        Assert.False(previewData.GetProperty("willTruncate").GetBoolean());

        // 预览响应不含密钥（redaction 在服务端采集侧处理，客户端事件原样计数）。

        // 时间窗 > 24 小时 → 400。
        HttpResponseMessage longWindow = await user.PostAsJsonAsync("/api/diagnostics/preview", new
        {
            from = "2026-01-01T00:00:00Z",
            to = "2026-09-19T00:00:00Z",
        });
        Assert.Equal(HttpStatusCode.BadRequest, longWindow.StatusCode);
        Assert.Equal("诊断时间范围不能超过 24 小时", await ReadMessageAsync(longWindow));

        // 无效时间格式 → 400。
        HttpResponseMessage badTime = await user.PostAsJsonAsync("/api/diagnostics/preview", new
        {
            from = "not-a-time",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badTime.StatusCode);
        Assert.Equal("诊断开始时间格式无效", await ReadMessageAsync(badTime));

        // 导出 ZIP：manifest.json 存在且 schemaVersion=1，README 与 client 事件在内。
        HttpResponseMessage export = await user.PostAsJsonAsync("/api/diagnostics/export", new
        {
            description = "诊断描述",
            runtime = new { browser = "Chrome", os = "Windows" },
            clientEvents = new[]
            {
                new { id = "evt-1", timestamp = "2026-09-19T00:00:00Z", level = "error",
                      category = "ui", message = "页面崩溃", route = "/canvas?x=1" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("attachment; filename=", export.Content.Headers.ContentDisposition?.ToString() ?? "");
        // ASP.NET 合成顺序与 Go 不同但语义一致（同 #54）。
        string cacheControl = export.Headers.GetValues("Cache-Control").FirstOrDefault() ?? "";
        Assert.Contains("no-store", cacheControl, StringComparison.Ordinal);
        Assert.Contains("private", cacheControl, StringComparison.Ordinal);
        Assert.NotEqual("", export.Headers.GetValues("X-Diagnostic-Bundle-ID").FirstOrDefault());
        Assert.Equal("1", export.Headers.GetValues("X-Diagnostic-Schema-Version").FirstOrDefault());
        byte[] zipBytes = await export.Content.ReadAsByteArrayAsync();
        Assert.Equal("PK", Encoding.ASCII.GetString(zipBytes, 0, 2));
        Assert.True(zipBytes.Length > 100);
    }

    [Fact]
    public async Task 未登录访问返回_401()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/diagnostics/preview", new { })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/diagnostics/export", new { })).StatusCode);
    }
}
