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
/// 账单人工核对（结算 / 退款）的端到端契约测试。
/// </summary>
/// <remarks>
/// 这是资金核心路径：每个用例都同时校验<b>订单状态</b>、<b>账户余额</b>与<b>账本分录</b>
/// 三者是否一致，避免只改状态却没动钱、或动了钱却没有分录。
/// </remarks>
public sealed class BillingResolveEndpointTests : IDisposable
{
    private const long CreditScale = 1_000_000;

    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public BillingResolveEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-billing-{Guid.NewGuid():N}");
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

    // ------------------------------------------------------------ 结算：整笔

    [Fact]
    public async Task 结算整笔订单把预留转为消费()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User user = await CreateUserAsync("bob");
        await SetBalanceAsync(user.ID, available: 100 * CreditScale, reserved: 50 * CreditScale);
        await CreateOrderAsync("order-1", user.ID, amount: 50 * CreditScale, reserved: 50 * CreditScale,
            status: BillingStatus.BillingStatusUncertain);

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/order-1/resolve", new { action = "settle", note = "上游确认成功" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement order = await ReadDataAsync(response);
        Assert.Equal("settled", order.GetProperty("status").GetString());
        Assert.Equal(50 * CreditScale, order.GetProperty("actualAmountMicrocredits").GetInt64());
        // resolvedBy 存的是操作者的用户 ID（不是用户名）。
        Assert.False(string.IsNullOrEmpty(order.GetProperty("resolvedBy").GetString()));
        Assert.Equal("上游确认成功", order.GetProperty("resolutionNote").GetString());

        // 余额：可用不变（预扣转实扣），预留清零。
        (long available, long reserved) = await ReadBalanceAsync(user.ID);
        Assert.Equal(100 * CreditScale, available);
        Assert.Equal(0, reserved);

