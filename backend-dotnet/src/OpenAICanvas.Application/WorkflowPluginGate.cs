#nullable enable
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>用户可见的插件状态视图。对应 Go: <c>PluginStateView</c>（workflow 子集）。</summary>
public sealed class WorkflowPluginStateView
{
    [System.Text.Json.Serialization.JsonPropertyName("pluginId")]
    public string PluginID { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("platformAvailable")]
    public bool PlatformAvailable { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("userEnabled")]
    public bool UserEnabled { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("userConfigured")]
    public bool UserConfigured { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("effectiveEnabled")]
    public bool EffectiveEnabled { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("canToggle")]
    public bool CanToggle { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("canConfigure")]
    public bool CanConfigure { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("blockedReason")]
    public string? BlockedReason { get; set; }
}

/// <summary>
/// RunningHub 工作流插件的可用性门控。
/// 对应 Go: <c>workflow_plugins.go</c> + <c>plugin_management.go</c> 中 user-scope 插件的判定子集。
/// </summary>
/// <remarks>
/// C# 侧没有 runtime 插件清单，平台状态行是唯一真源：无平台行即视为平台不可用
/// （与 Go 内置清单 Enabled:false 的默认部署行为一致）。用户未保存过个人选择时，
/// userEnabled 默认取平台可用值（保留旧的"全局可用即全员可用"行为，Go 注释同款）。
/// </remarks>
public sealed class WorkflowPluginGate
{
    /// <summary>插件中心 ID。注意 ProviderWorkflowValues 返回的 "runninghub" 是 Provider ID，不是本 ID。</summary>
    public const string RunningHub = "runninghub-workflow-provider";

    private static readonly string[] KnownPluginIDs = [RunningHub];

    private readonly Repository _repository;

    public WorkflowPluginGate(Repository repository)
    {
        _repository = repository;
    }

    /// <summary>interfaceType → 插件中心 ID。对应 Go: <c>workflowPluginIDForInterface</c>。</summary>
    public static (string PluginID, bool Ok) PluginIDForInterface(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "runninghub-workflow-image" or "runninghub-workflow-video" or "runninghub-workflow-audio"
                => (RunningHub, true),
            _ => ("", false),
        };
    }

    /// <summary>平台状态行（缓存约定：管理端点写后直读，读路径每次查库，量级为 1 行）。</summary>
    private async Task<bool> PlatformAvailableAsync(string pluginID, CancellationToken cancellationToken)
    {
        PluginPlatformState? platformState = await _repository
            .PluginPlatformStateAsync(pluginID, cancellationToken).ConfigureAwait(false);
        return platformState?.Available ?? false;
    }

    /// <summary>单插件状态视图。对应 Go: <c>pluginStateForUser</c>（user-scope 分支）。</summary>
    public async Task<WorkflowPluginStateView> StateForUserAsync(
        string userID, string pluginID, CancellationToken cancellationToken)
    {
        if (!KnownPluginIDs.Contains(pluginID, StringComparer.Ordinal))
        {
            throw AppError.NotFound("插件不存在");
        }

        bool platformAvailable = await PlatformAvailableAsync(pluginID, cancellationToken).ConfigureAwait(false);

        bool userEnabled = false;
        bool userConfigured = false;
        UserPluginState? userState = await _repository
            .UserPluginStateAsync(userID, pluginID, cancellationToken).ConfigureAwait(false);
        if (userState is not null)
        {
            userEnabled = userState.Enabled;
            userConfigured = true;
        }
        else
        {
            // 未保存个人选择时沿用平台可用值（Go 对 RunningHub 取 runtime 状态，
            // 此处以平台状态行替代，默认部署两端同为禁用）。
            userEnabled = platformAvailable;
        }

        bool effective = platformAvailable && userEnabled;
        return new WorkflowPluginStateView
        {
            PluginID = pluginID,
            PlatformAvailable = platformAvailable,
            UserEnabled = userEnabled,
            UserConfigured = userConfigured,
            EffectiveEnabled = effective,
            CanToggle = platformAvailable,
            CanConfigure = platformAvailable,
            BlockedReason = platformAvailable ? null : "管理员已停用该插件",
        };
    }

    /// <summary>用户维度的 statuses + states（GET /plugins/status 的 data）。</summary>
    public async Task<(IReadOnlyDictionary<string, string> Statuses, IReadOnlyDictionary<string, WorkflowPluginStateView> States)>
        StatusesForUserAsync(string userID, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> statuses = new(StringComparer.Ordinal);
        Dictionary<string, WorkflowPluginStateView> states = new(StringComparer.Ordinal);
        foreach (string pluginID in KnownPluginIDs)
        {
            WorkflowPluginStateView state = await StateForUserAsync(userID, pluginID, cancellationToken).ConfigureAwait(false);
            statuses[pluginID] = state.EffectiveEnabled ? "enabled" : "disabled";
            states[pluginID] = state;
        }
        return (statuses, states);
    }

    /// <summary>按平台状态判定（worker 后台执行用，无用户维度）。对应 Go: <c>RequireWorkflowPluginForInterface</c>。</summary>
    public async Task RequireForInterfaceAsync(string interfaceType, CancellationToken cancellationToken = default)
    {
        (string pluginID, bool ok) = PluginIDForInterface(interfaceType);
        if (!ok)
        {
            throw AppError.Forbidden("未知工作流插件");
        }
        if (!await PlatformAvailableAsync(pluginID, cancellationToken).ConfigureAwait(false))
        {
            throw AppError.Forbidden("RunningHub 工作流插件未启用");
        }
    }

    /// <summary>按 EffectiveEnabled 判定（任务创建 / 管理代理用）。对应 Go: <c>RequireWorkflowPluginForUser</c>。</summary>
    public async Task RequireForUserAsync(string userID, string interfaceType, CancellationToken cancellationToken = default)
    {
        (string pluginID, bool ok) = PluginIDForInterface(interfaceType);
        if (!ok)
        {
            throw AppError.Forbidden("未知工作流插件");
        }
        WorkflowPluginStateView state = await StateForUserAsync(userID, pluginID, cancellationToken).ConfigureAwait(false);
        if (!state.EffectiveEnabled)
        {
            throw AppError.Forbidden("RunningHub 工作流插件未启用");
        }
    }
}
