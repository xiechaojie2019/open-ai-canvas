#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
// Go 的 TaskStatus（表 tasks 的状态枚举）与 BCL 的 System.Threading.Tasks.TaskStatus 同名。
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 任务、任务日志与文本回放增量。
/// 对应 Go 的 <c>repository/repository.go</c>（任务部分）与 <c>repository/text_replay.go</c>。
/// </summary>
public sealed partial class Repository
{
    // ---------------------------------------------------------------- 任务查询

    /// <summary>按 ID 查任务（不限用户）。对应 Go: <c>Task</c>。</summary>
    public async Task<TaskEntity?> TaskAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<TaskEntity>(
            connection,
            SqlBuilder.Select<TaskEntity>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按用户与 ID 查任务。对应 Go: <c>TaskForUser</c>。</summary>
    public async Task<TaskEntity?> TaskForUserAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<TaskEntity>(
            connection,
            SqlBuilder.Select<TaskEntity>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 任务列表。对应 Go: <c>Tasks</c>。
    /// Go 显式列出列（不用 <c>SELECT *</c>），这里沿用同样的列集合，
    /// 避免把大字段（如 text_draft）带回列表接口。
    /// </summary>
    public async Task<IReadOnlyList<TaskEntity>> TasksAsync(
        string userId,
        int limit,
        string projectId,
        bool activeOnly,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > 100)
        {
            limit = 50;
        }

        List<string> conditions = ["user_id = @userId"];
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            conditions.Add("project_id = @projectId");
        }
        if (activeOnly)
        {
            conditions.Add("status IN @activeStatuses");
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<TaskEntity>(
            connection,
            SqlBuilder.Select<TaskEntity>(
                string.Join(" AND ", conditions),
                orderBy: "created_at DESC",
                limitOffset: Dialect.LimitOffset(limit, 0)),
            new
            {
                userId,
                projectId = projectId.Trim(),
                activeStatuses = new[] { TaskStatus.TaskStatusQueued, TaskStatus.TaskStatusRunning },
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>任务日志（按创建时间升序）。对应 Go: <c>TaskLogs</c>。</summary>
    public async Task<IReadOnlyList<TaskLog>> TaskLogsAsync(
        string userId, string taskId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<TaskLog>(
            connection,
            SqlBuilder.Select<TaskLog>("user_id = @userId AND task_id = @taskId", orderBy: "created_at ASC"),
            new { userId, taskId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 计费订单

    /// <summary>按 ID 查账单。对应 Go: <c>BillingOrder</c>。</summary>
    public async Task<BillingOrder?> BillingOrderAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<BillingOrder>(
            connection,
            SqlBuilder.Select<BillingOrder>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按任务 ID 批量取账单（键为任务 ID）。对应 Go: <c>BillingOrdersByTaskIDs</c>。</summary>
    public async Task<Dictionary<string, BillingOrder>> BillingOrdersByTaskIDsAsync(
        string userId, IReadOnlyList<string> taskIds, CancellationToken cancellationToken = default)
    {
        Dictionary<string, BillingOrder> result = new(StringComparer.Ordinal);
        if (taskIds.Count == 0)
        {
            return result;
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (BillingOrder order in await QueryAsync<BillingOrder>(
            connection,
            SqlBuilder.Select<BillingOrder>("user_id = @userId AND task_id IN @taskIds"),
            new { userId, taskIds },
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(order.TaskID))
            {
                result[order.TaskID] = order;
            }
        }
        return result;
    }

    /// <summary>
    /// 任务最近一次上游请求 ID。对应 Go: <c>LatestProviderRequestIDForTask</c>。
    /// 无匹配行时返回空串（Go 的 First 会返回 ErrRecordNotFound，调用方忽略该错误）。
    /// </summary>
    public async Task<string> LatestProviderRequestIDForTaskAsync(
        string taskId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        string? value = await ScalarAsync<string>(
            connection,
            """
            SELECT provider_request_id FROM api_call_logs
            WHERE task_id = @taskId AND provider_request_id <> ''
            ORDER BY created_at DESC
            LIMIT 1
            """,
            new { taskId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (value ?? "").Trim();
    }

    // ---------------------------------------------------------------- 文本回放增量

    /// <summary>
    /// 追加一条文本增量。对应 Go: <c>AppendTaskTextDelta</c>。
    /// 事务内先对任务行加锁并校验状态，再按任务/用户配额判定，最后写入。
    /// </summary>
    public async Task<TaskTextDelta> AppendTaskTextDeltaAsync(
        string userId,
        string taskId,
        string content,
        DateTime expiresAt,
        TextReplayLimits limits,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        TaskTextDelta created = new();
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        TaskEntity? task = await FirstOrDefaultAsync<TaskEntity>(
            connection,
            $"""
            SELECT id, user_id, status FROM tasks
            WHERE id = @taskId AND user_id = @userId
            {Dialect.ForUpdate()}
            """,
            new { taskId, userId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (task is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw AppError.NotFound("任务不存在");
        }

        if (task.Status is TaskStatus.TaskStatusSucceeded or TaskStatus.TaskStatusFailed or TaskStatus.TaskStatusCancelled)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new TextReplayException(TextReplayFailure.Closed);
        }

        TaskUsage usage = await QuerySingleOrDefaultAsync<TaskUsage>(
            connection,
            """
            SELECT COUNT(*) AS "Count", COALESCE(SUM(byte_count), 0) AS "Bytes", COALESCE(MAX(sequence), 0) AS "Max"
            FROM task_text_delta WHERE task_id = @taskId
            """,
            new { taskId },
            transaction,
            cancellationToken).ConfigureAwait(false) ?? new TaskUsage();

        long userBytes = await ScalarAsync<long>(
            connection,
            "SELECT COALESCE(SUM(byte_count), 0) FROM task_text_delta WHERE user_id = @userId",
            new { userId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        long incoming = System.Text.Encoding.UTF8.GetByteCount(content);
        if (usage.Count + 1 > limits.MaxTaskEvents
            || usage.Bytes + incoming > limits.MaxTaskBytes
            || userBytes + incoming > limits.MaxUserBytes)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new TextReplayException(TextReplayFailure.QuotaExceeded);
        }

        created = new TaskTextDelta
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            TaskID = taskId,
            Sequence = usage.Max + 1,
            Content = content,
            ByteCount = incoming,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
        };

        await ExecuteAsync(
            connection,
            SqlBuilder.Insert<TaskTextDelta>(),
            created,
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    /// <summary>按游标拉取文本增量。对应 Go: <c>TaskTextDeltas</c>。</summary>
    public async Task<IReadOnlyList<TaskTextDelta>> TaskTextDeltasAsync(
        string userId, string taskId, long after, int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > 1000)
        {
            limit = 1000;
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<TaskTextDelta>(
            connection,
            SqlBuilder.Select<TaskTextDelta>(
                "user_id = @userId AND task_id = @taskId AND sequence > @after",
                orderBy: "sequence ASC",
                limitOffset: Dialect.LimitOffset(limit, 0)),
            new { userId, taskId, after },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 归并文本增量：失败/取消时把增量拼进 <c>text_draft</c>，再统一续期。
    /// 对应 Go: <c>CompactTaskTextDeltas</c>。
    /// </summary>
    public async Task CompactTaskTextDeltasAsync(
        string taskId, DateTime expiresAt, bool keepDraft, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        TaskEntity? task = await FirstOrDefaultAsync<TaskEntity>(
            connection,
            $"SELECT id, status, text_draft FROM tasks WHERE id = @taskId{Dialect.ForUpdate()}",
            new { taskId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (task is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw AppError.NotFound("任务不存在");
        }

        if (keepDraft && string.IsNullOrEmpty(task.TextDraft))
        {
            IReadOnlyList<TaskTextDelta> items = await QueryAsync<TaskTextDelta>(
                connection,
                "SELECT content FROM task_text_delta WHERE task_id = @taskId ORDER BY sequence ASC",
                new { taskId },
                transaction,
                cancellationToken).ConfigureAwait(false);

            string draft = string.Concat(items.Select(item => item.Content));
            await ExecuteAsync(
                connection,
                "UPDATE tasks SET text_draft = @draft WHERE id = @taskId",
                new { draft, taskId },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            "UPDATE task_text_delta SET expires_at = @expiresAt WHERE task_id = @taskId",
            new { expiresAt, taskId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 清理过期文本增量。对应 Go: <c>CleanupTaskTextDeltas</c>。
    /// 过期且属于失败/取消任务、且草稿为空的，先把增量并入草稿再删除，避免丢正文。
    /// </summary>
    public async Task<long> CleanupTaskTextDeltasAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> taskIds = (await QueryAsync<string>(
            connection,
            "SELECT DISTINCT task_id FROM task_text_delta WHERE expires_at <= @now",
            new { now },
            transaction,
            cancellationToken).ConfigureAwait(false)).AsList();

        foreach (string taskId in taskIds)
        {
            TaskEntity? task = await FirstOrDefaultAsync<TaskEntity>(
                connection,
                "SELECT id, status, text_draft FROM tasks WHERE id = @taskId LIMIT 1",
                new { taskId },
                transaction,
                cancellationToken).ConfigureAwait(false);

            if (task is null)
            {
                continue;
            }

            // 已有草稿、或不是失败/取消态的任务：不合并，直接进入删除阶段。
            if (!string.IsNullOrEmpty(task.TextDraft)
                || (task.Status != TaskStatus.TaskStatusFailed && task.Status != TaskStatus.TaskStatusCancelled))
            {
                continue;
            }

            IReadOnlyList<TaskTextDelta> items = await QueryAsync<TaskTextDelta>(
                connection,
                "SELECT content FROM task_text_delta WHERE task_id = @taskId ORDER BY sequence ASC",
                new { taskId },
                transaction,
                cancellationToken).ConfigureAwait(false);

            string draft = string.Concat(items.Select(item => item.Content));
            await ExecuteAsync(
                connection,
                "UPDATE tasks SET text_draft = @draft WHERE id = @taskId",
                new { draft, taskId },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        int deleted = await ExecuteAsync(
            connection,
            """
            DELETE FROM task_text_delta
            WHERE expires_at <= @now
               OR NOT EXISTS (SELECT 1 FROM tasks WHERE tasks.id = task_text_delta.task_id)
            """,
            new { now },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// 收尾前端自管的文本回放任务：写入最终正文并置为 succeeded。
    /// 对应 Go: <c>CompleteTextReplayTask</c>。条件更新保证只有仍处于 text_replay 的任务能被收尾。
    /// </summary>
    public async Task<bool> CompleteTextReplayTaskAsync(
        string userId, string taskId, string resultJson, DateTime now, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await ExecuteAsync(
            connection,
            """
            UPDATE tasks SET
                status = @status, stage = @stage, progress = 100,
                result_json = @resultJson, text_draft = '',
                error = '', completed_at = @now,
                lease_owner = '', lease_expires_at = NULL, updated_at = @now
            WHERE id = @taskId AND user_id = @userId AND status = @textReplayStatus
            """,
            new
            {
                status = TaskStatus.TaskStatusSucceeded,
                stage = "已完成",
                resultJson,
                now,
                taskId,
                userId,
                textReplayStatus = TaskStatus.TaskStatusTextReplay,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>文本回放增量统计。对应 Go: <c>TextReplayStats</c>。</summary>
    public async Task<TextReplayStats> TextReplayStatsAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QuerySingleOrDefaultAsync<TextReplayStats>(
            connection,
            """
            SELECT
                COUNT(*) AS "EventCount",
                COUNT(DISTINCT task_id) AS "TaskCount",
                COALESCE(SUM(byte_count), 0) AS "ByteCount",
                MIN(created_at) AS "OldestAt"
            FROM task_text_delta
            """,
            cancellationToken: cancellationToken).ConfigureAwait(false) ?? new TextReplayStats();
    }

    private sealed class TaskUsage
    {
        public long Count { get; set; }
        public long Bytes { get; set; }
        public long Max { get; set; }
    }
}

/// <summary>文本回放配额。对应 Go: <c>repository.TextReplayLimits</c>。</summary>
public sealed class TextReplayLimits
{
    public long MaxTaskBytes { get; init; }
    public long MaxUserBytes { get; init; }
    public long MaxTaskEvents { get; init; }
}

/// <summary>文本回放增量统计。对应 Go: <c>repository.TextReplayStats</c>。</summary>
public sealed class TextReplayStats
{
    public long EventCount { get; set; }
    public long TaskCount { get; set; }
    public long ByteCount { get; set; }
    public DateTime? OldestAt { get; set; }
}

/// <summary>文本回放失败的两种原因。对应 Go 的两个哨兵错误。</summary>
public enum TextReplayFailure
{
    /// <summary>对应 Go: <c>ErrTextReplayQuotaExceeded</c>。</summary>
    QuotaExceeded,

    /// <summary>对应 Go: <c>ErrTextReplayClosed</c>。</summary>
    Closed,
}

/// <summary>
/// 文本回放的哨兵异常。对应 Go 的 <c>ErrTextReplayQuotaExceeded</c> / <c>ErrTextReplayClosed</c>；
/// 仓储层抛出，服务层翻译成对用户可见的 400 提示。
/// </summary>
public sealed class TextReplayException : Exception
{
    public TextReplayException(TextReplayFailure failure)
        : base(failure == TextReplayFailure.QuotaExceeded
            ? "text replay quota exceeded"
            : "text replay task is closed")
    {
        Failure = failure;
    }

    public TextReplayFailure Failure { get; }
}
