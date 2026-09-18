#nullable enable
namespace OpenAICanvas.Payment;

/// <summary>
/// 支付渠道适配器。对应 Go: <c>paymentsdk.Provider</c>。
/// </summary>
/// <remarks>
/// 宿主始终掌握订单、凭证存储、网络策略与入账事务；适配器只负责翻译
/// 某一家支付渠道的协议。实现可以是进程内实现，也可以是
/// <see cref="RpcPaymentProvider"/> 这样的独立进程桥接。
/// </remarks>
public interface IPaymentProvider
{
    /// <summary>描述符（渠道标识、展示名、收银台模式等）。</summary>
    PaymentProviderDescriptor Descriptor { get; }

    /// <summary>校验配置（密钥、商户号等是否可用）。对应 Go: <c>ValidateConfig</c>。</summary>
    Task ValidateConfigAsync(Dictionary<string, string> config, CancellationToken cancellationToken = default);

    /// <summary>下单并返回收银台跳转信息。对应 Go: <c>CreateOrder</c>。</summary>
    Task<Checkout> CreateOrderAsync(
        Dictionary<string, string> config, CreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>查询订单。对应 Go: <c>QueryOrder</c>。</summary>
    Task<PaymentResult> QueryOrderAsync(
        Dictionary<string, string> config, QueryRequest request, CancellationToken cancellationToken = default);

    /// <summary>关闭订单。对应 Go: <c>CloseOrder</c>。</summary>
    Task<PaymentResult> CloseOrderAsync(
        Dictionary<string, string> config, CloseRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 校验支付渠道回调并解析出通知内容。对应 Go: <c>VerifyNotification</c>。
    /// </summary>
    Task<PaymentNotification> VerifyNotificationAsync(
        Dictionary<string, string> config,
        IReadOnlyDictionary<string, string[]> headers,
        byte[] rawBody,
        CancellationToken cancellationToken = default);

    /// <summary>下载对账账单。对应 Go: <c>DownloadTradeBill</c>。</summary>
    Task<List<BillRecord>> DownloadTradeBillAsync(
        Dictionary<string, string> config, DateTime billDate, CancellationToken cancellationToken = default);
}

/// <summary>
/// 支付适配器注册表。对应 Go: <c>internal/payment/registry.go</c>。
/// </summary>
public sealed class PaymentRegistry
{
    private readonly Dictionary<string, IPaymentProvider> _providers;

    /// <summary>
    /// 构造注册表。ID 为空或重复都会直接失败——支付渠道标识重复会让下单走错适配器，
    /// 属于必须在启动期暴露的配置错误。
    /// </summary>
    public PaymentRegistry(params IPaymentProvider[] providers)
    {
        _providers = new Dictionary<string, IPaymentProvider>(providers.Length, StringComparer.Ordinal);
        foreach (IPaymentProvider provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider, "payment provider is nil");

            string id = provider.Descriptor.ID.Trim();
            if (id.Length == 0)
            {
                throw new ArgumentException("payment provider id is empty");
            }

            if (!_providers.TryAdd(id, provider))
            {
                throw new ArgumentException($"duplicate payment provider \"{id}\"");
            }
        }
    }

    /// <summary>按 ID 取适配器。对应 Go: <c>Get</c>。</summary>
    public IPaymentProvider? Get(string id) =>
        _providers.TryGetValue(id.Trim(), out IPaymentProvider? provider) ? provider : null;

    /// <summary>全部描述符（按 ID 升序）。对应 Go: <c>Descriptors</c>。</summary>
    public List<PaymentProviderDescriptor> Descriptors() =>
        _providers.Values
            .Select(provider => provider.Descriptor)
            .OrderBy(descriptor => descriptor.ID, StringComparer.Ordinal)
            .ToList();

    /// <summary>全部适配器。对应 Go: <c>Providers</c>。</summary>
    public IReadOnlyCollection<IPaymentProvider> Providers() => _providers.Values;
}
