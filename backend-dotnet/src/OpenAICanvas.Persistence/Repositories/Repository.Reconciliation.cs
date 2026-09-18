#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 支付对账运行与明细。对应 Go: <c>repository/payment.go</c> 的对账部分。
/// </summary>
/// <remarks>
/// 每天每个渠道只能有一个对账运行：<see cref="BeginPaymentReconciliationAsync"/> 用
/// <c>(provider_id, bill_date)</c> 唯一键 + 行锁保证「两个 worker 不会同时补发同一笔积分」。
/// </remarks>
public sealed partial class Repository
{
    /// <summary>对账运行占用后，多久内不重跑（避免 worker 互相抢）。对应 Go 的 30 分钟冷却。</summary>
    private static readonly TimeSpan ReconciliationCooldown = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 开始（或复用）某渠道某天的对账运行。返回 <c>Started=false</c> 表示已有运行在进行中。
    /// 对应 Go: <c>BeginPaymentReconciliation</c>。
    /// </summary>
    public async Task<(PaymentReconciliationRun Run, bool Started)> BeginPaymentReconciliationAsync(
        PaymentReconciliationRun run, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        PaymentReconciliationRun? current = await FirstOrDefaultAsync<PaymentReconciliationRun>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<PaymentReconciliationRun>()} FROM payment_reconciliation_runs
            WHERE provider_id = @providerId AND bill_date = @billDate
            {Dialect.ForUpdate()}
            """,
            new { providerId = run.ProviderID, billDate = run.BillDate },
            transaction,
            cancellationToken).ConfigureAwait(false);

        bool started = false;

        if (current is null)
        {
            int created = await ExecuteAsync(
                connection,
                SqlBuilder.Insert<PaymentReconciliationRun>()
                    + Dialect.OnConflictDoNothing("\"provider_id\", \"bill_date\""),
                run,
                transaction,
                cancellationToken).ConfigureAwait(false);

            if (created == 1)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return (run, true);
            }

            // 并发下别的 worker 先插入成功：重新读出来，按同样的冷却规则处理，而不是报冲突。
            current = await FirstOrDefaultAsync<PaymentReconciliationRun>(
                connection,
                $"""
                SELECT {SqlBuilder.Projection<PaymentReconciliationRun>()} FROM payment_reconciliation_runs
                WHERE provider_id = @providerId AND bill_date = @billDate
                {Dialect.ForUpdate()}
                """,
                new { providerId = run.ProviderID, billDate = run.BillDate },
                transaction,
                cancellationToken).ConfigureAwait(false)
                ?? throw new PaymentStateConflictException();
        }

        // 已有运行且仍在冷却期内：让调用方直接拿到现状，不再重复下载账单。
        if (current.Status == PaymentReconciliationStatus.PaymentReconciliationRunning
            && current.UpdatedAt > DateTime.UtcNow - ReconciliationCooldown)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (current, false);
        }

        // 重跑前清掉旧明细，避免新旧结果混在一起。
        await ExecuteAsync(
            connection,
            "DELETE FROM payment_reconciliation_items WHERE run_id = @runId",
            new { runId = current.ID },
            transaction,
            cancellationToken).ConfigureAwait(false);

        DateTime now = DateTime.UtcNow;
        await ExecuteAsync(
            connection,
            """
            UPDATE payment_reconciliation_runs SET
                config_id = @configId, status = @running,
                total_items = 0, match_items = 0, recovered_items = 0, error_items = 0,
                error = '', started_by = @startedBy, started_at = @now, completed_at = NULL,
                updated_at = @now
            WHERE id = @id
            """,
            new
            {
                configId = run.ConfigID,
                running = PaymentReconciliationStatus.PaymentReconciliationRunning,
                startedBy = run.StartedBy,
                now,
                id = current.ID,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        started = true;

        PaymentReconciliationRun reloaded = await FirstOrDefaultAsync<PaymentReconciliationRun>(
            connection,
            SqlBuilder.Select<PaymentReconciliationRun>("id = @id", limitOffset: " LIMIT 1"),
            new { id = current.ID },
            transaction,
            cancellationToken).ConfigureAwait(false) ?? current;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (reloaded, started);
    }

    /// <summary>
    /// 写入对账明细并置为完成。条件更新 <c>status = running</c> 保证不会覆盖已终结的运行。
    /// 对应 Go: <c>CompletePaymentReconciliation</c>。
    /// </summary>
    public async Task CompletePaymentReconciliationAsync(
        string id,
        IReadOnlyList<PaymentReconciliationItem> items,
        int matched,
        int recovered,
        int failed,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // 按 200 条分批插入，避免单条语句参数过多。
        for (int index = 0; index < items.Count; index += 200)
        {
            PaymentReconciliationItem[] chunk = items.Skip(index).Take(200).ToArray();
            await ExecuteAsync(
                connection, SqlBuilder.Insert<PaymentReconciliationItem>(), chunk, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        DateTime now = DateTime.UtcNow;
        int affected = await ExecuteAsync(
            connection,
            """
            UPDATE payment_reconciliation_runs SET
                status = @completed, total_items = @totalItems,
                match_items = @matched, recovered_items = @recovered, error_items = @failed,
                error = '', completed_at = @now, updated_at = @now
            WHERE id = @id AND status = @running
            """,
            new
            {
                completed = PaymentReconciliationStatus.PaymentReconciliationCompleted,
                totalItems = items.Count,
                matched,
                recovered,
                failed,
                now,
                id = id.Trim(),
                // WHERE 里用的条件值也必须传参，否则 Dapper 会报缺少参数。
                running = PaymentReconciliationStatus.PaymentReconciliationRunning,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new PaymentStateConflictException();
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>标记对账失败。对应 Go: <c>FailPaymentReconciliation</c>。</summary>
    public async Task FailPaymentReconciliationAsync(
        string id, string message, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        await ExecuteAsync(
            connection,
            """
            UPDATE payment_reconciliation_runs SET
                status = @failed, error = @message, completed_at = @now, updated_at = @now
            WHERE id = @id
            """,
            new
            {
                failed = PaymentReconciliationStatus.PaymentReconciliationFailed,
                message,
                now,
                id = id.Trim(),
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 查对账运行。对应 Go: <c>PaymentReconciliationRun</c>。</summary>
    public async Task<PaymentReconciliationRun?> PaymentReconciliationRunAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<PaymentReconciliationRun>(
            connection,
            SqlBuilder.Select<PaymentReconciliationRun>("id = @id", limitOffset: " LIMIT 1"),
            new { id = id.Trim() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按渠道 + 账单日查对账运行。对应 Go: <c>PaymentReconciliationRunByDate</c>。</summary>
    public async Task<PaymentReconciliationRun?> PaymentReconciliationRunByDateAsync(
        string providerId, string billDate, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<PaymentReconciliationRun>(
            connection,
            SqlBuilder.Select<PaymentReconciliationRun>(
                "provider_id = @providerId AND bill_date = @billDate", limitOffset: " LIMIT 1"),
            new { providerId = providerId.Trim(), billDate = billDate.Trim() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>对账运行分页。对应 Go: <c>AdminPaymentReconciliationRuns</c>。</summary>
    public async Task<(IReadOnlyList<PaymentReconciliationRun> Runs, long Total)> AdminPaymentReconciliationRunsAsync(
        string providerId, string status, int limit, int offset, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        List<string> conditions = [];
        DynamicParameters parameters = new();

        string normalizedProvider = providerId.Trim();
        if (normalizedProvider.Length > 0 && normalizedProvider != "all")
        {
            conditions.Add("provider_id = @providerId");
            parameters.Add("providerId", normalizedProvider);
        }

        string normalizedStatus = status.Trim();
        if (normalizedStatus.Length > 0 && normalizedStatus != "all")
        {
            conditions.Add("status = @status");
            parameters.Add("status", normalizedStatus);
        }

        string where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);

        long total = await ScalarAsync<long>(
            connection, $"SELECT COUNT(*) FROM payment_reconciliation_runs{where}", parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        IReadOnlyList<PaymentReconciliationRun> runs = await QueryAsync<PaymentReconciliationRun>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<PaymentReconciliationRun>()} FROM payment_reconciliation_runs{where}
            ORDER BY bill_date DESC, started_at DESC
            {Dialect.LimitOffset(limit, offset)}
            """,
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (runs, total);
    }

    /// <summary>
    /// 对账明细分页。未解决的排在前面（管理员优先处理）。
    /// 对应 Go: <c>PaymentReconciliationItems</c>。
    /// </summary>
    public async Task<(IReadOnlyList<PaymentReconciliationItem> Items, long Total)> PaymentReconciliationItemsAsync(
        string runId, string result, int limit, int offset, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        List<string> conditions = ["run_id = @runId"];
        DynamicParameters parameters = new();
        parameters.Add("runId", runId.Trim());

        string normalizedResult = result.Trim();
        if (normalizedResult.Length > 0 && normalizedResult != "all")
        {
            conditions.Add("result = @result");
            parameters.Add("result", normalizedResult);
        }

        string where = " WHERE " + string.Join(" AND ", conditions);

        long total = await ScalarAsync<long>(
            connection, $"SELECT COUNT(*) FROM payment_reconciliation_items{where}", parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        IReadOnlyList<PaymentReconciliationItem> items = await QueryAsync<PaymentReconciliationItem>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<PaymentReconciliationItem>()} FROM payment_reconciliation_items{where}
            ORDER BY resolved ASC, created_at ASC
            {Dialect.LimitOffset(limit, offset)}
            """,
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (items, total);
    }

    /// <summary>
    /// 账单日区间内已入账的本地订单（按渠道支付时间）。
    /// 对应 Go: <c>CreditedPaymentOrdersBetween</c>。
    /// </summary>
    public async Task<IReadOnlyList<PaymentOrder>> CreditedPaymentOrdersBetweenAsync(
        string providerId, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<PaymentOrder>(
            connection,
            SqlBuilder.Select<PaymentOrder>(
                "provider_id = @providerId AND status = @credited AND provider_paid_at >= @start AND provider_paid_at < @end",
                orderBy: "provider_paid_at ASC"),
            new
            {
                providerId = providerId.Trim(),
                credited = PaymentOrderStatus.PaymentOrderCredited,
                start,
                end,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 账单日内结果仍未确定的订单数。用于判断「渠道账单缺失」是否真的可以视为无异常。
    /// 对应 Go: <c>UnresolvedPaymentOrderCandidateCountOverlapping</c>。
    /// </summary>
    /// <remarks>
    /// 已关闭订单由查单/关单确认为未支付，不再计入；已入账订单另行与账单比对。
    /// </remarks>
    public async Task<long> UnresolvedPaymentOrderCandidateCountOverlappingAsync(
        string providerId, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long>(
            connection,
            """
            SELECT COUNT(*) FROM payment_orders
            WHERE provider_id = @providerId AND status IN @statuses
              AND created_at < @end AND expires_at >= @start
            """,
            new
            {
                providerId = providerId.Trim(),
                statuses = new[]
                {
                    PaymentOrderStatus.PaymentOrderCreated,
                    PaymentOrderStatus.PaymentOrderPending,
                    PaymentOrderStatus.PaymentOrderClosing,
                    PaymentOrderStatus.PaymentOrderCreateFailed,
                },
                start,
                end,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
