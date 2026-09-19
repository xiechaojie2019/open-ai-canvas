#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>诊断数据窗口查询。对应 Go: <c>repository/diagnostics.go</c>。</summary>
public sealed partial class Repository
{
    /// <summary>窗口内任务（精简列，上限 100）。对应 Go: <c>DiagnosticTasks</c>。</summary>
    public async Task<IReadOnlyList<TaskEntity>> DiagnosticTasksAsync(
        string userId, DateTime from, DateTime to, string taskId, string projectId,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<TaskEntity>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<TaskEntity>("tasks")}
            FROM tasks
            WHERE user_id = @userId
              AND (CASE WHEN @taskId <> '' THEN id = @taskId ELSE (created_at >= @from AND created_at <= @to
                AND (@projectId = '' OR project_id = @projectId)) END)
            ORDER BY created_at ASC
            LIMIT 100
            """,
            new { userId, from, to, taskId = taskId.Trim(), projectId = projectId.Trim() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>窗口内任务日志（上限 2000）。对应 Go: <c>DiagnosticTaskLogs</c>。</summary>
    public async Task<IReadOnlyList<TaskLog>> DiagnosticTaskLogsAsync(
        string userId, DateTime from, DateTime to, string taskId, string projectId,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<TaskLog>(
            connection,
            """
            SELECT * FROM task_logs
            WHERE user_id = @userId
              AND (CASE WHEN @taskId <> '' THEN task_id = @taskId ELSE (created_at >= @from AND created_at <= @to
                AND (@projectId = '' OR task_id IN (SELECT id FROM tasks WHERE user_id = @userId AND project_id = @projectId))) END)
            ORDER BY created_at ASC
            LIMIT 2000
            """,
            new { userId, from, to, taskId = taskId.Trim(), projectId = projectId.Trim() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>窗口内上游调用（不含请求/响应报文，上限 1000）。对应 Go: <c>DiagnosticAPICallLogs</c>。</summary>
    public async Task<IReadOnlyList<ApiCallLog>> DiagnosticAPICallLogsAsync(
        string userId, DateTime from, DateTime to, string taskId, string projectId,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ApiCallLog>(
            connection,
            """
            SELECT id, user_id, trace_id, request_id, channel_id, task_id, billing_order_id,
              source, capability, operation, request_kind, billable, api_format, method, path, status,
              status_code, error, error_code, model, usage_available, input_tokens, output_tokens,
              cached_tokens, media_count, video_seconds, estimated_cost_micros, cost_available, currency,
              provider_request_id, provider_status, poll_count, concurrency_limit, upstream_url,
              request_content_type, started_at, duration_ms, created_at
            FROM api_call_logs
            WHERE user_id = @userId
              AND (CASE WHEN @taskId <> '' THEN task_id = @taskId ELSE (created_at >= @from AND created_at <= @to
                AND (@projectId = '' OR task_id IN (SELECT id FROM tasks WHERE user_id = @userId AND project_id = @projectId))) END)
            ORDER BY created_at ASC
            LIMIT 1000
            """,
            new { userId, from, to, taskId = taskId.Trim(), projectId = projectId.Trim() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
