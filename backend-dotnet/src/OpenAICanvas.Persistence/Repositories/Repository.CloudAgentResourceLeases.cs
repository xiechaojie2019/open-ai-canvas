using System.Data.Common;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 云 Agent 资源租约仓储：在准备动作等待审批期间钉住不可变输入；
/// 任务提交后由任务自身持有资源引用。对应 Go: <c>repository/cloud_agent_resource_lease.go</c>。
/// </summary>
public sealed partial class Repository
{
    private const string LeaseTaskOwnerPrefix = "task:";

    /// <summary>按 (user_id, owner_id, resource_id) 幂等钉住资源。对应 Go: <c>UpsertCloudAgentResourceLeases</c>。</summary>
    public Task UpsertCloudAgentResourceLeasesAsync(
        string userID, string runID, string ownerID, IReadOnlyList<string> resourceIDs, DateTime expiresAt,
        CancellationToken cancellationToken = default) =>
        InTransactionAsync((connection, transaction) =>
            UpsertCloudAgentResourceLeasesCoreAsync(
                connection, transaction, userID, runID, ownerID, resourceIDs, expiresAt, cancellationToken));

    /// <summary>
    /// 以审批所有者的资源集为准（用户修改生成参数后刷新）。对应 Go: <c>ReplaceCloudAgentResourceLeases</c>。
    /// </summary>
    public Task ReplaceCloudAgentResourceLeasesAsync(
        string userID, string runID, string ownerID, IReadOnlyList<string> resourceIDs, DateTime expiresAt,
        CancellationToken cancellationToken = default) =>
        InTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(
                connection,
                $"DELETE FROM {Quote("cloudAgentResourceLeases")} WHERE {Quote("userId")} = @userID AND {Quote("ownerId")} = @ownerID",
                new { userID, ownerID },
                transaction,
                cancellationToken).ConfigureAwait(false);
            await UpsertCloudAgentResourceLeasesCoreAsync(
                connection, transaction, userID, runID, ownerID, resourceIDs, expiresAt, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>释放某个所有者名下的全部租约。对应 Go: <c>ReleaseCloudAgentResourceLeases</c>。</summary>
    public Task ReleaseCloudAgentResourceLeasesAsync(
        string userID, string ownerID, CancellationToken cancellationToken = default) =>
        InTransactionAsync((connection, transaction) =>
            ReleaseCloudAgentResourceLeasesCoreAsync(connection, transaction, userID, ownerID, cancellationToken));

    /// <summary>按运行释放全部租约（清理收尾）。对应 Go: <c>ReleaseCloudAgentResourceLeasesByRun</c>。</summary>
    public Task ReleaseCloudAgentResourceLeasesByRunAsync(
        string userID, string runID, CancellationToken cancellationToken = default) =>
        InTransactionAsync((connection, transaction) =>
            ReleaseCloudAgentResourceLeasesByRunCoreAsync(connection, transaction, userID, runID, cancellationToken));

    /// <summary>事务内变体（供检查点上下文复用同一连接）。</summary>
    public Task ReleaseCloudAgentResourceLeasesByRunInTxAsync(
        DbConnection connection, DbTransaction transaction, string userID, string runID,
        CancellationToken cancellationToken = default) =>
        ReleaseCloudAgentResourceLeasesByRunCoreAsync(connection, transaction, userID, runID, cancellationToken);

    private Task ReleaseCloudAgentResourceLeasesByRunCoreAsync(
        DbConnection connection, DbTransaction? transaction, string userID, string runID,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            $"DELETE FROM {Quote("cloudAgentResourceLeases")} WHERE {Quote("userId")} = @userID AND {Quote("runId")} = @runID",
            new { userID, runID },
            transaction,
            cancellationToken);

    /// <summary>审批通过提交任务后，把租约从审批所有者转到任务所有者。对应 Go: <c>TransferCloudAgentResourceLeases</c>。</summary>
    public Task TransferCloudAgentResourceLeasesAsync(
        string userID, string fromOwner, string toOwner, string runID, DateTime expiresAt,
        CancellationToken cancellationToken = default) =>
        InTransactionAsync(async (connection, transaction) =>
        {
            if (fromOwner.Length == 0 || toOwner.Length == 0 || fromOwner == toOwner)
            {
                return true;
            }
            List<LeaseRow> leases = (await QueryAsync<LeaseRow>(
                connection,
                $"SELECT {Quote("runId")} AS {Quote("RunID")}, {Quote("resourceId")} AS {Quote("ResourceID")} " +
                $"FROM {Quote("cloudAgentResourceLeases")} WHERE {Quote("userId")} = @userID AND {Quote("ownerId")} = @fromOwner",
                new { userID, fromOwner },
                transaction,
                cancellationToken).ConfigureAwait(false)).ToList();
            await ExecuteAsync(
                connection,
                $"DELETE FROM {Quote("cloudAgentResourceLeases")} WHERE {Quote("userId")} = @userID AND {Quote("ownerId")} = @toOwner",
                new { userID, toOwner },
                transaction,
                cancellationToken).ConfigureAwait(false);
            foreach (LeaseRow lease in leases)
            {
                await UpsertLeaseAsync(
                    connection, transaction, userID, runID, toOwner, lease.ResourceID, expiresAt, cancellationToken)
                    .ConfigureAwait(false);
            }
            await ExecuteAsync(
                connection,
                $"DELETE FROM {Quote("cloudAgentResourceLeases")} WHERE {Quote("userId")} = @userID AND {Quote("ownerId")} = @fromOwner",
                new { userID, fromOwner },
                transaction,
                cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>清理过期租约。对应 Go: <c>ReleaseExpiredCloudAgentResourceLeases</c>。</summary>
    public async Task ReleaseExpiredCloudAgentResourceLeasesAsync(
        DateTime now, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            $"DELETE FROM {Quote("cloudAgentResourceLeases")} WHERE {Quote("expiresAt")} <= @now",
            new { now },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------- 事务内入口

    public Task UpsertCloudAgentResourceLeasesInTxAsync(
        DbConnection connection, DbTransaction transaction, string userID, string runID, string ownerID,
        IReadOnlyList<string> resourceIDs, DateTime expiresAt, CancellationToken cancellationToken = default) =>
        UpsertCloudAgentResourceLeasesCoreAsync(
            connection, transaction, userID, runID, ownerID, resourceIDs, expiresAt, cancellationToken);

    public Task ReplaceCloudAgentResourceLeasesInTxAsync(
        DbConnection connection, DbTransaction transaction, string userID, string runID, string ownerID,
        IReadOnlyList<string> resourceIDs, DateTime expiresAt, CancellationToken cancellationToken = default) =>
        ReplaceCloudAgentResourceLeasesInTxCoreAsync(
            connection, transaction, userID, runID, ownerID, resourceIDs, expiresAt, cancellationToken);

    private async Task ReplaceCloudAgentResourceLeasesInTxCoreAsync(
        DbConnection connection, DbTransaction transaction, string userID, string runID, string ownerID,
        IReadOnlyList<string> resourceIDs, DateTime expiresAt, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            $"DELETE FROM {Quote("cloudAgentResourceLeases")} WHERE {Quote("userId")} = @userID AND {Quote("ownerId")} = @ownerID",
            new { userID, ownerID },
            transaction,
            cancellationToken).ConfigureAwait(false);
        await UpsertCloudAgentResourceLeasesCoreAsync(
            connection, transaction, userID, runID, ownerID, resourceIDs, expiresAt, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task ReleaseCloudAgentResourceLeasesInTxAsync(
        DbConnection connection, DbTransaction transaction, string userID, string ownerID,
        CancellationToken cancellationToken = default) =>
        ReleaseCloudAgentResourceLeasesCoreAsync(connection, transaction, userID, ownerID, cancellationToken);

    public Task TransferCloudAgentResourceLeasesInTxAsync(
        DbConnection connection, DbTransaction transaction, string userID, string fromOwner, string toOwner,
        string runID, DateTime expiresAt, CancellationToken cancellationToken = default) =>
        TransferCloudAgentResourceLeasesCoreAsync(
            connection, transaction, userID, fromOwner, toOwner, runID, expiresAt, cancellationToken);

    private async Task TransferCloudAgentResourceLeasesCoreAsync(
        DbConnection connection, DbTransaction transaction, string userID, string fromOwner, string toOwner,
        string runID, DateTime expiresAt, CancellationToken cancellationToken)
    {
        if (fromOwner.Length == 0 || toOwner.Length == 0 || fromOwner == toOwner)
        {
            return;
        }
        List<LeaseRow> leases = (await QueryAsync<LeaseRow>(
            connection,
            $"SELECT {Quote("runId")} AS {Quote("RunID")}, {Quote("resourceId")} AS {Quote("ResourceID")} " +
            $"FROM {Quote("cloudAgentResourceLeases")} WHERE {Quote("userId")} = @userID AND {Quote("ownerId")} = @fromOwner",
            new { userID, fromOwner },
            transaction,
            cancellationToken).ConfigureAwait(false)).ToList();
        await ExecuteAsync(
            connection,
            $"DELETE FROM {Quote("cloudAgentResourceLeases")} WHERE {Quote("userId")} = @userID AND {Quote("ownerId")} = @toOwner",
            new { userID, toOwner },
            transaction,
            cancellationToken).ConfigureAwait(false);
        foreach (LeaseRow lease in leases)
        {
            await UpsertLeaseAsync(
                connection, transaction, userID, runID, toOwner, lease.ResourceID, expiresAt, cancellationToken)
                .ConfigureAwait(false);
        }
        await ExecuteAsync(
            connection,
            $"DELETE FROM {Quote("cloudAgentResourceLeases")} WHERE {Quote("userId")} = @userID AND {Quote("ownerId")} = @fromOwner",
            new { userID, fromOwner },
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- 核心实现

    private async Task UpsertCloudAgentResourceLeasesCoreAsync(
        DbConnection connection, DbTransaction? transaction, string userID, string runID, string ownerID,
        IReadOnlyList<string> resourceIDs, DateTime expiresAt, CancellationToken cancellationToken)
    {
        foreach (string id in UniqueSortedStrings(resourceIDs))
        {
            await UpsertLeaseAsync(
                connection, transaction, userID, runID, ownerID, id, expiresAt, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task UpsertLeaseAsync(
        DbConnection connection, DbTransaction? transaction, string userID, string runID, string ownerID,
        string resourceID, DateTime expiresAt, CancellationToken cancellationToken)
    {
        // 冲突时只更新 run_id / expires_at，保留首次钉住关系。
        string sql = $"""
            INSERT INTO {Quote("cloudAgentResourceLeases")} ({Quote("userId")}, {Quote("runId")}, {Quote("ownerId")}, {Quote("resourceId")}, {Quote("expiresAt")})
            VALUES (@userID, @runID, @ownerID, @resourceID, @expiresAt)
            ON CONFLICT ({Quote("userId")}, {Quote("ownerId")}, {Quote("resourceId")})
            DO UPDATE SET {Quote("runId")} = excluded.{Quote("runId")}, {Quote("expiresAt")} = excluded.{Quote("expiresAt")}
            """;
        await ExecuteAsync(
            connection, sql,
            new { userID, runID, ownerID, resourceID, expiresAt },
            transaction, cancellationToken).ConfigureAwait(false);
    }

    private Task ReleaseCloudAgentResourceLeasesCoreAsync(
        DbConnection connection, DbTransaction? transaction, string userID, string ownerID,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            $"DELETE FROM {Quote("cloudAgentResourceLeases")} WHERE {Quote("userId")} = @userID AND {Quote("ownerId")} = @ownerID",
            new { userID, ownerID },
            transaction,
            cancellationToken);

    internal static List<string> UniqueSortedStrings(IEnumerable<string> values)
    {
        SortedSet<string> result = new(StringComparer.Ordinal);
        foreach (string value in values)
        {
            if (value.Length > 0)
            {
                result.Add(value);
            }
        }
        return [.. result];
    }

    private sealed class LeaseRow
    {
        public string RunID { get; set; } = "";

        public string ResourceID { get; set; } = "";
    }
}
