#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Protocol;
using OpenAICanvas.Providers;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>渠道模型保存请求。对应 Go: <c>app.ChannelModelRequest</c>（channel_models.go）。</summary>
public sealed class ChannelModelRequest
{
    [JsonPropertyName("modelKey")]
    public string ModelKey { get; set; } = "";

    [JsonPropertyName("providerModelKey")]
    public string ProviderModelKey { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    [JsonPropertyName("billingMode")]
    public string BillingMode { get; set; } = "";

    [JsonPropertyName("unitPriceMicrocredits")]
    public long UnitPriceMicrocredits { get; set; }

    [JsonPropertyName("inputTokenPriceMicrocredits")]
    public long InputTokenPriceMicrocredits { get; set; }

    [JsonPropertyName("outputTokenPriceMicrocredits")]
    public long OutputTokenPriceMicrocredits { get; set; }

    [JsonPropertyName("cachedTokenPriceMicrocredits")]
    public long CachedTokenPriceMicrocredits { get; set; }

    [JsonPropertyName("priceConfigured")]
    public bool PriceConfigured { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("capabilityConfig")]
    public ModelCapabilityConfig? CapabilityConfig { get; set; }

    [JsonPropertyName("priceTiers")]
    public List<ChannelModelPriceTierRequest>? PriceTiers { get; set; }
}

/// <summary>渠道内某个规格的价格档请求。对应 Go: <c>app.ChannelModelPriceTierRequest</c>。</summary>
public sealed class ChannelModelPriceTierRequest
{
    [JsonPropertyName("selector")]
    public Dictionary<string, string>? Selector { get; set; }

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = "";

    [JsonPropertyName("videoSeconds")]
    public long VideoSeconds { get; set; }

    [JsonPropertyName("providerModelKey")]
    public string ProviderModelKey { get; set; } = "";

    [JsonPropertyName("billingMode")]
    public string BillingMode { get; set; } = "";

    [JsonPropertyName("unitPriceMicrocredits")]
    public long UnitPriceMicrocredits { get; set; }

    [JsonPropertyName("inputTokenPriceMicrocredits")]
    public long InputTokenPriceMicrocredits { get; set; }

    [JsonPropertyName("outputTokenPriceMicrocredits")]
    public long OutputTokenPriceMicrocredits { get; set; }

    [JsonPropertyName("cachedTokenPriceMicrocredits")]
    public long CachedTokenPriceMicrocredits { get; set; }

    [JsonPropertyName("priceConfigured")]
    public bool PriceConfigured { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}

/// <summary>
/// 渠道模型管理写路径：保存（含价格档归一化）与删除。
/// 对应 Go: <c>app/channel_models.go</c> 的 SaveAdminChannelModel / DeleteAdminChannelModels。
/// </summary>
public sealed class ChannelModelAdminService
{
    private const int MaxBatchDeleteCount = 100;
    private const long MaxTokenPriceMicrocredits = 1_000_000 * CreditPolicyService.CreditScale;

    /// <summary>上游拉取汇总。对应 Go: <c>app.AdminChannelModelFetchResult</c>。</summary>
    public sealed record AdminChannelModelFetchResultDto(
        [property: JsonPropertyName("models")] IReadOnlyList<string> Models,
        [property: JsonPropertyName("added")] long Added);

    private readonly Repository _repository;
    private readonly LogicalModelService _logicalModels;
    private readonly ChannelModelCatalogService _catalog;

    public ChannelModelAdminService(
        Repository repository,
        LogicalModelService logicalModels,
        ChannelModelCatalogService? catalog = null)
    {
        _repository = repository;
        _logicalModels = logicalModels;
        _catalog = catalog ?? new ChannelModelCatalogService();
    }

    /// <summary>保存渠道模型（创建或更新）。对应 Go: <c>SaveAdminChannelModel</c>。</summary>
    public async Task<ChannelModel> SaveAdminChannelModelAsync(
        User actor,
        string channelId,
        string id,
        ChannelModelRequest request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        ModelChannel? channel = await _repository.AdminSystemChannelAsync(channelId, cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            // Go 返回裸 gorm.ErrRecordNotFound → 500 固定文案。
            throw new InvalidOperationException("record not found");
        }

        (string modelKey, string providerModelKey, string capability, string protocol) =
            NormalizeChannelModelContract(channel, request);

        // 先检查同渠道重复模型，避免无关能力校验或生成无用序列号掩盖真正的冲突。
        ChannelModel? conflict = await _repository.ChannelModelByKeyIncludingDisabledAsync(
            channelId, modelKey, cancellationToken).ConfigureAwait(false);
        if (conflict is not null && conflict.ID != id.Trim())
        {
            throw AppError.BadAuthRequest("该渠道已存在模型 " + modelKey + "，请直接编辑已有模型");
        }

        if (capability is "text" or "image" or "video")
        {
            _ = ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
                capability, protocol, providerModelKey, request.CapabilityConfig);
        }

