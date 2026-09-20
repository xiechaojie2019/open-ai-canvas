#nullable enable
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Providers;

/// <summary>
/// 上游业务体错误（进程内保留原始原因）。
/// 对应 Go: <c>internal/app/provider.go</c> 的 <c>providerPayloadError</c>。
/// </summary>
/// <remarks>
/// Provider 正文可能包含密钥或内部诊断信息：<see cref="Raw"/> 仅供协议层的机器判断，
/// <b>禁止原样进入用户错误文案与日志</b>；对外只暴露归类后的稳定文案。
/// </remarks>
public sealed class ProviderPayloadException : Exception
{
    public ProviderPayloadException(string raw, string message)
        : base(message)
    {
        Raw = raw;
    }

    /// <summary>上游原始正文（敏感，不对外暴露）。</summary>
    public string Raw { get; }
}

/// <summary>
/// 上游 HTTP 层错误。
/// 对应 Go: <c>providerHTTPError</c>。文案按状态码映射为固定的可行动提示。
/// </summary>
public sealed class ProviderHttpException : Exception
{
    public ProviderHttpException(int statusCode, string status, string body, TimeSpan retryAfter)
        : base(BuildMessage(statusCode))
    {
        StatusCode = statusCode;
        Status = status;
        Body = body;
        RetryAfter = retryAfter;
    }

    public int StatusCode { get; }
    public string Status { get; }
    public string Body { get; }
    public TimeSpan RetryAfter { get; }

    /// <summary>对应 Go: <c>(e providerHTTPError) Error()</c>。</summary>
    public static string BuildMessage(int statusCode) => statusCode switch
    {
        524 => "上游网关超时（524）：模型请求可能仍在服务端执行并产生费用，请勿立即重试，请先到供应商后台核对任务或账单",
        400 or 422 => "模型服务拒绝了请求，请检查模型和参数",
        401 or 403 => "模型服务鉴权失败，请检查 API Key 和模型权限",
        404 => "模型或模型接口不存在，请检查渠道配置",
        408 or 504 => "模型服务响应超时，请稍后重试",
        429 => "模型服务请求过于频繁或额度不足，请稍后重试",
        >= 500 => $"模型服务暂时不可用（HTTP {statusCode}）",
        _ => $"模型服务请求失败（HTTP {statusCode}）",
    };
}

/// <summary>响应正文解码失败。对应 Go: <c>providerResponseDecodeError</c>。</summary>
public sealed class ProviderResponseDecodeException : Exception
{
    public ProviderResponseDecodeException(Exception cause)
        : base(cause.Message, cause)
    {
    }
}

/// <summary>渠道熔断。对应 Go: <c>providerCircuitOpenError</c>。</summary>
public sealed class ProviderCircuitOpenException : Exception
{
    public ProviderCircuitOpenException()
        : base("当前渠道连续失败，已暂时熔断，请稍后重试")
    {
    }
}

/// <summary>
/// 渠道并发配额获取失败（占槽超时 / 取消 / 协调器不可用）。
/// 对应 Go: <c>platform.channelSlotError</c>，错误码见 <c>platform.ChannelSlotFailureDetails</c>。
/// </summary>
/// <remarks>
/// 它属于<b>可重试的瞬时故障</b>：并发满载只是暂时的，换任务/稍后重试即可，
/// 不应像"路径不存在"那样触发协议回落。
/// </remarks>
public sealed class ProviderChannelSlotException : Exception
{
    /// <summary>占槽等待超时。对应 Go: <c>channel_concurrency_wait_timeout</c>。</summary>
    public const string WaitTimeoutCode = "channel_concurrency_wait_timeout";

    /// <summary>占槽等待被取消。对应 Go: <c>channel_concurrency_wait_cancelled</c>。</summary>
    public const string WaitCancelledCode = "channel_concurrency_wait_cancelled";

    /// <summary>渠道并发不可用。对应 Go: <c>channel_concurrency_unavailable</c>。</summary>
    public const string UnavailableCode = "channel_concurrency_unavailable";

    public ProviderChannelSlotException(string code, string message, Exception? cause = null)
        : base(message, cause) => Code = code;

    public string Code { get; }

    /// <summary>由 <see cref="OperationCanceledException"/> 归类出超时 / 取消。</summary>
    public static ProviderChannelSlotException FromCancellation(Exception error) =>
        new(error is TimeoutException ? WaitTimeoutCode : WaitCancelledCode, error.Message, error);

    /// <summary>协调器不可用或其它失败。</summary>
    public static ProviderChannelSlotException Unavailable(string message, Exception? cause = null) =>
        new(UnavailableCode, message, cause);
}

/// <summary>
/// 上游任务状态尚未同步（应继续查询原任务而非重新提交）。
/// 对应 Go: <c>providerStatePendingError</c>。
/// </summary>
public sealed class ProviderStatePendingException : Exception
{
    public ProviderStatePendingException(string taskId, Exception? cause)
        : base($"上游任务状态尚未同步，将继续查询原任务（任务 {taskId}）", cause)
    {
        TaskId = taskId;
    }

    public string TaskId { get; }
}

