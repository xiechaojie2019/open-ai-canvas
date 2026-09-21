#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Platform;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Application;

/// <summary>创建任务请求。对应 Go: <c>app.CreateTaskRequest</c>（trace/request 由中间件注入）。</summary>
public sealed class CreateTaskRequestDto
{
    [JsonPropertyName("projectId")]
    public string ProjectID { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("logicalModelId")]
    public string LogicalModelID { get; set; } = "";

    [JsonPropertyName("input")]
    public Dictionary<string, JsonElement>? Input { get; set; }
}

/// <summary>
/// 任务创建准入。对应 Go: <c>internal/app/task_creation.go</c>。
/// </summary>
/// <remarks>
/// 本批已移植：文本回放任务全路径（不排队、不计费）、类型校验、输入归一、
/// 活动限额与存储配额、密钥保护、输出脱敏。
/// 待移植（PENDING #58）：前台模型路由 / 系统渠道 admission / 计费预留的
/// 队列任务全准入——在该批落地前，非回放任务返回 Go 维护模式的 503 信封
/// （与 IsDraining 行为一致），不会产生未计费或未路由的半准入任务。
/// </remarks>
public sealed partial class TaskCreationService
{
    private readonly Repository _repository;
    private readonly IRuntimePolicyProvider _runtimePolicy;
    private readonly Platform.FeatureAvailabilityService? _features;
    private readonly string _dataDir;
    private WorkflowPluginGate? _workflowPlugins;

    /// <summary>工作流插件门控；惰性初始化，与 CanvasService 共用同一仓储单例。</summary>
    internal WorkflowPluginGate WorkflowPlugins => _workflowPlugins ??= new WorkflowPluginGate(_repository);

    public TaskCreationService(
        Repository repository,
        IRuntimePolicyProvider? runtimePolicy = null,
        string? dataDir = null,
        Platform.FeatureAvailabilityService? features = null)
    {
        _repository = repository;
        _runtimePolicy = runtimePolicy ?? new DefaultRuntimePolicyProvider();
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? "data" : dataDir!;
        _features = features;
        LogicalModels = new LogicalModelService(repository);
    }

    internal LogicalModelService LogicalModels { get; }

