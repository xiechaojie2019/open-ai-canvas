#nullable enable
using System.Text.Json;
using OpenAICanvas.Application;
using OpenAICanvas.Application.Prompts;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 提示词模板管理路由。对应 Go: <c>handler/auth.go</c> 的 4 条
/// <c>/admin/prompt-templates</c>。
/// </summary>
public static class PromptTemplateEndpoints
{
    /// <summary>请求体上限 64KB。对应 Go: <c>64&lt;&lt;10</c>。</summary>
    private const long BodyLimit = 64L << 10;

    public static void MapPromptTemplateRoutes(this IEndpointRouteBuilder api, CanvasService service)
    {
        api.MapGet("/admin/prompt-templates", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                (IReadOnlyList<PromptTemplate> templates, List<PromptOperationDefinition> definitions) =
                    await service.PromptTemplates.AdminPromptTemplatesAsync(actor, cancellationToken)
                        .ConfigureAwait(false);

                return ApiResults.Ok(new { templates, definitions });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPost("/admin/prompt-templates", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            PromptTemplateRequest? request = await ReadJsonAsync<PromptTemplateRequest>(
                context, BodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                PromptTemplate template = await service.PromptTemplates
                    .CreatePromptTemplateAsync(actor, request, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { template });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapPatch("/admin/prompt-templates/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            PromptTemplateRequest? request = await ReadJsonAsync<PromptTemplateRequest>(
                context, BodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                PromptTemplate template = await service.PromptTemplates
                    .UpdatePromptTemplateAsync(actor, id, request, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { template });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapDelete("/admin/prompt-templates/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireAdminAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                await service.PromptTemplates
                    .DeletePromptTemplateAsync(actor, id, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });
    }

    // ------------------------------------------------------------ 辅助

    private static async Task<bool> TryRequireAdminAsync(
        HttpContext context, CanvasService service, CancellationToken cancellationToken)
    {
        try
        {
            User actor = await service.CurrentUserAsync(
                SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
            CanvasService.RequireAdmin(actor);
            return true;
        }
        catch (Exception error)
        {
            IResult result = ApiResults.FailService(error, context);
            await result.ExecuteAsync(context).ConfigureAwait(false);
            return false;
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(
        HttpContext context, long maxBytes, CancellationToken cancellationToken)
        where T : class
    {
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maxBytes)
        {
            return null;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                context.Request.Body, CanvasJson.ReadOptions, cancellationToken).ConfigureAwait(false);
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
