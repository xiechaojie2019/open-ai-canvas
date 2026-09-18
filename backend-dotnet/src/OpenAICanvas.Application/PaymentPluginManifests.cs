#nullable enable
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Application;

/// <summary>
/// 内置支付渠道的清单定义。对应 Go: <c>internal/app/payment_plugins.go</c>。
/// </summary>
/// <remarks>
/// <para>
/// 这两个渠道的 <c>Runtime.Backend</c> 是 <c>host:xxx</c>，表示<b>宿主内置适配器</b>
/// （与 <c>plugin-packages/</c> 里 <c>runtime.backend = "rpc"</c> 的独立进程插件不同）。
/// </para>
/// <para>
/// <b>注意</b>：宿主侧的适配器实现本身尚未移植（按用户指示暂缓），
/// 所以 <c>PaymentRegistry</c> 目前是空的——<c>GET /payments/providers</c> 返回空数组，
/// 下单会报「未知支付渠道」。清单先落地，是为了管理端能展示配置表单、
/// 以及后续接入适配器时不必再改契约。
/// </para>
/// </remarks>
public static class PaymentPluginManifests
{
    public const string PluginWeChatNative = "official-payment-wechat-native";
    public const string PluginAlipayPage = "official-payment-alipay-page";
    public const string ProviderWeChat = "wechat-native";
    public const string ProviderAlipay = "alipay-page-pay";

    /// <summary>内置渠道清单（微信 Native、支付宝网页）。对应 Go: <c>bundledPaymentPluginManifests</c>。</summary>
    public static List<PaymentPluginManifest> Bundled() =>
    [
        Build(
            PluginWeChatNative,
            ProviderWeChat,
            name: "微信支付 Native",
            vendor: "微信支付",
            description: "微信支付 Native 扫码充值适配器。",
            runtime: "host:wechatpay-v3-native",
            icon: "brand:wechat-pay",
            checkoutMode: "qr_code",
            configuration: WeChatConfiguration()),

        Build(
            PluginAlipayPage,
            ProviderAlipay,
            name: "支付宝电脑网站支付",
            vendor: "支付宝",
            description: "支付宝 alipay.trade.page.pay 电脑网站充值适配器。",
            runtime: "host:alipay-page-pay",
            icon: "brand:alipay",
            checkoutMode: "redirect",
            configuration: AlipayConfiguration()),
    ];

    /// <summary>按渠道 ID 找清单。对应 Go: <c>paymentManifestForProvider</c>。</summary>
    public static PaymentPluginManifest? ForProvider(string providerId) =>
        Bundled().FirstOrDefault(manifest =>
            string.Equals(manifest.Contribution.ID, providerId.Trim(), StringComparison.Ordinal));

    private static PaymentPluginManifest Build(
        string pluginId,
        string providerId,
        string name,
        string vendor,
        string description,
        string runtime,
        string icon,
        string checkoutMode,
        List<ManifestField> configuration) => new()
    {
        PluginID = pluginId,
        PluginVersion = "1.0.0",
        Name = name,
        Vendor = vendor,
        Description = description,
        Runtime = runtime,
        // 内置清单默认禁用，需要管理员配置并启用后才对用户可见。
        Enabled = false,
        Installable = true,
        ConfigFields = configuration,
        Contribution = new ManifestPaymentProvider
        {
            ID = providerId,
            Label = name,
            Icon = icon,
            CheckoutMode = checkoutMode,
            IdentityFields = IdentityFields(providerId),
            NotificationSuccess = NotificationSuccess(providerId),
            NotificationFailure = NotificationFailure(providerId),
            ExpiryPolicy = new ManifestPaymentExpiryPolicy
            {
                DefaultMinutes = 30,
                MinMinutes = 5,
                MaxMinutes = 1440,
            },
        },
    };

    /// <summary>身份字段：用于识别「同一商户的另一套配置」。</summary>
    private static List<string> IdentityFields(string providerId) => providerId switch
    {
        ProviderWeChat => ["appId", "mchId"],
        ProviderAlipay => ["appId", "sellerId"],
        _ => [],
    };

