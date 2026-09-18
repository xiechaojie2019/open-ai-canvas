using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Platform;

/// <summary>
/// 功能开放配置。对应 Go: <c>platform.FeatureAvailability</c>（struct，字段顺序即输出顺序）。
/// </summary>
public sealed class FeatureAvailability
{
    [JsonPropertyName("welcomeEnabled")]
    public bool WelcomeEnabled { get; set; } = true;

    [JsonPropertyName("shortDramaEnabled")]
    public bool ShortDramaEnabled { get; set; } = true;

    [JsonPropertyName("taskCenterEnabled")]
    public bool TaskCenterEnabled { get; set; } = true;

    [JsonPropertyName("creditsEnabled")]
    public bool CreditsEnabled { get; set; } = true;

    [JsonPropertyName("customChannelsEnabled")]
    public bool CustomChannelsEnabled { get; set; } = true;

    /// <summary>前台模型目录默认关闭，需要运维明确配置后才开放。</summary>
    [JsonPropertyName("frontendModelsEnabled")]
    public bool FrontendModelsEnabled { get; set; }

    [JsonPropertyName("pluginCenterEnabled")]
    public bool PluginCenterEnabled { get; set; } = true;

    [JsonPropertyName("systemPluginsVisibleToUsers")]
    public bool SystemPluginsVisibleToUsers { get; set; } = true;

    [JsonPropertyName("timelineTranscriptionEnabled")]
    public bool TimelineTranscriptionEnabled { get; set; } = true;
}

/// <summary>
/// 公开功能开放配置。对应 Go: <c>platform.PublicFeatureAvailability</c>。
/// </summary>
/// <remarks>
/// Go 用结构体嵌入 <see cref="FeatureAvailability"/>，JSON 会平铺到同一层；
/// 这里按声明顺序显式展开，保证字段顺序与 Go 一致。
/// </remarks>
public sealed class PublicFeatureAvailability
{
    [JsonPropertyName("welcomeEnabled")]
    public bool WelcomeEnabled { get; init; }

    [JsonPropertyName("shortDramaEnabled")]
    public bool ShortDramaEnabled { get; init; }

    [JsonPropertyName("taskCenterEnabled")]
    public bool TaskCenterEnabled { get; init; }

    [JsonPropertyName("creditsEnabled")]
    public bool CreditsEnabled { get; init; }

    [JsonPropertyName("customChannelsEnabled")]
    public bool CustomChannelsEnabled { get; init; }

    [JsonPropertyName("frontendModelsEnabled")]
    public bool FrontendModelsEnabled { get; init; }

    [JsonPropertyName("pluginCenterEnabled")]
    public bool PluginCenterEnabled { get; init; }

    [JsonPropertyName("systemPluginsVisibleToUsers")]
    public bool SystemPluginsVisibleToUsers { get; init; }

    [JsonPropertyName("timelineTranscriptionEnabled")]
    public bool TimelineTranscriptionEnabled { get; init; }

    /// <summary>是否已由运维显式配置过（没有设置记录时为 false）。</summary>
    [JsonPropertyName("configured")]
    public bool Configured { get; init; }

    [JsonPropertyName("updatedBy")]
    [GoOmitEmpty]
    public string UpdatedBy { get; init; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; init; }
}

/// <summary>功能标识。对应 Go: <c>platform.Feature*</c> 常量。</summary>
public static class FeatureNames
{
    public const string ShortDrama = "shortDrama";
    public const string TaskCenter = "taskCenter";
    public const string Credits = "credits";
    public const string CustomChannels = "customChannels";
    public const string FrontendModels = "frontendModels";
    public const string PluginCenter = "pluginCenter";
    public const string SystemPlugins = "systemPluginsVisibleToUsers";
    public const string TimelineTranscription = "timelineTranscription";
}

/// <summary>
/// 功能开放配置的读写与守卫。对应 Go: <c>internal/platform/feature_availability.go</c>。
/// </summary>
public sealed class FeatureAvailabilityService
{
    private const string SettingKey = "feature_availability";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Repository _repository;

    public FeatureAvailabilityService(Repository repository) => _repository = repository;

    /// <summary>默认全开放，但前台模型目录除外。</summary>
    public static FeatureAvailability Default() => new();

    /// <summary>读取当前配置。对应 Go: <c>FeatureAvailability()</c>。</summary>
    public async Task<PublicFeatureAvailability> GetAsync(CancellationToken cancellationToken = default)
    {
        (SystemSetting? setting, FeatureAvailability value) =
            await ReadAsync(cancellationToken).ConfigureAwait(false);
        return Project(setting, value);
    }

