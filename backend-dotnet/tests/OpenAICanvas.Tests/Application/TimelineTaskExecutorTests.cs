#nullable enable
using System.Text.Json;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using OpenAICanvas.Platform;
using Xunit;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Tests.Application;

/// <summary>时间线 Worker 的本地执行分支、归属校验和明确配置错误。</summary>
[Collection("TimelineTaskEnvironment")]
public sealed class TimelineTaskExecutorTests : IDisposable
{
    private readonly string _databasePath;
    private readonly string _dataDir;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly IRuntimePolicyProvider _policy = new DefaultRuntimePolicyProvider();

    public TimelineTaskExecutorTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-timeline-{Guid.NewGuid():N}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-timeline-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
            DataDir = _dataDir,
        });
        _repository = new Repository(_database);
        new SchemaMigrator(_database).MigrateAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(_databasePath);
        TryDelete(_dataDir);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Worker转写未配置服务_任务进入失败终态并保留配置提示()
    {
        await SeedUserAsync("timeline-worker");
        TaskEntity task = new()
        {
            ID = "timeline-transcription-missing-config",
            UserID = "timeline-worker",
            Type = "timeline_transcription",
            Status = TaskStatus.TaskStatusQueued,
            Stage = "等待队列调度",
            Progress = 5,
            Prompt = "字幕转写",
            Provider = "local",
            Model = "whisper.cpp",
            InputJSON = "{\"resourceId\":\"missing\",\"language\":\"zh\"}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await _repository.CreateAsync(task);
        TaskEntity claimed = (await _repository.ClaimNextTaskAsync(
            "timeline-worker-owner", TimeSpan.FromMinutes(1)))!;

        string? previous = Environment.GetEnvironmentVariable("CANVAS_WHISPER_BASE_URL");
        Environment.SetEnvironmentVariable("CANVAS_WHISPER_BASE_URL", null);
        try
        {
            TimelineTaskExecutor executor = CreateExecutor();
            TaskWorkerService worker = new(_repository, _policy, timeline: executor);
            await worker.ProcessClaimedTaskAsync(claimed, null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CANVAS_WHISPER_BASE_URL", previous);
        }

        TaskEntity stored = (await _repository.TaskAsync(task.ID))!;
        Assert.Equal(TaskStatus.TaskStatusFailed, stored.Status);
        Assert.Equal("任务失败", stored.Stage);
        Assert.Contains("CANVAS_WHISPER_BASE_URL", stored.Error, StringComparison.Ordinal);
        Assert.NotNull(stored.CompletedAt);
        Assert.Equal("", stored.LeaseOwner);
    }

    [Fact]
    public async Task 渲染任务引用其他用户资源_在启动ffmpeg前拒绝归属越权()
    {
        await SeedUserAsync("timeline-owner");
        await SeedUserAsync("timeline-foreign");
        await _repository.CreateAsync(new Resource
        {
            ID = "foreign-video",
            UserID = "timeline-foreign",
            Kind = "video",
            Status = ResourceStatus.ResourceStatusReady,
            Provider = "local",
            ObjectKey = "users/timeline-foreign/video/foreign.mp4",
            MimeType = "video/mp4",
            Size = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        TaskEntity task = new()
        {
            ID = "timeline-render-foreign-resource",
            UserID = "timeline-owner",
            Type = "timeline_render",
            Status = TaskStatus.TaskStatusQueued,
            Stage = "等待队列调度",
            Progress = 5,
            Prompt = "时间线渲染",
            Provider = "local",
            Model = "ffmpeg",
            InputJSON = """
                {"projectId":"project-1","timeline":{"version":2,"tracks":[{"id":"video-track","kind":"video","visible":true}],"clips":[{"id":"clip-1","kind":"video","trackId":"video-track","startMs":0,"durationMs":1000,"directMedia":{"storageKey":"resource:foreign-video"}}]}}
                """,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await _repository.CreateAsync(task);
        TaskEntity claimed = (await _repository.ClaimNextTaskAsync(
            "timeline-render-owner", TimeSpan.FromMinutes(1)))!;

        TimelineTaskExecutor executor = CreateExecutor();
        TimelineTaskException error = await Assert.ThrowsAsync<TimelineTaskException>(
            () => executor.ExecuteAsync(claimed));

        Assert.Contains("无法读取时间线引用的媒体", error.Message, StringComparison.Ordinal);
        TaskEntity stored = (await _repository.TaskAsync(task.ID))!;
        Assert.Equal("准备媒体…", stored.Stage);
        Assert.Equal(TaskStatus.TaskStatusRunning, stored.Status);
    }

    private TimelineTaskExecutor CreateExecutor()
    {
        ResourceDomainService resources = new(_repository, _policy, _dataDir);
        UploadQuota quota = new(_repository, _policy);
        ResourceUploadService uploads = new(_repository, quota, _dataDir, _policy);
        FeatureAvailabilityService features = new(_repository);
        return new TimelineTaskExecutor(_repository, resources, uploads, features);
    }

    private async Task SeedUserAsync(string id)
    {
        await _repository.CreateAsync(new User
        {
            ID = id,
            Username = id,
            Email = id + "@example.com",
            DisplayName = id,
            Role = UserRole.UserRoleUser,
            Status = UserStatus.UserStatusActive,
            PasswordHash = "$2a$10$abcdefghijklmnopqrstuv",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // SQLite/file handles may release after the test returns.
        }
    }
}

[CollectionDefinition("TimelineTaskEnvironment", DisableParallelization = true)]
public sealed class TimelineTaskEnvironmentCollection : ICollectionFixture<object>
{
}
