#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Application;

/// <summary>任务列表查询条件。对应 Go: <c>service.TaskListOptions</c>。</summary>
public sealed class TaskListOptions
{
    public int Limit { get; init; } = 50;
    public string ProjectID { get; init; } = "";
    public bool ActiveOnly { get; init; }
}

/// <summary>
/// 文本回放结果。对应 Go: <c>app.TextReplayResult</c>。
/// 字段顺序与 Go 声明顺序一致。
/// </summary>
public sealed class TextReplayResultDto
{
    [JsonPropertyName("deltas")]
    public required IReadOnlyList<TaskTextDelta> Deltas { get; init; }

    [JsonPropertyName("textDraft")]
    [GoOmitEmpty]
    public string TextDraft { get; init; } = "";

    [JsonPropertyName("finalText")]
    [GoOmitEmpty]
    public string FinalText { get; init; } = "";

    [JsonPropertyName("complete")]
    public bool Complete { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("stage")]
    [GoOmitEmpty]
    public string Stage { get; init; } = "";

    [JsonPropertyName("progress")]
    public long Progress { get; init; }

    [JsonPropertyName("error")]
    [GoOmitEmpty]
    public string Error { get; init; } = "";
}

/// <summary>
/// 任务服务。对应 Go 的 <c>internal/app</c> 里任务读取与文本回放部分。
/// </summary>
/// <remarks>
/// 本类只覆盖<b>不依赖 provider 执行引擎</b>的读写路径：
/// 列表、详情、日志、文本增量、文本回放收尾、回放统计。
/// 任务创建、重试、取消、上游查询等需要执行引擎的部分另行实现。
/// </remarks>
public sealed class TaskService
{
    /// <summary>单条文本增量上限 64KB。对应 Go: <c>textReplayMaxEventBytes</c>。</summary>
    private const int TextReplayMaxEventBytes = 64 << 10;

    /// <summary>单任务文本上限 2MB。对应 Go: <c>textReplayMaxTaskBytes</c>。</summary>
    private const long TextReplayMaxTaskBytes = 2L << 20;

    /// <summary>单用户文本上限 64MB。对应 Go: <c>textReplayMaxUserBytes</c>。</summary>
    private const long TextReplayMaxUserBytes = 64L << 20;

    /// <summary>单任务增量条数上限。对应 Go: <c>textReplayMaxTaskEvents</c>。</summary>
    private const long TextReplayMaxTaskEvents = 4096;

    /// <summary>成功任务增量保留 24 小时。对应 Go: <c>textReplaySuccessRetention</c>。</summary>
    private static readonly TimeSpan TextReplaySuccessRetention = TimeSpan.FromHours(24);

    /// <summary>失败/取消任务草稿保留 7 天。对应 Go: <c>textReplayDraftRetention</c>。</summary>
    private static readonly TimeSpan TextReplayDraftRetention = TimeSpan.FromDays(7);

    /// <summary>任务日志正文上限。对应 Go: <c>taskLogPayloadLimit</c>。</summary>
    private const int TaskLogPayloadLimit = 4000;

    private readonly Repository _repository;

    public TaskService(Repository repository)
    {
        _repository = repository;
    }

    // ------------------------------------------------------------ 读取

    /// <summary>任务列表。对应 Go: <c>TasksWithOptions</c>。</summary>
    public async Task<List<TaskSummaryDto>> TasksWithOptionsAsync(
        string userId, TaskListOptions options, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TaskEntity> tasks = await _repository.TasksAsync(
            userId, options.Limit, options.ProjectID, options.ActiveOnly, cancellationToken).ConfigureAwait(false);

        // 只有挂了计费订单的任务才需要回查账单，避免无谓查询。
        List<string> billingIds = TaskOutput.BillingTaskIDs(tasks);
        Dictionary<string, BillingOrder> orders = await _repository
            .BillingOrdersByTaskIDsAsync(userId, billingIds, cancellationToken).ConfigureAwait(false);

        return TaskOutput.SummariesWithBilling(tasks, orders);
    }

    /// <summary>任务详情。对应 Go: <c>Task</c>（含上游请求 ID 补齐与输出过滤）。</summary>
    public async Task<TaskEntity> TaskAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        TaskEntity task = await _repository.TaskForUserAsync(userId, id, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("任务不存在");

        await HydrateProviderRequestIDAsync(task, cancellationToken).ConfigureAwait(false);
        return TaskOutput.ForOutput(task);
    }

