#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 兑换码、积分发放/调账与账单列表。
/// 对应 Go 的 <c>repository/finance.go</c>（积分与兑换码部分）与
/// <c>repository/admin_audit.go</c>（兑换码禁用）。
/// </summary>
public sealed partial class Repository
{
    // ---------------------------------------------------------------- 积分发放

    /// <summary>
    /// 幂等发放积分：靠账本 <c>reference_key</c> 唯一索引去重。
    /// 对应 Go: <c>GrantCreditsOnce</c>。返回账户与是否本次真正发放。
    /// </summary>
    public async Task<(CreditAccount Account, bool Granted)> GrantCreditsOnceAsync(
        string userId,
        string entryType,
        long amount,
        string referenceKey,
        string note,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // 账户不存在时先建（并发下靠主键冲突忽略）。
        await ExecuteAsync(
            connection,
            SqlBuilder.Insert<CreditAccount>() + Dialect.OnConflictDoNothing(string.Empty),
            new CreditAccount
            {
                UserID = userId,
                AvailableMicrocredits = 0,
                ReservedMicrocredits = 0,
                Version = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        CreditLedgerEntry entry = new()
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            Type = entryType,
            AmountMicrocredits = amount,
            ReferenceKey = referenceKey,
            Note = note,
            CreatedAt = DateTime.UtcNow,
        };

        int created = await ExecuteAsync(
            connection,
            SqlBuilder.Insert<CreditLedgerEntry>() + Dialect.OnConflictDoNothing("\"reference_key\""),
            entry,
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (created == 0)
        {
            // 已发放过：返回当前账户，granted = false。
            CreditAccount? existing = await QuerySingleOrDefaultAsync<CreditAccount>(
                connection,
                SqlBuilder.Select<CreditAccount>("user_id = @userId", limitOffset: " LIMIT 1"),
                new { userId },
                transaction,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (existing ?? new CreditAccount { UserID = userId }, false);
        }

        await ExecuteAsync(
            connection,
            """
            UPDATE credit_accounts SET
                available_microcredits = available_microcredits + @amount,
                version = version + 1,
                updated_at = @now
            WHERE user_id = @userId
            """,
            new { amount, now = DateTime.UtcNow, userId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        CreditAccount account = await QuerySingleOrDefaultAsync<CreditAccount>(
            connection,
            SqlBuilder.Select<CreditAccount>("user_id = @userId", limitOffset: " LIMIT 1"),
            new { userId },
            transaction,
            cancellationToken).ConfigureAwait(false) ?? new CreditAccount { UserID = userId };

        // 回填账本条目的变化量与结余快照。
        await ExecuteAsync(
            connection,
            """
            UPDATE credit_ledger_entries SET
                available_delta_microcredits = @amount,
                available_after_microcredits = @availableAfter,
                reserved_after_microcredits = @reservedAfter
            WHERE id = @id
            """,
            new
            {
                amount,
                availableAfter = account.AvailableMicrocredits,
                reservedAfter = account.ReservedMicrocredits,
                id = entry.ID,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (account, true);
    }

    /// <summary>
    /// 管理员调账（可正可负）。扣减时用条件更新保证余额不为负。
    /// 对应 Go: <c>AdjustCredits</c>。
    /// </summary>
    public async Task<CreditAccount> AdjustCreditsAsync(
        string userId,
        string actorUserId,
        long amount,
        string note,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            SqlBuilder.Insert<CreditAccount>() + Dialect.OnConflictDoNothing(string.Empty),
            new CreditAccount
            {
                UserID = userId,
                AvailableMicrocredits = 0,
                ReservedMicrocredits = 0,
                Version = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        // 扣减时附带余额下限条件：命中 0 行说明余额不足。
        string guard = amount < 0 ? " AND available_microcredits + @amount >= 0" : "";
        int affected = await ExecuteAsync(
            connection,
            $"""
            UPDATE credit_accounts SET
                available_microcredits = available_microcredits + @amount,
                version = version + 1,
                updated_at = @now
            WHERE user_id = @userId{guard}
            """,
            new { amount, now = DateTime.UtcNow, userId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InsufficientCreditsException();
        }

        CreditAccount account = await QuerySingleOrDefaultAsync<CreditAccount>(
            connection,
            SqlBuilder.Select<CreditAccount>("user_id = @userId", limitOffset: " LIMIT 1"),
            new { userId },
            transaction,
            cancellationToken).ConfigureAwait(false) ?? new CreditAccount { UserID = userId };

        await ExecuteAsync(
            connection,
            SqlBuilder.Insert<CreditLedgerEntry>(),
            new CreditLedgerEntry
            {
                ID = IdGenerator.NewId(),
                UserID = userId,
                // 加分为 admin_grant，扣减为 admin_adjustment。
                Type = amount > 0 ? CreditLedgerType.CreditLedgerAdminGrant : CreditLedgerType.CreditLedgerAdminAdjust,
                AmountMicrocredits = amount,
                AvailableDeltaMicrocredits = amount,
                AvailableAfterMicrocredits = account.AvailableMicrocredits,
                ReservedAfterMicrocredits = account.ReservedMicrocredits,
                ActorUserID = actorUserId,
                Note = note,
                CreatedAt = DateTime.UtcNow,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return account;
    }

    // ---------------------------------------------------------------- 兑换码

    /// <summary>创建兑换码批次与全部兑换码。对应 Go: <c>CreateRedeemBatch</c>。</summary>
    public async Task CreateRedeemBatchAsync(
        RedeemBatch batch,
        IReadOnlyList<RedeemCode> codes,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection, SqlBuilder.Insert<RedeemBatch>(), batch, transaction, cancellationToken).ConfigureAwait(false);

        // Go 用 CreateInBatches(200) 分批；这里按同样粒度提交，避免单条语句参数过多。
        foreach (RedeemCode[] chunk in Chunk(codes, 200))
        {
            await ExecuteAsync(
                connection,
                SqlBuilder.Insert<RedeemCode>(),
                chunk,
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 兑换码批次列表（含可用/已兑换/已禁用/已过期计数）。
    /// 对应 Go: <c>AdminRedeemBatches</c>。
    /// </summary>
    public async Task<(IReadOnlyList<RedeemBatch> Batches, long Total)> AdminRedeemBatchesAsync(
        string keyword,
        string validity,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        List<string> conditions = [];
        DynamicParameters parameters = new();
        string trimmed = keyword.Trim();
        if (trimmed.Length > 0)
        {
            conditions.Add("(lower(note) LIKE @pattern OR CAST(amount_microcredits AS TEXT) LIKE @pattern OR CAST(count AS TEXT) LIKE @pattern)");
            parameters.Add("pattern", "%" + trimmed.ToLowerInvariant() + "%");
        }

        DateTime now = DateTime.UtcNow;
        if (validity == "active")
        {
            conditions.Add("(expires_at IS NULL OR expires_at > @validityNow)");
            parameters.Add("validityNow", now);
        }
        else if (validity == "expired")
        {
            conditions.Add("(expires_at IS NOT NULL AND expires_at <= @validityNow)");
            parameters.Add("validityNow", now);
        }

        string where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);

        long total = await ScalarAsync<long>(
            connection,
            $"SELECT COUNT(*) FROM redeem_batches{where}",
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // 四个计数是子查询别名，Dapper 按列别名回填到实体的瞬态字段。
        parameters.Add("now", now);
        string sql = $"""
            SELECT
                {SqlBuilder.Projection<RedeemBatch>("redeem_batches")},
                (SELECT COUNT(*) FROM redeem_codes rc WHERE rc.batch_id = redeem_batches.id AND rc.status = 'unused' AND (rc.expires_at IS NULL OR rc.expires_at > @now)) AS "AvailableCount",
                (SELECT COUNT(*) FROM redeem_codes rc WHERE rc.batch_id = redeem_batches.id AND rc.status = 'redeemed') AS "RedeemedCount",
                (SELECT COUNT(*) FROM redeem_codes rc WHERE rc.batch_id = redeem_batches.id AND rc.status = 'disabled') AS "DisabledCount",
                (SELECT COUNT(*) FROM redeem_codes rc WHERE rc.batch_id = redeem_batches.id AND rc.status = 'unused' AND rc.expires_at IS NOT NULL AND rc.expires_at <= @now) AS "ExpiredCount"
            FROM redeem_batches{where}
            ORDER BY created_at DESC
            {Dialect.LimitOffset(limit, offset)}
            """;

        IReadOnlyList<RedeemBatch> batches = await QueryAsync<RedeemBatch>(
            connection, sql, parameters, cancellationToken: cancellationToken).ConfigureAwait(false);

        return (batches, total);
    }

    /// <summary>单个批次（含计数）。对应 Go: <c>RedeemBatch</c>。</summary>
    public async Task<RedeemBatch?> RedeemBatchAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;

        string sql = $"""
            SELECT
                {SqlBuilder.Projection<RedeemBatch>("redeem_batches")},
                (SELECT COUNT(*) FROM redeem_codes rc WHERE rc.batch_id = redeem_batches.id AND rc.status = 'unused' AND (rc.expires_at IS NULL OR rc.expires_at > @now)) AS "AvailableCount",
                (SELECT COUNT(*) FROM redeem_codes rc WHERE rc.batch_id = redeem_batches.id AND rc.status = 'redeemed') AS "RedeemedCount",
                (SELECT COUNT(*) FROM redeem_codes rc WHERE rc.batch_id = redeem_batches.id AND rc.status = 'disabled') AS "DisabledCount",
                (SELECT COUNT(*) FROM redeem_codes rc WHERE rc.batch_id = redeem_batches.id AND rc.status = 'unused' AND rc.expires_at IS NOT NULL AND rc.expires_at <= @now) AS "ExpiredCount"
            FROM redeem_batches
            WHERE redeem_batches.id = @id
            LIMIT 1
            """;

        return await FirstOrDefaultAsync<RedeemBatch>(
            connection, sql, new { id, now }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 批次内的兑换码（含兑换人用户名/昵称）。
    /// 对应 Go: <c>AdminRedeemCodes</c>。
    /// </summary>
    public async Task<(IReadOnlyList<AdminRedeemCodeRow> Codes, long Total)> AdminRedeemCodesAsync(
        string batchId,
        string status,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        List<string> conditions = ["redeem_codes.batch_id = @batchId"];
        DynamicParameters parameters = new();
        parameters.Add("batchId", batchId);
        DateTime now = DateTime.UtcNow;
        parameters.Add("now", now);

        switch (status)
        {
            case "available":
                conditions.Add("(redeem_codes.status = @unused AND (redeem_codes.expires_at IS NULL OR redeem_codes.expires_at > @now))");
                parameters.Add("unused", RedeemCodeStatus.RedeemCodeUnused);
                break;
            case "redeemed":
                conditions.Add("redeem_codes.status = @redeemed");
                parameters.Add("redeemed", RedeemCodeStatus.RedeemCodeRedeemed);
                break;
            case "disabled":
                conditions.Add("redeem_codes.status = @disabled");
                parameters.Add("disabled", RedeemCodeStatus.RedeemCodeDisabled);
                break;
            case "expired":
                conditions.Add("(redeem_codes.status = @unused AND redeem_codes.expires_at IS NOT NULL AND redeem_codes.expires_at <= @now)");
                parameters.Add("unused", RedeemCodeStatus.RedeemCodeUnused);
                break;
        }

        string where = " WHERE " + string.Join(" AND ", conditions);

        long total = await ScalarAsync<long>(
            connection,
            $"SELECT COUNT(*) FROM redeem_codes{where}",
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string sql = $"""
            SELECT
                {SqlBuilder.Projection<RedeemCode>("redeem_codes")},
                users.username AS "RedeemedUsername", users.display_name AS "RedeemedDisplayName"
            FROM redeem_codes
            LEFT JOIN users ON users.id = redeem_codes.redeemed_by
            {where}
            ORDER BY redeem_codes.created_at ASC, redeem_codes.id ASC
            {Dialect.LimitOffset(limit, offset)}
            """;

        IReadOnlyList<AdminRedeemCodeRow> rows = await QueryAsync<AdminRedeemCodeRow>(
            connection, sql, parameters, cancellationToken: cancellationToken).ConfigureAwait(false);

        return (rows, total);
    }

    /// <summary>
    /// 核销兑换码并发放积分。对应 Go: <c>RedeemCode</c>。
    /// 用条件更新抢占兑换码，抢不到说明已用/已禁用/已过期。
    /// </summary>
    public async Task<CreditAccount> RedeemCodeAsync(
        string userId,
        string codeHash,
        string redeemedIp,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        RedeemCode? code = await FirstOrDefaultAsync<RedeemCode>(
            connection,
            SqlBuilder.Select<RedeemCode>("code_hash = @codeHash", limitOffset: " LIMIT 1"),
            new { codeHash },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (code is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new RedeemCodeInvalidException();
        }

        DateTime now = DateTime.UtcNow;
        string expiryGuard = code.ExpiresAt is not null ? " AND expires_at > @now" : "";
        int affected = await ExecuteAsync(
            connection,
            $"""
            UPDATE redeem_codes SET
                status = @redeemed, redeemed_by = @userId, redeemed_at = @now,
                redeemed_ip = @redeemedIp, updated_at = @now
            WHERE id = @id AND status = @unused{expiryGuard}
            """,
            new
            {
                redeemed = RedeemCodeStatus.RedeemCodeRedeemed,
                userId,
                now,
                redeemedIp,
                id = code.ID,
                unused = RedeemCodeStatus.RedeemCodeUnused,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new RedeemCodeInvalidException();
        }

        await ExecuteAsync(
            connection,
            SqlBuilder.Insert<CreditAccount>() + Dialect.OnConflictDoNothing(string.Empty),
            new CreditAccount
            {
                UserID = userId,
                AvailableMicrocredits = 0,
                ReservedMicrocredits = 0,
                Version = 0,
                CreatedAt = now,
                UpdatedAt = now,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            """
            UPDATE credit_accounts SET
                available_microcredits = available_microcredits + @amount,
                version = version + 1,
                updated_at = @now
            WHERE user_id = @userId
            """,
            new { amount = code.AmountMicrocredits, now, userId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        CreditAccount account = await QuerySingleOrDefaultAsync<CreditAccount>(
            connection,
            SqlBuilder.Select<CreditAccount>("user_id = @userId", limitOffset: " LIMIT 1"),
            new { userId },
            transaction,
            cancellationToken).ConfigureAwait(false) ?? new CreditAccount { UserID = userId };

        await ExecuteAsync(
            connection,
            SqlBuilder.Insert<CreditLedgerEntry>(),
            new CreditLedgerEntry
            {
                ID = IdGenerator.NewId(),
                UserID = userId,
                Type = CreditLedgerType.CreditLedgerRedeem,
                AmountMicrocredits = code.AmountMicrocredits,
                AvailableDeltaMicrocredits = code.AmountMicrocredits,
                AvailableAfterMicrocredits = account.AvailableMicrocredits,
                ReservedAfterMicrocredits = account.ReservedMicrocredits,
                RedeemCodeID = code.ID,
                Note = "兑换码充值",
                CreatedAt = now,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return account;
    }

    /// <summary>禁用批次内全部未使用（且未过期）的兑换码。对应 Go: <c>DisableRedeemBatch</c>。</summary>
    public async Task<long> DisableRedeemBatchAsync(
        string batchId, DateTime now, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(
            connection,
            """
            UPDATE redeem_codes SET status = @disabled, updated_at = @now
            WHERE batch_id = @batchId AND status = @unused
              AND (expires_at IS NULL OR expires_at > @now)
            """,
            new
            {
                disabled = RedeemCodeStatus.RedeemCodeDisabled,
                now,
                batchId,
                unused = RedeemCodeStatus.RedeemCodeUnused,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>禁用单个兑换码。对应 Go: <c>DisableRedeemCode</c>。</summary>
    public async Task<bool> DisableRedeemCodeAsync(
        string batchId, string codeId, DateTime now, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await ExecuteAsync(
            connection,
            """
            UPDATE redeem_codes SET status = @disabled, updated_at = @now
            WHERE id = @codeId AND batch_id = @batchId AND status = @unused
              AND (expires_at IS NULL OR expires_at > @now)
            """,
            new
            {
                disabled = RedeemCodeStatus.RedeemCodeDisabled,
                now,
                codeId,
                batchId,
                unused = RedeemCodeStatus.RedeemCodeUnused,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    // ---------------------------------------------------------------- 账单列表

    /// <summary>
    /// 管理员账单列表。<c>status="review"</c> 是待人工核对的复合条件。
    /// 对应 Go: <c>AdminBillingOrders</c>。
    /// </summary>
    public async Task<(IReadOnlyList<BillingOrder> Orders, long Total)> AdminBillingOrdersAsync(
        string status,
        string keyword,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        List<string> joins = [];
        List<string> conditions = [];
        DynamicParameters parameters = new();

        DateTime now = DateTime.UtcNow;
        if (status == "review")
        {
            // 待核对：明确 uncertain，或 running 超时，或 reserved 但任务已终结。
            joins.Add("LEFT JOIN tasks ON tasks.id = billing_orders.task_id");
            conditions.Add("""
                (billing_orders.status = @uncertain
                 OR (billing_orders.status = @running AND billing_orders.updated_at < @staleBefore)
                 OR (billing_orders.status = @reserved AND tasks.status IN @terminalStatuses))
                """);
            parameters.Add("uncertain", BillingStatus.BillingStatusUncertain);
            parameters.Add("running", BillingStatus.BillingStatusRunning);
            parameters.Add("staleBefore", now.AddMinutes(-40));
            parameters.Add("reserved", BillingStatus.BillingStatusReserved);
            parameters.Add("terminalStatuses", new[] { TaskStatus.TaskStatusFailed, TaskStatus.TaskStatusCancelled });
        }
        else if (status.Length > 0 && status != "all")
        {
            conditions.Add("billing_orders.status = @status");
            parameters.Add("status", status);
        }

        string trimmed = keyword.Trim();
        if (trimmed.Length > 0)
        {
            joins.Add("LEFT JOIN users ON users.id = billing_orders.user_id");
            conditions.Add("""
                (lower(billing_orders.model) LIKE @pattern
                 OR lower(billing_orders.scene) LIKE @pattern
                 OR lower(billing_orders.provider_request_id) LIKE @pattern
                 OR lower(users.username) LIKE @pattern
                 OR lower(users.display_name) LIKE @pattern)
                """);
            parameters.Add("pattern", "%" + trimmed.ToLowerInvariant() + "%");
        }

        string joinSql = joins.Count == 0 ? "" : " " + string.Join(" ", joins);
        string whereSql = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);

        long total = await ScalarAsync<long>(
            connection,
            $"SELECT COUNT(*) FROM billing_orders{joinSql}{whereSql}",
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string sql = $"""
            SELECT {SqlBuilder.Projection<BillingOrder>("billing_orders")} FROM billing_orders{joinSql}{whereSql}
            ORDER BY billing_orders.created_at DESC
            {Dialect.LimitOffset(limit, offset)}
            """;

        IReadOnlyList<BillingOrder> orders = await QueryAsync<BillingOrder>(
            connection, sql, parameters, cancellationToken: cancellationToken).ConfigureAwait(false);

        return (orders, total);
    }

    // ---------------------------------------------------------------- 内部

    private static IEnumerable<T[]> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (int index = 0; index < source.Count; index += size)
        {
            yield return source.Skip(index).Take(size).ToArray();
        }
    }
}

/// <summary>批次内兑换码行（含兑换人信息）。对应 Go: <c>AdminRedeemCodeRow</c>。</summary>
public sealed class AdminRedeemCodeRow
{
    public string ID { get; set; } = string.Empty;
    public string BatchID { get; set; } = string.Empty;

    /// <summary>兑换码哈希。Go 侧嵌入 RedeemCode 时随行返回，用于反查明文。</summary>
    public string CodeHash { get; set; } = string.Empty;
    public string CodeSuffix { get; set; } = string.Empty;
    public long AmountMicrocredits { get; set; }
    public string Status { get; set; } = string.Empty;
    public string RedeemedBy { get; set; } = string.Empty;
    public DateTime? RedeemedAt { get; set; }
    public string RedeemedIP { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string RedeemedUsername { get; set; } = string.Empty;
    public string RedeemedDisplayName { get; set; } = string.Empty;
}

/// <summary>余额不足。对应 Go: <c>ErrInsufficientCredits</c>。</summary>
public sealed class InsufficientCreditsException : Exception
{
    public InsufficientCreditsException() : base("insufficient credits")
    {
    }
}

/// <summary>兑换码无效或已使用。对应 Go: <c>ErrRedeemCodeInvalid</c>。</summary>
public sealed class RedeemCodeInvalidException : Exception
{
    public RedeemCodeInvalidException() : base("redeem code invalid")
    {
    }
}
