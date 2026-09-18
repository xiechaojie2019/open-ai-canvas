#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Auth;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Outbound;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>管理后台渠道分页。对应 Go: <c>app.AdminChannelPage</c>（字段顺序即输出顺序）。</summary>
public sealed class AdminChannelPageDto
{
    [JsonPropertyName("channels")]
    public IReadOnlyList<PublicModelChannelDto> Channels { get; init; } = [];

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("page")]
    public long Page { get; init; }

    [JsonPropertyName("pageSize")]
    public long PageSize { get; init; }
}

/// <summary>渠道保存请求。对应 Go: <c>app.ChannelRequest</c>（指针字段可区分「未提交」）。</summary>
public sealed class ChannelRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("publicAlias")]
    public string? PublicAlias { get; set; }

    [JsonPropertyName("sortOrder")]
    public long? SortOrder { get; set; }

    [JsonPropertyName("baseUrl")]
    public string BaseURL { get; set; } = "";

    [JsonPropertyName("apiKey")]
    public string APIKey { get; set; } = "";

    [JsonPropertyName("secretKey")]
    public string SecretKey { get; set; } = "";

    [JsonPropertyName("concurrencyLimit")]
    public long? ConcurrencyLimit { get; set; }

    [JsonPropertyName("useGlobalConcurrency")]
    public bool? UseGlobalConcurrency { get; set; }

    [JsonPropertyName("models")]
    public List<string>? Models { get; set; }

    [JsonPropertyName("headers")]
    public List<OutboundHeader>? Headers { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}

/// <summary>
/// 系统渠道管理 CRUD。对应 Go: <c>app/admin.go</c> 的渠道部分与
/// <c>app/channel_models.go</c> 的 <c>syncInitialChannelModels</c> / <c>AdminChannelModels</c>。
/// </summary>
/// <remarks>
/// 渠道模型的上游拉取（fetch/import/test）依赖出站 HTTP 客户端，属阶段 5/10，另行接线。
/// </remarks>
public sealed class ChannelAdminService
{
    private const long MinChannelConcurrencyLimit = 1;
    private const long MaxChannelConcurrencyLimit = 999;

    private readonly Repository _repository;
    private readonly LogicalModelService _logicalModels;

    public ChannelAdminService(Repository repository, LogicalModelService logicalModels)
    {
        _repository = repository;
        _logicalModels = logicalModels;
    }

    /// <summary>
    /// 在服务启动时补齐系统渠道的模型占位记录。
    /// 对应 Go: <c>Service.EnsureSystemChannelModels</c>。
    /// </summary>
    /// <remarks>
    /// 仅渠道模型记录完全为空时才执行同步；已有（即使全部停用）记录不应被启动流程
    /// 重置。这使管理员手动停用或移除模型后的状态保持不变。
    /// </remarks>
    public async Task EnsureSystemChannelModelsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModelChannel> channels = await _repository.SystemChannelsAsync(
            includeDisabled: true, cancellationToken).ConfigureAwait(false);

