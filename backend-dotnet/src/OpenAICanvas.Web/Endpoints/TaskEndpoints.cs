#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Web.Contracts;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 任务读取与文本回放路由。对应 Go: <c>handler/routes.go: RegisterTaskRoutes</c>
/// 中不依赖 provider 执行引擎的部分。
/// </summary>
/// <remarks>
/// 未实现（需要 provider 执行引擎，属阶段 4 后续）：<c>POST /tasks</c>、
/// <c>POST /tasks/:id/retry</c>、<c>POST /tasks/:id/cancel</c>、
/// <c>POST /tasks/:id/query-provider</c>、<c>GET /tasks/:id/text-events</c>（SSE）。
/// </remarks>
public static class TaskEndpoints
{
    /// <summary>请求体上限 16MB。对应 Go: <c>16&lt;&lt;20</c>。</summary>
    private const long BodyLimit = 16L << 20;

    /// <summary>文本增量请求体上限 1MB（Go 未显式限制，这里给一个防御性上限）。</summary>
    private const long DeltaBodyLimit = 1L << 20;

    public static void MapTaskRoutes(this IEndpointRouteBuilder api, CanvasService service)
    {
        // ------------------------------------------------------------ 任务列表

        api.MapGet("/tasks", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            if (!TryParsePositiveQueryInt(context.Request.Query["pageSize"], 50, out int pageSize, out string? error))
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, new InvalidOperationException(error));
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                List<TaskSummaryDto> tasks = await service.Tasks.TasksWithOptionsAsync(
                    actor.ID,
                    new TaskListOptions
                    {
                        Limit = pageSize,
                        ProjectID = context.Request.Query["projectId"].ToString(),
                        ActiveOnly = context.Request.Query["activeOnly"].ToString() == "true",
                    },
                    cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(tasks);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        // ------------------------------------------------------------ 任务详情

        api.MapGet("/tasks/{id}", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                TaskEntity task = await service.Tasks
                    .TaskAsync(actor.ID, id, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(task);
            }
            catch (Exception ex)
            {
                // Go 对任务不存在显式返回 404（不是 failService 的默认状态）。
                return ApiResults.FailService(ex, context);
            }
        });

        // ------------------------------------------------------------ 任务日志

        api.MapGet("/tasks/{id}/logs", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                IReadOnlyList<TaskLog> logs = await service.Tasks
                    .TaskLogsAsync(actor.ID, id, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(logs);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        // ------------------------------------------------------------ 文本增量

        api.MapPost("/tasks/{id}/text-deltas", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            TextContentRequest? request = await ReadJsonAsync<TextContentRequest>(
                context, DeltaBodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                TaskTextDelta delta = await service.Tasks
                    .AppendTaskTextDeltaAsync(actor.ID, id, request.Content, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(delta);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        api.MapGet("/tasks/{id}/text-deltas", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            if (!TryReadTextEventCursor(context, out long after, out string? cursorError))
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, new InvalidOperationException(cursorError));
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                TextReplayResultDto result = await service.Tasks
                    .TaskTextReplayAsync(actor.ID, id, after, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(result);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        // ------------------------------------------------------------ 文本回放收尾

        api.MapPost("/tasks/{id}/text-replay-complete", async (HttpContext context, string id, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            TextReplayCompleteRequest? request = await ReadJsonAsync<TextReplayCompleteRequest>(
                context, BodyLimit, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                TaskEntity task = await service.Tasks
                    .CompleteTextReplayTaskAsync(actor.ID, id, request.Text, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(task);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });

        // ------------------------------------------------------------ 回放统计（管理员）

        api.MapGet("/admin/text-replay-stats", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await TryRequireUserAsync(context, service, cancellationToken).ConfigureAwait(false))
            {
                return Results.Empty;
            }

            try
            {
                User actor = await service.CurrentUserAsync(
                    SessionCookie.Read(context), cancellationToken).ConfigureAwait(false);

                OpenAICanvas.Persistence.Repositories.TextReplayStats stats = await service.Tasks
                    .AdminTextReplayStatsAsync(actor, cancellationToken).ConfigureAwait(false);

                return ApiResults.Ok(stats);
            }
            catch (Exception ex)
            {
                return ApiResults.FailService(ex, context);
            }
        });
    }

    // ------------------------------------------------------------ 辅助

    /// <summary>要求已登录；未登录时写出 401 并返回 false。</summary>
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

    /// <summary>
    /// 解析正整数查询参数。对应 Go: <c>parsePositiveQueryInt</c>。
    /// 空值取默认；非正整数视为请求错误，不静默回落默认值。
    /// </summary>
    private static bool TryParsePositiveQueryInt(
        string? raw, int fallback, out int value, out string? error)
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
            error = "query parameter must be a positive integer";
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// 读取文本事件游标：取 <c>after</c> 与 <c>Last-Event-ID</c> 的较大值，两者都必须是十进制非负整数。
    /// 对应 Go: <c>taskTextEventCursor</c>。
    /// </summary>
    private static bool TryReadTextEventCursor(HttpContext context, out long cursor, out string? error)
    {
        cursor = 0;
        error = null;

        foreach ((string name, string raw) in new[]
        {
            ("after", context.Request.Query["after"].ToString()),
            ("Last-Event-ID", context.Request.Headers["Last-Event-ID"].ToString()),
        })
        {
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }

            if (!long.TryParse(raw, out long value) || value < 0)
            {
                error = "after 或 Last-Event-ID 必须是非负整数";
                return false;
            }

            if (value > cursor)
            {
                cursor = value;
            }
        }

        return true;
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

    /// <summary>文本增量请求体。对应 Go 的内联匿名结构 <c>struct{ Content string }</c>。</summary>
    private sealed class TextContentRequest
    {
        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }

    /// <summary>文本回放收尾请求体。对应 Go 的内联匿名结构 <c>struct{ Text string }</c>。</summary>
    private sealed class TextReplayCompleteRequest
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";
    }
}
