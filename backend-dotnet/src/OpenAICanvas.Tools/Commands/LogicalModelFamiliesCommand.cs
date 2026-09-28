using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Dapper;
using OpenAICanvas.Application;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Tools.Commands;

/// <summary>对应 Go <c>cmd/migrate-logical-model-families</c>，默认只输出计划。</summary>
internal static class LogicalModelFamiliesCommand
{
    private static readonly FamilyDefinition[] Definitions =
    [
        new("seedance-2-5", "Seedance 2.5", "Seedance 2.5 视频生成，按所选分辨率自动路由并结算", ["doubao-seedance-2-5-480p", "doubao-seedance-2-5", "doubao-seedance-2-5-1080p"]),
        new("seedance-2-0", "Seedance 2.0", "Seedance 2.0 视频生成，按所选分辨率自动路由并结算", ["doubao-seedance-2-0-480p", "doubao-seedance-2-0", "doubao-seedance-2-0-1080p"]),
        new("seedance-2-0-mini", "Seedance 2.0 Mini", "Seedance 2.0 Mini 视频生成，按所选分辨率自动路由并结算", ["doubao-seedance-2-0-mini-480p", "doubao-seedance-2-0-mini"]),
        new("seedance-2-0-fast", "Seedance 2.0 Fast", "Seedance 2.0 Fast 视频生成，按所选分辨率自动路由并结算", ["doubao-seedance-2-0-fast-480p", "doubao-seedance-2-0-fast"]),
        new("grok-video-1-5", "Grok Imagine Video 1.5", "Grok Imagine Video 1.5，按所选分辨率自动路由并结算", ["grok-video-1.5-480p", "grok-video-1.5-720p", "grok-imagine-video-1.5-1080p"]),
        new("artdance-2-0", "Artdance 2.0", "Artdance 2.0 视频生成，按所选分辨率自动路由并结算", ["artdance-2-0-480p", "artdance-2-0-720p", "artdance-2-0-1080p"]),
        new("artdance-2-5", "Artdance 2.5", "Artdance 2.5 视频生成，按所选分辨率自动路由并结算", ["artdance-2-5-480p", "artdance-2-5-720p"]),
        new("artdance-2-mini", "Artdance 2 Mini", "Artdance 2 Mini 视频生成，按所选分辨率自动路由并结算", ["artdance-2-mini-480p", "artdance-2-mini-720p"]),
        new("artdance-fast", "Artdance Fast", "Artdance Fast 视频生成，按所选分辨率自动路由并结算", ["artdance-fast-480p", "artdance-fast-720p"]),
    ];

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        bool apply = ToolEnvironment.HasFlag(args, "--apply");
        await using ToolDatabase tools = ToolDatabase.Open("postgres");
        ToolEnvironment.RequirePostgres(tools.Database, "migrate-logical-model-families");
        await ToolEnvironment.EnsureMigratedAsync(tools.Database, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<LogicalModel> models = await tools.Repository.LogicalModelsAsync(
            includeDisabled: true, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, LogicalModelGraph> graphs = await tools.Repository.LogicalModelGraphsAsync(
            models, includeDisabled: true, cancellationToken).ConfigureAwait(false);
        Dictionary<string, LogicalModel> byCode = models.ToDictionary(model => model.Code, StringComparer.Ordinal);
        List<FamilyPlan> plans = [];
        foreach (FamilyDefinition definition in Definitions)
        {
            FamilyPlan plan = BuildPlan(definition, byCode, graphs);
            LogicalModel? existing = models.FirstOrDefault(model => model.Code == definition.Code && model.ArchivedAt is null);
            plan.Exists = existing is not null;
            plan.ExistingID = existing?.ID ?? string.Empty;
            plans.Add(plan);
            Console.WriteLine($"家族 {definition.Code}: 旧 SKU=[{string.Join(", ", plan.OldModels.Select(item => item.Code))}] 路由=[{string.Join(", ", plan.Routes.Select(item => item.ModelKey))}] 已存在={plan.Exists}");
        }

        await EnsureNoActiveTasksAsync(tools.Database, plans.SelectMany(plan => plan.OldModels).Select(model => model.ID), cancellationToken)
            .ConfigureAwait(false);
        if (!apply)
        {
            Console.WriteLine("dry-run 完成；确认无误后传入 --apply 执行写入");
            return 0;
        }

        User actor = ToolEnvironment.FindMigrationAdmin(await tools.Repository.UsersAsync(cancellationToken).ConfigureAwait(false));
        foreach (FamilyPlan plan in plans)
        {
            string familyId = plan.ExistingID;
            if (!plan.Exists)
            {
                AdminLogicalModelDto saved = await tools.Service.SaveAdminLogicalModelAsync(
                    actor, string.Empty, ToRequest(plan), cancellationToken).ConfigureAwait(false);
                familyId = saved.ID;
                Console.WriteLine($"已创建模型家族：{plan.Definition.Code}");
            }
            else
            {
                Console.WriteLine($"模型家族已存在，跳过创建：{plan.Definition.Code}");
            }

            foreach (LogicalModel old in plan.OldModels)
            {
                if (old.ID == familyId || old.ArchivedAt is not null)
                {
                    continue;
                }
                await tools.Service.DeleteAdminLogicalModelAsync(actor, old.ID, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"已归档旧 SKU 目录项：{old.Code}");
            }
        }
        Console.WriteLine("模型家族迁移完成；历史任务、账务和路由快照均未改写");
        return 0;
    }

    private static FamilyPlan BuildPlan(
        FamilyDefinition definition,
        IReadOnlyDictionary<string, LogicalModel> byCode,
        IReadOnlyDictionary<string, LogicalModelGraph> graphs)
    {
        List<LogicalModel> oldModels = [];
        List<RoutePlan> routes = [];
        List<CapabilitySpec> specs = [];
        List<Dictionary<string, JsonElement>> previousDefaults = [];
        long sortOrder = long.MaxValue;
        string icon = string.Empty;

        foreach (string code in definition.OldCodes)
        {
            if (!byCode.TryGetValue(code, out LogicalModel? old))
            {
                throw new InvalidOperationException($"模型家族 {definition.Code} 缺少旧 SKU 模型：{code}");
            }
            if (!graphs.TryGetValue(old.ID, out LogicalModelGraph? graph) || graph.Revision is null)
            {
                throw new InvalidOperationException($"旧 SKU 模型 {code} 缺少当前 revision");
            }
            LogicalModelRoute[] enabledRoutes = graph.Routes.Where(route => route.Enabled && route.Weight > 0).ToArray();
            if (enabledRoutes.Length != 1)
            {
                throw new InvalidOperationException($"旧 SKU 模型 {code} 需要恰好一条启用供应线路，当前为 {enabledRoutes.Length} 条");
            }
            ChannelModel? channelModel = graph.ChannelModels.FirstOrDefault(item => item.ID == enabledRoutes[0].ChannelModelID);
            if (channelModel is null || channelModel.Capability != "video")
            {
                throw new InvalidOperationException($"旧 SKU 模型 {code} 的启用供应线路不存在或不是视频模型");
            }

            ModelCapabilityConfig? config = ModelCapabilityConfigOps.DecodeModelCapabilityConfig(channelModel.CapabilityConfigJSON);
            config = ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
                channelModel.Capability,
                channelModel.Protocol,
                string.IsNullOrWhiteSpace(channelModel.ProviderModelKey) ? channelModel.ModelKey : channelModel.ProviderModelKey,
                config);
            CapabilitySpec spec = ModelCapabilityConfigOps.CapabilitySpecFromModelCapabilityConfig(config, channelModel.Capability);
            spec = CapabilitySpecPresets.WithPriceTiers(spec, channelModel);
            specs.Add(spec);
            previousDefaults.Add(DeserializeDefaults(graph.Revision.DefaultOptionsJSON));
            routes.Add(new RoutePlan(channelModel.ID, channelModel.ModelKey));
            oldModels.Add(old);
            sortOrder = Math.Min(sortOrder, old.SortOrder);
            if (icon.Length == 0)
            {
                icon = old.Icon;
            }
        }

        CapabilitySpec merged = MergeSpecs(specs);
        return new FamilyPlan(definition, oldModels, routes, merged, MergeDefaults(merged, previousDefaults),
            sortOrder == long.MaxValue ? 0 : sortOrder, icon);
    }

