#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OpenAICanvas.Application.CloudAgent;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// 「同轮多写接续快照」的回归契约。对应 Go:
/// <c>internal/app/cloud_agent_step_hash.go</c> + <c>cloud_agent_step_hash_test.go</c>。
/// </summary>
/// <remarks>
/// 回归（2026-09-15）：模型一轮里并发提交多个写操作（实测：给 6 个镜头建图时一轮 3 个
/// generate_media），它们共享同一个「读画布时的哈希」。第一个写成功后画布就变了，后续调用
/// 全部撞快照 CAS，模型得靠报错里的新哈希重试 —— 而下一轮又并发提交，十几轮都跑不完。
/// <para>
/// 这里锁住放宽的<b>边界</b>：只把「与本轮基线同源」的后续调用接到当前哈希；
/// 本轮第一个调用、模型自己已改用新哈希的调用、以及跨轮的情况都不动。
/// 画布被用户手改（无 mutation 行）或别的运行改（RunID 不同）时也必须拒绝。
/// </para>
/// </remarks>
public sealed class CloudAgentStepHashContractTests : IDisposable
{
    private const string UserID = "user";
    private const string CanvasID = "agent-canvas";

    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly CloudAgentRuntimeService _runtime;

    public CloudAgentStepHashContractTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-step-hash-{Guid.NewGuid():N}.db");
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
        // 接续快照只用到仓储（画布 + 变更记录），其余依赖不参与。
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

    // ------------------------------------------------------------------ 夹具

    private async Task MigrateAsync() => await new SchemaMigrator(_database).MigrateAsync();

    /// <summary>建一张只含一个节点的画布，返回它的初始画布哈希与媒体内容哈希。</summary>
    private async Task<(string CanvasHash, JsonObject Doc)> SeedCanvasAsync()
    {
        JsonObject doc = NewDocument();
        await _repository.UpsertCanvasProjectAsync(NewCanvasProject(Json(doc), revision: 0));
        return (CloudAgentContracts.CanvasHash(doc), doc);
    }