        foreach (ModelChannel channel in channels)
        {
            IReadOnlyList<ChannelModel> items = await _repository.ChannelModelsAsync(
                channel.ID, enabledOnly: false, cancellationToken).ConfigureAwait(false);
            if (items.Count == 0)
            {
                await SyncInitialChannelModelsAsync(channel, ChannelModelNames(channel), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>管理后台渠道分页。对应 Go: <c>AdminSystemChannelPage</c>。</summary>
    public async Task<AdminChannelPageDto> AdminChannelPageAsync(
        User actor,
        string keyword,
        string status,
        long page,
        long limit,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (page, limit) = NormalizeAdminPage(page, limit);

        (IReadOnlyList<ModelChannel> channels, long total) = await _repository.AdminSystemChannelsAsync(
            keyword, status, limit, (page - 1) * limit, cancellationToken).ConfigureAwait(false);

        List<PublicModelChannelDto> result = new(channels.Count);
        foreach (ModelChannel channel in channels)
        {
            IReadOnlyList<ChannelModel> items = await _repository.ChannelModelsAsync(
                channel.ID, false, cancellationToken).ConfigureAwait(false);
            result.Add(ChannelService.PublicChannel(channel, admin: true, items));
        }

        return new AdminChannelPageDto { Channels = result, Total = total, Page = page, PageSize = limit };
    }

    /// <summary>对应 Go: <c>normalizeAdminPage</c>。</summary>
    internal static (long Page, long Limit) NormalizeAdminPage(long page, long limit)
    {
        if (page <= 0)
        {
            page = 1;
        }
        if (limit <= 0 || limit > 100)
        {
            limit = 20;
        }
        return (page, limit);
    }

    /// <summary>创建系统渠道。对应 Go: <c>CreateSystemChannel</c>。</summary>
    public async Task<PublicModelChannelDto> CreateSystemChannelAsync(
        User actor,
        ChannelRequest request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        string channelId = await _repository.NextPrefixedIdAsync("CHANNEL", cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        ModelChannel channel = ChannelFromRequest(
            request,
            new ModelChannel
            {
                ID = channelId,
                UserID = actor.ID,
                Scope = "system",
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        EncryptSystemChannelSecrets(channel);
        await _repository.CreateAsync(channel, cancellationToken).ConfigureAwait(false);
        await SyncInitialChannelModelsAsync(channel, request.Models ?? [], cancellationToken).ConfigureAwait(false);
        _logicalModels.InvalidateRouteCatalog();

        IReadOnlyList<ChannelModel> items = await _repository.ChannelModelsAsync(
            channel.ID, false, cancellationToken).ConfigureAwait(false);
        return ChannelService.PublicChannel(channel, admin: true, items);
    }

    /// <summary>复制系统渠道（含渠道模型与价格档）。对应 Go: <c>DuplicateSystemChannel</c>。</summary>
    public async Task<PublicModelChannelDto> DuplicateSystemChannelAsync(
        User actor,
        string id,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        ModelChannel? source = await _repository.AdminSystemChannelAsync(id, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            throw new InvalidOperationException("record not found");
        }
        IReadOnlyList<ChannelModel> sourceModels = await _repository.ChannelModelsAsync(
            source.ID, false, cancellationToken).ConfigureAwait(false);
        if (sourceModels.Count == 0)
        {
            sourceModels = ChannelModelNames(source)
                .Select(name => new ChannelModel
                {
                    ModelKey = name,
                    ProviderModelKey = name,
                    DisplayName = name,
                    BillingMode = "fixed_request",
                    PriceVersion = 1,
                    PriceTiers = [],
                })
                .ToList();
        }

        string channelId = await _repository.NextPrefixedIdAsync("CHANNEL", cancellationToken).ConfigureAwait(false);
        ModelChannel channel = CloneChannel(source);
        channel.ID = channelId;
        channel.UserID = actor.ID;
        channel.Scope = "system";
        channel.Name = DuplicateChannelName(source.Name);
        channel.CreatedAt = DateTime.UtcNow;
        channel.UpdatedAt = DateTime.UtcNow;
        channel.DeletedAt = null;
        EncryptSystemChannelSecrets(channel);

        List<ChannelModel> channelModels = [];
        List<ChannelModelPriceTier> priceTiers = [];
        foreach (ChannelModel sourceModel in sourceModels)
        {
            string modelId = await _repository.NextPrefixedIdAsync("MODEL", cancellationToken).ConfigureAwait(false);
            ChannelModel channelModel = CloneChannelModel(sourceModel);
            channelModel.ID = modelId;
            channelModel.ChannelID = channel.ID;
            channelModel.CreatedAt = DateTime.UtcNow;
            channelModel.UpdatedAt = DateTime.UtcNow;
            channelModel.DeletedAt = null;
            channelModel.PriceTiers = [];
            channelModels.Add(channelModel);

            foreach (ChannelModelPriceTier sourceTier in sourceModel.PriceTiers)
            {
                string tierId = await _repository.NextPrefixedIdAsync("PTIER", cancellationToken).ConfigureAwait(false);
                var priceTier = new ChannelModelPriceTier
                {
                    ID = tierId,
                    ChannelModelID = channelModel.ID,
                    SelectorKey = sourceTier.SelectorKey,
                    SelectorJSON = sourceTier.SelectorJSON,
                    Resolution = sourceTier.Resolution,
                    VideoSeconds = sourceTier.VideoSeconds,
                    ProviderModelKey = sourceTier.ProviderModelKey,
                    BillingMode = sourceTier.BillingMode,
                    UnitPriceMicrocredits = sourceTier.UnitPriceMicrocredits,
                    InputTokenPriceMicrocredits = sourceTier.InputTokenPriceMicrocredits,
                    OutputTokenPriceMicrocredits = sourceTier.OutputTokenPriceMicrocredits,
                    CachedTokenPriceMicrocredits = sourceTier.CachedTokenPriceMicrocredits,
                    PriceConfigured = sourceTier.PriceConfigured,
                    Enabled = sourceTier.Enabled,
                    PriceVersion = sourceTier.PriceVersion,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                priceTiers.Add(priceTier);
            }
        }

        await _repository.CreateDuplicatedSystemChannelAsync(channel, channelModels, priceTiers, cancellationToken)
            .ConfigureAwait(false);
        _logicalModels.InvalidateRouteCatalog();

        IReadOnlyList<ChannelModel> items = await _repository.ChannelModelsAsync(
            channel.ID, false, cancellationToken).ConfigureAwait(false);
        return ChannelService.PublicChannel(channel, admin: true, items);
    }

    /// <summary>更新系统渠道。对应 Go: <c>UpdateSystemChannel</c>（含 presentation-only 短路径）。</summary>
    public async Task<PublicModelChannelDto> UpdateSystemChannelAsync(
        User actor,
        string id,
        ChannelRequest request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        if (PresentationOnly(request))
        {
            return await UpdateChannelPresentationAsync(id, request, cancellationToken).ConfigureAwait(false);
        }

        bool updateModels = request.Models is not null;
        ModelChannel? channel = await _repository.AdminSystemChannelAsync(id, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            throw new InvalidOperationException("record not found");
        }
        // Go 在更新前解密旧凭证再参与合并；当前加密实现为占位（原值返回），行为一致。
        request = MergeChannelRequest(request, channel);
        ModelChannel next = ChannelFromRequest(request, CloneChannel(channel));
        next.ID = channel.ID;
        next.UserID = channel.UserID;
        next.Scope = "system";
        next.CreatedAt = channel.CreatedAt;
        next.UpdatedAt = DateTime.UtcNow;
        if (request.APIKey.Length == 0)
        {
            next.APIKey = channel.APIKey;
        }
        if (request.SecretKey.Length == 0)
        {
            next.SecretKey = channel.SecretKey;
        }
        EncryptSystemChannelSecrets(next);
        await _repository.SaveModelChannelAsync(next, cancellationToken).ConfigureAwait(false);

        if (updateModels)
        {
            await SyncInitialChannelModelsAsync(next, request.Models ?? [], cancellationToken).ConfigureAwait(false);
        }
        _logicalModels.InvalidateRouteCatalog();

        IReadOnlyList<ChannelModel> items = await _repository.ChannelModelsAsync(
            next.ID, false, cancellationToken).ConfigureAwait(false);
        return ChannelService.PublicChannel(next, admin: true, items);
    }

    /// <summary>删除系统渠道。对应 Go: <c>DeleteSystemChannel</c>。</summary>
    public async Task DeleteSystemChannelAsync(
        User actor,
        string id,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        ModelChannel? channel = await _repository.AdminSystemChannelAsync(id, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            throw AppError.BadAuthRequest("系统渠道不存在或已删除");
        }
        // 保留主体供历史账单和调用日志关联，但从所有业务查询中隐藏并清除密钥。
        bool deleted = await _repository.DeleteSystemChannelAsync(channel.ID, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (!deleted)
        {
            throw AppError.BadAuthRequest("系统渠道不存在或已删除");
        }
        _logicalModels.InvalidateRouteCatalog();
    }

    /// <summary>渠道模型管理列表（含能力配置展示归一化）。对应 Go: <c>AdminChannelModels</c>。</summary>
    public async Task<IReadOnlyList<ChannelModel>> AdminChannelModelsAsync(
        User actor,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        if (await _repository.AdminSystemChannelAsync(channelId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }

        IReadOnlyList<ChannelModel> items = await EnsureChannelModelsAsync(channelId, true, cancellationToken)
            .ConfigureAwait(false);
        foreach (ChannelModel item in items)
        {
            if (string.IsNullOrWhiteSpace(item.CapabilityConfigJSON))
            {
                continue;
            }
            ModelCapabilityConfig? config;
            try
            {
                config = ModelCapabilityConfigOps.DecodeModelCapabilityConfig(item.CapabilityConfigJSON);
            }
            catch (AppError)
            {
                continue;
            }
            if (config is null)
            {
                continue;
            }
            try
            {
                ModelCapabilityConfig? normalized = ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
                    item.Capability,
                    item.Protocol,
                    LogicalModelService.FirstNonEmpty(item.ProviderModelKey, item.ModelKey),
                    config);
                if (normalized is not null)
                {
                    item.CapabilityConfig = CapabilityConfigToDictionary(normalized);
                }
            }
            catch (AppError)
            {
                continue;
            }
        }
        return items;
    }

    /// <summary>更新渠道模型排序。对应 Go: <c>UpdateAdminChannelModelSort</c>。</summary>
    public async Task UpdateAdminChannelModelSortAsync(
        User actor,
        string channelId,
        string modelId,
        long? sortOrder,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        if (sortOrder is null)
        {
            throw AppError.BadAuthRequest("请填写排序值");
        }
        ValidateChannelSortOrder(sortOrder.Value);
        if (await _repository.AdminSystemChannelAsync(channelId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }
        if (!await _repository.UpdateChannelModelSortAsync(
                channelId, modelId, sortOrder.Value, DateTime.UtcNow, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("record not found");
        }
        _logicalModels.InvalidateRouteCatalog();
    }

    /// <summary>渠道排序条目。对应 Go: <c>app.ChannelOrderItem</c>。</summary>
    public sealed record ChannelOrderItemDto(
        [property: JsonPropertyName("id")] string ID,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("enabled")] bool Enabled);

    /// <summary>渠道排序读取（channelID 为空时列渠道，否则列渠道模型）。对应 Go: <c>AdminChannelOrder</c>。</summary>
    public async Task<IReadOnlyList<ChannelOrderItemDto>> AdminChannelOrderAsync(
        User actor,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        List<ChannelOrderItemDto> result = [];
        if (channelId.Length == 0)
        {
            IReadOnlyList<ModelChannel> rows = await _repository.SystemChannelsAsync(true, cancellationToken)
                .ConfigureAwait(false);
            foreach (ModelChannel row in rows)
            {
                result.Add(new ChannelOrderItemDto(row.ID, row.Name, row.Enabled));
            }
            return result;
        }

        if (await _repository.AdminSystemChannelAsync(channelId, cancellationToken).ConfigureAwait(false) is null)
        {
            // Go 返回裸 gorm.ErrRecordNotFound → 500 固定文案。
            throw new InvalidOperationException("record not found");
        }
        IReadOnlyList<ChannelModel> models = await _repository.ChannelModelsAsync(
            channelId, false, cancellationToken).ConfigureAwait(false);
        foreach (ChannelModel row in models)
        {
            result.Add(new ChannelOrderItemDto(
                row.ID,
                LogicalModelService.FirstNonEmpty(row.DisplayName, row.ModelKey),
                row.Enabled));
        }
        return result;
    }

    /// <summary>渠道排序保存（乐观并发，快照过期回 409）。对应 Go: <c>SaveAdminChannelOrder</c>。</summary>
    public async Task SaveAdminChannelOrderAsync(
        User actor,
        string channelId,
        IReadOnlyList<string>? ids,
        IReadOnlyList<string>? expectedIds,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        if (ids is null || expectedIds is null || ids.Count > 10000)
        {
            throw AppError.BadAuthRequest("请重新加载完整排序列表");
        }

        Persistence.Repositories.Repository.ChannelOrderOutcome outcome = await _repository.SaveChannelOrderAsync(
            channelId, ids, expectedIds, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (outcome == Persistence.Repositories.Repository.ChannelOrderOutcome.Changed)
        {
            throw AppError.New(409, "列表已发生变化，请重新打开排序后再保存");
        }
        _logicalModels.InvalidateRouteCatalog();
    }

    // ------------------------------------------------------------ 内部实现

    /// <summary>对应 Go: <c>duplicateChannelName</c>。追加「 - 副本」并限制总长 80 字符。</summary>
    private static string DuplicateChannelName(string name)
    {
        const string suffix = " - 副本";
        string baseName = name.Trim();
        if (baseName.Length == 0)
        {
            baseName = "系统渠道";
        }
        if (baseName.Length + suffix.Length > 80)
        {
            baseName = baseName[..(80 - suffix.Length)];
        }
        return baseName + suffix;
    }

    /// <summary>对应 Go: <c>presentationOnly</c>。</summary>
    private static bool PresentationOnly(ChannelRequest request) =>
        (request.PublicAlias is not null || request.SortOrder is not null) &&
        request.Name.Length == 0 && request.BaseURL.Length == 0 && request.APIKey.Length == 0 &&
        request.SecretKey.Length == 0 && request.ConcurrencyLimit is null &&
        request.UseGlobalConcurrency is null && request.Models is null &&
        request.Headers is null && request.Enabled is null;

    /// <summary>对应 Go: <c>updateChannelPresentation</c>（只改别名/排序，不校验上游地址）。</summary>
    private async Task<PublicModelChannelDto> UpdateChannelPresentationAsync(
        string id,
        ChannelRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SortOrder is not null)
        {
            ValidateChannelSortOrder(request.SortOrder.Value);
        }
        string? alias = null;
        if (request.PublicAlias is not null)
        {
            alias = request.PublicAlias.Trim();
            if (alias.Length > 80)
            {
                throw AppError.BadAuthRequest("前台显示别名不能超过 80 个字符");
            }
        }

        bool updated = await _repository.UpdateSystemChannelPresentationAsync(
            id, alias, request.SortOrder, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (!updated)
        {
            throw new InvalidOperationException("record not found");
        }

        _logicalModels.InvalidateRouteCatalog();
        ModelChannel? channel = await _repository.AdminSystemChannelAsync(id, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            throw new InvalidOperationException("record not found");
        }
        IReadOnlyList<ChannelModel> items = await _repository.ChannelModelsAsync(
            channel.ID, false, cancellationToken).ConfigureAwait(false);
        return ChannelService.PublicChannel(channel, admin: true, items);
    }

    /// <summary>排序值校验。对应 Go: <c>validateChannelSortOrder</c>。</summary>
    private static void ValidateChannelSortOrder(long value)
    {
        if (value < 0 || value > 999999)
        {
            throw AppError.BadAuthRequest("排序值必须是 0-999999 的整数，数值越小越靠前");
        }
    }

    /// <summary>请求 → 渠道实体。对应 Go: <c>channelFromRequest</c>。</summary>
    private ModelChannel ChannelFromRequest(ChannelRequest request, ModelChannel channel)
    {
        string name = request.Name.Trim();
        string baseURL = request.BaseURL.Trim();
        if (name.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写渠道名称");
        }
        if (baseURL.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写 Base URL");
        }
        // 启用/停用或只修改价格、模型等配置时，不应要求上游域名当前可解析。
        // 只有 Base URL 实际变化时才做出站地址校验。
        bool connectionChanged =
            baseURL.TrimEnd('/') != channel.BaseURL.TrimEnd('/');
        if (connectionChanged)
        {
            OutboundGuard.ValidateOutboundUrlAsync(baseURL).GetAwaiter().GetResult();
        }

        List<string> models = UniqueNonEmpty(request.Models ?? []);
        string modelsJson = JsonSerializer.Serialize(models);
        string headersJson = OutboundGuard.EncodeOutboundHeadersJson(request.Headers ?? []);

        channel.Name = name;
        if (request.PublicAlias is not null)
        {
            string alias = request.PublicAlias.Trim();
            if (alias.Length > 80)
            {
                throw AppError.BadAuthRequest("前台显示别名不能超过 80 个字符");
            }
            channel.PublicAlias = alias;
        }
        if (request.SortOrder is not null)
        {
            ValidateChannelSortOrder(request.SortOrder.Value);
            channel.SortOrder = request.SortOrder.Value;
        }
        channel.BaseURL = baseURL.TrimEnd('/');
        if (request.APIKey.Length > 0)
        {
            channel.APIKey = request.APIKey;
        }
        if (request.SecretKey.Length > 0)
        {
            channel.SecretKey = request.SecretKey;
        }
        // 系统渠道只保存地址与凭证；实际协议和鉴权方式由所选模型决定。
        channel.APIFormat = "openai";
        if (request.UseGlobalConcurrency is true)
        {
            channel.ConcurrencyLimit = 0;
        }
        else if (request.ConcurrencyLimit is not null)
        {
            if (request.ConcurrencyLimit < MinChannelConcurrencyLimit ||
                request.ConcurrencyLimit > MaxChannelConcurrencyLimit)
            {
                throw AppError.BadAuthRequest("最大并发数必须是 1-999 的整数");
            }
            channel.ConcurrencyLimit = request.ConcurrencyLimit.Value;
        }
        else if (request.UseGlobalConcurrency is not null)
        {
            throw AppError.BadAuthRequest("请填写渠道最大并发数");
        }
        channel.ModelsJSON = modelsJson;
        channel.HeadersJSON = headersJson;
        if (request.Enabled is not null)
        {
            channel.Enabled = request.Enabled.Value;
        }
        return channel;
    }

    /// <summary>对应 Go: <c>mergeChannelRequest</c>。空字段以现有渠道补齐。</summary>
    private static ChannelRequest MergeChannelRequest(ChannelRequest request, ModelChannel channel)
    {
        if (request.Name.Trim().Length == 0)
        {
            request.Name = channel.Name;
        }
        if (request.BaseURL.Trim().Length == 0)
        {
            request.BaseURL = channel.BaseURL;
        }
        if (request.Models is null)
        {
            request.Models = ChannelModelNames(channel);
        }
        if (request.Headers is null)
        {
            request.Headers = OutboundGuard.ParseOutboundHeadersJson(channel.HeadersJSON);
        }
        return request;
    }

    /// <summary>
    /// 加密渠道密钥。对应 Go: <c>encryptSystemChannelSecrets</c>。
    /// 当前加密实现为占位（原值返回），与设置加密保持同一替换点。
    /// </summary>
    private static void EncryptSystemChannelSecrets(ModelChannel channel)
    {
        channel.APIKey = AuthService.EncryptSecret(channel.APIKey);
        channel.SecretKey = AuthService.EncryptSecret(channel.SecretKey);
    }

    /// <summary>
    /// 按名称清单同步渠道模型占位：新增缺失项，名单外模型停用。
    /// 对应 Go: <c>syncInitialChannelModels</c>。
    /// </summary>
    private async Task SyncInitialChannelModelsAsync(
        ModelChannel channel,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ChannelModel> existing = await _repository.ChannelModelsAsync(
            channel.ID, false, cancellationToken).ConfigureAwait(false);
        Dictionary<string, ChannelModel> byKey = new(StringComparer.Ordinal);
        foreach (ChannelModel item in existing)
        {
            byKey[item.ModelKey] = item;
        }

        HashSet<string> desired = new(StringComparer.Ordinal);
        HashSet<string> retired = RetiredChannelModelKeys(channel.RetiredModelsJSON);
        foreach (string rawName in UniqueNonEmpty(names))
        {
            string name = rawName.StartsWith("models/", StringComparison.Ordinal) ? rawName["models/".Length..] : rawName;
            if (retired.Contains(ChannelModelCatalogKey(name)))
            {
                continue;
            }
            desired.Add(name);
            if (byKey.TryGetValue(name, out ChannelModel? existingItem))
            {
                continue;
            }
            string modelId = await _repository.NextPrefixedIdAsync("MODEL", cancellationToken).ConfigureAwait(false);
            await _repository.CreateAsync(new ChannelModel
            {
                ID = modelId,
                ChannelID = channel.ID,
                ModelKey = name,
                DisplayName = name,
                BillingMode = "fixed_request",
                PriceVersion = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }, cancellationToken).ConfigureAwait(false);
        }

        foreach (ChannelModel item in existing)
        {
            if (!desired.Contains(item.ModelKey) && item.Enabled)
            {
                item.Enabled = false;
                item.PriceVersion++;
                item.UpdatedAt = DateTime.UtcNow;
                await _repository.SaveChannelModelAsync(item, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 渠道模型列表为空时按 ModelsJSON 兜底补齐。
    /// 对应 Go: <c>ensureChannelModels</c>。
    /// </summary>
    private async Task<IReadOnlyList<ChannelModel>> EnsureChannelModelsAsync(
        string channelId,
        bool includeDisabled,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ChannelModel> items = await _repository.ChannelModelsAsync(
            channelId, !includeDisabled, cancellationToken).ConfigureAwait(false);
        if (items.Count > 0)
        {
            return items;
        }
        ModelChannel? channel = await _repository.AdminSystemChannelAsync(channelId, cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            throw new InvalidOperationException("record not found");
        }
        await SyncInitialChannelModelsAsync(channel, ChannelModelNames(channel), cancellationToken)
            .ConfigureAwait(false);
        return await _repository.ChannelModelsAsync(channelId, !includeDisabled, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>对应 Go: <c>channelModelNames</c>。</summary>
    private static List<string> ChannelModelNames(ModelChannel channel)
    {
        List<string>? models;
        try
        {
            models = JsonSerializer.Deserialize<List<string>>(channel.ModelsJSON);
        }
        catch (JsonException)
        {
            models = null;
        }
        return UniqueNonEmpty(models ?? []);
    }

    /// <summary>对应 Go: <c>retiredChannelModelKeys</c>。</summary>
    private static HashSet<string> RetiredChannelModelKeys(string? raw)
    {
        List<string>? values;
        try
        {
            values = JsonSerializer.Deserialize<List<string>>(raw ?? "");
        }
        catch (JsonException)
        {
            values = null;
        }
        HashSet<string> result = new(StringComparer.Ordinal);
        foreach (string value in values ?? [])
        {
            string key = ChannelModelCatalogKey(value);
            if (key.Length > 0)
            {
                result.Add(key);
            }
        }
        return result;
    }

    /// <summary>对应 Go: <c>channelModelCatalogKey</c>。</summary>
    private static string ChannelModelCatalogKey(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith("models/", StringComparison.Ordinal))
        {
            trimmed = trimmed["models/".Length..];
        }
        return trimmed.ToLowerInvariant();
    }

    private static List<string> UniqueNonEmpty(IEnumerable<string> values)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string rawValue in values)
        {
            string value = rawValue.Trim();
            if (value.Length == 0 || !seen.Add(value))
            {
                continue;
            }
            result.Add(value);
        }
        return result;
    }

    private static ModelChannel CloneChannel(ModelChannel source) => new()
    {
        ID = source.ID,
        UserID = source.UserID,
        Scope = source.Scope,
        Enabled = source.Enabled,
        Name = source.Name,
        PublicAlias = source.PublicAlias,
        SortOrder = source.SortOrder,
        BaseURL = source.BaseURL,
        APIKey = source.APIKey,
        SecretKey = source.SecretKey,
        APIFormat = source.APIFormat,
        ConcurrencyLimit = source.ConcurrencyLimit,
        ModelsJSON = source.ModelsJSON,
        RetiredModelsJSON = source.RetiredModelsJSON,
        HeadersJSON = source.HeadersJSON,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        DeletedAt = source.DeletedAt,
    };

    private static ChannelModel CloneChannelModel(ChannelModel source) => new()
    {
        ID = source.ID,
        ChannelID = source.ChannelID,
        ModelKey = source.ModelKey,
        ProviderModelKey = source.ProviderModelKey,
        DisplayName = source.DisplayName,
        SortOrder = source.SortOrder,
        Icon = source.Icon,
        Capability = source.Capability,
        Protocol = source.Protocol,
        BillingMode = source.BillingMode,
        UnitPriceMicrocredits = source.UnitPriceMicrocredits,
        InputTokenPriceMicrocredits = source.InputTokenPriceMicrocredits,
        OutputTokenPriceMicrocredits = source.OutputTokenPriceMicrocredits,
        CachedTokenPriceMicrocredits = source.CachedTokenPriceMicrocredits,
        PriceConfigured = source.PriceConfigured,
        Enabled = source.Enabled,
        PriceVersion = source.PriceVersion,
        CapabilityConfigJSON = source.CapabilityConfigJSON,
        CapabilityVersion = source.CapabilityVersion,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        DeletedAt = source.DeletedAt,
    };

    /// <summary>归一化能力配置 → 展示字典。对应 Go 的 <c>json.Marshal + Unmarshal 到 map</c> 往返。</summary>
    private static Dictionary<string, JsonElement> CapabilityConfigToDictionary(ModelCapabilityConfig config)
    {
        try
        {
            string encoded = JsonSerializer.Serialize(config);
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(encoded) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
