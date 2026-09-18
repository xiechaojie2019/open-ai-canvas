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

/// <summary>
/// 系统渠道管理路由。对应 Go: <c>internal/handler/auth.go</c> 的渠道部分与
/// <c>internal/handler/finance.go</c> 的渠道模型部分。
/// </summary>
/// <remarks>
/// 渠道模型的 fetch/import/test 依赖出站 HTTP 客户端，属阶段 5/10，另行接线。
/// </remarks>
public static class ChannelEndpoints
{
    public static void MapChannelRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        IRateLimiter limiter)
    {
        api.MapGet("/admin/channels", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                (long page, long limit) = ParsePagination(context, fallbackPageSize: 20);
                AdminChannelPageDto result = await service.AdminChannelPageAsync(
                    actor,
                    context.Request.Query["keyword"].ToString(),
                    context.Request.Query["status"].ToString(),
                    page,
                    limit,
                    cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/admin/channels", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ChannelRequest? request = await ReadJsonAsync<ChannelRequest>(
                    context, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                PublicModelChannelDto channel = await service.CreateSystemChannelAsync(
                    actor, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { channel });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/admin/channels/{id}/duplicate", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                PublicModelChannelDto channel = await service.DuplicateSystemChannelAsync(
                    actor, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { channel });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/admin/channels/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ChannelRequest? request = await ReadJsonAsync<ChannelRequest>(
                    context, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                PublicModelChannelDto channel = await service.UpdateSystemChannelAsync(
                    actor, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { channel });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/admin/channels/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.DeleteSystemChannelAsync(actor, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { ok = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/admin/channels/{id}/models", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyList<ChannelModel> models = await service.AdminChannelModelsAsync(
                    actor, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { models });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/admin/channels/{id}/models", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ChannelModelRequest? request = await ReadJsonAsync<ChannelModelRequest>(
                    context, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                ChannelModel model = await service.SaveAdminChannelModelAsync(
                    actor, id, "", request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { model });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/admin/channels/{id}/models/{modelId}", async (
            HttpContext context,
            string id,
            string modelId,
            CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ChannelModelRequest? request = await ReadJsonAsync<ChannelModelRequest>(
                    context, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                ChannelModel model = await service.SaveAdminChannelModelAsync(
                    actor, id, modelId, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { model });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/admin/channels/{id}/models/{modelId}", async (
            HttpContext context,
            string id,
            string modelId,
            CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                await service.DeleteAdminChannelModelAsync(actor, id, modelId, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new { ok = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/admin/channels/{id}/models/batch-delete", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                BatchDeleteRequest? request = await ReadJsonAsync<BatchDeleteRequest>(
                    context, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                long deleted = await service.DeleteAdminChannelModelsAsync(
                    actor, id, request.ModelIds ?? [], cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { deleted });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/admin/channels/{id}/models/{modelId}/sort", async (
            HttpContext context,
            string id,
            string modelId,
            CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                SortRequest? request = await ReadJsonAsync<SortRequest>(
                    context, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }
                await service.UpdateAdminChannelModelSortAsync(
                    actor, id, modelId, request.SortOrder, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { updated = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // 上游模型目录拉取与导入。对应 Go 的 handler/finance.go fetch/import 路由。
        api.MapPost("/admin/channels/{id}/models/fetch", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                if (!await AuthEndpoints.EnforceRateLimitAsync(
                        context, limiter, "admin-channel-models-fetch:" + actor.ID + ":" + id,
                        10, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }
                IReadOnlyList<string> models = await service.PreviewAdminChannelModelsAsync(
                    actor, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { models });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/admin/channels/{id}/models/import", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                if (!await AuthEndpoints.EnforceRateLimitAsync(
                        context, limiter, "admin-channel-models-import:" + actor.ID + ":" + id,
                        10, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }

                ImportRequest? request = await ReadJsonAsync<ImportRequest>(
                    context, cancellationToken, maxBytes: 64 << 10).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                }

                ChannelModelAdminService.AdminChannelModelFetchResultDto result = await service
                    .ImportAdminChannelModelsAsync(actor, id, request.Models, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // 渠道/渠道模型排序。对应 Go 的 registerChannelOrderRoutes（两条路径循环注册）。
        foreach (string orderPath in new[] { "/admin/channels/order", "/admin/channels/{id}/models/order" })
        {
            string path = orderPath;
            api.MapGet(path, async (HttpContext context, CancellationToken cancellationToken) =>
            {
                try
                {
                    User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                        .ConfigureAwait(false);
                    string? id = context.Request.RouteValues.TryGetValue("id", out object? value)
                        ? value?.ToString()
                        : null;
                    IReadOnlyList<ChannelAdminService.ChannelOrderItemDto> items = await service
                        .AdminChannelOrderAsync(actor, id ?? "", cancellationToken).ConfigureAwait(false);
                    return ApiResults.Ok(new { items });
                }
                catch (Exception error)
                {
                    return ApiResults.FailService(error, context);
                }
            });

            api.MapPut(path, async (HttpContext context, CancellationToken cancellationToken) =>
            {
                try
                {
                    User actor = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                        .ConfigureAwait(false);
                    ChannelOrderRequest? request = await ReadJsonAsync<ChannelOrderRequest>(
                        context, cancellationToken, maxBytes: 1 << 20).ConfigureAwait(false);
                    if (request is null)
                    {
                        return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
                    }
                    string? id = context.Request.RouteValues.TryGetValue("id", out object? value)
                        ? value?.ToString()
                        : null;
                    await service.SaveAdminChannelOrderAsync(
                        actor, id ?? "", request.IDs, request.ExpectedIds, cancellationToken).ConfigureAwait(false);
                    return ApiResults.Ok(new { saved = true });
                }
                catch (Exception error)
                {
                    return ApiResults.FailService(error, context);
                }
            });
        }
    }

    private sealed class ChannelOrderRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("ids")]
        public List<string>? IDs { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("expectedIds")]
        public List<string>? ExpectedIds { get; set; }
    }

    private sealed class SortRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("sortOrder")]
        public long? SortOrder { get; set; }
    }

    private sealed class ImportRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("models")]
        public List<string>? Models { get; set; }
    }

    private sealed class BatchDeleteRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("modelIds")]
        public List<string>? ModelIds { get; set; }
    }

    /// <summary>对应 Go: <c>parsePaginationQuery</c>。非法值回 400（由调用方处理 error）。</summary>
    private static (long Page, long Limit) ParsePagination(HttpContext context, int fallbackPageSize)
    {
        long page = ParsePositiveQueryInt(context.Request.Query["page"].ToString(), 1);
        long pageSize = ParsePositiveQueryInt(
            context.Request.Query["pageSize"].ToString(), fallbackPageSize);
        return (page, pageSize);
    }

    private static long ParsePositiveQueryInt(string raw, long fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }
        return long.TryParse(raw, out long parsed) && parsed > 0 ? parsed : fallback;
    }

    private static async Task<T?> ReadJsonAsync<T>(
        HttpContext context,
        CancellationToken cancellationToken,
        long maxBytes = 0)
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
                CanvasJson.ReadOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (BadHttpRequestException)
        {
            return null;
        }
    }
}
