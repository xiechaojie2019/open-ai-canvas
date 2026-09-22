#nullable enable

using System.Text.Json;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;

namespace OpenAICanvas.Application;

/// <summary>用户可见的插件状态视图。对应 Go: <c>PluginStateView</c>。</summary>
public class PluginStateView
{
    public string PluginID { get; set; } = "";
    public bool PlatformAvailable { get; set; }
    public bool UserEnabled { get; set; }
    public bool UserConfigured { get; set; }
    public bool EffectiveEnabled { get; set; }
    public bool CanToggle { get; set; }
    public bool CanConfigure { get; set; }
    public string? BlockedReason { get; set; }
}

/// <summary>管理端的插件状态视图（附启用人数）。对应 Go: <c>AdminPluginStateView</c>。</summary>
public sealed class AdminPluginStateView : PluginStateView
{
    public long EnabledUserCount { get; set; }
}

/// <summary>
/// 插件管理服务：来源策略表、用户/平台两级可用性与管理端动作。
/// 对应 Go: <c>internal/app/plugin_management.go</c>。
/// </summary>
public sealed class PluginManagementService
{
    private readonly Repository _repository;
    private readonly PluginRuntime _runtime;
    private readonly FeatureAvailabilityService _features;

    public PluginManagementService(
        Repository repository, PluginRuntime runtime, FeatureAvailabilityService features)
    {
        _repository = repository;
        _runtime = runtime;
        _features = features;
    }

    /// <summary>
    /// 官方应用与系统支付插件的固定策略（activationScope/configurationScope）。
    /// 其余官方插件回落 official/protocol/system/system。
    /// </summary>
    private static PluginManagementView? ManagementFor(string pluginID) => pluginID.Trim() switch
    {
        "runninghub-workflow-provider" => new PluginManagementView
        {
            Origin = "official", Kind = "application", ActivationScope = "user", ConfigurationScope = "user",
        },
        "eagle-asset-connector" => new PluginManagementView
        {
            Origin = "official", Kind = "application", ActivationScope = "user", ConfigurationScope = "user",
        },
        "prompt-optimizer" or "portrait-clearance" or "ai-art-critique"
            or "media-conversion" or "editor-shell" => new PluginManagementView
        {
            Origin = "official", Kind = "application", ActivationScope = "user", ConfigurationScope = "none",
        },
        "official-payment-wechat-native" or "official-payment-alipay-page" => new PluginManagementView
        {
            Origin = "official", Kind = "system", ActivationScope = "system", ConfigurationScope = "system",
        },
        _ => null,
    };

    private static PluginManagementView ManagementForUploaded => new()
    {
        Origin = "uploaded", Kind = "protocol", ActivationScope = "system", ConfigurationScope = "system",
    };

    private static PluginManagementView ManagementFallback => new()
    {
        Origin = "official", Kind = "protocol", ActivationScope = "system", ConfigurationScope = "system",
    };

    private static PluginManagementView ManagementForPlugin(PluginView plugin) =>
        plugin.Source == "uploaded" ? ManagementForUploaded : ManagementFor(plugin.Manifest.ID) ?? ManagementFallback;

    /// <summary>把管理面投影写入插件视图；列表端点序列化前必须填充，前端合同要求 management 非空。</summary>
    public PluginManagementView ManagementFor(PluginView plugin) => ManagementForPlugin(plugin);

    private static bool HasManagementTable(string pluginID, string source) =>
        source == "uploaded" || ManagementFor(pluginID) is not null;

    /// <summary>单插件的用户状态视图。对应 Go: <c>pluginStateForUser</c>。</summary>
    public async Task<PluginStateView> StateForUserAsync(
        string userID, PluginView plugin, CancellationToken cancellationToken = default)
    {
        PluginManagementView management = ManagementForPlugin(plugin);
        bool platformAvailable;
        bool userEnabled;
        bool userConfigured = false;

        if (management.ActivationScope == "system")
        {
            platformAvailable = plugin.Status == "enabled";
            userEnabled = platformAvailable;
        }
        else
        {
            PluginPlatformState? platformState = await _repository
                .PluginPlatformStateAsync(plugin.Manifest.ID, cancellationToken).ConfigureAwait(false);
            // 与 WorkflowPluginGate 一致：无平台状态行即默认停用（RunningHub 登录后默认 disabled）。
            platformAvailable = platformState?.Available ?? false;

            UserPluginState? userState = await _repository
                .UserPluginStateAsync(userID, plugin.Manifest.ID, cancellationToken).ConfigureAwait(false);
            if (userState is not null)
            {
                userEnabled = userState.Enabled;
                userConfigured = true;
            }
            else
            {
                userEnabled = plugin.Manifest.ID == "media-conversion" || platformAvailable;
            }
        }

        bool effective = platformAvailable
            && (management.ActivationScope == "user" ? userEnabled : true);
        return new PluginStateView
        {
            PluginID = plugin.Manifest.ID,
            PlatformAvailable = platformAvailable,
            UserEnabled = userEnabled,
            UserConfigured = userConfigured,
            EffectiveEnabled = effective,
            CanToggle = management.ActivationScope == "user" && platformAvailable,
            CanConfigure = management.ConfigurationScope == "user" && platformAvailable,
            BlockedReason = !platformAvailable ? "管理员已停用该插件"
                : management.ActivationScope == "system" ? "系统插件由管理员统一管理"
                : null,
        };
    }

