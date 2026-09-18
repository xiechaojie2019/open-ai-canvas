#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>前台模型保存请求。对应 Go: <c>app.LogicalModelRequest</c>。</summary>
public sealed class LogicalModelRequest
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("sortOrder")]
    public long SortOrder { get; set; }

    [JsonPropertyName("pricePolicy")]
    public string PricePolicy { get; set; } = "";

    [JsonPropertyName("billingMode")]
    public string BillingMode { get; set; } = "";

    [JsonPropertyName("unitPriceMicrocredits")]
    public long UnitPriceMicrocredits { get; set; }

    [JsonPropertyName("inputPriceMicrocredits")]
    public long InputPriceMicrocredits { get; set; }

    [JsonPropertyName("outputPriceMicrocredits")]
    public long OutputPriceMicrocredits { get; set; }

    [JsonPropertyName("cachedPriceMicrocredits")]
    public long CachedPriceMicrocredits { get; set; }

    /// <summary>
    /// 只用于将用户本地保存的旧目录选择迁移到当前模型家族，
    /// 不能用它重写任务、账单或路由尝试中的不可变快照。
    /// </summary>
    [JsonPropertyName("legacyModelIds")]
    public List<string>? LegacyModelIDs { get; set; }

    [JsonPropertyName("capabilitySpec")]
    public CapabilitySpec? CapabilitySpec { get; set; }

    [JsonPropertyName("defaultOptions")]
    public Dictionary<string, JsonElement>? DefaultOptions { get; set; }

    [JsonPropertyName("routes")]
    public List<LogicalRouteRequest>? Routes { get; set; }

    /// <summary>仅供系统渠道同步流程使用，前台模型不再拥有独立的能力和价格真相。对应 Go 的 <c>json:"-"</c>。</summary>
    [JsonIgnore]
    public string SourceChannelModelID { get; set; } = "";
}

public sealed class LogicalRouteRequest
{
    [JsonPropertyName("channelModelId")]
    public string ChannelModelID { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("priority")]
    public long Priority { get; set; }

    [JsonPropertyName("weight")]
    public long Weight { get; set; }
}

/// <summary>供应线路的管理视图。对应 Go: <c>app.AdminLogicalRoute</c>。</summary>
public sealed class AdminLogicalRouteDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("channelModelId")]
    public string ChannelModelID { get; init; } = "";

    [JsonPropertyName("channelId")]
    public string ChannelID { get; init; } = "";

    [JsonPropertyName("channelModelKey")]
    public string ChannelModelKey { get; init; } = "";

    [JsonPropertyName("channelModelName")]
    public string ChannelModelName { get; init; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("priority")]
    public long Priority { get; init; }

    [JsonPropertyName("weight")]
    public long Weight { get; init; }

    [JsonPropertyName("available")]
    public bool Available { get; init; }

    /// <summary>Go 的非导出字段 <c>structurallyAvailable</c>：只参与错误归因，不序列化。</summary>
    public bool StructurallyAvailable { get; init; }

    [JsonPropertyName("capabilitySpec")]
    public CapabilitySpec CapabilitySpec { get; init; } = new();
}

