using System.Net;
using Microsoft.AspNetCore.Http;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Web.Contracts;
using OpenAICanvas.Web.Diagnostics;

namespace OpenAICanvas.Web.Http;

/// <summary>
/// Go 版 <c>internal/handler/response.go</c> 的等价实现。
/// 所有成功/失败响应都必须经由本类，才能保证 code / reason / msg / HTTP status 与 Go 一致。
/// </summary>
public static class ApiResults
{
    /// <summary>对应 Go 的 <c>internalErrorMessage</c>。</summary>
    public const string InternalErrorMessage = "系统处理失败，请稍后重试";

    /// <summary>对应 Go: <c>ok(c, data)</c>。</summary>
    public static IResult Ok(object? data) => new EnvelopeResult(
        StatusCodes.Status200OK,
        new ApiEnvelope { Code = ErrorCodes.Ok, Data = data, Msg = "ok" });

    /// <summary>
    /// 对应 Go: <c>fail(c, status, err)</c>。
    /// 只接受调用方已经确认可公开的错误；service 返回值统一交给 <see cref="FailService"/> 投影。
    /// </summary>
    public static IResult Fail(int status, Exception? error)
    {
        string message = StatusText(status);
        if (error is not null && !string.IsNullOrWhiteSpace(error.Message))
        {
            message = error.Message;
        }

        return WriteFailure(status, status, ErrorReasons.ForStatus(status), message);
    }

    /// <summary>对应 Go: <c>failService(c, err)</c>。</summary>
    public static IResult FailService(Exception error, HttpContext? httpContext = null)
    {
        if (error is EmailCodeCooldownException cooldown)
        {
            // 429 + Retry-After + code=42901，与 Go 完全一致。
            if (httpContext is not null)
            {
                httpContext.Response.Headers["Retry-After"] = cooldown.Seconds.ToString();
            }

            return WriteFailure(
                ErrorCodes.TooManyRequests,
                ErrorCodes.RateLimited,
                ErrorReasons.RateLimited,
                cooldown.Message);
        }

        if (error is AppError appError && IsValidErrorStatus(appError.Status))
        {
            return WriteAppError(appError, httpContext);
        }

        return FailInternal(StatusCodes.Status500InternalServerError, error, httpContext);
    }

    /// <summary>对应 Go: <c>writeAppError</c>。</summary>
    public static IResult WriteAppError(AppError appError, HttpContext? httpContext = null)
    {
        int code = appError.Code != 0 ? appError.Code : appError.Status;
        string reason = string.IsNullOrEmpty(appError.Reason)
            ? ErrorReasons.ForStatus(appError.Status)
            : appError.Reason;

        string message = appError.Message.Trim();
        if (message.Length == 0)
        {
            message = SafeInternalErrorMessage(appError.Status);
        }

        if (appError.Status >= StatusCodes.Status500InternalServerError)
        {
            LogHandlerError(httpContext, appError.Status, appError.Cause ?? appError);
        }

        return WriteFailure(appError.Status, code, reason, message);
    }

    /// <summary>
    /// 对应 Go: <c>failInternal</c>。
    /// 保留真实 HTTP 状态，但绝不把未分类错误原文写入响应。
    /// </summary>
    public static IResult FailInternal(int status, Exception? error, HttpContext? httpContext = null)
    {
        if (!IsValidErrorStatus(status))
        {
            status = StatusCodes.Status500InternalServerError;
        }

        LogHandlerError(httpContext, status, error);
        return WriteFailure(status, status, ErrorReasons.ForStatus(status), SafeInternalErrorMessage(status));
    }

    /// <summary>对应 Go: <c>writeFailure</c>。reason 为空时字段整体省略。</summary>
    public static IResult WriteFailure(int status, int code, string reason, string message) =>
        new EnvelopeResult(status, new ApiEnvelope
        {
            Code = code,
            Data = null,
            Msg = message,
            Reason = reason,
        });

    /// <summary>
    /// 非标准组合：失败状态下仍携带 data，且不带 reason。
    /// 对应 Go 中直接 <c>c.JSON(status, gin.H{"code":..,"data":..,"msg":..})</c> 的写法
    /// （例如健康检查的"服务仍在启动"）。
    /// </summary>
    public static IResult Custom(int status, int code, object? data, string message) =>
        new EnvelopeResult(status, new ApiEnvelope
        {
            Code = code,
            Data = data,
            Msg = message,
        });

    /// <summary>对应 Go: <c>validErrorStatus</c>（400..599）。</summary>
    public static bool IsValidErrorStatus(int status) =>
        status >= StatusCodes.Status400BadRequest && status <= 599;

    /// <summary>对应 Go: <c>safeInternalErrorMessage</c>。5xx 只回固定文案。</summary>
    public static string SafeInternalErrorMessage(int status) => status switch
    {
        StatusCodes.Status502BadGateway => "上游服务暂时不可用，请稍后重试",
        StatusCodes.Status503ServiceUnavailable => "服务暂时不可用，请稍后重试",
        StatusCodes.Status504GatewayTimeout => "上游服务响应超时，请稍后重试",
        _ => InternalErrorMessage,
    };

    /// <summary>
    /// 对应 Go: <c>logHandlerError</c>。
    /// 普通访问日志会输出 Gin error；这里只记录错误类型，避免密钥或上游响应体进入日志。
    /// </summary>
    public static void LogHandlerError(HttpContext? httpContext, int status, Exception? error)
    {
        if (error is null)
        {
            return;
        }

        string method = httpContext?.Request.Method ?? string.Empty;
        string route = httpContext?.GetEndpoint()?.DisplayName ?? "<unmatched>";
        CanvasLog.Warn(
            $"handler request failed: method={method} route={route} status={status} error_type={error.GetType().Name}");
    }

    /// <summary>
    /// 等价于 Go <c>http.StatusText</c>。未注册状态返回空串，与 Go 行为一致。
    /// </summary>
    public static string StatusText(int status)
    {
        if (!Enum.IsDefined(typeof(HttpStatusCode), status))
        {
            return string.Empty;
        }

        HttpStatusCode code = (HttpStatusCode)status;
        // .NET 的枚举名是 "BadRequest"，Go 的 StatusText 是 "Bad Request"，
        // 两者都会进入响应 msg，因此必须还原 Go 的英文短语。
        return code switch
        {
            HttpStatusCode.BadRequest => "Bad Request",
            HttpStatusCode.Unauthorized => "Unauthorized",
            HttpStatusCode.Forbidden => "Forbidden",
            HttpStatusCode.NotFound => "Not Found",
            HttpStatusCode.MethodNotAllowed => "Method Not Allowed",
            HttpStatusCode.Conflict => "Conflict",
            HttpStatusCode.RequestEntityTooLarge => "Request Entity Too Large",
            HttpStatusCode.UnsupportedMediaType => "Unsupported Media Type",
            HttpStatusCode.UnprocessableEntity => "Unprocessable Entity",
            HttpStatusCode.TooManyRequests => "Too Many Requests",
            HttpStatusCode.InternalServerError => "Internal Server Error",
            HttpStatusCode.BadGateway => "Bad Gateway",
            HttpStatusCode.ServiceUnavailable => "Service Unavailable",
            HttpStatusCode.GatewayTimeout => "Gateway Timeout",
            _ => SplitPascalCase(code.ToString()),
        };
    }

    private static string SplitPascalCase(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        System.Text.StringBuilder builder = new(name.Length + 4);
        for (int index = 0; index < name.Length; index++)
        {
            char current = name[index];
            if (index > 0 && char.IsUpper(current) && !char.IsUpper(name[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}
