namespace OpenAICanvas.Domain.Kernel;

/// <summary>
/// 稳定机器可读原因，前端应判断 reason 而不是解析 msg。
/// 序列化后即为字符串字面量，与 Go 版逐字一致。
/// </summary>
/// <remarks>对应 Go: internal/kernel/error_codes.go 的 ErrorReason</remarks>
public static class ErrorReasons
{
    public const string InvalidArgument = "invalid_argument";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string FailedPrecondition = "failed_precondition";
    public const string QuotaExceeded = "quota_exceeded";
    public const string RateLimited = "rate_limited";
    public const string Unavailable = "unavailable";
    public const string Timeout = "timeout";
    public const string Internal = "internal";
    public const string BadGateway = "bad_gateway";

    /// <summary>
    /// 状态码到原因的唯一映射。空字符串表示"无 reason"，调用方据此决定是否写出该字段。
    /// </summary>
    public static string ForStatus(int status) => status switch
    {
        ErrorCodes.InvalidArgument => InvalidArgument,
        ErrorCodes.Unauthorized => Unauthorized,
        ErrorCodes.Forbidden => Forbidden,
        ErrorCodes.NotFound => NotFound,
        ErrorCodes.Conflict => Conflict,
        ErrorCodes.TooManyRequests => RateLimited,
        ErrorCodes.BadGateway => BadGateway,
        ErrorCodes.Unavailable => Unavailable,
        ErrorCodes.Timeout => Timeout,
        _ => status >= 500 ? Internal : InvalidArgument,
    };
}
