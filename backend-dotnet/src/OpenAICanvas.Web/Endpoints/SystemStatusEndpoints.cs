using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Web.Http;
using BuildInfoProvider = OpenAICanvas.Domain.Build.BuildInfoProvider;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 健康检查与版本路由，对应 Go: <c>cmd/server/system_status.go: registerSystemStatusRoutes</c>。
/// 挂在 <c>/api</c> 分组下，与 Go 版一致。
/// </summary>
public static class SystemStatusEndpoints
{
    public static void MapSystemStatusRoutes(this IEndpointRouteBuilder api, SystemStatus status)
    {
        api.MapGet("/health/live", () => ApiResults.Ok(new HealthLiveDataDto
        {
            Build = BuildInfoProvider.Current(),
        }));

        api.MapGet("/health/startup", (CancellationToken cancellationToken) =>
            BuildStartupOrReadyResult(status, cancellationToken, "服务仍在启动", requireReady: false));

        api.MapGet("/health/ready", (CancellationToken cancellationToken) =>
            BuildStartupOrReadyResult(status, cancellationToken, "服务暂未就绪", requireReady: true));

        // Go 把 /health 直接指向 ready 处理器。
        api.MapGet("/health", (CancellationToken cancellationToken) =>
            BuildStartupOrReadyResult(status, cancellationToken, "服务暂未就绪", requireReady: true));

        api.MapGet("/system/version", async (CancellationToken cancellationToken) =>
        {
            SystemStatusSnapshotDto snapshot = await status.SnapshotAsync(cancellationToken).ConfigureAwait(false);
            return ApiResults.Ok(new SystemVersionDataDto
            {
                Build = snapshot.Build,
                Schema = snapshot.Schema,
            });
        });
    }

    private static async Task<IResult> BuildStartupOrReadyResult(
        SystemStatus status,
        CancellationToken cancellationToken,
        string unavailableMessage,
        bool requireReady)
    {
        SystemStatusSnapshotDto snapshot = await status.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        bool healthy = requireReady ? snapshot.Ready : snapshot.Started;
        if (!healthy)
        {
            // Go 的写法是 c.JSON(503, gin.H{"code":503,"data":snapshot,"msg":...})，不带 reason。
            return ApiResults.Custom(
                StatusCodes.Status503ServiceUnavailable,
                ErrorCodes.Unavailable,
                snapshot,
                unavailableMessage);
        }

        return ApiResults.Ok(snapshot);
    }
}
