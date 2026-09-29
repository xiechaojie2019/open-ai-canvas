#nullable enable
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Application;

/// <summary>媒体恢复检查点。对应 Go: <c>app.mediaCheckpoint</c>（JSON 契约一致）。</summary>
public sealed class MediaCheckpointDto
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("items")]
    public List<MediaCheckpointItemDto> Items { get; set; } = [];

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; }

    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    [JsonPropertyName("manualAttempts")]
    public int ManualAttempts { get; set; }

    [JsonPropertyName("lastManualAt")]
    public DateTime LastManualAt { get; set; }
}

/// <summary>检查点条目。对应 Go: <c>app.mediaCheckpointItem</c>。</summary>
public sealed class MediaCheckpointItemDto
{
    [JsonPropertyName("reference")]
    public MediaCheckpointReferenceDto Reference { get; set; } = new();

    [JsonPropertyName("tempName")]
    [Domain.Serialization.GoOmitEmpty]
    public string TempName { get; set; } = "";

    [JsonPropertyName("mimeType")]
    [Domain.Serialization.GoOmitEmpty]
    public string MimeType { get; set; } = "";

    [JsonPropertyName("resourceId")]
    [Domain.Serialization.GoOmitEmpty]
    public string ResourceID { get; set; } = "";
}

/// <summary>上游媒体引用。对应 Go: <c>protocol.MediaReference</c> 的持久化子集。</summary>
public sealed class MediaCheckpointReferenceDto
{
    [JsonPropertyName("url")]
    [Domain.Serialization.GoOmitEmpty]
    public string URL { get; set; } = "";

    [JsonPropertyName("mimeType")]
    [Domain.Serialization.GoOmitEmpty]
    public string MimeType { get; set; } = "";

    [JsonPropertyName("dataUrl")]
    [Domain.Serialization.GoOmitEmpty]
    public string DataURL { get; set; } = "";
}

