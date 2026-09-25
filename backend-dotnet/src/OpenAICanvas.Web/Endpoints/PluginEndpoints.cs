#nullable enable

using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 插件中心与协议目录路由。对应 Go: <c>handler/plugin.go</c> + <c>handler/plugin_admin.go</c>。
/// </summary>
public static partial class PluginEndpoints
{
    public static void MapPluginRoutes(this IEndpointRouteBuilder api, CanvasService service)
    {
        // 协议目录：优先使用插件运行时快照（含已安装插件），运行时未启动时回落官方包。
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
                providers = ProtocolCatalogService.PluginProviderCatalog(
                    scope, capability, includeUnavailable: false,
                    registry: service.Plugins.RegistrySnapshot()),
            });
        });

        // ------------------------------------------------------ 用户侧（登录）

        api.MapGet("/plugins/status", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                (IReadOnlyDictionary<string, string> statuses, IReadOnlyDictionary<string, PluginStateView> states) =
                    await service.PluginManagement.PluginsForUserAsync(user, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { statuses, states });
            }
            catch (Exception error)
            {
                IResult failure = ApiResults.FailService(error, context);
                await failure.ExecuteAsync(context).ConfigureAwait(false);
                return Results.Empty;
            }
        });

        api.MapPut("/plugins/{pluginId}/activation", async (HttpContext context, string pluginId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                bool enabled = await ReadBoolBodyAsync(context, "enabled", cancellationToken).ConfigureAwait(false);
                PluginStateView state = await service.PluginManagement.SetUserPluginEnabledAsync(
                    user, pluginId, enabled, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { state });
            }
            catch (Exception error)
            {
                IResult failure = ApiResults.FailService(error, context);
                await failure.ExecuteAsync(context).ConfigureAwait(false);
                return Results.Empty;
            }
        });

        // ------------------------------------------------------ 插件中心（功能开关）

        api.MapGet("/plugins", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                await RequirePluginCenterAsync(service, user, cancellationToken).ConfigureAwait(false);
                List<PluginView> plugins = service.Plugins.List();
                foreach (PluginView plugin in plugins)
                {
                    plugin.Management = service.PluginManagement.ManagementFor(plugin);
                }
                Dictionary<string, AdminPluginStateView> states = await service.PluginManagement
                    .AdminPluginStatesAsync(cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { plugins, states });
            }
            catch (Exception error)
            {
                IResult failure = ApiResults.FailService(error, context);
                await failure.ExecuteAsync(context).ConfigureAwait(false);
                return Results.Empty;
            }
        });

        api.MapPost("/plugins", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                CanvasService.RequireAdmin(user);
                await RequirePluginCenterAsync(service, user, cancellationToken).ConfigureAwait(false);

                (byte[] body, string fileName) = await ReadUploadAsync(context, cancellationToken).ConfigureAwait(false);
                PluginView plugin = await service.PluginManagement.InstallForAdminAsync(
                    user, body, fileName, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { plugin });
            }
            catch (Exception error)
            {
                IResult failure = ApiResults.FailService(error, context);
                await failure.ExecuteAsync(context).ConfigureAwait(false);
                return Results.Empty;
            }
        });

        api.MapGet("/plugins/{pluginId}/package", async (HttpContext context, string pluginId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                List<PluginView> plugins = service.Plugins.List();
                foreach (PluginView item in plugins)
                {
                    item.Management = service.PluginManagement.ManagementFor(item);
                }
                PluginView? plugin = plugins.FirstOrDefault(item => item.Manifest.ID == pluginId.Trim());
                if (plugin is null)
                {
                    throw OpenAICanvas.Domain.Kernel.AppError.NotFound($"插件 {pluginId} 不存在");
                }
                if (plugin.Source != "uploaded" && user.Role != OpenAICanvas.Domain.Entities.UserRole.UserRoleAdmin)
                {
                    throw OpenAICanvas.Domain.Kernel.AppError.Forbidden("无权访问该插件包");
                }
                byte[] bytes = await service.PluginManagement.PackageBytesAsync(pluginId, cancellationToken)
                    .ConfigureAwait(false);
                context.Response.Headers.CacheControl = "private, no-store";
                string fileName = (plugin.FileName.Length != 0 ? plugin.FileName : pluginId + ".yingce-plugin")
                    .Replace("\"", "");
                context.Response.Headers.ContentDisposition = "attachment; filename=\"" + fileName + "\"";
                return Results.File(bytes, "application/zip", fileName);
            }
            catch (Exception error)
            {
                IResult failure = ApiResults.FailService(error, context);
                await failure.ExecuteAsync(context).ConfigureAwait(false);
                return Results.Empty;
            }
        });

        // ------------------------------------------------------ 管理端

        api.MapGet("/admin/plugins", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                CanvasService.RequireAdmin(user);
                List<PluginView> plugins = service.Plugins.List();
                foreach (PluginView plugin in plugins)
                {
                    plugin.Management = service.PluginManagement.ManagementFor(plugin);
                }
                Dictionary<string, AdminPluginStateView> states = await service.PluginManagement
                    .AdminPluginStatesAsync(cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { plugins, states });
            }
            catch (Exception error)
            {
                IResult failure = ApiResults.FailService(error, context);
                await failure.ExecuteAsync(context).ConfigureAwait(false);
                return Results.Empty;
            }
        });

        api.MapPut("/admin/plugins/{pluginId}/availability", async (HttpContext context, string pluginId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                CanvasService.RequireAdmin(user);
                bool available = await ReadBoolBodyAsync(context, "available", cancellationToken).ConfigureAwait(false);
                await service.PluginManagement.SetPluginPlatformAvailabilityAsync(
                    user, pluginId, available, cancellationToken).ConfigureAwait(false);
                Dictionary<string, AdminPluginStateView> states = await service.PluginManagement
                    .AdminPluginStatesAsync(cancellationToken).ConfigureAwait(false);
                states.TryGetValue(pluginId.Trim(), out AdminPluginStateView? state);
                return ApiResults.Ok(new { state });
            }
            catch (Exception error)
            {
                IResult failure = ApiResults.FailService(error, context);
                await failure.ExecuteAsync(context).ConfigureAwait(false);
                return Results.Empty;
            }
        });

        api.MapPost("/plugins/{pluginId}/enable", async (HttpContext context, string pluginId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                CanvasService.RequireAdmin(user);
                await service.PluginManagement.SetPluginPlatformAvailabilityAsync(
                    user, pluginId, available: true, cancellationToken).ConfigureAwait(false);
                Dictionary<string, AdminPluginStateView> states = await service.PluginManagement
                    .AdminPluginStatesAsync(cancellationToken).ConfigureAwait(false);
                states.TryGetValue(pluginId.Trim(), out AdminPluginStateView? enabledState);
                return ApiResults.Ok(new { state = enabledState });
            }
            catch (Exception error)
            {
                IResult failure = ApiResults.FailService(error, context);
                await failure.ExecuteAsync(context).ConfigureAwait(false);
                return Results.Empty;
            }
        });

        api.MapPost("/plugins/{pluginId}/disable", async (HttpContext context, string pluginId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                CanvasService.RequireAdmin(user);
                await service.PluginManagement.SetPluginPlatformAvailabilityAsync(
                    user, pluginId, available: false, cancellationToken).ConfigureAwait(false);
                Dictionary<string, AdminPluginStateView> states = await service.PluginManagement
                    .AdminPluginStatesAsync(cancellationToken).ConfigureAwait(false);
                states.TryGetValue(pluginId.Trim(), out AdminPluginStateView? disabledState);
                return ApiResults.Ok(new { state = disabledState });
            }
            catch (Exception error)
            {
                IResult failure = ApiResults.FailService(error, context);
                await failure.ExecuteAsync(context).ConfigureAwait(false);
                return Results.Empty;
            }
        });

        api.MapDelete("/plugins/{pluginId}", async (HttpContext context, string pluginId, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                CanvasService.RequireAdmin(user);
                await service.PluginManagement.UninstallForAdminAsync(user, pluginId, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { deleted = true });
            }
            catch (Exception error)
            {
                IResult failure = ApiResults.FailService(error, context);
                await failure.ExecuteAsync(context).ConfigureAwait(false);
                return Results.Empty;
            }
        });
    }

    /// <summary>插件中心访问门控：管理员直通，其余用户要求 pluginCenter 功能开放。</summary>
    private static async Task RequirePluginCenterAsync(
        CanvasService service, User user, CancellationToken cancellationToken)
    {
        if (user.Role == OpenAICanvas.Domain.Entities.UserRole.UserRoleAdmin)
        {
            return;
        }
        await service.Features.RequireFeatureAsync(
            OpenAICanvas.Platform.FeatureNames.PluginCenter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>解析 {enabled|available: bool} 的 JSON body。对应 Go 的 ShouldBindJSON。</summary>
    private static async Task<bool> ReadBoolBodyAsync(
        HttpContext context, string field, CancellationToken cancellationToken)
    {
        using System.Text.Json.JsonDocument document = await System.Text.Json.JsonDocument.ParseAsync(
            context.Request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
            && document.RootElement.TryGetProperty(field, out System.Text.Json.JsonElement value)
            && value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
            && value.GetBoolean();
    }

    /// <summary>
    /// 读取上传包：multipart 字段 <c>file</c> 或裸 body（上限 16MB）。对应 Go 的上传解析。
    /// </summary>
    private static async Task<(byte[] Body, string FileName)> ReadUploadAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        const long maxBytes = 16L << 20;
        if (context.Request.ContentType is null
            || !context.Request.Headers.ContentType.ToString().StartsWith("multipart/form-data", StringComparison.Ordinal))
        {
            using MemoryStream buffer = new();
            await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (buffer.Length > maxBytes)
            {
                throw OpenAICanvas.Domain.Kernel.AppError.New(400, "插件包超过 16MB 上限");
            }
            return (buffer.ToArray(), "");
        }

        Microsoft.AspNetCore.Http.IFormFile? file = context.Request.Form.Files.GetFile("file")
            ?? throw OpenAICanvas.Domain.Kernel.AppError.New(400, "缺少上传文件字段 file");
        if (file.Length > maxBytes)
        {
            throw OpenAICanvas.Domain.Kernel.AppError.New(400, "插件包超过 16MB 上限");
        }
        using MemoryStream upload = new();
        await file.CopyToAsync(upload, cancellationToken).ConfigureAwait(false);
        return (upload.ToArray(), file.FileName);
    }
}
