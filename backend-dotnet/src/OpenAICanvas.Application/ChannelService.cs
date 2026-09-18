#nullable enable
using System.Text.Json;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>渠道服务。对应 Go: <c>internal/app/admin.go</c> 的渠道部分。</summary>
public sealed class ChannelService
{
    private readonly Repository _repository;

    public ChannelService(Repository repository)
    {
        _repository = repository;
    }

    /// <summary>系统渠道公开列表。对应 Go: <c>PublicSystemChannels</c>。</summary>
    public async Task<IReadOnlyList<PublicModelChannelDto>> PublicSystemChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModelChannel> channels = await _repository.SystemChannelsAsync(false, cancellationToken)
            .ConfigureAwait(false);

        List<PublicModelChannelDto> result = new(channels.Count);
        foreach (ModelChannel channel in channels)
        {
            IReadOnlyList<ChannelModel> items = await _repository.ChannelModelsAsync(channel.ID, false, cancellationToken)
                .ConfigureAwait(false);
            result.Add(PublicChannel(channel, admin: false, items));
        }

        return result;
    }

    // ------------------------------------------------------------ 内部转换

    /// <summary>
    /// 渠道模型能力配置的展示投影。对应 Go <c>publicChannel</c> 的行为：
    /// 解码失败或空配置 → null（omitempty 省略）；归一化失败 → 保留原始解码值。
    /// </summary>
    private static ModelCapabilityConfig? CapabilityConfigForPresentation(ChannelModel item)
    {
        ModelCapabilityConfig? config;
        try
        {
            config = ModelCapabilityConfigOps.DecodeModelCapabilityConfig(item.CapabilityConfigJSON);
        }
        catch (AppError)
        {
            return null;
        }
        if (config is null)
        {
            return null;
        }
        try
        {
            ModelCapabilityConfig? normalized = ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
                item.Capability,
                item.Protocol,
                LogicalModelService.FirstNonEmpty(item.ProviderModelKey, item.ModelKey),
                config);
            // Go：normalizeErr == nil 时 capabilityConfig = normalized（可能是 nil）。
            return normalized;
        }
        catch (AppError)
        {
            // 归一化失败时 Go 保留原始解码值继续输出。
            return config;
        }
    }

    /// <summary>渠道公开投影。管理端与公开端共用；供 <see cref="ChannelAdminService"/> 复用。</summary>
    internal static PublicModelChannelDto PublicChannel(
        ModelChannel channel,
        bool admin,
        IReadOnlyList<ChannelModel> channelModels)
    {
        List<string> models = new(channelModels.Count);
        List<PublicChannelModelPriceDto> modelCosts = new(channelModels.Count);

        foreach (ChannelModel item in channelModels)
        {
            if (!item.Enabled)
            {
                continue;
            }

            models.Add(item.ModelKey);

            if (item.PriceConfigured)
            {
                modelCosts.Add(new PublicChannelModelPriceDto
                {
                    Model = item.ModelKey,
                    DisplayName = item.DisplayName,
                    Icon = item.Icon,
                    Capability = item.Capability,
                    Protocol = item.Protocol,
                    BillingMode = item.BillingMode,
                    UnitPriceMicrocredits = item.UnitPriceMicrocredits,
                    InputTokenPriceMicrocredits = item.InputTokenPriceMicrocredits,
                    OutputTokenPriceMicrocredits = item.OutputTokenPriceMicrocredits,
                    CachedTokenPriceMicrocredits = item.CachedTokenPriceMicrocredits,
                    CapabilityConfig = CapabilityConfigForPresentation(item),
                });
            }
        }

        if (models.Count == 0 && !string.IsNullOrEmpty(channel.ModelsJSON))
        {
            try
            {
                models = JsonSerializer.Deserialize<List<string>>(channel.ModelsJSON) ?? models;
            }
            catch (JsonException)
            {
                // 静默忽略损坏的 ModelsJSON。
            }
        }

        string apiKey = "";
        string baseURL = channel.BaseURL;
        List<OutboundHeaderDto>? headers = null;

        if (channel.Scope == "system")
        {
            if (!admin)
            {
                apiKey = "system";
                baseURL = "/api/ai/system/" + channel.ID;
            }
            if (admin)
            {
                headers = ParseHeaders(channel.HeadersJSON);
            }
        }
        else if (admin)
        {
            apiKey = channel.APIKey;
        }

        (string name, string alias) = admin
            ? (channel.Name, channel.PublicAlias)
            : (channel.PublicAlias, "");

        return new PublicModelChannelDto
        {
            ID = channel.ID,
            UserID = channel.UserID,
            Scope = channel.Scope,
            Enabled = channel.Enabled,
            Name = name,
            PublicAlias = alias,
            SortOrder = channel.SortOrder,
            BaseURL = baseURL,
            APIKey = apiKey,
            APIFormat = channel.APIFormat,
            ConcurrencyLimit = channel.ConcurrencyLimit,
            Models = models,
            ModelCosts = modelCosts,
            Headers = headers,
            HasAPIKey = !string.IsNullOrEmpty(channel.APIKey),
            HasSecretKey = !string.IsNullOrEmpty(channel.SecretKey),
            CreatedAt = channel.CreatedAt,
            UpdatedAt = channel.UpdatedAt,
        };
    }

    private static List<OutboundHeaderDto>? ParseHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<OutboundHeaderDto>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
