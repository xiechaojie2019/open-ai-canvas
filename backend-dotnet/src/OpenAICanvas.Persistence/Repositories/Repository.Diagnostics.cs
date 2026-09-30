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
            WHERE "userId" = @userId
              AND (CASE WHEN @taskId <> '' THEN id = @taskId ELSE ("createdAt" >= @from AND "createdAt" <= @to
                AND (@projectId = '' OR "projectId" = @projectId)) END)
            ORDER BY "createdAt" ASC
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
            SELECT * FROM "taskLogs"
            WHERE "userId" = @userId
              AND (CASE WHEN @taskId <> '' THEN "taskId" = @taskId ELSE ("createdAt" >= @from AND "createdAt" <= @to
                AND (@projectId = '' OR "taskId" IN (SELECT id FROM tasks WHERE "userId" = @userId AND "projectId" = @projectId))) END)
            ORDER BY "createdAt" ASC
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
            SELECT id, "userId", "traceId", "requestId", "channelId", "taskId", "billingOrderId",
              source, capability, operation, "requestKind", billable, "apiFormat", method, path, status,
              "statusCode", error, "errorCode", model, "usageAvailable", "inputTokens", "outputTokens",
              "cachedTokens", "mediaCount", "videoSeconds", "estimatedCostMicros", "costAvailable", currency,
              "providerRequestId", "providerStatus", "pollCount", "concurrencyLimit", "upstreamUrl",
              "requestContentType", "startedAt", "durationMs", "createdAt"
            FROM "apiCallLogs"
            WHERE "userId" = @userId
              AND (CASE WHEN @taskId <> '' THEN "taskId" = @taskId ELSE ("createdAt" >= @from AND "createdAt" <= @to
                AND (@projectId = '' OR "taskId" IN (SELECT id FROM tasks WHERE "userId" = @userId AND "projectId" = @projectId))) END)
            ORDER BY "createdAt" ASC
            LIMIT 1000
            """,
            new { userId, from, to, taskId = taskId.Trim(), projectId = projectId.Trim() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