    private static CanvasProject NewCanvasProject(string payload, long revision) => new()
    {
        ID = CanvasID,
        UserID = UserID,
        ProjectID = "",
        Title = "画布",
        PayloadJSON = payload,
        Revision = revision,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    /// <summary>把画布改成「多了一个节点」的样子，模拟本轮第一个写操作已经落盘。</summary>
    private async Task<(string Hash, string Raw)> MutateCanvasAsync(JsonObject doc, string nodeID, string nodeType)
    {
        ((JsonArray)doc["nodes"]!).Add(new JsonObject
        {
            ["id"] = nodeID,
            ["type"] = nodeType,
            ["title"] = "本轮写操作留下的节点",
        });
        string raw = Json(doc);
        CanvasProject canvas = await _repository.CanvasProjectForUserAsync(UserID, CanvasID)
            ?? throw new InvalidOperationException("画布不存在");
        await _repository.UpsertCanvasProjectAsync(NewCanvasProject(raw, canvas.Revision));
        return (CloudAgentContracts.CanvasHash(doc), raw);
    }

    /// <summary>把画布改回指定内容（不留下任何 mutation 行）。</summary>
    private async Task RestoreCanvasAsync(string payload)
    {
        CanvasProject canvas = await _repository.CanvasProjectForUserAsync(UserID, CanvasID)
            ?? throw new InvalidOperationException("画布不存在");
        await _repository.UpsertCanvasProjectAsync(NewCanvasProject(payload, canvas.Revision));
    }

    private async Task SeedMutationAsync(
        string mutationID, string runID, string stepID, string before, string after, string beforeJSON)
    {
        await _repository.CreateCloudAgentCanvasMutationAsync(new CloudAgentCanvasMutation
        {
            ID = mutationID,
            RunID = runID,
            UserID = UserID,
            CanvasID = CanvasID,
            StepID = stepID,
            Operation = "canvas_apply_ops",
            BeforeSnapshotHash = before,
            AfterSnapshotHash = after,
            BeforeJSON = beforeJSON,
            Status = "applied",
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static JsonObject NewDocument() => new()
    {
        ["nodes"] = new JsonArray(new JsonObject
        {
            ["id"] = "image-base",
            ["type"] = "image",
            ["title"] = "底图",
        }),
        ["connections"] = new JsonArray(),
    };

    private static string Json(JsonObject doc) => doc.ToJsonString(GoJson.WriteOptions);

    /// <summary>构造一个 generate_media 调用；snapshotHash 传空表示「模型漏传」。</summary>
    private static CloudAgentCallDto MediaCall(string callID, string snapshotHash, string nodeID = "image-new")
    {
        CloudAgentMediaArgs args = new()
        {
            Mode = "image",
            Prompt = "画一只猫",
            NodeID = nodeID,
            Title = "新图",
            SnapshotHash = snapshotHash,
        };
        return new CloudAgentCallDto
        {
            ID = callID,
            Function = new CloudAgentCallFunctionDto
            {
                Name = "generate_media",
                Arguments = JsonSerializer.Serialize(args, GoJson.WriteOptions),
            },
        };
    }

    private static CloudAgentExecution NewRun(string runID) => new()
    {
        ID = runID,
        UserID = UserID,
        Status = "running",
        Revision = 1,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static CloudAgentRuntimeDto NewState(string baseline, CloudAgentCallDto first, CloudAgentCallDto second)
        => new()
        {
            Request = new CloudAgentRequestDto { CanvasID = CanvasID },
            Calls = [first, second],
            CallIndex = 1,
            Step = 3,
            StepSnapshotHash = baseline,
        };

    private static string SnapshotHashOf(CloudAgentCallDto call)
    {
        JsonObject args = (JsonObject)JsonNode.Parse(call.Function.Arguments)!;
        return CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(args, "snapshotHash"));
    }

    // ------------------------------------------------------------------ 用例

    [Fact]
    public async Task 同轮后续写操作应被接到最新哈希()
    {
        await MigrateAsync();
        (string baseline, JsonObject doc) = await SeedCanvasAsync();
        Assert.NotEqual("", baseline);

        CloudAgentCallDto first = MediaCall("media-call-0", baseline);
        CloudAgentCallDto second = MediaCall("media-call-1", baseline);
        CloudAgentRuntimeDto state = NewState(baseline, first, second);
        CloudAgentExecution run = NewRun("run-1");

        // ① 画布没变 → 原样返回，不做任何改动。
        string untouched = second.Function.Arguments;
        CloudAgentCallDto got = await _runtime.RefreshStepSnapshotHashAsync(
            run, state, second, CancellationToken.None);
        Assert.Equal(untouched, got.Function.Arguments);

        // ② 画布变了（模拟本轮第一个写操作已成功）→ 第二个调用应接到最新哈希。
        (string latest, string canvasRaw) = await MutateCanvasAsync(doc, "sibling-write", "image");
        Assert.NotEqual(baseline, latest);
        await SeedMutationAsync("mutation-1", run.ID, first.ID, baseline, latest, canvasRaw);

        await _runtime.RefreshStepSnapshotHashAsync(run, state, second, CancellationToken.None);
        Assert.Equal(latest, SnapshotHashOf(second));
        // 刷新哈希时不能改动其它参数。
        JsonObject refreshed = (JsonObject)JsonNode.Parse(second.Function.Arguments)!;
        Assert.Equal("image-new", CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(refreshed, "nodeId")));
        Assert.Equal("画一只猫", CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(refreshed, "prompt")));

        // ③ 本轮第一个调用不刷新（它的哈希本身就该被校验）。
        //    真实运行时 call 恒为 Calls[CallIndex]，所以这里把游标回到 0 才是等价场景。
        string firstArguments = first.Function.Arguments;
        state.CallIndex = 0;
        await _runtime.RefreshStepSnapshotHashAsync(run, state, first, CancellationToken.None);
        state.CallIndex = 1;
        Assert.Equal(firstArguments, first.Function.Arguments);

        // ④ 模型已自行改用新哈希的调用不刷新 —— 那种情况要如实报冲突，别掩盖。
        CloudAgentCallDto own = MediaCall("media-call-1", "model-reread-hash");
        string ownArguments = own.Function.Arguments;
        await _runtime.RefreshStepSnapshotHashAsync(run, state, own, CancellationToken.None);
        Assert.Equal(ownArguments, own.Function.Arguments);

        // ⑤ 批卡批准后的真实序列：服务端把整批调用统一改写成同一个新哈希，基线一起抬到该哈希；
        //    此后逐条提交，**每提交一条画布都会再变**（那条草稿节点要绑上 taskId）。
        //    后面几条必须仍能被补到当时的哈希 —— 补不上就撞快照 CAS，
        //    实测表现就是「一轮里第二个镜头必被拒」。
        state.StepSnapshotHash = latest;
        (string afterBind, string boundRaw) = await MutateCanvasAsync(doc, "bound-task", "video");
        Assert.NotEqual(latest, afterBind);
        await SeedMutationAsync("mutation-2", run.ID, second.ID, latest, afterBind, boundRaw);

        await _runtime.RefreshStepSnapshotHashAsync(run, state, second, CancellationToken.None);
        Assert.Equal(afterBind, SnapshotHashOf(second));

        // ⑥ 漏传 snapshotHash 的 generate_media 应补当前媒体内容哈希。
        CloudAgentCallDto empty = MediaCall("media-call-2", snapshotHash: "");
        await _runtime.RefreshStepSnapshotHashAsync(run, state, empty, CancellationToken.None);
        CanvasProject? canvas = await _repository.CanvasProjectForUserAsync(UserID, CanvasID);
        JsonObject currentDoc = CloudAgentJsonHelpers.Document(canvas!.PayloadJSON);
        Assert.Equal(CloudAgentContracts.MediaContentHash(currentDoc), SnapshotHashOf(empty));
    }

    [Fact]
    public async Task 外部写入不能被吸收()
    {
        await MigrateAsync();
        (string baseline, JsonObject doc) = await SeedCanvasAsync();
        CloudAgentCallDto first = MediaCall("media-call-0", baseline);
        CloudAgentCallDto second = MediaCall("media-call-1", baseline);
        CloudAgentRuntimeDto state = NewState(baseline, first, second);
        CloudAgentExecution run = NewRun("run-current");
        string originalArguments = second.Function.Arguments;

        // 画布直接被改（自动保存 / 另一个标签页），没有当前运行的 mutation 行。
        await MutateCanvasAsync(doc, "external-node", "image");
        await _runtime.RefreshStepSnapshotHashAsync(run, state, second, CancellationToken.None);
        Assert.Equal(originalArguments, second.Function.Arguments);

        // 另一个 Agent 运行的写入，对当前运行同样是冲突。
        await RestoreCanvasAsync(Json(NewDocument()));
        JsonObject otherDoc = NewDocument();
        (string otherHash, string otherRaw) = await MutateCanvasAsync(otherDoc, "other-run-node", "image");
        await SeedMutationAsync("mutation-other", "run-other", "other-call", baseline, otherHash, otherRaw);

        await _runtime.RefreshStepSnapshotHashAsync(run, state, second, CancellationToken.None);
        Assert.Equal(originalArguments, second.Function.Arguments);
    }

    /// <summary>基线与调用不同源（模型中途重读过画布）时不得替它抹平。</summary>
    [Fact]
    public async Task 基线不同的调用不被改写()
    {
        await MigrateAsync();
        (string baseline, JsonObject doc) = await SeedCanvasAsync();
        CloudAgentCallDto first = MediaCall("media-call-0", baseline);
        CloudAgentCallDto second = MediaCall("media-call-1", baseline);
        CloudAgentRuntimeDto state = NewState(baseline, first, second);
        CloudAgentExecution run = NewRun("run-2");
        (string latest, string canvasRaw) = await MutateCanvasAsync(doc, "sibling-write", "image");
        await SeedMutationAsync("mutation-1", run.ID, first.ID, baseline, latest, canvasRaw);

        // 基线被清空（跨轮）时同样不该改写。
        state.StepSnapshotHash = "";
        await _runtime.RefreshStepSnapshotHashAsync(run, state, second, CancellationToken.None);
        Assert.Equal(baseline, SnapshotHashOf(second));

        // 基线与画布都已经与本调用无关时，也不改写。
        state.StepSnapshotHash = "another-step-baseline";
        await _runtime.RefreshStepSnapshotHashAsync(run, state, second, CancellationToken.None);
        Assert.Equal(baseline, SnapshotHashOf(second));
    }

    /// <summary>
    /// 直读型护栏：只有非写工具、且非本轮第一个调用时才会去查画布。
    /// 写工具名单就是 Go 的 <c>cloudAgentWrite</c>，漏一个都会让该工具回到「必被拒」的旧行为。
    /// </summary>
    [Fact]
    public async Task 非写工具不参与接续快照()
    {
        await MigrateAsync();
        (string baseline, JsonObject doc) = await SeedCanvasAsync();
        CloudAgentCallDto first = MediaCall("media-call-0", baseline);
        CloudAgentCallDto read = new()
        {
            ID = "call-read",
            Function = new CloudAgentCallFunctionDto
            {
                Name = "canvas_get_state",
                Arguments = JsonSerializer.Serialize(
                    new { snapshotHash = baseline, nodeId = "image-new" }, GoJson.WriteOptions),
            },
        };
        CloudAgentRuntimeDto state = NewState(baseline, first, read);
        // canvas_get_state 不是写工具，未登记在写工具名单里。
        Assert.False(CloudAgentTools.IsWrite(read.Function.Name));
        CloudAgentExecution run = NewRun("run-3");
        (string latest, string canvasRaw) = await MutateCanvasAsync(doc, "sibling-write", "image");
        await SeedMutationAsync("mutation-1", run.ID, first.ID, baseline, latest, canvasRaw);

        string originalArguments = read.Function.Arguments;
        await _runtime.RefreshStepSnapshotHashAsync(run, state, read, CancellationToken.None);
        Assert.Equal(originalArguments, read.Function.Arguments);
    }
}
