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
        : base(FullMessage(statusCode, body))
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

    /// <summary>
    /// 完整文案：400/422 先归类再追加安全详情，其余只追加安全详情。
    /// 对应 Go: <c>(e providerHTTPError) Error()</c>。
    /// </summary>
    /// <remarks>
    /// 文案里带上上游详情（<c>"…；上游：&lt;detail&gt;"</c>）与 Go 完全一致。机器判断请用
    /// <see cref="Body"/>（原始正文）而不是本消息：正文一旦被归类/脱敏就不可逆。
    /// </remarks>
    private static string FullMessage(int statusCode, string body) =>
        statusCode is 400 or 422
            ? ProviderErrorMessages.WithDetail(Summary(statusCode), body)
            : ProviderErrorMessages.AppendDetail(Summary(statusCode), body);

    /// <summary>状态码摘要（<b>不含</b>上游详情）。对应 Go: <c>providerHTTPError.summary()</c>。</summary>
    public static string BuildMessage(int statusCode) => Summary(statusCode);

    private static string Summary(int statusCode) => statusCode switch
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
/// <c>providerPayloadErrorMessage</c> / <c>providerErrorWithDetail</c> /
/// <c>appendProviderErrorDetail</c>。
/// </summary>
public static class ProviderErrorMessages
{
    /// <summary>默认兜底文案。对应 Go 的 <c>"模型服务请求失败"</c>。</summary>
    public const string GenericFailure = "模型服务请求失败";

    /// <summary>网络层兜底文案。</summary>
    public const string NetworkFailure = "连接模型服务失败，请检查渠道地址和网络";

    /// <summary>
    /// 响应体中途断开（上游或中转网关提前关闭连接、未发终止块）的文案。
    /// 对应 Go: <c>providerConnectionError</c>（<c>internal/app/provider_http_client.go:334</c>）。
    /// </summary>
    /// <remarks>
    /// 这类失败<b>不是"连不上"</b>：请求已经发出、上游也已开始处理并计费，只是结果没收全。
    /// 文案必须把用户推向中转站的记录与扣费，而不是让他去查渠道地址和网络 ——
    /// 原样抛 <c>HttpIOException</c> 会落进 <see cref="UserFacing"/> 的兜底分支变成
    /// <see cref="NetworkFailure"/>，把排障方向带偏。
    /// </remarks>
    public const string ConnectionClosedPrematurely =
        "模型服务连接提前关闭，未收到完整结果；请先核对中转站任务和扣费记录，再决定是否重试";

    /// <summary>
    /// 把异常映射为用户可见文案。
    /// 对应 Go: <c>err.Error()</c>（<c>task_terminal.go</c> 的 <c>userFacingMessage</c> ⇒
    /// <c>InterceptResponseText(taskFailureMessage(err))</c>）。
    /// </summary>
    /// <remarks>
    /// 各异常类型在构造时已把「稳定归类 + 安全上游详情」写进 <see cref="Exception.Message"/>，
    /// 因此这里对它们直接透出即可。<b>只有无法识别的异常</b>才回落
    /// <see cref="NetworkFailure"/>：那类异常的 Message 是进程内部措辞（类型名、路径、栈消息），
    /// 对外没有排障价值，照抄 Go 的 <c>err.Error()</c> 反而会把内部实现细节送到用户面前。
    /// </remarks>
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
            // 构造时已按 Go 的 Error() 语义完成归类 + 追加安全详情，直接透出。
            case ProviderHttpException httpError:
                return httpError.Message;
            // 上游业务失败：对应 Go 的 providerPayloadError.Error()。
            // 此前漏了这一分支，导致这类失败被兜底成「连接模型服务失败」，
            // 把「请求内容/渠道配置」的问题伪装成网络问题。
            case ProviderPayloadException payloadError:
                return payloadError.Message;
            // 熔断/占槽/传输异常自带准确文案：兜底成"网络失败"会把熔断、
            // 并发配置等进程内错误伪装成连不上上游，排障方向会被带偏。
            case ProviderCircuitOpenException:
            case ProviderChannelSlotException:
            case ProviderTransportException:
                return error.Message;
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

    /// <summary>不能归类时使用的兜底文案。对应 Go: <c>providerPayloadErrorMessage</c> 的 fallback。</summary>
    public const string PayloadFallback = "模型服务返回失败，请检查请求内容或渠道配置";

    /// <summary>安全上游详情的分隔符。对应 Go 的 <c>"；上游："</c>。</summary>
    public const string UpstreamDetailSeparator = "；上游：";

    /// <summary>
    /// 先归类、再追加安全详情。对应 Go: <c>providerErrorWithDetail</c>。
    /// </summary>
    public static string WithDetail(string fallback, string? raw)
    {
        if (PayloadErrorCategory(raw, out string categorized))
        {
            fallback = categorized;
        }
        return AppendDetail(fallback, raw);
    }

    /// <summary>
    /// 追加安全详情：详情为空或与 fallback 相同则不加。对应 Go: <c>appendProviderErrorDetail</c>。
    /// </summary>
    public static string AppendDetail(string fallback, string? raw)
    {
        string detail = ProviderErrorDetail.Extract(raw);
        if (detail.Length > 0 && !string.Equals(detail, fallback, StringComparison.Ordinal))
        {
            return fallback + UpstreamDetailSeparator + detail;
        }
        return fallback;
    }

    /// <summary>
    /// 归类失败时的兜底文案（含安全上游详情）。
    /// 对应 Go: <c>providerPayloadErrorMessage</c>。
    /// </summary>
    public static string PayloadError(string? raw) => WithDetail(PayloadFallback, raw);
}
