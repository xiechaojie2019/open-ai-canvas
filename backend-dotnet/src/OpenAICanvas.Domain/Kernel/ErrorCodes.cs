namespace OpenAICanvas.Domain.Kernel;

/// <summary>
/// 业务信封 code：成功为 0；失败默认等于 HTTP 状态。需要区分同一状态的不同原因时，用 status*100+n。
/// 不使用与 HTTP 无关的 1001/2001 号段，避免和现有 42901 以及前端按 status/code 判断重试打架。
/// </summary>
/// <remarks>对应 Go: internal/kernel/error_codes.go</remarks>
public static class ErrorCodes
{
    public const int Ok = 0;

    public const int InvalidArgument = 400;
    public const int Unauthorized = 401;
    public const int Forbidden = 403;
    public const int NotFound = 404;
    public const int Conflict = 409;
    public const int TooManyRequests = 429;
    public const int Internal = 500;
    public const int BadGateway = 502;
    public const int Unavailable = 503;
    public const int Timeout = 504;

    public const int QuotaExceeded = 40301;
    public const int IdempotencyConflict = 40901;
    public const int RateLimited = 42901;
}
