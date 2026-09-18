using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Platform;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 功能开放配置路由。对应 Go: <c>internal/handler/feature_availability.go</c>。
/// </summary>
public static class FeatureEndpoints
{
    /// <summary>PATCH 请求体上限 16KB。对应 Go 的 <c>16&lt;&lt;10</c>。</summary>
    private const long PatchBodyLimit = 16 * 1024;

    public static void MapFeatureRoutes(this IEndpointRouteBuilder api, CanvasService service)
    {
        // 未登录也可访问：欢迎页需要知道是否展示。
        api.MapGet("/public/welcome", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            // Go 显式关闭缓存，避免切换开关后前端仍拿到旧值。
            context.Response.Headers["Cache-Control"] = "no-store";

            PublicFeatureAvailability setting =
                await service.FeatureAvailabilityAsync(cancellationToken).ConfigureAwait(false);

            return ApiResults.Ok(new { welcomeEnabled = setting.WelcomeEnabled });
        });

        api.MapGet("/features", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            PublicFeatureAvailability setting =
                await service.FeatureAvailabilityAsync(cancellationToken).ConfigureAwait(false);

            return ApiResults.Ok(new { features = setting });
        });

        api.MapGet("/admin/settings/features", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            PublicFeatureAvailability setting =
                await service.FeatureAvailabilityAsync(cancellationToken).ConfigureAwait(false);

            return ApiResults.Ok(new { features = setting });
        });

        api.MapPatch("/admin/settings/features", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                // 关键：以当前值为基底做"仅覆盖请求里出现的字段"的合并。
                // 不能直接反序列化到基底对象——STJ 会把缺失字段写成类型默认值，
                // 而 Go 的 json.Unmarshal 只覆盖 JSON 里出现的字段，两者语义不同。
                JsonElement? patch = await ReadJsonElementAsync(
                    context, PatchBodyLimit, cancellationToken).ConfigureAwait(false);

                if (patch is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }

                PublicFeatureAvailability current =
                    await service.FeatureAvailabilityAsync(cancellationToken).ConfigureAwait(false);

                FeatureAvailability merged = new()
                {
                    WelcomeEnabled = ReadBool(patch.Value, "welcomeEnabled") ?? current.WelcomeEnabled,
                    ShortDramaEnabled = ReadBool(patch.Value, "shortDramaEnabled") ?? current.ShortDramaEnabled,
                    TaskCenterEnabled = ReadBool(patch.Value, "taskCenterEnabled") ?? current.TaskCenterEnabled,
                    CreditsEnabled = ReadBool(patch.Value, "creditsEnabled") ?? current.CreditsEnabled,
                    CustomChannelsEnabled = ReadBool(patch.Value, "customChannelsEnabled") ?? current.CustomChannelsEnabled,
                    FrontendModelsEnabled = ReadBool(patch.Value, "frontendModelsEnabled") ?? current.FrontendModelsEnabled,
                    PluginCenterEnabled = ReadBool(patch.Value, "pluginCenterEnabled") ?? current.PluginCenterEnabled,
                    SystemPluginsVisibleToUsers = ReadBool(patch.Value, "systemPluginsVisibleToUsers") ?? current.SystemPluginsVisibleToUsers,
                    TimelineTranscriptionEnabled = ReadBool(patch.Value, "timelineTranscriptionEnabled") ?? current.TimelineTranscriptionEnabled,
                };

                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                PublicFeatureAvailability updated = await service
                    .UpdateFeatureAvailabilityAsync(merged, actor, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { features = updated });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>读取请求体里的布尔字段；字段不存在或类型不对时返回 null（表示保持当前值）。</summary>
    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static async Task<JsonElement?> ReadJsonElementAsync(
        HttpContext context,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maxBytes)
        {
            return null;
        }

        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                context.Request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> TryRequireUserAsync(
        HttpContext context,
        CanvasService service,
        CancellationToken cancellationToken)
    {
        try
        {
            await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            IResult result = ApiResults.FailService(error, context);
            await result.ExecuteAsync(context).ConfigureAwait(false);
            return false;
        }
    }

    private static async Task<bool> TryRequireAdminAsync(
        HttpContext context,
        CanvasService service,
        CancellationToken cancellationToken)
    {
        try
        {
            User user = await service.CurrentUserAsync(
                SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
            CanvasService.RequireAdmin(user);
            return true;
        }
        catch (Exception error)
        {
            IResult result = ApiResults.FailService(error, context);
            await result.ExecuteAsync(context).ConfigureAwait(false);
            return false;
        }
    }

}
