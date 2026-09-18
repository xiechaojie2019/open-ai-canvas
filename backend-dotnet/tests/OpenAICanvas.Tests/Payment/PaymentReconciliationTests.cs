#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Payment;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;
using Xunit;

namespace OpenAICanvas.Tests.Payment;

/// <summary>
/// 支付对账的端到端契约测试。
/// </summary>
/// <remarks>
/// 对账最关键的约束是：<b>下载到的账单只是线索，补发积分前必须经签名查单确认</b>。
/// 测试里通过让「查单结果」与「账单记录」不一致来验证这条防线。
/// </remarks>
public sealed class PaymentReconciliationTests : IDisposable
{
    private const long CreditScale = 1_000_000;

    /// <summary>渠道账单时区（与实现一致）。</summary>
    private static readonly TimeSpan BillZone = TimeSpan.FromHours(8);

    private readonly string _dataDir;
    private readonly FakePaymentProvider _provider = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    /// <summary>账单日：两天前（一定早于「今天」，且在过去三个月内）。</summary>
    private readonly string _billDate;

    /// <summary>账单日正午对应的 UTC 时刻，用于让本地已入账订单落进对账窗口。</summary>
    private readonly DateTime _billDateNoonUtc;

    public PaymentReconciliationTests()
    {
        _billDate = DateTimeOffset.UtcNow.ToOffset(BillZone).AddDays(-2).ToString("yyyy-MM-dd");
        _billDateNoonUtc = DateTimeOffset
            .ParseExact(_billDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            .ToOffset(BillZone)
            .AddHours(12)
            .UtcDateTime;

        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-recon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CANVAS_BACKEND_DATA_DIR", _dataDir);
            builder.UseSetting("CANVAS_DATABASE_DRIVER", "sqlite");
            builder.UseSetting("CANVAS_AUTO_MIGRATE", "true");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<CanvasService>();
                services.AddSingleton(serviceProvider => new CanvasService(
                    serviceProvider.GetRequiredService<Repository>(),
                    runtimePolicy: serviceProvider.GetRequiredService<IRuntimePolicyProvider>(),
                    authHost: serviceProvider.GetRequiredService<CanvasAuthHost>(),
                    paymentRegistry: new PaymentRegistry(_provider),
                    pluginAvailability: new AlwaysAvailablePluginAvailability()));
            });
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
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------ 参数与权限

