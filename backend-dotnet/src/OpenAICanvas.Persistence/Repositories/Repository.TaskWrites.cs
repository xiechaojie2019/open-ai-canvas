#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>任务写入仓储。对应 Go: <c>repository/finance.go</c> 的任务创建事务与取消条件更新。</summary>
public sealed partial class Repository
{
    /// <summary>用户活动任务数（排队 + 运行）。对应 Go: <c>ActiveTaskCountForUser</c>。</summary>
    public async Task<long> ActiveTaskCountForUserAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM tasks WHERE \"userId\" = @userId AND status IN ('queued', 'running')",
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建任务（无计费；活跃限额 + 逻辑模型有效性同事务）。对应 Go: <c>CreateTaskWithActiveLimit</c>。</summary>
    public Task CreateTaskWithActiveLimitAsync(
        TaskEntity task, int activeTaskLimit, CancellationToken cancellationToken = default) =>
        CreateTaskInTransactionAsync(task, billingOrder: null, activeTaskLimit, cancellationToken);

    /// <summary>创建任务（含计费预留）。对应 Go: <c>CreateTaskWithCreditReservation</c>。</summary>
    public Task CreateTaskWithCreditReservationAsync(
        TaskEntity task, BillingOrder order, int activeTaskLimit, CancellationToken cancellationToken = default) =>
        CreateTaskInTransactionAsync(task, order, activeTaskLimit, cancellationToken);

    private async Task CreateTaskInTransactionAsync(
        TaskEntity task, BillingOrder? billingOrder, int activeTaskLimit, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // 逻辑模型有效性守卫：任务引用的前台模型必须仍启用且版本未变。
        if (task.LogicalModelID.Length > 0)
        {
            long active = await ScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM \"logicalModels\" WHERE id = @id AND enabled = 1 AND \"archivedAt\" IS NULL AND \"activeRevisionId\" = @revision",
                new { id = task.LogicalModelID, revision = task.LogicalModelRevisionID },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (active == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("logical_model_unavailable");
            }
        }

        long count = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM tasks WHERE \"userId\" = @userId AND status IN ('queued', 'running')",
            new { userId = task.UserID },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (count >= activeTaskLimit)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("active_task_limit");
        }