    /// <summary>创建任务。对应 Go: <c>CreateTask</c>。</summary>
    public async Task<TaskEntity> CreateAsync(
        string userId,
        CreateTaskRequestDto request,
        string traceId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        string prompt = request.Prompt.Trim();
        if (prompt.Length == 0)
        {
            // Go 用原始错误（英文文案经 fail(400, err) 原样输出）。
            throw new InvalidOperationException("prompt is required");
        }
        string taskType = request.Type.Trim();
        ValidateTaskType(taskType);
        Dictionary<string, JsonElement> input = NormalizeTaskInput(request.Input);

        // 前端自管的文本持久化任务：直连模型生成、增量上报 text-deltas，不排入 worker 队列。
        if (IsTextReplayTaskRequest(input))
        {
            return await CreateTextReplayAsync(userId, request, input, traceId, requestId, cancellationToken)
                .ConfigureAwait(false);
        }

        return await CreateQueuedAsync(
            userId, request, input, taskType, prompt, traceId, requestId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>文本回放任务。对应 Go: <c>createTextReplayTask</c>。</summary>
    public async Task<TaskEntity> CreateTextReplayAsync(
        string userId,
        CreateTaskRequestDto request,
        Dictionary<string, JsonElement> normalizedInput,
        string traceId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        string prompt = request.Prompt.Trim();
        if (prompt.Length == 0)
        {
            prompt = InputString(normalizedInput, "prompt").Trim();
        }
        if (prompt.Length == 0)
        {
            throw new InvalidOperationException("prompt is required");
        }
        string taskType = request.Type.Trim();
        ValidateTaskType(taskType);

        TaskEntity task = new()
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            TraceID = traceId,
            RequestID = requestId,
            ProjectID = request.ProjectID,
            Type = taskType,
            Status = TaskStatus.TaskStatusTextReplay,
            Stage = "文本持久化（前端自管）",
            Progress = 5,
            Prompt = prompt,
            Operation = request.Operation,
            Provider = request.Provider,
            Model = request.Model.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ProtectTaskSecrets(normalizedInput);
        task.InputJSON = SerializeInput(normalizedInput);
        await CreateWithinStorageQuotaAsync(task, billingOrder: null, cancellationToken).ConfigureAwait(false);
        return TaskForOutput(task);
    }

    // ------------------------------------------------------------ 时间线转写

    /// <summary>时间线字幕转写任务。对应 Go: <c>CreateTimelineTranscriptionTask</c>。</summary>
    public async Task<TaskEntity> CreateTimelineTranscriptionAsync(
        string userId,
        TimelineTranscriptionRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (_features is not null)
        {
            await _features.RequireFeatureAsync(
                OpenAICanvas.Platform.FeatureNames.TimelineTranscription, cancellationToken).ConfigureAwait(false);
        }
        string resourceID = request.ResourceID.Trim();
        if (resourceID.Length == 0)
        {
            throw AppError.BadAuthRequest("必须指定待转写媒体");
        }
        Resource? resource = await _repository
            .ResourceForUserAsync(userId, resourceID, cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.BadAuthRequest("无法读取待转写媒体，可能已被删除");
        }
        string mime = (resource.MimeType ?? "").ToLowerInvariant();
        if (!mime.StartsWith("video/", StringComparison.Ordinal)
            && !mime.StartsWith("audio/", StringComparison.Ordinal))
        {
            throw AppError.BadAuthRequest("仅支持音视频文件转写");
        }
        Dictionary<string, JsonElement> input = new(StringComparer.Ordinal)
        {
            ["resourceId"] = JsonSerializer.SerializeToElement(resourceID),
            ["language"] = JsonSerializer.SerializeToElement(request.Language.Trim()),
        };
        TaskEntity task = new()
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            ProjectID = request.ProjectID,
            Type = "timeline_transcription",
            Status = TaskStatus.TaskStatusQueued,
            Stage = "等待队列调度",
            Progress = 5,
            Prompt = "字幕转写",
            Provider = "local",
            Model = "whisper.cpp",
            InputJSON = JsonSerializer.Serialize(input),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await CreateWithinStorageQuotaAsync(task, billingOrder: null, cancellationToken).ConfigureAwait(false);
        await _repository.RecordUserActivityAsync(userId, "task", 1, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        return TaskForOutput(task);
    }

    // ------------------------------------------------------------ 准入共享件

    /// <summary>存储配额内的任务创建事务。对应 Go: <c>createTaskWithinStorageQuota</c>。</summary>
    private async Task CreateWithinStorageQuotaAsync(
        TaskEntity task, BillingOrder? billingOrder, CancellationToken cancellationToken)
    {
        Platform.RuntimePolicySetting policy = _runtimePolicy.Current();
        UserStorageUsage usage = await _repository
            .UserStorageUsageAsync(task.UserID, cancellationToken).ConfigureAwait(false);
        long incomingBytes =
            System.Text.Encoding.UTF8.GetByteCount(task.Prompt)
            + System.Text.Encoding.UTF8.GetByteCount(task.InputJSON)
            + System.Text.Encoding.UTF8.GetByteCount(task.Error);
        if (usage.TaskCount >= policy.Resource.TaskCount)
        {
            throw AppError.QuotaExceeded($"账号任务历史已达到 {policy.Resource.TaskCount} 条上限，请联系管理员归档");
        }
        if (usage.TaskBytes + incomingBytes > policy.Resource.TaskDataGB * 1024L * 1024L * 1024L)
        {
            throw AppError.QuotaExceeded($"账号任务历史数据已达到 {policy.Resource.TaskDataGB}GB 上限，请联系管理员归档");
        }
        try
        {
            if (billingOrder is not null)
            {
                await _repository.CreateTaskWithCreditReservationAsync(
                    task, billingOrder, policy.Task.ActiveTaskLimit, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _repository.CreateTaskWithActiveLimitAsync(
                    task, policy.Task.ActiveTaskLimit, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException error) when (error.Message == "active_task_limit")
        {
            throw AppError.BadAuthRequest(
                $"同时排队或运行的任务最多 {policy.Task.ActiveTaskLimit} 个，请等待已有任务完成");
        }
        catch (InvalidOperationException error) when (error.Message == "insufficient_credits")
        {
            throw AppError.BadAuthRequest("积分不足，请先使用兑换码充值");
        }
        catch (InvalidOperationException error) when (error.Message == "logical_model_unavailable")
        {
            throw AppError.BadAuthRequest("所选模型已停用、归档或配置已更新，请重新选择");
        }
    }

    /// <summary>任务类型白名单。对应 Go: <c>validateTaskType</c>。</summary>
    private static void ValidateTaskType(string taskType)
    {
        switch (taskType)
        {
            case "text":
            case "canvas_text":
            case "canvas_image":
            case "canvas_video":
            case "canvas_audio":
                return;
        }
        if (taskType.StartsWith("video_", StringComparison.Ordinal) && taskType["video_".Length..].Length > 0)
        {
            return;
        }
        if (taskType.Length == 0)
        {
            throw new InvalidOperationException("task type is required");
        }
        throw new InvalidOperationException($"不支持的任务类型：{taskType}");
    }

    /// <summary>输入归一为 JSON 对象（键序 Ordinal）。对应 Go: <c>normalizeTaskInput</c>。</summary>
    internal static Dictionary<string, JsonElement> NormalizeTaskInput(Dictionary<string, JsonElement>? input)
    {
        if (input is null)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
        Dictionary<string, JsonElement> normalized = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonElement> pair in input)
        {
            // Go 只对 canvasSnapshot 做 data: URL 压缩，其余保留原样。
            if (pair.Key == "canvasSnapshot" && pair.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                normalized[pair.Key] = CompactPersistedValue(pair.Value);
                continue;
            }
            normalized[pair.Key] = pair.Value.Clone();
        }
        return normalized;
    }

    /// <summary>canvasSnapshot 内嵌 data: URL 压缩为空串。对应 Go: <c>compactPersistedValue</c>。</summary>
    private static JsonElement CompactPersistedValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && (property.Value.GetString() ?? "").StartsWith("data:", StringComparison.Ordinal))
                {
                    result[property.Name] = JsonSerializer.SerializeToElement("");
                    continue;
                }
                result[property.Name] = property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? CompactPersistedValue(property.Value)
                    : property.Value.Clone();
            }
            return JsonSerializer.SerializeToElement(result);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            List<JsonElement> items = [];
            foreach (JsonElement item in value.EnumerateArray())
            {
                items.Add(item.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? CompactPersistedValue(item)
                    : item.Clone());
            }
            return JsonSerializer.SerializeToElement(items);
        }
        return value.Clone();
    }

    /// <summary>replay 标记识别。对应 Go: <c>isTextReplayTaskRequest</c>。</summary>
    internal static bool IsTextReplayTaskRequest(Dictionary<string, JsonElement> input) =>
        input.TryGetValue("replay", out JsonElement value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(
                (value.GetString() ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    /// <summary>递归加密密钥字段。对应 Go: <c>protectTaskSecrets</c>。</summary>
    internal void ProtectTaskSecretsPublic(Dictionary<string, JsonElement> input) => ProtectTaskSecrets(input);

    internal static bool IsTaskSecretFieldPublic(string key) => IsTaskSecretField(key);

    internal static Dictionary<string, JsonElement> ApplyRoutedProviderSelectionPublic(
        Dictionary<string, JsonElement> input, RoutedModel routed) =>
        ApplyRoutedProviderSelection(input, routed);

    internal static TaskEntity TaskForOutputPublic(TaskEntity task) => TaskForOutput(task);

    internal static bool TaskInputUsesCustomChannelPublic(Dictionary<string, JsonElement> input) =>
        TaskInputUsesCustomChannel(input);

    internal static ModelRequestIntent ModelRequestIntentFromTaskInputPublic(
        Dictionary<string, JsonElement> input, string taskType, string operation) =>
        ModelRequestIntentFromTaskInput(input, taskType, operation);

    internal async Task<BillingOrder?> TaskBillingOrderAsyncPublic(
        string userId, TaskEntity task, Dictionary<string, JsonElement> input, CancellationToken ct) =>
        await TaskBillingOrderAsync(userId, task, input, ct).ConfigureAwait(false);

    private void ProtectTaskSecrets(Dictionary<string, JsonElement> input)
    {
        foreach (string key in input.Keys.ToList())
        {
            JsonElement value = input[key];
            if (IsTaskSecretField(key))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    string secret = value.GetString() ?? "";
                    if (secret.Length > 0 && secret != "system"
                        && !secret.StartsWith(SettingsCrypto.EncryptedPrefix, StringComparison.Ordinal))
                    {
                        input[key] = JsonSerializer.SerializeToElement(
                            SettingsCrypto.EncryptSecret(secret, DataDir));
                    }
                }
                continue;
            }
            if (value.ValueKind == JsonValueKind.Object)
            {
                Dictionary<string, JsonElement> child = new(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    child[property.Name] = property.Value.Clone();
                }
                ProtectTaskSecrets(child);
                input[key] = JsonSerializer.SerializeToElement(child);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                input[key] = JsonSerializer.SerializeToElement(ProtectSecretsInArray(value));
            }
        }
    }

    private JsonElement ProtectSecretsInArray(JsonElement array)
    {
        List<JsonElement> items = [];
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                Dictionary<string, JsonElement> child = new(StringComparer.Ordinal);
                foreach (JsonProperty property in item.EnumerateObject())
                {
                    child[property.Name] = property.Value.Clone();
                }
                ProtectTaskSecrets(child);
                items.Add(JsonSerializer.SerializeToElement(child));
            }
            else if (item.ValueKind == JsonValueKind.Array)
            {
                items.Add(ProtectSecretsInArray(item));
            }
            else
            {
                items.Add(item.Clone());
            }
        }
        return JsonSerializer.SerializeToElement(items);
    }

