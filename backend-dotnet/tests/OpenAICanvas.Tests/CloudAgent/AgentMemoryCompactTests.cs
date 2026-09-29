#nullable enable
using OpenAICanvas.Application.CloudAgent;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// 个人记忆压缩（Go v18）：设置读写、压缩计划解析与应用、定时到期判定。
/// 对应 Go: <c>app/cloud_agent_memory_compact.go</c>。
/// </summary>
public sealed class AgentMemoryCompactTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly AgentMemoryCompactService _compact;
    private readonly AgentLessonService _lessons;

    public AgentMemoryCompactTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-memory-compact-{Guid.NewGuid():N}.db");
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
        _compact = new AgentMemoryCompactService(_repository);
        _lessons = new AgentLessonService(_repository);
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

    [Fact]
    public async Task 设置读写与默认值()
    {
        await MigrateAsync();
        AgentMemoryCompactView view = await _compact.GetAsync("user-a");
        Assert.Equal("off", view.CompactInterval);
        Assert.Equal("idle", view.LastStatus);

        AgentMemoryCompactView updated = await _compact.UpdateSettingAsync("user-a", new AgentMemorySettingRequest
        {
            CompactInterval = "weekly",
            ChannelID = "CHANNEL_1",
            ChannelModelKey = "MODEL_K",
        });
        Assert.Equal("weekly", updated.CompactInterval);
        Assert.Equal("CHANNEL_1", updated.ChannelID);
        Assert.Equal("MODEL_K", updated.ChannelModelKey);

        // 重复读取不重建行。
        Assert.Equal("weekly", (await _compact.GetAsync("user-a")).CompactInterval);

        await Assert.ThrowsAsync<AppError>(() => _compact.UpdateSettingAsync("user-a",
            new AgentMemorySettingRequest { CompactInterval = "hourly" }));
    }

    [Fact]
    public async Task 压缩计划解析与应用()
    {
        await MigrateAsync();
        CloudAgentLessons.AgentLessonView first = await _lessons.CreateUserAgentMemoryAsync("user-a",
            new AgentMemoryRequest { Topic = "t1", Category = "video", Situation = "重复一", Lesson = "做法一" });
        CloudAgentLessons.AgentLessonView second = await _lessons.CreateUserAgentMemoryAsync("user-a",
            new AgentMemoryRequest { Topic = "t2", Category = "video", Situation = "重复二", Lesson = "做法二" });
        CloudAgentLessons.AgentLessonView third = await _lessons.CreateUserAgentMemoryAsync("user-a",
            new AgentMemoryRequest { Topic = "t3", Category = "image", Situation = "太啰嗦的一次性描述：某张图的帽子", Lesson = "做法三" });

        string planText = """
            模型输出如下：

            ```json
            {"rewrites":[{"id":"ID3","topic":"t3","category":"image","situation":"图像细节模糊时","lesson":"做法三"}],
             "merges":[{"ids":["ID1","ID2"],"topic":"merged","category":"video","situation":"合并后的场景","lesson":"合并做法","source":"compact"}]}
            ```
            """;
        // 用真实 ID 替换占位。
        planText = planText.Replace("ID3", third.ID).Replace("ID1", first.ID).Replace("ID2", second.ID);

        AgentMemoryCompactSummary summary = await _compact.ApplyCompactTextAsync("user-a", planText);
        Assert.Equal(1, summary.Rewritten);
        Assert.Equal(1, summary.Merged);
        Assert.Equal(1, summary.Removed);
        Assert.Equal(0, summary.Skipped);

        System.Collections.Generic.List<CloudAgentLessons.AgentLessonView> remaining =
            await _lessons.UserAgentMemoriesAsync("user-a", "", 0);
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, view => view.Topic == "merged");
        Assert.Contains(remaining, view => view.Topic == "t3" && view.Situation == "图像细节模糊时");
    }

    [Fact]
    public async Task 计划引用未知或已用条目时跳过()
    {
        await MigrateAsync();
        string planText = """
            {"rewrites":[{"id":"ghost","topic":"x","category":"video","situation":"s","lesson":"l"}],
             "merges":[{"ids":["only-one"],"topic":"m","category":"video","situation":"s","lesson":"l"}]}
            """;
        AgentMemoryCompactSummary summary = await _compact.ApplyCompactTextAsync("user-a", planText);
        Assert.Equal(2, summary.Skipped);
        Assert.Equal(0, summary.Rewritten);
        Assert.Equal(0, summary.Merged);
    }

    [Fact]
    public async Task 空输出与非法JSON报错()
    {
        await MigrateAsync();
        await Assert.ThrowsAsync<AppError>(() => _compact.ApplyCompactTextAsync("user-a", "  "));
        await Assert.ThrowsAsync<AppError>(() => _compact.ApplyCompactTextAsync("user-a", "不是 JSON"));
        await Assert.ThrowsAsync<AppError>(() => _compact.ApplyCompactTextAsync("user-a", "{\"rewrites\": 非法}"));
    }

    [Fact]
    public async Task 定时调度只对到期设置启动()
    {
        await MigrateAsync();
        // 有记忆时调度才会真正启动（无记忆时 Go 静默跳过）。
        await _lessons.CreateUserAgentMemoryAsync("user-a",
            new AgentMemoryRequest { Topic = "t1", Category = "video", Situation = "s", Lesson = "l" });
        await _compact.UpdateSettingAsync("user-a", new AgentMemorySettingRequest { CompactInterval = "monthly" });
        await _compact.UpdateSettingAsync("user-b", new AgentMemorySettingRequest { CompactInterval = "off" });

        // off 不进调度表；monthly 无 LastCompactAt 到期，但没有可选模型 → 启动失败置 failed 并记录错误。
        await _compact.DispatchDueAsync();
        AgentMemoryCompactView view = await _compact.GetAsync("user-a");
        Assert.Equal("failed", view.LastStatus);
        Assert.Contains("文本模型", view.LastError, StringComparison.Ordinal);

        // user-b 保持 idle（不在调度表）。
        Assert.Equal("idle", (await _compact.GetAsync("user-b")).LastStatus);
    }
}
