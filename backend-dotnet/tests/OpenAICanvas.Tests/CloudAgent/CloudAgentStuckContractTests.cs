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

    /// <summary>
    /// 源码级契约：媒体错误检查点必须用最新 revision 重试 CAS，并把「是否真的落地」回传调用方。
    ///
    /// 线上实例（2026-10-13，run <c>agf09c94b248cbdabb578e15c2ecc16ad9</c>）：模型调 generate_media
    /// 拿到 409「画布已变化」后，<c>MediaErrorAsync</c> 的 CAS 同样失败，而返回值被丢掉 ——
    /// <c>callIndex</c> 不推进、<c>tool_failed</c> 不落库、<c>revision</c> 原地不动。
    /// 调度器 2 秒后重跑同一步、撞同一个错误，直到 5 分钟看门狗收尾（日志只有一串「连续 15 次 CAS 冲突」）。
    /// Go 版（<c>cloud_agent_runtime.go:1609</c>）同样丢掉返回值，本 fork 的 count 批量与 auto 免审批
    /// 把这条路径从偶发变成必现。
    ///
    /// 这个测试盯的是「调用点有没有接线」：只测 helper 会漏掉返回值被忽略。
    /// </summary>
    [Fact]
    public void 媒体错误检查点必须重试CAS并回传是否落地()
    {
        string source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "OpenAICanvas.Application", "CloudAgent", "CloudAgentRuntimeExecution.cs"));

        // ① 媒体错误检查点必须返回 bool，让调用方知道失败有没有写进去。
        Assert.Contains("private async Task<bool> MediaErrorAsync(", source);
        // ② 冲突后必须重取最新 revision 再试，而不是拿过期 revision 原地重撞。
        Assert.Contains("CloudAgentAsync(run.UserID, run.ID, CancellationToken.None)", source);
        Assert.Contains("revision = latest.Revision;", source);
        // ③ 写进去了要同步调用方句柄（否则后续 CAS 全用过期的）；这条必须落在 MediaErrorAsync 体内。
        int bodyStart = source.IndexOf("private async Task<bool> MediaErrorAsync(", StringComparison.Ordinal);
        Assert.True(bodyStart >= 0);
        Assert.Contains("run.Revision++;", source[bodyStart..]);
        // ④ 至少一个调用点检查了返回值；全都不检查就等于没修。
        int checkedCalls = CountOccurrences(source, "bool recorded = await MediaErrorAsync(");
        Assert.True(checkedCalls >= 3,
            $"媒体错误检查点的调用点必须检查返回值（当前只找到 {checkedCalls} 处）");
        // ⑤ 写不进去时必须推向终态，绝不能返回 false 让调度器原地重跑 —— 那正是卡死的成因。
        Assert.Contains("if (!recorded)", source);
        Assert.Contains("throw CloudAgentSessionService.CreationConflict();", source);
    }

    /// <summary>
    /// 源码级契约：审批规划阶段的 CAS 失败不得裸抛 409。
    ///
    /// 线上实例（2026-10-13，run <c>agf09c94b248cbdabb578e15c2ecc16ad9</c>，request_approval 模式）：
    /// 模型调 generate_media → <c>PlanApprovalAsync</c> 算出 plan 后 CAS 落审批记录失败 →
    /// 旧的写法直接 <c>throw CreationConflict()</c>。这个 409 逃出媒体错误处理，
    /// 被调度器 <c>catch (AppError error) when (error.Status == 409)</c> 静默吞掉，
    /// 于是每 2 秒原地重跑同一步；事件停在 6 条、revision 停在 6、日志只有一串
    /// 「连续 15 次 CAS 冲突」，5 分钟后被看门狗判 failed —— 用户看到的「生节点卡死」。
    ///
    /// 这段 <c>PlanApprovalAsync</c> 是本 fork 为复用审批逻辑新抽出来的（Go 里是同一条
    /// <c>advanceCloudAgentTool</c> 的内联流程），所以这个坑由移植引入，必须在调用点堵住：
    /// CAS 失败要经 <c>MediaErrorAsync</c> 记进运行，让 callIndex 推进、模型能换招。
    /// </summary>
    [Fact]
    public void 审批规划阶段CAS失败必须记入运行而不是裸抛409()
    {
        string source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "OpenAICanvas.Application", "CloudAgent", "CloudAgentRuntimeExecution.cs"));

        int start = source.IndexOf("private async Task PlanApprovalAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "必须存在审批规划阶段");
        int end = source.IndexOf("private async Task<", start, StringComparison.Ordinal);
        Assert.True(end > start, "无法界定 PlanApprovalAsync 的函数体");
        string body = source[start..end];

        // 该函数里出现的每处 409 重抛，前面都必须先经过 MediaErrorAsync 记录。
        int rethrows = CountOccurrences(body, "throw CloudAgentSessionService.CreationConflict();");
        int guarded = CountOccurrences(body, "bool recorded = await MediaErrorAsync(");
        Assert.True(rethrows > 0, "审批规划阶段应当保留「连失败都写不进去才终态化」的兜底重抛");
        Assert.True(guarded >= rethrows,
            $"PlanApprovalAsync 里每处 409 重抛都必须先经 MediaErrorAsync 记录（重抛 {rethrows} 处，记录 {guarded} 处）");

        // 反向断言：不允许出现「CAS 失败 → 直接重抛」这种无记录的裸抛。
        Assert.DoesNotContain(
            "if (!claimed)\n            {\n                throw CloudAgentSessionService.CreationConflict();\n            }",
            body.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>
    /// 源码级契约：批量建草稿时，每一项都必须先用**当前**画布刷新自己的快照。
    ///
    /// 线上实例（2026-10-13，run <c>ag2e281717c6a9101cea5efc2fce4bd5ea</c>，auto + count=3 + 参考图）：
    /// <c>BakeMediaDraftAsync</c> 逐项调 <c>CreateMediaNodeAsync</c>，而后者内部会再走一次
    /// <c>MediaDocumentAsync</c> 校验 <c>args.SnapshotHash</c>。第一项建成功后画布多了一个草稿节点，
    /// 第二项手里仍是模型原始快照 ⇒ 409「画布已变化」。因为它抛在 CAS 回调内部
    /// （调用点在 <c>PrepareAutoMediaDraftAsync</c> 的 try 之外），绕过工具失败落库、
    /// 被 <c>AdvanceAsync</c> 当并发冲突静默跳过 —— 每 2 秒原地重跑，直到 5 分钟看门狗。
    /// 堆栈（当时实测）：
    /// <c>MediaDocumentAsync:427 → CreateMediaNodeAsync:1061 → BakeMediaDraftAsync:441</c>。
    ///
    /// 修复前 <c>count=1</c> 正常、<c>count>=2</c> 必卡；这就是用户报的「生节点卡死」。
    /// </summary>
    [Fact]
    public void 批量建草稿必须逐项刷新快照而不是沿用原始哈希()
    {
        string source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "OpenAICanvas.Application", "CloudAgent", "CloudAgentRuntimeExecution.cs"));

        int start = source.IndexOf("private async Task BakeMediaDraftAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "必须存在草稿固化方法");
        int end = source.IndexOf("private async Task MutatePlanApprovalAsync(", start, StringComparison.Ordinal);
        Assert.True(end > start, "无法界定 BakeMediaDraftAsync 的函数体");
        string body = source[start..end];

        // ① 循环里必须有一次「建节点之前」的快照刷新：MediaContentHash 的赋值要出现在
        //    CreateMediaNodeAsync 调用之前（建完之后的赋值是给下一项/记账用的）。
        int create = body.IndexOf("_media.CreateMediaNodeAsync(", StringComparison.Ordinal);
        Assert.True(create > 0, "必须调用 CreateMediaNodeAsync");
        int refreshBefore = body.IndexOf(
            "item.SnapshotHash = CloudAgentContracts.MediaContentHash(currentDoc);", StringComparison.Ordinal);
        Assert.True(refreshBefore >= 0 && refreshBefore < create,
            "建节点之前必须用当前画布刷新该项快照，否则 count>=2 时第二项必然 409");
        // ② 刷新用的画布必须是本事务里刚读出来的，不能复用外部 scope 的旧对象。
        Assert.Contains("context.CanvasProjectForUserAsync(", body);
    }

    /// <summary>
    /// 源码级契约：CAS 回调里建草稿失败必须当场记成工具失败，不得穿出事务变成静默 409。
    /// 这是「批量快照」之外的第二道保险：任何残留的建草稿异常都不能再让运行空转。
    /// </summary>
    [Fact]
    public void 建草稿失败必须在事务内记成工具失败不得穿出成静默409()
    {
        string source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "OpenAICanvas.Application", "CloudAgent", "CloudAgentRuntimeExecution.cs"));

        // auto 路径与审批路径各一处：CAS 回调体内用 try/catch 包住 BakeMediaDraftAsync /
        // MutatePlanApprovalAsync，失败时 state.MediaBatch 清空并落 tool 结果。
        int baked = CountOccurrences(source, "await BakeMediaDraftAsync(context, run, state, _runtimePolicy.Current(), plan)");
        Assert.True(baked >= 1, "找不到 auto 路径的草稿固化调用");
        int guarded = CountOccurrences(source, "ToolResult(run.ID, state, call, null, cause);");
        Assert.True(guarded >= 2,
            $"auto 与审批两条路径都必须把建草稿失败记成工具失败（当前只找到 {guarded} 处）");
        // 失败时必须清掉半成品批次，避免下一轮拿残缺 batch 继续跑。
        Assert.Contains("state.MediaBatch = null;", source);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
        return count;
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
