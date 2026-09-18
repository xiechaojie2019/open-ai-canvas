using Microsoft.AspNetCore.Http;
using OpenAICanvas.Web.Diagnostics;
using OpenAICanvas.Web.Http;

namespace OpenAICanvas.Web.Middleware;

/// <summary>
/// 兜底异常投影，等价于 Go 的 <c>gin.Recovery()</c> + handler 层未捕获错误。
/// </summary>
/// <remarks>
/// 关键约束：未分类异常绝不把原始 message 写入响应体，只回固定文案（对应 C5 红线）。
/// 响应已经开始写出时无法再改状态码，此时只能中断连接。
/// </remarks>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;

    public ExceptionHandlingMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端主动断开：Go 侧同样不会写响应体。
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted)
            {
                CanvasLog.Error($"响应已开始写出，无法投影异常：{exception.GetType().Name}");
                throw;
            }

            context.Response.Clear();
            IResult result = ApiResults.FailInternal(StatusCodes.Status500InternalServerError, exception, context);
            await result.ExecuteAsync(context);
        }
    }
}
