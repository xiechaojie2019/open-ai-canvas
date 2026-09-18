using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 积分账本与任务分页的仓储方法。
/// 对应 Go 的 <c>repository/finance.go</c> 与 <c>repository/admin_audit.go</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>
    /// 积分账本分页。对应 Go: <c>CreditLedger(userID, entryType, limit, offset)</c>。
    /// </summary>
    /// <remarks>
    /// 两个容易漏的细节：
    /// <list type="bullet">
    /// <item><b>永远排除 reserve 类型</b>——预扣记录不是用户可见的收支。</item>
    /// <item>limit 的钳制发生在 Count 之后，因此 total 不受 limit 影响。</item>
    /// </list>
    /// </remarks>
    public async Task<(IReadOnlyList<CreditLedgerEntry> Entries, long Total)> CreditLedgerAsync(
        string userId,
        string entryType,
        long limit,
        long offset,
        CancellationToken cancellationToken = default)
    {
        string condition = "user_id = @userId AND type <> @reserve";
        DynamicParameters parameters = new();
        parameters.Add("userId", userId);
        parameters.Add("reserve", CreditLedgerType.CreditLedgerReserve);

        switch (entryType)
        {
            case "income":
                condition += " AND type IN @incomeTypes";
                parameters.Add("incomeTypes", new[]
                {
                    CreditLedgerType.CreditLedgerRedeem,
                    CreditLedgerType.CreditLedgerAdminGrant,
                    CreditLedgerType.CreditLedgerAdminAdjust,
                    CreditLedgerType.CreditLedgerSignupBonus,
                    CreditLedgerType.CreditLedgerCheckinBonus,
                });
                break;
            case "consume":
                condition += " AND type = @consume";
                parameters.Add("consume", CreditLedgerType.CreditLedgerConsume);
                break;
            case "refund":
                condition += " AND type = @refund";
                parameters.Add("refund", CreditLedgerType.CreditLedgerRefund);
                break;
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        long total = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"credit_ledger_entries\" WHERE " + condition,
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (limit is <= 0 or > 100)
        {
            limit = 30;
        }

        if (offset < 0)
        {
            offset = 0;
        }

        IReadOnlyList<CreditLedgerEntry> entries = await QueryAsync<CreditLedgerEntry>(
            connection,
            SqlBuilder.Select<CreditLedgerEntry>(condition, "created_at DESC", Dialect.LimitOffset(limit, offset)),
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (entries, total);
    }

    /// <summary>
    /// 账本引用键是否存在。用于判断"今天是否已签到"。
    /// 对应 Go: <c>CreditLedgerReferenceExists</c>。
    /// </summary>
    public async Task<bool> CreditLedgerReferenceExistsAsync(
        string referenceKey,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long count = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"credit_ledger_entries\" WHERE reference_key = @referenceKey",
            new { referenceKey },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return count > 0;
    }

    /// <summary>
    /// 指定用户的任务分页。对应 Go: <c>AdminUserTasks(userID, limit, offset)</c>。
    /// </summary>
    /// <remarks>只取列表需要的列，不返回 prompt / input_json / result_json 等大字段。</remarks>
    public async Task<(IReadOnlyList<TaskEntity> Tasks, long Total)> AdminUserTasksAsync(
        string userId,
        long limit,
        long offset,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        long total = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"tasks\" WHERE user_id = @userId",
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TaskEntity> tasks = await QueryAsync<TaskEntity>(
            connection,
            SqlBuilder.SelectColumns<TaskEntity>(
                [
                    "ID", "UserID", "ProjectID", "Type", "Status", "Stage", "Progress", "Operation",
                    "Provider", "Model", "BillingOrderID", "ProviderRequestID", "PollStage",
                    "Attempts", "StartedAt", "CompletedAt", "CreatedAt", "UpdatedAt",
                ],
                "user_id = @userId",
                "created_at DESC",
                Dialect.LimitOffset(limit, offset)),
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (tasks, total);
    }
}