    /// <summary>判断单个功能是否开放。对应 Go: <c>FeatureEnabled</c>。</summary>
    public async Task<bool> FeatureEnabledAsync(string feature, CancellationToken cancellationToken = default)
    {
        (_, FeatureAvailability value) = await ReadAsync(cancellationToken).ConfigureAwait(false);

        return feature switch
        {
            FeatureNames.ShortDrama => value.ShortDramaEnabled,
            FeatureNames.TaskCenter => value.TaskCenterEnabled,
            FeatureNames.Credits => value.CreditsEnabled,
            FeatureNames.CustomChannels => value.CustomChannelsEnabled,
            FeatureNames.FrontendModels => value.FrontendModelsEnabled,
            FeatureNames.PluginCenter => value.PluginCenterEnabled,
            FeatureNames.SystemPlugins => value.SystemPluginsVisibleToUsers,
            FeatureNames.TimelineTranscription => value.TimelineTranscriptionEnabled,
            _ => throw new InvalidOperationException("未知功能开放配置"),
        };
    }

    /// <summary>功能守卫：未开放时抛出对应文案的 403。对应 Go: <c>RequireFeature</c>。</summary>
    public async Task RequireFeatureAsync(string feature, CancellationToken cancellationToken = default)
    {
        if (await FeatureEnabledAsync(feature, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        throw AppError.Forbidden(feature switch
        {
            FeatureNames.ShortDrama => "短剧创作暂未开放",
            FeatureNames.TaskCenter => "任务中心暂未开放",
            FeatureNames.Credits => "积分功能暂未开放",
            FeatureNames.CustomChannels => "自定义渠道暂未开放",
            FeatureNames.FrontendModels => "前台模型目录暂未开放",
            FeatureNames.PluginCenter => "插件中心暂未开放",
            FeatureNames.SystemPlugins => "系统插件暂未向普通用户展示",
            FeatureNames.TimelineTranscription => "字幕转写暂未开放",
            _ => "该功能暂未开放",
        });
    }

    /// <summary>写入配置。对应 Go: <c>UpdateFeatureAvailability</c>（审计写入另行接入）。</summary>
    public async Task<PublicFeatureAvailability> UpdateAsync(
        FeatureAvailability value,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        (SystemSetting? current, _) = await ReadAsync(cancellationToken).ConfigureAwait(false);

        SystemSetting setting = new()
        {
            Key = SettingKey,
            ValueJSON = JsonSerializer.Serialize(value),
            UpdatedBy = actorUserId,
            CreatedAt = current?.CreatedAt ?? default,
            UpdatedAt = DateTime.UtcNow,
        };

        await _repository.SaveSystemSettingAsync(setting, cancellationToken).ConfigureAwait(false);
        return Project(setting, value);
    }

    /// <summary>
    /// 读取设置。没有记录时返回默认值。
    /// </summary>
    /// <remarks>
    /// 反序列化以<b>默认值为基底</b>，这样老配置缺少新字段时不会意外把功能关掉——
    /// 与 Go 的实现一致。
    /// </remarks>
    private async Task<(SystemSetting? Setting, FeatureAvailability Value)> ReadAsync(
        CancellationToken cancellationToken)
    {
        SystemSetting? setting = await _repository.SystemSettingAsync(SettingKey, cancellationToken)
            .ConfigureAwait(false);

        if (setting is null)
        {
            return (null, Default());
        }

        if (string.IsNullOrWhiteSpace(setting.ValueJSON))
        {
            throw new InvalidOperationException("功能开放配置格式无效");
        }

        try
        {
            FeatureAvailability? parsed = JsonSerializer.Deserialize<FeatureAvailability>(
                setting.ValueJSON, JsonOptions);
            return parsed is null
                ? throw new InvalidOperationException("功能开放配置格式无效")
                : (setting, parsed);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("功能开放配置格式无效");
        }
    }

    private static PublicFeatureAvailability Project(SystemSetting? setting, FeatureAvailability value) => new()
    {
        WelcomeEnabled = value.WelcomeEnabled,
        ShortDramaEnabled = value.ShortDramaEnabled,
        TaskCenterEnabled = value.TaskCenterEnabled,
        CreditsEnabled = value.CreditsEnabled,
        CustomChannelsEnabled = value.CustomChannelsEnabled,
        FrontendModelsEnabled = value.FrontendModelsEnabled,
        PluginCenterEnabled = value.PluginCenterEnabled,
        SystemPluginsVisibleToUsers = value.SystemPluginsVisibleToUsers,
        TimelineTranscriptionEnabled = value.TimelineTranscriptionEnabled,
        Configured = setting is not null,
        UpdatedBy = setting?.UpdatedBy ?? string.Empty,
        UpdatedAt = setting is null || setting.UpdatedAt == default ? null : setting.UpdatedAt,
    };
}
