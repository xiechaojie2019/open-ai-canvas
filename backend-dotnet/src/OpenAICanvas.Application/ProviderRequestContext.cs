#nullable enable
using OpenAICanvas.Platform;
using OpenAICanvas.Protocol;
using OpenAICanvas.Providers;

namespace OpenAICanvas.Application;

/// <summary>
/// 把 <see cref="Coordinator"/> 适配为 Provider 出站所需的运行时上下文。
/// 对应 Go: <c>Service.AcquireChannelSlot</c> / <c>Service.RecordChannelResult</c> /
/// <c>Coordinator.CircuitOpen</c> 在 <c>provider_http_client.go</c> 中的组合调用。
/// </summary>
/// <remarks>
/// 放在 <c>Application</c> 层是因为只有它能同时引用 <c>Platform</c> 与 <c>Providers</c>；
/// <c>Providers</c> 保持对运行时策略无感知，便于单测直接构造纯传输场景。
/// </remarks>
public sealed class ProviderRequestContext : IProviderRequestContext
{
    /// <summary>对应 Go 的并发槽租约 TTL 常量。</summary>
    private static readonly TimeSpan SlotTTL = TimeSpan.FromMinutes(5);

    private readonly Coordinator? _coordinator;
    private readonly IRuntimePolicyProvider _policy;
    private readonly Func<string, int>? _channelConcurrencyLimit;
    private readonly ProtocolAdapterRegistry? _declarativeAdapter;

    /// <param name="policy">运行时策略来源（读取响应上限、熔断阈值与渠道并发）。</param>
    /// <param name="coordinator">多实例协调器；<c>null</c> 时退化为单实例无协调行为。</param>
    /// <param name="channelConcurrencyLimit">
    /// 读取单个渠道的并发覆盖值（0 表示未配置）。对应 Go: <c>host.ChannelConcurrencyLimit</c>。
    /// </param>
    public ProviderRequestContext(
        IRuntimePolicyProvider policy,
        Coordinator? coordinator = null,
        Func<string, int>? channelConcurrencyLimit = null,
        ProtocolAdapterRegistry? declarativeAdapter = null)
    {
        _policy = policy;
        _coordinator = coordinator;
        _channelConcurrencyLimit = channelConcurrencyLimit;
        _declarativeAdapter = declarativeAdapter;
    }

    /// <summary>声明式插件注册表快照。对应 <c>IProviderRequestContext.DeclarativeAdapter</c>。</summary>
    ProtocolAdapterRegistry? IProviderRequestContext.DeclarativeAdapter => _declarativeAdapter;

    /// <summary>
    /// 上游响应字节上限，取自运行时策略的"生成文件大小"限制。
    /// 对应 Go: <c>provider_http_client.go</c> 的 <c>responseLimit = megabytes(policy.Resource.GeneratedFileMB)</c>。
    /// </summary>
    public long MaxResponseBytes => _policy.Current().Resource.GeneratedFileMB * (1L << 20);

    public async Task<bool> IsCircuitOpenAsync(string channelId, CancellationToken cancellationToken)
    {
        if (_coordinator is null)
        {
            return false;
        }
        (bool open, string? error) = await _coordinator.CircuitOpenAsync(channelId, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            // 与 Go 一致：读取熔断状态失败是硬错误，不能当成"未打开"而放行。
            throw new InvalidOperationException($"读取渠道熔断状态失败：{error}");
        }
        return open;
    }

    /// <summary>
    /// 获取渠道并发槽。渠道为空时以主机名作为范围（对应 Go 的 <c>custom:{host}</c> 语义由调用方传入）。
    /// </summary>
    public async Task<Func<ValueTask>?> AcquireChannelSlotAsync(
        string channelId, string fallbackScope, CancellationToken cancellationToken)
    {
        if (_coordinator is null)
        {
            return null;
        }

        RuntimeTaskPolicy task = _policy.Current().Task;
        int limit = task.ChannelConcurrency;

        string scope = channelId.Trim();
        if (scope.Length > 0)
        {
            int channelLimit = _channelConcurrencyLimit?.Invoke(scope) ?? 0;
            if (channelLimit > 0)
            {
                if (channelLimit is < Coordinator.MinChannelConcurrencyLimit
                    or > Coordinator.MaxChannelConcurrencyLimit)
                {
                    throw new InvalidOperationException("渠道并发配置超出 1-999 范围");
                }
                limit = channelLimit;
            }
        }
        else
        {
            scope = fallbackScope.Trim();
        }

        if (scope.Length == 0)
        {
            throw new InvalidOperationException("渠道并发范围为空");
        }

        try
        {
            return await _coordinator
                .AcquireWithWaitAsync("channel:" + scope, limit, SlotTTL, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // 与 Go 的 channelSlotError 一致：抛可识别的类型，便于轮询逻辑判定为可重试。
            throw ProviderChannelSlotException.Unavailable(
                $"获取渠道并发配额失败（渠道 {scope}，并发上限 {limit}）：{error.Message}", error);
        }
    }

    public async Task RecordChannelResultAsync(
        string channelId, bool failed, CancellationToken cancellationToken)
    {
        if (_coordinator is null)
        {
            return;
        }
        RuntimeRequestPolicy request = _policy.Current().Request;
        await _coordinator.RecordChannelResultAsync(
            channelId,
            failed,
            request.ChannelCircuitFailureCount,
            TimeSpan.FromSeconds(request.ChannelCircuitOpenSeconds),
            cancellationToken).ConfigureAwait(false);
    }
}