/// <summary>前台模型的管理视图。对应 Go: <c>app.AdminLogicalModel</c>。</summary>
/// <remarks>Go 以匿名内嵌 PublicLogicalModel，序列化时基类字段在前——与 STJ 继承顺序一致。</remarks>
public sealed class AdminLogicalModelDto : PublicLogicalModelDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("activeRevisionId")]
    public string ActiveRevisionID { get; init; } = "";

    [JsonPropertyName("revisionVersion")]
    public long RevisionVersion { get; init; }

    [JsonPropertyName("configurationError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfigurationError { get; init; }

    [JsonPropertyName("availabilityError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AvailabilityError { get; init; }

    [JsonPropertyName("routes")]
    public IReadOnlyList<AdminLogicalRouteDto> Routes { get; init; } = [];
}

/// <summary>
/// 前台模型管理端 CRUD。对应 Go: <c>logical_models.go</c> 的
/// AdminLogicalModels / SaveAdminLogicalModel / DeleteAdminLogicalModel 与校验函数。
/// </summary>
public sealed partial class LogicalModelService
{
    /// <summary>管理端前台模型列表。对应 Go: <c>AdminLogicalModels</c>。</summary>
    public async Task<IReadOnlyList<AdminLogicalModelDto>> AdminLogicalModelsAsync(
        User actor,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        IReadOnlyList<LogicalModel> items = await _repository.LogicalModelsAsync(true, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyDictionary<string, LogicalModelGraph> graphs = await _repository
            .LogicalModelGraphsAsync(items, true, cancellationToken).ConfigureAwait(false);

        List<string> systemChannelIds = [];
        foreach (LogicalModelGraph? graph in graphs.Values)
        {
            foreach (ChannelModel channelModel in graph.ChannelModels)
            {
                systemChannelIds.Add(channelModel.ChannelID);
            }
        }
        IReadOnlyList<ModelChannel> systemChannels = await _repository.SystemChannelsByIDsAsync(
            systemChannelIds, true, cancellationToken).ConfigureAwait(false);
        Dictionary<string, ModelChannel> systemChannelById = new(StringComparer.Ordinal);
        foreach (ModelChannel channel in systemChannels)
        {
            systemChannelById[channel.ID] = channel;
        }

        List<AdminLogicalModelDto> result = [];
        foreach (LogicalModel item in items)
        {
            if (!graphs.TryGetValue(item.ID, out LogicalModelGraph? graph) || graph.Revision is null)
            {
                continue;
            }
            result.Add(await BuildAdminLogicalModelAsync(item, graph, systemChannelById, cancellationToken)
                .ConfigureAwait(false));
        }
        return result;
    }

    /// <summary>对应 Go: <c>buildAdminLogicalModel</c>。</summary>
    /// <remarks>纯内存组装，没有真正的异步等待；保持 Task 返回类型以对齐调用方。</remarks>
    private async Task<AdminLogicalModelDto> BuildAdminLogicalModelAsync(
        LogicalModel item,
        LogicalModelGraph graph,
        Dictionary<string, ModelChannel> systemChannelById,
        CancellationToken cancellationToken)
    {
        CapabilitySpec productSpec = CapabilitySpecOps.DecodeCapabilitySpec(graph.Revision!.CapabilitySpecJSON);

        Dictionary<string, ChannelModel> channelModelById = new(StringComparer.Ordinal);
        foreach (ChannelModel channelModel in graph.ChannelModels)
        {
            channelModelById[channelModel.ID] = channelModel;
        }

        List<AdminLogicalRouteDto> routes = [];
        foreach (LogicalModelRoute route in graph.Routes)
        {
            if (!channelModelById.TryGetValue(route.ChannelModelID, out ChannelModel? channelModel))
            {
                // Go 返回裸 errors.New，由 failService 投影为 500 固定文案。
                throw new InvalidOperationException("供应线路引用的渠道模型不存在");
            }
            CapabilitySpec capabilitySpec = ChannelModelCapabilitySpec(channelModel);
            bool channelOk = systemChannelById.ContainsKey(channelModel.ChannelID);
            bool structurallyAvailable = route.Enabled && route.Weight > 0 && channelModel.Enabled && channelOk;
            bool billingAvailable = item.PricePolicy != "unified" || item.BillingMode != "token" ||
                                    ModelCapabilityConfigOps.SupportsTokenBilling(item.Capability, channelModel.Protocol);
            bool available = structurallyAvailable && billingAvailable &&
                             (item.PricePolicy != "channel" || channelModel.PriceConfigured);
            routes.Add(new AdminLogicalRouteDto
            {
                ID = route.ID,
                ChannelModelID = channelModel.ID,
                ChannelID = channelModel.ChannelID,
                ChannelModelKey = channelModel.ModelKey,
                ChannelModelName = channelModel.DisplayName,
                Enabled = route.Enabled,
                Priority = route.Priority,
                Weight = route.Weight,
                Available = available,
                StructurallyAvailable = structurallyAvailable,
                CapabilitySpec = capabilitySpec,
            });
        }

        List<CapabilitySpec> routeSpecs = routes.Select(route => route.CapabilitySpec).ToList();
        CapabilitySpec enriched = CapabilitySpecPresets.WithRoutePresets(productSpec, routeSpecs);
        Dictionary<string, JsonElement> defaults =
            CapabilitySpecOps.DecodeLogicalDefaults(graph.Revision.DefaultOptionsJSON, enriched);

        PublicLogicalModelDto publicProjection = PublicLogicalModel(new CachedLogicalModel
        {
            Model = item,
            Revision = graph.Revision,
            ProductSpec = enriched,
            Defaults = defaults,
        }, available: false);

        List<CapabilitySpec> structuralRouteSpecs = routes
            .Where(route => route.StructurallyAvailable)
            .Select(route => route.CapabilitySpec)
            .ToList();
        List<CapabilitySpec> settlementRouteSpecs = routes
            .Where(route => route.Available)
            .Select(route => route.CapabilitySpec)
            .ToList();
        string? configurationError = LogicalModelConfigurationError(enriched, structuralRouteSpecs);
        string? availabilityError = LogicalModelAvailabilityError(
            item.PricePolicy, enriched, structuralRouteSpecs, settlementRouteSpecs);

        await Task.CompletedTask.ConfigureAwait(false);
        return new AdminLogicalModelDto
        {
            ID = publicProjection.ID,
            Code = publicProjection.Code,
            Name = publicProjection.Name,
            Icon = publicProjection.Icon,
            Description = publicProjection.Description,
            Capability = publicProjection.Capability,
            SortOrder = publicProjection.SortOrder,
            PricePolicy = publicProjection.PricePolicy,
            PricingMode = publicProjection.PricingMode,
            DisplayPrice = publicProjection.DisplayPrice,
            PriceLabel = publicProjection.PriceLabel,
            BillingMode = publicProjection.BillingMode,
            UnitPriceMicrocredits = publicProjection.UnitPriceMicrocredits,
            InputPriceMicrocredits = publicProjection.InputPriceMicrocredits,
            OutputPriceMicrocredits = publicProjection.OutputPriceMicrocredits,
            CachedPriceMicrocredits = publicProjection.CachedPriceMicrocredits,
            PriceTiers = publicProjection.PriceTiers,
            LegacyModelIDs = publicProjection.LegacyModelIDs,
            CapabilitySpec = enriched,
            CapabilityProfiles = publicProjection.CapabilityProfiles,
            DefaultOptions = defaults,
            Available = settlementRouteSpecs.Count > 0 &&
                        configurationError is null && availabilityError is null,
            Enabled = item.Enabled,
            ActiveRevisionID = graph.Revision.ID,
            RevisionVersion = graph.Revision.Version,
            ConfigurationError = configurationError,
            AvailabilityError = availabilityError,
            Routes = routes,
        };
    }

    /// <summary>保存前台模型。对应 Go: <c>SaveAdminLogicalModel</c>。</summary>
    public async Task<AdminLogicalModelDto> SaveAdminLogicalModelAsync(
        User actor,
        string id,
        LogicalModelRequest request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        (LogicalModel item, LogicalModelRevision revision, List<LogicalModelRoute> routes, bool creating) =
            await LogicalModelBundleAsync(actor, id, request, cancellationToken).ConfigureAwait(false);

        await _repository.SaveLogicalModelBundleAsync(item, revision, routes, creating, cancellationToken)
            .ConfigureAwait(false);
        InvalidateRouteCatalog();

        // 模型、版本和路由属于后台关键配置，保存成功却缺失审计记录不能静默返回成功。
        var audit = new AdminAuditEvent
        {
            ID = IdGenerator.NewId(),
            ActorUserID = actor.ID,
            Action = creating ? "logical_model.create" : "logical_model.update",
            TargetType = "logical_model",
            TargetID = item.ID,
            Summary = "保存前台模型及供应线路",
            MetadataJSON = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["revisionId"] = revision.ID,
                ["routeCount"] = routes.Count,
            }),
            CreatedAt = DateTime.UtcNow,
        };
        await _repository.AppendAdminAuditAsync(audit, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, LogicalModelGraph> graphs = await _repository.LogicalModelGraphsAsync(
            [item], true, cancellationToken).ConfigureAwait(false);
        List<string> channelIds = graphs[item.ID].ChannelModels
            .Select(channelModel => channelModel.ChannelID)
            .ToList();
        IReadOnlyList<ModelChannel> systemChannels = await _repository.SystemChannelsByIDsAsync(
            channelIds, true, cancellationToken).ConfigureAwait(false);
        Dictionary<string, ModelChannel> systemChannelById = new(StringComparer.Ordinal);
        foreach (ModelChannel channel in systemChannels)
        {
            systemChannelById[channel.ID] = channel;
        }

        return await BuildAdminLogicalModelAsync(item, graphs[item.ID], systemChannelById, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>归档前台模型。对应 Go: <c>DeleteAdminLogicalModel</c>。</summary>
    public async Task DeleteAdminLogicalModelAsync(
        User actor,
        string id,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        LogicalModel? item = await _repository.LogicalModelAsync(id.Trim(), cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            throw AppError.BadAuthRequest("前台模型不存在或已删除");
        }
        if (item.ArchivedAt is not null)
        {
            throw AppError.BadAuthRequest("前台模型不存在或已删除");
        }
        if (item.SourceChannelModelID.Length > 0)
        {
            throw AppError.BadAuthRequest("该前台模型由系统渠道自动同步，请在系统渠道模型中停用");
        }

        var audit = new AdminAuditEvent
        {
            ID = IdGenerator.NewId(),
            ActorUserID = actor.ID,
            Action = "logical_model.archive",
            TargetType = "logical_model",
            TargetID = item.ID,
            Summary = "归档前台模型",
            MetadataJSON = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = item.Code,
                ["name"] = item.Name,
            }),
            CreatedAt = DateTime.UtcNow,
        };

        Repository.ArchiveLogicalModelOutcome outcome = await _repository.ArchiveLogicalModelAsync(
            item.ID, audit, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case Repository.ArchiveLogicalModelOutcome.InUse:
                throw AppError.BadAuthRequest("前台模型仍被排队中或进行中任务使用，请等待任务结束后再归档");
            case Repository.ArchiveLogicalModelOutcome.NotFound:
                throw AppError.BadAuthRequest("前台模型不存在或已删除");
        }

        InvalidateRouteCatalog();
    }

    // ------------------------------------------------------------ 保存校验（logicalModelBundle）

    /// <summary>对应 Go: <c>logicalModelBundle</c>。全部校验通过后返回待落库的三元组。</summary>
    private async Task<(LogicalModel Item, LogicalModelRevision Revision, List<LogicalModelRoute> Routes, bool Creating)>
        LogicalModelBundleAsync(
            User actor,
            string id,
            LogicalModelRequest request,
            CancellationToken cancellationToken)
    {
        string code = request.Code.Trim().ToLowerInvariant();
        string name = request.Name.Trim();
        string capability = CapabilitySpecOps.NormalizeCapability(request.Capability);
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, @"^[a-z0-9][a-z0-9._-]{1,79}$"))
        {
            throw AppError.BadAuthRequest("模型 code 需为 2-80 位小写字母、数字、点、下划线或连字符");
        }
        if (name.Length == 0 || name.Length > 120)
        {
            throw AppError.BadAuthRequest("请填写 1-120 个字符的模型名称");
        }

        string sourceChannelModelId = request.SourceChannelModelID.Trim();
        if (sourceChannelModelId.Length > 0)
        {
            ChannelModel? source = await _repository.ChannelModelAsync(sourceChannelModelId, cancellationToken)
                .ConfigureAwait(false);
            if (source is null)
            {
                throw AppError.BadAuthRequest("系统渠道模型不存在");
            }
            if (await _repository.AdminSystemChannelAsync(source.ChannelID, cancellationToken).ConfigureAwait(false)
                is null)
            {
                throw AppError.BadAuthRequest("前台模型只能同步系统渠道模型");
            }
            capability = CapabilitySpecOps.NormalizeCapability(source.Capability);
            CapabilitySpec derivedSpec = ChannelModelCapabilitySpec(source);
            Dictionary<string, JsonElement> derivedDefaults = ChannelModelDefaultOptions(source, derivedSpec);
            if (request.Routes is null || request.Routes.Count == 0)
            {
                request.CapabilitySpec = derivedSpec;
                request.DefaultOptions = derivedDefaults;
                request.Routes =
                [
                    new LogicalRouteRequest { ChannelModelID = source.ID, Enabled = true, Priority = 100, Weight = 100 },
                ];
            }
            request.PricePolicy = "channel";
            request.BillingMode = "fixed_request";
            request.UnitPriceMicrocredits = 0;
            request.InputPriceMicrocredits = 0;
            request.OutputPriceMicrocredits = 0;
            request.CachedPriceMicrocredits = 0;
            // 未完成定价的系统模型仅在后台目录保留同步记录，不能暴露到创作端。
            if (id.Trim().Length == 0)
            {
                request.Enabled = source.Enabled && ChannelModelHasActivePriceTier(source);
            }
        }

        CapabilitySpec normalizedSpec =
            CapabilitySpecOps.NormalizeCapabilitySpec(request.CapabilitySpec ?? new CapabilitySpec());
        request.CapabilitySpec = normalizedSpec;
        if (CapabilitySpecOps.NormalizeCapability(normalizedSpec.Capability) != capability)
        {
            throw AppError.BadAuthRequest("前台模型类型与能力配置不一致");
        }

        Dictionary<string, JsonElement> normalizedDefaults = CapabilitySpecOps.NormalizeLogicalDefaults(
            normalizedSpec, request.DefaultOptions ?? []);
        request.DefaultOptions = normalizedDefaults;

        if (request.UnitPriceMicrocredits < 0 || request.InputPriceMicrocredits < 0 ||
            request.OutputPriceMicrocredits < 0 || request.CachedPriceMicrocredits < 0)
        {
            throw AppError.BadAuthRequest("用户价格不能为负数");
        }

        string pricePolicy = request.PricePolicy.Trim();
        if (pricePolicy is not ("channel" or "unified"))
        {
            throw AppError.BadAuthRequest("请选择跟随供应价格或统一定价");
        }

        string billingMode = request.BillingMode.Trim();
        if (pricePolicy == "channel")
        {
            billingMode = "fixed_request";
            request.UnitPriceMicrocredits = 0;
            request.InputPriceMicrocredits = 0;
            request.OutputPriceMicrocredits = 0;
            request.CachedPriceMicrocredits = 0;
        }
        else if (billingMode is not ("fixed_request" or "per_second" or "token"))
        {
            throw AppError.BadAuthRequest("前台模型计费方式仅支持按次、按秒或 Token");
        }

        if (pricePolicy == "unified" && billingMode == "per_second" && capability != "video")
        {
            throw AppError.BadAuthRequest("只有视频前台模型可以按秒计费");
        }

        bool creating = id.Trim().Length == 0;
        LogicalModel item;
        if (creating)
        {
            string newId = await _repository.NextPrefixedIdAsync("LMODEL", cancellationToken).ConfigureAwait(false);
            item = new LogicalModel { ID = newId, CreatedAt = DateTime.UtcNow };
        }
        else
        {
            LogicalModel? existing = await _repository.LogicalModelAsync(id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                // Go 返回原始 gorm.ErrRecordNotFound，由 failService 投影为 500。
                throw new InvalidOperationException("record not found");
            }
            if (existing.ArchivedAt is not null)
            {
                throw AppError.BadAuthRequest("前台模型不存在或已删除");
            }
            item = existing;
        }

        item.Code = code;
        item.Name = name;
        item.Icon = request.Icon.Trim();
        item.Description = request.Description.Trim();
        item.Capability = capability;
        if (sourceChannelModelId.Length > 0)
        {
            item.SourceChannelModelID = sourceChannelModelId;
        }
        item.Enabled = request.Enabled;
        item.SortOrder = request.SortOrder;
        item.PricePolicy = pricePolicy;
        item.BillingMode = billingMode;
        item.UnitPriceMicrocredits = request.UnitPriceMicrocredits;
        item.InputPriceMicrocredits = request.InputPriceMicrocredits;
        item.OutputPriceMicrocredits = request.OutputPriceMicrocredits;
        item.CachedPriceMicrocredits = request.CachedPriceMicrocredits;
        if (request.LegacyModelIDs is not null)
        {
            IReadOnlyList<string> legacy = NormalizeLegacyModelIDs(request.LegacyModelIDs);
            item.LegacyModelIDsJSON = JsonSerializer.Serialize(legacy);
        }
        item.UpdatedAt = DateTime.UtcNow;

        string revisionId = await _repository.NextPrefixedIdAsync("REVISION", cancellationToken).ConfigureAwait(false);
        string specJson = JsonSerializer.Serialize(request.CapabilitySpec);
        string defaultsJson = JsonSerializer.Serialize(request.DefaultOptions);
        var revision = new LogicalModelRevision
        {
            ID = revisionId,
            LogicalModelID = item.ID,
            CapabilitySpecJSON = specJson,
            DefaultOptionsJSON = defaultsJson,
            CreatedBy = actor.ID,
            CreatedAt = DateTime.UtcNow,
        };

        List<LogicalModelRoute> routes = [];
        HashSet<string> seenChannelModels = new(StringComparer.Ordinal);
        List<CapabilitySpec> structuralRouteSpecs = [];
        List<CapabilitySpec> settlementRouteSpecs = [];
        List<string> enabledRouteProtocols = [];

        foreach (LogicalRouteRequest input in request.Routes ?? [])
        {
            string channelModelId = input.ChannelModelID.Trim();
            if (channelModelId.Length == 0 || !seenChannelModels.Add(channelModelId))
            {
                throw AppError.BadAuthRequest("供应线路必须选择不重复的渠道模型");
            }

            ChannelModel? channelModel = await _repository.ChannelModelAsync(channelModelId, cancellationToken)
                .ConfigureAwait(false);
            if (channelModel is null)
            {
                throw AppError.BadAuthRequest("供应线路引用的渠道模型不存在");
            }

            CapabilitySpec routeCapabilitySpec = ChannelModelCapabilitySpec(channelModel);
            if (CapabilitySpecOps.NormalizeCapability(routeCapabilitySpec.Capability) != capability)
            {
                throw AppError.BadAuthRequest("供应线路能力类型与前台模型不一致");
            }

            if (input.Enabled)
            {
                enabledRouteProtocols.Add(channelModel.Protocol);
            }
            if (request.Enabled && input.Enabled && input.Weight <= 0)
            {
                throw AppError.BadAuthRequest("启用供应线路的同级权重必须大于 0");
            }
            if (input.Weight < 0)
            {
                throw AppError.BadAuthRequest("供应线路的同级权重不能为负数");
            }
            if (await _repository.SystemChannelAsync(channelModel.ChannelID, cancellationToken).ConfigureAwait(false)
                is null)
            {
                throw AppError.BadAuthRequest("供应线路只能选择系统渠道模型");
            }

            if (request.Enabled && input.Enabled && channelModel.Enabled)
            {
                structuralRouteSpecs.Add(routeCapabilitySpec);
                if (pricePolicy != "channel" || channelModel.PriceConfigured)
                {
                    settlementRouteSpecs.Add(routeCapabilitySpec);
                }
            }

            string routeId = await _repository.NextPrefixedIdAsync("ROUTE", cancellationToken).ConfigureAwait(false);
            routes.Add(new LogicalModelRoute
            {
                ID = routeId,
                ChannelModelID = channelModel.ID,
                Enabled = input.Enabled,
                Priority = input.Priority,
                Weight = input.Weight,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        if (pricePolicy == "unified" && billingMode == "token" &&
            !SupportsLogicalModelTokenBilling(capability, enabledRouteProtocols))
        {
            throw AppError.BadAuthRequest("Token 计费仅支持文本前台模型，或全部启用供应线路均为火山方舟视频协议的视频前台模型");
        }

        // 停用必须始终可执行，便于管理员立即阻止失效线路继续对外服务；重新启用时再强校验结构能力和计费可用性。
        if (request.Enabled)
        {
            if (structuralRouteSpecs.Count == 0)
            {
                throw AppError.BadAuthRequest("启用前台模型前至少需要一条已启用的供应线路");
            }
            CapabilitySpecOps.ValidateProductSpecWithinRoutes(request.CapabilitySpec, structuralRouteSpecs);
            string? availabilityError = LogicalModelAvailabilityError(
                pricePolicy, request.CapabilitySpec, structuralRouteSpecs, settlementRouteSpecs);
            if (availabilityError is not null)
            {
                throw AppError.BadAuthRequest(availabilityError);
            }
        }

        return (item, revision, routes, creating);
    }

    /// <summary>对应 Go: <c>channelModelDefaultOptions</c>（系统渠道同步路径）。</summary>
    private static Dictionary<string, JsonElement> ChannelModelDefaultOptions(
        ChannelModel channelModel, CapabilitySpec spec)
    {
        string capability = CapabilitySpecOps.NormalizeCapability(channelModel.Capability);
        ModelCapabilityConfig? config = ModelCapabilityConfigOps.DecodeModelCapabilityConfig(
            channelModel.CapabilityConfigJSON);
        if (config is not null)
        {
            config = ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
                capability,
                channelModel.Protocol,
                FirstNonEmpty(channelModel.ProviderModelKey, channelModel.ModelKey),
                config);
        }

        Dictionary<string, JsonElement> defaults = CapabilitySpecOps.SortedMap<JsonElement>();
        if (config is not null)
        {
            switch (capability)
            {
                case "image" when config.Image is not null:
                    defaults["size"] = JsonSerializer.SerializeToElement(config.Image.Size.Default);
                    if (config.Image.Quality.Supported)
                    {
                        defaults["quality"] = JsonSerializer.SerializeToElement(config.Image.Quality.Default);
                    }
                    if (config.Image.TransparentBackground.Supported)
                    {
                        defaults["transparentBackground"] =
                            JsonSerializer.SerializeToElement(config.Image.TransparentBackground.Default);
                    }
                    break;
                case "video" when config.Video is not null:
                    defaults["videoSeconds"] = JsonSerializer.SerializeToElement(config.Video.Duration.Default);
                    defaults["vquality"] = JsonSerializer.SerializeToElement(
                        ModelCapabilityConfigOps.NormalizeChannelModelTierResolution(config.Video.DefaultResolution));
                    defaults["size"] = JsonSerializer.SerializeToElement(config.Video.DefaultRatio);
                    if (config.Video.GenerateAudio.Supported)
                    {
                        defaults["videoGenerateAudio"] =
                            JsonSerializer.SerializeToElement(config.Video.GenerateAudio.Default);
                    }
                    if (config.Video.Watermark.Supported)
                    {
                        defaults["videoWatermark"] = JsonSerializer.SerializeToElement(config.Video.Watermark.Default);
                    }
                    break;
            }
        }

        // 当渠道默认规格没有价格档时，优先选择第一个可结算档，确保初始选择可直接生成。
        if (capability == "video" &&
            ModelSku.ChannelModelPriceTierForIntent(
                channelModel,
                new ModelRequestIntent
                {
                    Capability = "video",
                    Options = defaults.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                }) is null)
        {
            foreach (ChannelModelPriceTier tier in channelModel.PriceTiers)
            {
                if (!tier.Enabled || !tier.PriceConfigured)
                {
                    continue;
                }
                if (tier.Resolution != "*")
                {
                    defaults["vquality"] = JsonSerializer.SerializeToElement(
                        ModelCapabilityConfigOps.NormalizeChannelModelTierResolution(tier.Resolution));
                }
                if (tier.VideoSeconds > 0)
                {
                    defaults["videoSeconds"] = JsonSerializer.SerializeToElement(tier.VideoSeconds);
                }
                break;
            }
        }

        return CapabilitySpecOps.NormalizeLogicalDefaults(spec, defaults);
    }

    /// <summary>对应 Go: <c>supportsLogicalModelTokenBilling</c>。</summary>
    private static bool SupportsLogicalModelTokenBilling(string capability, IReadOnlyList<string> enabledRouteProtocols)
    {
        if (capability == "text")
        {
            return true;
        }
        if (capability != "video" || enabledRouteProtocols.Count == 0)
        {
            return false;
        }
        foreach (string protocol in enabledRouteProtocols)
        {
            if (!ModelCapabilityConfigOps.SupportsTokenBilling(capability, protocol))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>对应 Go: <c>logicalModelConfigurationError</c>。</summary>
    private static string? LogicalModelConfigurationError(
        CapabilitySpec product, IReadOnlyList<CapabilitySpec> routeSpecs)
    {
        if (routeSpecs.Count == 0 || LogicalModelCapabilityCovered(product, routeSpecs))
        {
            return null;
        }
        return "供应线路已无法完整覆盖创作端能力，请调整线路或能力范围";
    }

    /// <summary>对应 Go: <c>logicalModelAvailabilityError</c>。</summary>
    private static string? LogicalModelAvailabilityError(
        string pricePolicy,
        CapabilitySpec product,
        IReadOnlyList<CapabilitySpec> structuralRouteSpecs,
        IReadOnlyList<CapabilitySpec> settlementRouteSpecs)
    {
        if (pricePolicy != "channel" || structuralRouteSpecs.Count == 0)
        {
            return null;
        }
        if (settlementRouteSpecs.Count == 0)
        {
            return "供应线路尚未配置可结算价格，请先完善渠道模型价格";
        }
        if (!LogicalModelCapabilityCovered(product, settlementRouteSpecs))
        {
            return "部分创作端能力只能由未配置价格的渠道模型承接，请完善对应价格";
        }
        return null;
    }
}