/// <summary>
/// 任务媒体恢复：作品生成成功但保存未完成时，把恢复信息持久化到任务行，
/// 支持延迟自动恢复与手动恢复；恢复复用同一任务与计费订单，绝不二次生成。
/// 对应 Go: <c>app/task_media_recovery.go</c>。
/// </summary>
/// <remarks>
/// 与 Go 的差异（如实记录）：结果物化（下载/暂存/上传 OSS/登记资源）依赖媒体落盘
/// 管线，.NET 侧该管线尚未对齐，通过 <see cref="Materializer"/> 委托接入；未注册时
/// 恢复重排队后的保存尝试会以"媒体保存管线未就绪"失败并保留检查点，与 Go 的
/// 失败保留语义一致，不会触发退款或第二次生成计费。
/// </remarks>
public sealed class TaskMediaRecoveryService
{
    /// <summary>自动重试退避序列。对应 Go: <c>mediaRecoveryDelays</c>。</summary>
    public static readonly TimeSpan[] RecoveryDelays =
    [
        TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10),
    ];

    private const int CheckpointMaxItems = 32;
    private const int ManualAttemptsMax = 3;
    private static readonly TimeSpan ManualInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(20);

    private readonly Repository _repository;
    private readonly string _dataDir;
    private readonly ILogger<TaskMediaRecoveryService>? _logger;

    /// <summary>
    /// 结果物化委托：把检查点条目变成资源并返回任务输出形状。由媒体落盘管线注册；
    /// 未注册时恢复后的保存尝试按失败保留检查点。
    /// </summary>
    public Func<TaskEntity, MediaCheckpointDto, CancellationToken,
        Task<Dictionary<string, object?>>>? Materializer { get; set; }

    /// <summary>优雅停机开关（恢复入口拒绝新请求）。</summary>
    public Func<bool>? IsDraining { get; set; }

    public TaskMediaRecoveryService(
        Repository repository, string dataDir, ILogger<TaskMediaRecoveryService>? logger = null)
    {
        _repository = repository;
        _dataDir = dataDir;
        _logger = logger;
    }

    // ------------------------------------------------------------- 检查点

    /// <summary>解码检查点：解密 + 反序列化 + 形状校验。对应 Go: <c>decodeMediaCheckpoint</c>。</summary>
    public MediaCheckpointDto DecodeCheckpoint(TaskEntity task)
    {
        string plain = SettingsCrypto.DecryptSecret(task.MediaRecoveryJSON, _dataDir);
        MediaCheckpointDto? checkpoint;
        try
        {
            checkpoint = JsonSerializer.Deserialize<MediaCheckpointDto>(plain, GoJson.ReadOptions);
        }
        catch (JsonException cause)
        {
            throw new InvalidOperationException(cause.Message, cause);
        }
        if (checkpoint is null
            || checkpoint.Items.Count == 0
            || checkpoint.Items.Count > CheckpointMaxItems
            || checkpoint.Mode is not ("image" or "video" or "audio"))
        {
            throw new InvalidOperationException("作品恢复信息无效");
        }
        return checkpoint;
    }

    /// <summary>编码并保存检查点。对应 Go: <c>saveMediaCheckpoint</c>。</summary>
    public async Task SaveCheckpointAsync(
        TaskEntity task, MediaCheckpointDto checkpoint, string stage,
        CancellationToken cancellationToken = default)
    {
        string encoded = SettingsCrypto.EncryptSecret(
            JsonSerializer.Serialize(checkpoint, GoJson.WriteOptions), _dataDir);
        await _repository.SaveTaskMediaCheckpointAsync(task, encoded, stage, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>结果物化入口（供媒体管线调用）。对应 Go: <c>RecoverTaskMedia</c> 的自动分支。</summary>
    public async Task<Dictionary<string, object?>> MaterializeAsync(
        TaskEntity task, CancellationToken cancellationToken = default)
    {
        MediaCheckpointDto checkpoint = DecodeCheckpoint(task);
        if (Materializer is null)
        {
            throw new InvalidOperationException("媒体保存管线未就绪");
        }
        return await Materializer(task, checkpoint, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- 手动恢复

    /// <summary>手动恢复：重排队同一任务，绝不第二次生成计费。对应 Go: <c>RecoverTaskMedia</c>。</summary>
    public async Task<TaskEntity> RecoverAsync(
        string userID, string id, CancellationToken cancellationToken = default)
    {
        if (IsDraining?.Invoke() == true)
        {
            throw AppError.BadAuthRequest("服务正在维护，请稍后重试保存");
        }
        TaskEntity? task = await _repository.TaskForUserAsync(userID, id.Trim(), cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.NotFound("任务不存在");
        if (task.MediaRecoveryJSON.Length == 0)
        {
            throw AppError.BadAuthRequest("该任务没有可恢复的作品，请先核对上游结果");
        }
        if (task.Status is TaskStatus.TaskStatusRunning
            or TaskStatus.TaskStatusQueued or TaskStatus.TaskStatusSucceeded)
        {
            return TaskOutput.ForOutput(task);
        }
        if (task.Status != TaskStatus.TaskStatusFailed)
        {
            throw AppError.BadAuthRequest("仅保存失败的作品可以恢复");
        }
        await RequireLiveProjectAsync(task, cancellationToken).ConfigureAwait(false);

        // 计费属于原始生成：复核中允许人工恢复，但绝不静默恢复已退款订单。
        if (task.BillingOrderID.Trim().Length > 0)
        {
            BillingOrder? order = await _repository.BillingOrderAsync(task.BillingOrderID, cancellationToken)
                .ConfigureAwait(false);
            if (order is null)
            {
                throw AppError.BadAuthRequest("任务与计费订单归属不一致");
            }
            if (order.UserID != task.UserID || order.TaskID != task.ID)
            {
                throw AppError.BadAuthRequest("任务与计费订单归属不一致");
            }
            if (order.Status == BillingStatus.BillingStatusRefunded)
            {
                throw AppError.BadAuthRequest("原任务已退款，请联系管理员处理作品，不会自动重新扣费");
            }
        }

        MediaCheckpointDto checkpoint = DecodeCheckpoint(task);
        if (checkpoint.ManualAttempts >= ManualAttemptsMax)
        {
            throw AppError.BadAuthRequest("已达到重试保存上限，请联系管理员核对渠道结果");
        }
        if (DateTime.UtcNow - checkpoint.LastManualAt < ManualInterval)
        {
            throw AppError.BadAuthRequest("请稍后再试，重试保存间隔至少一分钟");
        }
        checkpoint.ManualAttempts++;
        checkpoint.LastManualAt = DateTime.UtcNow;
        // 手动恢复只有这一次尝试，不再给完整的自动重试预算。
        checkpoint.Attempts = RecoveryDelays.Length;
        string encrypted = SettingsCrypto.EncryptSecret(
            JsonSerializer.Serialize(checkpoint, GoJson.WriteOptions), _dataDir);
        try
        {
            await _repository.RequeueTaskMediaRecoveryAsync(task, encrypted, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskStateConflictException)
        {
            TaskEntity? latest = await _repository.TaskForUserAsync(userID, task.ID, cancellationToken)
                .ConfigureAwait(false);
            if (latest is not null && latest.Status is TaskStatus.TaskStatusQueued
                or TaskStatus.TaskStatusRunning or TaskStatus.TaskStatusSucceeded)
            {
                return TaskOutput.ForOutput(latest);
            }
            throw;
        }
        TaskEntity next = await _repository.TaskForUserAsync(userID, task.ID, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.NotFound("任务不存在");
        return TaskOutput.ForOutput(next);
    }

    /// <summary>
    /// 自动恢复失败处理：可重试时按退避表延迟重排队，预算耗尽转结果持久化失败
    /// （不退款、不触发第二次生成）。对应 Go: <c>handleMediaRecoveryFailure</c>。
    /// </summary>
    public async Task HandleFailureAsync(
        TaskEntity task, string stage, bool retryable, Exception cause,
        Func<string, string, string, TimeSpan, CancellationToken, Task> deferRunning,
        CancellationToken cancellationToken = default)
    {
        TaskEntity? latest = await _repository.TaskAsync(task.ID, cancellationToken).ConfigureAwait(false);
        if (latest is null || latest.Status != TaskStatus.TaskStatusRunning
            || latest.LeaseOwner != task.LeaseOwner)
        {
            throw new TaskStateConflictException();
        }
        task.MediaRecoveryJSON = latest.MediaRecoveryJSON;
        task.MediaStage = latest.MediaStage;
        if (task.MediaRecoveryJSON.Length == 0)
        {
            return;
        }
        MediaCheckpointDto checkpoint = DecodeCheckpoint(task);
        if (ReferenceEquals(cause, RecoveryBusyError)
            && DateTime.UtcNow - checkpoint.StartedAt < StaleAfter)
        {
            await deferRunning(task.ID, task.LeaseOwner, "作品已生成，等待保存",
                TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return;
        }
        if (retryable && checkpoint.Attempts < RecoveryDelays.Length
            && DateTime.UtcNow - checkpoint.StartedAt < StaleAfter)
        {
            TimeSpan delay = RecoveryDelays[checkpoint.Attempts]
                + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 5_000));
            checkpoint.Attempts++;
            await SaveCheckpointAsync(task, checkpoint, stage, cancellationToken).ConfigureAwait(false);
            await deferRunning(task.ID, task.LeaseOwner, "作品已生成，保存遇到问题，正在自动恢复",
                delay, cancellationToken).ConfigureAwait(false);
            return;
        }
        await SaveCheckpointAsync(task, checkpoint, stage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>作品保存并发已满。对应 Go: <c>errMediaRecoveryBusy</c>。</summary>
    public static Exception RecoveryBusyError { get; } =
        new InvalidOperationException("作品保存并发已满");

    // ------------------------------------------------------------- 归属校验

    /// <summary>媒体任务的项目必须存活。对应 Go: <c>mediaTaskProject</c>。</summary>
    public async Task RequireLiveProjectAsync(
        TaskEntity task, CancellationToken cancellationToken = default)
    {
        string id = task.ProjectID;
        if (id.Length == 0)
        {
            return;
        }
        CanvasProject? canvas = await _repository.CanvasProjectForUserAsync(
            task.UserID, id, cancellationToken).ConfigureAwait(false);
        if (canvas is not null)
        {
            id = canvas.ProjectID;
            if (id.Length == 0)
            {
                return;
            }
        }
        Project? project = await _repository.ProjectForUserAsync(task.UserID, id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.NotFound("任务所属画布或项目已不存在");
        if (project.Status == ProjectStatus.ProjectStatusArchived)
        {
            throw AppError.BadAuthRequest("项目已归档，无法恢复作品");
        }
    }
}