        // 账本：一条 consume，金额 -50，预留变化 -50。
        CreditLedgerEntry entry = await ReadSingleLedgerAsync(user.ID, "consume");
        Assert.Equal(-50 * CreditScale, entry.AmountMicrocredits);
        Assert.Equal(0, entry.AvailableDeltaMicrocredits);
        Assert.Equal(-50 * CreditScale, entry.ReservedDeltaMicrocredits);
    }

    [Fact]
    public async Task 结算已结算订单幂等不重复扣费()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User user = await CreateUserAsync("bob");
        await SetBalanceAsync(user.ID, available: 100 * CreditScale, reserved: 50 * CreditScale);
        await CreateOrderAsync("order-1", user.ID, amount: 50 * CreditScale, reserved: 50 * CreditScale,
            status: BillingStatus.BillingStatusRunning);

        await admin.PostAsJsonAsync("/api/admin/billing-orders/order-1/resolve",
            new { action = "settle", note = "第一次" });

        // 第二次走服务层：订单已是 settled，会先被「不需要人工核对」拦下。
        HttpResponseMessage second = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/order-1/resolve", new { action = "settle", note = "第二次" });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Contains("不需要人工核对", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 余额与账本都没被重复改动。
        (long available, long reserved) = await ReadBalanceAsync(user.ID);
        Assert.Equal(100 * CreditScale, available);
        Assert.Equal(0, reserved);
        Assert.Equal(1, await CountLedgerAsync(user.ID));
    }

    [Fact]
    public async Task 已退款订单不能结算()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User user = await CreateUserAsync("bob");
        await SetBalanceAsync(user.ID, available: 100 * CreditScale, reserved: 0);
        await CreateOrderAsync("order-1", user.ID, amount: 50 * CreditScale, reserved: 50 * CreditScale,
            status: BillingStatus.BillingStatusRefunded);

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/order-1/resolve", new { action = "settle", note = "强行结算" });

        // 状态校验先拦下：refunded 不在可核对状态集合里。
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------ 结算：token 计费

    [Fact]
    public async Task token订单按用量结算并退回差额()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User user = await CreateUserAsync("bob");
        await SetBalanceAsync(user.ID, available: 100 * CreditScale, reserved: 10);
        await CreateOrderAsync("order-1", user.ID,
            amount: 10, reserved: 10, status: BillingStatus.BillingStatusRunning,
            billingMode: "token",
            inputPrice: 10_000, outputPrice: 20_000, cachedPrice: 0,
            multiplierBps: 10_000);
        await CreateCallLogAsync("order-1", inputTokens: 100, outputTokens: 50, cachedTokens: 0);

        // base = 100*10000 + 50*20000 = 2_000_000
        // actual = (2_000_000 * 10_000 + 9_999_999_999) / 10_000_000_000 = 2
        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/order-1/resolve", new { action = "settle", note = "按用量结算" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement order = await ReadDataAsync(response);
        Assert.Equal("settled", order.GetProperty("status").GetString());
        Assert.Equal(2, order.GetProperty("actualAmountMicrocredits").GetInt64());
        Assert.Equal(8, order.GetProperty("refundedAmountMicrocredits").GetInt64());
        Assert.True(order.GetProperty("usageAvailable").GetBoolean());
        Assert.Equal(100, order.GetProperty("inputTokens").GetInt64());

        // 余额：预留清零，可用 +8（差额退回）。
        (long available, long reserved) = await ReadBalanceAsync(user.ID);
        Assert.Equal(100 * CreditScale + 8, available);
        Assert.Equal(0, reserved);

        // 账本：consume（-2，预留 -10）+ refund（+8）。
        IReadOnlyList<CreditLedgerEntry> entries = await ReadLedgerAsync(user.ID);
        Assert.Equal(2, entries.Count);
        CreditLedgerEntry consume = entries.Single(e => e.Type == "consume");
        Assert.Equal(-2, consume.AmountMicrocredits);
        Assert.Equal(-10, consume.ReservedDeltaMicrocredits);
        CreditLedgerEntry refund = entries.Single(e => e.Type == "refund");
        Assert.Equal(8, refund.AmountMicrocredits);
        Assert.Equal(8, refund.AvailableDeltaMicrocredits);
    }

    [Fact]
    public async Task token订单受硬上限截断()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User user = await CreateUserAsync("bob");
        await SetBalanceAsync(user.ID, available: 100 * CreditScale, reserved: 10);
        await CreateOrderAsync("order-1", user.ID,
            amount: 10, reserved: 10, status: BillingStatus.BillingStatusRunning,
            billingMode: "token",
            inputPrice: 10_000, outputPrice: 20_000, cachedPrice: 0,
            multiplierBps: 10_000, chargeLimit: 1);
        await CreateCallLogAsync("order-1", inputTokens: 100, outputTokens: 50, cachedTokens: 0);

        // 实际算出 2，但硬上限是 1，所以按 1 结算。
        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/order-1/resolve", new { action = "settle", note = "触顶" });

        JsonElement order = await ReadDataAsync(response);
        Assert.Equal(1, order.GetProperty("actualAmountMicrocredits").GetInt64());
        Assert.Equal(9, order.GetProperty("refundedAmountMicrocredits").GetInt64());

        // 硬上限触发时账本备注有专门文案。
        CreditLedgerEntry consume = (await ReadLedgerAsync(user.ID)).Single(e => e.Type == "consume");
        Assert.Contains("硬上限", consume.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task token订单缺少用量时结算失败()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User user = await CreateUserAsync("bob");
        await SetBalanceAsync(user.ID, available: 100 * CreditScale, reserved: 10);
        await CreateOrderAsync("order-1", user.ID,
            amount: 10, reserved: 10, status: BillingStatus.BillingStatusRunning,
            billingMode: "token",
            inputPrice: 10_000, outputPrice: 20_000, cachedPrice: 0,
            multiplierBps: 10_000);

        // 没有对应的成功调用日志 → 用量不可用。
        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/order-1/resolve", new { action = "settle", note = "无用量" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // 订单状态与余额都没变（事务回滚）。
        JsonElement order = await ReadOrderAsync("order-1");
        Assert.Equal("running", order.GetProperty("status").GetString());
        (long available, long reserved) = await ReadBalanceAsync(user.ID);
        Assert.Equal(100 * CreditScale, available);
        Assert.Equal(10, reserved);
    }

    [Fact]
    public async Task 零价token订单按整笔金额结算()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User user = await CreateUserAsync("bob");
        await SetBalanceAsync(user.ID, available: 100 * CreditScale, reserved: 10);
        await CreateOrderAsync("order-1", user.ID,
            amount: 10, reserved: 10, status: BillingStatus.BillingStatusRunning,
            billingMode: "token",
            inputPrice: 0, outputPrice: 0, cachedPrice: 0,
            multiplierBps: 10_000);

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/order-1/resolve", new { action = "settle", note = "零价" });

        // Go 的 token 分支条件是 `token && !zeroPriced`，所以零价 token 会落到
        // 「整笔预留转消费」分支，actual = AmountMicrocredits，而不是 0。
        JsonElement order = await ReadDataAsync(response);
        Assert.Equal("settled", order.GetProperty("status").GetString());
        Assert.Equal(10, order.GetProperty("actualAmountMicrocredits").GetInt64());

        (long available, long reserved) = await ReadBalanceAsync(user.ID);
        Assert.Equal(100 * CreditScale, available);
        Assert.Equal(0, reserved);
    }

    // ------------------------------------------------------------ 退款

    [Fact]
    public async Task 退款把预留退回可用余额()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User user = await CreateUserAsync("bob");
        await SetBalanceAsync(user.ID, available: 100 * CreditScale, reserved: 50 * CreditScale);
        await CreateOrderAsync("order-1", user.ID, amount: 50 * CreditScale, reserved: 50 * CreditScale,
            status: BillingStatus.BillingStatusUncertain);

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/order-1/resolve", new { action = "refund", note = "上游失败" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement order = await ReadDataAsync(response);
        Assert.Equal("refunded", order.GetProperty("status").GetString());
        Assert.Equal(50 * CreditScale, order.GetProperty("refundedAmountMicrocredits").GetInt64());
        Assert.Equal("上游失败", order.GetProperty("error").GetString());

        (long available, long reserved) = await ReadBalanceAsync(user.ID);
        Assert.Equal(150 * CreditScale, available);
        Assert.Equal(0, reserved);

        CreditLedgerEntry entry = await ReadSingleLedgerAsync(user.ID, "refund");
        Assert.Equal(50 * CreditScale, entry.AmountMicrocredits);
        Assert.Equal(-50 * CreditScale, entry.ReservedDeltaMicrocredits);
    }

    [Fact]
    public async Task 已结算订单不能直接退款()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User user = await CreateUserAsync("bob");
        await SetBalanceAsync(user.ID, available: 100 * CreditScale, reserved: 50 * CreditScale);
        await CreateOrderAsync("order-1", user.ID, amount: 50 * CreditScale, reserved: 50 * CreditScale,
            status: BillingStatus.BillingStatusRunning);

        // 先结算。
        await admin.PostAsJsonAsync("/api/admin/billing-orders/order-1/resolve",
            new { action = "settle", note = "先结算" });

        // 再退款会被状态校验拦下（settled 不在可核对集合）。
        HttpResponseMessage refund = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/order-1/resolve", new { action = "refund", note = "反悔" });
        Assert.Equal(HttpStatusCode.BadRequest, refund.StatusCode);

        // 余额保持结算后的状态。
        (long available, long reserved) = await ReadBalanceAsync(user.ID);
        Assert.Equal(100 * CreditScale, available);
        Assert.Equal(0, reserved);
    }

    // ------------------------------------------------------------ 参数与权限

    [Fact]
    public async Task 核对参数校验()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User user = await CreateUserAsync("bob");
        await SetBalanceAsync(user.ID, available: 100 * CreditScale, reserved: 50 * CreditScale);
        await CreateOrderAsync("order-1", user.ID, amount: 50 * CreditScale, reserved: 50 * CreditScale,
            status: BillingStatus.BillingStatusUncertain);

        HttpResponseMessage noNote = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/order-1/resolve", new { action = "settle", note = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, noNote.StatusCode);
        Assert.Contains("请填写核对依据", await noNote.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage badAction = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/order-1/resolve", new { action = "delete", note = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, badAction.StatusCode);
        Assert.Contains("请选择结算或退款", await badAction.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage missing = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/NOT_EXIST/resolve", new { action = "settle", note = "x" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task 批量核对返回部分失败项()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User user = await CreateUserAsync("bob");
        await SetBalanceAsync(user.ID, available: 100 * CreditScale, reserved: 100 * CreditScale);
        await CreateOrderAsync("order-ok", user.ID, amount: 50 * CreditScale, reserved: 50 * CreditScale,
            status: BillingStatus.BillingStatusUncertain);
        await CreateOrderAsync("order-bad", user.ID, amount: 50 * CreditScale, reserved: 50 * CreditScale,
            status: BillingStatus.BillingStatusSettled);

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/batch-resolve",
            new { ids = new[] { "order-ok", "order-bad" }, action = "refund", note = "批量退款" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(1, data.GetProperty("resolvedCount").GetInt32());

        JsonElement failed = data.GetProperty("failed");
        Assert.Equal(1, failed.GetArrayLength());
        Assert.Equal("order-bad", failed[0].GetProperty("id").GetString());
        Assert.Contains("不需要人工核对", failed[0].GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 批量核对参数校验()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage empty = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/batch-resolve", new { ids = Array.Empty<string>(), action = "settle", note = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("请选择要处理的计费订单", await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        string[] tooMany = Enumerable.Range(0, 101).Select(i => $"order-{i}").ToArray();
        HttpResponseMessage overLimit = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/batch-resolve", new { ids = tooMany, action = "settle", note = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, overLimit.StatusCode);
        Assert.Contains("最多处理 100 条", await overLimit.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage blank = await admin.PostAsJsonAsync(
            "/api/admin/billing-orders/batch-resolve", new { ids = new[] { " " }, action = "settle", note = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    [Fact]
    public async Task 非管理员不能核对账单()
    {
        using HttpClient _ = await SignInAsAdminAsync();
        using HttpClient bob = await SignInAsync("bob", secondUser: true);

        HttpResponseMessage single = await bob.PostAsJsonAsync(
            "/api/admin/billing-orders/order-1/resolve", new { action = "settle", note = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, single.StatusCode);

        HttpResponseMessage batch = await bob.PostAsJsonAsync(
            "/api/admin/billing-orders/batch-resolve", new { ids = new[] { "order-1" }, action = "settle", note = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, batch.StatusCode);
    }

    // ------------------------------------------------------------ 辅助

    private async Task<HttpClient> SignInAsAdminAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "admin",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        return Authenticated(response);
    }

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

    private async Task SetBalanceAsync(string userId, long available, long reserved)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        await repository.CreateAsync(new CreditAccount
        {
            UserID = userId,
            AvailableMicrocredits = available,
            ReservedMicrocredits = reserved,
            Version = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private async Task CreateOrderAsync(
        string id,
        string userId,
        long amount,
        long reserved,
        string status,
        string billingMode = "unit",
        long inputPrice = 0,
        long outputPrice = 0,
        long cachedPrice = 0,
        long multiplierBps = 10_000,
        long chargeLimit = 0)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        await repository.CreateAsync(new BillingOrder
        {
            ID = id,
            UserID = userId,
            // (user_id, idempotency_key) 有唯一索引，同一用户下不能重复。
            IdempotencyKey = id,
            Model = "test-model",
            Scene = "test",
            Capability = "text",
            BillingMode = billingMode,
            AmountMicrocredits = amount,
            ReservedAmountMicrocredits = reserved,
            ChargeLimitMicrocredits = chargeLimit,
            InputTokenPriceMicrocredits = inputPrice,
            OutputTokenPriceMicrocredits = outputPrice,
            CachedTokenPriceMicrocredits = cachedPrice,
            MultiplierBasisPoints = multiplierBps,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private async Task CreateCallLogAsync(
        string orderId, long inputTokens, long outputTokens, long cachedTokens)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        await repository.CreateAsync(new ApiCallLog
        {
            ID = IdGenerator.NewId(),
            BillingOrderID = orderId,
            UserID = "user-1",
            Status = ApiCallStatus.ApiCallStatusSucceeded,
            UsageAvailable = true,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CachedTokens = cachedTokens,
            CreatedAt = DateTime.UtcNow,
        });
    }

    private async Task<(long Available, long Reserved)> ReadBalanceAsync(string userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        CreditAccount account = await repository.CreditAccountAsync(userId)
            ?? throw new InvalidOperationException("account missing");
        return (account.AvailableMicrocredits, account.ReservedMicrocredits);
    }

    private async Task<IReadOnlyList<CreditLedgerEntry>> ReadLedgerAsync(string userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        (IReadOnlyList<CreditLedgerEntry> entries, _) = await repository.CreditLedgerAsync(userId, "", 100, 0);
        return entries;
    }

    private async Task<CreditLedgerEntry> ReadSingleLedgerAsync(string userId, string type)
    {
        IReadOnlyList<CreditLedgerEntry> entries = await ReadLedgerAsync(userId);
        return entries.Single(e => e.Type == type);
    }

    private async Task<long> CountLedgerAsync(string userId) => (await ReadLedgerAsync(userId)).Count;

    private async Task<JsonElement> ReadOrderAsync(string id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        BillingOrder order = await repository.BillingOrderAsync(id)
            ?? throw new InvalidOperationException("order missing");
        return JsonSerializer.SerializeToElement(order);
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }
}
