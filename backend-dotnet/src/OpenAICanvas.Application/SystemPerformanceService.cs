#nullable enable
using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>主机维度。对应 Go: <c>app.SystemPerformanceHost</c>。</summary>
public sealed class SystemPerformanceHostDto
{
    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = "";

    [JsonPropertyName("os")]
    public string OS { get; set; } = "";

    [JsonPropertyName("arch")]
    public string Arch { get; set; } = "";

    [JsonPropertyName("cpuCores")]
    public int CPUCores { get; set; }

    [JsonPropertyName("gomaxprocs")]
    public int Gomaxprocs { get; set; }

    [JsonPropertyName("processId")]
    public int ProcessID { get; set; }

    [JsonPropertyName("uptimeSeconds")]
    public long UptimeSeconds { get; set; }

    [JsonPropertyName("goroutines")]
    public int Goroutines { get; set; }

    [JsonPropertyName("activeWorkerTasks")]
    public long ActiveWorkerTasks { get; set; }

    [JsonPropertyName("loadAverage")]
    public double[] LoadAverage { get; set; } = [0, 0, 0];

    [JsonPropertyName("loadAverageAvailable")]
    public bool LoadAverageAvailable { get; set; }
}

/// <summary>内存维度。对应 Go: <c>app.SystemPerformanceMemory</c>。</summary>
public sealed class SystemPerformanceMemoryDto
{
    [JsonPropertyName("systemAvailable")]
    public bool SystemAvailable { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("usedBytes")]
    public long UsedBytes { get; set; }

    [JsonPropertyName("availableBytes")]
    public long AvailableBytes { get; set; }

    [JsonPropertyName("usagePercent")]
    public double UsagePercent { get; set; }

    [JsonPropertyName("heapAllocBytes")]
    public long HeapAllocBytes { get; set; }

    [JsonPropertyName("heapSysBytes")]
    public long HeapSysBytes { get; set; }

    [JsonPropertyName("sysBytes")]
    public long SysBytes { get; set; }

    [JsonPropertyName("heapObjects")]
    public long HeapObjects { get; set; }

    [JsonPropertyName("gcCount")]
    public long GCCount { get; set; }

    [JsonPropertyName("lastGCAt")]
    [GoOmitEmpty]
    public DateTime? LastGCAt { get; set; }
}

/// <summary>磁盘维度。对应 Go: <c>app.SystemPerformanceDisk</c>。</summary>
public sealed class SystemPerformanceDiskDto
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("writable")]
    public bool Writable { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("usedBytes")]
    public long UsedBytes { get; set; }

    [JsonPropertyName("freeBytes")]
    public long FreeBytes { get; set; }

    [JsonPropertyName("usagePercent")]
    public double UsagePercent { get; set; }
}

/// <summary>缓存组。对应 Go: <c>app.SystemPerformanceCacheGroup</c>。</summary>
public sealed class SystemPerformanceCacheGroupDto
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("keys")]
    public long Keys { get; set; }

    [JsonPropertyName("clearable")]
    public bool Clearable { get; set; }
}

/// <summary>Redis 客户端池统计。对应 Go: <c>app.SystemPerformanceRedisPool</c>。</summary>
public sealed class SystemPerformanceRedisPoolDto
{
    [JsonPropertyName("hits")]
    public long Hits { get; set; }

    [JsonPropertyName("misses")]
    public long Misses { get; set; }

    [JsonPropertyName("timeouts")]
    public long Timeouts { get; set; }

    [JsonPropertyName("totalConnections")]
    public long TotalConnections { get; set; }

    [JsonPropertyName("idleConnections")]
    public long IdleConnections { get; set; }

    [JsonPropertyName("staleConnections")]
    public long StaleConnections { get; set; }

    [JsonPropertyName("pendingRequests")]
    public long PendingRequests { get; set; }
}