    [Fact]
    public async Task 非管理员不能对账()
    {
        using HttpClient _ = await SignInAsAdminAsync();
        using HttpClient bob = await SignInAsync("bob");

        HttpResponseMessage response = await bob.PostAsJsonAsync("/api/admin/payments/reconciliations", new
        {
            providerId = "alipay-page-pay",
            billDate = _billDate,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task 对账日期格式与范围校验()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await ConfigureProviderAsync(admin);

        // 格式非法
        HttpResponseMessage bad = await admin.PostAsJsonAsync("/api/admin/payments/reconciliations", new
        {
            providerId = "alipay-page-pay",
            billDate = "2026/09/16",
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("账单日期格式必须为", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 今天（不允许）
        string today = DateTimeOffset.UtcNow.ToOffset(BillZone).ToString("yyyy-MM-dd");
        HttpResponseMessage current = await admin.PostAsJsonAsync("/api/admin/payments/reconciliations", new
        {
            providerId = "alipay-page-pay",
            billDate = today,
        });
        Assert.Equal(HttpStatusCode.BadRequest, current.StatusCode);
        Assert.Contains("只能对账昨天及更早", await current.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 超过三个月
        string tooOld = DateTimeOffset.UtcNow.ToOffset(BillZone).AddMonths(-4).ToString("yyyy-MM-dd");
        HttpResponseMessage old = await admin.PostAsJsonAsync("/api/admin/payments/reconciliations", new
        {
            providerId = "alipay-page-pay",
            billDate = tooOld,
        });
        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        Assert.Contains("最近三个月", await old.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未知渠道与未配置渠道被拒()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage unknown = await admin.PostAsJsonAsync("/api/admin/payments/reconciliations", new
        {
            providerId = "not-a-provider",
            billDate = _billDate,
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("未知支付渠道", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage unconfigured = await admin.PostAsJsonAsync("/api/admin/payments/reconciliations", new
        {
            providerId = "alipay-page-pay",
            billDate = _billDate,
        });
        Assert.Equal(HttpStatusCode.BadRequest, unconfigured.StatusCode);
        Assert.Contains("尚未配置", await unconfigured.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 比对结果

    [Fact]
    public async Task 账单与本地都无记录时对账成功且无明细()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await ConfigureProviderAsync(admin);

        _provider.BillRecords = [];
        JsonElement run = await RunReconciliationAsync(admin);

        Assert.Equal("completed", run.GetProperty("status").GetString());
        Assert.Equal(0, run.GetProperty("totalItems").GetInt64());
        Assert.Equal(0, run.GetProperty("matchItems").GetInt64());
        Assert.Equal(0, run.GetProperty("errorItems").GetInt64());
    }

    [Fact]
    public async Task 本地已入账且账单有记录时记为_matched()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await ConfigureProviderAsync(admin);
        await CreateCreditedOrderAsync(admin, merchantOrderNo: "M-MATCH", amountFen: 1000, tradeNo: "T-1");

        _provider.BillRecords =
        [
            new BillRecord
            {
                MerchantOrderNo = "M-MATCH",
                ProviderTradeNo = "T-1",
                ProviderStatus = "TRADE_SUCCESS",
                AmountFen = 1000,
                Currency = "CNY",
                PaidAt = _billDateNoonUtc,
            },
        ];

        JsonElement run = await RunReconciliationAsync(admin);

        Assert.Equal("completed", run.GetProperty("status").GetString());
        Assert.Equal(1, run.GetProperty("matchItems").GetInt64());
        Assert.Equal(0, run.GetProperty("errorItems").GetInt64());

        JsonElement item = await FirstItemAsync(admin, run.GetProperty("id").GetString()!);
        Assert.Equal("matched", item.GetProperty("result").GetString());
        Assert.True(item.GetProperty("resolved").GetBoolean());
    }

    [Fact]
    public async Task 账单有记录但本地无订单时记为_local_order_not_found()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await ConfigureProviderAsync(admin);

        _provider.BillRecords =
        [
            new BillRecord
            {
                MerchantOrderNo = "M-GHOST",
                ProviderTradeNo = "T-GHOST",
                ProviderStatus = "TRADE_SUCCESS",
                AmountFen = 1000,
                Currency = "CNY",
                PaidAt = _billDateNoonUtc,
            },
        ];

        JsonElement run = await RunReconciliationAsync(admin);

        Assert.Equal(1, run.GetProperty("errorItems").GetInt64());

        JsonElement item = await FirstItemAsync(admin, run.GetProperty("id").GetString()!);
        Assert.Equal("local_order_not_found", item.GetProperty("result").GetString());
        Assert.Contains("本地订单不存在", item.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 金额不一致记为_amount_mismatch()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await ConfigureProviderAsync(admin);
        await CreateCreditedOrderAsync(admin, merchantOrderNo: "M-AMT", amountFen: 1000, tradeNo: "T-2");

        _provider.BillRecords =
        [
            new BillRecord
            {
                MerchantOrderNo = "M-AMT",
                ProviderTradeNo = "T-2",
                ProviderStatus = "TRADE_SUCCESS",
                AmountFen = 999,
                Currency = "CNY",
                PaidAt = _billDateNoonUtc,
            },
        ];

        JsonElement run = await RunReconciliationAsync(admin);

        Assert.Equal(1, run.GetProperty("errorItems").GetInt64());

        JsonElement item = await FirstItemAsync(admin, run.GetProperty("id").GetString()!);
        Assert.Equal("amount_mismatch", item.GetProperty("result").GetString());
    }

    [Fact]
    public async Task 本地已入账但账单缺失记为_provider_record_missing()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await ConfigureProviderAsync(admin);
        await CreateCreditedOrderAsync(admin, merchantOrderNo: "M-LOCAL", amountFen: 1000, tradeNo: "T-3");

        _provider.BillRecords = [];

        JsonElement run = await RunReconciliationAsync(admin);

        Assert.Equal(1, run.GetProperty("errorItems").GetInt64());

        JsonElement item = await FirstItemAsync(admin, run.GetProperty("id").GetString()!);
        Assert.Equal("provider_record_missing", item.GetProperty("result").GetString());
    }

    // ------------------------------------------------------------ 补发与安全防线

    [Fact]
    public async Task 账单确认已支付且签名查单一致时自动补发()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await ConfigureProviderAsync(admin);
        User bob = await CreateUserAsync("bob");

        // 本地有一笔未入账的待支付订单。
        string merchantOrderNo = await CreatePendingOrderAsync(admin, bob.ID, amountFen: 1000, credits: 20 * CreditScale);

        // 账单说已支付。
        _provider.BillRecords =
        [
            new BillRecord
            {
                MerchantOrderNo = merchantOrderNo,
                ProviderTradeNo = "T-RECOVER",
                ProviderStatus = "TRADE_SUCCESS",
                AmountFen = 1000,
                Currency = "CNY",
                PaidAt = _billDateNoonUtc,
            },
        ];

        // 签名查单也确认已支付。
        _provider.QueryResult = new PaymentResult
        {
            MerchantOrderNo = merchantOrderNo,
            ProviderTradeNo = "T-RECOVER",
            ProviderStatus = "TRADE_SUCCESS",
            AmountFen = 1000,
            Currency = "CNY",
            Paid = true,
            PaidAt = _billDateNoonUtc,
        };

        JsonElement run = await RunReconciliationAsync(admin);

        Assert.Equal(1, run.GetProperty("recoveredItems").GetInt64());

        JsonElement item = await FirstItemAsync(admin, run.GetProperty("id").GetString()!);
        Assert.Equal("recovered", item.GetProperty("result").GetString());

        // 积分已补发。
        IReadOnlyList<CreditLedgerEntry> entries = await ReadLedgerAsync(bob.ID);
        Assert.Single(entries.Where(entry => entry.Type == "payment_topup"));
    }

    [Fact]
    public async Task 账单说已支付但签名查单未确认时不补发()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await ConfigureProviderAsync(admin);
        User bob = await CreateUserAsync("bob");
        string merchantOrderNo = await CreatePendingOrderAsync(admin, bob.ID, amountFen: 1000, credits: 20 * CreditScale);

        _provider.BillRecords =
        [
            new BillRecord
            {
                MerchantOrderNo = merchantOrderNo,
                ProviderTradeNo = "T-FAKE",
                ProviderStatus = "TRADE_SUCCESS",
                AmountFen = 1000,
                Currency = "CNY",
                PaidAt = _billDateNoonUtc,
            },
        ];

        // 查单返回未支付 —— 伪造账单不能换来积分。
        _provider.QueryResult = new PaymentResult
        {
            MerchantOrderNo = merchantOrderNo,
            ProviderTradeNo = "",
            ProviderStatus = "WAITING",
            AmountFen = 1000,
            Currency = "CNY",
            Paid = false,
        };

        JsonElement run = await RunReconciliationAsync(admin);

        Assert.Equal(1, run.GetProperty("errorItems").GetInt64());

        JsonElement item = await FirstItemAsync(admin, run.GetProperty("id").GetString()!);
        Assert.Equal("credit_failed", item.GetProperty("result").GetString());
        Assert.Contains("签名查单未确认", item.GetProperty("detail").GetString()!, StringComparison.Ordinal);

        // 没有产生任何入账记录。
        IReadOnlyList<CreditLedgerEntry> entries = await ReadLedgerAsync(bob.ID);
        Assert.Empty(entries.Where(entry => entry.Type == "payment_topup"));
    }

    // ------------------------------------------------------------ 列表与明细

    [Fact]
    public async Task 对账运行列表与明细分页()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await ConfigureProviderAsync(admin);

        _provider.BillRecords =
        [
            new BillRecord
            {
                MerchantOrderNo = "M-LIST",
                ProviderTradeNo = "T-LIST",
                ProviderStatus = "TRADE_SUCCESS",
                AmountFen = 1000,
                Currency = "CNY",
                PaidAt = _billDateNoonUtc,
            },
        ];

        JsonElement run = await RunReconciliationAsync(admin);
        string runId = run.GetProperty("id").GetString()!;

        HttpResponseMessage list = await admin.GetAsync("/api/admin/payments/reconciliations");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        JsonElement listData = await ReadDataAsync(list);
        Assert.Equal(1, listData.GetProperty("total").GetInt64());
        Assert.Equal("alipay-page-pay", listData.GetProperty("runs")[0].GetProperty("providerId").GetString());

        HttpResponseMessage items = await admin.GetAsync($"/api/admin/payments/reconciliations/{runId}/items");
        Assert.Equal(HttpStatusCode.OK, items.StatusCode);
        JsonElement itemsData = await ReadDataAsync(items);
        Assert.Equal(1, itemsData.GetProperty("total").GetInt64());
        Assert.Equal(runId, itemsData.GetProperty("run").GetProperty("id").GetString());

        // 按结果过滤。
        HttpResponseMessage filtered = await admin.GetAsync(
            $"/api/admin/payments/reconciliations/{runId}/items?result=matched");
        JsonElement filteredData = await ReadDataAsync(filtered);
        Assert.Equal(0, filteredData.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task 明细分页参数校验()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/admin/payments/reconciliations?pageSize=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    private async Task<HttpClient> SignInAsync(string username)
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

    private async Task ConfigureProviderAsync(HttpClient admin)
    {
        HttpResponseMessage response = await admin.PutAsJsonAsync(
            "/api/admin/payments/providers/alipay-page-pay/config", new
            {
                enabled = true,
                closeAfterMinutes = 30,
                values = new Dictionary<string, string>
                {
                    ["publicBaseUrl"] = "https://example.com",
                    ["appId"] = "app-1",
                    ["sellerId"] = "seller-1",
                    ["merchantPrivateKey"] = "private-key",
                    ["alipayPublicKey"] = "public-key",
                },
            });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>直接落库一笔已入账订单（账单日内）。</summary>
    private async Task CreateCreditedOrderAsync(
        HttpClient admin, string merchantOrderNo, long amountFen, string tradeNo)
    {
        string userId = await CurrentUserIdAsync(admin);

        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        await repository.CreateAsync(new PaymentOrder
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            IdempotencyKey = merchantOrderNo,
            MerchantOrderNo = merchantOrderNo,
            ProviderID = "alipay-page-pay",
            PluginID = "official-payment-alipay-page",
            AmountFen = amountFen,
            Currency = "CNY",
            CreditsMicrocredits = 10 * CreditScale,
            Status = PaymentOrderStatus.PaymentOrderCredited,
            ProviderTradeNo = tradeNo,
            ProviderPaidAt = _billDateNoonUtc,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    /// <summary>落库一笔未入账的待支付订单，并返回商户单号。</summary>
    private async Task<string> CreatePendingOrderAsync(
        HttpClient admin, string userId, long amountFen, long credits)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        string merchantOrderNo = "M-" + Guid.NewGuid().ToString("N")[..16];
        await repository.CreateAsync(new PaymentOrder
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            IdempotencyKey = merchantOrderNo,
            MerchantOrderNo = merchantOrderNo,
            ProviderID = "alipay-page-pay",
            PluginID = "official-payment-alipay-page",
            AmountFen = amountFen,
            Currency = "CNY",
            CreditsMicrocredits = credits,
            Status = PaymentOrderStatus.PaymentOrderPending,
            // 让订单落进账单日窗口。
            CreatedAt = _billDateNoonUtc,
            UpdatedAt = _billDateNoonUtc,
            ExpiresAt = _billDateNoonUtc.AddHours(1),
        });
        return merchantOrderNo;
    }

    private async Task<JsonElement> RunReconciliationAsync(HttpClient admin)
    {
        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/payments/reconciliations", new
        {
            providerId = "alipay-page-pay",
            billDate = _billDate,
        });
        response.EnsureSuccessStatusCode();
        return (await ReadDataAsync(response)).GetProperty("run");
    }

    private async Task<JsonElement> FirstItemAsync(HttpClient admin, string runId)
    {
        HttpResponseMessage response = await admin.GetAsync($"/api/admin/payments/reconciliations/{runId}/items");
        JsonElement data = await ReadDataAsync(response);
        return data.GetProperty("items")[0];
    }

    private async Task<string> CurrentUserIdAsync(HttpClient client)
    {
        JsonElement data = await ReadDataAsync(await client.GetAsync("/api/auth/session"));
        return data.GetProperty("user").GetProperty("id").GetString()!;
    }

    private async Task<IReadOnlyList<CreditLedgerEntry>> ReadLedgerAsync(string userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        (IReadOnlyList<CreditLedgerEntry> entries, _) = await repository.CreditLedgerAsync(userId, "", 100, 0);
        return entries;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }
}
