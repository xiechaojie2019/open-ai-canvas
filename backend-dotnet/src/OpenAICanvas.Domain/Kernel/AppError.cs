namespace OpenAICanvas.Domain.Kernel;

/// <summary>
/// 业务层对外公开的结构化错误。
/// Message 必须可安全展示给用户，Cause 仅用于保留内部诊断链路，不得直接写入 HTTP 响应。
/// </summary>
/// <remarks>对应 Go: internal/kernel/errors.go</remarks>
public class AppError : Exception
{
    public int Status { get; }
    public int Code { get; }
    public string Reason { get; }

    /// <summary>标记调用方可安全重试，用于限流等场景。</summary>
    public bool Retryable { get; }

    /// <summary>内部诊断原因。不会进入 HTTP 响应体，只用于日志。</summary>
    public Exception? Cause { get; }

    /// <summary>Go 的 Error() 返回值，即可安全展示给用户的文案。</summary>
    public override string Message { get; }

    public AppError(
        int status,
        string message,
        int? code = null,
        string? reason = null,
        bool retryable = false,
        Exception? cause = null)
        : base(message, cause)
    {
        Status = status;
        Code = code ?? status;
        Reason = reason ?? ErrorReasons.ForStatus(status);
        Message = message;
        Retryable = retryable;
        Cause = cause;
    }

    public static AppError New(int status, string message) => new(status, message);

    public static AppError Wrap(int status, string message, Exception? cause) =>
        new(status, message, cause: cause);

    public static AppError RateLimited(string message) => new(
        ErrorCodes.TooManyRequests,
        message,
        ErrorCodes.RateLimited,
        ErrorReasons.RateLimited,
        retryable: true);

    public static AppError QuotaExceeded(string message) => new(
        ErrorCodes.Forbidden,
        message,
        ErrorCodes.QuotaExceeded,
        ErrorReasons.QuotaExceeded);

    public static AppError BadAuthRequest(string message) => New(400, message);

    public static AppError NotFound(string message) => New(404, message);

    public static AppError Unauthorized(string message) => New(401, message);

    public static AppError Forbidden(string message) => New(403, message);
}