    /// <summary>任务日志。对应 Go: <c>TaskLogs</c>。</summary>
    public Task<IReadOnlyList<TaskLog>> TaskLogsAsync(
        string userId, string id, CancellationToken cancellationToken = default) =>
        _repository.TaskLogsAsync(userId, id, cancellationToken);

    /// <summary>
    /// 补齐任务的上游请求 ID：先看计费订单，再回落到最近一条上游调用日志。
    /// 对应 Go: <c>hydrateTaskProviderRequestID</c>。
    /// </summary>
    public async Task HydrateProviderRequestIDAsync(
        TaskEntity task, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(task.ProviderRequestID))
        {
            return;
        }

        if (!string.IsNullOrEmpty(task.BillingOrderID))
        {
            BillingOrder? order = await _repository
                .BillingOrderAsync(task.BillingOrderID, cancellationToken).ConfigureAwait(false);
            if (order is not null)
            {
                task.ProviderRequestID = order.ProviderRequestID.Trim();
            }
        }

        if (string.IsNullOrEmpty(task.ProviderRequestID))
        {
            task.ProviderRequestID = await _repository
                .LatestProviderRequestIDForTaskAsync(task.ID, cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------ 文本回放

    /// <summary>
    /// 收尾前端自管的文本回放任务。对应 Go: <c>CompleteTextReplayTask</c>。
    /// </summary>
    public async Task<TaskEntity> CompleteTextReplayTaskAsync(
        string userId, string taskId, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw AppError.BadAuthRequest("文本内容不能为空");
        }

        if (Encoding.UTF8.GetByteCount(text) > TextReplayMaxTaskBytes)
        {
            throw AppError.BadAuthRequest("文本内容过大");
        }

        // 先确认任务归属；不存在直接 404。
        if (await _repository.TaskForUserAsync(userId, taskId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw AppError.NotFound("任务不存在");
        }

        string resultJson = JsonSerializer.Serialize(new { mode = "text", text });
        bool completed = await _repository.CompleteTextReplayTaskAsync(
            userId, taskId, resultJson, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);

        if (!completed)
        {
            throw AppError.BadAuthRequest("该文本任务已结束或不属于你，无法完成");
        }

        try
        {
            await FinalizeTaskTextReplayAsync(taskId, TaskStatus.TaskStatusSucceeded, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // 归并失败不影响任务收尾结果，只记日志（与 Go 一致）。
            await AppendLogAsync(userId, taskId, "error", "文本回放窗口更新失败", error.Message, cancellationToken)
                .ConfigureAwait(false);
        }

        return await _repository.TaskForUserAsync(userId, taskId, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("任务不存在");
    }

    /// <summary>追加文本增量。对应 Go: <c>AppendTaskTextDelta</c>。</summary>
    public async Task<TaskTextDelta> AppendTaskTextDeltaAsync(
        string userId, string taskId, string content, CancellationToken cancellationToken = default)
    {
        TaskEntity task = await _repository.TaskForUserAsync(userId, taskId, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("任务不存在");

        if (CapabilityFromTaskType(task.Type) != "text")
        {
            throw AppError.BadAuthRequest("只有文本生成任务支持增量回放");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw AppError.BadAuthRequest("文本增量不能为空");
        }

        if (Encoding.UTF8.GetByteCount(content) > TextReplayMaxEventBytes)
        {
            throw AppError.BadAuthRequest("单条文本增量不能超过 64KB");
        }

        try
        {
            return await _repository.AppendTaskTextDeltaAsync(
                userId,
                taskId,
                content,
                DateTime.UtcNow.Add(TextReplayDraftRetention),
                new TextReplayLimits
                {
                    MaxTaskBytes = TextReplayMaxTaskBytes,
                    MaxUserBytes = TextReplayMaxUserBytes,
                    MaxTaskEvents = TextReplayMaxTaskEvents,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (TextReplayException replay) when (replay.Failure == TextReplayFailure.QuotaExceeded)
        {
            throw AppError.BadAuthRequest("文本回放增量已达到配额，请等待任务归并后继续");
        }
        catch (TextReplayException replay) when (replay.Failure == TextReplayFailure.Closed)
        {
            throw AppError.BadAuthRequest("已结束任务不能继续写入文本增量");
        }
    }

    /// <summary>读取文本回放快照。对应 Go: <c>TaskTextReplay</c>。</summary>
    public async Task<TextReplayResultDto> TaskTextReplayAsync(
        string userId, string taskId, long after, CancellationToken cancellationToken = default)
    {
        TaskEntity task = await _repository.TaskForUserAsync(userId, taskId, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("任务不存在");

        IReadOnlyList<TaskTextDelta> deltas = await _repository
            .TaskTextDeltasAsync(userId, taskId, after, 1000, cancellationToken).ConfigureAwait(false);

        bool complete = task.Status is TaskStatus.TaskStatusSucceeded
            or TaskStatus.TaskStatusFailed
            or TaskStatus.TaskStatusCancelled;

        return new TextReplayResultDto
        {
            Deltas = deltas,
            TextDraft = task.TextDraft,
            FinalText = task.Status == TaskStatus.TaskStatusSucceeded ? TaskResultText(task.ResultJSON) : "",
            Complete = complete,
            Status = task.Status,
            Stage = task.Stage,
            Progress = task.Progress,
            Error = task.Error,
        };
    }

    /// <summary>文本回放增量统计（需管理员）。对应 Go: <c>AdminTextReplayStats</c>。</summary>
    public Task<TextReplayStats> AdminTextReplayStatsAsync(
        User? actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        return _repository.TextReplayStatsAsync(cancellationToken);
    }

    // ------------------------------------------------------------ 内部

    /// <summary>
    /// 归并文本增量并续期。对应 Go: <c>finalizeTaskTextReplay</c>。
    /// 失败/取消态保留草稿（7 天），其余按成功态 24 小时。
    /// </summary>
    internal async Task FinalizeTaskTextReplayAsync(
        string taskId, string status, CancellationToken cancellationToken)
    {
        bool keepDraft = status is TaskStatus.TaskStatusFailed or TaskStatus.TaskStatusCancelled;
        TimeSpan retention = keepDraft ? TextReplayDraftRetention : TextReplaySuccessRetention;

        try
        {
            await _repository.CompactTaskTextDeltasAsync(
                taskId, DateTime.UtcNow.Add(retention), keepDraft, cancellationToken).ConfigureAwait(false);
        }
        catch (AppError error) when (error.Status == 404)
        {
            // Go 把 ErrRecordNotFound 视为无需处理（任务已被删除）。
        }
    }

    /// <summary>写一条任务日志。对应 Go: <c>Service.log</c>。</summary>
    private async Task AppendLogAsync(
        string userId,
        string taskId,
        string level,
        string message,
        string payload,
        CancellationToken cancellationToken)
    {
        string traceId = "";
        string requestId = "";
        if (!string.IsNullOrEmpty(taskId))
        {
            TaskEntity? task = await _repository.TaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (task is not null)
            {
                traceId = task.TraceID;
                requestId = task.RequestID;
            }
        }

        await _repository.CreateAsync(new TaskLog
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            TaskID = taskId,
            TraceID = traceId,
            RequestID = requestId,
            Level = level,
            Message = message,
            Payload = TruncateTaskLogPayload(payload),
            CreatedAt = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>日志正文截断，保证不切坏 UTF-8 序列。对应 Go: <c>truncateTaskLogPayload</c>。</summary>
    private static string TruncateTaskLogPayload(string payload)
    {
        if (Encoding.UTF8.GetByteCount(payload) <= TaskLogPayloadLimit)
        {
            return payload;
        }

        // 按字节切再回退到合法的 UTF-8 边界，避免出现替换字符。
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        int end = TaskLogPayloadLimit;
        while (end > 0 && (bytes[end] & 0xC0) == 0x80)
        {
            end--;
        }

        string head = Encoding.UTF8.GetString(bytes, 0, end);
        int originalLength = payload.EnumerateRunes().Count();
        return head + $"\n...（日志内容已截断，原始长度 {originalLength} 字符）";
    }

    /// <summary>任务类型到能力。对应 Go: <c>capabilityFromTaskType</c>。</summary>
    private static string CapabilityFromTaskType(string taskType)
    {
        string value = taskType.ToLowerInvariant();
        foreach (string capability in new[] { "video", "image", "audio", "text" })
        {
            if (value.Contains(capability, StringComparison.Ordinal))
            {
                return capability;
            }
        }

        return value.Contains("storyboard", StringComparison.Ordinal)
            || value.Contains("agent", StringComparison.Ordinal)
            ? "text"
            : "";
    }

    /// <summary>从任务结果 JSON 取正文。对应 Go: <c>taskResultText</c>。</summary>
    private static string TaskResultText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            return document.RootElement.TryGetProperty("text", out JsonElement text)
                && text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
