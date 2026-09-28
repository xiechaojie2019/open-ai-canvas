using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Dapper;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Tools.Commands;

/// <summary>对应 Go <c>cmd/migrate-channel-model-price-tiers</c> 的保守最小版本。</summary>
internal static class ChannelModelPriceTiersCommand
{
    private static readonly string[] FamilyCodes =
    [
        "seedance-2-5", "seedance-2-0", "seedance-2-0-mini", "seedance-2-0-fast",
        "grok-video-1-5", "artdance-2-0", "artdance-2-5", "artdance-2-mini", "artdance-fast",
    ];

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        bool apply = ToolEnvironment.HasFlag(args, "--apply");
        await using ToolDatabase tools = ToolDatabase.Open("postgres");
        ToolEnvironment.RequirePostgres(tools.Database, "migrate-channel-model-price-tiers");
        await ToolEnvironment.EnsureMigratedAsync(tools.Database, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<LogicalModel> models = await tools.Repository.LogicalModelsAsync(true, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, LogicalModelGraph> graphs = await tools.Repository.LogicalModelGraphsAsync(models, true, cancellationToken).ConfigureAwait(false);
        List<Plan> plans = [];
        foreach (string code in FamilyCodes)
        {
            LogicalModel? logical = models.FirstOrDefault(item => item.Code == code && item.ArchivedAt is null);
            if (logical is null) throw new InvalidOperationException($"缺少前台模型家族 {code}");
            if (!graphs.TryGetValue(logical.ID, out LogicalModelGraph? graph) || graph.Routes.Count == 0 || graph.ChannelModels.Count == 0)
            {
                throw new InvalidOperationException($"前台模型 {code} 没有当前供应线路");
            }
            ChannelModel canonical = graph.ChannelModels
                .FirstOrDefault(model => model.ID == logical.SourceChannelModelID)
                ?? graph.ChannelModels.First();
            List<ChannelModelPriceTier> tiers;
            if (canonical.PriceTiers.Count > 0)
            {
                tiers = canonical.PriceTiers.Where(tier => tier.Enabled && tier.PriceConfigured).ToList();
            }
            else if (canonical.PriceConfigured)
            {
                tiers = [await DefaultTierAsync(tools.Repository, canonical, cancellationToken).ConfigureAwait(false)];
            }
            else
            {
                tiers = [];
            }
            if (tiers.Count == 0) throw new InvalidOperationException($"系统模型 {canonical.ModelKey} 没有可迁移价格档");
            plans.Add(new Plan(logical, canonical, tiers));
            Console.WriteLine($"{code}: 系统模型={canonical.ModelKey} 价格档={tiers.Count}");
        }

        if (!apply)
        {
            Console.WriteLine("dry-run 完成；确认后使用 --apply 写入。历史任务与账务快照不会改写");
            return 0;
        }

        await EnsureNoActiveTasksAsync(tools.Database, plans, cancellationToken).ConfigureAwait(false);
        foreach (Plan plan in plans)
        {
            await tools.Repository.SaveChannelModelWithPriceTiersAsync(
                plan.ChannelModel, plan.Tiers, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"已同步 {plan.LogicalModel.Code} 到系统模型 {plan.ChannelModel.ModelKey}");
        }
        Console.WriteLine("价格档迁移完成：新任务按系统模型价格档结算");
        return 0;
    }

    private static async Task<ChannelModelPriceTier> DefaultTierAsync(
        Repository repository,
        ChannelModel model,
        CancellationToken cancellationToken)
    {
        string id = await repository.NextPrefixedIdAsync("PTIER", cancellationToken).ConfigureAwait(false);
        return new ChannelModelPriceTier
        {
            ID = id,
            ChannelModelID = model.ID,
            SelectorKey = "{}",
            SelectorJSON = "{}",
            Resolution = "*",
            ProviderModelKey = model.ProviderModelKey,
            BillingMode = model.BillingMode,
            UnitPriceMicrocredits = model.UnitPriceMicrocredits,
            InputTokenPriceMicrocredits = model.InputTokenPriceMicrocredits,
            OutputTokenPriceMicrocredits = model.OutputTokenPriceMicrocredits,
            CachedTokenPriceMicrocredits = model.CachedTokenPriceMicrocredits,
            PriceConfigured = model.PriceConfigured,
            Enabled = model.Enabled,
            PriceVersion = model.PriceVersion,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private static async Task EnsureNoActiveTasksAsync(
        CanvasDatabase database,
        IReadOnlyList<Plan> plans,
        CancellationToken cancellationToken)
    {
        string[] logicalIds = plans.Select(plan => plan.LogicalModel.ID).ToArray();
        string[] channelIds = plans.Select(plan => plan.ChannelModel.ID).ToArray();
        await using DbConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        long active = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM tasks WHERE (logical_model_id IN @logicalIds OR channel_model_id IN @channelIds) AND status IN @statuses",
            new { logicalIds, channelIds, statuses = new[] { OpenAICanvas.Domain.Entities.TaskStatus.TaskStatusQueued, OpenAICanvas.Domain.Entities.TaskStatus.TaskStatusRunning } },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (active > 0) throw new InvalidOperationException($"仍有 {active} 个排队或运行中的任务引用迁移模型，稍后重试");
    }

    private sealed record Plan(LogicalModel LogicalModel, ChannelModel ChannelModel, List<ChannelModelPriceTier> Tiers);
}
