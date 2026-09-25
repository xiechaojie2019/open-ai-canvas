#nullable enable
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Payment;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Application;

/// <summary>
/// 内置支付渠道的清单定义。对应 Go: <c>internal/app/payment_plugins.go</c>。
/// </summary>
/// <remarks>
/// <para>
/// 官方支付渠道通过 <c>yingce.payment/v1</c> RPC 插件进程运行，包内适配器由 Go 编译。
/// </para>
/// <para>
/// 这些清单仅在官方包缺失时作为配置表单回退；没有可校验并运行的包时，渠道保持不可用。
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
            runtime: "rpc",
            icon: "brand:wechat-pay",
            checkoutMode: "qr_code",
            configuration: WeChatConfiguration()),

        Build(
            PluginAlipayPage,
            ProviderAlipay,
            name: "支付宝电脑网站支付",
            vendor: "支付宝",
            description: "支付宝 alipay.trade.page.pay 电脑网站充值适配器。",
            runtime: "rpc",
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

    internal static PaymentProviderDescriptor DescriptorFromManifest(
        Manifest manifest, ManifestPaymentProvider contribution) => new()
    {
        ID = contribution.ID,
        PluginID = manifest.Metadata.ID,
        PluginVersion = manifest.Metadata.Version,
        Name = contribution.Label,
        Icon = contribution.Icon,
        CheckoutMode = contribution.CheckoutMode,
        IdentityFields = [.. contribution.IdentityFields],
        NotificationSuccess = new NotificationResponse
        {
            Status = contribution.NotificationSuccess.Status,
            ContentType = contribution.NotificationSuccess.ContentType,
            Body = contribution.NotificationSuccess.Body,
        },
        NotificationFailure = new NotificationResponse
        {
            Status = contribution.NotificationFailure.Status,
            ContentType = contribution.NotificationFailure.ContentType,
            Body = contribution.NotificationFailure.Body,
        },
    };
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
/// 支付插件可用性按运行时插件状态与管理员平台开关判定。
/// </summary>
public sealed class RuntimePaymentPluginAvailability : IPluginAvailability
{
    private readonly PluginRuntime _runtime;
    private readonly Repository _repository;

    public RuntimePaymentPluginAvailability(PluginRuntime runtime, Repository repository)
    {
        _runtime = runtime;
        _repository = repository;
    }

    public async Task<bool> IsAvailableAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        PluginView? plugin = _runtime.List()
            .FirstOrDefault(item => string.Equals(item.Manifest.ID, pluginId.Trim(), StringComparison.Ordinal));
        if (plugin is null)
        {
            return false;
        }

        PluginPlatformState? state = await _repository
            .PluginPlatformStateAsync(plugin.Manifest.ID, cancellationToken).ConfigureAwait(false);
        return state?.Available ?? string.Equals(plugin.Status, "enabled", StringComparison.Ordinal);
    }
}
