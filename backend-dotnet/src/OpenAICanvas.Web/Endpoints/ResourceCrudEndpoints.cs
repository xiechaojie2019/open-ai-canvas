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
/// 资源 CRUD 与导入路由。对应 Go: <c>handler/user_data.go</c> 的 resources 部分
/// （/resources 列表、详情、整传、URL 导入、存储用量、OSS 直链、ARK 同步）。
/// </summary>
public static class ResourceCrudEndpoints
{
    public static void MapResourceCrudRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        ResourceUploadService uploads,
        ResourceDomainService resources,
        IRateLimiter limiter,
        IRuntimePolicyProvider policyProvider)
    {
        api.MapGet("/resources", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                long pageSize = ParsePositiveQueryInt(context.Request.Query["pageSize"].ToString(), 200);
                if (pageSize < 0)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                IReadOnlyList<Resource> list = await resources
                    .ListResourcesAsync(user.ID, pageSize, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { resources = list });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/resources/storage-usage", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AccountFileStorageUsage usage = await resources
                    .AccountFileStorageUsageAsync(user.ID, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { usage });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/resources/{id}", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                Resource? resource = await resources
                    .GetResourceAsync(user.ID, id, cancellationToken).ConfigureAwait(false);
                if (resource is null)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
                }
                return ApiResults.Ok(new { resource });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/resources/{id}/oss-url", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                Resource? resource = await resources
                    .GetResourceAsync(user.ID, id, cancellationToken).ConfigureAwait(false);
                if (resource is null)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status404NotFound, new InvalidOperationException("record not found"));
                }
                string ossUrl = await resources
                    .DirectResourceUrlAsync(user.ID, resource.ID, cancellationToken).ConfigureAwait(false);
                // 签名地址只用于当前复制动作，禁止浏览器或中间代理缓存。
                context.Response.GetTypedHeaders().CacheControl = new Microsoft.Net.Http.Headers.CacheControlHeaderValue
                {
                    Private = true,
                    NoStore = true,
                };
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                return ApiResults.Ok(new { url = ossUrl });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/resources", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                RuntimePolicySetting policy = policyProvider.Current();
                if (!await AuthEndpoints.EnforceRateLimitAsync(
                        context, limiter, "resources-upload:" + user.ID,
                        policy.Request.ResourceUploadPerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }
                long maxBytes = (policy.Resource.ResourceUploadMB << 20) + (1 << 20);
                if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maxBytes)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException($"单个上传文件必须小于 {policy.Resource.ResourceUploadMB}MB"));
                }
                IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
                IFormFile? file = form.Files.GetFile("file");
                if (file is null)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException($"单个上传文件必须小于 {policy.Resource.ResourceUploadMB}MB"));
                }
                _ = int.TryParse(form["width"].ToString(), out int width);
                _ = long.TryParse(form["durationMs"].ToString(), out long durationMs);
                _ = int.TryParse(form["height"].ToString(), out int height);
                string kind = form["kind"].ToString();
                Resource resource = await uploads.UploadResourceFromStreamAsync(
                    user.ID, file.FileName, file.Length, kind, width, height, durationMs,
                    file.OpenReadStream(), form.Files.GetFile("file")?.ContentType,
                    context.Request.Headers["X-Idempotency-Key"].ToString() is { Length: > 0 } key ? key : null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { resource });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/resources/import", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                RuntimePolicySetting policy = policyProvider.Current();
                if (!await AuthEndpoints.EnforceRateLimitAsync(
                        context, limiter, "resources-import:" + user.ID,
                        policy.Request.ResourceImportPerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }
                ImportResourceRequest? request = await ReadJsonAsync<ImportResourceRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                Resource resource = await uploads.ImportResourceUrlAsync(
                    user.ID, request.Url, request.Kind, request.Width, request.Height, request.DurationMs,
                    context.Request.Headers["X-Idempotency-Key"].ToString() is { Length: > 0 } key ? key : null,
                    cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { resource });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/resources/{id}/ark-private-asset", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                RuntimePolicySetting policy = policyProvider.Current();
                if (!await AuthEndpoints.EnforceRateLimitAsync(
                        context, limiter, "resources-ark-private-asset:" + user.ID,
                        policy.Request.ResourceImportPerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }
                await resources
                    .SyncResourceToArkPrivateAssetAsync(user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { sync = new { } });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>URL 导入请求。对应 Go 的匿名绑定 struct。</summary>
    public sealed class ImportResourceRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("kind")]
        public string Kind { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("width")]
        public int Width { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("height")]
        public int Height { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }
    }

    private static long ParsePositiveQueryInt(string value, long fallback)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }
        return long.TryParse(value, out long parsed) ? parsed : -1;
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
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            return null;
        }
    }
}
