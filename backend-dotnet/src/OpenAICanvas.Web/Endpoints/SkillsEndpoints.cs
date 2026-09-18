#nullable enable
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 技能库读取与状态路由。对应 Go: <c>handler/skills.go</c>。
/// </summary>
/// <remarks>
/// 本轮覆盖 8 条：列表 / 已添加 / 详情 / 删除 / 加入 / 移出 / 收藏 / 取消收藏。
/// 创建与更新（<c>POST /skills</c>、<c>PUT /skills/{id}</c>）依赖技能包文件写入，
/// 安装 / 同步 / 文件读取同样另行实现。
/// </remarks>
public static class SkillsEndpoints
{
    public static void MapSkillsRoutes(this IEndpointRouteBuilder api, CanvasService service)
    {
        // ------------------------------------------------------------ 列表与详情

        api.MapGet("/skills", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            if (!TryParsePagination(context, fallbackPageSize: 20, out int page, out int pageSize, out string? error))
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, new InvalidOperationException(error));
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                // scope 默认 public，sort 默认 popular。
                string scope = context.Request.Query["scope"].ToString();
                if (scope.Length == 0)
                {
                    scope = "public";
                }

                string sort = context.Request.Query["sort"].ToString();
                if (sort.Length == 0)
                {
                    sort = "popular";
                }

                SkillListDto result = await service.Skills.SkillsAsync(
                    actor.ID, page, pageSize, scope,
                    context.Request.Query["search"].ToString(),
                    context.Request.Query["tag"].ToString(),
                    sort,
                    cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        // 注意：字面量段必须在 {id} 之前声明（ASP.NET 也按字面量优先匹配，顺序仅为可读性）。
        api.MapGet("/skills/added", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                List<SkillItemDto> skills = await service.Skills
                    .AddedSkillsAsync(actor.ID, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { skills });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/skills/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                SkillItemDto skill = await service.Skills
                    .SkillDetailAsync(actor.ID, id, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { skill });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        // ------------------------------------------------------------ 删除

        api.MapDelete("/skills/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                await service.Skills.DeleteSkillAsync(actor.ID, id, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(new { deleted = true });
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        // ------------------------------------------------------------ 加入与收藏

        api.MapPost("/skills/{id}/add", (HttpContext context, string id, CancellationToken cancellationToken) =>
            SetSkillAddedAsync(context, service, id, added: true, cancellationToken));

        api.MapDelete("/skills/{id}/add", (HttpContext context, string id, CancellationToken cancellationToken) =>
            SetSkillAddedAsync(context, service, id, added: false, cancellationToken));

        api.MapPost("/skills/{id}/like", (HttpContext context, string id, CancellationToken cancellationToken) =>
            SetSkillLikedAsync(context, service, id, liked: true, cancellationToken));

        api.MapDelete("/skills/{id}/like", (HttpContext context, string id, CancellationToken cancellationToken) =>
            SetSkillLikedAsync(context, service, id, liked: false, cancellationToken));
    }

    // ------------------------------------------------------------ 辅助

    private static async Task<IResult> SetSkillAddedAsync(
        HttpContext context, CanvasService service, string id, bool added, CancellationToken cancellationToken)
    {
        if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        try
        {
            User actor = await service.CurrentUserAsync(
                SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

            SkillItemDto skill = await service.Skills
                .SetSkillAddedAsync(actor.ID, id, added, cancellationToken).ConfigureAwait(false);

            return ApiResults.Ok(new { skill });
        }
        catch (Exception ex)
        {
            return ApiResults.FailService(ex, context);
        }
    }

    private static async Task<IResult> SetSkillLikedAsync(
        HttpContext context, CanvasService service, string id, bool liked, CancellationToken cancellationToken)
    {
        if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        try
        {
            User actor = await service.CurrentUserAsync(
                SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

            SkillItemDto skill = await service.Skills
                .SetSkillLikedAsync(actor.ID, id, liked, cancellationToken).ConfigureAwait(false);

            return ApiResults.Ok(new { skill });
        }
        catch (Exception ex)
        {
            return ApiResults.FailService(ex, context);
        }
    }

    private static async Task<bool> TryRequireUserAsync(
        HttpContext context, CanvasService service, CancellationToken cancellationToken)
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

    private static bool TryParsePagination(
        HttpContext context, int fallbackPageSize, out int page, out int pageSize, out string? error)
    {
        page = 1;
        pageSize = fallbackPageSize;
        error = null;

        if (!TryParsePositive(context.Request.Query["page"], 1, "page", out page, out error))
        {
            return false;
        }

        return TryParsePositive(context.Request.Query["pageSize"], fallbackPageSize, "pageSize", out pageSize, out error);
    }

    private static bool TryParsePositive(
        string? raw, int fallback, string name, out int value, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(raw))
        {
            value = fallback;
            return true;
        }

        if (!int.TryParse(raw, out int parsed) || parsed < 1)
        {
            value = 0;
            error = $"{name}: query parameter must be a positive integer";
            return false;
        }

        value = parsed;
        return true;
    }
}
