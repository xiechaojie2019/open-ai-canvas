using System.Collections.Concurrent;
using StackExchange.Redis;

namespace OpenAICanvas.Platform;

/// <summary>
/// 多实例协调器：频控、并发槽与渠道熔断。
/// 对应 Go: <c>internal/platform/coordination.go</c> 的 <c>Coordinator</c>。
/// </summary>
/// <remarks>
/// 未配置 Redis 时退化到<b>进程内</b>实现（单实例部署），语义与 Go 一致。
/// 协调写操作<b>不透明重试</b>：限流/并发失败必须及时返回，不能放大故障流量。
/// </remarks>
public sealed class Coordinator
{
    /// <summary>并发槽上限的允许区间。对应 Go: <c>MinChannelConcurrencyLimit</c> / <c>MaxChannelConcurrencyLimit</c>。</summary>
    public const int MinChannelConcurrencyLimit = 1;

    public const int MaxChannelConcurrencyLimit = 999;

    /// <summary>渠道并发默认值。对应 Go: <c>defaultChannelConcurrencyValue</c>。</summary>
    public const int DefaultChannelConcurrencyValue = 3;

    /// <summary>协调操作超时。对应 Go: <c>CoordinationTimeout</c>。</summary>
    public static readonly TimeSpan CoordinationTimeout = TimeSpan.FromSeconds(2);

    private const string FixedWindowScript = """
        local existing = tonumber(redis.call('GET', KEYS[1]) or '0')
        if existing >= tonumber(ARGV[2]) then return existing + 1 end
        local count = redis.call('INCR', KEYS[1])
        if count == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end
        return count
        """;