    /// <summary>用户维度 statuses + states。对应 Go: <c>PluginsForUser</c>。</summary>
    public async Task<(IReadOnlyDictionary<string, string> Statuses, IReadOnlyDictionary<string, PluginStateView> States)>
        PluginsForUserAsync(User actor, CancellationToken cancellationToken = default)
    {
        List<PluginView> plugins = _runtime.List();
        bool systemVisible = await _features
            .FeatureEnabledAsync(FeatureNames.SystemPlugins, cancellationToken).ConfigureAwait(false);

        Dictionary<string, string> statuses = new(StringComparer.Ordinal);
        Dictionary<string, PluginStateView> states = new(StringComparer.Ordinal);
        foreach (PluginView plugin in plugins)
        {
            PluginManagementView management = ManagementForPlugin(plugin);
            if (management.ActivationScope == "system" && !systemVisible)
            {
                continue;
            }
            PluginStateView state = await StateForUserAsync(actor.ID, plugin, cancellationToken).ConfigureAwait(false);
            statuses[state.PluginID] = state.EffectiveEnabled ? "enabled" : "disabled";
            states[state.PluginID] = state;
        }
        return (statuses, states);
    }

    /// <summary>任务/代理创建前的强校验。对应 Go: <c>RequirePluginForUser</c>。</summary>
    public async Task RequirePluginForUserAsync(
        string userID, string pluginID, CancellationToken cancellationToken = default)
    {
        PluginView? plugin = _runtime.List().FirstOrDefault(item => item.Manifest.ID == pluginID.Trim());
        if (plugin is null)
        {
            throw AppError.NotFound($"插件 {pluginID} 不存在");
        }
        PluginStateView state = await StateForUserAsync(userID, plugin, cancellationToken).ConfigureAwait(false);
        if (!state.EffectiveEnabled)
        {
            throw AppError.Forbidden(state.BlockedReason ?? "插件未启用");
        }
    }

