using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Application;
using OpenAICanvas.Application.Appearance;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Platform;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 外观配置路由（公开读取 + 管理端读写 + 资源上传/下发）。
/// 对应 Go: <c>internal/handler/appearance.go</c> 的 <c>RegisterAppearanceRoutes</c>。
/// </summary>
public static class AppearanceEndpoints
{
    /// <summary>PATCH 请求体上限。对应 Go 的 <c>32&lt;&lt;10</c>。</summary>
    private const long PatchBodyCap = 32 << 10;

    public static void MapAppearanceRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        AppearanceService appearance,
        IRateLimiter limiter,
        IRuntimePolicyProvider policyProvider)
    {
        // GET /api/public/appearance
        api.MapGet("/public/appearance", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                PublicAppearanceSetting setting = await appearance.GetPublicAsync(cancellationToken)
                    .ConfigureAwait(false);
                context.Response.Headers["Cache-Control"] = "no-store";
                return ApiResults.Ok(new { appearance = setting });
            }
            catch (AppError error)
            {
                return ApiResults.FailService(error, context);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // GET /api/public/appearance/assets/{slot}?v=revision
        api.MapGet("/public/appearance/assets/{slot}", async (
            HttpContext context, string slot, CancellationToken cancellationToken) =>
        {
            try
            {
                ResourceStream stream = await appearance.OpenAssetAsync(
                    slot, context.Request.Headers["Range"].ToString(), cancellationToken).ConfigureAwait(false);
                await using (stream.Body.ConfigureAwait(false))
                {
                    Resource resource = stream.Resource;
                    string mimeType = string.IsNullOrEmpty(resource.MimeType)
                        ? "application/octet-stream"
                        : resource.MimeType;
                    // 带 v= 说明是内容寻址的不可变资源，可长缓存；否则须每次校验。
                    context.Response.Headers["Cache-Control"] = context.Request.Query["v"].ToString().Length > 0
                        ? "public, max-age=31536000, immutable"
                        : "public, no-cache";
                    context.Response.Headers["Accept-Ranges"] = "bytes";
                    context.Response.Headers["Referrer-Policy"] = "no-referrer";
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    if (stream.ContentRange.Length > 0)
                    {
                        context.Response.Headers["Content-Range"] = stream.ContentRange;
                    }
                    context.Response.ContentType = mimeType;

                    (long Start, long Length, string ContentRange)? range =
                        ResourceDomainService.ResolveRange(resource.Size, context.Request.Headers["Range"].ToString());
                    if (range is null)
                    {
                        context.Response.Headers["Content-Range"] = $"bytes */{resource.Size}";
                        return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
                    }
                    (long offset, long length, string contentRange) = range.Value;
                    if (contentRange.Length > 0)
                    {
                        context.Response.Headers["Content-Range"] = contentRange;
                        context.Response.StatusCode = StatusCodes.Status206PartialContent;
                    }
                    else
                    {
                        context.Response.StatusCode = StatusCodes.Status200OK;
                    }
                    context.Response.ContentLength = length;
                    await CopyRangeAsync(stream.Body, context.Response.Body, offset, length, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Empty;
                }
            }
            catch (AppError error)
            {
                return ApiResults.FailService(error, context);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // GET /api/admin/settings/appearance
        api.MapGet("/admin/settings/appearance", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AdminAppearanceSetting setting = await appearance.GetAdminAsync(actor, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (AppError error)
            {
                return ApiResults.FailService(error, context);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // PATCH /api/admin/settings/appearance —— 以当前值为基底做部分更新
        api.MapPatch("/admin/settings/appearance", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AppearanceSetting? request = await ReadJsonAsync<AppearanceSetting>(
                    context, PatchBodyCap, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest,
                        new InvalidOperationException("外观配置请求体无效"));
                }
                AdminAppearanceSetting setting = await appearance.UpdateAsync(actor, request, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (AppError error)
            {
                return ApiResults.FailService(error, context);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // DELETE /api/admin/settings/appearance
        api.MapDelete("/admin/settings/appearance", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AdminAppearanceSetting setting = await appearance.ResetAsync(actor, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { setting });
            }
            catch (AppError error)
            {
                return ApiResults.FailService(error, context);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // POST /api/admin/settings/appearance/assets/{slot} —— multipart 表单字段 file
        api.MapPost("/admin/settings/appearance/assets/{slot}", async (
            HttpContext context, string slot, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                RuntimePolicySetting policy = policyProvider.Current();
                if (!await AuthEndpoints.EnforceRateLimitAsync(
                        context, limiter, "admin-appearance-upload:" + actor.ID,
                        policy.Request.ResourceUploadPerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }

                IFormFile? file = context.Request.HasFormContentType
                    ? (await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false)).Files["file"]
                    : null;
                if (file is null || file.Length <= 0)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest,
                        new InvalidOperationException("请选择要上传的文件"));
                }

                await using Stream body = file.OpenReadStream();
                (byte[] head, string sniffed) = await SniffAsync(body, file.Length, cancellationToken)
                    .ConfigureAwait(false);
                AppearanceService.ValidateUpload(slot, file.Length, head, sniffed);

                Resource resource = await appearance.UploadAssetAsync(
                    actor, slot, file.FileName, file.Length, sniffed, body, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { resource });
            }
            catch (AppError error)
            {
                return ApiResults.FailService(error, context);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>
    /// 读取前 512 字节做嗅探，并把流位置复位。对应 Go 的 <c>header.Open()</c> + <c>file.Read(buffer)</c>。
    /// </summary>
    private static async Task<(byte[] Head, string MimeType)> SniffAsync(
        Stream body, long size, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[512];
        int read = 0;
        if (body.CanSeek)
        {
            read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            body.Seek(0, SeekOrigin.Begin);
        }
        byte[] head = buffer[..read];
        // 复用资源上传模块的嗅探实现（与 Go 的 http.DetectContentType 对齐）。
        string mimeType = ResourceUploadService.DetectUploadedMimeType(new MemoryStream(head, writable: false), "", "");
        return (head, mimeType);
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
            return await System.Text.Json.JsonSerializer.DeserializeAsync<T>(
                context.Request.Body,
                OpenAICanvas.Web.Serialization.CanvasJson.ReadOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (BadHttpRequestException)
        {
            return null;
        }
    }

    private static async Task CopyRangeAsync(
        Stream source, Stream destination, long offset, long count, CancellationToken cancellationToken)
    {
        if (offset > 0)
        {
            if (source.CanSeek)
            {
                source.Seek(offset, SeekOrigin.Begin);
            }
            else
            {
                byte[] skip = new byte[64 << 10];
                long remaining = offset;
                while (remaining > 0)
                {
                    int want = (int)Math.Min(skip.Length, remaining);
                    int read = await source.ReadAsync(skip.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        return;
                    }
                    remaining -= read;
                }
            }
        }
        byte[] buffer = new byte[64 << 10];
        long left = count;
        while (left > 0)
        {
            int want = (int)Math.Min(buffer.Length, left);
            int read = await source.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            left -= read;
        }
    }
}
