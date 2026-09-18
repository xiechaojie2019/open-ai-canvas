#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 管理端分析总览与模型价格路由的端到端契约测试。
/// 对应 Go: <c>handler/admin_analytics.go</c> + <c>app/analytics.go</c>。
/// </summary>
public sealed class AdminInsightEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;
    private HttpClient? _userClient;

    public AdminInsightEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-insight-{Guid.NewGuid():N}");
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

    /// <summary>首个注册用户即管理员。</summary>
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

    [Fact]
    public async Task 分析总览_空库指标与窗口()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/admin/analytics/overview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(30, data.GetProperty("trend").GetArrayLength());
        JsonElement kpi = data.GetProperty("kpi");
        Assert.Equal(0, kpi.GetProperty("generationTasks").GetInt64());
        Assert.Equal(0, kpi.GetProperty("upstreamRequests").GetInt64());
        Assert.Equal(0, kpi.GetProperty("currentQueuedTasks").GetInt64());
        Assert.Equal(0, kpi.GetProperty("activeUsers").GetInt64());
        Assert.Equal(0, data.GetProperty("models").GetArrayLength());
        Assert.Equal(0, data.GetProperty("users").GetArrayLength());
        Assert.Equal(0, data.GetProperty("failures").GetArrayLength());

        // models / users 视图与 CSV 导出。
        HttpResponseMessage models = await admin.GetAsync("/api/admin/analytics/models");
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        Assert.Equal(0, (await ReadDataAsync(models)).GetProperty("models").GetArrayLength());
        HttpResponseMessage users = await admin.GetAsync("/api/admin/analytics/users");
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        JsonElement usersData = await ReadDataAsync(users);
        Assert.True(usersData.TryGetProperty("dau", out _));
        Assert.True(usersData.TryGetProperty("wau", out _));
        Assert.True(usersData.TryGetProperty("mau", out _));
        HttpResponseMessage csv = await admin.GetAsync("/api/admin/analytics/export.csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.StartsWith("text/csv", csv.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task 模型价格_CRUD与校验()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 创建：币种缺省 USD，能力归一。
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/model-pricings", new
        {
            model = "  gpt-video  ",
            capability = "VIDEO",
            inputPerMillionMicros = 1000,
            outputPerMillionMicros = 2000,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement pricing = (await ReadDataAsync(created)).GetProperty("pricing");
        string pricingId = pricing.GetProperty("id").GetString()!;
        Assert.Equal("gpt-video", pricing.GetProperty("model").GetString());
        Assert.Equal("video", pricing.GetProperty("capability").GetString());
        Assert.Equal("USD", pricing.GetProperty("currency").GetString());

        // 列表包含新价格。
        HttpResponseMessage listed = await admin.GetAsync("/api/admin/model-pricings");
        Assert.Equal(1, (await ReadDataAsync(listed)).GetProperty("pricings").GetArrayLength());

        // 更新（PATCH）。
        HttpResponseMessage updated = await admin.PatchAsJsonAsync(
            $"/api/admin/model-pricings/{pricingId}", new
            {
                model = "gpt-video",
                capability = "video",
                currency = "cny",
                perRequestMicros = 50,
            });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("CNY", (await ReadDataAsync(updated)).GetProperty("pricing").GetProperty("currency").GetString());

        // 校验：缺模型 → 400；负价格 → 400。
        HttpResponseMessage blank = await admin.PostAsJsonAsync("/api/admin/model-pricings", new
        {
            capability = "video",
        });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        using JsonDocument blankDoc = JsonDocument.Parse(await blank.Content.ReadAsStringAsync());
        Assert.Equal("请填写模型并选择能力类型", blankDoc.RootElement.GetProperty("msg").GetString());
        HttpResponseMessage negative = await admin.PostAsJsonAsync("/api/admin/model-pricings", new
        {
            model = "x",
            capability = "video",
            inputPerMillionMicros = -1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);

        // 删除 → 再删仍 200（Go 删除不校验存在）。
        HttpResponseMessage deleted = await admin.DeleteAsync($"/api/admin/model-pricings/{pricingId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        HttpResponseMessage deletedAgain = await admin.DeleteAsync($"/api/admin/model-pricings/{pricingId}");
        Assert.Equal(HttpStatusCode.OK, deletedAgain.StatusCode);
    }

    [Fact]
    public async Task 未登录访问返回_401()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/admin/analytics/overview")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/admin/model-pricings")).StatusCode);
    }
}
