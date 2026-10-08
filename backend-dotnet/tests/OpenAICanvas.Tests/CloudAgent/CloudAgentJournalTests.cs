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
}
