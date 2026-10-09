#nullable enable
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using OpenAICanvas.Application.CloudAgent;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// canvas_apply_ops 变更记录链路的回归契约。
///
/// 线上事故（2026-10-09，画布 <c>garMU43rjBfPDSpiExCUK</c>）：Agent 用「操作画布」把反推出来的
/// 提示词正文写进已有节点，界面报
/// <c>更新画布内容失败 / 执行失败：画布变更缺少可追踪的 Agent 操作信息</c>。
///
/// 根因：Go 的 <c>applyCloudAgentCanvas</c> 接收 <c>cloudAgentCanvasEventRecorder(run.ID, state)</c>，
/// RunID 由 recorder 内部注入；.NET 把这段内联展开成
/// <c>RecordAsync + EmitCanvasChangeOnlyAsync</c> 两次调用，**漏传了 RunID**。
/// <c>RecordAsync</c> 的首道校验就是 <c>RunID.Length == 0 → BadAuthRequest</c>。
/// 失败形态并不"干净"：同一步里的 <c>SaveDocumentAsync</c> 先落了库，随后抛出的异常在
/// 派发 lambda 里被吞成工具错误（与 Go 一致，事务照常提交）—— 画布确实被改了，却没有
/// 对应的 <c>cloudAgentCanvasMutations</c> 行，既无法撤销也无从追溯；
/// 用户看到「更新画布内容失败」，画布上却已经有内容（线上实测节点 content 已写入）。
/// 实证：全库 <c>cloudAgentCanvasMutations</c> 只有 <c>generate_media_*</c> 两行，
/// <c>canvas_apply_ops</c> 一行都没有 —— 这条路径从未成功过。
///
/// 对应 Go: <c>app/cloud_agent_tools.go:applyCloudAgentCanvas</c>、
/// <c>app/cloud_agent_mutation.go:cloudAgentMutationRecorderForRun</c>。
/// </summary>
public sealed class CloudAgentCanvasApplyContractTests : IDisposable
{
    private const string CanvasID = "canvas-1";
    private const string UserID = "user-a";

    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;

    public CloudAgentCanvasApplyContractTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-apply-{Guid.NewGuid():N}.db");
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
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

