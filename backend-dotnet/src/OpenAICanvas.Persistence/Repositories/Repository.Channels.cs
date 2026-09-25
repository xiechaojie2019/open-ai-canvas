using System.Data.Common;
using System.Text.Json;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 渠道与渠道模型仓储方法。
/// 对应 Go 的 <c>repository/repository.go</c> 与 <c>repository/finance.go</c> 的渠道部分。
/// </summary>
/// <remarks>
/// <b>软删除</b>：<c>model_channels</c>、<c>channel_models</c>、<c>channel_model_price_tiers</c>
/// 三张表都带 <c>deleted_at</c>。GORM 会自动追加 <c>deleted_at IS NULL</c>，
/// Dapper 没有这个行为，因此这里所有查询都必须显式经 <see cref="RepositoryBase.Where"/>
/// 注入过滤，否则已删除的渠道会泄漏到前台。
/// </remarks>
public sealed partial class Repository
{
    /// <summary>按 ID 查启用系统渠道，并仅用于服务端代理读取凭证。</summary>
    public async Task<ModelChannel?> SystemChannelForProxyAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<ModelChannel>(
            connection,
            SqlBuilder.Select<ModelChannel>(
                SoftDelete.Apply("model_channels", "id = @id AND scope = @scope AND enabled = @enabled"),
                limitOffset: " LIMIT 1"),
            new { id, scope = "system", enabled = Dialect.Boolean(true) },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ModelChannel>> SystemChannelsAsync(
        bool includeDisabled,
        CancellationToken cancellationToken = default)
    {
        string condition = includeDisabled
            ? "scope = @scope"
            : "scope = @scope AND enabled = @enabled";

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ModelChannel>(
            connection,
            SqlBuilder.Select<ModelChannel>(
                SoftDelete.Apply("model_channels", condition),
                "sort_order ASC, created_at ASC, id ASC"),
            new { scope = "system", enabled = Dialect.Boolean(true) },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>系统渠道引用（只取 3 列）。对应 Go: <c>AdminSystemChannelReferences</c>。</summary>
    public async Task<IReadOnlyList<ModelChannel>> AdminSystemChannelReferencesAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ModelChannel>(
            connection,
            SqlBuilder.SelectColumns<ModelChannel>(
                ["ID", "Name", "Enabled"],
                SoftDelete.Apply("model_channels", "scope = @scope"),
                "created_at ASC"),
            new { scope = "system" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 查系统渠道。对应 Go: <c>SystemChannel(id)</c>（要求启用）。</summary>
    public async Task<ModelChannel?> SystemChannelAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<ModelChannel>(
            connection,
            SqlBuilder.Select<ModelChannel>(
                SoftDelete.Apply("model_channels", "id = @id AND scope = @scope AND enabled = @enabled"),
                limitOffset: " LIMIT 1"),
            new { id, scope = "system", enabled = Dialect.Boolean(true) },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 查系统渠道（不要求启用）。对应 Go: <c>AdminSystemChannel(id)</c>。</summary>
    public async Task<ModelChannel?> AdminSystemChannelAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<ModelChannel>(
            connection,
            SqlBuilder.Select<ModelChannel>(
                SoftDelete.Apply("model_channels", "id = @id AND scope = @scope"),
                limitOffset: " LIMIT 1"),
            new { id, scope = "system" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 只更新渠道展示字段（别名/排序）。局部语义：null 表示保留原值。
    /// 对应 Go: <c>UpdateSystemChannelPresentation</c>。
    /// </summary>
    public async Task<bool> UpdateSystemChannelPresentationAsync(
        string id,
        string? publicAlias,
        long? sortOrder,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            "UPDATE \"model_channels\" SET \"public_alias\" = COALESCE(@publicAlias, \"public_alias\"), \"sort_order\" = COALESCE(@sortOrder, \"sort_order\"), \"updated_at\" = @now WHERE \"id\" = @id AND \"scope\" = @scope AND \"deleted_at\" IS NULL",
            new { id, publicAlias, sortOrder, now, scope = "system" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>
    /// 渠道下的模型列表。对应 Go: <c>ChannelModels(channelID, includeDisabled)</c>。
    /// </summary>
    /// <param name="channelId">渠道 ID。</param>
    /// <param name="enabledOnly">是否只取启用项（Go 的参数名是 includeDisabled，取反）。</param>
    public async Task<IReadOnlyList<ChannelModel>> ChannelModelsAsync(
        string channelId,
        bool enabledOnly,
        CancellationToken cancellationToken = default)
    {
        string condition = enabledOnly
            ? "channel_id = @channelId AND enabled = @enabled"
            : "channel_id = @channelId";

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ChannelModel> items = await QueryAsync<ChannelModel>(
            connection,
            SqlBuilder.Select<ChannelModel>(
                SoftDelete.Apply("channel_models", condition),
                "sort_order ASC, created_at ASC, id ASC"),
            new { channelId, enabled = Dialect.Boolean(true) },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Go 的 ChannelModels 同样附着价格档（空档为空切片，json 无 omitempty）。
        await AttachChannelModelPriceTiersAsync(items, cancellationToken).ConfigureAwait(false);
        return items;
    }

    /// <summary>
    /// 管理后台渠道分页（关键字/状态过滤）。对应 Go: <c>AdminSystemChannels</c>。
    /// </summary>
    public async Task<(IReadOnlyList<ModelChannel> Channels, long Total)> AdminSystemChannelsAsync(
        string keyword,
        string status,
        long limit,
        long offset,
        CancellationToken cancellationToken = default)
    {
        List<string> conditions = [$"scope = @scope"];
        DynamicParameters parameters = new();
        parameters.Add("scope", "system");

        string trimmed = keyword.Trim();
        if (trimmed.Length > 0)
        {
            conditions.Add("(lower(name) LIKE @pattern OR lower(public_alias) LIKE @pattern OR lower(base_url) LIKE @pattern)");
            parameters.Add("pattern", "%" + trimmed.ToLowerInvariant() + "%");
        }
        if (status == "enabled")
        {
            conditions.Add("enabled = @enabled");
            parameters.Add("enabled", Dialect.Boolean(true));
        }
        else if (status == "disabled")
        {
            conditions.Add("enabled = @disabled");
            parameters.Add("disabled", Dialect.Boolean(false));
        }

        string where = string.Join(" AND ", conditions);

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long total = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"model_channels\" WHERE deleted_at IS NULL AND " + where,
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ModelChannel> channels = await QueryAsync<ModelChannel>(
            connection,
            SqlBuilder.Select<ModelChannel>(
                SoftDelete.Apply("model_channels", where),
                "sort_order ASC, created_at ASC, id ASC",
                Dialect.LimitOffset(limit, offset)),
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (channels, total);
    }

    /// <summary>
    /// 复制渠道：渠道主体 + 渠道模型 + 价格档同事务落库。
    /// 对应 Go: <c>CreateDuplicatedSystemChannel</c>。
    /// </summary>
    public async Task CreateDuplicatedSystemChannelAsync(
        ModelChannel channel,
        IReadOnlyList<ChannelModel> channelModels,
        IReadOnlyList<ChannelModelPriceTier> priceTiers,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                SqlBuilder.Insert(typeof(ModelChannel)), channel, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            foreach (ChannelModel item in channelModels)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert(typeof(ChannelModel)), item, transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            foreach (ChannelModelPriceTier tier in priceTiers)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert(typeof(ChannelModelPriceTier)), tier, transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量创建缺失的渠道模型；唯一键冲突时保留已有定价配置。
    /// 对应 Go: <c>CreateMissingChannelModels</c>（clause.OnConflict DoNothing）。
    /// </summary>
    public async Task<long> CreateMissingChannelModelsAsync(
        IReadOnlyList<ChannelModel> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return 0;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(ChannelModel), onConflictDoNothing: true),
            items,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected;
    }

    /// <summary>保存排序的结果。对应 Go 的 <c>ErrChannelOrderChanged</c>。</summary>
    public enum ChannelOrderOutcome
    {
        Ok,
        Changed,
    }

    /// <summary>
    /// 全量顺序在事务中保存；拒绝过期快照，避免覆盖另一管理员的调整。
    /// 对应 Go: <c>SaveChannelOrder</c>。
    /// </summary>
    public async Task<ChannelOrderOutcome> SaveChannelOrderAsync(
        string channelId,
        IReadOnlyList<string> ids,
        IReadOnlyList<string> expected,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        bool perChannel = channelId.Length > 0;
        string table = perChannel ? "channel_models" : "model_channels";
        string condition = perChannel
            ? SoftDelete.Apply(table, "channel_id = @channelId")
            : SoftDelete.Apply(table, "scope = @scope");
        object parameters = perChannel
            ? (object)new { channelId }
            : new { scope = "system" };

        return await InTransactionAsync(async (connection, transaction) =>
        {
            if (perChannel)
            {
                // 锁系统渠道主体，串行化同渠道的排序与模型增删（SQLite 无行锁，退化为存在性检查）。
                ModelChannel? channel = await FirstOrDefaultAsync<ModelChannel>(
                    connection,
                    SqlBuilder.SelectColumns<ModelChannel>(
                        ["ID"],
                        SoftDelete.Apply("model_channels", "id = @channelId AND scope = @scope"),
                        limitOffset: " LIMIT 1") + Dialect.ForUpdate(),
                    new { channelId, scope = "system" },
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                if (channel is null)
                {
                    throw new InvalidOperationException("record not found");
                }
            }

            // PostgreSQL 下先按稳定顺序锁定，再读取最新快照（Go 的同名处理）。
            if (Dialect.IsPostgres)
            {
                await QueryAsync<string>(
                    connection,
                    $"SELECT \"id\" FROM \"{table}\" WHERE {condition} ORDER BY \"id\" ASC" + Dialect.ForUpdate(),
                    parameters,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }

            List<string> current = (await QueryAsync<string>(
                connection,
                $"SELECT \"id\" FROM \"{table}\" WHERE {condition} ORDER BY \"sort_order\" ASC, \"created_at\" ASC, \"id\" ASC",
                parameters,
                transaction,
                cancellationToken).ConfigureAwait(false)).ToList();

            if (!current.SequenceEqual(expected) || ids.Count != current.Count)
            {
                return ChannelOrderOutcome.Changed;
            }

            HashSet<string> allowed = new(current, StringComparer.Ordinal);
            foreach (string id in ids)
            {
                // 重复 ID 或未知 ID 都视为快照过期。
                if (!allowed.Remove(id))
                {
                    return ChannelOrderOutcome.Changed;
                }
            }

            for (int index = 0; index < ids.Count; index++)
            {
                await ExecuteAsync(
                    connection,
                    $"UPDATE \"{table}\" SET \"sort_order\" = @sortOrder, \"updated_at\" = @now WHERE \"id\" = @id",
                    new { sortOrder = (long)index, now, id = ids[index] },
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }

            if (perChannel)
            {
                await RefreshChannelModelNamesAsync(connection, transaction, channelId, now, cancellationToken)
                    .ConfigureAwait(false);
            }
            return ChannelOrderOutcome.Ok;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按主键全量更新渠道模型（价格档另行保存）。对应 Go: <c>SaveChannelModel</c>。</summary>
    public async Task SaveChannelModelAsync(
        ChannelModel item,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            SqlBuilder.Update(typeof(ChannelModel)),
            item,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按渠道 + ID 查渠道模型（含价格档）。对应 Go: <c>ChannelModelByID</c>。</summary>
    public async Task<ChannelModel?> ChannelModelByIDAsync(
        string channelId,
        string id,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ChannelModel? item = await FirstOrDefaultAsync<ChannelModel>(
            connection,
            SqlBuilder.Select<ChannelModel>(
                SoftDelete.Apply("channel_models", "id = @id AND channel_id = @channelId"),
                limitOffset: " LIMIT 1"),
            new { id, channelId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }
        await AttachChannelModelPriceTiersAsync([item], cancellationToken).ConfigureAwait(false);
        return item;
    }

    /// <summary>
    /// 按渠道 + 模型键查渠道模型（含停用项，用于保存冲突检查）。
    /// 对应 Go: <c>ChannelModelByKeyIncludingDisabled</c>。
    /// </summary>
    public async Task<ChannelModel?> ChannelModelByKeyIncludingDisabledAsync(
        string channelId,
        string modelKey,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ChannelModel? item = await FirstOrDefaultAsync<ChannelModel>(
            connection,
            SqlBuilder.Select<ChannelModel>(
                SoftDelete.Apply("channel_models", "channel_id = @channelId AND model_key = @modelKey"),
                limitOffset: " LIMIT 1"),
            new { channelId, modelKey },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }
        await AttachChannelModelPriceTiersAsync([item], cancellationToken).ConfigureAwait(false);
        return item;
    }

    /// <summary>价格档键。对应 Go: <c>channelModelPriceTierKey</c>（SelectorKey 优先，否则按解析度/时长推导）。</summary>
    private static string ChannelModelPriceTierKey(ChannelModelPriceTier tier)
    {
        if (!string.IsNullOrWhiteSpace(tier.SelectorKey))
        {
            return tier.SelectorKey;
        }
        // 规范 JSON（key 字典序）与 Application.ModelSku.CanonicalSkuSelector 的输出一致；
        // Persistence 不能反向引用 Application，这里保持最小等价实现。
        var selector = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vquality"] = tier.Resolution.Trim(),
            ["videoSeconds"] = tier.VideoSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }
            .OrderBy(pair => pair.Key, StringComparer.Ordinal);
        string key = JsonSerializer.Serialize(
            new Dictionary<string, string>(selector, StringComparer.Ordinal));
        return key;
    }

    /// <summary>
    /// 原子保存系统模型与其活动价格档。移除价格档采用软删除，
    /// 让已结算订单的 PriceTierID 仍能回溯到原始配置版本。
    /// 对应 Go: <c>SaveChannelModelWithPriceTiers</c>。
    /// </summary>
    public async Task SaveChannelModelWithPriceTiersAsync(
        ChannelModel item,
        IReadOnlyList<ChannelModelPriceTier> tiers,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            IReadOnlyList<ChannelModelPriceTier> existing = await QueryAsync<ChannelModelPriceTier>(
                connection,
                SqlBuilder.Select<ChannelModelPriceTier>(
                    SoftDelete.Apply("channel_model_price_tiers", "channel_model_id = @id")),
                new { id = item.ID },
                transaction,
                cancellationToken).ConfigureAwait(false);

            Dictionary<string, ChannelModelPriceTier> existingByKey = new(StringComparer.Ordinal);
            foreach (ChannelModelPriceTier tier in existing)
            {
                existingByKey[ChannelModelPriceTierKey(tier)] = tier;
            }

            HashSet<string> selected = new(StringComparer.Ordinal);
            foreach (ChannelModelPriceTier tier in tiers)
            {
                tier.ChannelModelID = item.ID;
                string key = ChannelModelPriceTierKey(tier);
                if (existingByKey.TryGetValue(key, out ChannelModelPriceTier? existingTier))
                {
                    // 复用已有行的 ID 并递增价格版本，保证账单可回溯；保留原行创建时间。
                    tier.ID = existingTier.ID;
                    tier.PriceVersion = existingTier.PriceVersion + 1;
                    tier.CreatedAt = existingTier.CreatedAt;
                    await ExecuteAsync(
                        connection,
                        SqlBuilder.Update(typeof(ChannelModelPriceTier)),
                        tier,
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        SqlBuilder.Insert(typeof(ChannelModelPriceTier)),
                        tier,
                        transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                }
                selected.Add(tier.ID);
            }

            foreach (ChannelModelPriceTier tier in existing)
            {
                if (selected.Contains(tier.ID))
                {
                    continue;
                }
                await ExecuteAsync(
                    connection,
                    "UPDATE \"channel_model_price_tiers\" SET \"deleted_at\" = @now WHERE \"id\" = @id AND \"deleted_at\" IS NULL",
                    new { now = DateTime.UtcNow, id = tier.ID },
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }

            // GORM 的 Save 是 UpdateOrCreate：新建模型的 UPDATE 命中 0 行后回退为 INSERT。
            int itemUpdated = await ExecuteAsync(
                connection,
                SqlBuilder.Update(typeof(ChannelModel)),
                item,
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (itemUpdated == 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert(typeof(ChannelModel)),
                    item,
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量删除渠道模型：锁定主体、校验归属、拒绝活动引用（前台模型线路 / 进行中任务）、
    /// 停用并 bump 价格版本后软删除，最后刷新渠道 ModelsJSON。
    /// 对应 Go: <c>DeleteChannelModels</c>。
    /// </summary>
    public async Task<(bool Ok, long Deleted)> DeleteChannelModelsAsync(
        string channelId,
        IReadOnlyList<string> ids,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        List<string> uniqueIds = UniqueStrings(ids);
        if (uniqueIds.Count == 0)
        {
            throw new InvalidOperationException("record not found");
        }

        return await InTransactionAsync(async (connection, transaction) =>
        {
            // 锁系统渠道主体（SQLite 无行锁，退化为存在性检查）。
            ModelChannel? channel = await FirstOrDefaultAsync<ModelChannel>(
                connection,
                SqlBuilder.SelectColumns<ModelChannel>(
                    ["ID"],
                    SoftDelete.Apply("model_channels", "id = @channelId AND scope = @scope"),
                    limitOffset: " LIMIT 1") + Dialect.ForUpdate(),
                new { channelId, scope = "system" },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (channel is null)
            {
                throw new InvalidOperationException("record not found");
            }

            long existing = await ScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM \"channel_models\" WHERE \"channel_id\" = @channelId AND \"id\" IN @ids AND \"deleted_at\" IS NULL",
                new { channelId, ids = uniqueIds },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (existing != uniqueIds.Count)
            {
                throw new InvalidOperationException("record not found");
            }

            long activeReferences = await ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*) FROM "logical_model_routes" AS "route"
                JOIN "logical_models" AS "logical_model" ON "logical_model"."active_revision_id" = "route"."logical_model_revision_id"
                WHERE "route"."channel_model_id" IN @ids
                """,
                new { ids = uniqueIds },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (activeReferences > 0)
            {
                return (false, 0);
            }

            activeReferences = await ScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM \"tasks\" WHERE \"channel_model_id\" IN @ids AND \"status\" IN @statuses",
                new { ids = uniqueIds, statuses = new[] { "queued", "running" } },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (activeReferences > 0)
            {
                return (false, 0);
            }

            int disabled = await ExecuteAsync(
                connection,
                "UPDATE \"channel_models\" SET \"enabled\" = @disabled, \"price_version\" = \"price_version\" + 1, \"updated_at\" = @now WHERE \"id\" IN @ids AND \"channel_id\" = @channelId AND \"deleted_at\" IS NULL",
                new { disabled = Dialect.Boolean(false), now, ids = uniqueIds, channelId },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (disabled != uniqueIds.Count)
            {
                throw new InvalidOperationException("record not found");
            }

            await ExecuteAsync(
                connection,
                "UPDATE \"channel_models\" SET \"deleted_at\" = @now WHERE \"id\" IN @ids AND \"channel_id\" = @channelId AND \"deleted_at\" IS NULL",
                new { now, ids = uniqueIds, channelId },
                transaction,
                cancellationToken).ConfigureAwait(false);

            await RefreshChannelModelNamesAsync(connection, transaction, channelId, now, cancellationToken)
                .ConfigureAwait(false);
            return (true, disabled);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 在独立事务中刷新渠道 ModelsJSON（带主体锁）。
    /// 对应 Go: <c>SyncChannelModelNames</c>。
    /// </summary>
    public async Task SyncChannelModelNamesAsync(
        string channelId,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            ModelChannel? channel = await FirstOrDefaultAsync<ModelChannel>(
                connection,
                SqlBuilder.SelectColumns<ModelChannel>(
                    ["ID"],
                    SoftDelete.Apply("model_channels", "id = @channelId AND scope = @scope"),
                    limitOffset: " LIMIT 1") + Dialect.ForUpdate(),
                new { channelId, scope = "system" },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (channel is null)
            {
                throw new InvalidOperationException("record not found");
            }
            await RefreshChannelModelNamesAsync(connection, transaction, channelId, now, cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按主键全量更新渠道。对应 Go: <c>r.db.Save(&channel)</c>。</summary>
    public async Task SaveModelChannelAsync(
        ModelChannel channel,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            SqlBuilder.Update(typeof(ModelChannel)),
            channel,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除系统渠道：清空密钥并停用主体（保留供历史账单关联），渠道模型软删除。
    /// 对应 Go: <c>DeleteSystemChannel</c>。
    /// </summary>
    public async Task<bool> DeleteSystemChannelAsync(
        string id,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            int channelUpdated = await ExecuteAsync(
                connection,
                "UPDATE \"model_channels\" SET \"api_key\" = '', \"secret_key\" = '', \"enabled\" = @disabled, \"updated_at\" = @now WHERE \"id\" = @id AND \"scope\" = @scope AND \"deleted_at\" IS NULL",
                new { id, disabled = Dialect.Boolean(false), now, scope = "system" },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (channelUpdated != 1)
            {
                return false;
            }

            await ExecuteAsync(
                connection,
                "UPDATE \"channel_models\" SET \"enabled\" = @disabled, \"updated_at\" = @now WHERE \"channel_id\" = @id AND \"deleted_at\" IS NULL",
                new { id, disabled = Dialect.Boolean(false), now },
                transaction,
                cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                "UPDATE \"channel_models\" SET \"deleted_at\" = @now WHERE \"channel_id\" = @id AND \"deleted_at\" IS NULL",
                new { id, now },
                transaction,
                cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                "UPDATE \"model_channels\" SET \"deleted_at\" = @now WHERE \"id\" = @id AND \"scope\" = @scope AND \"deleted_at\" IS NULL",
                new { id, now, scope = "system" },
                transaction,
                cancellationToken).ConfigureAwait(false);

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 更新渠道模型排序：锁主体 → 更新排序 → 刷新渠道 ModelsJSON。
    /// 对应 Go: <c>UpdateChannelModelSort</c>。
    /// </summary>
    public async Task<bool> UpdateChannelModelSortAsync(
        string channelId,
        string modelId,
        long sortOrder,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            // 锁系统渠道主体（SQLite 无行锁，退化为存在性检查）。
            object lockParameters = new { channelId, scope = "system" };
            ModelChannel? channel = await FirstOrDefaultAsync<ModelChannel>(
                connection,
                SqlBuilder.SelectColumns<ModelChannel>(["ID"], SoftDelete.Apply("model_channels", "id = @channelId AND scope = @scope"), limitOffset: " LIMIT 1") + Dialect.ForUpdate(),
                lockParameters,
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (channel is null)
            {
                throw new InvalidOperationException("record not found");
            }

            int updated = await ExecuteAsync(
                connection,
                "UPDATE \"channel_models\" SET \"sort_order\" = @sortOrder, \"updated_at\" = @now WHERE \"channel_id\" = @channelId AND \"id\" = @modelId AND \"deleted_at\" IS NULL",
                new { sortOrder, now, channelId, modelId },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (updated == 0)
            {
                throw new InvalidOperationException("record not found");
            }

            await RefreshChannelModelNamesAsync(connection, transaction, channelId, now, cancellationToken);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 用启用的渠道模型键刷新渠道 ModelsJSON。
    /// 对应 Go: <c>refreshChannelModelNames</c> / <c>SyncChannelModelNames</c>。
    /// </summary>
    public async Task RefreshChannelModelNamesAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string channelId,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        List<string> names = (await QueryAsync<string>(
            connection,
            "SELECT \"model_key\" FROM \"channel_models\" WHERE \"channel_id\" = @channelId AND \"enabled\" = @enabled AND \"deleted_at\" IS NULL ORDER BY \"sort_order\" ASC, \"created_at\" ASC, \"id\" ASC",
            new { channelId, enabled = Dialect.Boolean(true) },
            transaction,
            cancellationToken).ConfigureAwait(false)).ToList();

        string encoded = JsonSerializer.Serialize(names);
        await ExecuteAsync(
            connection,
            "UPDATE \"model_channels\" SET \"models_json\" = @encoded, \"updated_at\" = @now WHERE \"id\" = @channelId AND \"scope\" = @scope",
            new { encoded, now, channelId, scope = "system" },
            transaction,
            cancellationToken).ConfigureAwait(false);
    }
}
