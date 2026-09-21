#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>任务状态在领取后被其他执行者改写。对应 Go: <c>repository.ErrTaskStateConflict</c>。</summary>
public sealed class TaskStateConflictException : Exception
{
    public TaskStateConflictException()
        : base("task state changed concurrently")
    {
    }
}

/// <summary>
/// 任务租约仓储：领取、续期、让渡、进度与终态写入。
/// 对应 Go: <c>repository/repository.go</c> 的 ClaimNextTask / RenewTaskLease /
/// ReleaseTaskLease / DeferRunningTaskForProviderPoll / UpdateTaskProgressForLease /
/// taskLeaseWriter / SaveTaskCompletion / UpdateTaskTerminalState。
/// </summary>
public sealed partial class Repository
{
    // ------------------------------------------------------------- 租约 WHERE 片段

    /// <summary>
    /// 租约写入者的 WHERE 片段。无租约任务只允许无租约记录写入；
    /// 有租约的执行者必须仍持有未过期的 owner。对应 Go: <c>taskLeaseWriter</c>。
    /// </summary>
    private static string TaskLeaseWriterCondition(string owner) =>
        owner.Length == 0
            ? "(lease_owner = '' OR lease_owner IS NULL)"
            : "lease_owner = @owner AND lease_expires_at > @now";

    private static object TaskLeaseWriterParameters(string owner) =>
        owner.Length == 0
            ? new { }
            : (object)new { owner, now = DateTime.UtcNow };

    // ------------------------------------------------------------- 领取

