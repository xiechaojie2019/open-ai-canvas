#nullable enable
using OpenAICanvas.Platform;
using Xunit;

namespace OpenAICanvas.Tests.Platform;

/// <summary>
/// 多实例协调器（单实例退化路径）的契约测试。
/// 对应 Go: <c>internal/platform/coordination.go</c> 的
/// <c>Coordinator.Allow</c> / <c>Acquire</c> / <c>AcquireWithWait</c> / <c>RateRetryAfter</c> /
/// <c>effectiveChannelConcurrencyLimit</c> / <c>channelSlotRetryDelay</c>。
/// </summary>
/// <remarks>
/// 只覆盖无 Redis 的本地实现 —— Redis 路径是 Lua 脚本，属集成测试范畴。
/// </remarks>
public sealed class CoordinatorTests
{
    private static Coordinator Local() => Coordinator.WithRedis(null);

    // ------------------------------------------------------------ Allow

    [Fact]
    public async Task 限流_窗口内放行到上限后拒绝()
    {
        Coordinator coordinator = Local();
        string key = "test-" + Guid.NewGuid().ToString("N");

        (bool first, _) = await coordinator.AllowAsync(key, 2, TimeSpan.FromMinutes(1));
        (bool second, _) = await coordinator.AllowAsync(key, 2, TimeSpan.FromMinutes(1));
        (bool third, _) = await coordinator.AllowAsync(key, 2, TimeSpan.FromMinutes(1));

        Assert.True(first);
        Assert.True(second);
        Assert.False(third);
    }

    [Fact]
    public async Task 限流_窗口过期后重置()
    {
        Coordinator coordinator = Local();
        string key = "test-" + Guid.NewGuid().ToString("N");

        await coordinator.AllowAsync(key, 1, TimeSpan.FromMilliseconds(50));
        (bool blocked, _) = await coordinator.AllowAsync(key, 1, TimeSpan.FromMilliseconds(50));
        Assert.False(blocked);

        await Task.Delay(80);
        (bool allowed, _) = await coordinator.AllowAsync(key, 1, TimeSpan.FromMilliseconds(50));
        Assert.True(allowed);
    }

    [Fact]
    public async Task 限流_不同键互不影响()
    {
        Coordinator coordinator = Local();
        string keyA = "a-" + Guid.NewGuid().ToString("N");
        string keyB = "b-" + Guid.NewGuid().ToString("N");

        await coordinator.AllowAsync(keyA, 1, TimeSpan.FromMinutes(1));
        (bool b, _) = await coordinator.AllowAsync(keyB, 1, TimeSpan.FromMinutes(1));

        Assert.True(b);
    }

    // ------------------------------------------------------------ AcquireLease

    [Fact]
    public async Task 并发槽_达到上限后拒绝()
    {
        Coordinator coordinator = Local();
        string scope = "scope-" + Guid.NewGuid().ToString("N");

        (SlotLease? firstLease, bool first, _) = await coordinator.AcquireLeaseAsync(scope, 1, TimeSpan.FromMinutes(1));
        (_, bool second, _) = await coordinator.AcquireLeaseAsync(scope, 1, TimeSpan.FromMinutes(1));

        Assert.True(first);
        Assert.False(second);

        await firstLease!.ReleaseAsync();
        (_, bool third, _) = await coordinator.AcquireLeaseAsync(scope, 1, TimeSpan.FromMinutes(1));
        Assert.True(third);
    }

    [Fact]
    public async Task 并发槽_租约过期后可重新获取()
    {
        Coordinator coordinator = Local();
        string scope = "scope-" + Guid.NewGuid().ToString("N");

        await coordinator.AcquireLeaseAsync(scope, 1, TimeSpan.FromMilliseconds(40));
        await Task.Delay(80);

        // 过期的槽位被惰性清理，不释放也能重新获取。
        (_, bool acquired, _) = await coordinator.AcquireLeaseAsync(scope, 1, TimeSpan.FromMinutes(1));
        Assert.True(acquired);
    }