    private static CapabilitySpec MergeSpecs(IReadOnlyList<CapabilitySpec> specs)
    {
        CapabilitySpec first = specs[0];
        CapabilitySpec result = new()
        {
            Version = 1,
            Capability = first.Capability,
            Inputs = CapabilitySpecOps.SortedMap<InputConstraint>(),
            Options = CapabilitySpecOps.SortedMap<OptionConstraint>(),
        };
        HashSet<string> operations = new(StringComparer.Ordinal);
        foreach (CapabilitySpec spec in specs)
        {
            if (spec.Version != 1 || spec.Capability != result.Capability)
            {
                throw new InvalidOperationException("供应线路能力类型不一致");
            }
            foreach (string operation in spec.Operations ?? []) operations.Add(operation);
            foreach ((string name, InputConstraint constraint) in spec.Inputs ?? [])
            {
                if (result.Inputs.TryGetValue(name, out InputConstraint? current))
                {
                    current.Min = Math.Min(current.Min, constraint.Min);
                    current.Max = Math.Max(current.Max, constraint.Max);
                }
                else
                {
                    result.Inputs[name] = new InputConstraint { Min = constraint.Min, Max = constraint.Max };
                }
            }
        }
        result.Operations = operations.Count == 0 ? null : operations.Order(StringComparer.Ordinal).ToList();

        HashSet<string> optionNames = specs
            .SelectMany(spec => spec.Options is null ? Enumerable.Empty<string>() : spec.Options.Keys)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string name in optionNames.Order(StringComparer.Ordinal))
        {
            List<JsonElement> values = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (CapabilitySpec spec in specs)
            {
                if (spec.Options is null || !spec.Options.TryGetValue(name, out OptionConstraint? constraint)) continue;
                foreach (JsonElement value in ExpandValues(constraint, name))
                {
                    if (seen.Add(CapabilitySpecOps.NormalizedScalar(value))) values.Add(value);
                }
            }
            if (values.Count > 0)
            {
                values = values.OrderBy(value => ScalarSortKey(value), StringComparer.Ordinal).ToList();
                result.Options[name] = new OptionConstraint { Values = values };
            }
        }
        return CapabilitySpecOps.NormalizeCapabilitySpec(result);
    }

    private static IReadOnlyList<JsonElement> ExpandValues(OptionConstraint constraint, string name)
    {
        if (constraint.Values is { Count: > 0 }) return constraint.Values;
        if (constraint.Min is null || constraint.Max is null || constraint.Step is null || constraint.Step <= 0)
        {
            throw new InvalidOperationException($"参数 {name} 无法展开数值范围");
        }
        int count = (int)Math.Floor((constraint.Max.Value - constraint.Min.Value) / constraint.Step.Value + 1e-9) + 1;
        if (count is < 1 or > 1000) throw new InvalidOperationException($"参数 {name} 数值范围过大，拒绝自动合并");
        List<JsonElement> values = new(count);
        for (int index = 0; index < count; index++)
        {
            double number = constraint.Min.Value + index * constraint.Step.Value;
            values.Add(JsonSerializer.SerializeToElement(number));
        }
        return values;
    }

    private static string ScalarSortKey(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out double number) => "0:" + number.ToString("R", CultureInfo.InvariantCulture),
        JsonValueKind.String => "1:" + value.GetString(),
        JsonValueKind.True => "2:true",
        JsonValueKind.False => "2:false",
        _ => "3:" + value.GetRawText(),
    };

    private static Dictionary<string, JsonElement> MergeDefaults(
        CapabilitySpec spec,
        IReadOnlyList<Dictionary<string, JsonElement>> previous)
    {
        Dictionary<string, JsonElement> result = CapabilitySpecOps.SortedMap<JsonElement>();
        foreach ((string name, OptionConstraint constraint) in spec.Options ?? [])
        {
            if (name == "vquality")
            {
                JsonElement? preferred = constraint.Values?.FirstOrDefault(value =>
                    string.Equals(CapabilitySpecOps.NormalizedScalar(value), "720p", StringComparison.OrdinalIgnoreCase));
                if (preferred is { ValueKind: not JsonValueKind.Undefined })
                {
                    result[name] = preferred.Value;
                    continue;
                }
            }
            foreach (Dictionary<string, JsonElement> defaults in previous)
            {
                if (defaults.TryGetValue(name, out JsonElement value) &&
                    CapabilitySpecOps.MatchOptionConstraint(name, constraint, value))
                {
                    result[name] = value;
                    break;
                }
            }
            if (!result.ContainsKey(name) && constraint.Values is { Count: > 0 }) result[name] = constraint.Values[0];
        }
        return result;
    }

    private static Dictionary<string, JsonElement> DeserializeDefaults(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw, ToolEnvironment.JsonOptions)
                   ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("旧 SKU 默认参数 JSON 无效", error);
        }
    }

    private static LogicalModelRequest ToRequest(FamilyPlan plan) => new()
    {
        Code = plan.Definition.Code,
        Name = plan.Definition.Name,
        Icon = plan.Icon,
        Description = plan.Definition.Description,
        Capability = "video",
        Enabled = true,
        SortOrder = plan.SortOrder,
        PricePolicy = "channel",
        BillingMode = "fixed_request",
        LegacyModelIDs = plan.OldModels.Select(model => model.ID).ToList(),
        CapabilitySpec = plan.Spec,
        DefaultOptions = plan.Defaults,
        Routes = plan.Routes.Select(route => new LogicalRouteRequest
        {
            ChannelModelID = route.ChannelModelID,
            Enabled = true,
            Weight = 1,
        }).ToList(),
    };

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
            new { ids = uniqueIds, statuses = new[] { "queued", "running" } },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (active > 0) throw new InvalidOperationException($"存在 {active} 个 queued 或 running 旧 SKU 任务，拒绝归档");
    }

    private sealed record FamilyDefinition(string Code, string Name, string Description, string[] OldCodes);
    private sealed record RoutePlan(string ChannelModelID, string ModelKey);

    private sealed class FamilyPlan(
        FamilyDefinition definition,
        List<LogicalModel> oldModels,
        List<RoutePlan> routes,
        CapabilitySpec spec,
        Dictionary<string, JsonElement> defaults,
        long sortOrder,
        string icon)
    {
        public FamilyDefinition Definition { get; } = definition;
        public List<LogicalModel> OldModels { get; } = oldModels;
        public List<RoutePlan> Routes { get; } = routes;
        public CapabilitySpec Spec { get; } = spec;
        public Dictionary<string, JsonElement> Defaults { get; } = defaults;
        public long SortOrder { get; } = sortOrder;
        public string Icon { get; } = icon;
        public bool Exists { get; set; }
        public string ExistingID { get; set; } = string.Empty;
    }
}
