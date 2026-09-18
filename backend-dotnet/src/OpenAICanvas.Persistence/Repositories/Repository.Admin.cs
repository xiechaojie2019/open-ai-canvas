using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 积分账户、批量停用与审计事件的仓储方法。
/// 对应 Go 的 <c>repository/finance.go</c> 与 <c>repository/admin_audit.go</c>。
/// </summary>
public sealed partial class Repository
{
    // ---------------------------------------------------------------- 积分账户

    /// <summary>按用户查积分账户；不存在返回 null。对应 Go: <c>CreditAccount</c>。</summary>
    public async Task<CreditAccount?> CreditAccountAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<CreditAccount>(
            connection,
            SqlBuilder.Select<CreditAccount>("user_id = @userId", limitOffset: " LIMIT 1"),
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>批量查积分账户。对应 Go: <c>CreditAccounts</c>。</summary>
    public async Task<IReadOnlyList<CreditAccount>> CreditAccountsAsync(
        IReadOnlyList<string> userIds,
        CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<CreditAccount>(
            connection,
            SqlBuilder.Select<CreditAccount>("user_id IN @userIds"),
            new { userIds },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 审计事件

    /// <summary>追加一条管理员审计事件。对应 Go: <c>AppendAdminAudit</c>。</summary>
    public async Task AppendAdminAuditAsync(
        AdminAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(AdminAuditEvent)),
            auditEvent,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>管理员审计事件分页。对应 Go: <c>AdminAuditEvents</c>。</summary>
    public async Task<(IReadOnlyList<AdminAuditEvent> Events, long Total)> AdminAuditEventsAsync(
        string targetType,
        string targetId,
        long limit,
        long offset,
        CancellationToken cancellationToken = default)
    {
        string condition = "target_type = @targetType AND target_id = @targetId";
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        long total = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"admin_audit_events\" WHERE " + condition,
            new { targetType, targetId },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        IReadOnlyList<AdminAuditEvent> events = await QueryAsync<AdminAuditEvent>(
            connection,
            SqlBuilder.Select<AdminAuditEvent>(condition, "created_at DESC", Dialect.LimitOffset(limit, offset)),
            new { targetType, targetId },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (events, total);
    }

    // ---------------------------------------------------------------- 批量停用

    /// <summary>批量停用的失败原因。对应 Go 的 <c>ErrBulk*</c> 三个哨兵错误。</summary>
    public enum BulkDisableOutcome
    {
        Ok,
        UserNotFound,
        IncludesCurrentAdmin,
        RemovesLastActiveAdmin,
    }

    /// <summary>
    /// 批量停用用户。保留校验、会话清理、状态更新与审计写入在同一事务内完成，
    /// 避免出现"部分停用"的中间态。对应 Go: <c>BulkDisableUsers</c>。
    /// </summary>
    public async Task<(BulkDisableOutcome Outcome, IReadOnlyList<User> Users)> BulkDisableUsersAsync(
        string actorId,
        IReadOnlyList<string> userIds,
        IReadOnlyList<AdminAuditEvent> events,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            // PostgreSQL 下对目标行加锁，避免并发批量操作互相覆盖。
            string lockClause = Dialect.ForUpdate();
            IReadOnlyList<User> users = await QueryAsync<User>(
                connection,
                SqlBuilder.Select<User>("id IN @userIds") + lockClause,
                new { userIds },
                transaction,
                cancellationToken).ConfigureAwait(false);

            if (users.Count != userIds.Count)
            {
                return (BulkDisableOutcome.UserNotFound, (IReadOnlyList<User>)Array.Empty<User>());
            }

            foreach (User user in users)
            {
                if (string.Equals(user.ID, actorId, StringComparison.Ordinal))
                {
                    return (BulkDisableOutcome.IncludesCurrentAdmin, (IReadOnlyList<User>)Array.Empty<User>());
                }
            }

            long remainingAdmins = await ScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM \"users\" WHERE role = @role AND status = @status AND id NOT IN @userIds",
                new { role = "admin", status = "active", userIds },
                transaction,
                cancellationToken).ConfigureAwait(false);

            if (remainingAdmins == 0)
            {
                return (BulkDisableOutcome.RemovesLastActiveAdmin, (IReadOnlyList<User>)Array.Empty<User>());
            }

            await ExecuteAsync(connection, "DELETE FROM \"auth_sessions\" WHERE user_id IN @userIds",
                new { userIds }, transaction, cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, "DELETE FROM \"task_text_delta\" WHERE user_id IN @userIds",
                new { userIds }, transaction, cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                "UPDATE \"users\" SET status = @status, updated_at = @now WHERE id IN @userIds",
                new { status = "disabled", now, userIds },
                transaction,
                cancellationToken).ConfigureAwait(false);

            foreach (AdminAuditEvent auditEvent in events)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert(typeof(AdminAuditEvent)),
                    auditEvent,
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            IReadOnlyList<User> updated = await QueryAsync<User>(
                connection,
                SqlBuilder.Select<User>("id IN @userIds"),
                new { userIds },
                transaction,
                cancellationToken).ConfigureAwait(false);

            return (BulkDisableOutcome.Ok, updated);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除用户的文本增量存档。对应 Go: <c>DeleteUserTaskTextDeltas</c>。</summary>
    public async Task DeleteUserTaskTextDeltasAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "DELETE FROM \"task_text_delta\" WHERE user_id = @userId",
            new { userId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>统计用户关联数据量。对应 Go: <c>AdminUserCounts</c>。</summary>
    public async Task<AdminUserCounts> AdminUserCountsAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        long ledger = await ScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM \"credit_ledger_entries\" WHERE user_id = @userId",
            new { userId }, cancellationToken: cancellationToken).ConfigureAwait(false);

        long tasks = await ScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM \"tasks\" WHERE user_id = @userId",
            new { userId }, cancellationToken: cancellationToken).ConfigureAwait(false);

        long apiCalls = await ScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM \"api_call_logs\" WHERE user_id = @userId",
            new { userId }, cancellationToken: cancellationToken).ConfigureAwait(false);

        long audit = await ScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM \"admin_audit_events\" WHERE target_type = 'user' AND target_id = @userId",
            new { userId }, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new AdminUserCounts
        {
            LedgerEntries = ledger,
            Tasks = tasks,
            ApiCalls = apiCalls,
            AuditEvents = audit,
        };
    }

    // ------------------------------------------------------------
    // 用户详情类查询
    // ------------------------------------------------------------

    /// <summary>用户存储用量汇总。对应 Go: <c>Repository.UserStorageUsage</c>。</summary>
    public async Task<UserStorageUsage> UserStorageUsageAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        string lengthExpr = Dialect.IsPostgres
            ? "octet_length(COALESCE({0}, ''))"
            : "length(CAST(COALESCE({0}, '') AS BLOB))";

        string query = $"""
            SELECT
                (SELECT COUNT(*) FROM assets WHERE user_id = @userId) AS asset_count,
                (SELECT COALESCE(SUM({string.Format(lengthExpr, "payload_json")}), 0) FROM assets WHERE user_id = @userId) AS asset_bytes,
                (SELECT COUNT(*) FROM canvas_projects WHERE user_id = @userId) AS canvas_count,
                (SELECT COALESCE(SUM({string.Format(lengthExpr, "payload_json")}), 0) FROM canvas_projects WHERE user_id = @userId) AS canvas_bytes,
                (SELECT COUNT(*) FROM tasks WHERE user_id = @userId) AS task_count,
                (
                    (SELECT COALESCE(SUM({string.Format(lengthExpr, "prompt")} + {string.Format(lengthExpr, "input_json")} + {string.Format(lengthExpr, "result_json")} + {string.Format(lengthExpr, "text_draft")} + {string.Format(lengthExpr, "error")}), 0) FROM tasks WHERE user_id = @userId)
                    + (SELECT COALESCE(SUM({string.Format(lengthExpr, "message")} + {string.Format(lengthExpr, "payload")}), 0) FROM task_logs WHERE user_id = @userId)
                    + (SELECT COALESCE(SUM({string.Format(lengthExpr, "url")} + {string.Format(lengthExpr, "payload")}), 0) FROM results WHERE user_id = @userId)
                    + (SELECT COALESCE(SUM(byte_count), 0) FROM task_text_delta WHERE user_id = @userId)
                    + (SELECT COALESCE(SUM({string.Format(lengthExpr, "path")} + {string.Format(lengthExpr, "model")} + {string.Format(lengthExpr, "provider_request_id")} + {string.Format(lengthExpr, "error_code")} + {string.Format(lengthExpr, "error")} + {string.Format(lengthExpr, "upstream_url")} + {string.Format(lengthExpr, "request_body")} + {string.Format(lengthExpr, "response_body")}), 0) FROM api_call_logs WHERE user_id = @userId)
                ) AS task_bytes,
                (SELECT COUNT(*) FROM api_call_logs WHERE user_id = @userId) AS api_call_count
            """;

        return await connection.QueryFirstOrDefaultAsync<UserStorageUsage>(
            new CommandDefinition(query, new { userId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false) ?? new UserStorageUsage();
    }

    /// <summary>用户已存储文件物理字节数（去重）。对应 Go: <c>Repository.UserStoredFileBytes</c>。</summary>
    public async Task<long> UserStoredFileBytesAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        const string query = """
            SELECT COALESCE((
                SELECT SUM(physical_resources.size)
                FROM (
                    SELECT MAX(size) AS size
                    FROM resources
                    WHERE user_id = @userId AND status = @status
                    GROUP BY COALESCE(NULLIF(provider, ''), 'local'), endpoint, bucket, object_key
                ) AS physical_resources
            ), 0)
            """;

        return await ScalarAsync<long>(connection, query,
            new { userId, status = ResourceStatus.ResourceStatusReady },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>用户当日上传字节数。对应 Go: <c>Repository.DailyUploadBytes</c>。</summary>
    public async Task<long> DailyUploadBytesAsync(string userId, string day, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        const string query = """
            SELECT COALESCE(bytes, 0) FROM user_daily_upload_usages
            WHERE user_id = @userId AND day = @day
            LIMIT 1
            """;

        return await ScalarAsync<long?>(connection, query,
            new { userId, day },
            cancellationToken: cancellationToken).ConfigureAwait(false) ?? 0;
    }
}

/// <summary>用户关联数据统计。对应 Go: <c>repository.AdminUserCounts</c>。</summary>
public sealed class AdminUserCounts
{
    public long LedgerEntries { get; init; }

    public long Tasks { get; init; }

    public long ApiCalls { get; init; }

    public long AuditEvents { get; init; }
}

/// <summary>用户存储用量汇总。对应 Go: <c>repository.UserStorageUsage</c>。</summary>
public sealed class UserStorageUsage
{
    public long AssetCount { get; init; }
    public long AssetBytes { get; init; }
    public long CanvasCount { get; init; }
    public long CanvasBytes { get; init; }
    public long TaskCount { get; init; }
    public long TaskBytes { get; init; }
    public long ApiCallCount { get; init; }
}