    /// <summary>
    /// 领取下一个可执行任务并写入租约。对应 Go: <c>ClaimNextTask</c>。
    /// 排队任务立即可领；运行任务在租约过期后可被接管的 worker 重新领取；
    /// 带 next_poll_at 的让渡任务到点后才能再次领取。
    /// </summary>
    /// <remarks>
    /// PostgreSQL 在事务内锁行跳过竞争任务；SQLite 没有行锁，
    /// UPDATE 必须重复领取条件（与 Go 的非 PG 分支一致），
    /// 让条件更新本身成为原子性来源。
    /// </remarks>
    public async Task<TaskEntity?> ClaimNextTaskAsync(
        string owner, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        DateTime now = DateTime.UtcNow;
        string claimCondition =
            "(status = @queued OR (status = @running AND (lease_expires_at IS NULL OR lease_expires_at <= @now))) "
            + "AND (next_poll_at IS NULL OR next_poll_at <= @now)";
        var claimParams = new { queued = TaskStatus.TaskStatusQueued, running = TaskStatus.TaskStatusRunning, now };

        string selectSql = SqlBuilder.Select<TaskEntity>(claimCondition, orderBy: "created_at ASC", limitOffset: " LIMIT 1");
        if (Dialect.IsPostgres)
        {
            selectSql += " FOR UPDATE SKIP LOCKED";
        }

        TaskEntity? task = await FirstOrDefaultAsync<TaskEntity>(
            connection, selectSql, claimParams, transaction, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        // 非 PG 方言重复领取条件：SQLite 下 SELECT 不持行锁，条件更新是唯一原子性来源。
        string updateWhere = $"id = @id";
        if (!Dialect.IsPostgres)
        {
            updateWhere += $" AND {claimCondition}";
        }
        string updateSql =
            "UPDATE tasks SET status = @running, stage = @stage, progress = @progress, "
            + "attempts = attempts + 1, started_at = COALESCE(started_at, @now), "
            + "lease_owner = @owner, lease_expires_at = @leaseExpiresAt, next_poll_at = NULL, updated_at = @now "
            + "WHERE " + updateWhere;
        int affected = await ExecuteAsync(
            connection,
            updateSql,
            new
            {
                id = task.ID,
                queued = TaskStatus.TaskStatusQueued,
                running = TaskStatus.TaskStatusRunning,
                now,
                stage = "后端接管任务",
                progress = 15L,
                owner,
                leaseExpiresAt = now.Add(leaseDuration),
            },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        task = await FirstOrDefaultAsync<TaskEntity>(
            connection,
            SqlBuilder.Select<TaskEntity>("id = @id", limitOffset: " LIMIT 1"),
            new { id = task.ID },
            transaction,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return task;
    }

    // ------------------------------------------------------------- 续期与释放

    /// <summary>续期任务租约。过期即失效。对应 Go: <c>RenewTaskLease</c>。</summary>
    public async Task RenewTaskLeaseAsync(
        string id, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        int affected = await ExecuteAsync(
            connection,
            "UPDATE tasks SET lease_expires_at = @newExpiresAt, updated_at = @now "
            + "WHERE id = @id AND status = @running AND lease_owner = @owner AND lease_expires_at > @now",
            new
            {
                id,
                owner,
                running = TaskStatus.TaskStatusRunning,
                now,
                newExpiresAt = now.Add(leaseDuration),
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException("任务租约已失效");
        }
    }

    /// <summary>释放任务租约（worker 停止兜底）。对应 Go: <c>ReleaseTaskLease</c>。</summary>
    public async Task ReleaseTaskLeaseAsync(
        string id, string owner, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE tasks SET lease_owner = '', lease_expires_at = NULL, updated_at = @now "
            + "WHERE id = @id AND status = @running AND lease_owner = @owner",
            new { id, owner, now = DateTime.UtcNow, running = TaskStatus.TaskStatusRunning },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- 让渡与进度

    /// <summary>
    /// 把运行中任务让渡回轮询队列，等待上游任务同步后再次领取。
    /// 对应 Go: <c>DeferRunningTaskForProviderPoll</c>。
    /// </summary>
    public async Task DeferRunningTaskForProviderPollAsync(
        string id, string owner, string stage, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        int affected = await ExecuteAsync(
            connection,
            "UPDATE tasks SET stage = @stage, error = '', completed_at = NULL, next_poll_at = @nextPollAt, "
            + "lease_owner = '', lease_expires_at = NULL, updated_at = @now "
            + "WHERE id = @id AND status = @running AND " + TaskLeaseWriterCondition(owner),
            MergeParameters(
                new { id, stage, nextPollAt = now.Add(delay), now, running = TaskStatus.TaskStatusRunning },
                TaskLeaseWriterParameters(owner)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new TaskStateConflictException();
        }
    }

    /// <summary>在持有租约的前提下更新阶段与进度。对应 Go: <c>UpdateTaskProgressForLease</c>。</summary>
    public async Task UpdateTaskProgressForLeaseAsync(
        string id, string owner, string stage, long progress, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await ExecuteAsync(
            connection,
            "UPDATE tasks SET stage = @stage, progress = @progress, updated_at = @now "
            + "WHERE id = @id AND status = @running AND " + TaskLeaseWriterCondition(owner),
            MergeParameters(
                new { id, stage, progress, now = DateTime.UtcNow, running = TaskStatus.TaskStatusRunning },
                TaskLeaseWriterParameters(owner)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new TaskStateConflictException();
        }
    }

    // ------------------------------------------------------------- 终态与结果

    /// <summary>
    /// 写入任务终态并清空租约。返回是否命中。对应 Go: <c>UpdateTaskTerminalState</c>。
    /// </summary>
    public async Task<bool> UpdateTaskTerminalStateAsync(
        string id,
        string owner,
        string expectedStatus,
        string status,
        string stage,
        string errorText,
        DateTime completedAt,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await ExecuteAsync(
            connection,
            "UPDATE tasks SET status = @status, stage = @stage, error = @error, completed_at = @completedAt, "
            + "lease_owner = '', lease_expires_at = NULL, updated_at = @completedAt "
            + "WHERE id = @id AND status = @expected AND " + TaskLeaseWriterCondition(owner),
            MergeParameters(
                new
                {
                    id,
                    expected = expectedStatus,
                    status,
                    stage,
                    error = errorText,
                    completedAt,
                },
                TaskLeaseWriterParameters(owner)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }

    /// <summary>
    /// 在持有租约的前提下保存任务结果与结果行。对应 Go: <c>SaveTaskCompletion</c>。
    /// </summary>
    /// <remarks>
    /// 与 Go 的 Select("*").Omit("id","created_at") 不同，这里显式列出业务列：
    /// 输入解密结果、输出、阶段进度与租约字段是 worker 实际会变更的集合，
    /// 避免把 trace/request 等只读列一并回写。
    /// </remarks>
    public async Task SaveTaskCompletionAsync(
        TaskEntity task,
        string expectedStatus,
        IReadOnlyList<Result> results,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        string where =
            "id = @id AND status = @expected AND " + TaskLeaseWriterCondition(task.LeaseOwner);
        var parameters = MergeParameters(
            new Dictionary<string, object?>
            {
                ["id"] = task.ID,
                ["expected"] = expectedStatus,
                ["status"] = task.Status,
                ["stage"] = task.Stage,
                ["progress"] = task.Progress,
                ["prompt"] = task.Prompt,
                ["error"] = task.Error,
                ["inputJson"] = task.InputJSON,
                ["resultJson"] = task.ResultJSON,
                ["textDraft"] = task.TextDraft,
                ["providerRequestId"] = task.ProviderRequestID,
                ["providerCancelStatus"] = task.ProviderCancelStatus,
                ["providerCancelError"] = task.ProviderCancelError,
                ["providerCancelAttempts"] = task.ProviderCancelAttempts,
                ["pollStage"] = task.PollStage,
                ["completedAt"] = task.CompletedAt,
                ["startedAt"] = task.StartedAt,
                ["leaseOwner"] = task.LeaseOwner,
                ["leaseExpiresAt"] = task.LeaseExpiresAt,
                ["nextPollAt"] = task.NextPollAt,
                ["updatedAt"] = DateTime.UtcNow,
            },
            TaskLeaseWriterParameters(task.LeaseOwner));

        string assignments = string.Join(
            ", ",
            new[]
            {
                "status = @status", "stage = @stage", "progress = @progress", "prompt = @prompt",
                "error = @error", "input_json = @inputJson", "result_json = @resultJson",
                "text_draft = @textDraft", "provider_request_id = @providerRequestId",
                "provider_cancel_status = @providerCancelStatus", "provider_cancel_error = @providerCancelError",
                "provider_cancel_attempts = @providerCancelAttempts", "poll_stage = @pollStage",
                "completed_at = @completedAt", "started_at = @startedAt",
                "lease_owner = @leaseOwner", "lease_expires_at = @leaseExpiresAt",
                "next_poll_at = @nextPollAt", "updated_at = @updatedAt",
            });
        int affected = await ExecuteAsync(
            connection,
            "UPDATE tasks SET " + assignments + " WHERE " + where,
            parameters,
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new TaskStateConflictException();
        }

        foreach (Result result in results)
        {
            int inserted = await ExecuteAsync(
                connection,
                SqlBuilder.Insert<Result>(),
                result,
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (inserted != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new TaskStateConflictException();
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>合并两个参数对象为 DynamicParameters（后者同名列覆盖前者）。</summary>
    private static DynamicParameters MergeParameters(object primary, object secondary)
    {
        DynamicParameters merged = new();
        if (primary is Dictionary<string, object?> dictionary)
        {
            foreach (KeyValuePair<string, object?> pair in dictionary)
            {
                merged.Add(pair.Key, pair.Value);
            }
        }
        else
        {
            foreach (System.Reflection.PropertyInfo property in primary.GetType()
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length == 0)
                {
                    merged.Add(property.Name, property.GetValue(primary));
                }
            }
        }
        foreach (System.Reflection.PropertyInfo property in secondary.GetType()
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0)
            {
                merged.Add(property.Name, property.GetValue(secondary));
            }
        }
        return merged;
    }
}
