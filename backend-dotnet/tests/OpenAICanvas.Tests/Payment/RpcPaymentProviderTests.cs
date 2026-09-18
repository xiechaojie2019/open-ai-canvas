#nullable enable
using System.Text.Json;
using OpenAICanvas.Payment;
using Xunit;

namespace OpenAICanvas.Tests.Payment;

/// <summary>
/// 支付插件进程宿主（<c>yingce.payment/v1</c>）的端到端测试。
/// </summary>
/// <remarks>
/// 这些用例会<b>真实启动子进程</b>并通过 stdin/stdout 交换 JSON，
/// 不是 mock。测试桩见 <c>tests/OpenAICanvas.PaymentTestPlugin</c>。
/// </remarks>
public sealed class RpcPaymentProviderTests : IDisposable
{
    private readonly string _packageDir;

    public RpcPaymentProviderTests()
    {
        _packageDir = Path.Combine(Path.GetTempPath(), $"pay-pkg-{Guid.NewGuid():N}");
        string backendDir = Path.Combine(_packageDir, "backend");
        Directory.CreateDirectory(backendDir);

        // 把测试桩的输出搬到 <pkg>/backend/，模拟真实插件包布局。
        string source = Path.Combine(AppContext.BaseDirectory, "payment-test-plugin");
        Assert.True(Directory.Exists(source), $"测试桩未复制到输出目录：{source}");

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(backendDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);

            // Linux 下 apphost 需要执行位，否则会被宿主判为 plugin_permission_denied。
            if (!OperatingSystem.IsWindows() && !relative.Contains('.'))
            {
                File.SetUnixFileMode(destination,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_packageDir))
        {
            try
            {
                Directory.Delete(_packageDir, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 子进程句柄可能尚未释放；临时目录由系统回收。
            }
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------ 正常路径

    [Fact]
    public async Task 校验配置成功()
    {
        RpcPaymentProvider provider = CreateProvider();

        await provider.ValidateConfigAsync(Config("ok"));
    }

    [Fact]
    public async Task 下单返回收银台信息()
    {
        RpcPaymentProvider provider = CreateProvider();

        Checkout checkout = await provider.CreateOrderAsync(Config("ok"), new CreateRequest
        {
            MerchantOrderNo = "M-1001",
            Description = "充值",
            AmountFen = 1990,
            Currency = "CNY",
            ExpiresAt = new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc),
            NotifyURL = "https://example.com/notify",
            ReturnURL = "https://example.com/return",
        });

        // Go 的 Checkout 没有 json tag，所以字段是 PascalCase。
        Assert.Equal("redirect", checkout.Mode);
        Assert.Equal("https://pay.example.com/checkout?id=abc", checkout.Value);
        Assert.Equal(new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc), checkout.ExpiresAt);
    }

    [Fact]
    public async Task 查询订单返回结果()
    {
        RpcPaymentProvider provider = CreateProvider();

        PaymentResult result = await provider.QueryOrderAsync(
            Config("ok"), new QueryRequest { MerchantOrderNo = "M-1001" });

        // Go 的 Result 带 json tag，所以字段是 camelCase。
        Assert.Equal("M-1001", result.MerchantOrderNo);
        Assert.Equal("T-2002", result.ProviderTradeNo);
        Assert.Equal("TRADE_SUCCESS", result.ProviderStatus);
        Assert.Equal(1990, result.AmountFen);
        Assert.Equal("CNY", result.Currency);
        Assert.True(result.Paid);
        Assert.False(result.Closed);
        Assert.Equal(new DateTime(2026, 9, 18, 9, 30, 0, DateTimeKind.Utc), result.PaidAt);
    }

    [Fact]
    public async Task 关单返回结果()
    {
        RpcPaymentProvider provider = CreateProvider();

        PaymentResult result = await provider.CloseOrderAsync(
            Config("ok"), new CloseRequest { MerchantOrderNo = "M-1001" });

        Assert.Equal("M-1001", result.MerchantOrderNo);
    }

    [Fact]
    public async Task 校验回调返回通知()
    {
        RpcPaymentProvider provider = CreateProvider();

        byte[] body = "out_trade_no=M-1001&total_amount=19.90"u8.ToArray();
        Dictionary<string, string[]> headers = new(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/x-www-form-urlencoded"],
        };

        PaymentNotification notification = await provider.VerifyNotificationAsync(Config("ok"), headers, body);

        Assert.Equal("evt-1", notification.EventID);
        // 嵌入的 Result 字段必须平铺在同一层。
        Assert.Equal("M-1001", notification.MerchantOrderNo);
        Assert.Equal("T-2002", notification.ProviderTradeNo);
        Assert.Equal(1990, notification.AmountFen);
        Assert.True(notification.Paid);
    }

    [Fact]
    public async Task 下载对账账单返回列表()
    {
        RpcPaymentProvider provider = CreateProvider();

        List<BillRecord> records = await provider.DownloadTradeBillAsync(
            Config("ok"), new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc));

        BillRecord record = Assert.Single(records);
        // Go 的 BillRecord 没有 json tag，字段是 PascalCase。
        Assert.Equal("M-1001", record.MerchantOrderNo);
        Assert.Equal("T-2002", record.ProviderTradeNo);
        Assert.Equal(1990, record.AmountFen);
        Assert.Equal(new DateTime(2026, 9, 18, 9, 30, 0, DateTimeKind.Utc), record.PaidAt);
    }