    private static ManifestPaymentResponse NotificationSuccess(string providerId) =>
        providerId == ProviderAlipay
            ? new ManifestPaymentResponse { Status = 200, ContentType = "text/plain; charset=utf-8", Body = "success" }
            : new ManifestPaymentResponse { Status = 204 };

    private static ManifestPaymentResponse NotificationFailure(string providerId) =>
        providerId == ProviderAlipay
            ? new ManifestPaymentResponse { Status = 400, ContentType = "text/plain; charset=utf-8", Body = "failure" }
            : new ManifestPaymentResponse { Status = 400 };

    private static List<ManifestField> WeChatConfiguration() =>
    [
        new() { Name = "publicBaseUrl", Type = "url", Label = "服务器公网地址", Required = true, Description = "用于生成微信支付回调地址，必须可被微信访问。" },
        new() { Name = "appId", Type = "string", Label = "AppID", Required = true },
        new() { Name = "mchId", Type = "string", Label = "商户号", Required = true },
        new() { Name = "merchantSerialNo", Type = "string", Label = "商户证书序列号", Required = true },
        new() { Name = "merchantPrivateKey", Type = "textarea", Label = "商户 API 私钥", Required = true, Secret = true },
        new() { Name = "apiV3Key", Type = "password", Label = "APIv3 密钥", Required = true, Secret = true },
        new() { Name = "wechatPayPublicKeyId", Type = "string", Label = "微信支付公钥 ID", Required = true },
        new() { Name = "wechatPayPublicKey", Type = "textarea", Label = "微信支付公钥", Required = true, Secret = true },
    ];

    private static List<ManifestField> AlipayConfiguration() =>
    [
        new() { Name = "publicBaseUrl", Type = "url", Label = "服务器公网地址", Required = true, Description = "用于生成支付宝异步通知和同步返回地址。" },
        new() { Name = "appId", Type = "string", Label = "应用 AppID", Required = true },
        new() { Name = "sellerId", Type = "string", Label = "支付宝商户 PID", Required = true },
        new() { Name = "merchantPrivateKey", Type = "textarea", Label = "应用私钥", Required = true, Secret = true },
        new() { Name = "alipayPublicKey", Type = "textarea", Label = "支付宝公钥", Required = true, Secret = true },
        new() { Name = "gateway", Type = "url", Label = "支付宝网关", Required = true, Default = "https://openapi.alipay.com/gateway.do" },
    ];
}

/// <summary>
/// 内置支付渠道清单的精简投影（只含支付用到的部分）。
/// 完整 <c>Manifest</c> 属于阶段 10 的插件系统。
/// </summary>
public sealed class PaymentPluginManifest
{
    public string PluginID { get; init; } = "";
    public string PluginVersion { get; init; } = "";
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Description { get; init; } = "";
    public string Runtime { get; init; } = "";
    public bool Enabled { get; init; }
    public bool Installable { get; init; }
    public List<ManifestField> ConfigFields { get; init; } = [];
    public required ManifestPaymentProvider Contribution { get; init; }
}


/// <summary>
/// 默认的插件可用性实现：按内置清单的 <c>Enabled</c> 判定。
/// </summary>
/// <remarks>
/// 插件系统（阶段 10）尚未移植，所以这里退化为「读内置清单的启用位」。
/// 内置清单默认 <c>Enabled=false</c>，与 Go 在插件未启用时的行为一致。
/// 接入真实插件系统后替换本实现即可，调用方无需改动。
/// </remarks>
public sealed class ManifestPluginAvailability : IPluginAvailability
{
    public Task<bool> IsAvailableAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PaymentPluginManifest? manifest = PaymentPluginManifests.Bundled()
            .FirstOrDefault(item => string.Equals(item.PluginID, pluginId.Trim(), StringComparison.Ordinal));

        return Task.FromResult(manifest?.Enabled ?? false);
    }
}
