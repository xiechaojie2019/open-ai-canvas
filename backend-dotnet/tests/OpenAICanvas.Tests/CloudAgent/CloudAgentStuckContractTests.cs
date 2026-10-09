#nullable enable
using Microsoft.Data.Sqlite;
using OpenAICanvas.Application.CloudAgent;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// 卡死兜底的回归契约。对应 Go: <c>app/cloud_agent_stuck.go</c> + <c>cloud_agent_stuck_test.go</c>。
///
/// 为什么需要它：调度器把所有 <c>409</c> 当成"并发推进，跳过等下一轮"静默吞掉
/// （见 <c>CloudAgentSchedulerWorker</c>）。而「画布已变化，本次未写入」这类 CAS 冲突也是 409，
/// 一旦某个运行稳定地落进这个分支，它就会每 2 秒空转一次、日志里一行都没有，
/// 永久停在 <c>running</c>。
///
/// 线上实例（2026-10-09，run <c>agec93ff351ad71aab8a61b69bdd4e00b3</c>）：画布操作写失败时
/// 画布已被写脏，模型仍拿旧快照哈希重试 ⇒ 规划审批恒 409 ⇒ 从 04:06 卡到人工发现为止。
/// Go 侧靠"5 分钟无进展即判定卡死并终态化"兜住，.NET 此前未移植这层保护。
/// </summary>
public sealed class CloudAgentStuckContractTests : IDisposable
{
    private const string UserID = "user-a";
    private const string CanvasID = "canvas-1";

    /// <summary>Sha256Hex("")。空偏好层时 RevisionOf/HashContent 都落在这个值上。</summary>
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly CloudAgentRuntimeService _runtime;

    public CloudAgentStuckContractTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-stuck-{Guid.NewGuid():N}.db");
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
        // 卡死判定只用到仓储：解码是静态的，其余依赖不参与。
        _runtime = new CloudAgentRuntimeService(_repository, null!, null!, null!, null!, null!, null!);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 构造一份能通过 <c>ValidateRuntime</c> 的执行状态。
    /// 偏好层留空：此时 <c>RevisionOf([]) == TextOf([]) 的哈希 == Sha256Hex("")</c>，
    /// 策略里的 profileRevision/profileHash 取同一个值即可自洽。
    /// <c>TaskIDs</c> 至少要有根任务，否则校验直接判「task history is invalid」。
    /// </summary>
    private static CloudAgentRuntimeDto NewState(string activeTaskID = "") => new()
    {
        Request = new CloudAgentRequestDto
        {
            CanvasID = CanvasID,
            Prompt = "把正文写进节点",
            PermissionMode = "request_approval",
            IdempotencyKey = "idem-0000000001",
            Model = "glm-5.3-flash",
            ChannelID = "CHANNEL_000001",
            ChannelModelKey = "glm-5.3-flash",
            ReasoningMode = "off",
            Budget = new CloudAgentBudgetDto { MaxCredits = 200 },
        },
        Policy = new CloudAgentPolicySnapshotDto
        {
            SystemPolicyID = "cloud-agent-system",
            SystemPolicyVersion = 2,
            // 三个策略哈希都必须是 64 位小写十六进制（IsSha256Hex）。
            SystemPolicyHash = new string('a', 64),
            MediaPolicyID = "cloud-agent-media",
            MediaPolicyVersion = 2,
            MediaPolicyHash = new string('b', 64),
            CapabilitySetVersion = "canvas-capabilities/v4",
            CapabilitySetHash = new string('c', 64),
            ReasoningMode = "off",
            CompilerVersion = CloudAgentContracts.CompilerVersion,
            ProfileRevision = EmptyHash,
            ProfileHash = EmptyHash,
        },
        Profile = new CloudAgentProfileSnapshotDto { Revision = EmptyHash, Hash = EmptyHash, Layers = [] },
        Canonical = new CloudAgentCanonicalRequestDto { SystemPrompt = "system" },
        ActiveTaskID = activeTaskID,
        // 活跃任务必须在任务历史里，校验会查这一条。
        TaskIDs = activeTaskID.Length == 0 ? ["task-root"] : ["task-root", activeTaskID],
        Step = 1,
        Events = [],
    };

