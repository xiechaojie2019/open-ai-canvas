#nullable enable
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Tests.Application;

/// <summary>验证后台异常兜底会把仍由原 worker 持有的 running 任务收尾为 failed。</summary>
public sealed class TaskTerminalServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-terminal-{Guid.NewGuid():N}.db");
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;

    public TaskTerminalServiceTests()
    {
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
        new SchemaMigrator(_database).MigrateAsync().GetAwaiter().GetResult();
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

    [Fact]
    public async System.Threading.Tasks.Task 后台异常_仍持有租约时写入失败终态并清空租约()
    {
        TaskEntity task = await SeedRunningTaskAsync("terminal-failed", "worker-a");
        TaskTerminalService terminal = new(_repository, new TaskService(_repository));

        bool handled = await terminal.EnsureFailedTerminalStateAsync(
            task,
            new InvalidOperationException("视频上游返回异常"));

        Assert.True(handled);
        TaskEntity stored = (await _repository.TaskAsync(task.ID))!;
        Assert.Equal(TaskStatus.TaskStatusFailed, stored.Status);
        Assert.Equal("任务失败", stored.Stage);
        Assert.Equal("连接模型服务失败，请检查渠道地址和网络", stored.Error);
        Assert.NotNull(stored.CompletedAt);
        Assert.Equal(string.Empty, stored.LeaseOwner);
        Assert.Null(stored.LeaseExpiresAt);
    }

    [Fact]
    public async System.Threading.Tasks.Task 后台异常_租约已被其他worker接管时不覆盖新状态()
    {
        TaskEntity task = await SeedRunningTaskAsync("terminal-taken-over", "worker-a");
        TaskEntity claimedSnapshot = new()
        {
            ID = task.ID,
            UserID = task.UserID,
            LeaseOwner = "worker-a",
            Status = TaskStatus.TaskStatusRunning,
        };
        await _repository.ReleaseTaskLeaseAsync(task.ID, "worker-a");
        TaskEntity replacement = (await _repository.ClaimNextTaskAsync("worker-b", TimeSpan.FromMinutes(1)))!;
        replacement.Stage = "新 worker 执行中";
        await _repository.UpdateTaskProgressForLeaseAsync(
            replacement.ID, "worker-b", replacement.Stage, replacement.Progress);

        TaskTerminalService terminal = new(_repository, new TaskService(_repository));
        bool handled = await terminal.EnsureFailedTerminalStateAsync(
            claimedSnapshot,
            new InvalidOperationException("旧 worker 异常"));

        Assert.False(handled);
        TaskEntity stored = (await _repository.TaskAsync(task.ID))!;
        Assert.Equal(TaskStatus.TaskStatusRunning, stored.Status);
        Assert.Equal("worker-b", stored.LeaseOwner);
        Assert.Equal("新 worker 执行中", stored.Stage);
    }

    private async System.Threading.Tasks.Task<TaskEntity> SeedRunningTaskAsync(string id, string owner)
    {
        await _repository.CreateAsync(new User
        {
            ID = "terminal-user",
            Username = "terminal-user",
            Email = "terminal-user@example.com",
            DisplayName = "terminal-user",
            Role = UserRole.UserRoleUser,
            Status = UserStatus.UserStatusActive,
            PasswordHash = "$2a$10$abcdefghijklmnopqrstuv",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _repository.CreateAsync(new TaskEntity
        {
            ID = id,
            UserID = "terminal-user",
            Type = "video_generation",
            Status = TaskStatus.TaskStatusQueued,
            Stage = "等待队列调度",
            Progress = 5,
            Prompt = "测试视频",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        return (await _repository.ClaimNextTaskAsync(owner, TimeSpan.FromMinutes(1)))!;
    }
}
