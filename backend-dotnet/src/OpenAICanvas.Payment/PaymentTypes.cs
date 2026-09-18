#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Payment;

/// <summary>
/// 支付插件协议类型。对应 Go: <c>payment-sdk/types.go</c>。
/// </summary>
/// <remarks>
/// <b>JSON 命名必须逐字对齐 Go</b>，这里有个容易踩的坑：
/// Go 侧一部分 struct 带 json tag（输出 camelCase），另一部分<b>没有 tag</b>
/// （<c>encoding/json</c> 会原样使用字段名，即 PascalCase）。
/// 两类混在同一个协议里，必须逐个核对，不能统一套 camelCase。
/// </remarks>
public static class PaymentJson
{
    /// <summary>
    /// RPC 协议序列化配置。字段名全部由显式 <see cref="JsonPropertyNameAttribute"/> 指定，
    /// 所以这里不设命名策略（避免与 Go 的 PascalCase 字段冲突）。
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = true,
            DictionaryKeyPolicy = null,
            WriteIndented = false,
            // 与 Go 的 json.Marshal 一致：不忽略零值，是否省略由字段上的 GoOmitEmpty 决定。
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = GoJsonEncoder.Instance,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };

        options.Converters.Add(new GoTimeConverter());
        return options;
    }
}

/// <summary>插件描述符。对应 Go: <c>paymentsdk.Descriptor</c>（全部字段带 tag）。</summary>
public sealed class PaymentProviderDescriptor
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("pluginId")]
    public string PluginID { get; set; } = "";

    [JsonPropertyName("pluginVersion")]
    [GoOmitEmpty]
    public string PluginVersion { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("checkoutMode")]
    public string CheckoutMode { get; set; } = "";

    [JsonPropertyName("identityFields")]
    [GoOmitEmpty]
    public List<string> IdentityFields { get; set; } = [];

    /// <summary>Go 的 omitempty 对 struct 无效，所以这两个字段总会输出。</summary>
    [JsonPropertyName("notificationSuccess")]
    public NotificationResponse NotificationSuccess { get; set; } = new();

    [JsonPropertyName("notificationFailure")]
    public NotificationResponse NotificationFailure { get; set; } = new();
}

/// <summary>
/// 通知响应。对应 Go: <c>paymentsdk.NotificationResponse</c>。
/// <b>Go 侧该 struct 没有 json tag</b>，所以 key 是 PascalCase。
/// </summary>
public sealed class NotificationResponse
{
    [JsonPropertyName("Status")]
    public int Status { get; set; }

    [JsonPropertyName("ContentType")]
    public string ContentType { get; set; } = "";

    [JsonPropertyName("Body")]
    public string Body { get; set; } = "";
}

/// <summary>
/// 下单请求。对应 Go: <c>paymentsdk.CreateRequest</c>（无 json tag → PascalCase）。
/// </summary>
public sealed class CreateRequest
{
    [JsonPropertyName("MerchantOrderNo")]
    public string MerchantOrderNo { get; set; } = "";