    private async Task MigrateAsync()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();
    }

    /// <summary>画布必须存在，否则事件投影阶段会抛「画布不存在」。</summary>
    private Task SeedCanvasAsync() => _repository.CreateAsync(new CanvasProject
    {
        ID = CanvasID,
        UserID = UserID,
        Title = "测试画布",
        PayloadJSON = """{"nodes":[],"connections":[]}""",
        Revision = 1,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    });

    private static CloudAgentExecution NewRun(string runID) => new()
    {
        ID = runID,
        UserID = UserID,
        Status = "running",
        Revision = 1,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Journal = [],
        Transcript = [],
    };

    private static CloudAgentRuntimeDto NewState() => new()
    {
        Request = new CloudAgentRequestDto { CanvasID = CanvasID, Prompt = "把正文写进节点" },
        Canonical = new CloudAgentCanonicalRequestDto
        {
            SystemPrompt = "system",
            Messages =
            [
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["role"] = JsonSerializer.SerializeToElement("user"),
                    ["content"] = JsonSerializer.SerializeToElement("把正文写进节点"),
                },
            ],
        },
        TextHistory = [new CloudAgentTextMessageDto("user", "把正文写进节点")],
        Events = [],
    };

    private async Task<(CloudAgentExecution Run, CloudAgentRuntimeDto State)> SeedRunAsync(string runID)
    {
        CloudAgentExecution run = NewRun(runID);
        CloudAgentRuntimeDto state = NewState();
        CloudAgentContracts.Save(run, state);
        await _repository.EnsureCloudAgentAsync(run);
        return (run, state);
    }

    private async Task<int> MutationCountAsync()
    {
        await using SqliteConnection connection = (SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();
        return (int)await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"cloudAgentCanvasMutations\"");
    }

    /// <summary>
    /// 记录器必须在落库前自行补上 RunID —— 这正是 <c>ApplyCanvasOpsAsync</c> 依赖的契约。
    /// 传入的 input 故意不带 RunID（与 Go 各调用点一致），落库行仍须带正确的 runId。
    /// </summary>
    [Fact]
    public async Task 画布操作记录器自行注入RunID并落库()
    {
        await MigrateAsync();
        await SeedCanvasAsync();
        (CloudAgentExecution run, CloudAgentRuntimeDto state) = await SeedRunAsync("run-apply");

        bool recorded = await _repository.MutateCloudAgentAsync(UserID, run.ID, run.Revision,
            async (current, context) =>
            {
                await CloudAgentMutations.RecorderForRun(run.ID, state)(context, new CloudAgentMutationInput
                {
                    // 刻意不填 RunID：Go 的调用点同样不填，靠 recorder 注入。
                    UserID = UserID,
                    CanvasID = CanvasID,
                    StepID = "call-1",
                    Operation = "canvas_apply_ops",
                    BeforeSnapshotHash = "before-hash",
                    AfterSnapshotHash = "after-hash",
                    BeforeJSON = """{"nodes":[],"connections":[]}""",
                }).ConfigureAwait(false);
                await context.SaveRunAsync(current).ConfigureAwait(false);
                return true;
            });
        Assert.True(recorded);

        await using SqliteConnection connection = (SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();
        string? runID = await connection.ExecuteScalarAsync<string>(
            "SELECT \"runId\" FROM \"cloudAgentCanvasMutations\" WHERE \"operation\" = 'canvas_apply_ops'");
        string? stepID = await connection.ExecuteScalarAsync<string>(
            "SELECT \"stepId\" FROM \"cloudAgentCanvasMutations\" WHERE \"operation\" = 'canvas_apply_ops'");

        Assert.Equal(run.ID, runID);
        Assert.Equal("call-1", stepID);
    }

    /// <summary>
    /// 反证：绕过记录器直接调 <c>RecordAsync</c> 且不填 RunID，必须抛线上那条报错。
    /// 这条断言把「RunID 只能由记录器注入」钉死 —— 一旦有人再把两步内联展开，
    /// 就会重新走到这里。
    /// </summary>
    [Fact]
    public async Task 裸调RecordAsync漏填RunID即抛可追踪性错误()
    {
        await MigrateAsync();
        await SeedCanvasAsync();
        (CloudAgentExecution run, _) = await SeedRunAsync("run-bare");
        Assert.Equal(0, await MutationCountAsync());

        AppError error = await Assert.ThrowsAsync<AppError>(() =>
            _repository.MutateCloudAgentAsync(UserID, run.ID, run.Revision,
                (_, context) => CloudAgentMutations.RecordAsync(context, new CloudAgentMutationInput
                {
                    UserID = UserID,
                    CanvasID = CanvasID,
                    StepID = "call-1",
                    Operation = "canvas_apply_ops",
                    BeforeSnapshotHash = "before-hash",
                    AfterSnapshotHash = "after-hash",
                    BeforeJSON = """{"nodes":[],"connections":[]}""",
                })));

        Assert.Equal("画布变更缺少可追踪的 Agent 操作信息", error.Message);
        // 本测试的 lambda 不吞异常，事务在这里整笔回滚 → 记录表 0 行。
        // 产线路径不同：AdvanceToolAsync 把该异常吞成工具错误并提交事务，
        // 画布已被 SaveDocumentAsync 写脏、却没有变更记录（见类型注释）。
        Assert.Equal(0, await MutationCountAsync());
    }

    /// <summary>
    /// 源码级契约：画布操作的执行入口必须经 run 绑定的记录器，
    /// 不得再内联展开成裸的 <c>RecordAsync</c>（漏 RunID 的陷阱就在那一步）。
    /// </summary>
    [Fact]
    public void 画布操作执行必须经run绑定的记录器()
    {
        string source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "OpenAICanvas.Application", "CloudAgent", "CloudAgentRuntimeExecution.cs"));
        string body = SourceSection(
            source,
            "private async Task<JsonObject> ApplyCanvasOpsAsync(",
            "private async Task<string> CompleteLenientAsync(");

        Assert.Contains("RecorderForRun(run.ID", body);
        Assert.Contains("RunID", body);
        Assert.DoesNotContain("CloudAgentMutations.RecordAsync(", body);
        Assert.DoesNotContain("EmitCanvasChangeOnlyAsync(", body);
    }

    private static string SourceSection(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到起点标记：{startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"未找到终点标记：{endMarker}");
        return source[start..end];
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