        List<ChannelModelPriceTier> tiers = await NormalizeChannelModelPriceTiersAsync(
            request, capability, protocol, providerModelKey, cancellationToken).ConfigureAwait(false);

        _ = await _repository.NextPrefixedIdAsync("MODEL", cancellationToken).ConfigureAwait(false);
        ChannelModel item;
        if (id.Length == 0)
        {
            string modelId = await _repository.NextPrefixedIdAsync("MODEL", cancellationToken).ConfigureAwait(false);
            item = new ChannelModel { ID = modelId, ChannelID = channelId, Enabled = true, PriceVersion = 1 };
        }
        else
        {
            ChannelModel? existing = await _repository.ChannelModelByIDAsync(channelId, id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                throw new InvalidOperationException("record not found");
            }
            item = existing;
            item.PriceVersion++;
        }

        item.ModelKey = modelKey;
        item.ProviderModelKey = providerModelKey;
        item.DisplayName = request.DisplayName.Trim();
        if (item.DisplayName.Length == 0)
        {
            item.DisplayName = modelKey;
        }
        item.Icon = request.Icon.Trim();
        item.Capability = capability;
        item.Protocol = protocol;
        ApplyChannelModelPriceTierSummary(item, tiers);

        if (capability is "text" or "image" or "video")
        {
            ModelCapabilityConfig capabilityConfig =
                ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
                    capability, protocol, providerModelKey, request.CapabilityConfig)!;
            string encoded = JsonSerializer.Serialize(capabilityConfig);
            if (item.CapabilityConfigJSON != encoded)
            {
                item.CapabilityVersion++;
            }
            item.CapabilityConfigJSON = encoded;
        }
        else
        {
            item.CapabilityConfigJSON = "";
            item.CapabilityVersion = 0;
        }

        if (request.Enabled is not null)
        {
            item.Enabled = request.Enabled.Value;
        }

        ValidateChannelModelTierCapabilities(tiers, item.CapabilityConfigJSON, capability);

        // 渠道模型及所有价格档必须同时落库，不能出现“能力已开放但规格价格尚未更新”的窗口。
        item.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveChannelModelWithPriceTiersAsync(item, tiers, cancellationToken).ConfigureAwait(false);
        item.PriceTiers = tiers;
        // 响应里不回显能力配置，避免把管理端内部结构带回列表。
        item.CapabilityConfig = null!;
        _logicalModels.InvalidateRouteCatalog();

        await _repository.SyncChannelModelNamesAsync(channel.ID, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        // syncLogicalModelsFromChannelModel 只失效路由目录（已在上一步完成）。
        _logicalModels.InvalidateRouteCatalog();
        return item;
    }

    /// <summary>删除单个渠道模型。对应 Go: <c>DeleteAdminChannelModel</c>。</summary>
    public Task<long> DeleteAdminChannelModelAsync(
        User actor,
        string channelId,
        string id,
        CancellationToken cancellationToken = default) =>
        DeleteAdminChannelModelsAsync(actor, channelId, [id], cancellationToken);

