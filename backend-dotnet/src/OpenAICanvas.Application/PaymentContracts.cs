#nullable enable
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Application;

/// <summary>用户可见的支付渠道。对应 Go: <c>app.PaymentProviderView</c>。</summary>
public sealed class PaymentProviderView
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("pluginId")]
    public string PluginID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "";

    [JsonPropertyName("checkoutMode")]
    public string CheckoutMode { get; init; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("pluginEnabled")]
    public bool PluginEnabled { get; set; }

    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("closeAfterMinutes")]
    public long CloseAfterMinutes { get; set; }
}

/// <summary>
/// 管理端支付渠道视图。对应 Go: <c>app.AdminPaymentProviderView</c>。
/// Go 用结构体嵌入 <see cref="PaymentProviderView"/>，JSON 平铺，C# 显式展开。
/// </summary>
public sealed class AdminPaymentProviderView
{
    // ---- 嵌入的 PaymentProviderView 字段 ----

    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("pluginId")]
    public string PluginID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "";

    [JsonPropertyName("checkoutMode")]
    public string CheckoutMode { get; init; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("pluginEnabled")]
    public bool PluginEnabled { get; init; }

    [JsonPropertyName("configured")]
    public bool Configured { get; init; }

    [JsonPropertyName("closeAfterMinutes")]
    public long CloseAfterMinutes { get; init; }

    // ---- 管理端附加字段 ----

    [JsonPropertyName("configId")]
    [GoOmitEmpty]
    public string ConfigID { get; init; } = "";

    [JsonPropertyName("configEnabled")]
    public bool ConfigEnabled { get; init; }

    [JsonPropertyName("version")]
    public long Version { get; init; }

    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("secretConfigured")]
    public Dictionary<string, bool> SecretConfigured { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("configFields")]
    public List<ManifestField> ConfigFields { get; init; } = [];

    [JsonPropertyName("updatedAt")]
    [GoOmitEmpty]
    public DateTime? UpdatedAt { get; init; }
}

/// <summary>更新渠道配置请求。对应 Go: <c>app.UpdatePaymentProviderConfigRequest</c>。</summary>
public sealed class UpdatePaymentProviderConfigRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("closeAfterMinutes")]
    public int CloseAfterMinutes { get; set; }

    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>充值商品请求。对应 Go: <c>app.TopupProductRequest</c>。</summary>
public sealed class TopupProductRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("amountFen")]
    public long AmountFen { get; set; }

    [JsonPropertyName("creditsMicrocredits")]
    public long CreditsMicrocredits { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }
}

/// <summary>创建支付订单请求。对应 Go: <c>app.CreatePaymentOrderRequest</c>。</summary>
public sealed class CreatePaymentOrderRequest
{
    [JsonPropertyName("productId")]
    public string ProductID { get; set; } = "";

    [JsonPropertyName("providerId")]
    public string ProviderID { get; set; } = "";

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; set; } = "";
}

/// <summary>收银台信息。对应 Go: <c>app.PaymentCheckoutView</c>。</summary>
public sealed class PaymentCheckoutView
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "";

    [JsonPropertyName("value")]
    [GoOmitEmpty]
    public string Value { get; init; } = "";

    [JsonPropertyName("url")]
    [GoOmitEmpty]
    public string URL { get; init; } = "";

    [JsonPropertyName("expiresAt")]
    [GoOmitEmpty]
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>支付订单视图。对应 Go: <c>app.PaymentOrderView</c>。</summary>
public sealed class PaymentOrderView
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("userId")]
    [GoOmitEmpty]
    public string UserID { get; init; } = "";

    [JsonPropertyName("merchantOrderNo")]
    public string MerchantOrderNo { get; init; } = "";

    [JsonPropertyName("productId")]
    public string ProductID { get; init; } = "";

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = "";

    [JsonPropertyName("providerId")]
    public string ProviderID { get; init; } = "";

    [JsonPropertyName("amountFen")]
    public long AmountFen { get; init; }

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "";

    [JsonPropertyName("creditsMicrocredits")]
    public long CreditsMicrocredits { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("providerStatus")]
    [GoOmitEmpty]
    public string ProviderStatus { get; init; } = "";

    [JsonPropertyName("providerTradeNo")]
    [GoOmitEmpty]
    public string ProviderTradeNo { get; init; } = "";

    [JsonPropertyName("checkout")]
    public PaymentCheckoutView Checkout { get; init; } = new();

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; init; }

    [JsonPropertyName("providerPaidAt")]
    [GoOmitEmpty]
    public DateTime? ProviderPaidAt { get; init; }

    [JsonPropertyName("creditedAt")]
    [GoOmitEmpty]
    public DateTime? CreditedAt { get; init; }

    [JsonPropertyName("closedAt")]
    [GoOmitEmpty]
    public DateTime? ClosedAt { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }
}

/// <summary>订单关联用户。对应 Go: <c>app.AdminPaymentOrderUser</c>。</summary>
public sealed class AdminPaymentOrderUser
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("username")]
    public string Username { get; init; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = "";

    [JsonPropertyName("email")]
    public string Email { get; init; } = "";
}

/// <summary>管理端订单视图。对应 Go: <c>app.AdminPaymentOrderView</c>。</summary>
public sealed class AdminPaymentOrderView
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("userId")]
    [GoOmitEmpty]
    public string UserID { get; init; } = "";

    [JsonPropertyName("merchantOrderNo")]
    public string MerchantOrderNo { get; init; } = "";

    [JsonPropertyName("productId")]
    public string ProductID { get; init; } = "";

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = "";

    [JsonPropertyName("providerId")]
    public string ProviderID { get; init; } = "";

    [JsonPropertyName("amountFen")]
    public long AmountFen { get; init; }

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "";

    [JsonPropertyName("creditsMicrocredits")]
    public long CreditsMicrocredits { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("providerStatus")]
    [GoOmitEmpty]
    public string ProviderStatus { get; init; } = "";

    [JsonPropertyName("providerTradeNo")]
    [GoOmitEmpty]
    public string ProviderTradeNo { get; init; } = "";

    [JsonPropertyName("checkout")]
    public PaymentCheckoutView Checkout { get; init; } = new();

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; init; }

    [JsonPropertyName("providerPaidAt")]
    [GoOmitEmpty]
    public DateTime? ProviderPaidAt { get; init; }

    [JsonPropertyName("creditedAt")]
    [GoOmitEmpty]
    public DateTime? CreditedAt { get; init; }

    [JsonPropertyName("closedAt")]
    [GoOmitEmpty]
    public DateTime? ClosedAt { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    /// <summary>关联用户。Go 里是指针，无值时输出 <c>null</c>。</summary>
    [JsonPropertyName("user")]
    public AdminPaymentOrderUser? User { get; init; }
}

/// <summary>管理端订单分页。对应 Go: <c>app.AdminPaymentOrderPage</c>。</summary>
public sealed class AdminPaymentOrderPage
{
    [JsonPropertyName("orders")]
    public required IReadOnlyList<AdminPaymentOrderView> Orders { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }
}

/// <summary>
/// 插件启用状态查询。对应 Go 的 <c>pluginStateForUser</c>。
/// </summary>
/// <remarks>
/// 插件系统（阶段 10）尚未移植，所以这里抽成接口：
/// 默认实现按内嵌清单的 <c>Enabled</c> 判定（内置清单默认禁用），
/// 测试可注入「全部可用」的实现来打通订单流程。
/// </remarks>
public interface IPluginAvailability
{
    Task<bool> IsAvailableAsync(string pluginId, CancellationToken cancellationToken = default);
}
