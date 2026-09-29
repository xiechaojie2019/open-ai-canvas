#nullable enable
using System.Data.Common;
using System.Globalization;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>切线路仓储错误标记（由应用层转换为用户可见错误）。</summary>
public static class TaskRouteErrors
{
    public const string TaskStateConflict = "task_state_conflict";
    public const string InsufficientCredits = "insufficient_credits";
    public const string BillingChargeLimit = "billing_charge_limit";
    public const string BillingStateConflict = "billing_state_conflict";
}

/// <summary>切线路后写回订单的成本快照。对应 Go: <c>model.BillingCostSnapshot</c>。</summary>
public sealed record TaskRouteCostSnapshot(
    bool CostConfigured,
    long CostUnitPriceMicrocredits,
    long CostInputTokenPriceMicrocredits,
    long CostOutputTokenPriceMicrocredits,
    long CostCachedTokenPriceMicrocredits,
    string CostBillingMode,
    long CostQuantity,
    long CostVideoFormulaTokens)
{
    /// <summary>从已填充成本字段的订单（如备用线路替换订单）提取快照。</summary>
    public static TaskRouteCostSnapshot FromOrder(BillingOrder order) => new(
        order.CostConfigured,
        order.CostUnitPriceMicrocredits,
        order.CostInputTokenPriceMicrocredits,
        order.CostOutputTokenPriceMicrocredits,
        order.CostCachedTokenPriceMicrocredits,
        order.CostBillingMode,
        order.CostQuantity,
        order.CostVideoFormulaTokens);
}

