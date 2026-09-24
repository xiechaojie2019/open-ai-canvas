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
        return await FirstOrDefaultAsync<CloudAgentExecution>(
            connection,
            SqlBuilder.Select<CloudAgentExecution>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按活动模型任务查运行。对应 Go: <c>CloudAgentForActiveTask</c>。</summary>
    public async Task<CloudAgentExecution?> CloudAgentForActiveTaskAsync(
        string userId, string taskId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<CloudAgentExecution>(
            connection,
            SqlBuilder.Select<CloudAgentExecution>(
                "user_id = @userId AND active_task_id = @taskId AND status IN ('running', 'queued')",
                limitOffset: " LIMIT 1"),
            new { userId, taskId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
        return await QueryAsync<CloudAgentExecution>(
            connection,
            SqlBuilder.Select<CloudAgentExecution>(
                "(status IN ('running', 'queued') OR cleanup_pending = @cleanup) AND id > @after",
                limitOffset: " ORDER BY id LIMIT @limit"),
            new { cleanup = true, after, limit },
            cancellationToken: cancellationToken).ConfigureAwait(false);
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

    public Task<object?> MutateCloudAgentAsync(
        string userId,
        string id,
        long revision,
        Func<CloudAgentExecution, CloudAgentMutationContext, Task> mutate,
        CancellationToken cancellationToken = default)
        => MutateCloudAgentAsync<object?>(
            userId, id, revision,
            async (run, context) =>
            {
                await mutate(run, context).ConfigureAwait(false);
                return null;
            },
            cancellationToken);

    /// <summary>事务内保存执行记录（GORM Save 语义）。对应 Go: <c>tx.Save(run)</c>。</summary>
    public async Task SaveCloudAgentInTxAsync(
        DbConnection connection, DbTransaction transaction, CloudAgentExecution run,
        CancellationToken cancellationToken)
    {
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
public sealed class CloudAgentMutationContext
{
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    private readonly Repository _repository;

    internal CloudAgentMutationContext(DbConnection connection, DbTransaction transaction, Repository repository)
    {
        _connection = connection;
        _transaction = transaction;
        _repository = repository;
    }

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
}
