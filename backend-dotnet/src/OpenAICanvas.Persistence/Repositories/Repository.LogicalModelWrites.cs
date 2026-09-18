#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 前台模型写路径仓储方法。对应 Go: <c>repository/logical_models.go</c> 的
/// <c>SaveLogicalModelBundle</c> / <c>ArchiveLogicalModel</c> 与 <c>repository/finance.go</c> 的
/// <c>ChannelModelByKey</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>
    /// 按 ID 查渠道模型（含价格档，强制软删除过滤）。
    /// 对应 Go: <c>ChannelModel(id)</c>。
    /// </summary>
    public async Task<ChannelModel?> ChannelModelAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ChannelModel? item = await FirstOrDefaultAsync<ChannelModel>(
            connection,
            SqlBuilder.Select<ChannelModel>(
                SoftDelete.Apply("channel_models", "id = @id"),
                limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (item is null)
        {
            return null;
        }

        await AttachChannelModelPriceTiersAsync([item], cancellationToken).ConfigureAwait(false);
        return item;
    }

    /// <summary>
    /// 按渠道 ID + 模型键查启用的渠道模型（含价格档，强制软删除过滤）。
    /// 对应 Go: <c>ChannelModelByKey</c>。
    /// </summary>
    public async Task<ChannelModel?> ChannelModelByKeyAsync(
        string channelId,
        string modelKey,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ChannelModel? item = await FirstOrDefaultAsync<ChannelModel>(
            connection,
            SqlBuilder.Select<ChannelModel>(
                SoftDelete.Apply("channel_models", "channel_id = @channelId AND model_key = @modelKey AND enabled = @enabled"),
                limitOffset: " LIMIT 1"),
            new { channelId, modelKey, enabled = Dialect.Boolean(true) },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (item is null)
        {
            return null;
        }

        await AttachChannelModelPriceTiersAsync([item], cancellationToken).ConfigureAwait(false);
        return item;
    }

    /// <summary>
    /// 保存前台模型 + 新版本 + 供应线路，必须同事务完成。
    /// 对应 Go: <c>SaveLogicalModelBundle</c>。
    /// </summary>
    /// <remarks>
    /// 版本号由模型行原子递增（<c>revision_sequence = revision_sequence + 1</c>），
    /// MAX(version)+1 会让两个并发事务分配同一版本。方法返回后
    /// <paramref name="revision"/>.Version、<paramref name="item"/>.ActiveRevisionID 已回填。
    /// </remarks>
    public async Task SaveLogicalModelBundleAsync(
        LogicalModel item,
        LogicalModelRevision revision,
        IReadOnlyList<LogicalModelRoute> routes,
        bool creating,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            if (creating)
            {
                int inserted = await ExecuteAsync(
                    connection,
                    SqlBuilder.Insert(typeof(LogicalModel)),
                    item,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                if (inserted != 1)
                {
                    throw new InvalidOperationException("failed to insert logical model");
                }
            }
            else
            {
                int updated = await ExecuteAsync(
                    connection,
                    """
                    UPDATE "logical_models" SET
                        "code" = @Code, "name" = @Name, "icon" = @Icon, "description" = @Description,
                        "capability" = @Capability, "enabled" = @Enabled, "sort_order" = @SortOrder,
                        "source_channel_model_id" = @SourceChannelModelID, "price_policy" = @PricePolicy,
                        "billing_mode" = @BillingMode, "unit_price_microcredits" = @UnitPriceMicrocredits,
                        "input_price_microcredits" = @InputPriceMicrocredits,
                        "output_price_microcredits" = @OutputPriceMicrocredits,
                        "cached_price_microcredits" = @CachedPriceMicrocredits,
                        "legacy_model_ids_json" = @LegacyModelIDsJSON, "updated_at" = @UpdatedAt
                    WHERE "id" = @ID
                    """,
                    item,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                if (updated != 1)
                {
                    // Go 返回裸 gorm.ErrRecordNotFound，由 failService 投影为 500 固定文案。
                    throw new InvalidOperationException("record not found");
                }
            }

            // 版本号必须由模型行原子递增，保证并发保存的版本单调。
            int bumped = await ExecuteAsync(
                connection,
                "UPDATE \"logical_models\" SET \"revision_sequence\" = \"revision_sequence\" + 1 WHERE \"id\" = @ID",
                item,
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (bumped != 1)
            {
                // Go 返回裸 gorm.ErrRecordNotFound，由 failService 投影为 500 固定文案。
                throw new InvalidOperationException("record not found");
            }

            long sequence = await ScalarAsync<long>(
                connection,
                "SELECT \"revision_sequence\" FROM \"logical_models\" WHERE \"id\" = @ID",
                item,
                transaction,
                cancellationToken).ConfigureAwait(false);

            revision.Version = sequence;
            await connection.ExecuteAsync(new CommandDefinition(
                SqlBuilder.Insert(typeof(LogicalModelRevision)),
                revision,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            foreach (LogicalModelRoute route in routes)
            {
                route.LogicalModelRevisionID = revision.ID;
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert(typeof(LogicalModelRoute)),
                    route,
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await ExecuteAsync(
                connection,
                "UPDATE \"logical_models\" SET \"active_revision_id\" = @ActiveRevisionID, \"updated_at\" = @UpdatedAt WHERE \"id\" = @ID",
                new { ActiveRevisionID = revision.ID, item.UpdatedAt, item.ID },
                transaction,
                cancellationToken).ConfigureAwait(false);

            item.ActiveRevisionID = revision.ID;
            item.RevisionSequence = revision.Version;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>归档前台模型的失败原因。对应 Go 的 <c>gorm.ErrRecordNotFound</c> / <c>ErrLogicalModelInUse</c>。</summary>
    public enum ArchiveLogicalModelOutcome
    {
        Ok,
        NotFound,
        InUse,
    }

    /// <summary>
    /// 归档前台模型：锁定主体、检查活动任务、置 archived_at 并写入审计，必须原子完成。
    /// 对应 Go: <c>ArchiveLogicalModel</c>。
    /// </summary>
    public async Task<ArchiveLogicalModelOutcome> ArchiveLogicalModelAsync(
        string id,
        AdminAuditEvent? audit,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            // PostgreSQL 下锁定主体，使任务创建与归档在同一行上串行，避免检查后仍写入新任务。
            string lockClause = Dialect.ForUpdate();
            LogicalModel? item = await FirstOrDefaultAsync<LogicalModel>(
                connection,
                SqlBuilder.Select<LogicalModel>("id = @id AND archived_at IS NULL", limitOffset: " LIMIT 1") + lockClause,
                new { id },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                return ArchiveLogicalModelOutcome.NotFound;
            }

            long activeTasks = await ScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM \"tasks\" WHERE \"logical_model_id\" = @id AND \"status\" IN @statuses",
                new
                {
                    id,
                    statuses = new[]
                    {
                        TaskStatuses.TaskStatusQueued,
                        TaskStatuses.TaskStatusRunning,
                    },
                },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (activeTasks > 0)
            {
                return ArchiveLogicalModelOutcome.InUse;
            }

            int archived = await ExecuteAsync(
                connection,
                "UPDATE \"logical_models\" SET \"enabled\" = @disabled, \"archived_at\" = @now, \"updated_at\" = @now WHERE \"id\" = @id AND \"archived_at\" IS NULL",
                new { disabled = Dialect.Boolean(false), now, id },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (archived != 1)
            {
                return ArchiveLogicalModelOutcome.NotFound;
            }

            if (audit is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert(typeof(AdminAuditEvent)),
                    audit,
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            return ArchiveLogicalModelOutcome.Ok;
        }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>任务状态常量别名。对应 Go: <c>model.TaskStatus*</c>。</summary>
internal static class TaskStatuses
{
    public const string TaskStatusQueued = "queued";
    public const string TaskStatusRunning = "running";
}
