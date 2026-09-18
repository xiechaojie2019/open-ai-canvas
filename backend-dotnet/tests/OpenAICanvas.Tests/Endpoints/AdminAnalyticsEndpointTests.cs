#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 管理后台 API 日志与存储统计路由的端到端契约测试。
/// 对应 Go: <c>handler/auth.go</c> 日志部分与 <c>handler/admin_storage.go</c> 统计部分。
/// </summary>
public sealed class AdminAnalyticsEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;
    private string _userId = "";

    public AdminAnalyticsEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-ana-{Guid.NewGuid():N}");
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
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        _userId = (await repository.UserByUsernameAsync("admin"))!.ID;
        return _adminClient;
    }

    private async Task SeedLogAsync(string id, string model = "seedance-video")
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        await repository.CreateAsync(new ApiCallLog
        {
            ID = id,
            UserID = _userId,
            ChannelID = "",
            Capability = "video",
            Model = model,
            Path = "/v1/videos",
            Method = "POST",
            Status = "succeeded",
            DurationMs = 1234,
            InputTokens = 10,
            OutputTokens = 20,
            Billable = false,
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 未登录访问日志返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/admin/api-logs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 日志列表_装饰与报文剥离()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedLogAsync("LOG_1");

        HttpResponseMessage response = await admin.GetAsync("/api/admin/api-logs?page=1&pageSize=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(1, data.GetProperty("total").GetInt64());
        Assert.Equal(50, data.GetProperty("pageSize").GetInt64());

        JsonElement log = data.GetProperty("logs")[0];
        // 渠道为空 → "自定义渠道"；用户装饰。
        Assert.Equal("自定义渠道", log.GetProperty("channelName").GetString());
        Assert.Equal("admin", log.GetProperty("userAccount").GetString());
        // 列表剥离原始报文（omitempty：空串字段整体省略）。
        Assert.False(log.TryGetProperty("requestBody", out _));
        Assert.False(log.TryGetProperty("responseBody", out _));
    }

    [Fact]
    public async Task 日志详情与不存在_404()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedLogAsync("LOG_2");

        HttpResponseMessage detail = await admin.GetAsync("/api/admin/api-logs/LOG_2");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        JsonElement log = (await ReadDataAsync(detail)).GetProperty("log");
        Assert.Equal("seedance-video", log.GetProperty("model").GetString());

        HttpResponseMessage missing = await admin.GetAsync("/api/admin/api-logs/LOG_NOPE");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task 日志导出CSV_含表头与数据行()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedLogAsync("LOG_3");

        HttpResponseMessage response = await admin.GetAsync("/api/admin/api-logs-export.csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("时间,用户,渠道,模型,能力,状态", body, StringComparison.Ordinal);
        Assert.Contains("seedance-video", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 日志类型校验_无效recordType()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/admin/api-logs?recordType=bogus");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("请求明细类型无效", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 存储统计_按类型与供应商分组()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            DateTime now = DateTime.UtcNow;
            await repository.CreateAsync(new Resource
            {
                ID = "RES_S1",
                UserID = _userId,
                Kind = "image",
                Status = "ready",
                Provider = "local",
                ObjectKey = "a.png",
                MimeType = "image/png",
                Size = 1024,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await repository.CreateAsync(new Resource
            {
                ID = "RES_S2",
                UserID = _userId,
                Kind = "video",
                Status = "pending",
                Provider = "",
                ObjectKey = "b.mp4",
                MimeType = "video/mp4",
                Size = 2048,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        HttpResponseMessage response = await admin.GetAsync("/api/admin/storage/stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement stats = await ReadDataAsync(response);
        Assert.Equal(2, stats.GetProperty("resourceCount").GetInt64());
        Assert.Equal(1, stats.GetProperty("readyCount").GetInt64());
        Assert.Equal(3072, stats.GetProperty("totalBytes").GetInt64());
        Assert.Equal(1024, stats.GetProperty("physicalBytes").GetInt64());
        Assert.Equal(2, stats.GetProperty("byKind").GetArrayLength());
        // 空 provider 归一为 local。
        Assert.Equal("local", stats.GetProperty("byProvider")[0].GetProperty("key").GetString());
    }
}