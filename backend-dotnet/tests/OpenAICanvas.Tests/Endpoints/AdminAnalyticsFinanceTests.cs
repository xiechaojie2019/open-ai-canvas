#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 管理员分析财务汇总契约测试：只统计已结算订单、按下单成本快照计成本、订单去重。
/// 对应 Go: <c>TestAdminAnalyticsFinanceUsesSettledSnapshotsAndDeduplicates</c>。
/// </summary>
public sealed class AdminAnalyticsFinanceTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly DateTime _now = DateTime.UtcNow;

    public AdminAnalyticsFinanceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-fin-{Guid.NewGuid():N}");
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

    private async Task<HttpClient> RegisterAsync(string username, string password = "password123")
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username,
            password,
            acceptedTerms = true,
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private async Task SeedAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        DateTime now = _now;
        BillingOrder[] orders =
        {
            new()
            {
                ID = "paid", Status = "settled", ActualAmountMicrocredits = 900_000,
                CostConfigured = true, CostUnitPriceMicrocredits = 300_000,
                CostBillingMode = "fixed_request", CostQuantity = 1,
            },
            new()
            {
                ID = "free", Status = "settled",
                CostConfigured = true, CostBillingMode = "fixed_request", CostQuantity = 1,
            },
            new() { ID = "unknown", Status = "settled", ActualAmountMicrocredits = 400_000 },
            new() { ID = "reserved", Status = "reserved", ReservedAmountMicrocredits = 1_000_000 },
            new() { ID = "refunded", Status = "refunded", ActualAmountMicrocredits = 800_000 },
            new()
            {
                ID = "loss", Status = "settled", ActualAmountMicrocredits = 100_000,
                CostConfigured = true, CostUnitPriceMicrocredits = 100_000,
                CostBillingMode = "per_second", CostQuantity = 3,
            },
        };
        List<ApiCallLog> logs = new();
        foreach (BillingOrder order in orders)
        {
            order.UserID = "user";
            order.ChannelID = "channel";
            order.IdempotencyKey = order.ID;
            order.CreatedAt = now;
            logs.Add(new ApiCallLog
            {
                ID = order.ID,
                BillingOrderID = order.ID,
                UserID = "user",
                ChannelID = "channel",
                Model = order.ID,
                Capability = "text",
                Billable = true,
                RequestKind = "create",
                Status = "succeeded",
                CreatedAt = now,
            });
        }
        foreach (string id in new[]
                 {
                     "retry", "poll", "download", "upload", "workflow-schema",
                     "cancel-query", "cancel", "wrong-user", "old-channel",
                 })
        {
            ApiCallLog item = new()
            {
                ID = id,
                BillingOrderID = "paid",
                UserID = "user",
                ChannelID = "channel",
                Model = "paid",
                Capability = "text",
                Billable = true,
                RequestKind = "create",
                Status = "succeeded",
                CreatedAt = now.AddSeconds(1),
            };
            switch (id)
            {
                case "poll" or "download" or "upload" or "workflow-schema" or "cancel-query" or "cancel":
                    item.RequestKind = id;
                    item.Model = "auxiliary";
                    item.CreatedAt = now.AddSeconds(-1);
                    break;
                case "wrong-user":
                    item.UserID = "other";
                    break;
                case "old-channel":
                    item.ChannelID = "previous";
                    break;
            }
            logs.Add(item);
        }
        foreach (BillingOrder order in orders)
        {
            await repository.CreateAsync(order);
        }
        foreach (ApiCallLog log in logs)
        {
            await repository.CreateAsync(log);
        }
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private string Range() =>
        $"from={Uri.EscapeDataString(_now.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))}"
        + $"&to={Uri.EscapeDataString(_now.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))}";

    [Fact]
    public async Task 财务汇总_结算快照与订单去重()
    {
        HttpClient admin = await RegisterAsync("admin");
        await SeedAsync();

        HttpResponseMessage response = await admin.GetAsync($"/api/admin/analytics/overview?{Range()}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement finance = (await ReadDataAsync(response)).GetProperty("kpi").GetProperty("finance");
        Assert.Equal(4, finance.GetProperty("settledOrders").GetInt64());
        Assert.Equal(3, finance.GetProperty("costedOrders").GetInt64());
        Assert.Equal(1_400_000, finance.GetProperty("revenueMicrocredits").GetInt64());
        Assert.Equal(600_000, finance.GetProperty("costMicrocredits").GetInt64());
        // 部分订单缺成本：利润与利润率输出 null。
        Assert.Equal(JsonValueKind.Null, finance.GetProperty("profitMicrocredits").ValueKind);
        Assert.Equal(JsonValueKind.Null, finance.GetProperty("profitMargin").ValueKind);

        Dictionary<string, JsonElement> rows = new(StringComparer.Ordinal);
        foreach (JsonElement row in (await ReadDataAsync(response)).GetProperty("models").EnumerateArray())
        {
            rows[row.GetProperty("model").GetString()!] = row.GetProperty("finance").Clone();
        }
        JsonElement paid = rows["paid"];
        Assert.Equal(1, paid.GetProperty("settledOrders").GetInt64());
        Assert.Equal(600_000, paid.GetProperty("profitMicrocredits").GetInt64());
        Assert.InRange(paid.GetProperty("profitMargin").GetDouble(), 66.6, 66.7);
        JsonElement free = rows["free"];
        Assert.Equal(1, free.GetProperty("costedOrders").GetInt64());
        Assert.Equal(0, free.GetProperty("profitMicrocredits").GetInt64());
        Assert.Equal(JsonValueKind.Null, free.GetProperty("profitMargin").ValueKind);
        JsonElement loss = rows["loss"];
        Assert.Equal(-200_000, loss.GetProperty("profitMicrocredits").GetInt64());
        Assert.Equal(-200, loss.GetProperty("profitMargin").GetDouble());

        HttpResponseMessage csvResponse = await admin.GetAsync($"/api/admin/analytics/export.csv?{Range()}");
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        string body = (await csvResponse.Content.ReadAsStringAsync()).TrimStart('\uFEFF');
        string[] lines = body.Split('\n', StringSplitOptions.TrimEntries);
        long revenue = 0, cost = 0;
        foreach (string line in lines.Skip(1))
        {
            if (line.Length == 0)
            {
                continue;
            }
            string[] columns = line.Split(',');
            Assert.Equal(19, columns.Length);
            if (columns[6] == "auxiliary")
            {
                // 辅助请求（轮询/下载等）不得携带订单财务。
                Assert.Equal("", columns[15]);
                Assert.Equal("", columns[16]);
                Assert.Equal("", columns[17]);
            }
            if (columns[15].Length > 0)
            {
                revenue += long.Parse(columns[15]);
            }
            if (columns[16].Length > 0)
            {
                cost += long.Parse(columns[16]);
            }
        }
        Assert.Equal(finance.GetProperty("revenueMicrocredits").GetInt64(), revenue);
        Assert.Equal(finance.GetProperty("costMicrocredits").GetInt64(), cost);

        HttpResponseMessage filtered = await admin.GetAsync($"/api/admin/analytics/overview?{Range()}&model=paid");
        Assert.True(filtered.IsSuccessStatusCode, await filtered.Content.ReadAsStringAsync());
        JsonElement filteredFinance = (await ReadDataAsync(filtered)).GetProperty("kpi").GetProperty("finance");
        Assert.Equal(1, filteredFinance.GetProperty("settledOrders").GetInt64());
        Assert.Equal(600_000, filteredFinance.GetProperty("profitMicrocredits").GetInt64());

        HttpResponseMessage previousChannel = await admin.GetAsync(
            $"/api/admin/analytics/overview?{Range()}&model=paid&channelId=previous");
        Assert.Equal(
            0,
            (await ReadDataAsync(previousChannel)).GetProperty("kpi").GetProperty("finance")
                .GetProperty("settledOrders").GetInt64());

        HttpResponseMessage auxiliary = await admin.GetAsync(
            $"/api/admin/analytics/overview?{Range()}&model=auxiliary");
        Assert.Equal(
            0,
            (await ReadDataAsync(auxiliary)).GetProperty("kpi").GetProperty("finance")
                .GetProperty("settledOrders").GetInt64());

        // 注册默认关闭：直接落库普通用户再登录，验证非管理员不可读财务。
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            await repository.CreateAsync(new User
            {
                ID = IdGenerator.NewId(),
                Username = "regular1",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123"),
                Role = "user",
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "regular1",
            password = "password123",
        });
        login.EnsureSuccessStatusCode();
        using HttpClient regular = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        regular.DefaultRequestHeaders.Add(
            "Cookie", login.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        HttpResponseMessage forbidden = await regular.GetAsync($"/api/admin/analytics/overview?{Range()}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
