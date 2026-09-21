#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 计费订单状态迁移（预留 → 运行 → 结算 / 退款 / 待核对）。
/// 对应 Go 的 <c>repository/finance.go</c> 计费部分与 <c>app/task_billing.go</c> 的协调层。
/// </summary>
/// <remarks>
/// 这是资金核心路径。所有余额变动都在事务内以「条件更新 + 校验影响行数」完成，
/// 保证并发下不会出现余额与订单状态不一致。账本条目是唯一的审计依据，
/// 每条迁移都必须写分录。
/// </remarks>
public sealed partial class Repository
{
    // ---------------------------------------------------------------- 状态迁移

    /// <summary>
    /// 预留 → 运行。对应 Go: <c>MarkBillingRunning</c>。
    /// </summary>
    public async Task MarkBillingRunningAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        BillingOrder? order = await FirstOrDefaultAsync<BillingOrder>(
            connection,
            "SELECT id, status FROM billing_orders WHERE id = @id LIMIT 1",
            new { id },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw AppError.NotFound("计费订单不存在");
        }

        if (order.Status == BillingStatus.BillingStatusRunning)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        DateTime now = DateTime.UtcNow;
        int affected = await ExecuteAsync(
            connection,
            """
            UPDATE billing_orders SET status = @running, started_at = @now, updated_at = @now
            WHERE id = @id AND status = @reserved
            """,
            new
            {
                running = BillingStatus.BillingStatusRunning,
                reserved = BillingStatus.BillingStatusReserved,
                now,
                id,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new BillingStateConflictException();
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>账单巡检统计。对应 Go: <c>repository.BillingReviewStats</c>。</summary>
    public sealed record BillingReviewStats(
        long Reserved, long Running, long Uncertain, DateTime? Oldest)
    {
        /// <summary>未闭合订单总数。对应 Go: <c>BillingReviewStats.Total</c>。</summary>
        public long Total => Reserved + Running + Uncertain;
    }

    /// <summary>
    /// 预留/运行 → 待核对。冻结积分保持不动，等人工处置。
    /// 对应 Go: <c>MarkBillingUncertain</c>。
    /// </summary>
    public async Task MarkBillingUncertainAsync(
        string id, string errorText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE billing_orders SET status = @uncertain, error = @errorText, updated_at = @now
            WHERE id = @id AND status IN @openStatuses
            """,
            new
            {
                uncertain = BillingStatus.BillingStatusUncertain,
                errorText,
                now = DateTime.UtcNow,
                id,
                openStatuses = new[] { BillingStatus.BillingStatusReserved, BillingStatus.BillingStatusRunning },
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 结算

    /// <summary>
    /// 结算订单：把预留积分转为实际消费，差额退回。
    /// 对应 Go: <c>SettleBillingOrder</c>。
    /// </summary>
    /// <remarks>
    /// token 计费按上游真实用量结算，并受 <c>charge_limit_microcredits</c> 硬上限约束。
    /// 结算失败但已拿到上游用量时，用量仍会写回订单（上游事实不能因账户异常而丢失）。
    /// </remarks>
    public async Task SettleBillingOrderAsync(
        string id, string providerRequestId, CancellationToken cancellationToken = default)
    {
        BillingUsage? observedUsage = null;
        long observedActual = 0;
        bool observedActualAvailable = false;

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            BillingOrder? order = await FirstOrDefaultAsync<BillingOrder>(
                connection,
                SqlBuilder.Select<BillingOrder>("id = @id", limitOffset: " LIMIT 1"),
                new { id },
                transaction,
                cancellationToken).ConfigureAwait(false) ?? throw AppError.NotFound("计费订单不存在");

            // 幂等：已结算直接成功返回。
            if (order.Status == BillingStatus.BillingStatusSettled)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (order.Status == BillingStatus.BillingStatusRefunded)
            {
                throw new InvalidOperationException("billing order already refunded");
            }

            bool tokenMode = order.BillingMode == "token" && !ZeroPricedTokenOrder(order);
            if (tokenMode)
            {
                BillingUsage usage = await ReadBillingUsageAsync(connection, transaction, id, cancellationToken)
                    .ConfigureAwait(false);
                observedUsage = usage;

                long reserved = order.ReservedAmountMicrocredits > 0
                    ? order.ReservedAmountMicrocredits
                    : order.AmountMicrocredits;

                long actual = TokenUsageAmount(order, usage);
                bool chargeCapped = order.ChargeLimitMicrocredits > 0 && actual > order.ChargeLimitMicrocredits;
                if (chargeCapped)
                {
                    actual = order.ChargeLimitMicrocredits;
                }

                observedActual = actual;
                observedActualAvailable = true;

                long refund = Math.Max(reserved - actual, 0);
                long supplement = Math.Max(actual - reserved, 0);

                int affected = await ExecuteAsync(
                    connection,
                    """
                    UPDATE credit_accounts SET
                        available_microcredits = available_microcredits + @delta,
                        reserved_microcredits = reserved_microcredits - @reserved,
                        version = version + 1,
                        updated_at = @now
                    WHERE user_id = @userId AND reserved_microcredits >= @reserved
                    """,
                    new
                    {
                        delta = refund - supplement,
                        reserved,
                        now = DateTime.UtcNow,
                        userId = order.UserID,
                    },
                    transaction,
                    cancellationToken).ConfigureAwait(false);

                if (affected != 1)
                {
                    throw new InvalidOperationException("reserved credit balance is inconsistent");
                }

                CreditAccount account = await ReadAccountAsync(connection, transaction, order.UserID, cancellationToken)
                    .ConfigureAwait(false);

                DateTime now = DateTime.UtcNow;
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE billing_orders SET
                        status = @settled, settled_at = @now, updated_at = @now,
                        actual_amount_microcredits = @actual, refunded_amount_microcredits = @refund,
                        input_tokens = @inputTokens, output_tokens = @outputTokens, cached_tokens = @cachedTokens,
                        usage_available = @usageAvailable
                        {providerSet}
                    WHERE id = @id
                    """.Replace("{providerSet}", providerRequestId.Length > 0 ? ", provider_request_id = @providerRequestId" : ""),
                    new
                    {
                        settled = BillingStatus.BillingStatusSettled,
                        now,
                        actual,
                        refund,
                        inputTokens = usage.InputTokens,
                        outputTokens = usage.OutputTokens,
                        cachedTokens = usage.CachedTokens,
                        usageAvailable = true,
                        providerRequestId,
                        id,
                    },
                    transaction,
                    cancellationToken).ConfigureAwait(false);

                string consumeNote = chargeCapped
                    ? "Token 实际用量超过 Agent 报价，已按本轮硬上限结算"
                    : supplement > 0
                        ? "Token 实际用量超过预授权，已补扣差额"
                        : "";

                await InsertLedgerAsync(connection, transaction, new CreditLedgerEntry
                {
                    ID = IdGenerator.NewId(),
                    UserID = order.UserID,
                    Type = CreditLedgerType.CreditLedgerConsume,
                    AmountMicrocredits = -actual,
                    AvailableDeltaMicrocredits = -supplement,
                    ReservedDeltaMicrocredits = -reserved,
                    AvailableAfterMicrocredits = account.AvailableMicrocredits,
                    ReservedAfterMicrocredits = account.ReservedMicrocredits,
                    BillingOrderID = order.ID,
                    Model = order.Model,
                    ChannelID = order.ChannelID,
                    Scene = order.Scene,
                    Note = consumeNote,
                    CreatedAt = now,
                }, cancellationToken).ConfigureAwait(false);

                if (refund > 0)
                {
                    await InsertLedgerAsync(connection, transaction, new CreditLedgerEntry
                    {
                        ID = IdGenerator.NewId(),
                        UserID = order.UserID,
                        Type = CreditLedgerType.CreditLedgerRefund,
                        AmountMicrocredits = refund,
                        AvailableDeltaMicrocredits = refund,
                        AvailableAfterMicrocredits = account.AvailableMicrocredits,
                        ReservedAfterMicrocredits = account.ReservedMicrocredits,
                        BillingOrderID = order.ID,
                        Model = order.Model,
                        ChannelID = order.ChannelID,
                        Scene = order.Scene,
                        Note = "Token 预授权差额退回",
                        CreatedAt = now,
                    }, cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            // 非 token（或零价 token）：整笔预留直接转为消费。
            int simpleAffected = await ExecuteAsync(
                connection,
                """
                UPDATE credit_accounts SET
                    reserved_microcredits = reserved_microcredits - @amount,
                    version = version + 1,
                    updated_at = @now
                WHERE user_id = @userId AND reserved_microcredits >= @amount
                """,
                new { amount = order.AmountMicrocredits, now = DateTime.UtcNow, userId = order.UserID },
                transaction,
                cancellationToken).ConfigureAwait(false);

            if (simpleAffected != 1)
            {
                throw new InvalidOperationException("reserved credit balance is inconsistent");
            }

            CreditAccount simpleAccount = await ReadAccountAsync(connection, transaction, order.UserID, cancellationToken)
                .ConfigureAwait(false);

            DateTime settledAt = DateTime.UtcNow;
            await ExecuteAsync(
                connection,
                """
                UPDATE billing_orders SET
                    status = @settled, actual_amount_microcredits = @amount,
                    settled_at = @now, updated_at = @now
                    {providerSet}
                WHERE id = @id
                """.Replace("{providerSet}", providerRequestId.Length > 0 ? ", provider_request_id = @providerRequestId" : ""),
                new
                {
                    settled = BillingStatus.BillingStatusSettled,
                    amount = order.AmountMicrocredits,
                    now = settledAt,
                    providerRequestId,
                    id,
                },
                transaction,
                cancellationToken).ConfigureAwait(false);

            await InsertLedgerAsync(connection, transaction, new CreditLedgerEntry
            {
                ID = IdGenerator.NewId(),
                UserID = order.UserID,
                Type = CreditLedgerType.CreditLedgerConsume,
                AmountMicrocredits = -order.AmountMicrocredits,
                ReservedDeltaMicrocredits = -order.AmountMicrocredits,
                AvailableAfterMicrocredits = simpleAccount.AvailableMicrocredits,
                ReservedAfterMicrocredits = simpleAccount.ReservedMicrocredits,
                BillingOrderID = order.ID,
                Model = order.Model,
                ChannelID = order.ChannelID,
                Scene = order.Scene,
                CreatedAt = settledAt,
            }, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            // usage 是上游已确认的事实；即使结算回滚，也要保留给用户和管理员核对。
            if (observedUsage is not null)
            {
                await PersistObservedUsageAsync(
                    connection, id, observedUsage, observedActual, observedActualAvailable,
                    providerRequestId, cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// 已退款订单的补偿结算：确认上游实际成功后，从可用积分直接扣费。
    /// 对应 Go: <c>RestoreRefundedBillingOrder</c>。
    /// </summary>
    /// <remarks>
    /// 原预留已退回可用余额，所以这里直接从 available 扣。条件更新
    /// <c>refunded → settled</c> 保证并发/重复的人工恢复请求幂等。
    /// </remarks>
    public async Task RestoreRefundedBillingOrderAsync(
        string id, string providerRequestId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        BillingOrder order = await FirstOrDefaultAsync<BillingOrder>(
            connection,
            SqlBuilder.Select<BillingOrder>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            transaction,
            cancellationToken).ConfigureAwait(false) ?? throw AppError.NotFound("计费订单不存在");

        if (order.Status == BillingStatus.BillingStatusSettled)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (order.Status != BillingStatus.BillingStatusRefunded)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new BillingStateConflictException();
        }

        long actual = order.AmountMicrocredits;
        BillingUsage? usage = null;
        if (order.BillingMode == "token")
        {
            if (ZeroPricedTokenOrder(order))
            {
                actual = 0;
            }
            else
            {
                usage = await ReadBillingUsageAsync(connection, transaction, id, cancellationToken)
                    .ConfigureAwait(false);
                actual = TokenUsageAmount(order, usage);
            }
        }

        if (actual < 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("invalid restored billing amount");
        }

        int affected = await ExecuteAsync(
            connection,
            """
            UPDATE credit_accounts SET
                available_microcredits = available_microcredits - @actual,
                version = version + 1,
                updated_at = @now
            WHERE user_id = @userId
            """,
            new { actual, now = DateTime.UtcNow, userId = order.UserID },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("credit account does not exist");
        }

        CreditAccount account = await ReadAccountAsync(connection, transaction, order.UserID, cancellationToken)
            .ConfigureAwait(false);

        DateTime now = DateTime.UtcNow;
        List<string> sets =
        [
            "status = @settled",
            "actual_amount_microcredits = @actual",
            "refunded_amount_microcredits = @refunded",
            "refunded_at = NULL",
            "settled_at = @now",
            "error = ''",
            "updated_at = @now",
        ];
        if (providerRequestId.Length > 0)
        {
            sets.Add("provider_request_id = @providerRequestId");
        }
        if (usage is not null)
        {
            sets.Add("input_tokens = @inputTokens");
            sets.Add("output_tokens = @outputTokens");
            sets.Add("cached_tokens = @cachedTokens");
            sets.Add("usage_available = @usageAvailable");
        }

        int orderAffected = await ExecuteAsync(
            connection,
            $"""
            UPDATE billing_orders SET {string.Join(", ", sets)}
            WHERE id = @id AND status = @refunded
            """,
            new
            {
                settled = BillingStatus.BillingStatusSettled,
                actual,
                refunded = Math.Max(order.AmountMicrocredits - actual, 0),
                now,
                providerRequestId,
                inputTokens = usage?.InputTokens ?? 0,
                outputTokens = usage?.OutputTokens ?? 0,
                cachedTokens = usage?.CachedTokens ?? 0,
                usageAvailable = true,
                id,
                refundedStatus = BillingStatus.BillingStatusRefunded,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (orderAffected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new BillingStateConflictException();
        }

        await InsertLedgerAsync(connection, transaction, new CreditLedgerEntry
        {
            ID = IdGenerator.NewId(),
            UserID = order.UserID,
            Type = CreditLedgerType.CreditLedgerConsume,
            AmountMicrocredits = -actual,
            AvailableDeltaMicrocredits = -actual,
            AvailableAfterMicrocredits = account.AvailableMicrocredits,
            ReservedAfterMicrocredits = account.ReservedMicrocredits,
            BillingOrderID = order.ID,
            Model = order.Model,
            ChannelID = order.ChannelID,
            Scene = order.Scene,
            Note = "人工查询确认上游成功，退款订单重新扣费",
            CreatedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 只读巡检：统计超过 age 未更新且仍处于未闭合状态（reserved/running/uncertain）的订单。
    /// 对应 Go: <c>StaleBillingReviewStats</c>。
    /// </summary>
    public async Task<BillingReviewStats> StaleBillingReviewStatsAsync(
        DateTime now, TimeSpan age, CancellationToken cancellationToken = default)
    {
        DateTime cutoff = now - age;
        string[] openStatuses =
        [
            BillingStatus.BillingStatusReserved,
            BillingStatus.BillingStatusRunning,
            BillingStatus.BillingStatusUncertain,
        ];
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<(string Status, long Count)> rows = await QueryAsync<(string Status, long Count)>(
            connection,
            """
            SELECT status, COUNT(*) FROM billing_orders
            WHERE status IN @statuses AND updated_at < @cutoff
            GROUP BY status
            """,
            new { statuses = openStatuses, cutoff },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        foreach ((string status, long count) in rows)
        {
            counts[status] = count;
        }

        DateTime? oldest = (await QueryAsync<DateTime?>(
            connection,
            """
            SELECT created_at FROM billing_orders
            WHERE status IN @statuses AND updated_at < @cutoff
            ORDER BY created_at ASC LIMIT 1
            """,
            new { statuses = openStatuses, cutoff },
            cancellationToken: cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(value => value.HasValue);

        counts.TryGetValue(BillingStatus.BillingStatusReserved, out long reserved);
        counts.TryGetValue(BillingStatus.BillingStatusRunning, out long running);
        counts.TryGetValue(BillingStatus.BillingStatusUncertain, out long uncertain);
        return new BillingReviewStats(reserved, running, uncertain, oldest);
    }

    /// <summary>
    /// 退款：把预留积分退回可用余额。对应 Go: <c>RefundBillingOrder</c>。
    /// 已结算订单不允许直接退款（需人工处理）。
    /// </summary>
    public async Task RefundBillingOrderAsync(
        string id, string errorText, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        BillingOrder order = await FirstOrDefaultAsync<BillingOrder>(
            connection,
            SqlBuilder.Select<BillingOrder>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            transaction,
            cancellationToken).ConfigureAwait(false) ?? throw AppError.NotFound("计费订单不存在");

        if (order.Status == BillingStatus.BillingStatusRefunded)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (order.Status == BillingStatus.BillingStatusSettled)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("settled billing order requires a manual refund");
        }

        int affected = await ExecuteAsync(
            connection,
            """
            UPDATE credit_accounts SET
                available_microcredits = available_microcredits + @amount,
                reserved_microcredits = reserved_microcredits - @amount,
                version = version + 1,
                updated_at = @now
            WHERE user_id = @userId AND reserved_microcredits >= @amount
            """,
            new { amount = order.AmountMicrocredits, now = DateTime.UtcNow, userId = order.UserID },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("reserved credit balance is inconsistent");
        }

        CreditAccount account = await ReadAccountAsync(connection, transaction, order.UserID, cancellationToken)
            .ConfigureAwait(false);

        DateTime now = DateTime.UtcNow;
        await ExecuteAsync(
            connection,
            """
            UPDATE billing_orders SET
                status = @refunded, error = @errorText,
                refunded_amount_microcredits = @amount, refunded_at = @now, updated_at = @now
            WHERE id = @id
            """,
            new
            {
                refunded = BillingStatus.BillingStatusRefunded,
                errorText,
                amount = order.AmountMicrocredits,
                now,
                id,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await InsertLedgerAsync(connection, transaction, new CreditLedgerEntry
        {
            ID = IdGenerator.NewId(),
            UserID = order.UserID,
            Type = CreditLedgerType.CreditLedgerRefund,
            AmountMicrocredits = order.AmountMicrocredits,
            AvailableDeltaMicrocredits = order.AmountMicrocredits,
            ReservedDeltaMicrocredits = -order.AmountMicrocredits,
            AvailableAfterMicrocredits = account.AvailableMicrocredits,
            ReservedAfterMicrocredits = account.ReservedMicrocredits,
            BillingOrderID = order.ID,
            Model = order.Model,
            ChannelID = order.ChannelID,
            Scene = order.Scene,
            Note = errorText,
            CreatedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 查询

    /// <summary>
    /// 订单对应的上游用量。取同订单最新一条成功且带 usage 的调用日志。
    /// 对应 Go: <c>billingUsage</c>。
    /// </summary>
    public async Task<BillingUsage> BillingUsageAsync(
        string orderId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadBillingUsageAsync(connection, null, orderId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 任务是否有成功的可计费调用。对应 Go: <c>TaskHasSuccessfulBillableCall</c>。
    /// </summary>
    public async Task<bool> TaskHasSuccessfulBillableCallAsync(
        string taskId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long count = await ScalarAsync<long>(
            connection,
            """
            SELECT COUNT(*) FROM api_call_logs
            WHERE task_id = @taskId AND billable = @billable AND status = @status
            """,
            new
            {
                taskId,
                billable = true,
                status = ApiCallStatus.ApiCallStatusSucceeded,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return count > 0;
    }

    /// <summary>记录人工核对结果。对应 Go: <c>RecordBillingResolution</c>。</summary>
    public async Task RecordBillingResolutionAsync(
        string id, string actorUserId, string note, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE billing_orders SET
                resolved_by = @actorUserId, resolution_note = @note, updated_at = @now
            WHERE id = @id
            """,
            new { actorUserId, note, now = DateTime.UtcNow, id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>回填上游请求 ID。对应 Go: <c>UpdateBillingProviderRequestID</c>。</summary>
    public async Task UpdateBillingProviderRequestIDAsync(
        string id, string providerRequestId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE billing_orders SET
                provider_request_id = @providerRequestId, updated_at = @now
            WHERE id = @id AND provider_request_id = ''
            """,
            new { providerRequestId, now = DateTime.UtcNow, id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 内部

    private async Task<BillingUsage> ReadBillingUsageAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string orderId,
        CancellationToken cancellationToken)
    {
        // 异步视频的真实 usage 由非计费的轮询请求返回；订单本身已限定归属，
        // 所以结算读同订单最新的成功 usage，而不是只看创建请求。
        ApiCallLog? log = await FirstOrDefaultAsync<ApiCallLog>(
            connection,
            """
            SELECT input_tokens AS "InputTokens", output_tokens AS "OutputTokens", cached_tokens AS "CachedTokens"
            FROM api_call_logs
            WHERE billing_order_id = @orderId AND status = @status AND usage_available = @usageAvailable
            ORDER BY created_at DESC
            LIMIT 1
            """,
            new
            {
                orderId,
                status = ApiCallStatus.ApiCallStatusSucceeded,
                usageAvailable = true,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (log is null)
        {
            throw new BillingUsageUnavailableException();
        }

        return new BillingUsage
        {
            InputTokens = log.InputTokens,
            OutputTokens = log.OutputTokens,
            CachedTokens = log.CachedTokens,
        };
    }

    private async Task<CreditAccount> ReadAccountAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string userId,
        CancellationToken cancellationToken) =>
        await FirstOrDefaultAsync<CreditAccount>(
            connection,
            SqlBuilder.Select<CreditAccount>("user_id = @userId", limitOffset: " LIMIT 1"),
            new { userId },
            transaction,
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("credit account does not exist");

    private async Task InsertLedgerAsync(
        DbConnection connection,
        DbTransaction transaction,
        CreditLedgerEntry entry,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            connection, SqlBuilder.Insert<CreditLedgerEntry>(), entry, transaction, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// 结算回滚后保留已观测到的上游用量。
    /// 对应 Go 的 <c>if err != nil &amp;&amp; observedUsage != nil</c> 补偿分支。
    /// </summary>
    private async Task PersistObservedUsageAsync(
        DbConnection connection,
        string id,
        BillingUsage usage,
        long actual,
        bool actualAvailable,
        string providerRequestId,
        CancellationToken cancellationToken)
    {
        List<string> sets =
        [
            "input_tokens = @inputTokens",
            "output_tokens = @outputTokens",
            "cached_tokens = @cachedTokens",
            "usage_available = @usageAvailable",
            "updated_at = @now",
        ];
        if (actualAvailable)
        {
            sets.Add("actual_amount_microcredits = @actual");
        }
        if (providerRequestId.Length > 0)
        {
            sets.Add("provider_request_id = @providerRequestId");
        }

        await ExecuteAsync(
            connection,
            $"""
            UPDATE billing_orders SET {string.Join(", ", sets)}
            WHERE id = @id AND status NOT IN @closedStatuses
            """,
            new
            {
                inputTokens = usage.InputTokens,
                outputTokens = usage.OutputTokens,
                cachedTokens = usage.CachedTokens,
                usageAvailable = true,
                now = DateTime.UtcNow,
                actual,
                providerRequestId,
                id,
                closedStatuses = new[] { BillingStatus.BillingStatusSettled, BillingStatus.BillingStatusRefunded },
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>零价 token 订单（三个单价全为 0）。对应 Go: <c>zeroPricedTokenOrder</c>。</summary>
    private static bool ZeroPricedTokenOrder(BillingOrder order) =>
        order.BillingMode == "token"
        && order.InputTokenPriceMicrocredits == 0
        && order.OutputTokenPriceMicrocredits == 0
        && order.CachedTokenPriceMicrocredits == 0;

    /// <summary>
    /// token 用量金额（含倍率，向上取整）。对应 Go: <c>tokenUsageAmount</c>。
    /// </summary>
    /// <remarks>
    /// 全程整数运算并逐步做溢出检查，避免浮点误差造成少扣或多扣。
    /// 倍率分母固定 10_000_000_000（basis points 再乘 1e6）。
    /// </remarks>
    private static long TokenUsageAmount(BillingOrder order, BillingUsage usage)
    {
        // 视频任务的输出 token 是唯一计价依据；缺失说明上游没回传用量。
        if (order.Capability == "video" && usage.OutputTokens <= 0)
        {
            throw new BillingUsageUnavailableException();
        }

        long input = Math.Max(usage.InputTokens - usage.CachedTokens, 0);
        long inputAmount = SafeTokenUsageProduct(input, order.InputTokenPriceMicrocredits);
        long outputAmount = SafeTokenUsageProduct(usage.OutputTokens, order.OutputTokenPriceMicrocredits);
        long cachedAmount = SafeTokenUsageProduct(usage.CachedTokens, order.CachedTokenPriceMicrocredits);

        // 逐段检查加法与乘法溢出，与 Go 的显式边界判定一致（不依赖 checked 的 OverflowException）。
        long baseAmount = SafeAdd(inputAmount, outputAmount);
        baseAmount = SafeAdd(baseAmount, cachedAmount);

        if (order.MultiplierBasisPoints <= 0
            || baseAmount > (long.MaxValue - 9_999_999_999) / order.MultiplierBasisPoints)
        {
            throw new InvalidOperationException("invalid token usage amount");
        }

        return (baseAmount * order.MultiplierBasisPoints + 9_999_999_999) / 10_000_000_000;
    }

    private static long SafeAdd(long left, long right)
    {
        if (left > long.MaxValue - right)
        {
            throw new InvalidOperationException("invalid token usage amount");
        }

        return left + right;
    }

    private static long SafeTokenUsageProduct(long tokens, long price)
    {
        if (tokens < 0 || price < 0 || (tokens > 0 && price > (long.MaxValue / tokens)))
        {
            throw new InvalidOperationException("invalid token usage amount");
        }

        return tokens * price;
    }
}

/// <summary>订单对应的上游用量。对应 Go: <c>repository.BillingUsage</c>。</summary>
public sealed class BillingUsage
{
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CachedTokens { get; init; }
}

/// <summary>上游用量不可用。对应 Go: <c>ErrBillingUsageUnavailable</c>。</summary>
public sealed class BillingUsageUnavailableException : Exception
{
    public BillingUsageUnavailableException() : base("billing usage unavailable")
    {
    }
}

/// <summary>订单状态与期望不符。对应 Go: <c>ErrBillingStateConflict</c>。</summary>
public sealed class BillingStateConflictException : Exception
{
    public BillingStateConflictException() : base("billing state conflict")
    {
    }
}
