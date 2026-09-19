#nullable enable
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using System.Text.Json;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 创作运行仓储。对应 Go: <c>repository/creation.go</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>创作存储用量（运行条数 + 结构化字节）。对应 Go: <c>CreationStorageUsage</c>。</summary>
    public async Task<(long Count, long Bytes)> CreationStorageUsageAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long count = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM creation_runs WHERE user_id = @userId",
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        long bytes = await ScalarAsync<long>(
            connection,
            """
            SELECT
              (SELECT COALESCE(SUM(length(COALESCE(state_json, '')) + length(COALESCE(approved_operations_json, '')) + length(COALESCE(approved_canvas_json, ''))), 0) FROM creation_runs WHERE user_id = @userId)
              + (SELECT COALESCE(SUM(length(COALESCE(request_json, '')) + length(COALESCE(quote_json, '')) + length(COALESCE(price_signature, ''))), 0) FROM creation_submissions WHERE user_id = @userId)
            """,
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (count, bytes);
    }

    /// <summary>按 ID 查运行。对应 Go: <c>CreationRun</c>（未命中返回 null）。</summary>
    public async Task<CreationRun?> CreationRunAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<CreationRun>(
            connection,
            SqlBuilder.Select<CreationRun>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按客户端键查运行。对应 Go: <c>CreationRunByClientKey</c>。</summary>
    public async Task<CreationRun?> CreationRunByClientKeyAsync(
        string userId, string clientKey, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<CreationRun>(
            connection,
            SqlBuilder.Select<CreationRun>(
                "user_id = @userId AND client_key = @clientKey", limitOffset: " LIMIT 1"),
            new { userId, clientKey },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>最近 100 个运行（更新时间倒序）。对应 Go: <c>CreationRuns</c>。</summary>
    public async Task<IReadOnlyList<CreationRun>> CreationRunsAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<CreationRun>(
            connection,
            SqlBuilder.Select<CreationRun>("user_id = @userId", "updated_at DESC") + " LIMIT 100",
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 创建运行（clientKey 幂等：同键同内容返回旧行，不同内容报冲突）。
    /// 对应 Go: <c>CreateCreationRun</c>。
    /// </summary>
    public async Task<CreationRun?> CreateCreationRunAsync(
        CreationRun run, CancellationToken cancellationToken = default)
    {
        CreationRun? old = await CreationRunByClientKeyAsync(run.UserID, run.ClientKey, cancellationToken)
            .ConfigureAwait(false);
        if (old is not null)
        {
            return old.CreateHash == run.CreateHash ? old : null;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                SqlBuilder.Insert(typeof(CreationRun)), run, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return run;
        }
        catch (Exception)
        {
            CreationRun? winner = await CreationRunByClientKeyAsync(run.UserID, run.ClientKey, cancellationToken)
                .ConfigureAwait(false);
            if (winner is not null && winner.CreateHash == run.CreateHash)
            {
                return winner;
            }
            throw;
        }
    }

    /// <summary>
    /// 运行互斥变更：条件写入拿行写锁，回调内完成全部读写后统一保存运行行。
    /// 对应 Go: <c>MutateCreationRun</c>。回调未命中运行抛 record not found。
    /// </summary>
    public async Task<T> MutateCreationRunAsync<T>(
        string userId,
        string id,
        Func<CreationRun, CreationRunMutationContext, Task<T>> callback,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int touched = await ExecuteAsync(
            connection,
            "UPDATE creation_runs SET revision = revision WHERE id = @id AND user_id = @userId",
            new { id, userId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (touched != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("record not found");
        }

        CreationRun? run = await FirstOrDefaultAsync<CreationRun>(
            connection,
            SqlBuilder.Select<CreationRun>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id, userId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("record not found");
        }

        CreationRunMutationContext context = new(connection, transaction, this);
        T result = await callback(run, context).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            """
            UPDATE creation_runs SET
              state_json = @StateJSON, status = @Status, revision = @Revision,
              canvas_id = @CanvasID,
              execution_epoch = @ExecutionEpoch, execution_owner = @ExecutionOwner,
              lease_expires_at = @LeaseExpiresAt,
              approved_proposal_version = @ApprovedProposalVersion,
              approved_proposal_hash = @ApprovedProposalHash,
              approved_operations_json = @ApprovedOperationsJSON,
              approved_canvas_json = @ApprovedCanvasJSON,
              approved_at = @ApprovedAt, updated_at = @UpdatedAt
            WHERE id = @ID AND user_id = @UserID
            """,
            run,
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>运行下的提交列表（创建时间升序）。对应 Go: <c>CreationSubmissions</c>。</summary>
    public async Task<IReadOnlyList<CreationSubmission>> CreationSubmissionsAsync(
        string userId, string runId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<CreationSubmission>(
            connection,
            SqlBuilder.Select<CreationSubmission>(
                "user_id = @userId AND run_id = @runId", "created_at ASC"),
            new { userId, runId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 查提交。对应 Go: <c>CreationSubmission</c>。</summary>
    public async Task<CreationSubmission?> CreationSubmissionAsync(
        string userId, string runId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<CreationSubmission>(
            connection,
            SqlBuilder.Select<CreationSubmission>(
                "id = @id AND user_id = @userId AND run_id = @runId", limitOffset: " LIMIT 1"),
            new { id, userId, runId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保存提交（GORM Save 全量更新）。对应 Go: <c>SaveCreationSubmission</c>。</summary>
    public async Task SaveCreationSubmissionAsync(
        CreationSubmission item, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE creation_submissions SET
              proposal_version = @ProposalVersion, proposal_hash = @ProposalHash,
              request_json = @RequestJSON, request_hash = @RequestHash,
              quote_json = @QuoteJSON, price_signature = @PriceSignature,
              expires_at = @ExpiresAt, approved_at = @ApprovedAt, revoked_at = @RevokedAt,
              task_id = @TaskID, updated_at = @UpdatedAt
            WHERE id = @ID
            """,
            item,
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (updated == 0)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<CreationSubmission>(), item, transaction, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>撤销未提交任务的报价。对应 Go: <c>RevokeCreationSubmissions</c>。</summary>
    public async Task RevokeCreationSubmissionsAsync(
        string runId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE creation_submissions SET revoked_at = @now
            WHERE run_id = @runId AND task_id IS NULL AND revoked_at IS NULL
            """,
            new { runId, now = DateTime.UtcNow },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CreationSubmission?> CreationSubmissionInTxAsync(
        DbConnection connection, DbTransaction? transaction,
        string userId, string runId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection owned = connection is null ? await OpenAsync(cancellationToken).ConfigureAwait(false) : null!;
        DbConnection conn = connection ?? owned;
        var item = await FirstOrDefaultAsync<CreationSubmission>(
            conn,
            SqlBuilder.Select<CreationSubmission>(
                "id = @id AND user_id = @userId AND run_id = @runId", limitOffset: " LIMIT 1"),
            new { id, userId, runId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        return item;
    }

    internal async Task<IReadOnlyList<CreationSubmission>> CreationSubmissionsInTxAsync(
        DbConnection connection, DbTransaction? transaction,
        string userId, string runId, CancellationToken cancellationToken = default)
    {
        return await QueryAsync<CreationSubmission>(
            connection,
            SqlBuilder.Select<CreationSubmission>(
                "user_id = @userId AND run_id = @runId", "created_at ASC"),
            new { userId, runId },
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<Resource?> ResourceForUserInTxAsync(
        DbConnection connection, DbTransaction? transaction,
        string userId, string id, CancellationToken cancellationToken = default)
    {
        return await FirstOrDefaultAsync<Resource>(
            connection,
            SqlBuilder.Select<Resource>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id, userId },
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 捕获服务端配置版本签名（不含渠道凭证）。对应 Go: <c>CreationPriceSignature</c>。
    /// 仅要求实现内部一致，序列化用 Ordinal 键序 + Go 转义。
    /// </summary>
    public async Task<string> CreationPriceSignatureAsync(
        TaskEntity task,
        string channelId,
        string modelKey,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ChannelModel? cm;
        if (task.ChannelModelID.Length > 0)
        {
            cm = await FirstOrDefaultAsync<ChannelModel>(
                connection,
                SqlBuilder.Select<ChannelModel>("id = @id", limitOffset: " LIMIT 1"),
                new { id = task.ChannelModelID },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            cm = await FirstOrDefaultAsync<ChannelModel>(
                connection,
                SqlBuilder.Select<ChannelModel>(
                    "channel_id = @channelId AND model_key = @modelKey", limitOffset: " LIMIT 1"),
                new { channelId, modelKey },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        if (cm is null)
        {
            throw new InvalidOperationException("record not found");
        }
        ModelChannel? channel = await FirstOrDefaultAsync<ModelChannel>(
            connection,
            SqlBuilder.Select<ModelChannel>("id = @id", limitOffset: " LIMIT 1"),
            new { id = cm.ChannelID },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            throw new InvalidOperationException("record not found");
        }
        IReadOnlyList<ChannelModelPriceTier> tiers = await QueryAsync<ChannelModelPriceTier>(
            connection,
            SqlBuilder.Select<ChannelModelPriceTier>("channel_model_id = @id", "id ASC"),
            new { id = cm.ID },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        Dictionary<string, object?> values = new(StringComparer.Ordinal)
        {
            ["channelModel"] = cm,
            ["capabilityConfig"] = cm.CapabilityConfigJSON,
            ["channel"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ID"] = channel.ID,
                ["Enabled"] = channel.Enabled,
                ["UpdatedAt"] = channel.UpdatedAt,
            },
            ["tiers"] = tiers,
        };
        if (task.LogicalModelID.Length > 0)
        {
            LogicalModel? logical = await FirstOrDefaultAsync<LogicalModel>(
                connection,
                SqlBuilder.Select<LogicalModel>("id = @id", limitOffset: " LIMIT 1"),
                new { id = task.LogicalModelID },
                cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("record not found");
            LogicalModelRoute? route = await FirstOrDefaultAsync<LogicalModelRoute>(
                connection,
                SqlBuilder.Select<LogicalModelRoute>("id = @id", limitOffset: " LIMIT 1"),
                new { id = task.RouteID },
                cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("record not found");
            values["logical"] = logical;
            values["route"] = route;
        }
        IReadOnlyList<SystemSetting> settings = await QueryAsync<SystemSetting>(
            connection,
            "SELECT * FROM system_settings WHERE key IN ('credit_policy', 'feature_availability') ORDER BY key",
            new { },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        values["pricingSettings"] = settings;

        return CreationHashExtensions.SignatureHash(JsonSerializer.SerializeToElement(values));
    }

}

internal static class CreationHashExtensions
{
    /// <summary>键序 Ordinal 递归排序 + SHA-256 hex（与 Application 侧签名同构）。</summary>
    internal static string SignatureHash(JsonElement element)
    {
        string encoded = JsonSerializer.Serialize(SortCanonical(element),
            new JsonSerializerOptions());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encoded))).ToLowerInvariant();
    }

    internal static JsonElement SortCanonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => JsonSerializer.SerializeToElement(
            new SortedDictionary<string, JsonElement>(
                element.EnumerateObject().ToDictionary(p => p.Name, p => SortCanonical(p.Value)),
                StringComparer.Ordinal)),
        JsonValueKind.Array => JsonSerializer.SerializeToElement(
            element.EnumerateArray().Select(SortCanonical).ToList()),
        _ => element.Clone(),
    };

    /// <summary>对应 Go: <c>creationHash</c>。SHA-256 hex of canonical JSON。</summary>
    public static string CreationHash(this object? value)
    {
        return SignatureHash(JsonSerializer.SerializeToElement(value));
    }
}

/// <summary>
/// 互斥变更上下文：回调内的读写都必须经由本上下文落在同一事务上。
/// </summary>
public sealed class CreationRunMutationContext
{
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    private readonly Repository _repository;

    internal CreationRunMutationContext(DbConnection connection, DbTransaction transaction, Repository repository)
    {
        _connection = connection;
        _transaction = transaction;
        _repository = repository;
    }

    public Task<CreationSubmission?> CreationSubmissionAsync(
        string userId, string runId, string id, CancellationToken cancellationToken = default) =>
        _repository.CreationSubmissionInTxAsync(_connection, _transaction, userId, runId, id, cancellationToken);

    public Task<IReadOnlyList<CreationSubmission>> CreationSubmissionsAsync(
        string userId, string runId, CancellationToken cancellationToken = default) =>
        _repository.CreationSubmissionsInTxAsync(_connection, _transaction, userId, runId, cancellationToken);

    public Task SaveSubmissionAsync(CreationSubmission item, CancellationToken cancellationToken = default) =>
        _repository.SaveCreationSubmissionInTxAsync(_connection, _transaction, item, cancellationToken);

    public Task RevokeSubmissionsAsync(string runId, CancellationToken cancellationToken = default) =>
        _repository.RevokeCreationSubmissionsInTxAsync(_connection, _transaction, runId, cancellationToken);

    public Task<Resource?> ResourceForUserAsync(
        string userId, string id, CancellationToken cancellationToken = default) =>
        _repository.ResourceForUserInTxAsync(_connection, _transaction, userId, id, cancellationToken);

    public Task<string> CreationPriceSignatureAsync(
        TaskEntity task, string channelId, string modelKey, CancellationToken cancellationToken = default) =>
        _repository.CreationPriceSignatureInTxAsync(_connection, _transaction, task, channelId, modelKey, cancellationToken);

    public Task CreateTaskWithQuotaAsync(
        TaskEntity task, BillingOrder? order, int activeTaskLimit, CancellationToken cancellationToken = default) =>
        _repository.CreateTaskWithQuotaInTxAsync(_connection, _transaction, task, order, activeTaskLimit, cancellationToken);

    public Task<TaskEntity?> TaskForUserAsync(
        string userId, string taskId, CancellationToken cancellationToken = default) =>
        _repository.TaskForUserInTxAsync(_connection, _transaction, userId, taskId, cancellationToken);

    public Task<UserStorageUsage> UserStorageUsageAsync(
        string userId, CancellationToken cancellationToken = default) =>
        _repository.UserStorageUsageInTxAsync(_connection, _transaction, userId, cancellationToken);

    public async Task<CanvasProject?> CanvasProjectForUserAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        return await _repository.CanvasProjectForUserInTxAsync(
            _connection, _transaction, userId, id, cancellationToken).ConfigureAwait(false);
    }

    public Task CreateCanvasProjectAsync(
        CanvasProject canvas, CancellationToken cancellationToken = default) =>
        _repository.CreateCanvasProjectInTxAsync(_connection, _transaction, canvas, cancellationToken);

    public Task CompareSaveCreationCanvasAsync(
        CanvasProject canvas, string previous, CancellationToken cancellationToken = default) =>
        _repository.CompareSaveCreationCanvasInTxAsync(_connection, _transaction, canvas, previous, cancellationToken);

    public Task<long> CreationRunStorageBytesAsync(string userId, CancellationToken cancellationToken = default) =>
        _repository.CreationRunStorageBytesInTxAsync(_connection, _transaction, userId, cancellationToken);
}

public sealed partial class Repository
{
    internal async Task SaveCreationSubmissionInTxAsync(
        DbConnection connection, DbTransaction transaction, CreationSubmission item,
        CancellationToken cancellationToken)
    {
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE creation_submissions SET
              proposal_version = @ProposalVersion, proposal_hash = @ProposalHash,
              request_json = @RequestJSON, request_hash = @RequestHash,
              quote_json = @QuoteJSON, price_signature = @PriceSignature,
              expires_at = @ExpiresAt, approved_at = @ApprovedAt, revoked_at = @RevokedAt,
              task_id = @TaskID, updated_at = @UpdatedAt
            WHERE id = @ID
            """,
            item,
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (updated == 0)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<CreationSubmission>(), item, transaction, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal async Task RevokeCreationSubmissionsInTxAsync(
        DbConnection connection, DbTransaction transaction, string runId,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            """
            UPDATE creation_submissions SET revoked_at = @now
            WHERE run_id = @runId AND task_id IS NULL AND revoked_at IS NULL
            """,
            new { runId, now = DateTime.UtcNow },
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string> CreationPriceSignatureInTxAsync(
        DbConnection connection, DbTransaction transaction, TaskEntity task,
        string channelId, string modelKey, CancellationToken cancellationToken)
    {
        ChannelModel? cm;
        if (task.ChannelModelID.Length > 0)
        {
            cm = await FirstOrDefaultAsync<ChannelModel>(
                connection,
                SqlBuilder.Select<ChannelModel>("id = @id", limitOffset: " LIMIT 1"),
                new { id = task.ChannelModelID },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            cm = await FirstOrDefaultAsync<ChannelModel>(
                connection,
                SqlBuilder.Select<ChannelModel>(
                    "channel_id = @channelId AND model_key = @modelKey", limitOffset: " LIMIT 1"),
                new { channelId, modelKey },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }
        if (cm is null)
        {
            throw new InvalidOperationException("record not found");
        }
        ModelChannel? channel = await FirstOrDefaultAsync<ModelChannel>(
            connection,
            SqlBuilder.Select<ModelChannel>("id = @id", limitOffset: " LIMIT 1"),
            new { id = cm.ChannelID },
            transaction,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        IReadOnlyList<ChannelModelPriceTier> tiers = await QueryAsync<ChannelModelPriceTier>(
            connection,
            SqlBuilder.Select<ChannelModelPriceTier>("channel_model_id = @id", "id ASC"),
            new { id = cm.ID },
            transaction,
            cancellationToken).ConfigureAwait(false);

        Dictionary<string, object?> values = new(StringComparer.Ordinal)
        {
            ["channelModel"] = cm,
            ["capabilityConfig"] = cm.CapabilityConfigJSON,
            ["channel"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ID"] = channel.ID,
                ["Enabled"] = channel.Enabled,
                ["UpdatedAt"] = channel.UpdatedAt,
            },
            ["tiers"] = tiers,
        };
        if (task.LogicalModelID.Length > 0)
        {
            LogicalModel? logical = await FirstOrDefaultAsync<LogicalModel>(
                connection,
                SqlBuilder.Select<LogicalModel>("id = @id", limitOffset: " LIMIT 1"),
                new { id = task.LogicalModelID },
                transaction,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("record not found");
            LogicalModelRoute? route = await FirstOrDefaultAsync<LogicalModelRoute>(
                connection,
                SqlBuilder.Select<LogicalModelRoute>("id = @id", limitOffset: " LIMIT 1"),
                new { id = task.RouteID },
                transaction,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("record not found");
            values["logical"] = logical;
            values["route"] = route;
        }
        IReadOnlyList<SystemSetting> settings = await QueryAsync<SystemSetting>(
            connection,
            "SELECT * FROM system_settings WHERE key IN ('credit_policy', 'feature_availability') ORDER BY key",
            new { },
            transaction,
            cancellationToken).ConfigureAwait(false);
        values["pricingSettings"] = settings;
        return CreationHashExtensions.SignatureHash(JsonSerializer.SerializeToElement(values));
    }

    internal async Task CreateTaskWithQuotaInTxAsync(
        DbConnection connection, DbTransaction transaction, TaskEntity task,
        BillingOrder? order, int activeTaskLimit, CancellationToken cancellationToken)
    {
        if (task.LogicalModelID.Length > 0)
        {
            long active = await ScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM logical_models WHERE id = @id AND enabled = 1 AND archived_at IS NULL AND active_revision_id = @revision",
                new { id = task.LogicalModelID, revision = task.LogicalModelRevisionID },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (active == 0)
            {
                throw new InvalidOperationException("logical_model_unavailable");
            }
        }
        long count = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM tasks WHERE user_id = @userId AND status IN ('queued', 'running')",
            new { userId = task.UserID },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (count >= activeTaskLimit)
        {
            throw new InvalidOperationException("active_task_limit");
        }
        if (order is not null)
        {
            await ReserveBillingOrderInTransactionAsync(
                connection, transaction, order, cancellationToken).ConfigureAwait(false);
        }
        await ExecuteAsync(
            connection, SqlBuilder.Insert<TaskEntity>(), task, transaction, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<long> CreationRunStorageBytesInTxAsync(
        DbConnection connection, DbTransaction transaction, string userId,
        CancellationToken cancellationToken)
    {
        return await ScalarAsync<long>(
            connection,
            """
            SELECT
              (SELECT COALESCE(SUM(length(COALESCE(state_json, '')) + length(COALESCE(approved_operations_json, '')) + length(COALESCE(approved_canvas_json, ''))), 0) FROM creation_runs WHERE user_id = @userId)
              + (SELECT COALESCE(SUM(length(COALESCE(request_json, '')) + length(COALESCE(quote_json, '')) + length(COALESCE(price_signature, ''))), 0) FROM creation_submissions WHERE user_id = @userId)
            """,
            new { userId },
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建画布工程。对应 Go: <c>CreateCreationCanvas</c>。</summary>
    public async Task CreateCanvasProjectAsync(
        CanvasProject canvas, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(CanvasProject)), canvas, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>比较并保存画布（期望旧载荷一致）。对应 Go: <c>CompareSaveCreationCanvas</c>。</summary>
    public async Task CompareSaveCreationCanvasAsync(
        CanvasProject canvas, string previous, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE canvas_projects SET payload_json = @PayloadJSON, title = @Title, updated_at = @now
            WHERE id = @ID AND user_id = @UserID AND payload_json = @previous
            """,
            new { canvas.PayloadJSON, canvas.Title, now = DateTime.UtcNow, canvas.ID, canvas.UserID, previous },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new InvalidOperationException("creation_conflict");
        }
    }


    internal async Task<TaskEntity?> TaskForUserInTxAsync(
        DbConnection connection, DbTransaction? transaction,
        string userId, string taskId, CancellationToken cancellationToken = default)
    {
        return await FirstOrDefaultAsync<TaskEntity>(
            connection,
            SqlBuilder.Select<TaskEntity>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id = taskId, userId },
            transaction,
            cancellationToken).ConfigureAwait(false);
    }


    internal async Task<UserStorageUsage> UserStorageUsageInTxAsync(
        DbConnection connection, DbTransaction? transaction,
        string userId, CancellationToken cancellationToken = default)
    {
        long taskCount = await ScalarAsync<long>(
            connection, "SELECT COUNT(*) FROM tasks WHERE user_id = @userId",
            new { userId }, transaction, cancellationToken).ConfigureAwait(false);
        long taskBytes = await ScalarAsync<long>(
            connection, "SELECT COALESCE(SUM(length(prompt) + length(COALESCE(input_json, '')) + length(COALESCE(error, ''))), 0) FROM tasks WHERE user_id = @userId",
            new { userId }, transaction, cancellationToken).ConfigureAwait(false);
        long assetCount = await ScalarAsync<long>(
            connection, "SELECT COUNT(*) FROM assets WHERE user_id = @userId",
            new { userId }, transaction, cancellationToken).ConfigureAwait(false);
        long assetBytes = await ScalarAsync<long>(
            connection, "SELECT COALESCE(SUM(length(COALESCE(payload_json, ''))), 0) FROM assets WHERE user_id = @userId",
            new { userId }, transaction, cancellationToken).ConfigureAwait(false);
        long canvasCount = await ScalarAsync<long>(
            connection, "SELECT COUNT(*) FROM canvas_projects WHERE user_id = @userId",
            new { userId }, transaction, cancellationToken).ConfigureAwait(false);
        long canvasBytes = await ScalarAsync<long>(
            connection, "SELECT COALESCE(SUM(length(COALESCE(payload_json, ''))), 0) FROM canvas_projects WHERE user_id = @userId",
            new { userId }, transaction, cancellationToken).ConfigureAwait(false);
        long apiCallCount = await ScalarAsync<long>(
            connection, "SELECT COUNT(*) FROM api_call_logs WHERE user_id = @userId",
            new { userId }, transaction, cancellationToken).ConfigureAwait(false);
        return new UserStorageUsage
        {
            AssetCount = assetCount, AssetBytes = assetBytes,
            CanvasCount = canvasCount, CanvasBytes = canvasBytes,
            TaskCount = taskCount, TaskBytes = taskBytes,
            ApiCallCount = apiCallCount,
        };
    }

    internal async Task<CanvasProject?> CanvasProjectForUserInTxAsync(
        DbConnection connection, DbTransaction? transaction,
        string userId, string id, CancellationToken cancellationToken = default)
    {
        return await FirstOrDefaultAsync<CanvasProject>(
            connection,
            SqlBuilder.Select<CanvasProject>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id, userId },
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task CreateCanvasProjectInTxAsync(
        DbConnection connection, DbTransaction? transaction,
        CanvasProject canvas, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            connection, SqlBuilder.Insert<CanvasProject>(), canvas, transaction, cancellationToken).ConfigureAwait(false);
    }

    internal async Task CompareSaveCreationCanvasInTxAsync(
        DbConnection connection, DbTransaction? transaction,
        CanvasProject canvas, string previous, CancellationToken cancellationToken = default)
    {
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE canvas_projects SET payload_json = @PayloadJSON, title = @Title, updated_at = @now
            WHERE id = @ID AND user_id = @UserID AND payload_json = @previous
            """,
            new { canvas.PayloadJSON, canvas.Title, now = DateTime.UtcNow, canvas.ID, canvas.UserID, previous },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new InvalidOperationException("creation_conflict");
        }
    }

}
