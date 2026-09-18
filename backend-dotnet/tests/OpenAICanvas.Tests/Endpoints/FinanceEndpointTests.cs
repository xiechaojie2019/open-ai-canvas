#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 钱包、兑换码与账单路由的端到端契约测试。
/// </summary>
public sealed class FinanceEndpointTests : IDisposable
{
    /// <summary>对应 Go 的 <c>app.CreditScale</c>。</summary>
    private const long CreditScale = 1_000_000;

    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public FinanceEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-finance-{Guid.NewGuid():N}");
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

    // ------------------------------------------------------------ 钱包

    [Fact]
    public async Task 未登录访问钱包返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/wallet");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 钱包返回账户账本与策略()
    {
        using HttpClient user = await SignInAsync("alice");

        HttpResponseMessage response = await user.GetAsync("/api/wallet");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);

        // 首个用户注册即获得注册奖励。
        Assert.Equal(100 * CreditScale, data.GetProperty("account").GetProperty("availableMicrocredits").GetInt64());
        // 注册奖励本身会写一条 signup_bonus 账本记录。
        Assert.Equal(1, data.GetProperty("total").GetInt64());
        Assert.Equal("signup_bonus", data.GetProperty("entries")[0].GetProperty("type").GetString());
        Assert.Equal(1, data.GetProperty("page").GetInt64());
        Assert.Equal(30, data.GetProperty("pageSize").GetInt64());

