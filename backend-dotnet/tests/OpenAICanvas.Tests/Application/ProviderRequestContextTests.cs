#nullable enable
using OpenAICanvas.Application;
using OpenAICanvas.Platform;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// Provider 运行时上下文适配器的契约测试。
/// 对应 Go: <c>provider_http_client.go</c> 中的 <c>AcquireChannelSlot</c> / <c>Coordinator.CircuitOpen</c> /
/// <c>Service.RecordChannelResult</c> 组合。
/// </summary>
public sealed class ProviderRequestContextTests
{
    private sealed class StubPolicyProvider : IRuntimePolicyProvider
    {
        private readonly RuntimePolicySetting _policy;

        public StubPolicyProvider(RuntimePolicySetting policy) => _policy = policy;

        public RuntimePolicySetting Current() => _policy;

        public PublicRuntimeLimits PublicLimits() => new();
    }

    private static ProviderRequestContext Context(
        int generatedFileMB = 64,
        int channelConcurrency = 3,
        Coordinator? coordinator = null,
        Func<string, int>? channelLimit = null)
    {
        RuntimePolicySetting baseline = RuntimePolicySetting.Default;
        RuntimePolicySetting policy = new()
        {
            Resource = new RuntimeResourcePolicy
            {
                ResourceUploadMB = baseline.Resource.ResourceUploadMB,
                GeneratedFileMB = generatedFileMB,
                DailyUploadMB = baseline.Resource.DailyUploadMB,
                StoredFileGB = baseline.Resource.StoredFileGB,
                StructuredDataMB = baseline.Resource.StructuredDataMB,
                TaskDataGB = baseline.Resource.TaskDataGB,
                AssetCount = baseline.Resource.AssetCount,
                CanvasCount = baseline.Resource.CanvasCount,
                TaskCount = baseline.Resource.TaskCount,
                ApiCallLogCount = baseline.Resource.ApiCallLogCount,
                RecycleBinRetentionDays = baseline.Resource.RecycleBinRetentionDays,
            },
            Task = new RuntimeTaskPolicy
            {
                WorkerConcurrency = baseline.Task.WorkerConcurrency,
                ChannelConcurrency = channelConcurrency,
                ActiveTaskLimit = baseline.Task.ActiveTaskLimit,
                ImageTimeoutMinutes = baseline.Task.ImageTimeoutMinutes,
                TextTimeoutMinutes = baseline.Task.TextTimeoutMinutes,
                AudioTimeoutMinutes = baseline.Task.AudioTimeoutMinutes,
                VideoTimeoutMinutes = baseline.Task.VideoTimeoutMinutes,
                StoryboardTimeoutMinutes = baseline.Task.StoryboardTimeoutMinutes,
                DefaultTimeoutMinutes = baseline.Task.DefaultTimeoutMinutes,
            },
            Request = baseline.Request,
        };
        return new ProviderRequestContext(new StubPolicyProvider(policy), coordinator, channelLimit);
    }

    // ------------------------------------------------------------ 响应上限

    [Fact]
    public void 响应上限_由生成文件MB换算为字节() =>
        Assert.Equal(64L << 20, Context(generatedFileMB: 64).MaxResponseBytes);

    [Fact]
    public void 响应上限_跟随策略值()
    {
        Assert.Equal(1L << 20, Context(generatedFileMB: 1).MaxResponseBytes);
        Assert.Equal(999L << 20, Context(generatedFileMB: 999).MaxResponseBytes);
    }

    // ------------------------------------------------------------ 熔断

    [Fact]
    public async Task 熔断_无协调器时未打开() =>
        Assert.False(await Context().IsCircuitOpenAsync("chan-1", CancellationToken.None));

    [Fact]
    public async Task 熔断_无Redis时未打开()
    {
        ProviderRequestContext context = Context(coordinator: Coordinator.WithRedis(null));

        Assert.False(await context.IsCircuitOpenAsync("chan-1", CancellationToken.None));
    }

    [Fact]
    public async Task 熔断_无协调器时记账为无操作()
    {
        // 不应抛异常。
        await Context().RecordChannelResultAsync("chan-1", failed: true, CancellationToken.None);
    }

    // ------------------------------------------------------------ 并发槽

    [Fact]
    public async Task 并发槽_无协调器时返回null()
    {
        Assert.Null(await Context().AcquireChannelSlotAsync("chan-1", "", CancellationToken.None));
    }