    [JsonPropertyName("Description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("AmountFen")]
    public long AmountFen { get; set; }

    [JsonPropertyName("Currency")]
    public string Currency { get; set; } = "";

    [JsonPropertyName("ExpiresAt")]
    public DateTime ExpiresAt { get; set; }

    [JsonPropertyName("NotifyURL")]
    public string NotifyURL { get; set; } = "";

    [JsonPropertyName("ReturnURL")]
    public string ReturnURL { get; set; } = "";
}

/// <summary>收银台跳转信息。对应 Go: <c>paymentsdk.Checkout</c>（无 tag）。</summary>
public sealed class Checkout
{
    [JsonPropertyName("Mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("Value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("ExpiresAt")]
    public DateTime ExpiresAt { get; set; }
}

/// <summary>查询请求。对应 Go: <c>paymentsdk.QueryRequest</c>（无 tag）。</summary>
public sealed class QueryRequest
{
    [JsonPropertyName("MerchantOrderNo")]
    public string MerchantOrderNo { get; set; } = "";
}

/// <summary>关单请求。对应 Go: <c>paymentsdk.CloseRequest</c>（无 tag）。</summary>
public sealed class CloseRequest
{
    [JsonPropertyName("MerchantOrderNo")]
    public string MerchantOrderNo { get; set; } = "";
}

/// <summary>订单结果。对应 Go: <c>paymentsdk.Result</c>（全部带 tag → camelCase）。</summary>
public class PaymentResult
{
    [JsonPropertyName("merchantOrderNo")]
    public string MerchantOrderNo { get; set; } = "";

    [JsonPropertyName("providerTradeNo")]
    [GoOmitEmpty]
    public string ProviderTradeNo { get; set; } = "";

    [JsonPropertyName("providerStatus")]
    public string ProviderStatus { get; set; } = "";

    [JsonPropertyName("amountFen")]
    public long AmountFen { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "";

    [JsonPropertyName("paid")]
    public bool Paid { get; set; }

    [JsonPropertyName("closed")]
    public bool Closed { get; set; }

    [JsonPropertyName("paidAt")]
    [GoOmitEmpty]
    public DateTime PaidAt { get; set; }
}

/// <summary>
/// 支付通知。对应 Go: <c>paymentsdk.Notification</c>。
/// Go 用结构体嵌入 <c>Result</c>，JSON 会平铺到同一层；C# 必须显式展开。
/// </summary>
public sealed class PaymentNotification
{
    [JsonPropertyName("eventId")]
    public string EventID { get; set; } = "";

    // ---- 以下为嵌入的 Result 字段（平铺） ----

    [JsonPropertyName("merchantOrderNo")]
    public string MerchantOrderNo { get; set; } = "";

    [JsonPropertyName("providerTradeNo")]
    [GoOmitEmpty]
    public string ProviderTradeNo { get; set; } = "";

    [JsonPropertyName("providerStatus")]
    public string ProviderStatus { get; set; } = "";

    [JsonPropertyName("amountFen")]
    public long AmountFen { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "";

    [JsonPropertyName("paid")]
    public bool Paid { get; set; }

    [JsonPropertyName("closed")]
    public bool Closed { get; set; }

    [JsonPropertyName("paidAt")]
    [GoOmitEmpty]
    public DateTime PaidAt { get; set; }
}

/// <summary>对账账单记录。对应 Go: <c>paymentsdk.BillRecord</c>（无 tag）。</summary>
public sealed class BillRecord
{
    [JsonPropertyName("MerchantOrderNo")]
    public string MerchantOrderNo { get; set; } = "";

    [JsonPropertyName("ProviderTradeNo")]
    public string ProviderTradeNo { get; set; } = "";

    [JsonPropertyName("ProviderStatus")]
    public string ProviderStatus { get; set; } = "";

    [JsonPropertyName("AmountFen")]
    public long AmountFen { get; set; }

    [JsonPropertyName("Currency")]
    public string Currency { get; set; } = "";

    [JsonPropertyName("PaidAt")]
    public DateTime PaidAt { get; set; }
}

/// <summary>
/// 插件返回的结构化错误。对应 Go: <c>paymentsdk.ProviderError</c>。
/// </summary>
public class PaymentProviderException : Exception
{
    public PaymentProviderException(string code, string message, bool temporary = false, Exception? cause = null)
        : base(message.Length > 0 ? message : code, cause)
    {
        Code = code;
        Temporary = temporary;
    }

    /// <summary>机器可读错误码。</summary>
    public string Code { get; }

    /// <summary>是否可重试（进程异常退出、超时等）。</summary>
    public bool Temporary { get; }
}

/// <summary>Go 侧定义的哨兵错误文案。对应 <c>paymentsdk</c> 的三个 <c>Err*</c>。</summary>
public static class PaymentErrors
{
    public const string OrderNotFound = "payment provider order not found";
    public const string OrderNotPaid = "payment provider order is not paid";
    public const string TradeBillNotFound = "payment provider trade bill not found";

    /// <summary>
    /// 判断错误消息是否表示「上游查不到该订单」。
    /// </summary>
    /// <remarks>
    /// Go 宿主用 <c>errors.Is(err, payment.ErrOrderNotFound)</c> 判定；
    /// 但走 RPC 时插件把哨兵错误包成了普通消息（<c>"payment provider order not found: xxx"</c>），
    /// <c>errors.Is</c> 匹配不到。这里按消息前缀识别，让进程内与 RPC 两条路径行为一致。
    /// </remarks>
    public static bool IsOrderNotFound(string message) =>
        message.StartsWith(OrderNotFound, StringComparison.Ordinal);

    /// <summary>
    /// 判断错误消息是否表示「渠道账单不存在」。
    /// </summary>
    /// <remarks>与 <see cref="IsOrderNotFound"/> 同理：RPC 路径下哨兵错误会被包成普通消息。</remarks>
    public static bool IsTradeBillNotFound(string message) =>
        message.Contains(TradeBillNotFound, StringComparison.Ordinal);
}

/// <summary>渠道账单不存在。对应 Go: <c>paymentsdk.ErrTradeBillNotFound</c>。</summary>
public sealed class PaymentTradeBillNotFoundException : PaymentProviderException
{
    public PaymentTradeBillNotFoundException(string message)
        : base("trade_bill_not_found", message.Length > 0 ? message : PaymentErrors.TradeBillNotFound)
    {
    }
}

/// <summary>上游查不到该订单。对应 Go: <c>paymentsdk.ErrOrderNotFound</c>。</summary>
public sealed class PaymentOrderNotFoundException : PaymentProviderException
{
    public PaymentOrderNotFoundException(string message)
        : base("order_not_found", message.Length > 0 ? message : PaymentErrors.OrderNotFound)
    {
    }
}
