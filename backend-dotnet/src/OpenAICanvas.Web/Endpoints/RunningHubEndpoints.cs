#nullable enable
using System.Text.Json;
using OpenAICanvas.Application;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// RunningHub 管理代理与插件状态路由。
/// 对应 Go: <c>handler/runninghub.go</c> 与 <c>handler/plugin.go</c> 的 <c>GET /plugins/status</c>。
/// </summary>
/// <remarks>
/// GET /plugins/status 与插件中心路由统一在 <see cref="PluginEndpoints"/>；
/// 本文件只保留 RunningHub 管理代理，避免同路径重复注册。
/// </remarks>
public static class RunningHubEndpoints
{
    public static void MapRunningHubRoutes(this IEndpointRouteBuilder api, CanvasService service)
    {
        api.MapPost("/runninghub/workflow-info",
            (Func<HttpContext, Task<IResult>>)(context => FetchHandler(context, service, app: false)));
        api.MapPost("/runninghub/app-info",
            (Func<HttpContext, Task<IResult>>)(context => FetchHandler(context, service, app: true)));
    }

    private static async Task<IResult> FetchHandler(
        HttpContext context, CanvasService service, bool app)
    {
        OpenAICanvas.Domain.Entities.User user;
        try
        {
            user = await service.CurrentUserAsync(
                SessionCookie.Read(context), context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            IResult failure = ApiResults.FailService(error, context);
            await failure.ExecuteAsync(context).ConfigureAwait(false);
            return Results.Empty;
        }

        try
        {
            await service.WorkflowPlugins.RequireForUserAsync(
                user.ID, "runninghub-workflow-image", context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            IResult failure = ApiResults.FailService(error, context);
            await failure.ExecuteAsync(context).ConfigureAwait(false);
            return Results.Empty;
        }

        // Go: http.MaxBytesReader 128KB。ContentLength 超限直接拒绝；
        // 分块请求靠反序列化阶段遇到提前断流抛错兜底。
        const long MaxBodyBytes = 128 * 1024;
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > MaxBodyBytes)
        {
            IResult failure = ApiResults.Fail(400, new InvalidOperationException("请求体过大"));
            await failure.ExecuteAsync(context).ConfigureAwait(false);
            return Results.Empty;
        }

        RunningHubFetchRequestDto? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<RunningHubFetchRequestDto>(
                context.Request.Body, CanvasJson.ReadOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            request = null;
        }
        if (request is null)
        {
            IResult failure = ApiResults.Fail(400, new InvalidOperationException("请求体不是合法 JSON"));
            await failure.ExecuteAsync(context).ConfigureAwait(false);
            return Results.Empty;
        }

        try
        {
            Dictionary<string, object?> result = app
                ? await service.RunningHub.FetchAppInfoAsync(request, context.RequestAborted).ConfigureAwait(false)
                : await service.RunningHub.FetchWorkflowInfoAsync(request, context.RequestAborted).ConfigureAwait(false);
            return ApiResults.Ok(result);
        }
        catch (Exception error)
        {
            IResult failure = ApiResults.FailService(error, context);
            await failure.ExecuteAsync(context).ConfigureAwait(false);
            return Results.Empty;
        }
    }
}