    /// <summary>密钥字段名。对应 Go: <c>isTaskSecretField</c>。</summary>
    private static bool IsTaskSecretField(string key) => key is "apiKey" or "secretKey" or "accessKeySecret";

    internal string DataDir => _dataDir;

    /// <summary>序列化输入（Ordinal 键序 + Go 转义）。与画布载荷保持一致。</summary>
    internal static string SerializeInput(Dictionary<string, JsonElement> input) =>
        JsonSerializer.Serialize(
            ProjectCharacterService.SortedElement(JsonSerializer.SerializeToElement(input)),
            ProjectCharacterService.GoPayloadOptions);

    private static string InputString(Dictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                ? value.GetRawText()
                : "";

    /// <summary>对外输出脱敏。对应 Go: <c>taskForOutput</c>。</summary>
    internal static TaskEntity TaskForOutput(TaskEntity task)
    {
        task.InputJSON = PublicTaskInputJSON(task.InputJSON);
        // 普通任务接口只暴露前台模型身份；渠道模型和供应线路属于管理员内部信息。
        task.LogicalModelRevisionID = "";
        task.RouteID = "";
        task.ChannelModelID = "";
        return task;
    }

    /// <summary>仅保留非敏感投影字段。对应 Go: <c>publicTaskInputJSON</c>。</summary>
    private static string PublicTaskInputJSON(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }
        JsonElement input;
        try
        {
            input = JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch (JsonException)
        {
            return "";
        }
        if (input.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        Dictionary<string, JsonElement> publicInput = new(StringComparer.Ordinal);
        foreach (string key in new[]
                 {
                     "mode", "metadata", "workflowStepId", "domainProjectId", "assetVersionId",
                     "resourceId", "mediaType", "role",
                 })
        {
            if (input.TryGetProperty(key, out JsonElement value))
            {
                publicInput[key] = value.Clone();
            }
        }
        if (publicInput.Count == 0)
        {
            return "";
        }
        return JsonSerializer.Serialize(publicInput, ProjectCharacterService.GoPayloadOptions);
    }
}

/// <summary>时间线转写请求。对应 Go: <c>app.TimelineTranscriptionCreateRequest</c>。</summary>
public sealed class TimelineTranscriptionRequestDto
{
    [JsonPropertyName("resourceId")]
    public string ResourceID { get; set; } = "";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "";

    [JsonPropertyName("projectId")]
    public string ProjectID { get; set; } = "";
}
