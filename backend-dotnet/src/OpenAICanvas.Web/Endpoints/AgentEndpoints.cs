#nullable enable
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 云 Agent 偏好档案路由。对应 Go: <c>handler/agent.go</c> 的 capabilities 与 profile 部分；
/// 运行执行（runs / approvals / SSE）依赖尚未移植的调度引擎，暂不开放。
/// </summary>
public static class AgentEndpoints
{
    public static void MapAgentProfileRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        AgentProfileService profiles)
    {
        api.MapGet("/agent/capabilities", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }

            // 执行引擎未移植前显式声明不可用，避免前端误判后端支持运行。
            return ApiResults.Fail(
                StatusCodes.Status501NotImplemented,
                new InvalidOperationException("云 Agent 执行引擎尚未开放"));
        });

        api.MapGet("/agent/profile", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                string scope = context.Request.Query["scope"].ToString().Trim();
                string projectId = context.Request.Query["projectId"].ToString().Trim();
                string canvasId = context.Request.Query["canvasId"].ToString().Trim();
                if (scope.Length == 0)
                {
                    scope = canvasId.Length > 0 ? "canvas" : projectId.Length > 0 ? "project" : "user";
                }

                switch (scope)
                {
                    case "user" when projectId.Length > 0 || canvasId.Length > 0:
                        return ApiResults.Fail(
                            StatusCodes.Status400BadRequest,
                            new InvalidOperationException("用户偏好不能带项目或画布 ID"));
                    case "project" when projectId.Length == 0 || canvasId.Length > 0:
                        return ApiResults.Fail(
                            StatusCodes.Status400BadRequest,
                            new InvalidOperationException("项目偏好需要 projectId，且不能带 canvasId"));
                    case "canvas" when canvasId.Length == 0:
                        return ApiResults.Fail(
                            StatusCodes.Status400BadRequest,
                            new InvalidOperationException("画布偏好需要 canvasId"));
                    case not ("user" or "project" or "canvas"):
                        return ApiResults.Fail(
                            StatusCodes.Status400BadRequest,
                            new InvalidOperationException("无效的 Agent 偏好作用域"));
                }

                AgentProfileViewDto view = await profiles.ProfileForScopeAsync(
                    user.ID, projectId, canvasId, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(view);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/agent/profile", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AgentProfileRequest? request = await ReadSingleObjectAsync<AgentProfileRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException("请求必须只包含一个 JSON 对象"));
                }

                AgentProfileViewDto view = await profiles.UpdateAsync(
                    user.ID, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(view);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>对应 Go 的 DisallowUnknownFields + 单对象校验：拒绝重复 JSON 或尾部内容。</summary>
    private static async Task<T?> ReadSingleObjectAsync<T>(
        HttpContext context,
        long maxBytes,
        CancellationToken cancellationToken)
        where T : class
    {
        if (maxBytes > 0 && context.Request.ContentLength is > 0 && context.Request.ContentLength > maxBytes)
        {
            return null;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                context.Request.Body,
                CanvasJson.ReadOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (BadHttpRequestException)
        {
            return null;
        }
    }
}