    // ------------------------------------------------------------ 请求编码

    [Fact]
    public async Task 请求字段名与协议一致()
    {
        RpcPaymentProvider provider = CreateProvider();

        // 测试桩回显 request 子对象的字段名，用来断言宿主发出的 JSON 与 Go 一致。
        // Go 的 QueryRequest 没有 json tag，所以 key 必须是 PascalCase。
        PaymentResult echoed = await provider.QueryOrderAsync(
            Config("echo-request"), new QueryRequest { MerchantOrderNo = "M-1001" });

        Assert.Equal("MerchantOrderNo", echoed.MerchantOrderNo);
    }

    // ------------------------------------------------------------ 错误分支

    [Fact]
    public async Task 插件返回失败时抛出结构化错误()
    {
        RpcPaymentProvider provider = CreateProvider();

        PaymentProviderException error = await Assert.ThrowsAsync<PaymentProviderException>(
            () => provider.QueryOrderAsync(Config("error"), new QueryRequest { MerchantOrderNo = "M-1" }));

        Assert.Equal("provider_error", error.Code);
        Assert.Equal("渠道拒绝该请求", error.Message);
        Assert.False(error.Temporary);
    }

    [Fact]
    public async Task 插件自定义错误码原样透传()
    {
        RpcPaymentProvider provider = CreateProvider();

        PaymentProviderException error = await Assert.ThrowsAsync<PaymentProviderException>(
            () => provider.QueryOrderAsync(Config("error-custom-code"), new QueryRequest { MerchantOrderNo = "M-1" }));

        Assert.Equal("trade_not_exist", error.Code);
        Assert.Equal("交易不存在", error.Message);
    }

    [Fact]
    public async Task 插件只给错误码时消息回落默认文案()
    {
        RpcPaymentProvider provider = CreateProvider();

        PaymentProviderException error = await Assert.ThrowsAsync<PaymentProviderException>(
            () => provider.QueryOrderAsync(Config("error-no-message"), new QueryRequest { MerchantOrderNo = "M-1" }));

        Assert.Equal("silent_failure", error.Code);
        Assert.Equal("支付插件返回失败", error.Message);
    }

    [Fact]
    public async Task 无效响应报plugin_invalid_response()
    {
        RpcPaymentProvider provider = CreateProvider();

        PaymentProviderException error = await Assert.ThrowsAsync<PaymentProviderException>(
            () => provider.QueryOrderAsync(Config("bad-json"), new QueryRequest { MerchantOrderNo = "M-1" }));

        Assert.Equal("plugin_invalid_response", error.Code);
        Assert.True(error.Temporary);
    }

    [Fact]
    public async Task 空响应报plugin_invalid_response()
    {
        RpcPaymentProvider provider = CreateProvider();

        PaymentProviderException error = await Assert.ThrowsAsync<PaymentProviderException>(
            () => provider.QueryOrderAsync(Config("empty"), new QueryRequest { MerchantOrderNo = "M-1" }));

        Assert.Equal("plugin_invalid_response", error.Code);
    }