/// <summary>
/// 任务路由切换与成本重算的事务写路径。
/// 对应 Go: <c>repository.SwitchTaskLogicalRoute</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>
    /// 原子切换运行中任务的供应线路，并按备用线路重算订单成本：
    /// ① 任务行 CAS（running + 原 route_id）；② 订单的成本快照改写（历史订单用下单价格快照，
    /// 缺失成本时不反查当前价格）；③ channel 计费时替换订单价格并按差额调整积分预留
    /// （新供应商价格不得扩大用户原始授权，替换只是价格快照而非新授权）。
    /// </summary>
    public async Task SwitchTaskLogicalRouteAsync(
        string taskId,
        string expectedRouteID,
        string routeID,
        string inputJSON,
        string billingOrderID,
        string channelID,
        string channelModelID,
        BillingOrder? replacement,
        TaskRouteCostSnapshot cost,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            int updated = await ExecuteAsync(
                connection,
                "UPDATE \"tasks\" SET \"route_id\" = @routeID, \"channel_model_id\" = @channelModelID, "
                + "\"input_json\" = @inputJSON, \"updated_at\" = @now "
                + "WHERE \"id\" = @taskId AND \"status\" = 'running' AND \"route_id\" = @expectedRouteID",
                new { routeID, channelModelID, inputJSON, now = DateTime.UtcNow, taskId, expectedRouteID },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (updated != 1)
            {
                throw new TaskRouteConflictException(TaskRouteErrors.TaskStateConflict, "任务状态已变化，未能切换线路");
            }
            if (billingOrderID.Length == 0)
            {
                return;
            }
            BillingOrder? order = await QueryFirstOrDefaultAsync<BillingOrder>(
                connection,
                SqlBuilder.Select<BillingOrder>(
                    "\"id\" = @billingOrderID AND \"task_id\" = @taskId "
                    + "AND \"status\" IN ('reserved', 'running')",
                    limitOffset: " LIMIT 1"),
                new { billingOrderID, taskId },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (order is null)
            {
                throw new TaskRouteConflictException(TaskRouteErrors.BillingStateConflict, "任务计费订单不存在或已结算");
            }
            DateTime now = DateTime.UtcNow;
            var updates = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["channel_id"] = channelID,
                ["channel_model_id"] = channelModelID,
                ["updated_at"] = now,
                ["cost_configured"] = cost.CostConfigured,
                ["cost_unit_price_microcredits"] = cost.CostUnitPriceMicrocredits,
                ["cost_input_token_price_microcredits"] = cost.CostInputTokenPriceMicrocredits,
                ["cost_output_token_price_microcredits"] = cost.CostOutputTokenPriceMicrocredits,
                ["cost_cached_token_price_microcredits"] = cost.CostCachedTokenPriceMicrocredits,
                ["cost_billing_mode"] = cost.CostBillingMode,
                ["cost_quantity"] = cost.CostQuantity,
                ["cost_video_formula_tokens"] = cost.CostVideoFormulaTokens,
            };
            if (replacement is not null)
            {
                if (replacement.UserID != order.UserID || replacement.TaskID != taskId
                    || replacement.AmountMicrocredits <= 0)
                {
                    throw new TaskRouteConflictException(TaskRouteErrors.BillingStateConflict, "备用线路订单与任务绑定不一致");
                }
                // 新供应商价格不能扩大用户原始授权；保留现有上限。
                if (replacement.AmountMicrocredits < 0
                    || (BillingChargeLimitApplies(order)
                        && (order.ChargeLimitMicrocredits < 0
                            || replacement.AmountMicrocredits > order.ChargeLimitMicrocredits)))
                {
                    throw new TaskRouteChargeLimitException("备用线路报价超过已批准费用上限，未切换线路；请重新确认生成报价");
                }
                long reserved = order.ReservedAmountMicrocredits > 0
                    ? order.ReservedAmountMicrocredits
                    : order.AmountMicrocredits;
                long delta = replacement.AmountMicrocredits - reserved;
                if (delta != 0)
                {
                    string accountUpdate = delta > 0
                        ? " SET \"version\" = \"version\" + 1, \"updated_at\" = @now, "
                            + "\"available_microcredits\" = \"available_microcredits\" - @delta, "
                            + "\"reserved_microcredits\" = \"reserved_microcredits\" + @delta "
                            + "WHERE \"user_id\" = @userID AND \"available_microcredits\" >= @delta"
                        : " SET \"version\" = \"version\" + 1, \"updated_at\" = @now, "
                            + "\"available_microcredits\" = \"available_microcredits\" + @release, "
                            + "\"reserved_microcredits\" = \"reserved_microcredits\" - @release "
                            + "WHERE \"user_id\" = @userID AND \"reserved_microcredits\" >= @release";
                    int accountUpdated = await ExecuteAsync(
                        connection,
                        "UPDATE \"credit_accounts\"" + accountUpdate,
                        new { now, userID = order.UserID, delta = Math.Abs(delta), release = Math.Abs(delta) },
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                    if (accountUpdated != 1)
                    {
                        if (delta > 0)
                        {
                            throw new InsufficientCreditsException();
                        }
                        throw new TaskRouteConflictException(
                            TaskRouteErrors.BillingStateConflict, "预留积分余额不一致");
                    }
                    CreditAccount? account = await QueryFirstOrDefaultAsync<CreditAccount>(
                        connection,
                        SqlBuilder.Select<CreditAccount>("\"user_id\" = @userID", limitOffset: " LIMIT 1"),
                        new { userID = order.UserID },
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                    if (account is null)
                    {
                        throw new TaskRouteConflictException(
                            TaskRouteErrors.BillingStateConflict, "积分账户不存在");
                    }
                    string entryType = delta < 0
                        ? CreditLedgerType.CreditLedgerRefund
                        : CreditLedgerType.CreditLedgerReserve;
                    await InsertLedgerAsync(connection, transaction, new CreditLedgerEntry
                    {
                        ID = IdGenerator.NewId(),
                        UserID = order.UserID,
                        Type = entryType,
                        AvailableDeltaMicrocredits = -delta,
                        ReservedDeltaMicrocredits = delta,
                        AvailableAfterMicrocredits = account.AvailableMicrocredits,
                        ReservedAfterMicrocredits = account.ReservedMicrocredits,
                        BillingOrderID = order.ID,
                        Model = order.Model,
                        ChannelID = channelID,
                        Scene = order.Scene,
                        Note = "备用供应线路价格调整",
                        CreatedAt = now,
                    }, cancellationToken).ConfigureAwait(false);
                }
                updates["billing_mode"] = replacement.BillingMode;
                updates["price_version"] = replacement.PriceVersion;
                updates["price_tier_id"] = replacement.PriceTierID;
                updates["price_tier_version"] = replacement.PriceTierVersion;
                updates["unit_price_microcredits"] = replacement.UnitPriceMicrocredits;
                updates["multiplier_basis_points"] = replacement.MultiplierBasisPoints;
                updates["quantity"] = replacement.Quantity;
                updates["amount_microcredits"] = replacement.AmountMicrocredits;
                updates["reserved_amount_microcredits"] = replacement.AmountMicrocredits;
                updates["input_token_price_microcredits"] = replacement.InputTokenPriceMicrocredits;
                updates["output_token_price_microcredits"] = replacement.OutputTokenPriceMicrocredits;
                updates["cached_token_price_microcredits"] = replacement.CachedTokenPriceMicrocredits;
            }
            var parameters = new DynamicParameters(updates);
            parameters.Add("billingOrderID", billingOrderID);
            parameters.Add("taskId", taskId);
            int billingUpdated = await ExecuteAsync(
                connection,
                "UPDATE \"billing_orders\" SET "
                + string.Join(", ", updates.Keys.Select(key =>
                    $"\"{SqlIdentifier(key)}\" = @{key}"))
                + " WHERE \"id\" = @billingOrderID AND \"task_id\" = @taskId "
                + "AND \"status\" IN ('reserved', 'running')",
                parameters,
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (billingUpdated != 1)
            {
                throw new TaskRouteConflictException(TaskRouteErrors.BillingStateConflict, "任务计费订单状态已变化，未能切换线路");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>新供应商价格不得扩大原始授权。对应 Go: <c>billingChargeLimitApplies</c>。</summary>
    private static bool BillingChargeLimitApplies(BillingOrder order) =>
        order.ChargeLimitSet || order.ChargeLimitMicrocredits > 0;

    /// <summary>白名单化列名，防止动态 SET 子句注入。</summary>
    private static string SqlIdentifier(string key) =>
        char.IsLetter(key[0]) && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? key
            : throw new InvalidOperationException("invalid update column");

    private async Task<T?> QueryFirstOrDefaultAsync<T>(
        DbConnection connection, string sql, object parameters, DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        IEnumerable<T> items = await QueryAsync<T>(
            connection, sql, parameters, transaction, cancellationToken).ConfigureAwait(false);
        return items.FirstOrDefault();
    }
}

/// <summary>切线路事务失败。Reason 对应 Go 的哨兵错误，应用层按类目转译。</summary>
public sealed class TaskRouteConflictException : Exception
{
    public TaskRouteConflictException(string reason, string message) : base(message)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>备用线路报价超出已批准费用上限。</summary>
public sealed class TaskRouteChargeLimitException : Exception
{
    public TaskRouteChargeLimitException(string message) : base(message)
    {
    }
}
