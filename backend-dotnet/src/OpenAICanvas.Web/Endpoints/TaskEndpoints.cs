#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Web.Contracts;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Middleware;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;
using OpenAICanvas.Platform;
using System.Diagnostics;
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
        MapTaskCoreRoutes(api, service, null, null);
    }

    /// <summary>
    /// 任务创建 / SSE / 时间线转写。对应 Go: <c>handler/routes.go</c> 的
    /// POST /tasks、GET /tasks/:id/text-events、POST /timeline/transcriptions。
    /// </summary>
    public static void MapTaskCreationRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        IRateLimiter limiter,
        IRuntimePolicyProvider policyProvider)
    {
        MapTaskCoreRoutes(api, service, limiter, policyProvider);
    }

    private static void MapTaskCoreRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        IRateLimiter? limiter,
        IRuntimePolicyProvider? policyProvider)
    {
        // 写路径仅在带限流的注册入口挂载，避免与只读入口重复注册。
        if (limiter is null || policyProvider is null)
        {
            return;
        }

        // ------------------------------------------------------------ 任务创建（限流 + 16MB）

        api.MapPost("/tasks", async (HttpContext context, CancellationToken cancellationToken) =>
            await CreateTaskHandler(context, service, limiter, policyProvider, cancellationToken));

        // ------------------------------------------------------------ 文本事件 SSE

        api.MapGet("/tasks/{id}/text-events", (HttpContext context, string id, CancellationToken cancellationToken) =>
            TextEventsHandlerAsync(context, service, id, cancellationToken));

        // ------------------------------------------------------------ 时间线转写

        api.MapPost("/timeline/transcriptions", async (HttpContext context, CancellationToken cancellationToken) =>
            await TimelineTranscriptionHandler(context, service, limiter, policyProvider, cancellationToken));

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

    // ------------------------------------------------------------ 任务创建与 SSE

    private static async Task<IResult> CreateTaskHandler(
        HttpContext context,
        CanvasService service,
        IRateLimiter? limiter,
        IRuntimePolicyProvider? policyProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                .ConfigureAwait(false);
            if (limiter is not null && policyProvider is not null)
            {
                RuntimePolicySetting policy = policyProvider.Current();
                if (!await AuthEndpoints.EnforceRateLimitAsync(
                        context, limiter, "tasks:" + user.ID,
                        policy.Request.TaskCreatePerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }
            }
            CreateTaskRequestDto? request = await ReadJsonAsync<CreateTaskRequestDto>(
                context, 16 << 20, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }
            TaskEntity task;
            try
            {
                task = await service.TaskCreations.CreateAsync(
                    user.ID,
                    request,
                    RequestCorrelationMiddleware.TraceId(context),
                    RequestCorrelationMiddleware.RequestId(context),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Go 的 handler 对 CreateTask 的所有错误统一 fail(c, 400, err)：
                // HTTP 恒为 400，msg 取错误文案（含维护模式提示）。
                return ApiResults.Fail(StatusCodes.Status400BadRequest, error);
            }
            return ApiResults.Ok(task);
        }
        catch (Exception error)
        {
            return ApiResults.FailService(error, context);
        }
    }

    private static async Task TextEventsHandlerAsync(
        HttpContext context,
        CanvasService service,
        string taskId,
        CancellationToken cancellationToken)
    {
        try
        {
            User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                .ConfigureAwait(false);
            long after = ParseTextEventCursor(context);
            if (after < 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(
                    "{\"code\":400,\"data\":null,\"msg\":\"after 或 Last-Event-ID 必须是非负整数\",\"reason\":\"bad_request\"}",
                    System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                return;
            }
            TextReplayResultDto initial = await service.Tasks.TaskTextReplayAsync(
                user.ID, taskId, after, cancellationToken).ConfigureAwait(false);

            context.Response.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-cache, no-transform";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Response.StatusCode = StatusCodes.Status200OK;

            await using System.IO.StreamWriter writer = new(
                context.Response.Body, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true);
            async Task WriteEventAsync(string eventName, long id, object value)
            {
                string data = JsonSerializer.Serialize(value, CanvasJson.WriteOptions);
                if (id > 0)
                {
                    await writer.WriteAsync($"id: {id}\n").ConfigureAwait(false);
                }
                await writer.WriteAsync($"event: {eventName}\n").ConfigureAwait(false);
                await writer.WriteAsync("data: " + data + "\n\n").ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }

            await writer.WriteAsync(": connected\n\n").ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);

            TextReplayResultDto replay = initial;
            string lastStatus = replay.Status;
            string lastStage = replay.Stage;
            long lastProgress = replay.Progress;
            await WriteEventAsync("progress", 0, new
            {
                status = replay.Status,
                stage = replay.Stage,
                progress = replay.Progress,
            }).ConfigureAwait(false);

            long pollAt = 750;
            long heartbeatAt = 15_000;
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                foreach (TaskTextDelta delta in replay.Deltas)
                {
                    await WriteEventAsync("delta", delta.Sequence, new
                    {
                        sequence = delta.Sequence,
                        content = delta.Content,
                    }).ConfigureAwait(false);
                    after = delta.Sequence;
                }
                if (replay.Complete)
                {
                    await WriteEventAsync("terminal", 0, replay).ConfigureAwait(false);
                    return;
                }
                if (context.RequestAborted.IsCancellationRequested)
                {
                    return;
                }
                long elapsed = stopwatch.ElapsedMilliseconds;
                if (elapsed >= heartbeatAt)
                {
                    await writer.WriteAsync(": heartbeat\n\n").ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                    heartbeatAt = elapsed + 15_000;
                }
                if (elapsed >= pollAt)
                {
                    TextReplayResultDto? next;
                    try
                    {
                        next = await service.Tasks.TaskTextReplayAsync(
                            user.ID, taskId, after, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        await WriteEventAsync("error", 0, new { message = "任务文本流不可用" })
                            .ConfigureAwait(false);
                        return;
                    }
                    if (next.Status != lastStatus || next.Stage != lastStage || next.Progress != lastProgress)
                    {
                        await WriteEventAsync("progress", 0, new
                        {
                            status = next.Status,
                            stage = next.Stage,
                            progress = next.Progress,
                        }).ConfigureAwait(false);
                        lastStatus = next.Status;
                        lastStage = next.Stage;
                        lastProgress = next.Progress;
                    }
                    replay = next;
                    pollAt = elapsed + 750;
                }
                long sleep = Math.Min(Math.Max(heartbeatAt - elapsed, 1), Math.Max(pollAt - elapsed, 1));
                try
                {
                    await Task.Delay((int)Math.Min(sleep, 250), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        catch (Exception error)
        {
            if (!context.Response.HasStarted)
            {
                IResult failure = ApiResults.FailService(error, context);
                await failure.ExecuteAsync(context).ConfigureAwait(false);
            }
        }
    }

    /// <summary>解析 after / Last-Event-ID 游标；非法返回 -1。对应 Go: <c>taskTextEventCursor</c>。</summary>
    private static long ParseTextEventCursor(HttpContext context)
    {
        long cursor = 0;
        foreach (string raw in new[]
                 {
                     context.Request.Query["after"].ToString(),
                     context.Request.Headers["Last-Event-ID"].ToString(),
                 })
        {
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }
            if (!long.TryParse(raw, out long value) || value < 0)
            {
                return -1;
            }
            if (value > cursor)
            {
                cursor = value;
            }
        }
        return cursor;
    }

    private static async Task<IResult> TimelineTranscriptionHandler(
        HttpContext context,
        CanvasService service,
        IRateLimiter? limiter,
        IRuntimePolicyProvider? policyProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                .ConfigureAwait(false);
            if (limiter is not null && policyProvider is not null)
            {
                RuntimePolicySetting policy = policyProvider.Current();
                if (!await AuthEndpoints.EnforceRateLimitAsync(
                        context, limiter, "timeline-ts:" + user.ID,
                        policy.Request.TaskCreatePerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
                {
                    return Results.Empty;
                }
            }
            TimelineTranscriptionRequestDto? request = await ReadJsonAsync<TimelineTranscriptionRequestDto>(
                context, 1 << 20, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(StatusCodes.Status400BadRequest, null);
            }
            TaskEntity task = await service.TaskCreations.CreateTimelineTranscriptionAsync(
                user.ID, request, cancellationToken).ConfigureAwait(false);
            return ApiResults.Ok(task);
        }
        catch (Exception error)
        {
            return ApiResults.FailService(error, context);
        }
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