        if (billingOrder is not null)
        {
            await ReserveBillingOrderInTransactionAsync(
                connection, transaction, billingOrder, cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection, SqlBuilder.Insert<TaskEntity>(), task, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>积分预留。对应 Go: <c>reserveBillingOrder</c>（余额不足抛 insufficient_credits）。</summary>
    private async Task ReserveBillingOrderInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        BillingOrder order,
        CancellationToken cancellationToken)
    {
        long reserved = order.ReservedAmountMicrocredits > 0
            ? order.ReservedAmountMicrocredits
            : order.AmountMicrocredits;
        order.ReservedAmountMicrocredits = reserved;

        await ExecuteAsync(
            connection,
            "INSERT INTO \"creditAccounts\" (\"userId\", \"availableMicrocredits\", \"reservedMicrocredits\", version, \"createdAt\", \"updatedAt\") VALUES (@userId, 0, 0, 0, @now, @now) ON CONFLICT DO NOTHING",
            new { userId = order.UserID, now = DateTime.UtcNow },
            transaction,
            cancellationToken).ConfigureAwait(false);

        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE "creditAccounts" SET
              "availableMicrocredits" = "availableMicrocredits" - @amount,
              "reservedMicrocredits" = "reservedMicrocredits" + @amount,
              version = version + 1, "updatedAt" = @now
            WHERE "userId" = @userId AND "availableMicrocredits" >= @amount
            """,
            new { amount = order.AmountMicrocredits, now = DateTime.UtcNow, userId = order.UserID },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("insufficient_credits");
        }
        await ExecuteAsync(
            connection, SqlBuilder.Insert<BillingOrder>(), order, transaction, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>条件取消：仅当任务仍处于预期状态时落终态。对应 Go: <c>CancelTaskIfStatus</c>。</summary>
    public Task<bool> CancelTaskIfStatusAsync(
        string userId, string taskId, string expectedStatus, DateTime now, CancellationToken cancellationToken = default) =>
        CancelTaskIfStatusAsync(userId, taskId, expectedStatus, null, null, null, now, cancellationToken);

    /// <summary>带取消链事实的条件取消（Go v28）。对应 Go: <c>CancelTaskIfStatus</c> 的取消来源参数。</summary>
    public async Task<bool> CancelTaskIfStatusAsync(
        string userId,
        string taskId,
        string expectedStatus,
        string? cancellationSource,
        string? cancellationActorID,
        string? cancellationDiagnosticJSON,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE tasks SET status = 'cancelled', stage = '任务已取消', error = '任务已取消',
              "completedAt" = @now, "updatedAt" = @now
              {cancellationSet}
            WHERE id = @taskId AND "userId" = @userId AND status = @expectedStatus
            """.Replace("{cancellationSet}", cancellationSource is null
                ? ""
                : ", \"cancellationSource\" = @cancellationSource, \"cancellationActorId\" = @cancellationActorID, \"cancellationRequestedAt\" = @now, \"executionDiagnosticJson\" = @diagnosticJSON"),
            new
            {
                taskId,
                userId,
                expectedStatus,
                now,
                cancellationSource,
                cancellationActorID,
                diagnosticJSON = cancellationDiagnosticJSON ?? (object?)DBNull.Value,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>
    /// 重试事务：活跃限额、计费预留、任务字段重置与 route_run 递增、文本增量清空。
    /// 对应 Go: <c>RetryTaskWithBilling</c>。命中 0 行抛 task_not_retryable。
    /// </summary>
    public async Task<TaskEntity> RetryTaskWithBillingAsync(
        string userId,
        TaskEntity prepared,
        BillingOrder? order,
        int activeTaskLimit,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long count = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM tasks WHERE \"userId\" = @userId AND status IN ('queued', 'running')",
            new { userId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (count >= activeTaskLimit)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("active_task_limit");
        }
        if (order is not null)
        {
            await ReserveBillingOrderInTransactionAsync(
                connection, transaction, order, cancellationToken).ConfigureAwait(false);
        }

        DateTime now = DateTime.UtcNow;
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE tasks SET
              status = 'queued', stage = '等待队列调度', progress = 5, error = '', "resultJson" = '',
              "textDraft" = '', "startedAt" = NULL, "completedAt" = NULL,
              "providerRequestId" = '', "pollStage" = '', "nextPollAt" = NULL,
              "providerCancelStatus" = '', "providerCancelError" = '', "providerCancelAttempts" = 0,
              "providerCancelRequestedAt" = NULL, "providerCancelledAt" = NULL, "providerCancelNextCheckAt" = NULL,
              "routeRun" = "routeRun" + 1,
              "logicalModelRevisionId" = @LogicalModelRevisionID, "routeId" = @RouteID,
              "channelModelId" = @ChannelModelID, "inputJson" = @InputJSON,
              model = @Model, provider = @Provider,
              "leaseOwner" = '', "leaseExpiresAt" = NULL, "updatedAt" = @now,
              "billingOrderId" = @billingOrderId
            WHERE id = @ID AND "userId" = @UserID AND status IN ('failed', 'cancelled')
            """,
            new
            {
                prepared.LogicalModelRevisionID,
                prepared.RouteID,
                prepared.ChannelModelID,
                prepared.InputJSON,
                prepared.Model,
                prepared.Provider,
                now,
                billingOrderId = order is not null ? order.ID : "",
                prepared.ID,
                prepared.UserID,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("task_not_retryable");
        }
        await ExecuteAsync(
            connection,
            "DELETE FROM \"taskTextDelta\" WHERE \"userId\" = @userId AND \"taskId\" = @taskId",
            new { userId, taskId = prepared.ID },
            transaction,
            cancellationToken).ConfigureAwait(false);

        TaskEntity? task = await FirstOrDefaultAsync<TaskEntity>(
            connection,
            SqlBuilder.Select<TaskEntity>("id = @id AND \"userId\" = @userId", limitOffset: " LIMIT 1"),
            new { id = prepared.ID, userId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("record not found");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return task;
    }

}
