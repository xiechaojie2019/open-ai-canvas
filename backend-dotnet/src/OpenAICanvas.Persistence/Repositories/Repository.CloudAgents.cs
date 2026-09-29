#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// Agent 运行时与画布变更仓储方法。
/// 对应 Go: <c>repository/cloud_agent.go</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>插入执行记录；主键冲突忽略。对应 Go: <c>EnsureCloudAgent</c>。</summary>
    public async Task EnsureCloudAgentAsync(
        CloudAgentExecution run, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(CloudAgentExecution)) + Dialect.OnConflictDoNothing("id"),
            run,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>按用户 + ID 读执行记录；不存在返回 null。对应 Go: <c>CloudAgent</c>。</summary>
    public async Task<CloudAgentExecution?> CloudAgentAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        CloudAgentExecution? run = await FirstOrDefaultAsync<CloudAgentExecution>(
            connection,
            SqlBuilder.Select<CloudAgentExecution>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (run is not null)
        {
            await LoadCloudAgentJournalCoreAsync(connection, null, run, cancellationToken).ConfigureAwait(false);
        }
        return run;
    }

    /// <summary>按活动模型任务查运行。对应 Go: <c>CloudAgentForActiveTask</c>。</summary>
    public async Task<CloudAgentExecution?> CloudAgentForActiveTaskAsync(
        string userId, string taskId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        CloudAgentExecution? run = await FirstOrDefaultAsync<CloudAgentExecution>(
            connection,
            SqlBuilder.Select<CloudAgentExecution>(
                "user_id = @userId AND active_task_id = @taskId AND status IN ('running', 'queued')",
                limitOffset: " LIMIT 1"),
            new { userId, taskId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (run is not null)
        {
            await LoadCloudAgentJournalCoreAsync(connection, null, run, cancellationToken).ConfigureAwait(false);
        }
        return run;
    }

    /// <summary>缺执行行的历史根任务（恢复用）。对应 Go: <c>CloudAgentRoots</c>。</summary>
    public async Task<IReadOnlyList<TaskEntity>> CloudAgentRootsAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<TaskEntity>(
            connection,
            SqlBuilder.Select<TaskEntity>(
                "operation = 'cloud_agent' AND id NOT IN (SELECT id FROM cloud_agent_executions)",
                limitOffset: " ORDER BY created_at LIMIT 50"),
            new { },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>稳定 keyset 分页的待推进运行。对应 Go: <c>ActiveCloudAgentsAfter</c>。</summary>
    public async Task<IReadOnlyList<CloudAgentExecution>> ActiveCloudAgentsAfterAsync(
        string after, int limit, CancellationToken cancellationToken = default)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 50;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<CloudAgentExecution> runs = (await QueryAsync<CloudAgentExecution>(
            connection,
            SqlBuilder.Select<CloudAgentExecution>(
                "(status IN ('running', 'queued') OR cleanup_pending = @cleanup) AND id > @after",
                limitOffset: " ORDER BY id LIMIT @limit"),
            new { cleanup = true, after, limit },
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        foreach (CloudAgentExecution run in runs)
        {
            await LoadCloudAgentJournalCoreAsync(connection, null, run, cancellationToken).ConfigureAwait(false);
        }
        return runs;
    }

    /// <summary>
    /// 空闲 SSE 订阅使用的轻量修订号读取；不存在返回 null。
    /// 对应 Go: <c>CloudAgentRevision</c>。
    /// </summary>
    public async Task<long?> CloudAgentRevisionAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT \"revision\" FROM \"cloud_agent_executions\" WHERE \"id\" = @id AND \"user_id\" = @userId",
            new { id, userId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// 修订号 CAS 互斥推进：先把 revision 加一（不匹配则返回 null），再在事务内回调。
    /// 回调里的读写必须经由 <see cref="CloudAgentMutationContext"/> 落在同一事务上。
    /// 对应 Go: <c>MutateCloudAgent</c>。
    /// </summary>
    public async Task<TResult?> MutateCloudAgentAsync<TResult>(
        string userId,
        string id,
        long revision,
        Func<CloudAgentExecution, CloudAgentMutationContext, Task<TResult>> mutate,
        CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            int claimed = await ExecuteAsync(
                connection,
                "UPDATE \"cloud_agent_executions\" SET \"revision\" = \"revision\" + 1 WHERE \"id\" = @id AND \"user_id\" = @userId AND \"revision\" = @revision",
                new { id, userId, revision },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (claimed != 1)
            {
                return default;
            }
            CloudAgentExecution? run = await FirstOrDefaultAsync<CloudAgentExecution>(
                connection,
                SqlBuilder.Select<CloudAgentExecution>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
                new { id, userId },
                transaction,
                cancellationToken).ConfigureAwait(false)
                ?? throw AppError.NotFound("Agent 运行不存在");
            CloudAgentMutationContext context = new(connection, transaction, this);
            return await mutate(run, context).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>无返回值回调版本：true=获得修订锁并完成；false=修订冲突。</summary>
    public async Task<bool> MutateCloudAgentAsync(
        string userId,
        string id,
        long revision,
        Func<CloudAgentExecution, CloudAgentMutationContext, Task> mutate,
        CancellationToken cancellationToken = default)
    {
        return await MutateCloudAgentAsync<object?>(
            userId, id, revision,
            async (run, context) =>
            {
                await mutate(run, context).ConfigureAwait(false);
                return null;
            },
            cancellationToken).ConfigureAwait(false) is not null;
    }

    internal async Task<CloudAgentExecution?> CloudAgentInTxAsync(
        DbConnection connection, DbTransaction transaction, string userId, string id,
        CancellationToken cancellationToken)
    {
        CloudAgentExecution? run = await FirstOrDefaultAsync<CloudAgentExecution>(
            connection,
            SqlBuilder.Select<CloudAgentExecution>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id, userId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (run is not null)
        {
            await LoadCloudAgentJournalCoreAsync(connection, transaction, run, cancellationToken).ConfigureAwait(false);
        }
        return run;
    }

    /// <summary>
    /// 打开非事务变更上下文：干跑规划在检查点事务外读取画布/资源（与 Go 的
    /// prepare 在 MutateCloudAgent 之前执行一致）。SQLite 下同样不嵌套连接。
    /// </summary>
    public async Task<CloudAgentMutationContext> OpenCloudAgentContextAsync(
        CancellationToken cancellationToken = default)
    {
        DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return new CloudAgentMutationContext(connection, transaction: null, this, ownsConnection: true);
    }

    /// <summary>事务内最近一次画布变更；不存在返回 null。</summary>
    public Task<CloudAgentCanvasMutation?> LatestCloudAgentCanvasMutationInTxAsync(
        DbConnection connection, DbTransaction transaction, string userId, string runId,
        CancellationToken cancellationToken)
        => LatestCloudAgentCanvasMutationByIdAsync(connection, transaction, userId, runId, cancellationToken);

    /// <summary>事务内保存执行记录（GORM Save 语义）。对应 Go: <c>tx.Save(run)</c>。</summary>
    public async Task SaveCloudAgentInTxAsync(
        DbConnection connection, DbTransaction transaction, CloudAgentExecution run,
        CancellationToken cancellationToken)
    {
        // 追加校验基线：EventCount 不允许回退；已有事件不允许改写（append-only）。
        long previousEvents = 0;
        Dictionary<long, string> previousEventBodies = new();
        Dictionary<string, string> previousMessages = new(StringComparer.Ordinal);
        if (run.CheckpointVersion >= 2)
        {
            CloudAgentExecution? baseline = await FirstOrDefaultAsync<CloudAgentExecution>(
                connection,
                "SELECT \"event_count\" AS \"EventCount\" FROM \"cloud_agent_executions\" WHERE \"id\" = @id AND \"user_id\" = @userId LIMIT 1",
                new { id = run.ID, userId = run.UserID },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (baseline is not null)
            {
                previousEvents = baseline.EventCount;
            }
            foreach (CloudAgentEventRecord record in await QueryAsync<CloudAgentEventRecord>(
                connection,
                "SELECT \"sequence\" AS \"Sequence\", \"event_json\" AS \"EventJSON\" FROM \"cloud_agent_event_records\" " +
                "WHERE \"run_id\" = @runID AND \"user_id\" = @userID",
                new { runID = run.ID, userID = run.UserID },
                transaction,
                cancellationToken).ConfigureAwait(false))
            {
                previousEventBodies[record.Sequence] = record.EventJSON;
            }
            foreach (CloudAgentMessageRecord message in await QueryAsync<CloudAgentMessageRecord>(
                connection,
                "SELECT \"run_id\" AS \"RunID\", \"kind\" AS \"Kind\", \"sequence\" AS \"Sequence\", \"message_json\" AS \"MessageJSON\" " +
                "FROM \"cloud_agent_message_records\" WHERE \"run_id\" = @runID AND \"user_id\" = @userID",
                new { runID = run.ID, userID = run.UserID },
                transaction,
                cancellationToken).ConfigureAwait(false))
            {
                previousMessages[$"{message.Kind}:{message.Sequence}"] = message.MessageJSON;
            }
            if (run.EventCount < previousEvents
                || (run.Journal is null ? 0 : run.Journal.Count) != run.EventCount)
            {
                throw new InvalidOperationException("cloud Agent journal cannot be truncated");
            }
            foreach ((long sequence, string body) in previousEventBodies)
            {
                CloudAgentEventRecord? current = run.Journal is not null && sequence <= run.Journal.Count
                    ? run.Journal[(int)(sequence - 1)]
                    : null;
                if (current is null
                    || current.Sequence != sequence
                    || !CloudAgentJson.SameJsonDocument(current.EventJSON, body))
                {
                    throw new InvalidOperationException("cloud Agent journal is append-only");
                }
            }
        }

        int updated = await ExecuteAsync(
            connection,
            SqlBuilder.Update(typeof(CloudAgentExecution)),
            SqlBuilder.Parameters(run),
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (updated == 0)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert(typeof(CloudAgentExecution)), run, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        if (run.CheckpointVersion < 2)
        {
            return;
        }
        foreach (CloudAgentEventRecord record in run.Journal)
        {
            if (record.Sequence <= previousEvents)
            {
                continue;
            }
            await ExecuteAsync(
                connection,
                SqlBuilder.Insert(typeof(CloudAgentEventRecord)),
                SqlBuilder.Parameters(record),
                transaction,
                cancellationToken).ConfigureAwait(false);
        }
        foreach (CloudAgentMessageRecord message in run.Transcript)
        {
            string key = $"{message.Kind}:{message.Sequence}";
            if (previousMessages.TryGetValue(key, out string? existing) && existing == message.MessageJSON)
            {
                continue;
            }
            await ExecuteAsync(
                connection,
                """
                INSERT INTO "cloud_agent_message_records" ("run_id", "kind", "sequence", "user_id", "message_json")
                VALUES (@RunID, @Kind, @Sequence, @UserID, @MessageJSON)
                ON CONFLICT ("run_id", "kind", "sequence")
                DO UPDATE SET "message_json" = excluded."message_json"
                """,
                SqlBuilder.Parameters(message),
                transaction,
                cancellationToken).ConfigureAwait(false);
        }
        foreach (string kind in new[] { "canonical", "history" })
        {
            long count = run.Transcript.Count(message => message.Kind == kind);
            await ExecuteAsync(
                connection,
                "DELETE FROM \"cloud_agent_message_records\" WHERE \"run_id\" = @runID AND \"user_id\" = @userID " +
                "AND \"kind\" = @kind AND \"sequence\" > @count",
                new { runID = run.ID, userID = run.UserID, kind, count },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 调用者最近运行的 journal 行，按事件时间排列。运行先解析完整，
    /// 固定事件上限不会把一个运行的事件切开。对应 Go: <c>RecentCloudAgentEventsForUser</c>。
    /// </summary>
    public async Task<List<CloudAgentEventRecord>> RecentCloudAgentEventsForUserAsync(
        string userID, int runLimit, CancellationToken cancellationToken = default)
    {
        if (runLimit < 1)
        {
            return [];
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<string> runIDs = (await QueryAsync<string>(
            connection,
            "SELECT \"id\" FROM \"cloud_agent_executions\" WHERE \"user_id\" = @userID " +
            "ORDER BY \"created_at\" DESC LIMIT @runLimit",
            new { userID, runLimit },
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        if (runIDs.Count == 0)
        {
            return [];
        }
        List<CloudAgentEventRecord> records = (await QueryAsync<CloudAgentEventRecord>(
            connection,
            "SELECT \"run_id\" AS \"RunID\", \"sequence\" AS \"Sequence\", \"event_json\" AS \"EventJSON\", \"created_at\" AS \"CreatedAt\" " +
            "FROM \"cloud_agent_event_records\" WHERE \"user_id\" = @userID AND \"run_id\" IN @runIDs ORDER BY \"created_at\"",
            new { userID, runIDs },
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        return records;
    }

    /// <summary>加载 journal 与 transcript（CheckpointVersion>=2 的状态重建依据）。</summary>
    private async Task LoadCloudAgentJournalCoreAsync(
        DbConnection connection, DbTransaction? transaction, CloudAgentExecution run,
        CancellationToken cancellationToken)
    {
        if (run.CheckpointVersion < 2)
        {
            return;
        }
        run.Journal = (await QueryAsync<CloudAgentEventRecord>(
            connection,
            "SELECT \"run_id\" AS \"RunID\", \"sequence\" AS \"Sequence\", \"event_json\" AS \"EventJSON\", \"created_at\" AS \"CreatedAt\" " +
            "FROM \"cloud_agent_event_records\" WHERE \"run_id\" = @runID AND \"user_id\" = @userID ORDER BY \"sequence\"",
            new { runID = run.ID, userID = run.UserID },
            transaction,
            cancellationToken).ConfigureAwait(false)).ToList();
        run.Transcript = (await QueryAsync<CloudAgentMessageRecord>(
            connection,
            "SELECT \"run_id\" AS \"RunID\", \"kind\" AS \"Kind\", \"sequence\" AS \"Sequence\", \"message_json\" AS \"MessageJSON\" " +
            "FROM \"cloud_agent_message_records\" WHERE \"run_id\" = @runID AND \"user_id\" = @userID ORDER BY \"kind\", \"sequence\"",
            new { runID = run.ID, userID = run.UserID },
            transaction,
            cancellationToken).ConfigureAwait(false)).ToList();
    }

    /// <summary>事务内记录 Agent 画布变更。对应 Go: <c>tx.Create</c> 同名方法。</summary>
    internal async Task CreateCloudAgentCanvasMutationInTxAsync(
        DbConnection connection, DbTransaction transaction, CloudAgentCanvasMutation mutation,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            SqlBuilder.Insert(typeof(CloudAgentCanvasMutation)),
            SqlBuilder.Parameters(mutation),
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>记录一次 Agent 画布变更。对应 Go: <c>CreateCloudAgentCanvasMutation</c>。</summary>
    public async Task CreateCloudAgentCanvasMutationAsync(
        CloudAgentCanvasMutation mutation, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(CloudAgentCanvasMutation)),
            SqlBuilder.Parameters(mutation),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>运行内最近的画布变更；不存在返回 null。对应 Go: <c>LatestCloudAgentCanvasMutation</c>。</summary>
    public async Task<CloudAgentCanvasMutation?> LatestCloudAgentCanvasMutationAsync(
        string userId, string runId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<CloudAgentCanvasMutation>(
            connection,
            SqlBuilder.Select<CloudAgentCanvasMutation>(
                "user_id = @userId AND run_id = @runId",
                limitOffset: " ORDER BY created_at DESC, id DESC LIMIT 1"),
            new { userId, runId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CloudAgentCanvasMutation?> LatestCloudAgentCanvasMutationByIdAsync(
        DbConnection connection, DbTransaction? transaction, string userId, string runId,
        CancellationToken cancellationToken)
    {
        return await FirstOrDefaultAsync<CloudAgentCanvasMutation>(
            connection,
            SqlBuilder.Select<CloudAgentCanvasMutation>(
                "user_id = @userId AND run_id = @runId",
                limitOffset: " ORDER BY created_at DESC, id DESC LIMIT 1"),
            new { userId, runId },
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>事务内标记 undo；未命中返回 false。</summary>
    public async Task<bool> MarkCloudAgentCanvasMutationUndoneInTxAsync(
        DbConnection connection, DbTransaction transaction, string userId, string runId,
        string mutationId, DateTime undoneAt, CancellationToken cancellationToken)
    {
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE "cloud_agent_canvas_mutations" SET "status" = 'undone', "undone_at" = @undoneAt
            WHERE "id" = @mutationId AND "user_id" = @userId AND "run_id" = @runId AND "status" = 'applied'
            """,
            new { mutationId, userId, runId, undoneAt },
            transaction,
            cancellationToken).ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>把 applied 变更标记为 undone；未命中返回 false。对应 Go: <c>MarkCloudAgentCanvasMutationUndone</c>。</summary>
    public async Task<bool> MarkCloudAgentCanvasMutationUndoneAsync(
        string userId, string runId, string mutationId, DateTime undoneAt,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE "cloud_agent_canvas_mutations" SET "status" = 'undone', "undone_at" = @undoneAt
            WHERE "id" = @mutationId AND "user_id" = @userId AND "run_id" = @runId AND "status" = 'applied'
            """,
            new { mutationId, userId, runId, undoneAt },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>
    /// 终态 CAS：不改写 StateJSON，损坏状态也能停止调度。未命中返回 false。
    /// 对应 Go: <c>MarkCloudAgentFailed</c>。
    /// </summary>
    public async Task<bool> MarkCloudAgentFailedAsync(
        string userId, string id, long revision, string message,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE "cloud_agent_executions"
            SET "status" = 'failed', "cleanup_pending" = @cleanup, "failure_message" = @message, "revision" = "revision" + 1
            WHERE "id" = @id AND "user_id" = @userId AND "revision" = @revision AND "status" IN ('queued', 'running')
            """,
            new { userId, id, revision, cleanup = true, message },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>
    /// 损坏安全的取消 CAS；不触碰 StateJSON。未命中返回 false。
    /// 对应 Go: <c>MarkCloudAgentCancelled</c>。
    /// </summary>
    public async Task<bool> MarkCloudAgentCancelledAsync(
        string userId, string id, long revision, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE "cloud_agent_executions"
            SET "status" = 'cancelled', "cleanup_pending" = @cleanup, "revision" = "revision" + 1
            WHERE "id" = @id AND "user_id" = @userId AND "revision" = @revision AND "status" IN ('queued', 'running', 'waiting_approval')
            """,
            new { userId, id, revision, cleanup = true },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated == 1;
    }
}

/// <summary>
/// Agent 运行互斥变更上下文：回调内的画布/任务/资源读写都经由本上下文落在同一事务。
/// 对应 Go: <c>MutateCloudAgent</c> 回调里的 <c>repo *Repository</c>（New(tx)）。
/// </summary>
public sealed class CloudAgentMutationContext : IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    private readonly Repository _repository;
    private readonly bool _ownsConnection;

    /// <summary>宿主仓储（事务外读取走这里；账单事实等旁路读取使用）。</summary>
    public Repository Repository => _repository;

    internal CloudAgentMutationContext(
        DbConnection connection, DbTransaction? transaction, Repository repository,
        bool ownsConnection = false)
    {
        _connection = connection;
        _transaction = transaction!;
        _repository = repository;
        _ownsConnection = ownsConnection;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsConnection)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>事务内读执行记录。</summary>
    public Task<CloudAgentExecution?> CloudAgentAsync(
        string userId, string id, CancellationToken cancellationToken = default) =>
        _repository.CloudAgentInTxAsync(_connection, _transaction, userId, id, cancellationToken);

    /// <summary>保存执行记录（GORM Save 语义，事务内）。</summary>
    public Task SaveRunAsync(CloudAgentExecution run, CancellationToken cancellationToken = default) =>
        _repository.SaveCloudAgentInTxAsync(_connection, _transaction, run, cancellationToken);

    public Task<CanvasProject?> CanvasProjectForUserAsync(
        string userId, string id, CancellationToken cancellationToken = default) =>
        _repository.CanvasProjectForUserInTxAsync(_connection, _transaction, userId, id, cancellationToken);

    public Task CompareSaveCreationCanvasAsync(
        CanvasProject canvas, string previous, CancellationToken cancellationToken = default) =>
        _repository.CompareSaveCreationCanvasInTxAsync(_connection, _transaction, canvas, previous, cancellationToken);

    public Task<TaskEntity?> TaskForUserAsync(
        string userId, string taskId, CancellationToken cancellationToken = default) =>
        _repository.TaskForUserInTxAsync(_connection, _transaction, userId, taskId, cancellationToken);

    public Task<Resource?> ResourceForUserAsync(
        string userId, string id, CancellationToken cancellationToken = default) =>
        _repository.ResourceForUserInTxAsync(_connection, _transaction, userId, id, cancellationToken);

    public Task<UserStorageUsage> UserStorageUsageAsync(
        string userId, CancellationToken cancellationToken = default) =>
        _repository.UserStorageUsageInTxAsync(_connection, _transaction, userId, cancellationToken);

    /// <summary>在事务内落库任务（配额校验）。对应 Go: <c>createTaskWithStorageQuotaRepository</c>。</summary>
    public Task CreateTaskWithQuotaAsync(
        TaskEntity task, BillingOrder? order, int activeTaskLimit, CancellationToken cancellationToken = default) =>
        _repository.CreateTaskWithQuotaInTxAsync(_connection, _transaction, task, order, activeTaskLimit, cancellationToken);

    public Task CreateCloudAgentCanvasMutationAsync(
        CloudAgentCanvasMutation mutation, CancellationToken cancellationToken = default) =>
        _repository.CreateCloudAgentCanvasMutationInTxAsync(_connection, _transaction, mutation, cancellationToken);

    public Task<CloudAgentCanvasMutation?> LatestCanvasMutationAsync(
        string userId, string runId, CancellationToken cancellationToken = default) =>
        _repository.LatestCloudAgentCanvasMutationByIdAsync(
            _connection, _transaction, userId, runId, cancellationToken);

    public Task<bool> MarkCanvasMutationUndoneAsync(
        string userId, string runId, string mutationId, DateTime undoneAt,
        CancellationToken cancellationToken = default) =>
        _repository.MarkCloudAgentCanvasMutationUndoneInTxAsync(
            _connection, _transaction, userId, runId, mutationId, undoneAt, cancellationToken);

    // ---------------------------------------------------- Agent 个人记忆（事务内）

    public Task<long> CountAgentLessonsByAuthorAsync(
        string userId, string status, CancellationToken cancellationToken = default) =>
        _repository.CountAgentLessonsByAuthorInTxAsync(_connection, _transaction, userId, status, cancellationToken);

    public Task<List<AgentLesson>> UserAgentLessonsAsync(
        string userId, string status, int limit, CancellationToken cancellationToken = default) =>
        _repository.UserAgentLessonsInTxAsync(_connection, _transaction, userId, status, limit, cancellationToken);

    public Task<AgentLesson?> AgentLessonByTopicAsync(
        string userId, string topic, CancellationToken cancellationToken = default) =>
        _repository.AgentLessonByTopicInTxAsync(_connection, _transaction, userId, topic, cancellationToken);

    public Task<List<AgentLesson>> ApprovedAgentLessonsAsync(
        string userId, int limit, CancellationToken cancellationToken = default) =>
        _repository.ApprovedAgentLessonsInTxAsync(_connection, _transaction, userId, limit, cancellationToken);

    public Task<List<AgentLesson>> AgentLessonsByCategoryAsync(
        string userId, string category, int limit, CancellationToken cancellationToken = default) =>
        _repository.AgentLessonsByCategoryInTxAsync(_connection, _transaction, userId, category, limit, cancellationToken);

    public Task<List<AgentLessonCategoryCountRow>> ApprovedAgentLessonCategoryCountsAsync(
        string userId, CancellationToken cancellationToken = default) =>
        _repository.ApprovedAgentLessonCategoryCountsInTxAsync(_connection, _transaction, userId, cancellationToken);

    public Task BumpAgentLessonHitsAsync(
        string userId, IReadOnlyList<string> ids, CancellationToken cancellationToken = default) =>
        _repository.BumpAgentLessonHitsInTxAsync(_connection, _transaction, userId, ids, cancellationToken);

    public Task TouchAgentLessonAsync(
        string userId, string id, DateTime verifiedAt, CancellationToken cancellationToken = default) =>
        _repository.TouchAgentLessonInTxAsync(_connection, _transaction, userId, id, verifiedAt, cancellationToken);

    public Task CreateAgentLessonAsync(
        AgentLesson lesson, CancellationToken cancellationToken = default) =>
        _repository.CreateAgentLessonInTxAsync(_connection, _transaction, lesson, cancellationToken);

    // -------------------------------------------------- Agent 资源租约（事务内）

    public Task UpsertCloudAgentResourceLeasesAsync(
        string userId, string runId, string ownerId, IReadOnlyList<string> resourceIds, DateTime expiresAt,
        CancellationToken cancellationToken = default) =>
        _repository.UpsertCloudAgentResourceLeasesInTxAsync(
            _connection, _transaction, userId, runId, ownerId, resourceIds, expiresAt, cancellationToken);

    public Task ReplaceCloudAgentResourceLeasesAsync(
        string userId, string runId, string ownerId, IReadOnlyList<string> resourceIds, DateTime expiresAt,
        CancellationToken cancellationToken = default) =>
        _repository.ReplaceCloudAgentResourceLeasesInTxAsync(
            _connection, _transaction, userId, runId, ownerId, resourceIds, expiresAt, cancellationToken);

    public Task ReleaseCloudAgentResourceLeasesAsync(
        string userId, string ownerId, CancellationToken cancellationToken = default) =>
        _repository.ReleaseCloudAgentResourceLeasesInTxAsync(
            _connection, _transaction, userId, ownerId, cancellationToken);

    public Task ReleaseCloudAgentResourceLeasesByRunAsync(
        string userId, string runId, CancellationToken cancellationToken = default) =>
        _repository.ReleaseCloudAgentResourceLeasesByRunInTxAsync(_connection, _transaction, userId, runId, cancellationToken);

    public Task TransferCloudAgentResourceLeasesAsync(
        string userId, string fromOwner, string toOwner, string runId, DateTime expiresAt,
        CancellationToken cancellationToken = default) =>
        _repository.TransferCloudAgentResourceLeasesInTxAsync(
            _connection, _transaction, userId, fromOwner, toOwner, runId, expiresAt, cancellationToken);
}
