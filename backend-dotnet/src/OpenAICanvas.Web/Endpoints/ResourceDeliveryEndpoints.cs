using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 资源文件下发路由（鉴权下发 + 匿名签名下发）。
/// 对应 Go: <c>internal/handler/user_data.go</c> 的
/// <c>GET /resources/:id/file</c> 与 <c>GET /public/resources/:id/file[/:filename]</c>。
/// </summary>
public static class ResourceDeliveryEndpoints
{
    public static void MapResourceDeliveryRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        ResourceDomainService resources)
    {
        // GET /api/resources/{id}/file?direct=1&proxy=1&variant=playback
        api.MapGet("/resources/{id}/file", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ResourceDelivery delivery = await resources.PrepareResourceDeliveryAsync(
                    user.ID, id, new ResourceDeliveryOptions(
                        ForceDirect: context.Request.Query["direct"] == "1",
                        ForceProxy: context.Request.Query["proxy"] == "1"),
                    cancellationToken).ConfigureAwait(false);

                // CDN / 对象存储直连：允许安全短期缓存后 307。
                if (delivery.RedirectURL.Length > 0)
                {
                    context.Response.Headers["Cache-Control"] =
                        "private, max-age=86400, stale-while-revalidate=3600";
                    context.Response.Headers["Referrer-Policy"] = "no-referrer";
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    return Results.Redirect(delivery.RedirectURL, permanent: false, preserveMethod: true);
                }

                Resource resource = delivery.Resource;
                string etag = ResourceDomainService.ResourceResponseETag(resource);
                // variant=playback：浏览器兼容播放副本。副本就绪时用独立 ETag 后缀，
                // 避免浏览器命中原件缓存 304 而继续黑屏。
                bool usePlayback = context.Request.Query["variant"] == "playback"
                    && string.Equals(resource.Provider, "local", StringComparison.OrdinalIgnoreCase)
                    && resource.PlaybackStatus == "ready"
                    && !string.IsNullOrEmpty(resource.PlaybackObjectKey);
                string serveETag = usePlayback ? etag + ":pb" : etag;

                // 资源 ID 内容不可变：图片可交给浏览器磁盘强缓存 30 天；
                // 视频/音频涉及转码副本与 Range，保持逐次条件请求。
                context.Response.Headers["Cache-Control"] = resource.MimeType.StartsWith("image/", StringComparison.Ordinal)
                    ? "private, max-age=2592000, stale-while-revalidate=86400"
                    : "private, no-cache";
                context.Response.Headers["ETag"] = serveETag;
                context.Response.Headers["Accept-Ranges"] = "bytes";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (resource.Kind == "file")
                {
                    context.Response.Headers["Content-Disposition"] = "attachment";
                    context.Response.Headers["Content-Security-Policy"] = "sandbox";
                }

                if (ResourceDomainService.IfNoneMatch(context.Request.Headers["If-None-Match"], serveETag))
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                string rangeHeader = context.Request.Headers["Range"].ToString();
                string ifRange = (context.Request.Headers["If-Range"].ToString() ?? string.Empty).Trim();
                if (ifRange.Length > 0 && ifRange != serveETag)
                {
                    rangeHeader = string.Empty;
                }

                // 转码副本未移植（PlaybackStatus 恒为空）：与 Go 的「副本未就绪回退原件」分支等价。
                if (usePlayback)
                {
                    context.Response.Headers["ETag"] = etag;
                }

                await using Stream? deliveryStream = delivery.Stream;
                ResourceStream stream = deliveryStream is not null
                    ? new ResourceStream(delivery.Resource, deliveryStream, delivery.ContentRange, delivery.AcceptRanges)
                        { ContentLength = delivery.Resource.Size }
                    : await resources.OpenResourceRangeAsync(user.ID, resource.ID, rangeHeader, cancellationToken)
                        .ConfigureAwait(false);

                return await WriteResourceAsync(context, stream, resource, cancellationToken).ConfigureAwait(false);
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

        // GET /api/public/resources/{id}/file 与 /file/{filename}
        IResult PublicHandler(HttpContext context, string id)
        {
            try
            {
                ResourceStream stream = resources.OpenPublicResourceRangeAsync(
                    id,
                    context.Request.Query["expires"],
                    context.Request.Query["signature"],
                    context.Request.Headers["Range"].ToString()).GetAwaiter().GetResult();

                return WritePublicResourceAsync(context, stream).GetAwaiter().GetResult();
            }
            catch (AppError error)
            {
                return ApiResults.FailService(error, context);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        }

        api.MapGet("/public/resources/{id}/file", (HttpContext context, string id) => PublicHandler(context, id));
        api.MapGet("/public/resources/{id}/file/{filename}", (HttpContext context, string id, string filename) =>
            PublicHandler(context, id));
    }

    /// <summary>
    /// 鉴权下发响应体：本地 provider 走单区间 Range（等价 Go 的 <c>http.ServeContent</c>），
    /// 不可满足时 416 并带 <c>Content-Range: bytes */size</c>。
    /// </summary>
    private static async Task<IResult> WriteResourceAsync(
        HttpContext context, ResourceStream stream, Resource resource, CancellationToken cancellationToken)
    {
        string mimeType = string.IsNullOrEmpty(resource.MimeType) ? "application/octet-stream" : resource.MimeType;
        await using (stream.Body.ConfigureAwait(false))
        {
            string rangeHeader = context.Request.Headers["Range"].ToString();
            (long start, long length, string contentRange)? resolved =
                ResourceDomainService.ResolveRange(resource.Size, rangeHeader);
            if (resolved is null)
            {
                context.Response.Headers["Content-Range"] = $"bytes */{resource.Size}";
                return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }

            (long offset, long bytes, string contentRangeValue) = resolved.Value;
            context.Response.Headers["Content-Type"] = mimeType;
            if (contentRangeValue.Length > 0)
            {
                context.Response.Headers["Content-Range"] = contentRangeValue;
                context.Response.StatusCode = StatusCodes.Status206PartialContent;
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
            }
            context.Response.ContentLength = bytes;

            bool sent = await CopyRangeAsync(stream.Body, context.Response.Body, offset, bytes, cancellationToken)
                .ConfigureAwait(false);
            if (!sent)
            {
                context.Response.ContentLength = null;
            }
            return Results.Empty;
        }
    }

    /// <summary>匿名下发响应体。与 Go 一致：额外带 Cache-Control 与 Referrer-Policy。</summary>
    private static async Task<IResult> WritePublicResourceAsync(HttpContext context, ResourceStream stream)
    {
        Resource resource = stream.Resource;
        string mimeType = string.IsNullOrEmpty(resource.MimeType) ? "application/octet-stream" : resource.MimeType;
        context.Response.Headers["Cache-Control"] = "public, max-age=0, must-revalidate";
        context.Response.Headers["Accept-Ranges"] = "bytes";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        await using (stream.Body.ConfigureAwait(false))
        {
            string rangeHeader = context.Request.Headers["Range"].ToString();
            (long start, long length, string contentRange)? resolved =
                ResourceDomainService.ResolveRange(resource.Size, rangeHeader);
            if (resolved is null)
            {
                context.Response.Headers["Content-Range"] = $"bytes */{resource.Size}";
                return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }
            (long offset, long bytes, string contentRangeValue) = resolved.Value;
            context.Response.Headers["Content-Type"] = mimeType;
            if (contentRangeValue.Length > 0)
            {
                context.Response.Headers["Content-Range"] = contentRangeValue;
                context.Response.StatusCode = StatusCodes.Status206PartialContent;
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
            }
            context.Response.ContentLength = bytes;
            await CopyRangeAsync(stream.Body, context.Response.Body, offset, bytes, CancellationToken.None)
                .ConfigureAwait(false);
            return Results.Empty;
        }
    }

    /// <summary>从 <paramref name="offset"/> 起精确拷贝 <paramref name="count"/> 字节。</summary>
    private static async Task<bool> CopyRangeAsync(
        Stream source, Stream destination, long offset, long count, CancellationToken cancellationToken)
    {
        if (source.CanSeek)
        {
            source.Seek(offset, SeekOrigin.Begin);
        }
        else
        {
            await SkipAsync(source, offset, cancellationToken).ConfigureAwait(false);
        }

        byte[] buffer = new byte[64 << 10];
        long remaining = count;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return false;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
        return true;
    }

    private static async Task SkipAsync(Stream source, long count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 << 10];
        long remaining = count;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return;
            }
            remaining -= read;
        }
    }
}