    /// <summary>用户个人启停。对应 Go: <c>SetUserPluginEnabled</c>。</summary>
    public async Task<PluginStateView> SetUserPluginEnabledAsync(
        User actor, string pluginID, bool enabled, CancellationToken cancellationToken = default)
    {
        pluginID = pluginID.Trim();
        PluginView? plugin = _runtime.List().FirstOrDefault(item => item.Manifest.ID == pluginID);
        if (plugin is null && !HasManagementTable(pluginID, ""))
        {
            throw AppError.NotFound($"插件 {pluginID} 不存在");
        }
        PluginManagementView management = plugin is not null
            ? ManagementForPlugin(plugin)
            : ManagementFor(pluginID) ?? ManagementFallback;
        if (management.ActivationScope != "user")
        {
            throw AppError.Forbidden("系统插件只能由管理员统一管理");
        }
        PluginStateView current = plugin is null
            ? await StateForUserAsync(actor.ID, new PluginView
              {
                  Manifest = new PluginManifestView { ID = pluginID },
                  Status = "disabled",
              }, cancellationToken).ConfigureAwait(false)
            : await StateForUserAsync(actor.ID, plugin, cancellationToken).ConfigureAwait(false);
        if (!current.PlatformAvailable)
        {
            throw AppError.Forbidden("管理员已停用该插件");
        }

        await _repository.SaveUserPluginStateAsync(new UserPluginState
        {
            UserID = actor.ID,
            PluginID = pluginID,
            Enabled = enabled,
            UpdatedAt = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
        return await StateForUserAsync(actor.ID, plugin!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>管理端全量状态。对应 Go: <c>AdminPluginStates</c>。</summary>
    public async Task<List<AdminPluginStateView>> AdminPluginStatesAsync(
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, long> counts = await _repository
            .EnabledPluginUserCountsAsync(cancellationToken).ConfigureAwait(false);
        List<AdminPluginStateView> states = [];
        foreach (PluginView plugin in _runtime.List())
        {
            PluginStateView baseState = await StateForUserAsync("", plugin, cancellationToken).ConfigureAwait(false);
            states.Add(new AdminPluginStateView
            {
                PluginID = baseState.PluginID,
                PlatformAvailable = baseState.PlatformAvailable,
                UserEnabled = baseState.UserEnabled,
                UserConfigured = baseState.UserConfigured,
                EffectiveEnabled = baseState.EffectiveEnabled,
                CanToggle = baseState.CanToggle,
                CanConfigure = baseState.CanConfigure,
                BlockedReason = baseState.BlockedReason,
                EnabledUserCount = counts.TryGetValue(baseState.PluginID, out long count) ? count : 0,
            });
        }
        return states;
    }

    /// <summary>平台可用性开关（系统插件同时改运行时注册表状态）。</summary>
    public async Task SetPluginPlatformAvailabilityAsync(
        User actor, string pluginID, bool available, CancellationToken cancellationToken = default)
    {
        pluginID = pluginID.Trim();
        PluginView? plugin = _runtime.List().FirstOrDefault(item => item.Manifest.ID == pluginID);
        PluginManagementView management = plugin is not null
            ? ManagementForPlugin(plugin)
            : ManagementFor(pluginID) ?? ManagementFallback;

        if (management.ActivationScope == "system" && plugin is not null)
        {
            // 系统插件的"平台可用"直接等于运行时启用位；首期不改写清单，
            // 只允许停用方向（重新启用需重新配置支付渠道）。
        }

        PluginStateView state = plugin is null
            ? new PluginStateView { PluginID = pluginID, PlatformAvailable = available }
            : await StateForUserAsync(actor.ID, plugin, cancellationToken).ConfigureAwait(false);

        await _repository.SavePluginPlatformStateAsync(new PluginPlatformState
        {
            PluginID = pluginID,
            Available = available,
            UpdatedBy = actor.ID,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(actor, "plugin.availability.update", "plugin", pluginID,
            (available ? "启用" : "停用") + "插件 " + pluginID,
            new { available, kind = management.Kind, origin = management.Origin },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>管理员安装插件包。对应 Go: <c>InstallPluginForAdmin</c>。</summary>
    public async Task<PluginView> InstallForAdminAsync(
        User actor, byte[] packageBytes, string fileName, CancellationToken cancellationToken = default)
    {
        PluginView installed = await _runtime.InstallAsync(packageBytes, fileName, cancellationToken)
            .ConfigureAwait(false);
        string pluginID = installed.Manifest.ID;
        string source = installed.Source;
        if (ManagementFor(pluginID) is not null)
        {
            throw AppError.New(400, $"插件 {pluginID} 由官方应用保留");
        }
        bool paymentReserved = pluginID.StartsWith("official-payment-", StringComparison.Ordinal);
        if (paymentReserved && installed.Manifest.Contributes.PaymentProviders.Count == 0)
        {
            throw AppError.New(400, $"插件 {pluginID} 由系统支付插件保留");
        }

        await _repository.SavePluginPlatformStateAsync(new PluginPlatformState
        {
            PluginID = pluginID,
            Available = installed.Status == "enabled",
            UpdatedBy = actor.ID,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(actor, "plugin.install", "plugin", pluginID,
            "安装插件 " + pluginID,
            new { fileName = installed.FileName, sha256 = installed.Sha256 },
            cancellationToken).ConfigureAwait(false);
        return installed;
    }

    /// <summary>管理员卸载插件。对应 Go: <c>UninstallPluginForAdmin</c>。</summary>
    public async Task UninstallForAdminAsync(
        User actor, string pluginID, CancellationToken cancellationToken = default)
    {
        pluginID = pluginID.Trim();
        await _runtime.UninstallAsync(pluginID, cancellationToken).ConfigureAwait(false);
        await _repository.DeleteUserPluginStatesAsync(pluginID, cancellationToken).ConfigureAwait(false);
        await _repository.DeletePluginPlatformStateAsync(pluginID, cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(actor, "plugin.uninstall", "plugin", pluginID,
            "卸载插件 " + pluginID, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>插件包下载。对应 Go: <c>PluginPackageBytes</c>。</summary>
    public Task<byte[]> PackageBytesAsync(string pluginID, CancellationToken cancellationToken = default) =>
        _runtime.PackageBytesAsync(pluginID, cancellationToken);

    private async Task AppendAuditAsync(
        User actor, string action, string targetType, string targetId,
        string summary, object? metadata, CancellationToken cancellationToken)
    {
        await _repository.AppendAdminAuditAsync(new AdminAuditEvent
        {
            ID = IdGenerator.NewId(),
            ActorUserID = actor.ID,
            Action = action,
            TargetType = targetType,
            TargetID = targetId,
            Summary = summary,
            MetadataJSON = metadata is null ? string.Empty : JsonSerializer.Serialize(metadata),
            CreatedAt = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
    }
}
