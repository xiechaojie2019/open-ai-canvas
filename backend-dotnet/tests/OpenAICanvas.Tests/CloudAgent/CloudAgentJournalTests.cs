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
using OpenAICanvas.Domain.Serialization;
using Xunit;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// Agent 执行日志（Go v28）：checkpoint 剥离与 journal/transcript 落库、
/// append-only 校验、v2 状态重建、Recent 事件读取。
/// 对应 Go: <c>cloudAgentCheckpoint</c> / <c>cloudAgentDecode</c> / <c>SaveCloudAgent</c>。
/// </summary>
public sealed class CloudAgentJournalTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;

    public CloudAgentJournalTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-journal-{Guid.NewGuid():N}.db");
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    private static CloudAgentRuntimeDto NewState(string runID, params string[] texts) =>
        new()
        {
            Request = new CloudAgentRequestDto { CanvasID = "canvas-1", Prompt = "做个视频" },
            Canonical = new CloudAgentCanonicalRequestDto
            {
                SystemPrompt = "system",
                Messages =
                [
                    new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["role"] = JsonSerializer.SerializeToElement("user"),
                        ["content"] = JsonSerializer.SerializeToElement("做个视频"),
                    },
                ],
            },
            TextHistory = [new CloudAgentTextMessageDto("user", texts.Length > 0 ? texts[0] : "hi")],
            Events = BuildEvents(runID, 2),
        };

    private static List<CloudAgentEventDto> BuildEvents(string runID, int count, int startSeq = 1)
    {
        List<CloudAgentEventDto> events = [];
        for (int index = 0; index < count; index++)
        {
            int seq = startSeq + index;
            events.Add(new CloudAgentEventDto
            {
                EventID = $"{runID}:{seq}",
                RunID = runID,
                Seq = seq,
                Type = "tool_completed",
                Payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["toolName"] = JsonSerializer.SerializeToElement("canvas_get_state"),
                },
                CreatedAt = DateTime.UtcNow,
            });
        }
        return events;
    }

    private static CloudAgentExecution NewRun(string runID) => new()
    {
        ID = runID,
        UserID = "user-a",
        Status = "running",
        Revision = 1,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Journal = [],
        Transcript = [],
    };

    [Fact]
    public async Task 检查点剥离三份大字段并落journal与transcript()
    {
        await MigrateAsync();
        CloudAgentExecution run = NewRun("run-1");
        CloudAgentRuntimeDto state = NewState("run-1", "历史消息");
        CloudAgentContracts.Save(run, state);

        Assert.Equal(2, run.CheckpointVersion);
        Assert.Equal(2, run.EventCount);
        Assert.Equal(2, run.MessageCount); // 1 canonical + 1 history
        // StateJSON 已剥离。
        using (JsonDocument document = JsonDocument.Parse(run.StateJSON))
        {
            // Go 的 checkpoint 同样保留 events 键但值为空（nil 序列化为 null）。
            Assert.True(document.RootElement.TryGetProperty("events", out JsonElement events));
            Assert.True(events.ValueKind is JsonValueKind.Null or JsonValueKind.Array
                && events.ValueKind != JsonValueKind.Array || events.GetArrayLength() == 0);
        }

        await _database.InTransactionAsync(async (connection, transaction) =>
        {
            await _repository.SaveCloudAgentInTxAsync(connection, transaction, run, CancellationToken.None);
            return true;
        });

        await using SqliteConnection connection = (SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM \"cloudAgentEventRecords\""));
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM \"cloudAgentMessageRecords\""));

        // 重新加载并重建：事件/消息与保存前一致。
        CloudAgentExecution? reloaded = await _repository.CloudAgentAsync("user-a", "run-1");
        Assert.NotNull(reloaded);
        CloudAgentRuntimeDto decoded = CloudAgentContracts.Decode(reloaded);
        Assert.Equal(2, decoded.Events.Count);
        Assert.Equal("canvas_get_state", decoded.Events[0].Payload["toolName"].GetString());
        Assert.Single(decoded.Canonical.Messages);
        Assert.Single(decoded.TextHistory);
        Assert.Equal("历史消息", decoded.TextHistory[0].Content);
    }

    [Fact]
    public async Task 追加事件允许_截断与改写拒绝()
    {
        await MigrateAsync();
        CloudAgentExecution run = NewRun("run-1");
        CloudAgentContracts.Save(run, NewState("run-1"));
        await _database.InTransactionAsync(async (connection, transaction) =>
        {
            await _repository.SaveCloudAgentInTxAsync(connection, transaction, run, CancellationToken.None);
            return true;
        });

        // 追加新事件：合法。
        CloudAgentExecution? reloaded = await _repository.CloudAgentAsync("user-a", "run-1");
        CloudAgentRuntimeDto state = CloudAgentContracts.Decode(reloaded!);
        state.Events.Add(BuildEvents("run-1", 1, startSeq: 3)[0]);
        CloudAgentContracts.Save(reloaded!, state);
        await _database.InTransactionAsync(async (connection, transaction) =>
        {
            await _repository.SaveCloudAgentInTxAsync(connection, transaction, reloaded!, CancellationToken.None);
            return true;
        });
        Assert.Equal(3, (await _repository.CloudAgentAsync("user-a", "run-1"))!.EventCount);

        // 截断（EventCount 回退）：拒绝。
        CloudAgentExecution? truncated = await _repository.CloudAgentAsync("user-a", "run-1");
        truncated!.Journal = truncated.Journal[..2];
        truncated.EventCount = 2;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _database.InTransactionAsync(async (connection, transaction) =>
        {
            await _repository.SaveCloudAgentInTxAsync(connection, transaction, truncated, CancellationToken.None);
            return true;
        }));

        // 改写已有事件体：拒绝（append-only）。
        CloudAgentExecution? rewritten = await _repository.CloudAgentAsync("user-a", "run-1");
        rewritten!.Journal[0].EventJSON = rewritten.Journal[0].EventJSON.Replace("canvas_get_state", "hacked");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _database.InTransactionAsync(async (connection, transaction) =>
        {
            await _repository.SaveCloudAgentInTxAsync(connection, transaction, rewritten, CancellationToken.None);
            return true;
        }));
    }

    [Fact]
    public async Task 旧格式运行不受journal路径影响()
    {
        await MigrateAsync();
        CloudAgentExecution legacy = new()
        {
            ID = "run-old",
            UserID = "user-a",
            Status = "completed",
            CheckpointVersion = 0,
            StateJSON = JsonSerializer.Serialize(NewState("run-old"), GoJson.WriteOptions),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Journal = [],
            Transcript = [],
        };
        await _database.InTransactionAsync(async (connection, transaction) =>
        {
            await _repository.SaveCloudAgentInTxAsync(connection, transaction, legacy, CancellationToken.None);
            return true;
        });

        CloudAgentExecution? reloaded = await _repository.CloudAgentAsync("user-a", "run-old");
        Assert.NotNull(reloaded);
        Assert.Equal(0, reloaded.CheckpointVersion);
        CloudAgentRuntimeDto decoded = CloudAgentContracts.Decode(reloaded);
        Assert.Equal(2, decoded.Events.Count);
    }

    [Fact]
    public async Task 最近事件按运行窗口读取()
    {
        await MigrateAsync();
        foreach (string runID in new[] { "run-a", "run-b" })
        {
            CloudAgentExecution run = NewRun(runID);
            CloudAgentContracts.Save(run, NewState(runID));
            await _database.InTransactionAsync(async (connection, transaction) =>
            {
                await _repository.SaveCloudAgentInTxAsync(connection, transaction, run, CancellationToken.None);
                return true;
            });
        }

        List<CloudAgentEventRecord> recent = await _repository.RecentCloudAgentEventsForUserAsync("user-a", 10);
        Assert.Equal(4, recent.Count);

        // runLimit 截断以运行为界，不切开一个运行的事件。
        List<CloudAgentEventRecord> oneRun = await _repository.RecentCloudAgentEventsForUserAsync("user-a", 1);
        Assert.Equal(2, oneRun.Count);
    }

    /// <summary>
    /// 回归：互斥变更上下文里的 run 必须已载入 journal/transcript。
    /// 线上事故（2026-10-08）：MutateCloudAgentAsync 事务内只 Select 了执行行、漏调
    /// LoadCloudAgentJournalCoreAsync，回调里 current.Journal 为空但 EventCount 有值，
    /// SaveCloudAgentInTxAsync 的 append-only 校验误判为截断 → 整轮报
    /// 「Agent 运行状态损坏，本轮已停止: cloud Agent journal cannot be truncated」。
    /// 对应 Go: <c>MutateCloudAgent</c> 里 <c>New(tx).CloudAgent(userID, id)</c> 总是带出 journal。
    /// </summary>
    [Fact]
    public async Task 互斥变更上下文必须载入journal_否则无改动保存会误报截断()
    {
        await MigrateAsync();
        CloudAgentExecution seed = NewRun("run-mutate");
        CloudAgentContracts.Save(seed, NewState("run-mutate"));
        await _database.InTransactionAsync(async (connection, transaction) =>
        {
            await _repository.SaveCloudAgentInTxAsync(connection, transaction, seed, CancellationToken.None);
            return true;
        });

        // 事务内读到的 current 应已带 journal（长度 == EventCount）。
        bool hydrated = await _repository.MutateCloudAgentAsync(
            "user-a", "run-mutate", seed.Revision,
            (current, _) =>
            {
                Assert.NotNull(current.Journal);
                Assert.Equal(current.EventCount, current.Journal!.Count);
                return Task.FromResult(true);
            });
        Assert.True(hydrated);

        // 拿到锁后不改任何事件、原样保存：不得抛「journal cannot be truncated」。
        bool saved = await _repository.MutateCloudAgentAsync(
            "user-a", "run-mutate", seed.Revision + 1,
            async (current, context) =>
            {
                await context.SaveRunAsync(current, CancellationToken.None);
                return true;
            });
        Assert.True(saved);
    }

    /// <summary>
    /// 回归：创建期的 journal / transcript 必须随执行行一起落库。
    /// 线上事故（2026-10-08，画布 <c>wJN-9FSKWAhlU0suv0ITi</c>）：Go 里 Journal/Transcript 不是
    /// 瞬态字段，而是 <c>gorm:"foreignKey:RunID;references:ID"</c> 的 has-many 关联，
    /// <c>EnsureCloudAgent</c> 的 <c>Create(run)</c> 会把它们一并写入；.NET 侧漏了这一步。
    /// 带技能的运行在创建期就有一个 <c>skills_load</c> 回执事件，于是
    /// <c>eventCount=1</c> 而事件行 0 条，首个 checkpoint 里
    /// <c>Sequence &lt;= previousEvents</c> 把 seq=1 跳过 → seq=1 永久缺失 →
    /// 之后每个 checkpoint 都抛「cloud Agent journal cannot be truncated」，
    /// 运行被判损坏，取消/暂停永久 500。
    /// 对应 Go: <c>EnsureCloudAgent</c> + <c>cloudAgentSave</c>。
    /// </summary>
    [Fact]
    public async Task 创建期的journal必须随执行行落库()
    {
        await MigrateAsync();
        CloudAgentExecution run = NewRun("run-skills");
        CloudAgentRuntimeDto state = NewState("run-skills");
        state.Events = BuildEvents("run-skills", 1); // 创建期合成事件（skills_load 回执）
        CloudAgentContracts.Save(run, state);
        Assert.Equal(1, run.EventCount);

        await _repository.EnsureCloudAgentAsync(run);

        await using SqliteConnection connection = (SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"cloudAgentEventRecords\""));
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(
            "SELECT MIN(\"sequence\") FROM \"cloudAgentEventRecords\""));
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"cloudAgentMessageRecords\""));

        // 首次 checkpoint：事件能累积到 seq=2，且 eventCount 与实际行数始终一致。
        CloudAgentExecution? reloaded = await _repository.CloudAgentAsync("user-a", "run-skills");
        Assert.NotNull(reloaded);
        Assert.Equal(1, CloudAgentContracts.Decode(reloaded).Events.Count);

        bool saved = await _repository.MutateCloudAgentAsync("user-a", "run-skills", reloaded.Revision,
            async (current, context) =>
            {
                CloudAgentRuntimeDto currentState = CloudAgentContracts.Decode(current);
                currentState.Events.Add(BuildEvents("run-skills", 1, startSeq: 2)[0]);
                CloudAgentContracts.Save(current, currentState);
                await context.SaveRunAsync(current, CancellationToken.None);
            });
        Assert.True(saved);

        CloudAgentExecution? checkpointed = await _repository.CloudAgentAsync("user-a", "run-skills");
        Assert.NotNull(checkpointed);
        Assert.Equal(2, checkpointed.EventCount);
        Assert.Equal(2, checkpointed.Journal.Count);
        long[] sequences = checkpointed.Journal.Select(record => record.Sequence).ToArray();
        Assert.Equal(new long[] { 1, 2 }, sequences);
    }

    /// <summary>
    /// 回归：journal 与实际行数不一致时 <c>Decode</c> 必须报错。
    /// Go 在 <c>cloudAgentDecode</c> 里直接返回 <c>Agent execution journal is incomplete</c>，
    /// 不回落 StateJSON——checkpoint 已把三份大字段剥离成空数组，
    /// 「按旧格式继续」只会拿到空事件表并让后续事件序号从 1 重排，
    /// 把一次可诊断的不一致拖成 eventCount 与实际行数背离的持久损坏。
    /// </summary>
    [Fact]
    public async Task 执行日志不完整时解码必须报错()
    {
        await MigrateAsync();
        CloudAgentExecution run = NewRun("run-broken");
        CloudAgentContracts.Save(run, NewState("run-broken"));
        await _repository.EnsureCloudAgentAsync(run);

        // 人为复刻线上形态：eventCount=2 但只剩 seq=2 一行。
        await using SqliteConnection connection = (SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("DELETE FROM \"cloudAgentEventRecords\" WHERE \"sequence\" = 1");
        await connection.ExecuteAsync("UPDATE \"cloudAgentExecutions\" SET \"eventCount\" = 2");

        CloudAgentExecution? reloaded = await _repository.CloudAgentAsync("user-a", "run-broken");
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded.EventCount);
        Assert.Single(reloaded.Journal);
        Assert.Throws<InvalidOperationException>(() => CloudAgentContracts.Decode(reloaded));
    }

    /// <summary>
    /// 回归：无返回值回调重载必须能区分「拿到修订锁」与「修订冲突」。
    /// 旧实现把回调包成 <c>MutateCloudAgentAsync&lt;object?&gt;</c> 再判 <c>is not null</c>，
    /// 而成功（回调返回 null）与冲突（<c>default</c>）两条路径都返回 null ⇒ 恒为 false。
    /// 走本重载的调用点（取消、审批规划、读工具回写）于是必然抛
    /// 「创作运行已变化，请刷新后重试」——取消/暂停按钮因此完全失效。
    /// 对应 Go: <c>MutateCloudAgent</c> 用 <c>error</c> 区分 <c>nil</c> 与 <c>ErrCreationConflict</c>。
    /// </summary>
    [Fact]
    public async Task 无返回值重载必须区分成功与修订冲突()
    {
        await MigrateAsync();
        CloudAgentExecution run = NewRun("run-void");
        CloudAgentContracts.Save(run, NewState("run-void"));
        await _repository.EnsureCloudAgentAsync(run);

        bool success = await _repository.MutateCloudAgentAsync("user-a", "run-void", run.Revision,
            async (current, context) => await context.SaveRunAsync(current, CancellationToken.None));
        Assert.True(success);
        Assert.Equal(2, (await _repository.CloudAgentAsync("user-a", "run-void"))!.Revision);

        // 同一个（已过期的）修订号再来一次：必须报冲突，而不是又一次「成功」。
        bool conflict = await _repository.MutateCloudAgentAsync("user-a", "run-void", run.Revision,
            async (current, context) => await context.SaveRunAsync(current, CancellationToken.None));
        Assert.False(conflict);
        Assert.Equal(2, (await _repository.CloudAgentAsync("user-a", "run-void"))!.Revision);
    }

    /// <summary>
    /// 回归：创建期的 <c>skills_load</c> 回执必须带 <c>skillIds</c>。
    /// Go 的 skills_load 事件带 skillIds——用途是让使用率统计把一轮归因到它真正加载的技能，
    /// 而不是只累加总数（<c>cloud_agent_runtime.go:172-180</c>）。.NET 侧漏了这个键，
    /// 而 <c>CloudAgentSkillUsageService</c> 正是靠它累加 <c>RunsEnabled</c> ⇒ 该项恒为 0。
    /// </summary>
    [Fact]
    public async Task 创建期的skills_load回执必须带skillIds()
    {
        await MigrateAsync();
        TaskEntity task = new()
        {
            ID = "run-skill-ids",
            UserID = "user-a",
            ProjectID = "canvas-1",
            Type = "canvas_text",
            Operation = CloudAgentContracts.CloudAgentOperation,
            Status = TaskStatus.TaskStatusRunning,
            Prompt = "做个视频",
            InputJSON = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        // EnsureExecutionAsync 只用到 repository，其余依赖在该方法内不参与。
        CloudAgentSessionService sessions = new(_repository, null!, null!, null!, null!);
        await sessions.EnsureExecutionAsync(task, new CloudAgentStateDto
        {
            Version = 1,
            Request = new CloudAgentRequestDto { CanvasID = "canvas-1", Prompt = "做个视频" },
            Skills = [new CloudAgentSkillDto { ID = "14811816981772", Name = "一图成片" }],
        }, CancellationToken.None);

        CloudAgentExecution? run = await _repository.CloudAgentAsync("user-a", "run-skill-ids");
        Assert.NotNull(run);
        CloudAgentEventDto agentEvent = Assert.Single(CloudAgentContracts.Decode(run).Events);
        Assert.Equal("skills_load", agentEvent.Payload["toolName"].GetString());
        JsonElement skillIds = agentEvent.Payload["skillIds"];
        Assert.Equal(JsonValueKind.Array, skillIds.ValueKind);
        Assert.Equal("14811816981772", Assert.Single(skillIds.EnumerateArray()).GetString());
    }
}
