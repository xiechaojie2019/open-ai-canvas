#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 充值商品、支付渠道配置、支付订单与支付通知。
/// 对应 Go: <c>repository/payment.go</c>。
/// </summary>
/// <remarks>
/// <see cref="CompletePaymentOrderAsync"/> 是资金路径：订单状态、账户余额、账本分录
/// 必须原子一致，靠「行锁 + 账本唯一引用键 + 条件更新」三重保护。
/// </remarks>
public sealed partial class Repository
{
    /// <summary>
    /// 可用积分上限（2^53-1，JS 安全整数上限）。
    /// 对应 Go: <c>maxPaymentCreditBalance</c>。超过即拒绝入账，避免前端精度丢失。
    /// </summary>
    private const long MaxPaymentCreditBalance = 9_007_199_254_740_991;

    // ---------------------------------------------------------------- 充值商品

    /// <summary>充值商品列表。对应 Go: <c>TopupProducts</c>。</summary>
    public async Task<IReadOnlyList<TopupProduct>> TopupProductsAsync(
        bool includeDisabled, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<TopupProduct>(
            connection,
            SqlBuilder.Select<TopupProduct>(
                includeDisabled ? null : "enabled = @enabled",
                orderBy: "sort_order ASC, amount_fen ASC, created_at ASC"),
            new { enabled = true },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 查充值商品。对应 Go: <c>TopupProduct</c>。</summary>
    public async Task<TopupProduct?> TopupProductAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<TopupProduct>(
            connection,
            SqlBuilder.Select<TopupProduct>("id = @id", limitOffset: " LIMIT 1"),
            new { id = id.Trim() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建充值商品。对应 Go: <c>CreateTopupProduct</c>。</summary>
    public async Task CreateTopupProductAsync(TopupProduct product, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlBuilder.Insert<TopupProduct>(), product, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>更新充值商品（不含创建者/创建时间）。对应 Go: <c>UpdateTopupProduct</c>。</summary>
    public async Task UpdateTopupProductAsync(TopupProduct product, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE topup_products SET
                name = @Name, description = @Description, amount_fen = @AmountFen,
                credits_microcredits = @CreditsMicrocredits, enabled = @Enabled,
                sort_order = @SortOrder, updated_by = @UpdatedBy, updated_at = @now
            WHERE id = @ID
            """,
            new
            {
                product.Name,
                product.Description,
                product.AmountFen,
                product.CreditsMicrocredits,
                product.Enabled,
                product.SortOrder,
                product.UpdatedBy,
                product.ID,
                now = DateTime.UtcNow,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 渠道配置

    /// <summary>
    /// 新建渠道配置，版本号自动递增。对应 Go: <c>CreatePaymentProviderConfig</c>。
    /// </summary>
    public async Task CreatePaymentProviderConfigAsync(
        PaymentProviderConfig config, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long latest = await ScalarAsync<long>(
            connection,
            "SELECT COALESCE(MAX(version), 0) FROM payment_provider_configs WHERE provider_id = @providerId",
            new { providerId = config.ProviderID },
            transaction,
            cancellationToken).ConfigureAwait(false);

        config.Version = latest + 1;
        await ExecuteAsync(connection, SqlBuilder.Insert<PaymentProviderConfig>(), config, transaction, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>最新版本的渠道配置。对应 Go: <c>LatestPaymentProviderConfig</c>。</summary>
    public async Task<PaymentProviderConfig?> LatestPaymentProviderConfigAsync(
        string providerId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<PaymentProviderConfig>(
            connection,
            SqlBuilder.Select<PaymentProviderConfig>("provider_id = @providerId", orderBy: "version DESC", limitOffset: " LIMIT 1"),
            new { providerId = providerId.Trim() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 查渠道配置。对应 Go: <c>PaymentProviderConfig</c>。</summary>
    public async Task<PaymentProviderConfig?> PaymentProviderConfigAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<PaymentProviderConfig>(
            connection,
            SqlBuilder.Select<PaymentProviderConfig>("id = @id", limitOffset: " LIMIT 1"),
            new { id = id.Trim() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 支付订单

    /// <summary>
    /// 用用户级幂等键占位下单。返回 <c>Created=false</c> 表示重试命中了已有订单。
    /// 对应 Go: <c>CreatePaymentOrder</c>。
    /// </summary>
    public async Task<(PaymentOrder Order, bool Created)> CreatePaymentOrderAsync(
        PaymentOrder order, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        int affected = await ExecuteAsync(
            connection,
            SqlBuilder.Insert<PaymentOrder>() + Dialect.OnConflictDoNothing("\"user_id\", \"idempotency_key\""),
            order,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (affected == 1)
        {
            return (order, true);
        }

        PaymentOrder? existing = await FirstOrDefaultAsync<PaymentOrder>(
            connection,
            SqlBuilder.Select<PaymentOrder>("user_id = @userId AND idempotency_key = @idempotencyKey", limitOffset: " LIMIT 1"),
            new { userId = order.UserID, idempotencyKey = order.IdempotencyKey },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return existing is null
            ? throw new PaymentStateConflictException()
            : (existing, false);
    }

    /// <summary>写入收银台并置为 pending。对应 Go: <c>SetPaymentOrderCheckout</c>。</summary>
    public async Task SetPaymentOrderCheckoutAsync(
        string id, string checkoutMode, string checkoutValue, DateTime checkoutExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await ExecuteAsync(
            connection,
            """
            UPDATE payment_orders SET
                status = @pending, checkout_mode = @checkoutMode, checkout_value = @checkoutValue,
                checkout_expires_at = @checkoutExpiresAt, last_error = '', updated_at = @now
            WHERE id = @id AND status IN @openStatuses
            """,
            new
            {
                pending = PaymentOrderStatus.PaymentOrderPending,
                checkoutMode,
                checkoutValue,
                checkoutExpiresAt,
                now = DateTime.UtcNow,
                id,
                openStatuses = new[]
                {
                    PaymentOrderStatus.PaymentOrderCreated,
                    PaymentOrderStatus.PaymentOrderCreateFailed,
                    PaymentOrderStatus.PaymentOrderPending,
                },
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (affected != 1)
        {
            throw new PaymentStateConflictException();
        }
    }

    /// <summary>标记下单失败。对应 Go: <c>SetPaymentOrderCreateFailure</c>。</summary>
    public async Task SetPaymentOrderCreateFailureAsync(
        string id, string message, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE payment_orders SET status = @failed, last_error = @message, updated_at = @now
            WHERE id = @id AND status = @created
            """,
            new
            {
                failed = PaymentOrderStatus.PaymentOrderCreateFailed,
                message,
                now = DateTime.UtcNow,
                id,
                created = PaymentOrderStatus.PaymentOrderCreated,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按用户与 ID 查订单。对应 Go: <c>PaymentOrderForUser</c>。</summary>
    public async Task<PaymentOrder?> PaymentOrderForUserAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<PaymentOrder>(
            connection,
            SqlBuilder.Select<PaymentOrder>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id = id.Trim(), userId = userId.Trim() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按幂等键查订单。对应 Go: <c>PaymentOrderByIdempotency</c>。</summary>
    public async Task<PaymentOrder?> PaymentOrderByIdempotencyAsync(
        string userId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<PaymentOrder>(
            connection,
            SqlBuilder.Select<PaymentOrder>("user_id = @userId AND idempotency_key = @idempotencyKey", limitOffset: " LIMIT 1"),
            new { userId = userId.Trim(), idempotencyKey = idempotencyKey.Trim() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 查订单。对应 Go: <c>PaymentOrder</c>。</summary>
    public async Task<PaymentOrder?> PaymentOrderAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<PaymentOrder>(
            connection,
            SqlBuilder.Select<PaymentOrder>("id = @id", limitOffset: " LIMIT 1"),
            new { id = id.Trim() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按渠道 + 商户单号查订单。对应 Go: <c>PaymentOrderByMerchant</c>。</summary>
    public async Task<PaymentOrder?> PaymentOrderByMerchantAsync(
        string providerId, string merchantOrderNo, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<PaymentOrder>(
            connection,
            SqlBuilder.Select<PaymentOrder>(
                "provider_id = @providerId AND merchant_order_no = @merchantOrderNo", limitOffset: " LIMIT 1"),
            new { providerId, merchantOrderNo },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>用户当前未支付订单数。对应 Go: <c>ActivePaymentOrderCount</c>。</summary>
    public async Task<long> ActivePaymentOrderCountAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM payment_orders WHERE user_id = @userId AND status IN @statuses",
            new
            {
                userId,
                statuses = new[]
                {
                    PaymentOrderStatus.PaymentOrderCreated,
                    PaymentOrderStatus.PaymentOrderPending,
                    PaymentOrderStatus.PaymentOrderCreateFailed,
                },
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>记录一次查单结果。对应 Go: <c>RecordPaymentQuery</c>。</summary>
    public async Task RecordPaymentQueryAsync(
        string id, string providerStatus, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        await ExecuteAsync(
            connection,
            """
            UPDATE payment_orders SET
                provider_status = @providerStatus, last_queried_at = @now, updated_at = @now
            WHERE id = @id
            """,
            new { providerStatus, now, id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>标记订单已关闭。对应 Go: <c>MarkPaymentOrderClosed</c>。</summary>
    public async Task MarkPaymentOrderClosedAsync(
        string id, string providerStatus, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        int affected = await ExecuteAsync(
            connection,
            """
            UPDATE payment_orders SET
                status = @closed, provider_status = @providerStatus, closed_at = @now, updated_at = @now
            WHERE id = @id AND status IN @openStatuses
            """,
            new
            {
                closed = PaymentOrderStatus.PaymentOrderClosed,
                providerStatus,
                now,
                id,
                openStatuses = new[]
                {
                    PaymentOrderStatus.PaymentOrderCreated,
                    PaymentOrderStatus.PaymentOrderPending,
                    PaymentOrderStatus.PaymentOrderCreateFailed,
                },
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (affected != 1)
        {
            throw new PaymentStateConflictException();
        }
    }

    /// <summary>关闭失败时回滚 closing 状态。对应 Go: <c>RestoreClosingPaymentOrder</c>。</summary>
    public async Task RestoreClosingPaymentOrderAsync(
        string id, string message, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE payment_orders SET status = @pending, last_error = @message, updated_at = @now
            WHERE id = @id AND status = @closing
            """,
            new
            {
                pending = PaymentOrderStatus.PaymentOrderPending,
                message,
                now = DateTime.UtcNow,
                id,
                closing = PaymentOrderStatus.PaymentOrderClosing,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 管理端订单列表。关键词同时匹配商户单号、渠道交易号、用户 ID 与用户名/昵称/邮箱。
    /// 对应 Go: <c>AdminPaymentOrders</c>。
    /// </summary>
    public async Task<(IReadOnlyList<PaymentOrder> Orders, long Total)> AdminPaymentOrdersAsync(
        string status, string keyword, int limit, int offset, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        List<string> conditions = [];
        DynamicParameters parameters = new();

        string normalizedStatus = status.Trim();
        if (normalizedStatus.Length > 0 && normalizedStatus != "all")
        {
            conditions.Add("status = @status");
            parameters.Add("status", normalizedStatus);
        }

        string normalizedKeyword = keyword.Trim();
        if (normalizedKeyword.Length > 0)
        {
            conditions.Add("""
                (merchant_order_no LIKE @like
                 OR provider_trade_no LIKE @like
                 OR user_id = @keyword
                 OR user_id IN (
                     SELECT id FROM users
                     WHERE LOWER(username) LIKE @lowerLike
                        OR LOWER(display_name) LIKE @lowerLike
                        OR LOWER(email) LIKE @lowerLike))
                """);
            parameters.Add("like", "%" + normalizedKeyword + "%");
            parameters.Add("keyword", normalizedKeyword);
            parameters.Add("lowerLike", "%" + normalizedKeyword.ToLowerInvariant() + "%");
        }

        string where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);

        long total = await ScalarAsync<long>(
            connection, $"SELECT COUNT(*) FROM payment_orders{where}", parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        IReadOnlyList<PaymentOrder> orders = await QueryAsync<PaymentOrder>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<PaymentOrder>()} FROM payment_orders{where}
            ORDER BY created_at DESC
            {Dialect.LimitOffset(limit, offset)}
            """,
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (orders, total);
    }

    // ---------------------------------------------------------------- 支付通知

    /// <summary>
    /// 保存已验签的通知（按渠道 + 事件 ID 去重）。返回是否首次入库。
    /// 对应 Go: <c>SaveVerifiedPaymentNotification</c>。
    /// </summary>
    public async Task<bool> SaveVerifiedPaymentNotificationAsync(
        PaymentNotification notification, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await ExecuteAsync(
            connection,
            SqlBuilder.Insert<PaymentNotification>()
                + Dialect.OnConflictDoNothing("\"provider_id\", \"provider_event_id\""),
            notification,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>待处理的通知。对应 Go: <c>PendingPaymentNotifications</c>。</summary>
    public async Task<IReadOnlyList<PaymentNotification>> PendingPaymentNotificationsAsync(
        int limit, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<PaymentNotification>(
            connection,
            SqlBuilder.Select<PaymentNotification>(
                "status = @pending AND next_attempt_at <= @now",
                orderBy: "created_at ASC",
                limitOffset: Dialect.LimitOffset(limit, 0)),
            new { pending = PaymentNotificationStatus.PaymentNotificationPending, now = DateTime.UtcNow },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>标记通知处理完成。对应 Go: <c>CompletePaymentNotification</c>。</summary>
    public async Task CompletePaymentNotificationAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        await ExecuteAsync(
            connection,
            """
            UPDATE payment_notifications SET
                status = @processed, processed_at = @now, updated_at = @now
            WHERE id = @id
            """,
            new { processed = PaymentNotificationStatus.PaymentNotificationProcessed, now, id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>通知处理失败，安排重试。对应 Go: <c>RetryPaymentNotification</c>。</summary>
    public async Task RetryPaymentNotificationAsync(
        string id, string message, DateTime next, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE payment_notifications SET
                status = @failed, attempts = attempts + 1,
                last_error = @message, next_attempt_at = @next, updated_at = @now
            WHERE id = @id
            """,
            new
            {
                failed = PaymentNotificationStatus.PaymentNotificationFailed,
                message,
                next,
                now = DateTime.UtcNow,
                id,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 入账

    /// <summary>
    /// 原子记录支付成功并发放积分。对应 Go: <c>CompletePaymentOrder</c>。
    /// </summary>
    /// <remarks>
    /// 三重保护：行锁订单 → 账本 <c>reference_key</c> 唯一约束（回调/查单竞态的最终防线）
    /// → 条件更新 <c>status &lt;&gt; credited</c>。任一处不满足都回滚，绝不重复发放。
    /// </remarks>
    public async Task<(PaymentOrder Order, bool Granted)> CompletePaymentOrderAsync(
        string providerId, string merchantOrderNo, PaymentEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        PaymentOrder order = await FirstOrDefaultAsync<PaymentOrder>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<PaymentOrder>()} FROM payment_orders
            WHERE provider_id = @providerId AND merchant_order_no = @merchantOrderNo
            {Dialect.ForUpdate()}
            """,
            new { providerId, merchantOrderNo },
            transaction,
            cancellationToken).ConfigureAwait(false) ?? throw AppError.NotFound("支付订单不存在");

        // 金额、币种、交易号必须与下单快照一致，否则拒绝入账。
        if (order.AmountFen != evidence.AmountFen
            || order.Currency != evidence.Currency
            || string.IsNullOrWhiteSpace(evidence.ProviderTradeNo))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new PaymentEvidenceMismatchException();
        }

        if (order.AmountFen <= 0 || order.CreditsMicrocredits <= 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new PaymentStateConflictException();
        }

        // 已入账：交易号一致则幂等返回。
        if (order.Status == PaymentOrderStatus.PaymentOrderCredited)
        {
            if (string.IsNullOrEmpty(order.ProviderTradeNo)
                || order.ProviderTradeNo.Trim() != evidence.ProviderTradeNo.Trim())
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new PaymentEvidenceMismatchException();
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (order, false);
        }

        // 同一渠道交易号不得被两个订单使用。
        long duplicate = await ScalarAsync<long>(
            connection,
            """
            SELECT COUNT(*) FROM payment_orders
            WHERE provider_id = @providerId AND provider_trade_no = @tradeNo AND id <> @id
            """,
            new { providerId, tradeNo = evidence.ProviderTradeNo, id = order.ID },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (duplicate > 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new PaymentTradeNoConflictException();
        }

        string referenceKey = "payment:" + providerId + ":" + merchantOrderNo;
        CreditLedgerEntry entry = new()
        {
            ID = IdGenerator.NewId(),
            UserID = order.UserID,
            Type = CreditLedgerType.CreditLedgerPaymentTopup,
            AmountMicrocredits = order.CreditsMicrocredits,
            PaymentOrderID = order.ID,
            ReferenceKey = referenceKey,
            Note = order.ProductName + " · 在线支付充值",
            CreatedAt = DateTime.UtcNow,
        };

        int ledgerCreated = await ExecuteAsync(
            connection,
            SqlBuilder.Insert<CreditLedgerEntry>() + Dialect.OnConflictDoNothing("\"reference_key\""),
            entry,
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (ledgerCreated == 0)
        {
            // 已入账订单在上面处理过。走到这里说明「账本有记录但订单非终态」，
            // 属于状态不一致，当作成功会掩盖问题。
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new PaymentStateConflictException();
        }

        await ExecuteAsync(
            connection,
            SqlBuilder.Insert<CreditAccount>() + Dialect.OnConflictDoNothing(string.Empty),
            new CreditAccount
            {
                UserID = order.UserID,
                AvailableMicrocredits = 0,
                ReservedMicrocredits = 0,
                Version = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        DateTime now = DateTime.UtcNow;
        int accountAffected = await ExecuteAsync(
            connection,
            """
            UPDATE credit_accounts SET
                available_microcredits = available_microcredits + @credits,
                version = version + 1, updated_at = @now
            WHERE user_id = @userId AND available_microcredits <= @ceiling
            """,
            new
            {
                credits = order.CreditsMicrocredits,
                now,
                userId = order.UserID,
                ceiling = MaxPaymentCreditBalance - order.CreditsMicrocredits,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (accountAffected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new PaymentCreditOverflowException();
        }

        CreditAccount account = await FirstOrDefaultAsync<CreditAccount>(
            connection,
            SqlBuilder.Select<CreditAccount>("user_id = @userId", limitOffset: " LIMIT 1"),
            new { userId = order.UserID },
            transaction,
            cancellationToken).ConfigureAwait(false) ?? throw new PaymentStateConflictException();

        await ExecuteAsync(
            connection,
            """
            UPDATE credit_ledger_entries SET
                available_delta_microcredits = @credits,
                available_after_microcredits = @availableAfter,
                reserved_after_microcredits = @reservedAfter
            WHERE id = @id
            """,
            new
            {
                credits = order.CreditsMicrocredits,
                availableAfter = account.AvailableMicrocredits,
                reservedAfter = account.ReservedMicrocredits,
                id = entry.ID,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        DateTime paidAt = evidence.PaidAt == default ? now : evidence.PaidAt;
        int orderAffected = await ExecuteAsync(
            connection,
            """
            UPDATE payment_orders SET
                status = @credited, provider_trade_no = @tradeNo,
                provider_status = @providerStatus, provider_paid_at = @paidAt,
                credited_at = @now, last_error = '', updated_at = @now
            WHERE id = @id AND status <> @credited
            """,
            new
            {
                credited = PaymentOrderStatus.PaymentOrderCredited,
                tradeNo = evidence.ProviderTradeNo,
                providerStatus = evidence.ProviderStatus,
                paidAt,
                now,
                id = order.ID,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (orderAffected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new PaymentStateConflictException();
        }

        PaymentOrder reloaded = await FirstOrDefaultAsync<PaymentOrder>(
            connection,
            SqlBuilder.Select<PaymentOrder>("id = @id", limitOffset: " LIMIT 1"),
            new { id = order.ID },
            transaction,
            cancellationToken).ConfigureAwait(false) ?? order;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (reloaded, true);
    }
}

/// <summary>入账证据。对应 Go: <c>repository.PaymentEvidence</c>。</summary>
public sealed class PaymentEvidence
{
    public string ProviderTradeNo { get; init; } = "";
    public string ProviderStatus { get; init; } = "";
    public long AmountFen { get; init; }
    public string Currency { get; init; } = "";
    public DateTime PaidAt { get; init; }
}

/// <summary>订单状态与期望不符。对应 Go: <c>ErrPaymentOrderStateConflict</c>。</summary>
public sealed class PaymentStateConflictException : Exception
{
    public PaymentStateConflictException() : base("payment order state conflict")
    {
    }
}

/// <summary>入账证据与订单快照不符。对应 Go: <c>ErrPaymentEvidenceMismatch</c>。</summary>
public sealed class PaymentEvidenceMismatchException : Exception
{
    public PaymentEvidenceMismatchException() : base("payment evidence does not match order")
    {
    }
}

/// <summary>渠道交易号已被其他订单使用。对应 Go: <c>ErrPaymentTradeNoConflict</c>。</summary>
public sealed class PaymentTradeNoConflictException : Exception
{
    public PaymentTradeNoConflictException() : base("payment provider trade number is already used")
    {
    }
}

/// <summary>入账会导致余额溢出。对应 Go: <c>ErrPaymentCreditOverflow</c>。</summary>
public sealed class PaymentCreditOverflowException : Exception
{
    public PaymentCreditOverflowException() : base("payment credit balance would overflow")
    {
    }
}