/// <summary>Redis 维度（当前单实例本地协调）。对应 Go: <c>app.SystemPerformanceRedis</c>。</summary>
public sealed class SystemPerformanceRedisDto
{
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("pool")]
    public SystemPerformanceRedisPoolDto Pool { get; set; } = new();

    [JsonPropertyName("cacheGroups")]
    public List<SystemPerformanceCacheGroupDto> CacheGroups { get; set; } = [];

    [JsonPropertyName("statusMessage")]
    [GoOmitEmpty]
    public string StatusMessage { get; set; } = "";
}

/// <summary>构建信息。对应 Go: <c>buildinfo.Info</c>。</summary>
public sealed class SystemPerformanceBuildDto
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("commit")]
    public string Commit { get; set; } = "";

    [JsonPropertyName("buildTime")]
    public string BuildTime { get; set; } = "";

    [JsonPropertyName("goVersion")]
    public string GoVersion { get; set; } = "";
}

/// <summary>系统性能总览。对应 Go: <c>app.AdminSystemPerformance</c>。</summary>
public sealed class AdminSystemPerformanceDto
{
    [JsonPropertyName("collectedAt")]
    public DateTime CollectedAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("host")]
    public SystemPerformanceHostDto Host { get; set; } = new();

    [JsonPropertyName("memory")]
    public SystemPerformanceMemoryDto Memory { get; set; } = new();

    [JsonPropertyName("disk")]
    public SystemPerformanceDiskDto Disk { get; set; } = new();

    [JsonPropertyName("database")]
    public DatabaseRuntimeStatsDto Database { get; set; } = new();

    [JsonPropertyName("redis")]
    public SystemPerformanceRedisDto Redis { get; set; } = new();

    [JsonPropertyName("build")]
    public SystemPerformanceBuildDto Build { get; set; } = new();
}

/// <summary>缓存清理请求。对应 Go: <c>app.AdminCacheClearRequest</c>。</summary>
public sealed class AdminCacheClearRequestDto
{
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";
}

/// <summary>缓存清理结果。对应 Go: <c>app.AdminCacheClearResult</c>。</summary>
public sealed class AdminCacheClearResultDto
{
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";

    [JsonPropertyName("deletedKeys")]
    public long DeletedKeys { get; set; }

    [JsonPropertyName("memoryEntries")]
    public int MemoryEntries { get; set; }

    [JsonPropertyName("groups")]
    public List<AdminCacheClearGroupDto> Groups { get; set; } = [];

    [JsonPropertyName("clearedAt")]
    public DateTime ClearedAt { get; set; }
}

/// <summary>缓存清理分组。对应 Go: <c>app.AdminCacheClearGroupResult</c>。</summary>
public sealed class AdminCacheClearGroupDto
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("deletedKeys")]
    public long DeletedKeys { get; set; }
}

/// <summary>
/// 系统性能与运行时缓存。对应 Go: <c>internal/app/admin_system_performance.go</c>。
/// </summary>
/// <remarks>
/// .NET 运行时等价指标：goroutines→线程池线程数、gomaxprocs→ProcessorCount、
/// heap 统计→GC API。Redis 协调器未移植，恒为本地单实例模式（与默认部署一致）。
/// </remarks>
public sealed class SystemPerformanceService
{
    private static DateTime ProcessStartedAt { get; } = DateTime.UtcNow;

    private readonly Repository _repository;
    private readonly string _dataDir;

    public SystemPerformanceService(Repository repository, string? dataDir = null)
    {
        _repository = repository;
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? "data" : dataDir!;
    }

    /// <summary>系统性能总览。对应 Go: <c>AdminSystemPerformance</c>。</summary>
    public async Task<AdminSystemPerformanceDto> PerformanceAsync(
        User actor, SystemPerformanceRedisDto? redis = null, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        DatabaseRuntimeStatsDto database = await _repository
            .DatabaseRuntimeStatsAsync(cancellationToken).ConfigureAwait(false);
        SystemPerformanceDiskDto disk = CollectDisk();
        AdminSystemPerformanceDto result = new()
        {
            CollectedAt = DateTime.UtcNow,
            Status = database.Connected && disk.Available && disk.Writable ? "healthy" : "degraded",
            Host = CollectHost(),
            Memory = CollectMemory(),
            Disk = disk,
            Database = database,
            Redis = redis ?? LocalRedis(),
        };
        result.Redis.CacheGroups =
        [
            new SystemPerformanceCacheGroupDto { ID = "rateLimits", Label = "本地请求频控", Clearable = true },
        ];
        return result;
    }

