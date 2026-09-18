using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Platform;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 本地媒体分片上传路由（开始 / 上传片 / 合并）。
/// 对应 Go: <c>internal/handler/resource_upload_session.go</c> 的 <c>RegisterChunkedUploadRoutes</c>。
/// </summary>
public static class ChunkedUploadEndpoints
{
    /// <summary>JSON 请求体上限 16KB。对应 Go: <c>chunkUploadBodyCapJSON</c>。</summary>
    private const long JsonBodyCap = 16 << 10;

    public static void MapChunkedUploadRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        ResourceUploadService uploads,
        UploadQuota quota,
        ChunkedUploadSessions sessions,
        IRateLimiter limiter,
        IRuntimePolicyProvider policyProvider)
    {
        // POST /api/resources/uploads —— 开始分片会话
        api.MapPost("/resources/uploads", async (HttpContext context, CancellationToken cancellationToken) =>
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

                ChunkUploadStartRequest? request = await ReadJsonAsync<ChunkUploadStartRequest>(
                    context, JsonBodyCap, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                if (string.IsNullOrEmpty(request.FileName) || request.FileName.Length > 255)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest,
                        new InvalidOperationException("文件名不能为空且不能超过 255 个字符"));
                }
                if (request.Size <= 0)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest,
                        new InvalidOperationException("文件大小必须大于 0"));
                }
                // 超账号存储总量的文件无论如何都会失败，提前给出明确提示。
                if (policy.Resource.StoredFileGB > 0 && request.Size > policy.Resource.StoredFileGB << 30)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest,
                        new InvalidOperationException($"文件超过账号存储总量上限 {policy.Resource.StoredFileGB}GB"));
                }
                // 同一用户并发会话数兜底，防内存占用失控。
                if (sessions.CountForUser(user.ID) >= ChunkedUploadSessions.MaxPerUser)
                {
                    return ApiResults.Fail(StatusCodes.Status429TooManyRequests,
                        new InvalidOperationException("同时进行中的上传过多，请稍后重试"));
                }

                ChunkedUploadSession session = sessions.Create(
                    user.ID,
                    request.FileName,
                    request.Kind ?? string.Empty,
                    request.Size,
                    request.Width,
                    request.Height,
                    request.DurationMs,
                    request.IdempotencyKey);

                return ApiResults.Ok(new
                {
                    uploadId = session.ID,
                    chunkSize = ChunkedUploadSessions.ChunkSize,
                    chunkCount = session.ChunkCount,
                });
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

        // PUT /api/resources/uploads/:id/chunks/:index —— 上传单片
        api.MapPut("/resources/uploads/{id}/chunks/{index}", async (
            HttpContext context, string id, string index, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ChunkedUploadSession? session = sessions.Take(id);
                if (session is null || session.UserID != user.ID)
                {
                    return ApiResults.Fail(StatusCodes.Status404NotFound,
                        new InvalidOperationException("上传会话不存在或已过期，请重新导入"));
                }
                if (!int.TryParse(index, out int chunkIndex) || chunkIndex < 0 || chunkIndex >= session.ChunkCount)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest,
                        new InvalidOperationException("非法的分片序号"));
                }
                long expected = session.ChunkSizeAt(chunkIndex);
                if (expected <= 0)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest,
                        new InvalidOperationException("非法的分片序号"));
                }

                string chunkPath = session.ChunkPath(chunkIndex);
                try
                {
                    using FileStream destination = new(
                        chunkPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    long written = await CopyExactlyAsync(
                        context.Request.Body, destination, expected, cancellationToken).ConfigureAwait(false);
                    if (written != expected)
                    {
                        TryDelete(chunkPath);
                        return ApiResults.Fail(StatusCodes.Status400BadRequest,
                            new InvalidOperationException($"分片 {chunkIndex} 上传不完整，请重试"));
                    }
                    // 超片检测：期望长度之外还有剩余字节即判定超限。
                    if (await HasExtraBytesAsync(context.Request.Body, cancellationToken).ConfigureAwait(false))
                    {
                        TryDelete(chunkPath);
                        return ApiResults.Fail(StatusCodes.Status400BadRequest,
                            new InvalidOperationException($"分片 {chunkIndex} 超过大小限制"));
                    }
                }
                catch (Exception error) when (error is not AppError)
                {
                    TryDelete(chunkPath);
                    return ApiResults.FailService(error, context);
                }

                return ApiResults.Ok(new { index = chunkIndex });
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

        // POST /api/resources/uploads/:id/complete —— 合并并入库
        api.MapPost("/resources/uploads/{id}/complete", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ChunkedUploadSession? session = sessions.Take(id);
                if (session is null || session.UserID != user.ID)
                {
                    return ApiResults.Fail(StatusCodes.Status404NotFound,
                        new InvalidOperationException("上传会话不存在或已过期，请重新导入"));
                }
                if (!session.HasAllChunks())
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest,
                        new InvalidOperationException("上传文件不完整，请重新导入"));
                }

                string mergedPath = session.MergedPath();
                long total = 0;
                try
                {
                    using FileStream merged = new(mergedPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    for (int i = 0; i < session.ChunkCount; i++)
                    {
                        using FileStream part = new(session.ChunkPath(i), FileMode.Open, FileAccess.Read, FileShare.Read);
                        await part.CopyToAsync(merged, cancellationToken).ConfigureAwait(false);
                        total += part.Length;
                    }
                }
                catch (Exception error)
                {
                    return ApiResults.FailService(error, context);
                }

                if (total != session.Size)
                {
                    sessions.Drop(id);
                    return ApiResults.Fail(StatusCodes.Status400BadRequest,
                        new InvalidOperationException("上传文件不完整，请重新导入"));
                }

                Resource resource;
                try
                {
                    using FileStream file = new(mergedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    resource = await uploads.UploadResourceFileAsync(
                        user.ID,
                        session.FileName,
                        session.Size,
                        session.Kind,
                        session.Width,
                        session.Height,
                        session.DurationMs,
                        file,
                        session.IdempotencyKey,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    // 无论成败都结束会话：失败时前端会整传重试，不需要保留残片。
                    sessions.Drop(id);
                }

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

    /// <summary>把 <paramref name="source"/> 的前 <paramref name="expected"/> 字节精确写入目标流。</summary>
    private static async Task<long> CopyExactlyAsync(
        Stream source, Stream destination, long expected, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 << 10];
        long written = 0;
        while (written < expected)
        {
            int want = (int)Math.Min(buffer.Length, expected - written);
            int read = await source.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
        }
        return written;
    }

    /// <summary>请求体在期望长度之外是否还有剩余字节。对应 Go 的探测式读取。</summary>
    private static async Task<bool> HasExtraBytesAsync(Stream body, CancellationToken cancellationToken)
    {
        byte[] probe = new byte[1];
        int read = await body.ReadAsync(probe.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return read > 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Go 此处忽略删除失败。
        }
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
                CanvasJson.ReadOptions,
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

    /// <summary>
    /// 开始分片上传请求体。对应 Go 的匿名 struct
    /// <c>{fileName, kind, size, width, height, durationMs, idempotencyKey}</c>。
    /// </summary>
    private sealed class ChunkUploadStartRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("fileName")]
        public string FileName { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("size")]
        public long Size { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("width")]
        public int Width { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("height")]
        public int Height { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("durationMs")]
        public long DurationMs { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("idempotencyKey")]
        public string? IdempotencyKey { get; init; }
    }
}