    [Fact]
    public async Task 并发槽_释放幂等()
    {
        Coordinator coordinator = Local();
        string scope = "scope-" + Guid.NewGuid().ToString("N");
        (SlotLease? lease, bool acquired, _) =
            await coordinator.AcquireLeaseAsync(scope, 1, TimeSpan.FromMinutes(1));
        Assert.True(acquired);

        await lease!.ReleaseAsync();
        await lease.ReleaseAsync();

        (_, bool reAcquired, _) = await coordinator.AcquireLeaseAsync(scope, 1, TimeSpan.FromMinutes(1));
        Assert.True(reAcquired);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task 并发槽_非法上限报错(int limit)
    {
        Coordinator coordinator = Local();

        (_, bool acquired, string? error) =
            await coordinator.AcquireLeaseAsync("s", limit, TimeSpan.FromMinutes(1));

        Assert.False(acquired);
        Assert.Equal("并发租约参数无效", error);
    }

    [Fact]
    public async Task 并发槽_非法TTL报错()
    {
        Coordinator coordinator = Local();

        (_, bool acquired, string? error) =
            await coordinator.AcquireLeaseAsync("s", 1, TimeSpan.Zero);

        Assert.False(acquired);
        Assert.Equal("并发租约参数无效", error);
    }

    [Fact]
    public async Task 并发槽_已取消令牌直接失败()
    {
        Coordinator coordinator = Local();
        using CancellationTokenSource source = new();
        source.Cancel();

        (_, bool acquired, _) = await coordinator.AcquireLeaseAsync(
            "s", 1, TimeSpan.FromMinutes(1), source.Token);

        Assert.False(acquired);
    }

    // ------------------------------------------------------------ AcquireWithWait

    [Fact]
    public async Task 等待获取_空闲时立即成功()
    {
        Coordinator coordinator = Local();
        string scope = "scope-" + Guid.NewGuid().ToString("N");

        Func<ValueTask> release = await coordinator.AcquireWithWaitAsync(scope, 1, TimeSpan.FromMinutes(1));

        Assert.NotNull(release);
        await release();
    }

    [Fact]
    public async Task 等待获取_取消时抛取消异常()
    {
        Coordinator coordinator = Local();
        string scope = "scope-" + Guid.NewGuid().ToString("N");
        await coordinator.AcquireLeaseAsync(scope, 1, TimeSpan.FromMinutes(1));

        using CancellationTokenSource source = new();
        source.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.AcquireWithWaitAsync(scope, 1, TimeSpan.FromMinutes(1), source.Token));
    }

    [Fact]
    public async Task 等待获取_有槽释放后成功()
    {
        Coordinator coordinator = Local();
        string scope = "scope-" + Guid.NewGuid().ToString("N");
        (SlotLease? held, bool acquired, _) =
            await coordinator.AcquireLeaseAsync(scope, 1, TimeSpan.FromMinutes(1));
        Assert.True(acquired);

        Task<Func<ValueTask>> waiting =
            coordinator.AcquireWithWaitAsync(scope, 1, TimeSpan.FromMinutes(1));
        await Task.Delay(80);
        await held!.ReleaseAsync();

        Func<ValueTask> release = await waiting;
        await release();
    }

    [Fact]
    public void 重试退避_落在半窗随机区间()
    {
        TimeSpan delay = TimeSpan.FromMilliseconds(400);

        for (int i = 0; i < 50; i++)
        {
            TimeSpan actual = Coordinator.ChannelSlotRetryDelay(delay);
            Assert.InRange(actual.TotalMilliseconds, 200, 400);
        }
    }

