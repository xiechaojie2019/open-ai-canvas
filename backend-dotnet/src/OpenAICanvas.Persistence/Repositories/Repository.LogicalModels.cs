#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 前台模型（逻辑模型）仓储方法。对应 Go: <c>repository/logical_models.go</c> 读路径子集。
/// </summary>
/// <remarks>
/// <see cref="LogicalModel"/>、<see cref="LogicalModelRevision"/>、<see cref="LogicalModelRoute"/>
/// 三张表没有软删除；<c>channel_models</c> / <c>model_channels</c> / <c>channel_model_price_tiers</c>
/// 有，相关查询必须经 <see cref="SoftDelete"/> 注入过滤。
/// </remarks>
public sealed partial class Repository
{
    /// <summary>
    /// 前台模型列表（未归档，sort_order asc, created_at asc）。
    /// 对应 Go: <c>LogicalModels(includeDisabled)</c>。
    /// </summary>
    public async Task<IReadOnlyList<LogicalModel>> LogicalModelsAsync(
        bool includeDisabled,
        CancellationToken cancellationToken = default)
    {
        string condition = includeDisabled
            ? "archived_at IS NULL"
            : "archived_at IS NULL AND enabled = @enabled";

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<LogicalModel>(
            connection,
            SqlBuilder.Select<LogicalModel>(condition, "sort_order ASC, created_at ASC, id ASC"),
            new { enabled = Dialect.Boolean(true) },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 查前台模型（含已归档）。对应 Go: <c>LogicalModel(id)</c>。</summary>
    public async Task<LogicalModel?> LogicalModelAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<LogicalModel>(
            connection,
            SqlBuilder.Select<LogicalModel>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 查前台模型版本。对应 Go: <c>LogicalModelRevision(id)</c>。</summary>
    public async Task<LogicalModelRevision?> LogicalModelRevisionAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<LogicalModelRevision>(
            connection,
            SqlBuilder.Select<LogicalModelRevision>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量加载前台模型的当前 revision、供应线路和渠道模型（含价格档）。
    /// 对应 Go: <c>LogicalModelGraphs</c>。管理列表与路由目录共用，避免 N+1。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, LogicalModelGraph>> LogicalModelGraphsAsync(
        IReadOnlyList<LogicalModel> items,
        bool includeDisabled,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, LogicalModelGraph> graphs = new(StringComparer.Ordinal);
        if (items.Count == 0)
        {
            return graphs;
        }

        foreach (LogicalModel item in items)
        {
            graphs[item.ID] = new LogicalModelGraph { Model = item };
        }

        List<string> revisionIDs = [];
        foreach (LogicalModel item in items)
        {
            if (item.ActiveRevisionID.Length > 0)
            {
                revisionIDs.Add(item.ActiveRevisionID);
            }
        }

        if (revisionIDs.Count == 0)
        {
            return graphs;
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        List<string> uniqueRevisionIDs = UniqueStrings(revisionIDs);
        Dictionary<string, LogicalModelRevision> revisionByID = new(StringComparer.Ordinal);
        foreach (LogicalModelRevision revision in await QueryAsync<LogicalModelRevision>(
                     connection,
                     SqlBuilder.Select<LogicalModelRevision>("id IN @ids"),
                     new { ids = uniqueRevisionIDs },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            revisionByID[revision.ID] = revision;
        }

        List<string> routeRevisionIDs = [];
        foreach (LogicalModel item in items)
        {
            if (!revisionByID.TryGetValue(item.ActiveRevisionID, out LogicalModelRevision? revision))
            {
                continue;
            }
            graphs[item.ID].Revision = revision;
            routeRevisionIDs.Add(revision.ID);
        }

        if (routeRevisionIDs.Count > 0)
        {
            string routeCondition = includeDisabled
                ? "logical_model_revision_id IN @ids"
                : "logical_model_revision_id IN @ids AND enabled = @enabled AND weight > @weight";
            object routeParameters = includeDisabled
                ? (object)new { ids = UniqueStrings(routeRevisionIDs) }
                : new { ids = UniqueStrings(routeRevisionIDs), enabled = Dialect.Boolean(true), weight = 0 };

            Dictionary<string, List<LogicalModelRoute>> routesByRevision = new(StringComparer.Ordinal);
            List<string> channelModelIDs = [];
            foreach (LogicalModelRoute route in await QueryAsync<LogicalModelRoute>(
                         connection,
                         SqlBuilder.Select<LogicalModelRoute>(routeCondition, "priority DESC, created_at ASC, id ASC"),
                         routeParameters,
                         cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (!routesByRevision.TryGetValue(route.LogicalModelRevisionID, out List<LogicalModelRoute>? list))
                {
                    routesByRevision[route.LogicalModelRevisionID] = list = [];
                }
                list.Add(route);
                channelModelIDs.Add(route.ChannelModelID);
            }

            IReadOnlyList<ChannelModel> channelModels = await ChannelModelsByIDsAsync(
                UniqueStrings(channelModelIDs), cancellationToken).ConfigureAwait(false);
            Dictionary<string, ChannelModel> channelModelByID = new(StringComparer.Ordinal);
            foreach (ChannelModel channelModel in channelModels)
            {
                channelModelByID[channelModel.ID] = channelModel;
            }

            foreach (LogicalModel item in items)
            {
                LogicalModelGraph graph = graphs[item.ID];
                if (graph.Revision is null)
                {
                    continue;
                }
                graph.Routes = routesByRevision.TryGetValue(graph.Revision.ID, out List<LogicalModelRoute>? routes)
                    ? routes
                    : [];
                graph.ChannelModels = [];
                HashSet<string> seenChannelModels = new(StringComparer.Ordinal);
                foreach (LogicalModelRoute route in graph.Routes)
                {
                    if (channelModelByID.TryGetValue(route.ChannelModelID, out ChannelModel? channelModel) &&
                        seenChannelModels.Add(channelModel.ID))
                    {
                        graph.ChannelModels.Add(channelModel);
                    }
                }
            }
        }

        return graphs;
    }

    /// <summary>批量查渠道模型（含价格档，强制软删除过滤）。对应 Go: <c>ChannelModelsByIDs</c>。</summary>
    public async Task<IReadOnlyList<ChannelModel>> ChannelModelsByIDsAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ChannelModel> items = await QueryAsync<ChannelModel>(
            connection,
            SqlBuilder.Select<ChannelModel>(SoftDelete.Apply("channel_models", "id IN @ids")),
            new { ids },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await AttachChannelModelPriceTiersAsync(items, cancellationToken).ConfigureAwait(false);
        return items;
    }

    /// <summary>
    /// 系统渠道批量查询（scope=system，强制软删除过滤）。
    /// 对应 Go: <c>SystemChannelsByIDs</c>。
    /// </summary>
    public async Task<IReadOnlyList<ModelChannel>> SystemChannelsByIDsAsync(
        IReadOnlyList<string> ids,
        bool includeDisabled,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        string condition = includeDisabled
            ? "id IN @ids AND scope = @scope"
            : "id IN @ids AND scope = @scope AND enabled = @enabled";

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ModelChannel>(
            connection,
            SqlBuilder.Select<ModelChannel>(
                SoftDelete.Apply("model_channels", condition)),
            new
            {
                ids = UniqueStrings(ids),
                scope = "system",
                enabled = Dialect.Boolean(true),
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 将价格档附着到已查询出的渠道模型。
    /// 对应 Go: <c>attachChannelModelPriceTiers</c>（含旧库默认档兜底）。
    /// </summary>
    public async Task AttachChannelModelPriceTiersAsync(
        IReadOnlyList<ChannelModel> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return;
        }

        List<string> ids = items.Select(item => item.ID).ToList();

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // GORM 对带 DeletedAt 的模型自动过滤；price tier 是软删除表，这里必须显式注入。
        IReadOnlyList<ChannelModelPriceTier> tiers = await QueryAsync<ChannelModelPriceTier>(
            connection,
            SqlBuilder.Select<ChannelModelPriceTier>(
                SoftDelete.Apply("channel_model_price_tiers", "channel_model_id IN @ids"),
                "selector_key ASC, created_at ASC, id ASC"),
            new { ids },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        Dictionary<string, List<ChannelModelPriceTier>> tiersByModelID = new(StringComparer.Ordinal);
        foreach (ChannelModelPriceTier tier in tiers)
        {
            // Go 的 attach 同时填充 Selector 瞬态字段（管理端响应会输出）。
            tier.Selector = SkuSelectorJson.Decode(tier.SelectorJSON);
            if (!tiersByModelID.TryGetValue(tier.ChannelModelID, out List<ChannelModelPriceTier>? list))
            {
                tiersByModelID[tier.ChannelModelID] = list = [];
            }
            list.Add(tier);
        }

        foreach (ChannelModel item in items)
        {
            item.PriceTiers = tiersByModelID.TryGetValue(item.ID, out List<ChannelModelPriceTier>? list)
                ? list
                : [];
            // 兼容尚未执行价格档回填的旧数据库；正式迁移会将同一数据持久化为默认档。
            if (item.PriceTiers.Count == 0 && item.PriceConfigured)
            {
                item.PriceTiers =
                [
                    new ChannelModelPriceTier
                    {
                        ChannelModelID = item.ID,
                        SelectorKey = "{}",
                        SelectorJSON = "{}",
                        Resolution = "*",
                        ProviderModelKey = item.ProviderModelKey,
                        BillingMode = item.BillingMode,
                        UnitPriceMicrocredits = item.UnitPriceMicrocredits,
                        InputTokenPriceMicrocredits = item.InputTokenPriceMicrocredits,
                        OutputTokenPriceMicrocredits = item.OutputTokenPriceMicrocredits,
                        CachedTokenPriceMicrocredits = item.CachedTokenPriceMicrocredits,
                        PriceConfigured = item.PriceConfigured,
                        Enabled = item.Enabled,
                        PriceVersion = item.PriceVersion,
                    },
                ];
            }
        }
    }

    /// <summary>对应 Go: <c>uniqueStrings</c>。去重并丢弃空串，保持首次出现顺序。</summary>
    internal static List<string> UniqueStrings(IEnumerable<string> values)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string value in values)
        {
            if (value.Length == 0 || !seen.Add(value))
            {
                continue;
            }
            result.Add(value);
        }
        return result;
    }
}

/// <summary>前台模型关系图。对应 Go: <c>repository.LogicalModelGraph</c>。</summary>
public sealed class LogicalModelGraph
{
    public LogicalModel Model { get; set; } = new();

    public LogicalModelRevision? Revision { get; set; }

    public List<LogicalModelRoute> Routes { get; set; } = [];

    public List<ChannelModel> ChannelModels { get; set; } = [];
}

/// <summary>SKU 选择器 JSON 解码（仓储层内部使用）。对应 Go: <c>model.DecodeSKUSelector</c>。</summary>
internal static class SkuSelectorJson
{
    public static Dictionary<string, string> Decode(string? raw)
    {
        Dictionary<string, string> selector = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return selector;
        }
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                raw,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is null)
            {
                return selector;
            }
            foreach ((string key, string value) in parsed)
            {
                selector[key] = value;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 与 Go 的 `_ = json.Unmarshal(...)` 一致：失败保留空表。
        }
        return selector;
    }
}