/// <summary>
/// 供应商错误到用户可见文案的映射。
/// 对应 Go: <c>providerUserFacingErrorMessage</c> / <c>providerPayloadErrorCategory</c> /
/// <c>providerPayloadErrorMessage</c>。
/// </summary>
public static class ProviderErrorMessages
{
    /// <summary>默认兜底文案。对应 Go 的 <c>"模型服务请求失败"</c>。</summary>
    public const string GenericFailure = "模型服务请求失败";

    /// <summary>网络层兜底文案。</summary>
    public const string NetworkFailure = "连接模型服务失败，请检查渠道地址和网络";

    /// <summary>
    /// 把异常映射为用户可见文案。
    /// 对应 Go: <c>providerUserFacingErrorMessage</c>。
    /// </summary>
    public static string UserFacing(Exception? error)
    {
        if (error is null)
        {
            return GenericFailure;
        }
        switch (error)
        {
            case OperationCanceledException:
                return "模型请求已取消";
            case TimeoutException:
                return "模型服务响应超时，请稍后重试";
            // 与 Go 一致：AppError 优先（业务错误文案直接透出）。
            case AppError appError when appError.Message.Trim().Length > 0:
                return appError.Message;
            case ProviderHttpException httpError:
                // 仅对上游参数校验类状态码解析正文：其他状态码的正文可能是网关 HTML、
                // 鉴权诊断或含密钥的内部信息，归类价值低且更容易误判。
                if (httpError.StatusCode is 400 or 422)
                {
                    if (PayloadErrorCategory(httpError.Body, out string categorized))
                    {
                        return categorized;
                    }
                }
                return httpError.Message;
            default:
                return NetworkFailure;
        }
    }

    /// <summary>
    /// 把上游失败正文归类为固定的用户可见原因。
    /// 对应 Go: <c>providerPayloadErrorCategory</c>。
    /// </summary>
    /// <remarks>
    /// 返回 <c>false</c> 表示无法归类，调用方应退回更通用的提示，
    /// <b>不要因为归类失败就把正文本身当成错误信息</b>。
    /// </remarks>
    public static bool PayloadErrorCategory(string? raw, out string message)
    {
        message = "";
        string normalized = (raw ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return false;
        }

        // 真人肖像类目只匹配供应商错误码里的稳定标识，不扫描自然语言 ——
        // 正文常回显用户提示词，"likeness"/"肖像"这类词单独出现并不能证明是真人形象被拒。
        // 该类目排在安全审核之前：错误码已足够具体，比通用审核提示更可行动。
        if (normalized.Contains("privacyinformation", StringComparison.Ordinal)
            || normalized.Contains("sensitivecontentdetected", StringComparison.Ordinal))
        {
            message = "输入素材疑似包含真人形象，该模型拒绝生成，请更换为非真人素材或改用其他模型";
            return true;
        }
        if (normalized.Contains("safety", StringComparison.Ordinal)
            || normalized.Contains("moderation", StringComparison.Ordinal)
            || normalized.Contains("content policy", StringComparison.Ordinal)
            || normalized.Contains("blocked", StringComparison.Ordinal))
        {
            message = "请求内容未通过模型服务安全审核，请调整后重试";
            return true;
        }
        if (normalized.Contains("quota", StringComparison.Ordinal)
            || normalized.Contains("insufficient", StringComparison.Ordinal)
            || normalized.Contains("balance", StringComparison.Ordinal)
            || normalized.Contains("billing", StringComparison.Ordinal))
        {
            message = "模型服务额度不足，请检查渠道余额或配额";
            return true;
        }
        if (normalized.Contains("model", StringComparison.Ordinal)
            && (normalized.Contains("not found", StringComparison.Ordinal)
                || normalized.Contains("permission", StringComparison.Ordinal)
                || normalized.Contains("access", StringComparison.Ordinal)))
        {
            message = "模型不存在或当前渠道未获得模型权限";
            return true;
        }
        // 推理/思考模式模型通常禁止强制指定工具调用（DeepSeek 思考模式返回
        // "Thinking mode does not support this tool_choice"，其他 OpenAI 兼容供应商措辞类似）。
        // 排在通用参数类目之前，避免稳定标识落回笼统的"请检查模型和参数"。
        bool thinkingLike = normalized.Contains("thinking", StringComparison.Ordinal)
            || normalized.Contains("reasoning", StringComparison.Ordinal);
        bool mentionsToolChoice = normalized.Contains("tool_choice", StringComparison.Ordinal);
        if ((thinkingLike && mentionsToolChoice)
            || (mentionsToolChoice
                && (normalized.Contains("not support", StringComparison.Ordinal)
                    || normalized.Contains("unsupported", StringComparison.Ordinal))))
        {
            message = "当前模型为思考/推理模式，不支持强制工具调用（tool_choice=required），"
                + "请改用自动工具选择或更换非思考模式模型";
            return true;
        }
        if (normalized.Contains("invalid", StringComparison.Ordinal)
            || normalized.Contains("parameter", StringComparison.Ordinal)
            || normalized.Contains("argument", StringComparison.Ordinal))
        {
            message = "模型服务拒绝了请求，请检查模型和参数";
            return true;
        }
        return false;
    }

    /// <summary>
    /// 归类失败时的兜底文案。
    /// 对应 Go: <c>providerPayloadErrorMessage</c>。
    /// </summary>
    public static string PayloadError(string? raw) =>
        PayloadErrorCategory(raw, out string message)
            ? message
            : "模型服务返回失败，请检查请求内容或渠道配置";
}