    /// <summary>
    /// 批量删除渠道模型。完整校验后再原子删除，刻意拒绝部分成功：
    /// 管理员可以安全地修正被占用的模型并原样重试。
    /// 对应 Go: <c>DeleteAdminChannelModels</c>。
    /// </summary>
    public async Task<long> DeleteAdminChannelModelsAsync(
        User actor,
        string channelId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        if (await _repository.AdminSystemChannelAsync(channelId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw AppError.BadAuthRequest("系统渠道不存在或已删除");
        }

        List<string> modelIds = NormalizeDeleteIds(ids);
        IReadOnlyList<ChannelModel> items = await _repository.ChannelModelsAsync(
            channelId, false, cancellationToken).ConfigureAwait(false);
        HashSet<string> selected = new(modelIds, StringComparer.Ordinal);
        int found = items.Count(item => selected.Contains(item.ID));
        if (found != modelIds.Count)
        {
            throw AppError.BadAuthRequest("所选渠道模型中存在已删除或不属于当前渠道的记录，请刷新后重试");
        }

        // 删除模型与渠道的兼容模型清单必须同事务提交，避免接口报错但列表已部分变化。
        (bool ok, long deleted) = await _repository.DeleteChannelModelsAsync(
            channelId, modelIds, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            throw AppError.BadAuthRequest("所选渠道模型中有模型仍被前台模型供应线路或进行中任务使用，本次未删除任何模型");
        }
        _logicalModels.InvalidateRouteCatalog();
        return deleted;
    }

    // ------------------------------------------------------------ 上游目录拉取与导入

    /// <summary>只读取上游模型目录，不修改渠道模型配置。对应 Go: <c>PreviewAdminChannelModels</c>。</summary>
    public async Task<IReadOnlyList<string>> PreviewAdminChannelModelsAsync(
        User actor,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        return await FetchCatalogKeysAsync(actor, channelId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 拉取上游目录并把缺失模型落为停用占位（已删除模型可重新拉取，退役 SKU 除外）。
    /// 对应 Go: <c>FetchAdminChannelModels</c>（未被路由使用，保留服务面）。
    /// </summary>
    public async Task<AdminChannelModelFetchResultDto> FetchAdminChannelModelsAsync(
        User actor,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        ModelChannel channel = await LoadChannelForCatalogAsync(actor, channelId, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> models = await FetchChannelModelsViaCatalogAsync(actor, channel, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<ChannelModel> existing = await _repository.ChannelModelsAsync(
            channelId, false, cancellationToken).ConfigureAwait(false);
        HashSet<string> known = existing
            .Select(item => CatalogKey(item.ModelKey))
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> retired = RetiredChannelKeys(channel.RetiredModelsJSON);

        List<ChannelModel> missing = [];
        foreach (string rawName in models)
        {
            string name = rawName.Trim();
            if (name.StartsWith("models/", StringComparison.Ordinal))
            {
                name = name["models/".Length..];
            }
            string key = CatalogKey(name);
            if (known.Contains(key) || retired.Contains(key))
            {
                continue;
            }
            // 自动发现不能绕过定价边界；新模型由管理员定价后再手动启用。
            string modelId = await _repository.NextPrefixedIdAsync("MODEL", cancellationToken).ConfigureAwait(false);
            missing.Add(new ChannelModel
            {
                ID = modelId,
                ChannelID = channelId,
                ModelKey = name,
                ProviderModelKey = name,
                DisplayName = name,
                BillingMode = "fixed_request",
                PriceVersion = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        long added = await _repository.CreateMissingChannelModelsAsync(missing, cancellationToken)
            .ConfigureAwait(false);
        if (added > 0)
        {
            _logicalModels.InvalidateRouteCatalog();
        }
        return new AdminChannelModelFetchResultDto(models, added);
    }

    /// <summary>只导入管理员明确选择、且仍存在于上游目录中的模型。对应 Go: <c>ImportAdminChannelModels</c>。</summary>
    public async Task<AdminChannelModelFetchResultDto> ImportAdminChannelModelsAsync(
        User actor,
        string channelId,
        IReadOnlyList<string>? selected,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        ModelChannel channel = await LoadChannelForCatalogAsync(actor, channelId, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> models = await FetchCatalogKeysAsync(actor, channelId, cancellationToken)
            .ConfigureAwait(false);
        if (selected is null || selected.Count == 0)
        {
            throw AppError.BadAuthRequest("请至少选择一个要导入的模型");
        }
        if (selected.Count > 500)
        {
            throw AppError.BadAuthRequest("单次最多导入 500 个模型");
        }

        Dictionary<string, string> available = new(StringComparer.Ordinal);
        foreach (string name in models)
        {
            available[CatalogKey(name)] = name;
        }
        List<string> chosen = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string rawName in selected)
        {
            string name = rawName.Trim();
            if (name.StartsWith("models/", StringComparison.Ordinal))
            {
                name = name["models/".Length..];
            }
            string key = CatalogKey(name);
            if (key.Length == 0)
            {
                continue;
            }
            if (!available.TryGetValue(key, out string? canonical))
            {
                throw AppError.BadAuthRequest("所选模型不在上游模型目录中：" + name);
            }
            if (!seen.Add(key))
            {
                continue;
            }
            chosen.Add(canonical);
        }
        if (chosen.Count == 0)
        {
            throw AppError.BadAuthRequest("请至少选择一个有效的模型");
        }

        IReadOnlyList<ChannelModel> existing = await _repository.ChannelModelsAsync(
            channelId, false, cancellationToken).ConfigureAwait(false);
        HashSet<string> known = existing
            .Select(item =>
            {
                string providerKey = LogicalModelService.FirstNonEmpty(item.ProviderModelKey, item.ModelKey);
                return CatalogKey(providerKey);
            })
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> retired = RetiredChannelKeys(channel.RetiredModelsJSON);

        List<ChannelModel> missing = [];
        foreach (string name in chosen)
        {
            string key = CatalogKey(name);
            if (known.Contains(key) || retired.Contains(key))
            {
                continue;
            }
            string modelId = await _repository.NextPrefixedIdAsync("MODEL", cancellationToken).ConfigureAwait(false);
            missing.Add(new ChannelModel
            {
                ID = modelId,
                ChannelID = channelId,
                ModelKey = name,
                DisplayName = name,
                BillingMode = "fixed_request",
                PriceVersion = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            known.Add(key);
        }
        long added = await _repository.CreateMissingChannelModelsAsync(missing, cancellationToken)
            .ConfigureAwait(false);
        if (added > 0)
        {
            _logicalModels.InvalidateRouteCatalog();
        }
        return new AdminChannelModelFetchResultDto(chosen, added);
    }

    /// <summary>对应 Go: <c>fetchAdminChannelModelCatalog</c>（用渠道保存的密钥与请求头访问上游）。</summary>
    private async Task<IReadOnlyList<string>> FetchCatalogKeysAsync(
        User actor,
        string channelId,
        CancellationToken cancellationToken)
    {
        ModelChannel channel = await LoadChannelForCatalogAsync(actor, channelId, cancellationToken)
            .ConfigureAwait(false);
        return await FetchChannelModelsViaCatalogAsync(actor, channel, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModelChannel> LoadChannelForCatalogAsync(
        User actor,
        string channelId,
        CancellationToken cancellationToken)
    {
        ModelChannel? channel = await _repository.AdminSystemChannelAsync(channelId, cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            throw new InvalidOperationException("record not found");
        }
        return channel;
    }

    private async Task<IReadOnlyList<string>> FetchChannelModelsViaCatalogAsync(
        User actor,
        ModelChannel channel,
        CancellationToken cancellationToken)
    {
        List<Outbound.OutboundHeader> headers = Outbound.OutboundGuard.ParseOutboundHeadersJson(channel.HeadersJSON);
        IReadOnlyList<string> models = await _catalog.FetchChannelModelsAsync(
            actor,
            new ChannelModelCatalogService.CatalogRequest(
                channel.BaseURL, channel.APIKey, channel.APIFormat, headers),
            cancellationToken).ConfigureAwait(false);
        return models.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>对应 Go: <c>retiredChannelModelKeys</c>。</summary>
    private static HashSet<string> RetiredChannelKeys(string? raw)
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
            string key = CatalogKey(value);
            if (key.Length > 0)
            {
                result.Add(key);
            }
        }
        return result;
    }

    /// <summary>对应 Go: <c>channelModelCatalogKey</c>。</summary>
    private static string CatalogKey(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith("models/", StringComparison.Ordinal))
        {
            trimmed = trimmed["models/".Length..];
        }
        return trimmed.ToLowerInvariant();
    }

    // ------------------------------------------------------------ 合同与校验

    /// <summary>
    /// 管理端渠道模型连通性测试。对应 Go: <c>app.TestAdminChannelModel</c>。
    /// 复用真实生成协议与运行时并发/熔断策略，不创建用户任务或计费订单。
    /// </summary>
    public async Task<long> TestAdminChannelModelAsync(
        User actor,
        string channelId,
        ChannelModelRequest request,
        TaskWorkerService worker,
        CancellationToken cancellationToken = default)
    {
        if (worker is null)
        {
            throw AppError.New(503, "任务执行引擎尚未就绪");
        }
        ModelChannel channel = await _repository.AdminSystemChannelAsync(channelId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.NotFound("系统渠道不存在或已停用");
        (string modelKey, string providerModelKey, string capability, string protocol) =
            NormalizeChannelModelContract(channel, request);
        Capabilities.ModelCapabilityConfig? profile = null;
        if (capability is "text" or "image" or "video")
        {
            profile = ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
                capability, protocol, providerModelKey, request.CapabilityConfig);
        }
        if (channel.BaseURL.Trim().Length == 0 || channel.APIKey.Trim().Length == 0)
        {
            throw AppError.BadAuthRequest("请先在渠道中配置 Base URL 和 API Key");
        }
        List<Outbound.OutboundHeader> headers = Outbound.OutboundGuard.ParseOutboundHeadersJson(channel.HeadersJSON);

        string prompt = capability switch
        {
            "image" => "A simple gray circle on a white background.",
            "video" => "A static gray circle on a white background.",
            "audio" => "Model test.",
            _ => "Reply with OK.",
        };
        string videoSeconds = "6";
        string imageSize = "", imageQuality = "";
        string videoRatio = "16:9", videoResolution = "720";
        if (capability == "image")
        {
            (imageSize, imageQuality) = ImageTestDefaults(profile?.Image);
        }
        if (capability == "video")
        {
            videoResolution = VideoTestDefaultResolution(profile?.Video);
        }
        TextTaskInput input = new()
        {
            Mode = capability,
            Prompt = prompt,
            Config = new ProviderConfig
            {
                ChannelID = channel.ID,
                APIFormat = channel.APIFormat,
                InterfaceType = protocol,
                BaseURL = channel.BaseURL,
                APIKey = channel.APIKey,
                SecretKey = channel.SecretKey ?? "",
                Headers = headers,
                Model = providerModelKey,
                ChannelModelKey = modelKey,
                Size = capability switch { "image" => imageSize, "video" => videoRatio, _ => "" },
                Quality = imageQuality,
                Count = "1",
                VideoSeconds = videoSeconds,
                VQuality = videoResolution,
                VideoGenerateAudio = "false",
                VideoWatermark = "false",
                AudioVoice = "alloy",
                AudioFormat = "mp3",
                AudioSpeed = "1",
            },
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal),
            ImageCapability = null,
            VideoCapability = null,
        };
        return await worker.RunProviderProbeAsync(capability, input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 视频测试默认分辨率。.NET 的 VideoCapabilityConfig 只声明分辨率枚举
    /// （比例由声明式协议回填），取第一个枚举值，缺省 720。
    /// 对应 Go: <c>videoTestDefaults</c>。
    /// </summary>
    private static string VideoTestDefaultResolution(Capabilities.VideoCapabilityConfig? profile)
    {
        if (profile is null || profile.Resolutions.Count == 0)
        {
            return "720";
        }
        string resolution = profile.Resolutions[0].Trim();
        return resolution.Length > 0 ? resolution : "720";
    }

    /// <summary>图片测试默认尺寸/质量。对应 Go: <c>imageTestDefaults</c>。</summary>
    private static (string Size, string Quality) ImageTestDefaults(Capabilities.ImageCapabilityConfig? profile)
    {
        if (profile is null)
        {
            return ("1024x1024", "auto");
        }
        string size = profile.Size.Parameter != "none" ? profile.Size.Default.Trim() : "";
        string quality = profile.Quality.Supported ? profile.Quality.Default.Trim() : "";
        return (size.Length > 0 ? size : "1024x1024", quality.Length > 0 ? quality : "auto");
    }

    /// <summary>对应 Go: <c>normalizeChannelModelContract</c>。</summary>
    private static (string ModelKey, string ProviderModelKey, string Capability, string Protocol)
        NormalizeChannelModelContract(ModelChannel channel, ChannelModelRequest request)
    {
        string modelKey = request.ModelKey.Trim();
        if (modelKey.StartsWith("models/", StringComparison.Ordinal))
        {
            modelKey = modelKey["models/".Length..];
        }
        if (modelKey.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写模型标识");
        }
        string providerModelKey = request.ProviderModelKey.Trim();
        if (providerModelKey.StartsWith("models/", StringComparison.Ordinal))
        {
            providerModelKey = providerModelKey["models/".Length..];
        }
        if (providerModelKey.Length == 0)
        {
            providerModelKey = modelKey;
        }
        string capability = CapabilitySpecOps.NormalizeCapability(request.Capability);
        if (capability.Length == 0)
        {
            throw AppError.BadAuthRequest("请选择模型能力");
        }

        if (!ProtocolRegistry.Shared.TryResolve(request.Protocol.Trim(), out ProtocolMetadata? metadata) ||
            !metadata.Enabled || metadata.UnavailableReason.Length > 0)
        {
            throw AppError.BadAuthRequest("请选择有效的模型请求协议");
        }
        string protocol = metadata.Id;
        string expected = ProtocolRegistry.CapabilityOf(metadata);
        if (expected.Length > 0 && expected != capability)
        {
            throw AppError.BadAuthRequest("模型能力与请求协议不匹配");
        }
        if ((protocol == "volcengine-jimeng-image" || protocol == "volcengine-jimeng-video") &&
            (channel.APIKey.Trim().Length == 0 || channel.SecretKey.Trim().Length == 0))
        {
            throw AppError.BadAuthRequest("即梦官方协议需要先在渠道中配置 Access Key 和 Secret Key");
        }
        return (modelKey, providerModelKey, capability, protocol);
    }

    /// <summary>对应 Go: <c>normalizeChannelModelPriceTiers</c>。旧 API 无 priceTiers 时等价一个默认档。</summary>
    private async Task<List<ChannelModelPriceTier>> NormalizeChannelModelPriceTiersAsync(
        ChannelModelRequest request,
        string capability,
        string protocol,
        string fallbackProviderModelKey,
        CancellationToken cancellationToken)
    {
        List<ChannelModelPriceTierRequest> inputs = request.PriceTiers ?? [];
        if (inputs.Count == 0)
        {
            inputs =
            [
                new ChannelModelPriceTierRequest
                {
                    Resolution = "*",
                    ProviderModelKey = fallbackProviderModelKey,
                    BillingMode = request.BillingMode,
                    UnitPriceMicrocredits = request.UnitPriceMicrocredits,
                    InputTokenPriceMicrocredits = request.InputTokenPriceMicrocredits,
                    OutputTokenPriceMicrocredits = request.OutputTokenPriceMicrocredits,
                    CachedTokenPriceMicrocredits = request.CachedTokenPriceMicrocredits,
                    PriceConfigured = request.PriceConfigured,
                    Enabled = true,
                },
            ];
        }

        List<ChannelModelPriceTier> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ChannelModelPriceTierRequest input in inputs)
        {
            (Dictionary<string, string> selector, string resolution, long videoSeconds) =
                NormalizeChannelModelTierSelector(capability, input);
            (Dictionary<string, string> _, string key) = ModelSku.CanonicalSkuSelector(selector);
            if (!seen.Add(key))
            {
                throw AppError.BadAuthRequest("同一个操作和规格组合只能配置一个价格档");
            }
            string billingMode = input.BillingMode.Trim();
            if (billingMode.Length == 0)
            {
                billingMode = "fixed_request";
            }
            ValidateChannelModelTierPricing(capability, protocol, billingMode, input);

            string tierId = await _repository.NextPrefixedIdAsync("PTIER", cancellationToken).ConfigureAwait(false);
            string providerModelKey = LogicalModelService.FirstNonEmpty(
                input.ProviderModelKey, fallbackProviderModelKey);
            if (providerModelKey.StartsWith("models/", StringComparison.Ordinal))
            {
                providerModelKey = providerModelKey["models/".Length..];
            }
            result.Add(new ChannelModelPriceTier
            {
                ID = tierId,
                SelectorKey = key,
                SelectorJSON = key,
                Selector = selector,
                Resolution = resolution,
                VideoSeconds = videoSeconds,
                ProviderModelKey = providerModelKey.Trim(),
                BillingMode = billingMode,
                UnitPriceMicrocredits = input.UnitPriceMicrocredits,
                InputTokenPriceMicrocredits = input.InputTokenPriceMicrocredits,
                OutputTokenPriceMicrocredits = input.OutputTokenPriceMicrocredits,
                CachedTokenPriceMicrocredits = input.CachedTokenPriceMicrocredits,
                PriceConfigured = input.PriceConfigured,
                Enabled = input.Enabled is null || input.Enabled.Value,
                PriceVersion = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        return result;
    }

    /// <summary>对应 Go: <c>normalizeChannelModelTierSelector</c>。</summary>
    private static (Dictionary<string, string> Selector, string Resolution, long VideoSeconds)
        NormalizeChannelModelTierSelector(string capability, ChannelModelPriceTierRequest input)
    {
        Dictionary<string, string> selector = CapabilitySpecOps.SortedMap<string>();
        foreach ((string rawKey, string rawValue) in input.Selector ?? [])
        {
            string key = rawKey.Trim();
            string value = rawValue.Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                continue;
            }
            switch (key)
            {
                case "operation":
                case "quality":
                case "size":
                {
                    value = value.ToLowerInvariant();
                    if (key is "quality" or "size" && value is "auto" or "any")
                    {
                        value = "*";
                    }
                    break;
                }
                case "vquality":
                    value = ModelCapabilityConfigOps.NormalizeChannelModelTierResolution(value);
                    break;
                case "videoSeconds":
                {
                    if (!long.TryParse(value, out long seconds) || seconds < 0)
                    {
                        throw AppError.BadAuthRequest("视频价格档时长必须是非负整数");
                    }
                    if (seconds == 0)
                    {
                        continue;
                    }
                    value = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                }
                case "imageCount":
                {
                    if (!long.TryParse(value, out long count) || count < 0)
                    {
                        throw AppError.BadAuthRequest("参考图片数量必须是非负整数");
                    }
                    if (count == 0)
                    {
                        continue;
                    }
                    value = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                }
                default:
                    throw AppError.BadAuthRequest("价格档不支持规格字段：" + key);
            }
            selector[key] = value;
        }

        if (capability == "video")
        {
            if (!selector.ContainsKey("vquality"))
            {
                string resolution = ModelCapabilityConfigOps.NormalizeChannelModelTierResolution(input.Resolution);
                if (resolution != "*")
                {
                    selector["vquality"] = resolution;
                }
            }
            if (!selector.ContainsKey("videoSeconds") && input.VideoSeconds > 0)
            {
                selector["videoSeconds"] = input.VideoSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        else if (input.Resolution.Length > 0 &&
                 ModelCapabilityConfigOps.NormalizeChannelModelTierResolution(input.Resolution) != "*")
        {
            throw AppError.BadAuthRequest("非视频模型不能使用视频分辨率价格档");
        }
        else if (input.VideoSeconds != 0)
        {
            throw AppError.BadAuthRequest("非视频模型不能使用视频时长价格档");
        }

        foreach (string key in new[] { "quality", "size" })
        {
            if (selector.ContainsKey(key) && capability != "image")
            {
                throw AppError.BadAuthRequest("只有图片模型可以按 " + key + " 配置价格档");
            }
        }
        if (selector.ContainsKey("vquality") && capability != "video")
        {
            throw AppError.BadAuthRequest("只有视频模型可以按分辨率配置价格档");
        }
        if (selector.ContainsKey("videoSeconds") && capability != "video")
        {
            throw AppError.BadAuthRequest("只有视频模型可以按时长配置价格档");
        }
        if (selector.ContainsKey("imageCount") && capability != "video")
        {
            throw AppError.BadAuthRequest("只有视频模型可以按参考图片数量配置价格档");
        }

        string resolvedResolution = selector.TryGetValue("vquality", out string? quality) && quality.Length > 0
            ? quality
            : "*";
        long resolvedSeconds = 0;
        if (selector.TryGetValue("videoSeconds", out string? secondsText) &&
            long.TryParse(secondsText, out long parsedSeconds))
        {
            resolvedSeconds = parsedSeconds;
        }
        return (selector, resolvedResolution, resolvedSeconds);
    }

    /// <summary>对应 Go: <c>validateChannelModelTierPricing</c>。</summary>
    private static void ValidateChannelModelTierPricing(
        string capability,
        string protocol,
        string billingMode,
        ChannelModelPriceTierRequest input)
    {
        if (billingMode is not ("fixed_request" or "per_second" or "token"))
        {
            throw AppError.BadAuthRequest("模型计费方式仅支持按次、按秒或 Token");
        }
        if (billingMode == "per_second" && capability != "video")
        {
            throw AppError.BadAuthRequest("只有视频模型可以按秒计费");
        }
        if (billingMode == "token" && !ModelCapabilityConfigOps.SupportsTokenBilling(capability, protocol))
        {
            throw AppError.BadAuthRequest("Token 计费仅支持文本模型和火山方舟视频协议");
        }
        if (input.UnitPriceMicrocredits < 0 || input.InputTokenPriceMicrocredits < 0 ||
            input.OutputTokenPriceMicrocredits < 0 || input.CachedTokenPriceMicrocredits < 0)
        {
            throw AppError.BadAuthRequest("模型积分价格不能小于 0");
        }
        if (!input.PriceConfigured)
        {
            return;
        }
        if (input.InputTokenPriceMicrocredits > MaxTokenPriceMicrocredits ||
            input.OutputTokenPriceMicrocredits > MaxTokenPriceMicrocredits ||
            input.CachedTokenPriceMicrocredits > MaxTokenPriceMicrocredits)
        {
            throw AppError.BadAuthRequest("Token 每百万用量价格不能超过 1,000,000 积分");
        }
    }

    /// <summary>对应 Go: <c>applyChannelModelPriceTierSummary</c>。摘要档优先取通配档。</summary>
    private static void ApplyChannelModelPriceTierSummary(ChannelModel item, IReadOnlyList<ChannelModelPriceTier> tiers)
    {
        item.PriceConfigured = false;
        item.BillingMode = "fixed_request";
        item.UnitPriceMicrocredits = 0;
        item.InputTokenPriceMicrocredits = 0;
        item.OutputTokenPriceMicrocredits = 0;
        item.CachedTokenPriceMicrocredits = 0;

        ChannelModelPriceTier? summary = null;
        foreach (ChannelModelPriceTier tier in tiers)
        {
            if (tier.Enabled && tier.PriceConfigured)
            {
                item.PriceConfigured = true;
            }
            if (summary is null || (tier.Resolution == "*" && tier.VideoSeconds == 0))
            {
                summary = tier;
            }
        }
        if (summary is null)
        {
            return;
        }
        item.BillingMode = summary.BillingMode;
        item.UnitPriceMicrocredits = summary.UnitPriceMicrocredits;
        item.InputTokenPriceMicrocredits = summary.InputTokenPriceMicrocredits;
        item.OutputTokenPriceMicrocredits = summary.OutputTokenPriceMicrocredits;
        item.CachedTokenPriceMicrocredits = summary.CachedTokenPriceMicrocredits;
    }

    /// <summary>对应 Go: <c>validateChannelModelTierCapabilities</c>。价格档规格必须落在能力配置内。</summary>
    private static void ValidateChannelModelTierCapabilities(
        IReadOnlyList<ChannelModelPriceTier> tiers,
        string rawCapabilityConfig,
        string capability)
    {
        if (capability != "video")
        {
            return;
        }
        ModelCapabilityConfig? config;
        try
        {
            config = ModelCapabilityConfigOps.DecodeModelCapabilityConfig(rawCapabilityConfig);
        }
        catch (AppError)
        {
            config = null;
        }
        if (config?.Video is null)
        {
            throw AppError.BadAuthRequest("视频模型能力配置无效，无法校验价格档规格");
        }

        HashSet<string> resolutionSupported = new(StringComparer.Ordinal);
        foreach (string resolution in config.Video.Resolutions)
        {
            resolutionSupported.Add(ModelCapabilityConfigOps.NormalizeChannelModelTierResolution(resolution));
        }
        HashSet<long> durationSupported = new((config.Video.Duration.Values ?? []).Select(value => (long)value));

        foreach (ChannelModelPriceTier tier in tiers)
        {
            if (tier.Resolution != "*" &&
                !resolutionSupported.Contains(ModelCapabilityConfigOps.NormalizeChannelModelTierResolution(tier.Resolution)))
            {
                throw AppError.BadAuthRequest("价格档分辨率不在该视频模型支持范围内：" + tier.Resolution);
            }
            if (tier.VideoSeconds == 0)
            {
                continue;
            }
            // DurationSupported 为 nil 视为支持（Go videoDurationSupported）。
            if (config.Video.DurationSupported is false)
            {
                continue;
            }
            if (config.Video.Duration.Selection == "enum" && !durationSupported.Contains(tier.VideoSeconds))
            {
                throw AppError.BadAuthRequest($"价格档时长 {tier.VideoSeconds} 秒不在该视频模型支持范围内");
            }
            if (config.Video.Duration.Selection == "range" &&
                (tier.VideoSeconds < config.Video.Duration.Min || tier.VideoSeconds > config.Video.Duration.Max ||
                 (config.Video.Duration.Step > 0 && (tier.VideoSeconds - config.Video.Duration.Min) % config.Video.Duration.Step != 0)))
            {
                throw AppError.BadAuthRequest($"价格档时长 {tier.VideoSeconds} 秒不在该视频模型支持范围内");
            }
        }
    }

    /// <summary>对应 Go: <c>normalizeAdminChannelModelDeleteIDs</c>。</summary>
    private static List<string> NormalizeDeleteIds(IReadOnlyList<string> values)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string value in values)
        {
            string id = value.Trim();
            if (id.Length == 0 || !seen.Add(id))
            {
                continue;
            }
            result.Add(id);
        }
        if (result.Count == 0)
        {
            throw AppError.BadAuthRequest("请至少选择一个要删除的渠道模型");
        }
        if (result.Count > MaxBatchDeleteCount)
        {
            throw AppError.BadAuthRequest("单次最多删除 100 个渠道模型");
        }
        return result;
    }
}
