using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 创作端前台模型目录路由。对应 Go: <c>internal/handler/logical_models.go: RegisterLogicalModelRoutes</c>
/// 的公开读取部分（<c>/models</c> 与 <c>/models/available</c>）。
/// </summary>
/// <remarks>
/// 管理端路由（<c>/admin/logical-models</c> 系列）与报价路由（<c>/models/:id/quote</c>）
/// 属阶段 3，另行接线。
/// </remarks>
public static class ModelEndpoints
{
    public static void MapModelRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        api.MapGet("/models", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyList<PublicLogicalModelDto> models = await service
                    .PublicLogicalModelsAsync(null, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { models });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/model-catalog", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User _ = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ModelCatalogResponseDto catalog = await service.ModelCatalog.CatalogAsync(
                    null, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(catalog);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/model-catalog/available", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User _ = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                ModelRequestIntent? intent = await ReadJsonAsync<ModelRequestIntent>(
                    context, cancellationToken).ConfigureAwait(false);
                ModelCatalogResponseDto catalog = await service.ModelCatalog.CatalogAsync(
                    intent, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(catalog);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/models/available", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }

            ModelRequestIntent? intent;
            try
            {
                // Go 的 json.Unmarshal 对字面量 null 不报错（零值 intent）；
                // 只有真正的语法错误才回 400。
                JsonElement? raw = await JsonSerializer.DeserializeAsync<JsonElement?>(
                    context.Request.Body,
                    CanvasJson.ReadOptions,
                    cancellationToken).ConfigureAwait(false);
                if (raw is null or { ValueKind: JsonValueKind.Null })
                {
                    intent = new ModelRequestIntent();
                }
                else
                {
                    intent = raw.Value.Deserialize<ModelRequestIntent>(CanvasJson.ReadOptions);
                }
            }
            catch (JsonException)
            {
                intent = null;
            }
            catch (BadHttpRequestException)
            {
                intent = null;
            }

            // Go: ShouldBindJSON 失败 → fail(c, 400, errors.New("模型能力请求格式错误"))。
            if (intent is null)
            {
                return ApiResults.Fail(
                    StatusCodes.Status400BadRequest,
                    new InvalidOperationException("模型能力请求格式错误"));
            }

            try
            {
                IReadOnlyList<PublicLogicalModelDto> models = await service
                    .PublicLogicalModelsAsync(intent, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { models });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        MapAdminLogicalModelRoutes(api, service);
    }

    /// <summary>
    /// 前台模型管理端路由与报价路由。
    /// 对应 Go: <c>RegisterLogicalModelRoutes</c> 的剩余部分。
    /// </summary>
    private static void MapAdminLogicalModelRoutes(IEndpointRouteBuilder api, CanvasService service)
    {
        api.MapGet("/admin/logical-models", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                Domain.Entities.User actor = await service
                    .CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                IReadOnlyList<AdminLogicalModelDto> models = await service
                    .AdminLogicalModelsAsync(actor, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { models });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/admin/logical-models", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                Domain.Entities.User actor = await service
                    .CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                LogicalModelRequest? request = await ReadJsonAsync<LogicalModelRequest>(
                    context, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    // Go: fail(c, 400, errors.New("前台模型参数格式错误"))。
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException("前台模型参数格式错误"));
                }

                AdminLogicalModelDto model = await service
                    .SaveAdminLogicalModelAsync(actor, "", request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { model });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/admin/logical-models/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                Domain.Entities.User actor = await service
                    .CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                LogicalModelRequest? request = await ReadJsonAsync<LogicalModelRequest>(
                    context, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException("前台模型参数格式错误"));
                }

                AdminLogicalModelDto model = await service
                    .SaveAdminLogicalModelAsync(actor, id, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { model });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapDelete("/admin/logical-models/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                Domain.Entities.User actor = await service
                    .CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                await service.DeleteAdminLogicalModelAsync(actor, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { ok = true });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/admin/logical-models/{id}/simulate", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                Domain.Entities.User actor = await service
                    .CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }

            ModelRequestIntent? intent = await ReadIntentAsync(context, cancellationToken).ConfigureAwait(false);
            if (intent is null)
            {
                // Go: fail(c, 400, errors.New("路由模拟请求格式错误"))。
                return ApiResults.Fail(
                    StatusCodes.Status400BadRequest,
                    new InvalidOperationException("路由模拟请求格式错误"));
            }

            try
            {
                Domain.Entities.User actor = await service
                    .CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                RouteSimulationResultDto result = await service
                    .SimulateLogicalModelRouteAsync(id, intent, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/models/{id}/quote", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }

            ModelRequestIntent? intent = await ReadIntentAsync(context, cancellationToken).ConfigureAwait(false);
            if (intent is null)
            {
                // Go: fail(c, 400, errors.New("模型报价请求格式错误"))。
                return ApiResults.Fail(
                    StatusCodes.Status400BadRequest,
                    new InvalidOperationException("模型报价请求格式错误"));
            }

            try
            {
                LogicalModelQuoteDto quote = await service
                    .QuoteLogicalModelAsync(id, intent, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { quote });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    /// <summary>读取意图请求体。字面量 null 视为零值意图（Go json.Unmarshal 语义）。</summary>
    private static async Task<ModelRequestIntent?> ReadIntentAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            JsonElement? raw = await JsonSerializer.DeserializeAsync<JsonElement?>(
                context.Request.Body,
                CanvasJson.ReadOptions,
                cancellationToken).ConfigureAwait(false);
            if (raw is null or { ValueKind: JsonValueKind.Null })
            {
                return new ModelRequestIntent();
            }
            return raw.Value.Deserialize<ModelRequestIntent>(CanvasJson.ReadOptions);
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

    /// <summary>读取 JSON 请求体，语法错误返回 null。</summary>
    private static async Task<T?> ReadJsonAsync<T>(
        HttpContext context,
        CancellationToken cancellationToken)
        where T : class
    {
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
