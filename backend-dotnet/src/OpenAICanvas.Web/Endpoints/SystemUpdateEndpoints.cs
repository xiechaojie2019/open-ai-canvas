#nullable enable
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 系统更新状态路由。用户已决策（2026-09-25）：.NET 走 Docker 部署，
/// 不移植 hostupdate 自更新机制；本组路由按 Docker 语义返回固定状态——
/// <c>supported=false</c> 表示当前部署形态由 <c>docker compose build</c> 升级，
/// 管理端 UI 据此隐藏更新入口。
/// 对应 Go: <c>handler/admin_update.go</c>。
/// </summary>
public static class SystemUpdateEndpoints
{
    public static void MapSystemUpdateRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        api.MapGet("/admin/system-update", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CanvasService.RequireAdmin(actor);
                return ApiResults.Ok(DockerStatus());
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // Docker 部署不支持在线检查更新：与 GET 同形态，不报错，前端保持可用。
        api.MapPost("/admin/system-update/check", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CanvasService.RequireAdmin(actor);
                return ApiResults.Ok(DockerStatus());
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/admin/system-update/start", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CanvasService.RequireAdmin(actor);
                return ApiResults.Fail(
                    StatusCodes.Status409Conflict,
                    new InvalidOperationException("当前部署使用 Docker 镜像，请通过 docker compose build 完成升级"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/admin/system-update/rollback", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CanvasService.RequireAdmin(actor);
                return ApiResults.Fail(
                    StatusCodes.Status409Conflict,
                    new InvalidOperationException("当前部署使用 Docker 镜像，请通过镜像回滚完成降级"));
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>Docker 部署的固定状态：不支持自更新。对应 Go: <c>hostupdate.Status</c>。</summary>
    private static Dictionary<string, object?> DockerStatus() => new(StringComparer.Ordinal)
    {
        ["supported"] = false,
        ["connected"] = true,
        ["repository"] = "",
        ["deployment"] = "docker",
        ["currentVersion"] = "docker",
        ["updateAvailable"] = false,
        ["checks"] = Array.Empty<object>(),
        ["operation"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["phase"] = "idle",
            ["targetVersion"] = "",
            ["startedAt"] = null,
            ["message"] = "",
        },
    };
}
