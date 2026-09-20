#nullable enable
using System.Globalization;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Providers;

/// <summary>
/// 供应商响应中的业务错误识别与错误码归一化。
/// 对应 Go: <c>internal/app/provider_error.go</c>。
/// </summary>
/// <remarks>
/// 只提取供应商<b>明确返回</b>的错误码与短消息，避免把完整响应或用户输入复制进调用日志。
/// </remarks>
public static class ProviderError
{
    /// <summary>内容审核失败的错误码。对应 Go: <c>contentModerationErrorCode</c>。</summary>
    public const string ContentModerationErrorCode = "sensitive_words_detected";

    /// <summary>内容审核失败的重试提示。对应 Go: <c>contentModerationRetryMessage</c>。</summary>
    public const string ContentModerationRetryMessage =
        "内容审核未通过，请修改提示词后重新生成；原任务不能直接重试";

    private const int MessageRuneLimit = 500;
    private const int CodeRuneLimit = 80;

    /// <summary>
    /// 从供应商载荷中提取 (code, message)。
    /// 依次尝试 <c>error</c>、<c>data</c> 两个嵌套对象，最后回落到顶层 ——
    /// 内层通常是供应商业务错误，外层 code 可能只是 HTTP 包装码。
    /// 对应 Go: <c>providerFailureDetails</c>。
    /// </summary>
    public static (string Code, string Message) FailureDetails(IReadOnlyDictionary<string, object?>? payload)
    {
        List<IReadOnlyDictionary<string, object?>> candidates = new(3);
        foreach (string key in new[] { "error", "data" })
        {
            Dictionary<string, object?>? nested = JsonFields.NestedObject(payload, key);
            if (nested is not null)
            {
                candidates.Add(nested);
            }
        }
        if (payload is not null)
        {
            candidates.Add(payload);
        }

        string code = "";
        string message = "";
        foreach (IReadOnlyDictionary<string, object?> candidate in candidates)
        {
            if (code.Length == 0)
            {
                candidate.TryGetValue("code", out object? codeValue);
                code = NormalizeErrorCode(codeValue);
            }
            if (message.Length == 0)
            {
                message = JsonFields.StringField(candidate, "message").Trim();
                if (message.Length == 0)
                {
                    message = JsonFields.StringField(candidate, "msg").Trim();
                }
            }
        }
        return (code, KernelUtil.TruncateRunes(message, MessageRuneLimit));
    }

    /// <summary>
    /// 从原始响应体识别业务失败。
    /// 返回 <c>(code, message, failed)</c>；非法 JSON 或非对象一律视为未识别。
    /// 对应 Go: <c>providerResponseBusinessFailure</c>。
    /// </summary>
    public static (string Code, string Message, bool Failed) ResponseBusinessFailure(string? responseBody)
    {
        if (string.IsNullOrEmpty(responseBody))
        {
            return ("", "", false);
        }
        Dictionary<string, object?>? payload = JsonFields.ParseObject(responseBody);
        if (payload is null)
        {
            return ("", "", false);
        }
        return PayloadBusinessFailure(payload);
    }

    /// <summary>
    /// 从已解析载荷识别业务失败。
    /// 对应 Go: <c>providerPayloadBusinessFailure</c>。
    /// </summary>
    public static (string Code, string Message, bool Failed) PayloadBusinessFailure(
        IReadOnlyDictionary<string, object?> payload)
    {
        Dictionary<string, object?>? errorValue = JsonFields.NestedObject(payload, "error");
        if (errorValue is not null)
        {
            Dictionary<string, object?> wrapper = new(StringComparer.Ordinal) { ["error"] = errorValue };
            (string code, string message) = FailureDetails(wrapper);
            if (code.Length > 0 || message.Length > 0)
            {
                return (code, message, true);
            }
        }

        payload.TryGetValue("code", out object? rawCode);
        if (!BusinessCodeFailed(rawCode))
        {
            return ("", "", false);
        }
        (string failedCode, string failedMessage) = FailureDetails(payload);
        return (failedCode, failedMessage, true);
    }

    /// <summary>
    /// 业务码是否代表失败：空、<c>0</c>、<c>success</c>/<c>succeeded</c>/<c>ok</c> 视为成功。
    /// 对应 Go: <c>providerBusinessCodeFailed</c>。
    /// </summary>
    public static bool BusinessCodeFailed(object? value)
    {
        string code = GoSprint(value).Trim().ToLowerInvariant();
        return code switch
        {
            "" or "0" or "success" or "succeeded" or "ok" or "<nil>" => false,
            _ => true,
        };
    }

    /// <summary>
    /// 错误码归一化：字符串/Stringer/数字均可，<c>0</c> 归一为空，超长按 rune 截断到 80。
    /// 对应 Go: <c>normalizedProviderErrorCode</c>。
    /// </summary>
    public static string NormalizeErrorCode(object? value)
    {
        string code = value switch
        {
            null => "",
            string text => text,
            bool flag => flag ? "true" : "false",
            // 与 Go 的 float64 分支一致：0 归一为空，其余按 %g 输出。
            double number => number == 0 ? "" : number.ToString("G", CultureInfo.InvariantCulture),
            float number => number == 0 ? "" : number.ToString("G", CultureInfo.InvariantCulture),
            int number => number == 0 ? "" : number.ToString(CultureInfo.InvariantCulture),
            long number => number == 0 ? "" : number.ToString(CultureInfo.InvariantCulture),
            decimal number => number == 0 ? "" : number.ToString(CultureInfo.InvariantCulture),
            _ => GoSprint(value),
        };
        code = code.Trim();
        if (code == "0")
        {
            return "";
        }
        return KernelUtil.TruncateRunes(code, CodeRuneLimit);
    }

    /// <summary>
    /// 是否为内容审核失败（大小写不敏感地包含审核错误码）。
    /// 对应 Go: <c>isContentModerationFailure</c>。
    /// </summary>
    public static bool IsContentModerationFailure(string value) =>
        value.ToLowerInvariant().Contains(ContentModerationErrorCode, StringComparison.Ordinal);

    /// <summary>
    /// 近似 Go 的 <c>fmt.Sprint</c>：nil 输出 <c>&lt;nil&gt;</c>，其余用不变文化格式化。
    /// </summary>
    private static string GoSprint(object? value)
    {
        if (value is null)
        {
            return "<nil>";
        }
        if (value is string text)
        {
            return text;
        }
        if (value is bool flag)
        {
            return flag ? "true" : "false";
        }
        if (value is IEnumerable<object?> list)
        {
            return "[" + string.Join(' ', list.Select(GoSprint)) + "]";
        }
        if (value is IFormattable formattable)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        }
        return value.ToString() ?? "";
    }
}