    [Fact]
    public async Task 并发槽_有协调器时返回可释放委托()
    {
        ProviderRequestContext context = Context(coordinator: Coordinator.WithRedis(null));

        Func<ValueTask>? release = await context.AcquireChannelSlotAsync("chan-1", "", CancellationToken.None);

        Assert.NotNull(release);
        await release();
    }

    [Fact]
    public async Task 并发槽_满载时等待直到取消()
    {
        Coordinator coordinator = Coordinator.WithRedis(null);
        // 先用独立上下文占满 chan-busy 的槽（上限 1）。
        ProviderRequestContext holder = Context(channelConcurrency: 1, coordinator: coordinator);
        Func<ValueTask>? held = await holder.AcquireChannelSlotAsync("chan-busy", "", CancellationToken.None);
        Assert.NotNull(held);

        ProviderRequestContext waiter = Context(channelConcurrency: 1, coordinator: coordinator);
        using CancellationTokenSource source = new();
        source.CancelAfter(150);

        // 与 Go 的 channelSlotError 一致：占槽失败归类为可识别的渠道并发异常。
        ProviderChannelSlotException error = await Assert.ThrowsAsync<ProviderChannelSlotException>(
            () => waiter.AcquireChannelSlotAsync("chan-busy", "", source.Token));
        Assert.False(string.IsNullOrEmpty(error.Code));

        await held();
    }

    [Fact]
    public async Task 并发槽_渠道为空时用回落范围()
    {
        Coordinator coordinator = Coordinator.WithRedis(null);
        ProviderRequestContext context = Context(channelConcurrency: 1, coordinator: coordinator);

        Func<ValueTask>? first = await context.AcquireChannelSlotAsync("", "fallback", CancellationToken.None);
        Assert.NotNull(first);

        // 同一回落范围应被同一把槽约束。
        using CancellationTokenSource source = new();
        source.CancelAfter(150);
        await Assert.ThrowsAsync<ProviderChannelSlotException>(
            () => context.AcquireChannelSlotAsync("", "fallback", source.Token));

        await first();
    }

    [Fact]
    public async Task 并发槽_范围为空时报错()
    {
        ProviderRequestContext context = Context(coordinator: Coordinator.WithRedis(null));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.AcquireChannelSlotAsync("", "  ", CancellationToken.None));

        Assert.Equal("渠道并发范围为空", error.Message);
    }

    [Fact]
    public async Task 并发槽_渠道覆盖值生效()
    {
        Coordinator coordinator = Coordinator.WithRedis(null);
        // 全局 5，但该渠道覆盖为 1。
        ProviderRequestContext context = Context(
            channelConcurrency: 5,
            coordinator: coordinator,
            channelLimit: _ => 1);

        Func<ValueTask>? first = await context.AcquireChannelSlotAsync("chan-1", "", CancellationToken.None);
        Assert.NotNull(first);

        using CancellationTokenSource source = new();
        source.CancelAfter(150);
        await Assert.ThrowsAsync<ProviderChannelSlotException>(
            () => context.AcquireChannelSlotAsync("chan-1", "", source.Token));

        await first();
    }

    [Fact]
    public async Task 并发槽_渠道覆盖值越界时报错()
    {
        ProviderRequestContext context = Context(coordinator: Coordinator.WithRedis(null), channelLimit: _ => 1000);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.AcquireChannelSlotAsync("chan-1", "", CancellationToken.None));

        Assert.Equal("渠道并发配置超出 1-999 范围", error.Message);
    }

    [Fact]
    public async Task 并发槽_渠道覆盖值为零时用全局值()
    {
        Coordinator coordinator = Coordinator.WithRedis(null);
        ProviderRequestContext context = Context(
            channelConcurrency: 2,
            coordinator: coordinator,
            channelLimit: _ => 0);

        Func<ValueTask>? first = await context.AcquireChannelSlotAsync("chan-1", "", CancellationToken.None);
        Func<ValueTask>? second = await context.AcquireChannelSlotAsync("chan-1", "", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);

        await first();
        await second();
    }

    // ------------------------------------------------------------ 实现接口

    [Fact]
    public void 适配器_实现Provider上下文接口() =>
        Assert.IsAssignableFrom<IProviderRequestContext>(Context());
}