    [Fact]
    public async Task 进程非零退出报plugin_process_failed()
    {
        RpcPaymentProvider provider = CreateProvider();

        PaymentProviderException error = await Assert.ThrowsAsync<PaymentProviderException>(
            () => provider.QueryOrderAsync(Config("exit-nonzero"), new QueryRequest { MerchantOrderNo = "M-1" }));

        Assert.Equal("plugin_process_failed", error.Code);
        Assert.True(error.Temporary);
    }

    [Fact]
    public async Task 响应超过上限被拒绝()
    {
        RpcPaymentProvider provider = CreateProvider();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.QueryOrderAsync(Config("huge"), new QueryRequest { MerchantOrderNo = "M-1" }));

        Assert.Contains("超过安全限制", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 启动期校验

    [Fact]
    public async Task 可执行文件缺失报plugin_executable_missing()
    {
        RpcPaymentProvider provider = new(Descriptor(), _packageDir, "backend/does-not-exist");

        PaymentProviderException error = await Assert.ThrowsAsync<PaymentProviderException>(
            () => provider.ValidateConfigAsync(Config("ok")));

        Assert.Equal("plugin_executable_missing", error.Code);
    }

    [Theory]
    [InlineData("/absolute/path")]
    [InlineData("../escape")]
    [InlineData("backend/../escape")]
    [InlineData("other/provider")]
    public void 非法入口路径被拒绝(string entry)
    {
        Assert.Throws<ArgumentException>(() => new RpcPaymentProvider(Descriptor(), _packageDir, entry));
    }

    [Fact]
    public void 描述符缺少ID被拒绝()
    {
        PaymentProviderDescriptor descriptor = Descriptor();
        descriptor.ID = "";

        Assert.Throws<ArgumentException>(() => new RpcPaymentProvider(descriptor, _packageDir, "backend/provider"));
    }

    // ------------------------------------------------------------ 注册表

    [Fact]
    public void 注册表拒绝重复ID()
    {
        RpcPaymentProvider first = CreateProvider();
        RpcPaymentProvider second = CreateProvider();

        ArgumentException error = Assert.Throws<ArgumentException>(() => new PaymentRegistry(first, second));
        Assert.Contains("duplicate payment provider", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 注册表按ID排序描述符()
    {
        RpcPaymentProvider beta = CreateProvider(id: "beta");
        RpcPaymentProvider alpha = CreateProvider(id: "alpha");

        PaymentRegistry registry = new(beta, alpha);

        List<PaymentProviderDescriptor> descriptors = registry.Descriptors();
        Assert.Equal(2, descriptors.Count);
        Assert.Equal("alpha", descriptors[0].ID);
        Assert.Equal("beta", descriptors[1].ID);
    }

    [Fact]
    public void 注册表按ID取适配器()
    {
        RpcPaymentProvider provider = CreateProvider(id: "alipay");

        PaymentRegistry registry = new(provider);

        Assert.Same(provider, registry.Get("alipay"));
        Assert.Same(provider, registry.Get("  alipay  "));
        Assert.Null(registry.Get("wechat"));
    }

    [Fact]
    public void 空注册表返回空列表()
    {
        PaymentRegistry registry = new();

        Assert.Empty(registry.Descriptors());
        Assert.Null(registry.Get("anything"));
    }

    // ------------------------------------------------------------ 辅助

    private RpcPaymentProvider CreateProvider(string id = "test-provider") =>
        new(Descriptor(id), _packageDir, EntryPath());

    /// <summary>
    /// 插件可执行文件名：Windows 是 apphost <c>.exe</c>，其他平台无扩展名。
    /// </summary>
    private static string EntryPath() =>
        OperatingSystem.IsWindows()
            ? "backend/OpenAICanvas.PaymentTestPlugin.exe"
            : "backend/OpenAICanvas.PaymentTestPlugin";

    private static PaymentProviderDescriptor Descriptor(string id = "test-provider") => new()
    {
        ID = id,
        PluginID = "official-payment-test",
        PluginVersion = "1.0.0",
        Name = "测试支付",
        Icon = "brand:test",
        CheckoutMode = "redirect",
        IdentityFields = ["appId"],
        NotificationSuccess = new NotificationResponse { Status = 200, ContentType = "text/plain", Body = "success" },
        NotificationFailure = new NotificationResponse { Status = 400, ContentType = "text/plain", Body = "fail" },
    };

    private static Dictionary<string, string> Config(string scenario) =>
        new(StringComparer.Ordinal) { ["scenario"] = scenario };
}