    private static CloudAgentExecution NewRun(string runID) => new()
    {
        ID = runID,
        UserID = UserID,
        Status = "running",
        Revision = 1,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private async Task MigrateAsync() => await new SchemaMigrator(_database).MigrateAsync();

    /// <summary>
    /// 落一条运行，并把最后一条事件的时间定在 <paramref name="lastEventAgo"/> 之前。
    /// 事件时间只能这样造：<c>AddEvent</c> 一律写 UtcNow。
    /// </summary>
    private async Task SeedRunAsync(string runID, TimeSpan lastEventAgo, string activeTaskID = "")
    {
        CloudAgentExecution run = NewRun(runID);
        CloudAgentRuntimeDto state = NewState(activeTaskID);
        CloudAgentContracts.AddEvent(state, run.ID, "assistant_message",
            CloudAgentContracts.Payload(("text", "起个头")));
        state.Events[^1].CreatedAt = DateTime.UtcNow - lastEventAgo;
        CloudAgentContracts.Save(run, state);
        await _repository.EnsureCloudAgentAsync(run);
    }

    private Task SeedTaskAsync(string taskID, string status) => _repository.CreateAsync(new TaskEntity
    {
        ID = taskID,
        UserID = UserID,
        ProjectID = CanvasID,
        Type = "canvas_text",
        Operation = CloudAgentContracts.CloudAgentOperation,
        Status = status,
        Prompt = "占位",
        InputJSON = "{}",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    });

    private async Task<CloudAgentExecution> ReloadAsync(string runID) =>
        await _repository.CloudAgentAsync(UserID, runID) ?? throw new InvalidOperationException("运行不存在");

    [Fact]
    public async Task 长时间没有进展的运行必须被判卡死并收尾()
    {
        await MigrateAsync();
        await SeedRunAsync("run-stuck-idle", TimeSpan.FromMinutes(10));

        double? idle = await _runtime.TryTerminateStuckCloudAgentAsync(await ReloadAsync("run-stuck-idle"));

        Assert.NotNull(idle);
        Assert.True(idle >= 9, $"静止时长应约 10 分钟，实际 {idle}");
        CloudAgentExecution after = await ReloadAsync("run-stuck-idle");
        Assert.NotEqual("running", after.Status);
        Assert.NotEqual("queued", after.Status);
        // 必须留下原因，否则用户只看到"突然停了"。
        Assert.False(string.IsNullOrWhiteSpace(after.FailureMessage));
        Assert.Contains("已判定为卡住", after.FailureMessage);
    }

    [Fact]
    public async Task 刚有进展的运行不该被判卡死()
    {
        await MigrateAsync();
        await SeedRunAsync("run-stuck-fresh", TimeSpan.FromSeconds(5));

        double? idle = await _runtime.TryTerminateStuckCloudAgentAsync(await ReloadAsync("run-stuck-fresh"));

        Assert.Null(idle);
        Assert.Equal("running", (await ReloadAsync("run-stuck-fresh")).Status);
    }

    [Fact]
    public async Task 还有任务在跑就不算卡死()
    {
        await MigrateAsync();
        await SeedTaskAsync("live-task", TaskStatus.TaskStatusRunning);
        await SeedRunAsync("run-stuck-live", TimeSpan.FromMinutes(10), activeTaskID: "live-task");

        double? idle = await _runtime.TryTerminateStuckCloudAgentAsync(await ReloadAsync("run-stuck-live"));

        // 任务还在排队/运行 → 不算卡死，绝不能误杀。
        Assert.Null(idle);
        Assert.Equal("running", (await ReloadAsync("run-stuck-live")).Status);
    }

    [Fact]
    public async Task 指向已终态任务的运行算没有在跑并触发兜底()
    {
        await MigrateAsync();
        await SeedTaskAsync("dead-task", TaskStatus.TaskStatusFailed);
        await SeedRunAsync("run-stuck-dead", TimeSpan.FromMinutes(10), activeTaskID: "dead-task");

        double? idle = await _runtime.TryTerminateStuckCloudAgentAsync(await ReloadAsync("run-stuck-dead"));

        Assert.NotNull(idle);
        Assert.NotEqual("running", (await ReloadAsync("run-stuck-dead")).Status);
    }

    /// <summary>
    /// 源码级契约：调度器必须先兜底再推进，并把持续 CAS 冲突记下来。
    /// 只吞 409 不留痕正是线上那次"永久卡死、零日志"的直接成因。
    /// </summary>
    [Fact]
    public void 调度器必须先兜底再推进并记录持续冲突()
    {
        string source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "OpenAICanvas.Web", "Workers", "CloudAgentSchedulerWorker.cs"));

        int stuck = source.IndexOf("TryTerminateStuckCloudAgentAsync", StringComparison.Ordinal);
        int advance = source.IndexOf("runtime.AdvanceAsync(", StringComparison.Ordinal);
        Assert.True(stuck >= 0, "调度器必须调用卡死兜底");
        Assert.True(advance >= 0, "调度器必须调用 AdvanceAsync");
        Assert.True(stuck < advance, "兜底必须先于推进，否则卡住的运行会先被空转一遍");

        Assert.Contains("ConflictLogThreshold", source);
        Assert.Contains("_conflictStreak", source);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenAICanvas.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
    }
}