    /// <summary>清理运行时缓存（限 runtime 作用域）。对应 Go: <c>ClearAdminRuntimeCache</c>。</summary>
    public AdminCacheClearResultDto ClearRuntimeCache(
        User actor, string scope, int rateLimiterEntries, bool routeCatalogCleared)
    {
        CanvasService.RequireAdmin(actor);
        if (scope.Trim() != "runtime")
        {
            throw AppError.BadAuthRequest("仅支持清理运行时缓存");
        }
        AdminCacheClearResultDto result = new()
        {
            Scope = "runtime",
            ClearedAt = DateTime.UtcNow,
            Groups = [],
        };
        // Redis 协调器未移植（恒为本地模式）：本地频控窗口计数由路由层经
        // InMemoryRateLimiter.ClearWindows() 清理；此处记录路由目录缓存失效。
        if (routeCatalogCleared)
        {
            result.MemoryEntries++;
        }
        result.Groups.Add(new AdminCacheClearGroupDto { ID = "rateLimits", DeletedKeys = rateLimiterEntries });
        return result;
    }

    // ------------------------------------------------------------ 指标采集

    private static SystemPerformanceHostDto CollectHost()
    {
        using Process process = Process.GetCurrentProcess();
        return new SystemPerformanceHostDto
        {
            Hostname = Environment.MachineName,
            OS = Environment.OSVersion.Platform.ToString(),
            Arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            CPUCores = Environment.ProcessorCount,
            Gomaxprocs = Environment.ProcessorCount,
            ProcessID = Environment.ProcessId,
            UptimeSeconds = (long)(DateTime.UtcNow - ProcessStartedAt).TotalSeconds,
            Goroutines = ThreadPool.ThreadCount,
            ActiveWorkerTasks = 0,
            LoadAverage = [0, 0, 0],
            LoadAverageAvailable = false,
        };
    }

    private static SystemPerformanceMemoryDto CollectMemory()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        long total = info.TotalAvailableMemoryBytes;
        long managed = GC.GetTotalMemory(false);
        long used = total > 0 ? Math.Min(managed, total) : 0;
        SystemPerformanceMemoryDto memory = new()
        {
            SystemAvailable = total > 0,
            TotalBytes = total,
            UsedBytes = used,
            AvailableBytes = Math.Max(0, total - used),
            UsagePercent = total > 0 ? used * 100.0 / total : 0,
            HeapAllocBytes = managed,
            HeapSysBytes = managed,
            SysBytes = Environment.WorkingSet,
            HeapObjects = 0,
            GCCount = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2),
        };
        if (info.PauseDurations.Length > 0)
        {
            memory.LastGCAt = DateTime.UtcNow - info.PauseDurations[^1];
        }
        return memory;
    }

    private SystemPerformanceDiskDto CollectDisk()
    {
        SystemPerformanceDiskDto disk = new();
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(_dataDir)) ?? "/";
            DriveInfo drive = new(root);
            if (drive.IsReady)
            {
                disk.Available = true;
                disk.Writable = true;
                disk.TotalBytes = drive.TotalSize;
                disk.FreeBytes = drive.AvailableFreeSpace;
                disk.UsedBytes = drive.TotalSize - drive.AvailableFreeSpace;
                disk.UsagePercent = drive.TotalSize > 0
                    ? disk.UsedBytes * 100.0 / drive.TotalSize
                    : 0;
                try
                {
                    string probe = Path.Combine(_dataDir, ".canvas-write-probe");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                }
                catch (Exception)
                {
                    disk.Writable = false;
                }
            }
        }
        catch (Exception)
        {
            disk.Available = false;
        }
        return disk;
    }

    private static SystemPerformanceRedisDto LocalRedis() => new()
    {
        Configured = false,
        Connected = false,
        Mode = "local",
        StatusMessage = "未配置 Redis，当前使用单实例本地协调",
    };
}
