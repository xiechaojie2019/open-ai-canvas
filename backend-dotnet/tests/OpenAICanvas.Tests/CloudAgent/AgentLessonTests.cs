#nullable enable
using Dapper;
using OpenAICanvas.Application.CloudAgent;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// Agent 个人记忆（Go v16/v17）：校验脱敏、CRUD、查重合并、导入导出与索引块注入。
/// 对应 Go: <c>app/cloud_agent_lessons.go</c> 与 <c>repository/agent_lesson.go</c>。
/// </summary>
public sealed class AgentLessonTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly AgentLessonService _service;

    public AgentLessonTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-agent-lesson-{Guid.NewGuid():N}.db");
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
        _service = new AgentLessonService(_repository);
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

    private static AgentMemoryRequest ValidRequest(string topic = "video.duration") => new()
    {
        Topic = topic,
        Category = "video",
        Situation = "需要指定视频时长时",
        Lesson = "先用 model_list 核对时长档位",
    };

    [Fact]
    public async Task 创建_列表_裁决_删除全链路()
    {
        await MigrateAsync();
        CloudAgentLessons.AgentLessonView created = await _service.CreateUserAgentMemoryAsync(
            "user-a", ValidRequest());
        Assert.Equal("approved", created.Status);
        Assert.Equal("video", created.Category);

        List<CloudAgentLessons.AgentLessonView> list = await _service.UserAgentMemoriesAsync("user-a", "", 0);
        Assert.Single(list);

        await _service.DecideUserAgentMemoryAsync("user-a", created.ID, "reject");
        List<CloudAgentLessons.AgentLessonView> rejected = await _service.UserAgentMemoriesAsync("user-a", "rejected", 0);
        Assert.Single(rejected);

        await _service.DeleteUserAgentMemoryAsync("user-a", created.ID);
        Assert.Empty(await _service.UserAgentMemoriesAsync("user-a", "", 0));
    }

    [Fact]
    public async Task 重复创建按指纹合并并提升状态()
    {
        await MigrateAsync();
        CloudAgentLessons.AgentLessonView first = await _service.CreateUserAgentMemoryAsync("user-a", ValidRequest());

        // 同内容、不同 topic 写法：指纹一致 → 返回已有条目。
        AgentMemoryRequest duplicate = ValidRequest("video duration");
        CloudAgentLessons.AgentLessonView merged = await _service.CreateUserAgentMemoryAsync("user-a", duplicate);
        Assert.Equal(first.ID, merged.ID);
        Assert.Equal(1, await CountAsync());

        // 导入同样合并。
        AgentMemoryImportResult imported = await _service.ImportUserAgentMemoriesAsync("user-a", new AgentMemoryBundle
        {
            Version = 1,
            Kind = "agent-memories",
            ExportedAt = DateTime.UtcNow,
            Memories = [new AgentMemoryExportItem
            {
                Topic = "video.duration",
                Category = "video",
                Situation = "需要指定视频时长时",
                Lesson = "先用 model_list 核对时长档位",
            }],
        });
        Assert.Equal(1, imported.Merged);
        Assert.Equal(0, imported.Imported);
    }

    [Fact]
    public async Task 导入忽略非法条目并计数()
    {
        await MigrateAsync();
        AgentMemoryImportResult result = await _service.ImportUserAgentMemoriesAsync("user-a", new AgentMemoryBundle
        {
            Version = 1,
            Kind = "agent-memories",
            ExportedAt = DateTime.UtcNow,
            Memories =
            [
                new AgentMemoryExportItem
                {
                    Topic = "t1",
                    Category = "video",
                    Situation = "正常条目",
                    Lesson = "做法",
                },
                new AgentMemoryExportItem
                {
                    Topic = "t2",
                    Category = "不存在的分类",
                    Situation = "非法条目",
                    Lesson = "做法",
                },
            ],
        });
        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task 裁决与删除未命中报不存在()
    {
        await MigrateAsync();
        await Assert.ThrowsAsync<AppError>(
            () => _service.DecideUserAgentMemoryAsync("user-a", "missing", "approve"));
        await Assert.ThrowsAsync<AppError>(
            () => _service.DeleteUserAgentMemoryAsync("user-a", "missing"));
        await Assert.ThrowsAsync<AppError>(
            () => _service.DecideUserAgentMemoryAsync("user-a", "missing", "unknown"));
    }

    [Fact]
    public async Task 管理端列表带作者信息并可删除()
    {
        await MigrateAsync();
        await _repository.CreateAsync(new User
        {
            ID = "user-a",
            Username = "alice",
            DisplayName = "爱丽丝",
            Role = "admin",
            Status = "active",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _service.CreateUserAgentMemoryAsync("user-a", ValidRequest());

        List<CloudAgentLessons.AgentLessonAdminView> lessons = await _service.AdminAgentLessonsAsync("", "", "", 0);
        Assert.Single(lessons);
        Assert.Equal("user-a", lessons[0].AuthorUserID);
        Assert.Equal("alice", lessons[0].AuthorUsername);

        await _service.AdminDeleteAgentLessonAsync(lessons[0].ID);
        Assert.Empty(await _service.AdminAgentLessonsAsync("", "", "", 0));
    }

    [Fact]
    public void 构建校验_分类_脱敏_字数()
    {
        Assert.Throws<AppError>(() => CloudAgentLessons.BuildMemory(
            "user-a", new AgentMemoryRequest { Topic = "t", Category = "nope", Situation = "s", Lesson = "l" }, "approved"));
        Assert.Throws<AppError>(() => CloudAgentLessons.BuildMemory(
            "user-a",
            new AgentMemoryRequest { Topic = "t", Category = "video", Situation = "看 https://example.com", Lesson = "l" },
            "approved"));
        Assert.Throws<AppError>(() => CloudAgentLessons.BuildMemory(
            "user-a", new AgentMemoryRequest { Topic = new string('长', 121), Category = "video", Situation = "s", Lesson = "l" },
            "approved"));
        Assert.Throws<AppError>(() => CloudAgentLessons.BuildMemory(
            "user-a", new AgentMemoryRequest { Topic = "t", Category = "video", Situation = "s" }, "approved"));
        // 步数上限。
        Assert.Throws<AppError>(() => CloudAgentLessons.BuildMemory(
            "user-a",
            new AgentMemoryRequest
            {
                Topic = "t",
                Category = "video",
                Situation = "s",
                Steps = Enumerable.Range(0, 13)
                    .Select(i => new AgentLessonStepDto { Tool = $"tool{i}", Action = "a" })
                    .ToArray(),
            },
            "approved"));

        AgentLesson entry = CloudAgentLessons.BuildMemory(
            "user-a",
            new AgentMemoryRequest
            {
                Topic = "t",
                Category = "video",
                Situation = "s",
                Steps = [new AgentLessonStepDto { Tool = "canvas_get_state", Action = "读取画布" }],
            },
            "pending");
        Assert.Equal("video", entry.Category);
        Assert.Equal("pending", entry.Status);
        Assert.Contains("canvas_get_state", entry.StepsJSON, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 索引块注入与剥离()
    {
        await MigrateAsync();
        await _service.CreateUserAgentMemoryAsync("user-a", ValidRequest());

        CloudAgentCanonicalRequestDto canonical = new() { SystemPrompt = "system" };
        await CloudAgentLessons.AttachAsync(canonical, _repository, "user-a", "帮我处理视频时长", CancellationToken.None);
        Assert.StartsWith("system", canonical.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains(CloudAgentLessons.BlockMarker, canonical.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("video.duration", canonical.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("库里共 1 条已批准记忆", canonical.SystemPrompt, StringComparison.Ordinal);

        Assert.Equal("system", CloudAgentLessons.Strip(canonical.SystemPrompt));

        // 重复注入不叠加。
        await CloudAgentLessons.AttachAsync(canonical, _repository, "user-a", "又一次", CancellationToken.None);
        Assert.Equal(1, canonical.SystemPrompt.Split(CloudAgentLessons.BlockMarker).Length - 1);

        // 空用户不注入。
        CloudAgentCanonicalRequestDto empty = new() { SystemPrompt = "base" };
        await CloudAgentLessons.AttachAsync(empty, _repository, "  ", "任务", CancellationToken.None);
        Assert.Equal("base", empty.SystemPrompt);
    }

    private async Task<long> CountAsync() =>
        await _repository.CountAgentLessonsByAuthorAsync("user-a", "", CancellationToken.None);
}
