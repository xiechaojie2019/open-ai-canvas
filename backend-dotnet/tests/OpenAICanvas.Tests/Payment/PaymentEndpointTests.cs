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
// 协议通知与实体同名：这里用的是协议类型（构造渠道回调载荷）。
using PaymentNotification = OpenAICanvas.Payment.PaymentNotification;

namespace OpenAICanvas.Tests.Payment;

/// <summary>
/// 支付路由的端到端契约测试。
/// </summary>
/// <remarks>
/// 生产环境的支付适配器注册表是空的，所以这里替换 <see cref="CanvasService"/>，
/// 注入一个进程内的测试适配器，让下单 / 查单 / 关单 / 回调全流程可验证。
/// </remarks>
public sealed class PaymentEndpointTests : IDisposable
{
    private const long CreditScale = 1_000_000;

    private readonly string _dataDir;
    private readonly FakePaymentProvider _provider = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public PaymentEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-pay-{Guid.NewGuid():N}");
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

    // ------------------------------------------------------------ 权限

    [Fact]
    public async Task 未登录访问支付接口返回_401()
    {
        foreach (string path in new[] { "/api/payments/providers", "/api/payments/products" })
        {
            HttpResponseMessage response = await _client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task 非管理员访问管理端支付接口返回_403()
    {
        using HttpClient _ = await SignInAsAdminAsync();
        using HttpClient bob = await SignInAsync("bob", secondUser: true);

        foreach (string path in new[] { "/api/admin/payments/providers", "/api/admin/payments/products" })
        {
            HttpResponseMessage response = await bob.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    // ------------------------------------------------------------ 商品

    [Fact]
    public async Task 管理员创建商品后用户可见()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage create = await admin.PostAsJsonAsync("/api/admin/payments/products", new
        {
            name = "100 积分",
            description = "测试商品",
            amountFen = 1000,
            creditsMicrocredits = 100 * CreditScale,
            enabled = true,
            sortOrder = 1,
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        HttpResponseMessage list = await admin.GetAsync("/api/payments/products");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        JsonElement data = await ReadDataAsync(list);
        JsonElement product = data.GetProperty("products")[0];
        Assert.Equal("100 积分", product.GetProperty("name").GetString());
        Assert.Equal(1000, product.GetProperty("amountFen").GetInt64());
        Assert.Equal(100 * CreditScale, product.GetProperty("creditsMicrocredits").GetInt64());
    }

    [Fact]
    public async Task 商品参数校验()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 名称为空
        HttpResponseMessage blankName = await admin.PostAsJsonAsync("/api/admin/payments/products", new
        {
            name = "  ",
            amountFen = 1000,
            creditsMicrocredits = 100 * CreditScale,
        });
        Assert.Equal(HttpStatusCode.BadRequest, blankName.StatusCode);

        // 金额超限（> 100 万元）
        HttpResponseMessage tooLarge = await admin.PostAsJsonAsync("/api/admin/payments/products", new
        {
            name = "超大",
            amountFen = 100_000_001,
            creditsMicrocredits = 100 * CreditScale,
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);

        // 积分超限（> 10 亿积分）
        HttpResponseMessage tooManyCredits = await admin.PostAsJsonAsync("/api/admin/payments/products", new
        {
            name = "超多",
            amountFen = 1000,
            creditsMicrocredits = 1_000_000_001L * CreditScale,
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooManyCredits.StatusCode);
    }

    [Fact]
    public async Task 停用商品不出现在用户列表()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await CreateProductAsync(admin, enabled: false);

        HttpResponseMessage list = await admin.GetAsync("/api/payments/products");

        JsonElement data = await ReadDataAsync(list);
        Assert.Equal(0, data.GetProperty("products").GetArrayLength());

        // 管理端仍能看到。
        HttpResponseMessage adminList = await admin.GetAsync("/api/admin/payments/products");
        JsonElement adminData = await ReadDataAsync(adminList);
        Assert.Equal(1, adminData.GetProperty("products").GetArrayLength());
    }

    // ------------------------------------------------------------ 渠道配置

    [Fact]
    public async Task 未配置渠道时用户看不到且不能下单()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await CreateProductAsync(admin);

        HttpResponseMessage providers = await admin.GetAsync("/api/payments/providers");
        JsonElement providersData = await ReadDataAsync(providers);
        Assert.Equal(0, providersData.GetProperty("providers").GetArrayLength());

        HttpResponseMessage order = await admin.PostAsJsonAsync("/api/payments/orders", new
        {
            productId = await FirstProductIdAsync(admin),
            providerId = "alipay-page-pay",
            idempotencyKey = "key-1",
        });
        Assert.Equal(HttpStatusCode.Forbidden, order.StatusCode);
        Assert.Contains("未启用或尚未配置", await order.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 配置渠道后可见并能下单()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string productId = await CreateProductAsync(admin);
        await ConfigureProviderAsync(admin);

        HttpResponseMessage providers = await admin.GetAsync("/api/payments/providers");
        JsonElement providersData = await ReadDataAsync(providers);
        JsonElement provider = providersData.GetProperty("providers")[0];
        Assert.Equal("alipay-page-pay", provider.GetProperty("id").GetString());
        Assert.True(provider.GetProperty("enabled").GetBoolean());
        Assert.True(provider.GetProperty("configured").GetBoolean());

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/payments/orders", new
        {
            productId,
            providerId = "alipay-page-pay",
            idempotencyKey = "key-1",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        JsonElement order = data.GetProperty("order");
        Assert.Equal("pending", order.GetProperty("status").GetString());
        Assert.Equal("redirect", order.GetProperty("checkout").GetProperty("mode").GetString());
        Assert.Contains("/checkout", order.GetProperty("checkout").GetProperty("url").GetString()!, StringComparison.Ordinal);

        // 下单时拼接的回调地址必须带渠道与配置 ID。
        CreateRequest sent = Assert.Single(_provider.CreateRequests);
        Assert.Contains("/api/payments/notify/alipay-page-pay/", sent.NotifyURL, StringComparison.Ordinal);
        Assert.Contains("/api/payments/return/alipay-page-pay?orderId=", sent.ReturnURL, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 下单幂等同键返回同一订单()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string productId = await CreateProductAsync(admin);
        await ConfigureProviderAsync(admin);

        object payload = new { productId, providerId = "alipay-page-pay", idempotencyKey = "key-1" };
        JsonElement first = await ReadDataAsync(await admin.PostAsJsonAsync("/api/payments/orders", payload));
        JsonElement second = await ReadDataAsync(await admin.PostAsJsonAsync("/api/payments/orders", payload));

        Assert.Equal(
            first.GetProperty("order").GetProperty("id").GetString(),
            second.GetProperty("order").GetProperty("id").GetString());

        // 渠道只被调用一次。
        Assert.Single(_provider.CreateRequests);
    }

    [Fact]
    public async Task 下单幂等键复用给不同商品返回_409()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string productId = await CreateProductAsync(admin);
        await ConfigureProviderAsync(admin);

        await admin.PostAsJsonAsync("/api/payments/orders", new
        {
            productId,
            providerId = "alipay-page-pay",
            idempotencyKey = "key-1",
        });

        HttpResponseMessage conflict = await admin.PostAsJsonAsync("/api/payments/orders", new
        {
            productId = "another-product",
            providerId = "alipay-page-pay",
            idempotencyKey = "key-1",
        });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    // ------------------------------------------------------------ 入账

    [Fact]
    public async Task 查单确认支付后入账()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string productId = await CreateProductAsync(admin, credits: 50 * CreditScale);
        await ConfigureProviderAsync(admin);
        JsonElement order = await CreateOrderAsync(admin, productId);
        string merchantOrderNo = order.GetProperty("merchantOrderNo").GetString()!;
        long amountFen = order.GetProperty("amountFen").GetInt64();

        // 渠道侧变为已支付。
        _provider.QueryResult = new PaymentResult
        {
            MerchantOrderNo = merchantOrderNo,
            ProviderTradeNo = "TRADE-1",
            ProviderStatus = "TRADE_SUCCESS",
            AmountFen = amountFen,
            Currency = "CNY",
            Paid = true,
            PaidAt = DateTime.UtcNow,
        };

        HttpResponseMessage response = await admin.PostAsync(
            $"/api/payments/orders/{order.GetProperty("id").GetString()}/query", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement updated = (await ReadDataAsync(response)).GetProperty("order");
        Assert.Equal("credited", updated.GetProperty("status").GetString());
        Assert.Equal("TRADE-1", updated.GetProperty("providerTradeNo").GetString());

        // 余额增加，且账本有一条 payment_topup。
        (long available, _) = await ReadBalanceAsync(await CurrentUserIdAsync(admin));
        Assert.True(available >= 50 * CreditScale);

        IReadOnlyList<CreditLedgerEntry> entries = await ReadLedgerAsync(await CurrentUserIdAsync(admin));
        CreditLedgerEntry topup = entries.Single(entry => entry.Type == "payment_topup");
        Assert.Equal(50 * CreditScale, topup.AmountMicrocredits);
        Assert.Equal("payment:alipay-page-pay:" + merchantOrderNo, topup.ReferenceKey);
    }

    [Fact]
    public async Task 查单金额不匹配拒绝入账()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string productId = await CreateProductAsync(admin);
        await ConfigureProviderAsync(admin);
        JsonElement order = await CreateOrderAsync(admin, productId);

        // 渠道回传的金额与订单不一致。
        _provider.QueryResult = new PaymentResult
        {
            MerchantOrderNo = order.GetProperty("merchantOrderNo").GetString() ?? "",
            ProviderTradeNo = "TRADE-1",
            ProviderStatus = "TRADE_SUCCESS",
            AmountFen = 999_999,
            Currency = "CNY",
            Paid = true,
        };

        HttpResponseMessage response = await admin.PostAsync(
            $"/api/payments/orders/{order.GetProperty("id").GetString()}/query", null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // 订单状态没变。
        HttpResponseMessage reread = await admin.GetAsync(
            $"/api/payments/orders/{order.GetProperty("id").GetString()}");
        JsonElement current = (await ReadDataAsync(reread)).GetProperty("order");
        Assert.NotEqual("credited", current.GetProperty("status").GetString());
    }

    [Fact]
    public async Task 回调入账且重复回调幂等()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string productId = await CreateProductAsync(admin, credits: 30 * CreditScale);
        await ConfigureProviderAsync(admin);
        JsonElement order = await CreateOrderAsync(admin, productId);

        _provider.Notification = new PaymentNotification
        {
            EventID = "evt-1",
            MerchantOrderNo = order.GetProperty("merchantOrderNo").GetString() ?? "",
            ProviderTradeNo = "TRADE-9",
            ProviderStatus = "TRADE_SUCCESS",
            AmountFen = order.GetProperty("amountFen").GetInt64(),
            Currency = "CNY",
            Paid = true,
            PaidAt = DateTime.UtcNow,
        };

        string notifyPath = $"/api/payments/notify/alipay-page-pay/{await CurrentConfigIdAsync(admin)}";
        HttpResponseMessage first = await _client.PostAsync(
            notifyPath, new StringContent("raw-body", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));

        // 适配器自定义了成功应答体（这里是 success）。
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("success", await first.Content.ReadAsStringAsync());

        string userId = await CurrentUserIdAsync(admin);
        IReadOnlyList<CreditLedgerEntry> afterFirst = await ReadLedgerAsync(userId);
        Assert.Single(afterFirst.Where(entry => entry.Type == "payment_topup"));

        // 重复回调不应重复入账。
        HttpResponseMessage second = await _client.PostAsync(
            notifyPath, new StringContent("raw-body", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        IReadOnlyList<CreditLedgerEntry> afterSecond = await ReadLedgerAsync(userId);
        Assert.Single(afterSecond.Where(entry => entry.Type == "payment_topup"));
    }

    [Fact]
    public async Task 回调验签失败返回渠道失败应答()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await ConfigureProviderAsync(admin);

        // 未设置 Notification → 适配器抛验签失败。
        HttpResponseMessage response = await _client.PostAsync(
            $"/api/payments/notify/alipay-page-pay/{await CurrentConfigIdAsync(admin)}",
            new StringContent("bad", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("failure", await response.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------ 收银台与关单

    [Fact]
    public async Task 收银台重定向到渠道地址()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string productId = await CreateProductAsync(admin);
        await ConfigureProviderAsync(admin);
        JsonElement order = await CreateOrderAsync(admin, productId);

        HttpResponseMessage response = await admin.GetAsync(
            $"/api/payments/orders/{order.GetProperty("id").GetString()}/checkout");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://pay.test/checkout/", response.Headers.Location?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 关单后状态为_closed()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string productId = await CreateProductAsync(admin);
        await ConfigureProviderAsync(admin);
        JsonElement order = await CreateOrderAsync(admin, productId);

        HttpResponseMessage response = await admin.PostAsync(
            $"/api/payments/orders/{order.GetProperty("id").GetString()}/close", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement closed = (await ReadDataAsync(response)).GetProperty("order");
        Assert.Equal("closed", closed.GetProperty("status").GetString());
        Assert.Single(_provider.CloseRequests);
    }

    [Fact]
    public async Task 返回页参数非法时重定向到_invalid()
    {
        HttpResponseMessage bad = await _client.GetAsync("/api/payments/return/alipay-page-pay?orderId=oops");
        Assert.Equal(HttpStatusCode.Redirect, bad.StatusCode);
        Assert.Equal("/wallet?payment=invalid", bad.Headers.Location?.ToString());

        string validId = new string('a', 32);
        HttpResponseMessage good = await _client.GetAsync($"/api/payments/return/alipay-page-pay?orderId={validId}");
        Assert.Equal(HttpStatusCode.Redirect, good.StatusCode);
        Assert.Equal($"/wallet?paymentOrder={validId}", good.Headers.Location?.ToString());
    }

    // ------------------------------------------------------------ 管理端

    [Fact]
    public async Task 管理端订单列表含用户信息()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string productId = await CreateProductAsync(admin);
        await ConfigureProviderAsync(admin);
        await CreateOrderAsync(admin, productId);

        HttpResponseMessage response = await admin.GetAsync("/api/admin/payments/orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(1, data.GetProperty("total").GetInt64());

        JsonElement row = data.GetProperty("orders")[0];
        Assert.Equal("admin", row.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal("pending", row.GetProperty("status").GetString());
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
            await using var scope = _factory.Services.CreateAsyncScope();
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            await repository.CreateAsync(new User
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
            });
        }

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

    private async Task<string> CreateProductAsync(HttpClient admin, bool enabled = true, long credits = 100 * CreditScale)
    {
        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/payments/products", new
        {
            name = "测试商品",
            amountFen = 1000,
            creditsMicrocredits = credits,
            enabled,
            sortOrder = 1,
        });
        response.EnsureSuccessStatusCode();

        JsonElement data = await ReadDataAsync(response);
        return data.GetProperty("product").GetProperty("id").GetString()!;
    }

    private async Task ConfigureProviderAsync(HttpClient admin)
    {
        HttpResponseMessage response = await admin.PutAsJsonAsync("/api/admin/payments/providers/alipay-page-pay/config", new
        {
            enabled = true,
            closeAfterMinutes = 30,
            // 支付宝渠道的必填字段（gateway 有默认值，可不传）。
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

    private async Task<string> FirstProductIdAsync(HttpClient admin)
    {
        HttpResponseMessage response = await admin.GetAsync("/api/admin/payments/products");
        JsonElement data = await ReadDataAsync(response);
        return data.GetProperty("products")[0].GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> CreateOrderAsync(HttpClient admin, string productId)
    {
        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/payments/orders", new
        {
            productId,
            providerId = "alipay-page-pay",
            idempotencyKey = "key-" + Guid.NewGuid().ToString("N"),
        });
        response.EnsureSuccessStatusCode();
        return (await ReadDataAsync(response)).GetProperty("order");
    }

    private async Task<string> CurrentUserIdAsync(HttpClient client)
    {
        JsonElement data = await ReadDataAsync(await client.GetAsync("/api/auth/session"));
        return data.GetProperty("user").GetProperty("id").GetString()!;
    }

    private async Task<string> CurrentConfigIdAsync(HttpClient admin)
    {
        JsonElement data = await ReadDataAsync(await admin.GetAsync("/api/admin/payments/providers"));
        return data.GetProperty("providers")[0].GetProperty("configId").GetString()!;
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

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }
}