        JsonElement policy = data.GetProperty("policy");
        Assert.Equal(100 * CreditScale, policy.GetProperty("signupBonusMicrocredits").GetInt64());
        Assert.Equal(10 * CreditScale, policy.GetProperty("checkinBonusMicrocredits").GetInt64());
        Assert.False(policy.GetProperty("checkedInToday").GetBoolean());
    }

    [Fact]
    public async Task 钱包_pageSize_非正整数返回_400()
    {
        using HttpClient user = await SignInAsync("alice");

        HttpResponseMessage response = await user.GetAsync("/api/wallet?pageSize=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------ 签到

    [Fact]
    public async Task 签到发放奖励且重复签到返回_409()
    {
        using HttpClient user = await SignInAsync("alice");

        HttpResponseMessage first = await user.PostAsync("/api/wallet/checkin", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        JsonElement data = await ReadDataAsync(first);
        Assert.True(data.GetProperty("granted").GetBoolean());
        // 100 注册奖励 + 10 签到奖励。
        Assert.Equal(110 * CreditScale, data.GetProperty("account").GetProperty("availableMicrocredits").GetInt64());

        HttpResponseMessage second = await user.PostAsync("/api/wallet/checkin", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("今天已经签到过了", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 签到后钱包标记已签到()
    {
        using HttpClient user = await SignInAsync("alice");
        await user.PostAsync("/api/wallet/checkin", null);

        HttpResponseMessage response = await user.GetAsync("/api/wallet");

        JsonElement data = await ReadDataAsync(response);
        Assert.True(data.GetProperty("policy").GetProperty("checkedInToday").GetBoolean());
    }

    // ------------------------------------------------------------ 兑换码核销

    [Fact]
    public async Task 无效兑换码返回_400()
    {
        using HttpClient user = await SignInAsync("alice");

        HttpResponseMessage response = await user.PostAsJsonAsync(
            "/api/wallet/redeem", new { code = "not-a-valid-code" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("兑换码无效或已使用", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 有效兑换码加分且不能重复使用()
    {
        using HttpClient admin = await SignInAsync("admin");
        string code = await CreateBatchAndGetFirstCodeAsync(admin, amount: 50 * CreditScale, count: 1);

        HttpResponseMessage redeem = await admin.PostAsJsonAsync("/api/wallet/redeem", new { code });
        Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);

        JsonElement data = await ReadDataAsync(redeem);
        // 100 注册奖励 + 50 兑换码。
        Assert.Equal(150 * CreditScale, data.GetProperty("account").GetProperty("availableMicrocredits").GetInt64());

        // 同一码再次核销必须失败。
        HttpResponseMessage again = await admin.PostAsJsonAsync("/api/wallet/redeem", new { code });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    // ------------------------------------------------------------ 兑换码批次（管理端）

    [Fact]
    public async Task 创建批次返回明文码且大写小写均可核销()
    {
        using HttpClient admin = await SignInAsync("admin");

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/redeem-batches", new
        {
            amountMicrocredits = 20 * CreditScale,
            count = 3,
            note = "测试批次",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);

        JsonElement batch = data.GetProperty("batch");
        Assert.Equal(3, batch.GetProperty("count").GetInt64());
        Assert.Equal(20 * CreditScale, batch.GetProperty("amountMicrocredits").GetInt64());
        Assert.Equal("测试批次", batch.GetProperty("note").GetString());

        JsonElement codes = data.GetProperty("codes");
        Assert.Equal(3, codes.GetArrayLength());
        Assert.Equal(32, codes[0].GetString()!.Length);

        // 含明文码，必须禁止缓存。
        Assert.Contains("no-store", response.Headers.GetValues("Cache-Control").First(), StringComparison.Ordinal);

        // 明文码转为大写后仍可核销（Go 侧会 lower + trim）。
        string upper = codes[1].GetString()!.ToUpperInvariant();
        HttpResponseMessage redeem = await admin.PostAsJsonAsync("/api/wallet/redeem", new { code = upper });
        Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);
    }

    [Fact]
    public async Task 创建批次参数校验()
    {
        using HttpClient admin = await SignInAsync("admin");

        HttpResponseMessage zero = await admin.PostAsJsonAsync("/api/admin/redeem-batches", new
        {
            amountMicrocredits = 0,
            count = 1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);

        HttpResponseMessage tooMany = await admin.PostAsJsonAsync("/api/admin/redeem-batches", new
        {
            amountMicrocredits = 1,
            count = 5001,
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        HttpResponseMessage past = await admin.PostAsJsonAsync("/api/admin/redeem-batches", new
        {
            amountMicrocredits = 1,
            count = 1,
            expiresAt = DateTime.UtcNow.AddDays(-1),
        });
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
    }

    [Fact]
    public async Task 批次列表返回计数()
    {
        using HttpClient admin = await SignInAsync("admin");
        string code = await CreateBatchAndGetFirstCodeAsync(admin, amount: 5 * CreditScale, count: 2);
        await admin.PostAsJsonAsync("/api/wallet/redeem", new { code });

        HttpResponseMessage response = await admin.GetAsync("/api/admin/redeem-batches");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(1, data.GetProperty("total").GetInt64());

        JsonElement batch = data.GetProperty("batches")[0];
        Assert.Equal(2, batch.GetProperty("count").GetInt64());
        // 4 个计算字段必须出现（Go 用子查询别名输出）。
        Assert.Equal(1, batch.GetProperty("availableCount").GetInt64());
        Assert.Equal(1, batch.GetProperty("redeemedCount").GetInt64());
        Assert.Equal(0, batch.GetProperty("disabledCount").GetInt64());
        Assert.Equal(0, batch.GetProperty("expiredCount").GetInt64());
    }

    [Fact]
    public async Task 批次内码列表返回明文与兑换人()
    {
        using HttpClient admin = await SignInAsync("admin");
        string code = await CreateBatchAndGetFirstCodeAsync(admin, amount: 5 * CreditScale, count: 1);
        await admin.PostAsJsonAsync("/api/wallet/redeem", new { code });

        string batchId = await GetFirstBatchIdAsync(admin);
        HttpResponseMessage response = await admin.GetAsync($"/api/admin/redeem-batches/{batchId}/codes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);

        Assert.True(data.GetProperty("plaintextAvailable").GetBoolean());
        Assert.Equal(1, data.GetProperty("total").GetInt64());

        JsonElement row = data.GetProperty("codes")[0];
        Assert.Equal(code, row.GetProperty("code").GetString());
        Assert.Equal("redeemed", row.GetProperty("status").GetString());
        Assert.Equal("admin", row.GetProperty("redeemedUsername").GetString());

        // 批次密文不得出现在响应里。
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("codesCipher", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 禁用批次内全部可用码()
    {
        using HttpClient admin = await SignInAsync("admin");
        await CreateBatchAndGetFirstCodeAsync(admin, amount: 5 * CreditScale, count: 3);
        string batchId = await GetFirstBatchIdAsync(admin);

        HttpResponseMessage response = await admin.PostAsync(
            $"/api/admin/redeem-batches/{batchId}/disable", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(3, data.GetProperty("disabledCount").GetInt64());

        // 再次禁用应无可禁用码。
        HttpResponseMessage again = await admin.PostAsync(
            $"/api/admin/redeem-batches/{batchId}/disable", null);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task 禁用单个兑换码后不可核销()
    {
        using HttpClient admin = await SignInAsync("admin");
        string code = await CreateBatchAndGetFirstCodeAsync(admin, amount: 5 * CreditScale, count: 2);
        string batchId = await GetFirstBatchIdAsync(admin);
        string codeId = await GetFirstCodeIdAsync(admin, batchId);

        HttpResponseMessage response = await admin.PostAsync(
            $"/api/admin/redeem-batches/{batchId}/codes/{codeId}/disable", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 被禁用的码不能再核销。
        HttpResponseMessage redeem = await admin.PostAsJsonAsync("/api/wallet/redeem", new { code });
        Assert.Equal(HttpStatusCode.BadRequest, redeem.StatusCode);
    }

    // ------------------------------------------------------------ 调账

    [Fact]
    public async Task 调账加分与扣减()
    {
        using HttpClient admin = await SignInAsync("admin");
        User target = await CreateUserAsync("bob");

        HttpResponseMessage grant = await admin.PostAsJsonAsync(
            $"/api/admin/users/{target.ID}/credits/adjust",
            new { amountMicrocredits = 30 * CreditScale, note = "补偿" });

        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        JsonElement granted = await ReadDataAsync(grant);
        Assert.Equal(30 * CreditScale, granted.GetProperty("account").GetProperty("availableMicrocredits").GetInt64());

        HttpResponseMessage deduct = await admin.PostAsJsonAsync(
            $"/api/admin/users/{target.ID}/credits/adjust",
            new { amountMicrocredits = -10 * CreditScale, note = "回收" });

        Assert.Equal(HttpStatusCode.OK, deduct.StatusCode);
        JsonElement deducted = await ReadDataAsync(deduct);
        Assert.Equal(20 * CreditScale, deducted.GetProperty("account").GetProperty("availableMicrocredits").GetInt64());
    }

    [Fact]
    public async Task 调账余额不足返回_400()
    {
        using HttpClient admin = await SignInAsync("admin");
        User target = await CreateUserAsync("bob");
        await admin.PostAsJsonAsync(
            $"/api/admin/users/{target.ID}/credits/adjust",
            new { amountMicrocredits = 5 * CreditScale, note = "初始" });

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            $"/api/admin/users/{target.ID}/credits/adjust",
            new { amountMicrocredits = -10 * CreditScale, note = "超额扣减" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("可用积分不足", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 调账参数校验()
    {
        using HttpClient admin = await SignInAsync("admin");
        User target = await CreateUserAsync("bob");

        HttpResponseMessage zero = await admin.PostAsJsonAsync(
            $"/api/admin/users/{target.ID}/credits/adjust", new { amountMicrocredits = 0, note = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);

        HttpResponseMessage noNote = await admin.PostAsJsonAsync(
            $"/api/admin/users/{target.ID}/credits/adjust", new { amountMicrocredits = 1, note = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, noNote.StatusCode);

        HttpResponseMessage missing = await admin.PostAsJsonAsync(
            "/api/admin/users/NOT_EXIST/credits/adjust", new { amountMicrocredits = 1, note = "x" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ------------------------------------------------------------ 账单列表

    [Fact]
    public async Task 账单列表默认按待核对过滤()
    {
        using HttpClient admin = await SignInAsync("admin");

        HttpResponseMessage response = await admin.GetAsync("/api/admin/billing-orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(0, data.GetProperty("total").GetInt64());
        Assert.Equal(20, data.GetProperty("pageSize").GetInt64());
    }

    [Fact]
    public async Task 账单列表_status_all_返回全部()
    {
        using HttpClient admin = await SignInAsync("admin");
        await CreateBillingOrderAsync("order-1", BillingStatus.BillingStatusSettled);

        HttpResponseMessage response = await admin.GetAsync("/api/admin/billing-orders?status=all");

        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(1, data.GetProperty("total").GetInt64());
        Assert.Equal("order-1", data.GetProperty("orders")[0].GetProperty("id").GetString());
    }

    // ------------------------------------------------------------ 权限

    [Fact]
    public async Task 非管理员访问管理接口返回_403()
    {
        using HttpClient _ = await SignInAsync("root");
        using HttpClient bob = await SignInAsync("bob", secondUser: true);

        foreach (string path in new[]
        {
            "/api/admin/redeem-batches",
            "/api/admin/billing-orders",
        })
        {
            HttpResponseMessage response = await bob.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        HttpResponseMessage create = await bob.PostAsJsonAsync(
            "/api/admin/redeem-batches", new { amountMicrocredits = 1, count = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    // ------------------------------------------------------------ 辅助

    private async Task<HttpClient> SignInAsync(string username, bool secondUser = false)
    {
        if (secondUser)
        {
            await CreateUserAsync(username);
            HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
            {
                username,
                password = "password123",
            });
            login.EnsureSuccessStatusCode();
            return Authenticated(login);
        }

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username,
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        return Authenticated(response);
    }

    private HttpClient Authenticated(HttpResponseMessage response)
    {
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private async Task<string> CreateBatchAndGetFirstCodeAsync(HttpClient admin, long amount, int count)
    {
        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/redeem-batches", new
        {
            amountMicrocredits = amount,
            count,
        });
        response.EnsureSuccessStatusCode();

        JsonElement data = await ReadDataAsync(response);
        return data.GetProperty("codes")[0].GetString()!;
    }

    private async Task<string> GetFirstBatchIdAsync(HttpClient admin)
    {
        HttpResponseMessage response = await admin.GetAsync("/api/admin/redeem-batches");
        JsonElement data = await ReadDataAsync(response);
        return data.GetProperty("batches")[0].GetProperty("id").GetString()!;
    }

    private async Task<string> GetFirstCodeIdAsync(HttpClient admin, string batchId)
    {
        HttpResponseMessage response = await admin.GetAsync($"/api/admin/redeem-batches/{batchId}/codes");
        JsonElement data = await ReadDataAsync(response);
        return data.GetProperty("codes")[0].GetProperty("id").GetString()!;
    }

    private async Task<User> CreateUserAsync(string username)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        User user = new()
        {
            ID = IdGenerator.NewId(),
            Username = username,
            Email = $"{username}@example.com",
            DisplayName = username,
            PasswordHash = OpenAICanvas.Auth.AuthService.HashPassword("password123"),
            Role = UserRole.UserRoleUser,
            Status = UserStatus.UserStatusActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await repository.CreateAsync(user);
        return user;
    }

    private async Task CreateBillingOrderAsync(string id, string status)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        await repository.CreateAsync(new BillingOrder
        {
            ID = id,
            UserID = "user-1",
            Status = status,
            AmountMicrocredits = 10 * CreditScale,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }
}
