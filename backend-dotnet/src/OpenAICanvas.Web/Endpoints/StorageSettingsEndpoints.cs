using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>平台和用户对象存储设置路由。</summary>
public static class StorageSettingsEndpoints
{
    private const long BodyLimit = 64 << 10;

    public static void MapStorageSettingsRoutes(this IEndpointRouteBuilder api, CanvasService service,
        StorageSettingsService settings, IRateLimiter limiter)
    {
        api.MapGet("/admin/settings/oss", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                PublicOSSSetting setting = await settings.AdminOSSSettingAsync(actor, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error) { return ApiResults.FailService(error, context); }
        });
        api.MapPatch("/admin/settings/oss", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                OSSSettingRequest? request = await ReadJsonAsync(context, cancellationToken).ConfigureAwait(false);
                if (request is null) return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                PublicOSSSetting setting = await settings.UpdateAdminOSSSettingAsync(actor, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error) { return ApiResults.FailService(error, context); }
        });
        api.MapPost("/admin/settings/oss/test", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                if (!await AuthEndpoints.EnforceRateLimitAsync(context, limiter, "admin-storage-test:" + actor.ID, 6, TimeSpan.FromMinutes(1)).ConfigureAwait(false)) return Results.Empty;
                OSSSettingRequest? request = await ReadJsonAsync(context, cancellationToken).ConfigureAwait(false);
                if (request is null) return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                return ApiResults.Ok(await settings.TestAdminOSSSettingAsync(actor, request, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception error) { return ApiResults.FailService(error, context); }
        });
        api.MapGet("/settings/oss", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                PublicOSSSetting setting = await settings.UserOSSSettingAsync(actor, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error) { return ApiResults.FailService(error, context); }
        });
        api.MapPatch("/settings/oss", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                OSSSettingRequest? request = await ReadJsonAsync(context, cancellationToken).ConfigureAwait(false);
                if (request is null) return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                PublicOSSSetting setting = await settings.UpdateUserOSSSettingAsync(actor, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (Exception error) { return ApiResults.FailService(error, context); }
        });
        api.MapPost("/settings/oss/test", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                if (!await AuthEndpoints.EnforceRateLimitAsync(context, limiter, "user-storage-test:" + actor.ID, 6, TimeSpan.FromMinutes(1)).ConfigureAwait(false)) return Results.Empty;
                OSSSettingRequest? request = await ReadJsonAsync(context, cancellationToken).ConfigureAwait(false);
                if (request is null) return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                return ApiResults.Ok(await settings.TestUserOSSSettingAsync(actor, request, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception error) { return ApiResults.FailService(error, context); }
        });
    }

    private static async Task<OSSSettingRequest?> ReadJsonAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is > BodyLimit) return null;
        try { return await JsonSerializer.DeserializeAsync<OSSSettingRequest>(context.Request.Body, CanvasJson.ReadOptions, cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { return null; }
        catch (BadHttpRequestException) { return null; }
    }
}