    // ------------------------------------------------------------ 并发上限归一

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(999, 999)]
    [InlineData(0, 3)]      // 小于下限 → 回落默认值
    [InlineData(-5, 3)]
    [InlineData(1000, 3)]   // 大于上限 → 回落默认值（不是夹取到 999）
    public void 并发上限_越界回落默认值而非夹取(int configured, int expected) =>
        Assert.Equal(expected, Coordinator.EffectiveChannelConcurrencyLimit(configured));

    [Fact]
    public void 并发上限_常量与Go一致()
    {
        // Go: maxRuntimeConcurrency = 999，区间 [1, 999]，默认 3。
        Assert.Equal(1, Coordinator.MinChannelConcurrencyLimit);
        Assert.Equal(999, Coordinator.MaxChannelConcurrencyLimit);
        Assert.Equal(3, Coordinator.DefaultChannelConcurrencyValue);
    }

    // ------------------------------------------------------------ 熔断（无 Redis 时直通）

    [Fact]
    public async Task 熔断_无Redis时始终未打开()
    {
        Coordinator coordinator = Local();

        (bool open, string? error) = await coordinator.CircuitOpenAsync("chan-1");

        Assert.False(open);
        Assert.Null(error);
    }

    [Fact]
    public async Task 熔断_渠道为空时未打开()
    {
        Coordinator coordinator = Local();

        (bool open, _) = await coordinator.CircuitOpenAsync("  ");

        Assert.False(open);
    }

    [Fact]
    public async Task 熔断_无Redis时记录结果为无操作()
    {
        Coordinator coordinator = Local();

        // 不应抛异常。
        await coordinator.RecordChannelResultAsync("chan-1", failed: true, failureLimit: 1, TimeSpan.FromSeconds(1));
        await coordinator.RecordChannelResultAsync("chan-1", failed: false, failureLimit: 1, TimeSpan.FromSeconds(1));
    }

    // ------------------------------------------------------------ 频控剩余等待

    [Fact]
    public async Task 剩余等待_已限流时返回不小于一秒()
    {
        Coordinator coordinator = Local();
        string key = "test-" + Guid.NewGuid().ToString("N");
        await coordinator.AllowAsync(key, 1, TimeSpan.FromMinutes(5));

        TimeSpan remaining = await coordinator.RateRetryAfterAsync(key, TimeSpan.FromMinutes(5));

        Assert.True(remaining >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task 剩余等待_未知键返回一秒()
    {
        Coordinator coordinator = Local();

        TimeSpan remaining = await coordinator.RateRetryAfterAsync(
            "unknown-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromSeconds(1), remaining);
    }

    // ------------------------------------------------------------ 本地计数维护

    [Fact]
    public async Task 本地计数_清空后归零()
    {
        Coordinator coordinator = Local();
        await coordinator.AllowAsync("k-" + Guid.NewGuid().ToString("N"), 1, TimeSpan.FromMinutes(1));

        Assert.Equal(1, coordinator.LocalRateCount());
        Assert.Equal(1, coordinator.ClearLocalRate());
        Assert.Equal(0, coordinator.LocalRateCount());
    }

    // ------------------------------------------------------------ 路由目录版本

    [Fact]
    public async Task 路由版本_无Redis时为零且自增无副作用()
    {
        Coordinator coordinator = Local();

        Assert.Equal(0, await coordinator.RouteCatalogVersionAsync());
        await coordinator.BumpRouteCatalogVersionAsync();
        Assert.Equal(0, await coordinator.RouteCatalogVersionAsync());
    }

    [Fact]
    public async Task 路由健康_无Redis时未屏蔽()
    {
        Coordinator coordinator = Local();

        Assert.Equal(default, await coordinator.RouteBlockedUntilAsync("route-1"));
        await coordinator.BlockRouteAsync("route-1", DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(default, await coordinator.RouteBlockedUntilAsync("route-1"));
    }

    // ------------------------------------------------------------ Create

    [Fact]
    public void 构建_postgres且无RedisUrl时报错()
    {
        string? previous = Environment.GetEnvironmentVariable("REDIS_URL");
        try
        {
            Environment.SetEnvironmentVariable("REDIS_URL", "");
            (Coordinator coordinator, string? error) = Coordinator.Create("postgres");

            Assert.NotNull(error);
            Assert.Contains("必须配置 REDIS_URL", error, StringComparison.Ordinal);
            Assert.False(coordinator.HasRedis());
        }
        finally
        {
            Environment.SetEnvironmentVariable("REDIS_URL", previous);
        }
    }

    [Fact]
    public void 构建_sqlite且无RedisUrl时不报错()
    {
        string? previous = Environment.GetEnvironmentVariable("REDIS_URL");
        try
        {
            Environment.SetEnvironmentVariable("REDIS_URL", "");
            (Coordinator coordinator, string? error) = Coordinator.Create("sqlite");

            Assert.Null(error);
            Assert.False(coordinator.HasRedis());
        }
        finally
        {
            Environment.SetEnvironmentVariable("REDIS_URL", previous);
        }
    }
}
