using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Platform;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 平台设置路由：运行时策略、绘图工具、模型响应拦截、方舟素材库、公告配图上传。
/// 对应 Go: <c>handler/auth.go</c> 设置部分、<c>handler/libtv.go</c> 之外的平台设置、
/// <c>handler/announcement.go</c> 配图上传。
/// </summary>
public static class AdminPlatformSettingsEndpoints
{
    public static void MapAdminPlatformSettingsRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        PlatformSettingsService platformSettings,
        ResourceUploadService uploads,
        IRateLimiter limiter,
        IRuntimePolicyProvider policyProvider)
    {
        // ------------------------------------------------------------ 运行时策略

        api.MapGet("/admin/settings/runtime-policy", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                PublicRuntimePolicySettingDto setting = await platformSettings
                    .AdminRuntimePolicySettingAsync(actor, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/admin/settings/runtime-policy/self-use", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                PublicRuntimePolicySettingDto setting = await platformSettings
                    .AdminSelfUseRuntimePolicyAsync(actor, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPut("/admin/settings/runtime-policy", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                Platform.RuntimePolicySetting? request = await ReadJsonAsync<Platform.RuntimePolicySetting>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                PublicRuntimePolicySettingDto setting = await platformSettings
                    .UpdateRuntimePolicySettingAsync(actor, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/admin/settings/runtime-policy", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                PublicRuntimePolicySettingDto setting = await platformSettings
                    .ResetRuntimePolicySettingAsync(actor, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // ------------------------------------------------------------ 绘图工具

        api.MapGet("/admin/settings/drawing-engine", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                CanvasService.RequireAdmin(actor);
                PublicDrawingEngineSetting setting = await service.DrawingEngine
                    .GetAsync(cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/admin/settings/drawing-engine", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                DrawingEngineSetting? request = await ReadJsonAsync<DrawingEngineSetting>(
                    context, 0, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                PublicDrawingEngineSetting setting = await service.DrawingEngine
                    .UpdateAsync(request, actor.ID, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // ------------------------------------------------------------ 模型响应拦截

        api.MapGet("/admin/settings/response-interception", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ResponseInterceptionSettingDto setting = await platformSettings
                    .AdminResponseInterceptionSettingAsync(actor, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/admin/settings/response-interception", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ResponseInterceptionSettingDto? request = await ReadJsonAsync<ResponseInterceptionSettingDto>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                ResponseInterceptionSettingDto setting = await platformSettings
                    .UpdateResponseInterceptionSettingAsync(actor, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // ------------------------------------------------------------ 方舟素材库

        api.MapGet("/admin/settings/ark-private-assets", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                PublicArkPrivateAssetSettingDto setting = await platformSettings
                    .AdminArkPrivateAssetSettingAsync(actor, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/admin/settings/ark-private-assets", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ArkPrivateAssetSettingRequestDto? request = await ReadJsonAsync<ArkPrivateAssetSettingRequestDto>(
                    context, 0, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                PublicArkPrivateAssetSettingDto setting = await platformSettings
                    .UpdateArkPrivateAssetSettingAsync(actor, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // ------------------------------------------------------------ 公告配图上传

        api.MapPost("/admin/announcement-images", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                RuntimePolicySetting policy = policyProvider.Current();
                if (!await AuthEndpoints.EnforceRateLimitAsync(
                        context, limiter, "admin-announcement-image-upload:" + actor.ID,
                        policy.Request.ResourceUploadPerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }
                long maxBytes = AnnouncementService.AnnouncementImageMaxBytes + (1 << 20);
                if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maxBytes)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
                IFormFile? file = form.Files.GetFile("file");
                if (file is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Resource resource = await service.Announcements.UploadAnnouncementImageAsync(
                    uploads, actor, file.FileName, file.Length, file.OpenReadStream(),
                    cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { resource });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    private static async Task<T?> ReadJsonAsync<T>(
        HttpContext context, long maxBytes, CancellationToken cancellationToken)
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
                OpenAICanvas.Web.Serialization.CanvasJson.ReadOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            return null;
        }
    }
}
