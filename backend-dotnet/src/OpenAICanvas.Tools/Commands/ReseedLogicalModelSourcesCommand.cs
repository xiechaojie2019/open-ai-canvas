using System.Data.Common;
using System.Text.Json;
using Dapper;
using OpenAICanvas.Application;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Tools.Commands;

/// <summary>对应 Go <c>cmd/reseed-logical-model-sources</c>，默认只输出计划。</summary>
internal static class ReseedLogicalModelSourcesCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        bool apply = ToolEnvironment.HasFlag(args, "--apply");
        await using ToolDatabase tools = ToolDatabase.Open("postgres");
        ToolEnvironment.RequirePostgres(tools.Database, "reseed-logical-model-sources");
        await ToolEnvironment.EnsureMigratedAsync(tools.Database, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<LogicalModel> models = await tools.Repository.LogicalModelsAsync(
            includeDisabled: false, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, LogicalModelGraph> graphs = await tools.Repository.LogicalModelGraphsAsync(
            models, includeDisabled: false, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ModelChannel> channels = await tools.Repository.SystemChannelsAsync(
            includeDisabled: true, cancellationToken).ConfigureAwait(false);
        HashSet<string> systemChannelIds = channels.Select(channel => channel.ID).ToHashSet(StringComparer.Ordinal);
        List<SourcePlan> plans = [];
        foreach (LogicalModel model in models)
        {
            if (!string.IsNullOrWhiteSpace(model.SourceChannelModelID)) continue;
            if (!graphs.TryGetValue(model.ID, out LogicalModelGraph? graph) || graph.Revision is null ||
                graph.Routes.Count != 1 || graph.ChannelModels.Count != 1)
            {
                throw new InvalidOperationException($"前台模型 {model.Code} 不是单条有效系统线路，拒绝自动改写");
            }
            LogicalModelRoute route = graph.Routes[0];
            ChannelModel source = graph.ChannelModels[0];
            if (route.ChannelModelID != source.ID || !systemChannelIds.Contains(source.ChannelID))
            {
                throw new InvalidOperationException($"前台模型 {model.Code} 的当前线路不是系统渠道模型");
            }
            if (!source.Enabled || !source.PriceConfigured || source.PriceTiers.All(tier => !tier.Enabled || !tier.PriceConfigured))
            {
                throw new InvalidOperationException($"前台模型 {model.Code} 的系统模型未启用或未配置价格");
            }
            plans.Add(new SourcePlan(model, source));
        }
        plans.Sort((left, right) => string.CompareOrdinal(left.LogicalModel.Code, right.LogicalModel.Code));

        foreach (SourcePlan plan in plans)
        {
            Console.WriteLine($"{plan.LogicalModel.Code}: 前台模型={plan.LogicalModel.ID}，系统模型={plan.ChannelModel.ModelKey}，协议={plan.ChannelModel.Protocol}");
        }
        if (!apply)
        {
            Console.WriteLine("dry-run 完成；确认后使用 --apply 写入。未匹配单条系统线路的前台模型不会被修改");
            return 0;
        }

        await EnsureNoActiveTasksAsync(tools.Database, plans.Select(plan => plan.LogicalModel.ID), cancellationToken)
            .ConfigureAwait(false);
        User actor = ToolEnvironment.FindMigrationAdmin(await tools.Repository.UsersAsync(cancellationToken).ConfigureAwait(false));
        foreach (SourcePlan plan in plans)
        {
            await tools.Service.SaveAdminLogicalModelAsync(actor, plan.LogicalModel.ID, new LogicalModelRequest
            {
                Code = plan.LogicalModel.Code,
                Name = plan.LogicalModel.Name,
                Icon = plan.LogicalModel.Icon,
                Description = plan.LogicalModel.Description,
                Capability = plan.ChannelModel.Capability,
                Enabled = plan.LogicalModel.Enabled,
                SortOrder = plan.LogicalModel.SortOrder,
                LegacyModelIDs = DecodeLegacyModelIds(plan.LogicalModel.LegacyModelIDsJSON),
                SourceChannelModelID = plan.ChannelModel.ID,
            }, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"已刷新前台模型：{plan.LogicalModel.Code}");
        }
        Console.WriteLine("前台模型目录刷新完成：能力、默认参数、路线和价格均以系统渠道模型为准");
        return 0;
    }

    private static List<string> DecodeLegacyModelIds(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw, ToolEnvironment.JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task EnsureNoActiveTasksAsync(
        OpenAICanvas.Persistence.CanvasDatabase database,
        IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        string[] uniqueIds = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (uniqueIds.Length == 0) return;
        await using DbConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        long active = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM tasks WHERE logical_model_id IN @ids AND status IN @statuses",
            new { ids = uniqueIds, statuses = new[] { OpenAICanvas.Domain.Entities.TaskStatus.TaskStatusQueued, OpenAICanvas.Domain.Entities.TaskStatus.TaskStatusRunning } },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (active > 0) throw new InvalidOperationException($"存在 {active} 个 queued 或 running 前台模型任务，拒绝刷新");
    }

    private sealed record SourcePlan(LogicalModel LogicalModel, ChannelModel ChannelModel);
}
