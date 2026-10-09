#nullable enable
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Providers;

/// <summary>
/// 上游失败正文的<b>安全详情</b>提取。
/// 对应 Go: <c>internal/app/provider_error_message.go</c> 的
/// <c>providerErrorDetail</c> / <c>providerErrorMessageField</c>。
/// </summary>
/// <remarks>
/// 上游正文可能含密钥、网关 HTML、请求 ID、账务信息等内部诊断。这里只做一件事：
/// 把「错误消息字段」抽出来，并整条或局部隐藏敏感内容，让错误文案可行动而<b>不回传原始响应</b>。
/// Go 侧把该详情拼在稳定文案后面（<c>"…；上游：&lt;detail&gt;"</c>），这是用户能在任务中心
/// 看到真实失败原因的唯一来源；只给「连接模型服务失败」会把排障方向带偏。
/// </remarks>
public static class ProviderErrorDetail
{
    /// <summary>被隐藏的链接占位符。对应 Go 的 <c>[链接已隐藏]</c>。</summary>
    public const string RedactedLink = "[链接已隐藏]";

    /// <summary>详情长度上限（按 rune 计）。对应 Go 的 <c>1500</c>。</summary>
    public const int DetailLimit = 1500;

    /// <summary>普通消息里的 URL 单独移除。对应 Go: <c>providerErrorURL</c>。</summary>
    private static readonly Regex UrlPattern = new(
        "https?://[^\\s<>\"'，。；）]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 账务/凭据/诊断类关键词：命中即整条隐藏。对应 Go: <c>providerErrorSensitive</c>。
    /// </summary>
    /// <remarks>
    /// 与 Go 保持同一份关键词表。刻意包含中文与英文两套写法：上游既有国产网关的中文提示，
    /// 也有 OpenAI 兼容层的英文错误，只挡一边会漏。
    /// </remarks>
    private static readonly Regex SensitivePattern = new(
        "余额|额度|配额|欠费|账单|充值|计费|密钥|密码|令牌|"
        + "balance|quota|billing|credit|payment|funds|wallet|"
        + "api[ _-]?key|authorization|bearer|cookie|"
        + "(?:access|refresh|auth|session)[ _-]?token|token\\s*[=:]|"
        + "secret|password|credential|sk-[a-z0-9]|tenant|"
        + "trace[ _-]?id|request[ _-]?id|internal stack|stack\\s*trace|"
        + "dial tcp|no such host|prompt\\s*[=:]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 从上游正文中提取可安全回传的失败详情；无法安全提取时返回空串。
    /// 对应 Go: <c>providerErrorDetail</c>。
    /// </summary>
    public static string Extract(string? raw)
    {
        string text = (raw ?? "").Trim();
        if (text.Length == 0)
        {
            return "";
        }

        if (TryReadMessageField(text, out string fromJson))
        {
            text = fromJson;
        }
        else if (text.StartsWith('{') || text.StartsWith('['))
        {
            // 声称是 JSON 却解析失败（含被截断的正文）：整条隐藏。
            return "";
        }

        // 网关 HTML、敏感账务/凭据诊断整条隐藏；普通消息里的 URL 单独移除。
        if (text.StartsWith('{') || text.StartsWith('[')
            || text.IndexOfAny(['<', '>']) >= 0
            || SensitivePattern.IsMatch(text))
        {
            return "";
        }

        text = UrlPattern.Replace(text, RedactedLink);
        return KernelUtil.TruncateRunes(JoinWhitespace(text), DetailLimit);
    }

    /// <summary>
    /// 尝试把 JSON 正文收敛为其中的错误消息字段。
    /// 对应 Go: <c>json.Unmarshal</c> 那一支；解析成功即返回 <c>true</c>（即使抽出的消息为空）。
    /// </summary>
    private static bool TryReadMessageField(string text, out string message)
    {
        message = "";
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }
        using (document)
        {
            message = MessageField(document.RootElement);
            return true;
        }
    }

    /// <summary>
    /// 只提取错误消息字段，不序列化整个响应，避免回传 headers、请求正文和诊断信息。
    /// 对应 Go: <c>providerErrorMessageField</c>（对象按 <c>error/message/msg/detail</c> 优先）。
    /// </summary>
    private static string MessageField(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return (value.GetString() ?? "").Trim();
            case JsonValueKind.Object:
                foreach (string key in MessageFieldKeys)
                {
                    if (value.TryGetProperty(key, out JsonElement child))
                    {
                        string nested = MessageField(child);
                        if (nested.Length > 0)
                        {
                            return nested;
                        }
                    }
                }
                return "";
            default:
                // 数组、数字、布尔、null 都不是可回传的错误消息（与 Go 的 default 分支一致）。
                return "";
        }
    }

    private static readonly string[] MessageFieldKeys = ["error", "message", "msg", "detail"];

    /// <summary>把空白折叠为单个空格。对应 Go: <c>strings.Join(strings.Fields(raw), " ")</c>。</summary>
    private static string JoinWhitespace(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