    private const string AcquireSlotScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[3]) then return 0 end
        redis.call('ZADD', KEYS[1], ARGV[2], ARGV[4])
        local latest = redis.call('ZREVRANGE', KEYS[1], 0, 0, 'WITHSCORES')
        redis.call('PEXPIRE', KEYS[1], math.max(tonumber(ARGV[5]), tonumber(latest[2]) - tonumber(ARGV[1]) + 60000))
        return 1
        """;

    private const string RenewSlotScript = """
        local expires = redis.call('ZSCORE', KEYS[1], ARGV[1])
        if not expires or tonumber(expires) <= tonumber(ARGV[2]) then return 0 end
        redis.call('ZADD', KEYS[1], 'XX', ARGV[3], ARGV[1])
        local latest = redis.call('ZREVRANGE', KEYS[1], 0, 0, 'WITHSCORES')
        redis.call('PEXPIRE', KEYS[1], math.max(tonumber(ARGV[4]), tonumber(latest[2]) - tonumber(ARGV[2]) + 60000))
        return 1
        """;

    /// <summary>供 <see cref="SlotLease"/> 使用的续期脚本。</summary>
    internal static string RenewScript => RenewSlotScript;

    private sealed class LocalRateEntry
    {
        public DateTimeOffset Started { get; set; }

        public int Count { get; set; }
    }

    private readonly object _localLock = new();
    private readonly Dictionary<string, LocalRateEntry> _localRate = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _localSlots =
        new(StringComparer.Ordinal);

    private Coordinator(IConnectionMultiplexer? redis, string instanceId)
    {
        RedisConnection = redis;
        InstanceId = string.IsNullOrWhiteSpace(instanceId) ? Guid.NewGuid().ToString("N") : instanceId;
    }

    /// <summary>
    /// 按环境变量 <c>REDIS_URL</c> 构建。返回的协调器可能不带 Redis（单实例模式）。
    /// 对应 Go: <c>NewCoordinator</c>（错误对应 Go 的返回 <c>err</c>）。
    /// </summary>
    /// <remarks>
    /// PostgreSQL 多实例模式<b>必须</b>配置 REDIS_URL，否则直接报错 ——
    /// 没有 Redis 就没有跨实例的频控与并发协调，静默退化会导致超卖。
    /// </remarks>
    public static (Coordinator Coordinator, string? Error) Create(string dialect)
    {
        Coordinator coordinator = new(null, Guid.NewGuid().ToString("N"));
        string redisUrl = (Environment.GetEnvironmentVariable("REDIS_URL") ?? string.Empty).Trim();
        if (redisUrl.Length == 0)
        {
            if (dialect == "postgres")
            {
                return (coordinator, "PostgreSQL 多实例模式必须配置 REDIS_URL，用于限流、并发和熔断协调");
            }
            return (coordinator, null);
        }

        try
        {
            ConfigurationOptions options = ConfigurationOptions.Parse(redisUrl);
            // 协调写操作不透明重试；限流/并发失败必须及时向调用方返回，不能放大故障流量。
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = (int)CoordinationTimeout.TotalMilliseconds;
            options.SyncTimeout = (int)CoordinationTimeout.TotalMilliseconds;
            IConnectionMultiplexer connection = ConnectionMultiplexer.Connect(options);
            return (new Coordinator(connection, Guid.NewGuid().ToString("N")), null);
        }
        catch (Exception error)
        {
            return (new Coordinator(null, Guid.NewGuid().ToString("N")), $"Redis 不可用：{error.Message}");
        }
    }

    /// <summary>供测试与嵌入式部署使用（已连接的 Redis 客户端）。</summary>
    public static Coordinator WithRedis(IConnectionMultiplexer? client, string instanceId = "") =>
        new(client, instanceId);

    public IConnectionMultiplexer? RedisConnection { get; }

    public string InstanceId { get; }

    public bool HasRedis() => RedisConnection is not null;

    public IDatabase? Redis() => RedisConnection?.GetDatabase();

    /// <summary>
    /// 固定窗口限流。<c>true</c> 表示放行。
    /// 对应 Go: <c>Coordinator.Allow</c>。
    /// </summary>
    public async Task<(bool Allowed, string? Error)> AllowAsync(
        string key, int limit, TimeSpan window, CancellationToken cancellationToken = default)
    {
        if (RedisConnection is not null)
        {
            try
            {
                RedisResult result = await RedisConnection.GetDatabase().ScriptEvaluateAsync(
                    FixedWindowScript,
                    ["canvas:rate:" + key],
                    [(long)window.TotalMilliseconds, limit]).ConfigureAwait(false);
                long count = (long)result;
                return (count <= limit, null);
            }
            catch (Exception error)
            {
                return (false, error.Message);
            }
        }

        lock (_localLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!_localRate.TryGetValue(key, out LocalRateEntry? entry)
                || now - entry.Started >= window)
            {
                _localRate[key] = new LocalRateEntry { Started = now, Count = 1 };
                return (true, null);
            }
            if (entry.Count >= limit)
            {
                return (false, null);
            }
            entry.Count++;
            return (true, null);
        }
    }

    /// <summary>
    /// 尝试获取一个并发租约；满载时返回 <c>Acquired = false</c>（不等待）。
    /// 对应 Go: <c>Coordinator.Acquire</c> / <c>AcquireLease</c>。
    /// </summary>
    public async Task<(SlotLease? Lease, bool Acquired, string? Error)> AcquireLeaseAsync(
        string scope, int limit, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return (null, false, null);
        }
        if (limit <= 0 || ttl <= TimeSpan.Zero)
        {
            return (null, false, "并发租约参数无效");
        }

        SlotLease lease = new(this, scope, $"{InstanceId}:{Guid.NewGuid():N}", ttl);

        if (RedisConnection is null)
        {
            lock (_localLock)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (!_localSlots.TryGetValue(scope, out Dictionary<string, DateTimeOffset>? slots))
                {
                    slots = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
                    _localSlots[scope] = slots;
                }
                foreach (string token in slots.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList())
                {
                    slots.Remove(token);
                }
                if (slots.Count >= limit)
                {
                    return (null, false, null);
                }
                slots[lease.Token] = now + ttl;
                return (lease, true, null);
            }
        }

        // 有过期分数的有序集合避免实例崩溃后永久占槽，业务数据库仍保存任务与账本真相。
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CoordinationTimeout);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            RedisResult result = await RedisConnection.GetDatabase().ScriptEvaluateAsync(
                AcquireSlotScript,
                ["canvas:slots:" + scope],
                [
                    now.ToUnixTimeMilliseconds(),
                    now.Add(ttl).ToUnixTimeMilliseconds(),
                    limit,
                    lease.Token,
                    (long)(ttl + TimeSpan.FromMinutes(1)).TotalMilliseconds,
                ]).ConfigureAwait(false);
            if ((long)result != 1)
            {
                return (null, false, null);
            }
            return (lease, true, null);
        }
        catch (Exception error)
        {
            return (null, false, error.Message);
        }
    }

    /// <summary>
    /// 获取并发槽，满载时按指数退避重试直到超时/取消。
    /// 对应 Go: <c>Coordinator.AcquireWithWait</c>。
    /// </summary>
    /// <returns>释放委托。调用方必须在其 <c>finally</c> 中调用。</returns>
    public async Task<Func<ValueTask>> AcquireWithWaitAsync(
        string scope, int limit, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        TimeSpan delay = TimeSpan.FromMilliseconds(200);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (SlotLease? lease, bool acquired, string? error) =
                await AcquireLeaseAsync(scope, limit, ttl, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }
            if (acquired)
            {
                SlotLease granted = lease!;
                return () => new ValueTask(granted.ReleaseAsync());
            }
            // 满载期间退避并错峰，避免每个等待者固定每秒五次同步争抢 Redis。
            try
            {
                await Task.Delay(ChannelSlotRetryDelay(delay), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2000));
        }
    }

    /// <summary>
    /// 对应 Go: <c>channelSlotRetryDelay</c> —— 在下半窗口随机等待，保留首轮至少 100ms 的退让。
    /// </summary>
    public static TimeSpan ChannelSlotRetryDelay(TimeSpan delay)
    {
        long half = (long)(delay.TotalMilliseconds / 2);
        return TimeSpan.FromMilliseconds(half + Random.Shared.NextInt64(half + 1));
    }

    /// <summary>
    /// 渠道熔断是否处于打开状态。对应 Go: <c>Coordinator.CircuitOpen</c>。
    /// </summary>
    public async Task<(bool Open, string? Error)> CircuitOpenAsync(
        string channelId, CancellationToken cancellationToken = default)
    {
        if (RedisConnection is null || string.IsNullOrWhiteSpace(channelId))
        {
            return (false, null);
        }
        try
        {
            bool exists = await RedisConnection.GetDatabase()
                .KeyExistsAsync("canvas:circuit:open:" + channelId).ConfigureAwait(false);
            return (exists, null);
        }
        catch (Exception error)
        {
            return (false, error.Message);
        }
    }

    /// <summary>
    /// 记录一次渠道调用结果，驱动熔断状态机。
    /// 对应 Go: <c>Coordinator.RecordChannelResult</c>。
    /// </summary>
    /// <remarks>
    /// 成功时<b>同时清除失败计数与打开标记</b>；失败累计到阈值则打开熔断。
    /// 未配置 Redis 时不做任何事 —— 单实例部署无跨实例熔断需求（与 Go 一致）。
    /// </remarks>
    public async Task RecordChannelResultAsync(
        string channelId, bool failed, int failureLimit, TimeSpan openDuration,
        CancellationToken cancellationToken = default)
    {
        if (RedisConnection is null || string.IsNullOrWhiteSpace(channelId))
        {
            return;
        }
        try
        {
            IDatabase database = RedisConnection.GetDatabase();
            string failureKey = "canvas:circuit:failures:" + channelId;
            string openKey = "canvas:circuit:open:" + channelId;
            if (!failed)
            {
                await database.KeyDeleteAsync([failureKey, openKey]).ConfigureAwait(false);
                return;
            }
            long count = await database.StringIncrementAsync(failureKey).ConfigureAwait(false);
            await database.KeyExpireAsync(failureKey, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            if (count >= failureLimit)
            {
                await database.StringSetAsync(openKey, "1", openDuration).ConfigureAwait(false);
            }
        }
        catch
        {
            // 与 Go 一致：熔断记账失败不影响主流程。
        }
    }

    // ------------------------------------------------------------ 路由目录 / 健康

    /// <summary>对应 Go: <c>RouteCatalogVersionKey</c>。</summary>
    public const string RouteCatalogVersionKey = "canvas:logical-model-route-catalog:version";

    public async Task<long> RouteCatalogVersionAsync(CancellationToken cancellationToken = default)
    {
        if (RedisConnection is null)
        {
            return 0;
        }
        RedisValue value = await RedisConnection.GetDatabase().StringGetAsync(RouteCatalogVersionKey)
            .ConfigureAwait(false);
        return value.IsNullOrEmpty ? 0 : (long)value;
    }

    public async Task BumpRouteCatalogVersionAsync(CancellationToken cancellationToken = default)
    {
        if (RedisConnection is null)
        {
            return;
        }
        await RedisConnection.GetDatabase().StringIncrementAsync(RouteCatalogVersionKey).ConfigureAwait(false);
    }

    /// <summary>对应 Go: <c>routeHealthKey</c>。</summary>
    public static string RouteHealthKey(string key) => "canvas:logical-route-health:" + key;

    public async Task<DateTimeOffset> RouteBlockedUntilAsync(
        string key, CancellationToken cancellationToken = default)
    {
        if (RedisConnection is null)
        {
            return default;
        }
        RedisValue value = await RedisConnection.GetDatabase().StringGetAsync(RouteHealthKey(key))
            .ConfigureAwait(false);
        return value.IsNullOrEmpty ? default : DateTimeOffset.FromUnixTimeMilliseconds((long)value);
    }

    public async Task BlockRouteAsync(
        string key, DateTimeOffset until, CancellationToken cancellationToken = default)
    {
        if (RedisConnection is null)
        {
            return;
        }
        TimeSpan ttl = until - DateTimeOffset.UtcNow;
        if (ttl <= TimeSpan.Zero)
        {
            await RedisConnection.GetDatabase().KeyDeleteAsync(RouteHealthKey(key)).ConfigureAwait(false);
            return;
        }
        await RedisConnection.GetDatabase()
            .StringSetAsync(RouteHealthKey(key), until.ToUnixTimeMilliseconds(), ttl).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 频控剩余等待

    /// <summary>
    /// 频控窗口剩余等待时长。对应 Go: <c>Coordinator.RateRetryAfter</c>。
    /// </summary>
    public async Task<TimeSpan> RateRetryAfterAsync(
        string key, TimeSpan window, CancellationToken cancellationToken = default)
    {
        if (RedisConnection is not null)
        {
            try
            {
                TimeSpan? ttl = await RedisConnection.GetDatabase()
                    .KeyTimeToLiveAsync("canvas:rate:" + key).ConfigureAwait(false);
                if (ttl is not null && ttl > TimeSpan.Zero)
                {
                    return ttl.Value;
                }
            }
            catch
            {
                // 与 Go 一致：读取失败时回落到窗口长度。
            }
            return window;
        }

        lock (_localLock)
        {
            if (_localRate.TryGetValue(key, out LocalRateEntry? entry))
            {
                TimeSpan remaining = entry.Started.Add(window) - DateTimeOffset.UtcNow;
                return remaining > TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            }
            return TimeSpan.FromSeconds(1);
        }
    }

    /// <summary>
    /// 移除进程内的并发槽。对应 Go: <c>SlotLease.Release</c> 的 <c>c.redis == nil</c> 分支。
    /// </summary>
    internal void ReleaseLocalSlot(string scope, string token)
    {
        lock (_localLock)
        {
            if (!_localSlots.TryGetValue(scope, out Dictionary<string, DateTimeOffset>? slots))
            {
                return;
            }
            slots.Remove(token);
            if (slots.Count == 0)
            {
                // 与 Go 一致：作用域空置后删除条目，避免长期运行下字典无限增长。
                _localSlots.Remove(scope);
            }
        }
    }

    /// <summary>
    /// 续期进程内并发槽。对应 Go: <c>SlotLease.Renew</c> 的 <c>c.redis == nil</c> 分支。
    /// </summary>
    /// <remarks>槽位不存在或已过期时返回"已失效"，与 Go 一致。</remarks>
    internal (bool Renewed, string? Error) RenewLocalSlot(
        string scope, string token, TimeSpan ttl, DateTimeOffset now)
    {
        lock (_localLock)
        {
            if (!_localSlots.TryGetValue(scope, out Dictionary<string, DateTimeOffset>? slots)
                || !slots.TryGetValue(token, out DateTimeOffset expiresAt)
                || expiresAt <= now)
            {
                return (false, "并发租约已失效");
            }
            slots[token] = now + ttl;
            return (true, null);
        }
    }

    /// <summary>对应 Go: <c>Coordinator.LocalRateCount</c>（测试用）。</summary>
    public int LocalRateCount()
    {
        lock (_localLock)
        {
            return _localRate.Count;
        }
    }

    /// <summary>对应 Go: <c>Coordinator.ClearLocalRate</c>（测试用）。</summary>
    public int ClearLocalRate()
    {
        lock (_localLock)
        {
            int count = _localRate.Count;
            _localRate.Clear();
            return count;
        }
    }

    // ------------------------------------------------------------ 渠道并发配置

    /// <summary>
    /// 把配置值夹到合法区间；越界时回落默认值（不是夹取）。
    /// 对应 Go: <c>effectiveChannelConcurrencyLimit</c>。
    /// </summary>
    public static int EffectiveChannelConcurrencyLimit(int configured) =>
        configured < MinChannelConcurrencyLimit || configured > MaxChannelConcurrencyLimit
            ? DefaultChannelConcurrencyValue
            : configured;

    /// <summary>
    /// 环境变量 <c>CANVAS_CHANNEL_CONCURRENCY</c> 决定的默认渠道并发。
    /// 对应 Go: <c>defaultChannelConcurrencyLimit</c>。
    /// </summary>
    public static int DefaultChannelConcurrencyLimit() =>
        EffectiveChannelConcurrencyLimit(EnvInt("CANVAS_CHANNEL_CONCURRENCY", DefaultChannelConcurrencyValue));

    /// <summary>对应 Go: <c>envInt</c>（非正整数回落默认值）。</summary>
    public static int EnvInt(string key, int fallback)
    {
        string value = (Environment.GetEnvironmentVariable(key) ?? string.Empty).Trim();
        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
    }
}

/// <summary>
/// 并发租约。<see cref="ReleaseAsync"/> 幂等（只能释放一次）。
/// 对应 Go: <c>platform.SlotLease</c>。
/// </summary>
public sealed class SlotLease
{
    private readonly Coordinator _coordinator;
    private readonly string _scope;
    private readonly TimeSpan _ttl;
    private readonly object _onceLock = new();
    private bool _released;

    internal SlotLease(Coordinator coordinator, string scope, string token, TimeSpan ttl)
    {
        _coordinator = coordinator;
        _scope = scope;
        _ttl = ttl;
        Token = token;
    }

    public string Token { get; }

    /// <summary>续期。对应 Go: <c>SlotLease.Renew</c>。</summary>
    public async Task<(bool Renewed, string? Error)> RenewAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return (false, null);
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (_coordinator.RedisConnection is null)
        {
            return _coordinator.RenewLocalSlot(_scope, Token, _ttl, now);
        }

        try
        {
            RedisResult result = await _coordinator.RedisConnection.GetDatabase().ScriptEvaluateAsync(
                Coordinator.RenewScript,
                ["canvas:slots:" + _scope],
                [
                    Token,
                    now.ToUnixTimeMilliseconds(),
                    now.Add(_ttl).ToUnixTimeMilliseconds(),
                    (long)(_ttl + TimeSpan.FromMinutes(1)).TotalMilliseconds,
                ]).ConfigureAwait(false);
            return (long)result == 1 ? (true, null) : (false, "并发租约已失效");
        }
        catch (Exception error)
        {
            return (false, error.Message);
        }
    }

    /// <summary>释放租约（幂等）。对应 Go: <c>SlotLease.Release</c>。</summary>
    public async Task ReleaseAsync()
    {
        lock (_onceLock)
        {
            if (_released)
            {
                return;
            }
            _released = true;
        }

        if (_coordinator.RedisConnection is null)
        {
            _coordinator.ReleaseLocalSlot(_scope, Token);
            return;
        }
        try
        {
            await _coordinator.RedisConnection.GetDatabase()
                .SortedSetRemoveAsync("canvas:slots:" + _scope, Token).ConfigureAwait(false);
        }
        catch
        {
            // 与 Go 一致：释放失败只记日志，租约会靠 TTL 自然过期。
        }
    }
}
