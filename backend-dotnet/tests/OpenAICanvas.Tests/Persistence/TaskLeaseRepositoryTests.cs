#nullable enable
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Tests.Persistence;

/// <summary>
/// 任务租约仓储验证：领取/续期/释放/让渡/进度/终态/完成落库的真实 SQLite 路径。
/// </summary>
public class TaskLeaseRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;

    public TaskLeaseRepositoryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-lease-{Guid.NewGuid():N}.db");
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

    private async Task<Repository> MigratedAsync()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();
        return _repository;
    }

    private static async Task<TaskEntity> SeedTaskAsync(Repository repository, string id)
    {
        await repository.CreateAsync(new User
        {
            ID = "USR_LEASE",
            Username = "worker",
            Email = "worker@example.com",
            DisplayName = "worker",
            Role = UserRole.UserRoleUser,
            Status = UserStatus.UserStatusActive,
            PasswordHash = "$2a$10$abcdefghijklmnopqrstuv",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        TaskEntity task = new()
        {
            ID = id,
            UserID = "USR_LEASE",
            Type = "canvas_image",
            Status = TaskStatus.TaskStatusQueued,
            Prompt = "画一张测试图",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await repository.CreateAsync(task);
        return task;
    }

    [Fact]
    public async Task 领取任务_写入租约并置运行态()
    {
        Repository repository = await MigratedAsync();
        await SeedTaskAsync(repository, "TSK_CLAIM1");

        TaskEntity? claimed = await repository.ClaimNextTaskAsync("worker-a", TimeSpan.FromSeconds(45));

        Assert.NotNull(claimed);
        Assert.Equal(TaskStatus.TaskStatusRunning, claimed!.Status);
        Assert.Equal("worker-a", claimed.LeaseOwner);
        Assert.Equal("后端接管任务", claimed.Stage);
        Assert.Equal(15, claimed.Progress);
        Assert.Equal(1, claimed.Attempts);
        Assert.NotNull(claimed.StartedAt);
        Assert.Null(claimed.NextPollAt);
    }

    [Fact]
    public async Task 已领取任务_租约未过期时不可被再次领取()
    {
        Repository repository = await MigratedAsync();
        await SeedTaskAsync(repository, "TSK_CLAIM2");
        await repository.ClaimNextTaskAsync("worker-a", TimeSpan.FromSeconds(45));

        TaskEntity? second = await repository.ClaimNextTaskAsync("worker-b", TimeSpan.FromSeconds(45));

        Assert.Null(second);
    }

    [Fact]
    public async Task 续期_所有者匹配则延长过期时间()
    {
        Repository repository = await MigratedAsync();
        await SeedTaskAsync(repository, "TSK_RENEW1");
        await repository.ClaimNextTaskAsync("worker-a", TimeSpan.FromSeconds(45));

        await repository.RenewTaskLeaseAsync("TSK_RENEW1", "worker-a", TimeSpan.FromSeconds(45));

        TaskEntity? task = await repository.TaskAsync("TSK_RENEW1");
        Assert.NotNull(task!.LeaseExpiresAt);
        Assert.True(task.LeaseExpiresAt > DateTime.UtcNow.AddSeconds(30));
    }


    [Fact]
    public async Task 续期_所有者不匹配则报租约失效()
    {
        Repository repository = await MigratedAsync();
        await SeedTaskAsync(repository, "TSK_RENEW2");
        await repository.ClaimNextTaskAsync("worker-a", TimeSpan.FromSeconds(45));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.RenewTaskLeaseAsync("TSK_RENEW2", "worker-b", TimeSpan.FromSeconds(45)));
    }
    [Fact]
    public async Task 释放_清空租约字段()
    {
        Repository repository = await MigratedAsync();
        await SeedTaskAsync(repository, "TSK_RELSE1");
        await repository.ClaimNextTaskAsync("worker-a", TimeSpan.FromSeconds(45));

        await repository.ReleaseTaskLeaseAsync("TSK_RELSE1", "worker-a");

        TaskEntity? task = await repository.TaskAsync("TSK_RELSE1");
        Assert.Equal("", task!.LeaseOwner);
        Assert.Null(task.LeaseExpiresAt);
    }

    [Fact]
    public async Task 让渡回轮询_写入下次轮询时间并清空租约()
    {
        Repository repository = await MigratedAsync();
        await SeedTaskAsync(repository, "TSK_DEFR1");
        await repository.ClaimNextTaskAsync("worker-a", TimeSpan.FromSeconds(45));

        await repository.DeferRunningTaskForProviderPollAsync(
            "TSK_DEFR1", "worker-a", "等待上游任务同步", TimeSpan.FromSeconds(15));

        TaskEntity? task = await repository.TaskAsync("TSK_DEFR1");
        Assert.Equal("等待上游任务同步", task!.Stage);
        Assert.NotNull(task.NextPollAt);
        Assert.Equal("", task.LeaseOwner);
    }

    [Fact]
    public async Task 让渡回轮询_租约所有者不匹配则报冲突()
    {
        Repository repository = await MigratedAsync();
        await SeedTaskAsync(repository, "TSK_DEFR2");
        await repository.ClaimNextTaskAsync("worker-a", TimeSpan.FromSeconds(45));

        await Assert.ThrowsAsync<TaskStateConflictException>(
            () => repository.DeferRunningTaskForProviderPollAsync(
                "TSK_DEFR2", "worker-b", "等待", TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task 持租约更新进度_所有者匹配时生效()
    {
        Repository repository = await MigratedAsync();
        await SeedTaskAsync(repository, "TSK_PROG1");
        await repository.ClaimNextTaskAsync("worker-a", TimeSpan.FromSeconds(45));

        await repository.UpdateTaskProgressForLeaseAsync("TSK_PROG1", "worker-a", "调用生成模型", 35);

        TaskEntity? task = await repository.TaskAsync("TSK_PROG1");
        Assert.Equal("调用生成模型", task!.Stage);
        Assert.Equal(35, task.Progress);
    }

    [Fact]
    public async Task 持租约更新进度_租约过期后拒绝写入()
    {
        Repository repository = await MigratedAsync();
        await SeedTaskAsync(repository, "TSK_PROG2");
        await repository.ClaimNextTaskAsync("worker-a", TimeSpan.FromSeconds(1));
        await Task.Delay(1100);

        await Assert.ThrowsAsync<TaskStateConflictException>(
            () => repository.UpdateTaskProgressForLeaseAsync("TSK_PROG2", "worker-a", "调用生成模型", 35));
    }

    [Fact]
    public async Task 写入终态_命中后清空租约并返回真()
    {
        Repository repository = await MigratedAsync();
        await SeedTaskAsync(repository, "TSK_TERM1");
        TaskEntity claimed = (await repository.ClaimNextTaskAsync("worker-a", TimeSpan.FromSeconds(45)))!;
        claimed.Status = TaskStatus.TaskStatusFailed;
        claimed.Stage = "执行失败";
        claimed.Error = "上游错误";
        claimed.CompletedAt = DateTime.UtcNow;

        bool hit = await repository.UpdateTaskTerminalStateAsync(
            claimed.ID,
            claimed.LeaseOwner,
            TaskStatus.TaskStatusRunning,
            claimed.Status,
            claimed.Stage,
            claimed.Error,
            claimed.CompletedAt!.Value);

        Assert.True(hit);
        TaskEntity? task = await repository.TaskAsync("TSK_TERM1");
        Assert.Equal(TaskStatus.TaskStatusFailed, task!.Status);
        Assert.Equal("", task.LeaseOwner);
    }

    [Fact]
    public async Task 保存完成_事务写入任务与结果行()
    {
        Repository repository = await MigratedAsync();
        await SeedTaskAsync(repository, "TSK_DONE1");
        TaskEntity claimed = (await repository.ClaimNextTaskAsync("worker-a", TimeSpan.FromSeconds(45)))!;
        TaskEntity completed = new()
        {
            ID = claimed.ID,
            UserID = claimed.UserID,
            Type = claimed.Type,
            Status = TaskStatus.TaskStatusSucceeded,
            Stage = "任务完成",
            Progress = 100,
            Prompt = claimed.Prompt,
            LeaseOwner = claimed.LeaseOwner,
            InputJSON = claimed.InputJSON,
            ResultJSON = "{\"ok\":true}",
            CompletedAt = DateTime.UtcNow,
        };

        await repository.SaveTaskCompletionAsync(completed, TaskStatus.TaskStatusRunning, new List<Result>
        {
            new()
            {
                ID = "RES_0001",
                UserID = claimed.UserID,
                TaskID = claimed.ID,
                Kind = "image",
                URL = "https://example.com/a.png",
                Payload = "{}",
                CreatedAt = DateTime.UtcNow,
            },
        });

        TaskEntity? task = await repository.TaskAsync("TSK_DONE1");
        Assert.Equal(TaskStatus.TaskStatusSucceeded, task!.Status);
        Assert.Equal(100, task.Progress);
        Assert.Equal("{\"ok\":true}", task.ResultJSON);
    }

    [Fact]
    public async Task 保存完成_状态已被并发修改则报冲突()
    {
        Repository repository = await MigratedAsync();
        await SeedTaskAsync(repository, "TSK_DONE2");
        TaskEntity claimed = (await repository.ClaimNextTaskAsync("worker-a", TimeSpan.FromSeconds(45)))!;
        claimed.Status = TaskStatus.TaskStatusSucceeded;

        await Assert.ThrowsAsync<TaskStateConflictException>(
            () => repository.SaveTaskCompletionAsync(claimed, "failed", []));
    }
}
