#nullable enable
using OpenAICanvas.Application;
using OpenAICanvas.Payment;

namespace OpenAICanvas.Tests.Payment;

/// <summary>
/// 进程内测试适配器：让支付订单全流程可以在没有真实渠道的情况下跑通。
/// </summary>
/// <remarks>
/// 生产环境的适配器注册表是空的（内置微信/支付宝适配器尚未移植），
/// 所以测试必须自己注入一个。行为通过公开字段控制，便于构造各种分支。
/// </remarks>
public sealed class FakePaymentProvider : IPaymentProvider
{
    /// <summary>下单时抛出的异常（非 null 时生效）。</summary>
    public Exception? CreateOrderError { get; set; }

    /// <summary>查单返回的结果；为 null 时返回「未支付」。</summary>
    public PaymentResult? QueryResult { get; set; }

    /// <summary>查单抛出的异常（非 null 时生效）。</summary>
    public Exception? QueryError { get; set; }

    /// <summary>关单返回的结果。</summary>
    public PaymentResult? CloseResult { get; set; }

    /// <summary>回调验签返回的通知；为 null 时抛验签失败。</summary>
    public PaymentNotification? Notification { get; set; }

    /// <summary>记录收到的下单请求，供断言回调 URL 等。</summary>
    public List<CreateRequest> CreateRequests { get; } = [];

    /// <summary>记录收到的关单请求。</summary>
    public List<CloseRequest> CloseRequests { get; } = [];

    /// <summary>下载账单返回的记录。</summary>
    public List<BillRecord> BillRecords { get; set; } = [];

    /// <summary>下载账单抛出的异常（非 null 时生效）。</summary>
    public Exception? DownloadBillError { get; set; }

    /// <remarks>
    /// ID 必须与内置清单里的渠道一致——管理端的配置字段定义来自清单，
    /// 用清单外的 ID 会在「保存配置」时报「支付插件清单不存在」。
    /// </remarks>
    public PaymentProviderDescriptor Descriptor { get; init; } = new()
    {
        ID = "alipay-page-pay",
        PluginID = "official-payment-alipay-page",
        PluginVersion = "1.0.0",
        Name = "支付宝电脑网站支付",
        Icon = "brand:alipay",
        CheckoutMode = "redirect",
        IdentityFields = ["appId", "sellerId"],
        NotificationSuccess = new NotificationResponse
        {
            Status = 200,
            ContentType = "text/plain; charset=utf-8",
            Body = "success",
        },
        NotificationFailure = new NotificationResponse
        {
            Status = 400,
            ContentType = "text/plain; charset=utf-8",
            Body = "failure",
        },
    };

    public Task ValidateConfigAsync(Dictionary<string, string> config, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<Checkout> CreateOrderAsync(
        Dictionary<string, string> config, CreateRequest request, CancellationToken cancellationToken = default)
    {
        CreateRequests.Add(request);

        if (CreateOrderError is not null)
        {
            throw CreateOrderError;
        }

        return Task.FromResult(new Checkout
        {
            Mode = Descriptor.CheckoutMode,
            Value = "https://pay.test/checkout/" + request.MerchantOrderNo,
            ExpiresAt = request.ExpiresAt,
        });
    }

    public Task<PaymentResult> QueryOrderAsync(
        Dictionary<string, string> config, QueryRequest request, CancellationToken cancellationToken = default)
    {
        if (QueryError is not null)
        {
            throw QueryError;
        }

        return Task.FromResult(QueryResult ?? new PaymentResult
        {
            MerchantOrderNo = request.MerchantOrderNo,
            ProviderStatus = "WAITING",
            Paid = false,
            Closed = false,
        });
    }

    public Task<PaymentResult> CloseOrderAsync(
        Dictionary<string, string> config, CloseRequest request, CancellationToken cancellationToken = default)
    {
        CloseRequests.Add(request);

        return Task.FromResult(CloseResult ?? new PaymentResult
        {
            MerchantOrderNo = request.MerchantOrderNo,
            ProviderStatus = "CLOSED",
            Paid = false,
            Closed = true,
        });
    }

    public Task<PaymentNotification> VerifyNotificationAsync(
        Dictionary<string, string> config,
        IReadOnlyDictionary<string, string[]> headers,
        byte[] rawBody,
        CancellationToken cancellationToken = default)
    {
        if (Notification is null)
        {
            throw new PaymentProviderException("verify_failed", "验签失败");
        }

        return Task.FromResult(Notification);
    }

    public Task<List<BillRecord>> DownloadTradeBillAsync(
        Dictionary<string, string> config, DateTime billDate, CancellationToken cancellationToken = default)
    {
        if (DownloadBillError is not null)
        {
            throw DownloadBillError;
        }

        return Task.FromResult(BillRecords);
    }
}

/// <summary>测试用插件可用性：全部可用。</summary>
public sealed class AlwaysAvailablePluginAvailability : IPluginAvailability
{
    public Task<bool> IsAvailableAsync(string pluginId, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
