#nullable enable

using OpenAICanvas.Application;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 插件目录读取路由。对应 Go: <c>handler/plugin.go</c> 的 <c>GET /plugins/catalog</c>。
/// </summary>
/// <remarks>
/// 插件中心（安装 / 列表 / 状态切换，Go 的其余 /plugins 路由）属于迁移计划 10.1，
/// 本轮只补齐渠道模型编辑弹窗依赖的协议目录。
/// </remarks>
public static class PluginEndpoints
{
    public static void MapPluginRoutes(this IEndpointRouteBuilder api, CanvasService service)
    {
        api.MapGet("/plugins/catalog", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                IResult failure = ApiResults.FailService(error, context);
                await failure.ExecuteAsync(context).ConfigureAwait(false);
                return Results.Empty;
            }

            string scope = context.Request.Query["scope"].ToString().Trim();
            if (scope.Length == 0)
            {
                scope = "user.custom-channel";
            }
            string capability = context.Request.Query["capability"].ToString().Trim();

            return ApiResults.Ok(new
            {
                providers = ProtocolCatalogService.PluginProviderCatalog(scope, capability, includeUnavailable: false),
            });
        });
    }
}
