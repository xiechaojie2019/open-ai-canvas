#nullable enable
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using OpenAICanvas.Domain.Serialization;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 任务媒体恢复（Go v34）：检查点编解码与租约栅栏写、恢复重排队条件、
/// 手动恢复校验链与次数/间隔限制。
/// 对应 Go: <c>repository/task_media_recovery.go</c> 与 <c>RecoverTaskMedia</c>。
/// </summary>
public sealed class TaskMediaRecoveryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly string _dataDir;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly TaskMediaRecoveryService _service;

    public TaskMediaRecoveryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-media-recovery-{Guid.NewGuid():N}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-media-recovery-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath};Pooling=False",
        });
        _repository = new Repository(_database);
        _service = new TaskMediaRecoveryService(_repository, _dataDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private async Task MigrateAsync()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();
    }

    private static MediaCheckpointDto Checkpoint(int manualAttempts = 0, int attempts = 0) => new()
    {
        Mode = "image",
        Items =
        [
            new MediaCheckpointItemDto
            {
                Reference = new MediaCheckpointReferenceDto { URL = "https://upstream/a.png", MimeType = "image/png" },
            },
        ],
        StartedAt = DateTime.UtcNow,
        Attempts = attempts,
        ManualAttempts = manualAttempts,
        LastManualAt = manualAttempts > 0 ? DateTime.UtcNow : default,
    };

    private async Task SeedFailedTaskAsync(
        string taskID = "task-1",
        string status = "failed",
        string checkpoint = "",
        string leaseOwner = "worker-1",
        DateTime? leaseExpires = null)
    {
        if (await _repository.UserAsync("user-a") is null)
        {
            await _repository.CreateAsync(new User
            {
                ID = "user-a",
                Username = "alice",
                Role = "user",
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        var task = new TaskEntity
        {
            ID = taskID,
            UserID = "user-a",
            ProjectID = "",
            Type = "canvas_image",
            Status = status,
            Stage = "作品已生成，正在保存",
            MediaRecoveryJSON = checkpoint,
            MediaStage = checkpoint.Length > 0 ? "upload" : "",
            LeaseOwner = leaseOwner,
            LeaseExpiresAt = leaseExpires,
            RouteRun = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await _repository.CreateAsync(task);
    }

    private static string Encrypt(TaskMediaRecoveryService service, MediaCheckpointDto checkpoint, string dataDir) =>
        SettingsCrypto.EncryptSecret(JsonSerializer.Serialize(checkpoint, GoJson.WriteOptions), dataDir);

    [Fact]
    public async Task 检查点保存走租约栅栏且任务字段同步()
    {
        await MigrateAsync();
        await SeedFailedTaskAsync(status: "running");
        // 归属不匹配的读回：直接取行验证 SQL 条件需要真实租约；这里覆盖冲突路径。
        TaskEntity task = (await _repository.TaskForUserAsync("user-a", "task-1"))!;
        task.LeaseOwner = "wrong-owner";

        await Assert.ThrowsAsync<TaskStateConflictException>(() =>
            _service.SaveCheckpointAsync(task, Checkpoint(), "download"));

        // 正确租约：写入成功并同步回实体。
        task.LeaseOwner = "worker-1";
        await _service.SaveCheckpointAsync(task, Checkpoint(), "download");
        Assert.NotEmpty(task.MediaRecoveryJSON);
        Assert.Equal("download", task.MediaStage);
    }

    [Fact]
    public async Task 解码校验_条目数量与模式()
    {
        await MigrateAsync();
        var empty = new MediaCheckpointDto { Mode = "image" };
        TaskEntity task = new() { MediaRecoveryJSON = Encrypt(_service, empty, _dataDir) };
        Assert.Throws<InvalidOperationException>(() => _service.DecodeCheckpoint(task));

        var badMode = new MediaCheckpointDto
        {
            Mode = "text",
            Items = [new MediaCheckpointItemDto()],
        };
        TaskEntity task2 = new() { MediaRecoveryJSON = Encrypt(_service, badMode, _dataDir) };
        Assert.Throws<InvalidOperationException>(() => _service.DecodeCheckpoint(task2));

        TaskEntity task3 = new() { MediaRecoveryJSON = Encrypt(_service, Checkpoint(), _dataDir) };
        Assert.Single(_service.DecodeCheckpoint(task3).Items);
    }

    [Fact]
    public async Task 手动恢复_校验链与重排队()
    {
        await MigrateAsync();
        string encrypted = Encrypt(_service, Checkpoint(), _dataDir);
        await SeedFailedTaskAsync(checkpoint: encrypted);

        // 无检查点。
        await SeedFailedTaskAsync(taskID: "task-none", checkpoint: "");
        AppError noneError = await Assert.ThrowsAsync<AppError>(
            () => _service.RecoverAsync("user-a", "task-none"));
        Assert.Contains("没有可恢复的作品", noneError.Message, StringComparison.Ordinal);

        // 成功任务直接返回。
        await SeedFailedTaskAsync(taskID: "task-ok", status: "succeeded", checkpoint: encrypted);
        TaskEntity ok = await _service.RecoverAsync("user-a", "task-ok");
        Assert.Equal("succeeded", ok.Status);

        // 正常恢复：任务回到排队，带刷新后的检查点。
        TaskEntity recovered = await _service.RecoverAsync("user-a", "task-1");
        Assert.Equal("queued", recovered.Status);
        Assert.Equal("等待恢复作品保存", recovered.Stage);
        Assert.Equal(string.Empty, recovered.Error);
        Assert.NotEqual(encrypted, recovered.MediaRecoveryJSON);
        MediaCheckpointDto decoded = _service.DecodeCheckpoint(recovered);
        Assert.Equal(1, decoded.ManualAttempts);
        Assert.Equal(4, decoded.Attempts); // 手动恢复只给一次尝试

        // 间隔限制：模拟恢复再次失败回到 failed 后，一分钟内重复拒绝。
        await using (SqliteConnection reset = (SqliteConnection)_database.CreateConnection())
        {
            await reset.OpenAsync();
            await reset.ExecuteAsync(
                "UPDATE tasks SET status = 'failed', stage = 'upload' WHERE id = 'task-1'");
        }
        AppError intervalError = await Assert.ThrowsAsync<AppError>(
            () => _service.RecoverAsync("user-a", "task-1"));
        Assert.Contains("至少一分钟", intervalError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 手动恢复_次数与退款拒绝()
    {
        await MigrateAsync();
        string exhausted = Encrypt(_service, Checkpoint(manualAttempts: 3), _dataDir);
        await SeedFailedTaskAsync(taskID: "task-max", checkpoint: exhausted);
        AppError maxError = await Assert.ThrowsAsync<AppError>(
            () => _service.RecoverAsync("user-a", "task-max"));
        Assert.Contains("重试保存上限", maxError.Message, StringComparison.Ordinal);

        // 已退款订单。
        await _repository.CreateAsync(new BillingOrder
        {
            ID = "order-rf",
            UserID = "user-a",
            TaskID = "task-rf",
            Capability = "image",
            Scene = "canvas_image",
            BillingMode = "fixed_request",
            AmountMicrocredits = 1_000,
            ReservedAmountMicrocredits = 1_000,
            Status = BillingStatus.BillingStatusRefunded,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        string checkpoint = Encrypt(_service, Checkpoint(), _dataDir);
        await SeedFailedTaskAsync(taskID: "task-rf", checkpoint: checkpoint);
        var refunded = (await _repository.TaskForUserAsync("user-a", "task-rf"))!;
        refunded.BillingOrderID = "order-rf";
        await _repository.SaveAsync(refunded);
        AppError refundError = await Assert.ThrowsAsync<AppError>(
            () => _service.RecoverAsync("user-a", "task-rf"));
        Assert.Contains("原任务已退款", refundError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 自动恢复失败_退避重试与预算耗尽()
    {
        await MigrateAsync();
        string encrypted = Encrypt(_service, Checkpoint(attempts: 3), _dataDir);
        await SeedFailedTaskAsync(status: "running", checkpoint: encrypted, leaseExpires: DateTime.UtcNow.AddMinutes(1));
        TaskEntity task = (await _repository.TaskForUserAsync("user-a", "task-1"))!;

        string? deferredStage = null;
        TimeSpan? deferredDelay = null;
        await _service.HandleFailureAsync(
            task, "upload", retryable: true,
            new InvalidOperationException("网络抖动"),
            (id, owner, stage, delay, _) =>
            {
                deferredStage = stage;
                deferredDelay = delay;
                return Task.CompletedTask;
            });
        Assert.Equal("作品已生成，保存遇到问题，正在自动恢复", deferredStage);
        Assert.NotNull(deferredDelay);

        // 预算耗尽：HandleFailure 会从库里重读检查点，所以第二次退避已把 attempts
        // 推到 4（= 退避表长度），第三次不再延期，仅保留检查点。
        deferredStage = null;
        TaskEntity task2 = (await _repository.TaskForUserAsync("user-a", "task-1"))!;
        await _service.HandleFailureAsync(
            task2, "upload", retryable: true,
            new InvalidOperationException("仍然失败"),
            (id, owner, stage, delay, _) =>
            {
                deferredStage = stage;
                deferredDelay = delay;
                return Task.CompletedTask;
            });
        MediaCheckpointDto saved = _service.DecodeCheckpoint(task2);
        Assert.Equal(TaskMediaRecoveryService.RecoveryDelays.Length, saved.Attempts);
    }

    [Fact]
    public async Task 物化委托注册后恢复保存可用()
    {
        await MigrateAsync();
        string encrypted = Encrypt(_service, Checkpoint(), _dataDir);
        await SeedFailedTaskAsync(checkpoint: encrypted);
        TaskEntity task = (await _repository.TaskForUserAsync("user-a", "task-1"))!;
        var service = new TaskMediaRecoveryService(_repository, _dataDir)
        {
            Materializer = (t, checkpoint, _) =>
                Task.FromResult<Dictionary<string, object?>>(new()
                {
                    ["mode"] = checkpoint.Mode,
                    ["images"] = Array.Empty<object?>(),
                }),
        };
        Dictionary<string, object?> result = await service.MaterializeAsync(task);
        Assert.Equal("image", (string)result["mode"]!);
    }
}
