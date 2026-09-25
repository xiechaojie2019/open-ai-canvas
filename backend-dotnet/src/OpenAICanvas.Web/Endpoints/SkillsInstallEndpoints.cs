#nullable enable

using System.Text.Json;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>技能包安装与 GitHub 同步路由。</summary>
public static partial class SkillsEndpoints
{
    /// <summary>
    /// 映射 POST /skills/install、POST /skills/install/github、POST /skills/{id}/sync。
    /// 可独立于现有技能读取路由接线，避免改变主组合根。
    /// </summary>
    public static void MapSkillInstallRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service)
    {
        api.MapPost("/skills/install", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                const long maxBytes = SkillsService.SkillPackageUploadMaxBytes;
                if (context.Request.ContentLength is > maxBytes)
                {
                    throw OpenAICanvas.Domain.Kernel.AppError.BadAuthRequest("技能文件超过 21MB 请求上限");
                }
                if (!context.Request.HasFormContentType)
                {
                    throw OpenAICanvas.Domain.Kernel.AppError.BadAuthRequest("请选择 Markdown 或 ZIP 技能文件");
                }

                IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
                IFormFile? file = form.Files.GetFile("file");
                if (file is null || file.Length <= 0)
                {
                    throw OpenAICanvas.Domain.Kernel.AppError.BadAuthRequest("请选择 Markdown 或 ZIP 技能文件");
                }
                if (file.Length > maxBytes)
                {
                    throw OpenAICanvas.Domain.Kernel.AppError.BadAuthRequest("技能文件超过 21MB 请求上限");
                }

                string sourceType = form["sourceType"].ToString().Trim().ToLowerInvariant();
                if (sourceType.Length == 0)
                {
                    sourceType = Path.GetExtension(file.FileName).ToLowerInvariant() switch
                    {
                        ".md" or ".markdown" => "markdown",
                        ".zip" => "zip",
                        _ => "",
                    };
                }
                if (!TryParseOptionalBool(form["isPrivate"].ToString(), out bool isPrivate))
                {
                    throw OpenAICanvas.Domain.Kernel.AppError.BadAuthRequest("isPrivate 必须是布尔值");
                }

                using MemoryStream buffer = new();
                await file.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                SkillItemDto skill = await service.Skills.InstallUploadAsync(
                    user.ID,
                    sourceType,
                    buffer.ToArray(),
                    new SkillInstallRequestDto
                    {
                        Name = form["name"].ToString(),
                        Description = form["description"].ToString(),
                        Tag = form["tag"].ToString(),
                        IsPrivate = isPrivate,
                    },
                    cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { skill });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/skills/install/github", async (
            HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                SkillGitHubInstallRequestDto? request = await ReadJsonAsync<SkillGitHubInstallRequestDto>(
                    context, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    throw OpenAICanvas.Domain.Kernel.AppError.BadAuthRequest("GitHub 技能数据格式无效");
                }
                SkillItemDto skill = await service.Skills.InstallGitHubAsync(
                    user.ID, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { skill });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/skills/{id}/sync", async (
            HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);
                SkillItemDto skill = await service.Skills.SyncGitHubAsync(
                    user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new { skill });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });
    }

    private static bool TryParseOptionalBool(string value, out bool result)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            result = false;
            return true;
        }
        if (value is "1" or "true" or "True" or "TRUE")
        {
            result = true;
            return true;
        }
        if (value is "0" or "false" or "False" or "FALSE")
        {
            result = false;
            return true;
        }
        result = false;
        return false;
    }

}
