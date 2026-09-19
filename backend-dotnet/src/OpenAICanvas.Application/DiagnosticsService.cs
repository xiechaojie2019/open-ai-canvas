#nullable enable
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Domain.Serialization;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application;

/// <summary>诊断运行环境。对应 Go: <c>app.DiagnosticRuntime</c>。</summary>
public sealed class DiagnosticRuntimeDto
{
    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = "";

    [JsonPropertyName("buildCommit")]
    public string BuildCommit { get; set; } = "";

    [JsonPropertyName("browser")]
    public string Browser { get; set; } = "";

    [JsonPropertyName("os")]
    public string OS { get; set; } = "";

    [JsonPropertyName("timezone")]
    public string Timezone { get; set; } = "";
}

/// <summary>诊断客户端事件。对应 Go: <c>app.DiagnosticClientEvent</c>。</summary>
public sealed class DiagnosticClientEventDto
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("level")]
    public string Level { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("code")]
    [GoOmitEmpty]
    public string Code { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("route")]
    [GoOmitEmpty]
    public string Route { get; set; } = "";

    [JsonPropertyName("durationMs")]
    [GoOmitEmpty]
    public long DurationMs { get; set; }

    [JsonPropertyName("httpStatus")]
    [GoOmitEmpty]
    public int HTTPStatus { get; set; }

    [JsonPropertyName("requestId")]
    [GoOmitEmpty]
    public string RequestID { get; set; } = "";

    [JsonPropertyName("traceId")]
    [GoOmitEmpty]
    public string TraceID { get; set; } = "";

    [JsonPropertyName("taskId")]
    [GoOmitEmpty]
    public string TaskID { get; set; } = "";

    [JsonPropertyName("projectId")]
    [GoOmitEmpty]
    public string ProjectID { get; set; } = "";

    [JsonPropertyName("canvasId")]
    [GoOmitEmpty]
    public string CanvasID { get; set; } = "";

    [JsonPropertyName("stack")]
    [GoOmitEmpty]
    public string Stack { get; set; } = "";
}

/// <summary>诊断导出请求。对应 Go: <c>app.DiagnosticExportRequest</c>。</summary>
public sealed class DiagnosticExportRequestDto
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    [JsonPropertyName("taskId")]
    [GoOmitEmpty]
    public string TaskID { get; set; } = "";

    [JsonPropertyName("projectId")]
    [GoOmitEmpty]
    public string ProjectID { get; set; } = "";

    [JsonPropertyName("description")]
    [GoOmitEmpty]
    public string Description { get; set; } = "";

    [JsonPropertyName("runtime")]
    public DiagnosticRuntimeDto Runtime { get; set; } = new();

    [JsonPropertyName("clientEvents")]
    public List<DiagnosticClientEventDto>? ClientEvents { get; set; }
}

/// <summary>诊断预览。对应 Go: <c>app.DiagnosticPreview</c>。</summary>
public sealed class DiagnosticPreviewDto
{
    [JsonPropertyName("clientEventLimit")]
    public int ClientEventLimit { get; set; }

    [JsonPropertyName("taskCount")]
    public int TaskCount { get; set; }

    [JsonPropertyName("taskLogCount")]
    public int TaskLogCount { get; set; }

    [JsonPropertyName("apiCallCount")]
    public int APICallCount { get; set; }

    [JsonPropertyName("estimatedBytes")]
    public long EstimatedBytes { get; set; }

    [JsonPropertyName("willTruncate")]
    public bool WillTruncate { get; set; }
}

/// <summary>诊断包（导出结果）。对应 Go: <c>app.DiagnosticBundle</c>。</summary>
public sealed class DiagnosticBundleDto
{
    [JsonPropertyName("bundleId")]
    public string BundleID { get; set; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("data")]
    public byte[] Data { get; set; } = [];
}

/// <summary>
/// 用户诊断包。对应 Go: <c>internal/app/diagnostics.go</c>。
/// </summary>
public sealed class DiagnosticsService
{
    private const int DiagnosticSchemaVersion = 1;
    private const int DiagnosticRedactionVersion = 1;
    private const int DiagnosticMaxClientEvents = 500;
    private const long DiagnosticMaxBundleBytes = 10L << 20;
    private const int DiagnosticMaxDescription = 1000;
    private const int DiagnosticMaxEventText = 4000;
    private const int DiagnosticTaskLimit = 100;
    private const int DiagnosticTaskLogLimit = 2000;
    private const int DiagnosticAPICallLimit = 1000;

    private static readonly string[] RedactMarkers =
    {
        "authorization", "cookie", "set-cookie", "x-goog-api-key", "x-canvas-upstream-headers",
        "api_key", "api-key", "apikey", "access_key", "access-key", "secret_key", "secret-key",
        "password", "token=",
    };

    private readonly Repository _repository;
    private readonly string _dataDir;

    public DiagnosticsService(Repository repository, string? dataDir = null)
    {
        _repository = repository;
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? "data" : dataDir!;
    }

    /// <summary>诊断预览。对应 Go: <c>PreviewDiagnosticBundle</c>。</summary>
    public async Task<DiagnosticPreviewDto> PreviewAsync(
        string userId, DiagnosticExportRequestDto request, CancellationToken cancellationToken = default)
    {
        DiagnosticCollection collection = await CollectAsync(userId, request, cancellationToken).ConfigureAwait(false);
        return new DiagnosticPreviewDto
        {
            ClientEventLimit = DiagnosticMaxClientEvents,
            TaskCount = collection.Tasks.Count,
            TaskLogCount = collection.TaskLogs.Count,
            APICallCount = collection.APICalls.Count,
            EstimatedBytes = 2048
                + collection.ClientEvents.Count * 420
                + collection.Tasks.Count * 620
                + collection.TaskLogs.Count * 700
                + collection.APICalls.Count * 520,
            WillTruncate = collection.Truncated,
        };
    }

    /// <summary>导出诊断 ZIP。对应 Go: <c>ExportDiagnosticBundle</c>。</summary>
    public async Task<DiagnosticBundleDto> ExportAsync(
        string userId, DiagnosticExportRequestDto request, CancellationToken cancellationToken = default)
    {
        DiagnosticCollection collection = await CollectAsync(userId, request, cancellationToken).ConfigureAwait(false);
        string bundleID = "DIAG_" + IdGenerator.NewId()[..8].ToUpperInvariant();
        // 对应 Go: appearanceIdentity —— 无配置或空品牌名时回落默认（影策 / open-ai-canvas）。
        SystemSetting? appearance = await _repository
            .SystemSettingAsync("appearance", cancellationToken).ConfigureAwait(false);
        string brandName = "影策";
        if (appearance is not null)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(appearance.ValueJSON);
                if (doc.RootElement.TryGetProperty("brandName", out JsonElement brand)
                    && brand.ValueKind == JsonValueKind.String
                    && brand.GetString()!.Trim().Length > 0)
                {
                    brandName = brand.GetString()!.Trim();
                }
            }
            catch (JsonException)
            {
                // 外观配置损坏时回落默认品牌名。
            }
        }
        byte[] data = BuildDiagnosticZIP(brandName, bundleID, collection);
        if (data.Length > DiagnosticMaxBundleBytes)
        {
            throw AppError.BadAuthRequest("诊断包超过 10 MB，请缩短时间范围后重试");
        }
        string fileName = $"yingce-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{bundleID}.zip";
        return new DiagnosticBundleDto { BundleID = bundleID, FileName = fileName, Data = data };
    }

    // ------------------------------------------------------------ 采集

    internal async Task<DiagnosticCollection> CollectAsync(
        string userId, DiagnosticExportRequestDto request, CancellationToken cancellationToken)
    {
        (DateTime from, DateTime to) = NormalizeWindow(request.From, request.To);
        string taskID = request.TaskID.Trim();
        string projectID = request.ProjectID.Trim();
        if (taskID.Length > 96 || projectID.Length > 96)
        {
            throw AppError.BadAuthRequest("诊断上下文无效");
        }
        if (taskID.Length > 0)
        {
            TaskEntity? task = await _repository
                .TaskForUserAsync(userId, taskID, cancellationToken).ConfigureAwait(false);
            if (task is null)
            {
                throw AppError.BadAuthRequest("任务不存在或无权访问");
            }
            if (projectID.Length > 0 && task.ProjectID != projectID)
            {
                throw AppError.BadAuthRequest("任务不属于当前项目");
            }
            if (projectID.Length == 0)
            {
                projectID = task.ProjectID;
            }
        }

        List<TaskEntity> tasks = [.. await _repository
            .DiagnosticTasksAsync(userId, from, to, taskID, projectID, cancellationToken).ConfigureAwait(false)];
        List<TaskLog> taskLogs = [.. await _repository
            .DiagnosticTaskLogsAsync(userId, from, to, taskID, projectID, cancellationToken).ConfigureAwait(false)];
        List<ApiCallLog> apiCalls = [.. await _repository
            .DiagnosticAPICallLogsAsync(userId, from, to, taskID, projectID, cancellationToken).ConfigureAwait(false)];

        List<DiagnosticClientEventDto> clientEvents = request.ClientEvents ?? [];
        bool truncated = false;
        if (clientEvents.Count > DiagnosticMaxClientEvents)
        {
            clientEvents = clientEvents.GetRange(clientEvents.Count - DiagnosticMaxClientEvents, DiagnosticMaxClientEvents);
            truncated = true;
        }

        DiagnosticCollection collection = new()
        {
            From = from,
            To = to,
            Description = RedactText(request.Description, DiagnosticMaxDescription),
            TaskID = taskID,
            ProjectID = projectID,
            Truncated = truncated,
            Runtime = new DiagnosticRuntimeRecord
            {
                AppVersion = RedactText(request.Runtime.AppVersion, 120),
                BuildCommit = RedactText(request.Runtime.BuildCommit, 120),
                Browser = RedactText(request.Runtime.Browser, 240),
                OS = RedactText(request.Runtime.OS, 120),
                Timezone = RedactText(request.Runtime.Timezone, 80),
            },
        };
        foreach (DiagnosticClientEventDto evt in clientEvents)
        {
            collection.ClientEvents.Add(SanitizeClientEvent(evt));
        }
        foreach (TaskEntity task in tasks)
        {
            collection.Tasks.Add(SanitizeTask(task));
        }
        foreach (TaskLog taskLog in taskLogs)
        {
            collection.TaskLogs.Add(SanitizeTaskLog(taskLog));
        }
        foreach (ApiCallLog apiCall in apiCalls)
        {
            collection.APICalls.Add(SanitizeAPICall(apiCall));
        }
        return collection;
    }

    // ------------------------------------------------------------ 内部记录与投影

    internal sealed class DiagnosticCollection
    {
        public DateTime From { get; init; }
        public DateTime To { get; init; }
        public string Description { get; init; } = "";
        public string TaskID { get; init; } = "";
        public string ProjectID { get; init; } = "";
        public DiagnosticRuntimeRecord Runtime { get; init; } = new();
        public List<DiagnosticClientEventRecord> ClientEvents { get; } = [];
        public List<DiagnosticTaskRecord> Tasks { get; } = [];
        public List<DiagnosticTaskLogRecord> TaskLogs { get; } = [];
        public List<DiagnosticAPICallRecord> APICalls { get; } = [];
        public bool Truncated { get; init; }
    }

    internal sealed class DiagnosticRuntimeRecord
    {
        [JsonPropertyName("appVersion")]
        public string AppVersion { get; set; } = "";

        [JsonPropertyName("buildCommit")]
        [GoOmitEmpty]
        public string BuildCommit { get; set; } = "";

        [JsonPropertyName("browser")]
        [GoOmitEmpty]
        public string Browser { get; set; } = "";

        [JsonPropertyName("os")]
        [GoOmitEmpty]
        public string OS { get; set; } = "";

        [JsonPropertyName("timezone")]
        [GoOmitEmpty]
        public string Timezone { get; set; } = "";
    }

    internal sealed class DiagnosticClientEventRecord
    {
        [JsonPropertyName("id")]
        [GoOmitEmpty]
        public string ID { get; set; } = "";

        [JsonPropertyName("timestamp")]
        [GoOmitEmpty]
        public string Timestamp { get; set; } = "";

        [JsonPropertyName("level")]
        [GoOmitEmpty]
        public string Level { get; set; } = "";

        [JsonPropertyName("category")]
        [GoOmitEmpty]
        public string Category { get; set; } = "";

        [JsonPropertyName("code")]
        [GoOmitEmpty]
        public string Code { get; set; } = "";

        [JsonPropertyName("message")]
        [GoOmitEmpty]
        public string Message { get; set; } = "";

        [JsonPropertyName("route")]
        [GoOmitEmpty]
        public string Route { get; set; } = "";

        [JsonPropertyName("durationMs")]
        [GoOmitEmpty]
        public long DurationMs { get; set; }

        [JsonPropertyName("httpStatus")]
        [GoOmitEmpty]
        public int HTTPStatus { get; set; }

        [JsonPropertyName("requestId")]
        [GoOmitEmpty]
        public string RequestID { get; set; } = "";

        [JsonPropertyName("traceId")]
        [GoOmitEmpty]
        public string TraceID { get; set; } = "";

        [JsonPropertyName("taskId")]
        [GoOmitEmpty]
        public string TaskID { get; set; } = "";

        [JsonPropertyName("projectId")]
        [GoOmitEmpty]
        public string ProjectID { get; set; } = "";

        [JsonPropertyName("canvasId")]
        [GoOmitEmpty]
        public string CanvasID { get; set; } = "";

        [JsonPropertyName("stack")]
        [GoOmitEmpty]
        public string Stack { get; set; } = "";
    }

    internal sealed class DiagnosticTaskRecord
    {
        [JsonPropertyName("id")]
        public string ID { get; set; } = "";

        [JsonPropertyName("traceId")]
        [GoOmitEmpty]
        public string TraceID { get; set; } = "";

        [JsonPropertyName("requestId")]
        [GoOmitEmpty]
        public string RequestID { get; set; } = "";

        [JsonPropertyName("projectId")]
        [GoOmitEmpty]
        public string ProjectID { get; set; } = "";

        [JsonPropertyName("type")]
        [GoOmitEmpty]
        public string Type { get; set; } = "";

        [JsonPropertyName("status")]
        [GoOmitEmpty]
        public string Status { get; set; } = "";

        [JsonPropertyName("stage")]
        [GoOmitEmpty]
        public string Stage { get; set; } = "";

        [JsonPropertyName("progress")]
        [GoOmitEmpty]
        public int Progress { get; set; }

        [JsonPropertyName("operation")]
        [GoOmitEmpty]
        public string Operation { get; set; } = "";

        [JsonPropertyName("provider")]
        [GoOmitEmpty]
        public string Provider { get; set; } = "";

        [JsonPropertyName("model")]
        [GoOmitEmpty]
        public string Model { get; set; } = "";

        [JsonPropertyName("logicalModelId")]
        [GoOmitEmpty]
        public string LogicalModelID { get; set; } = "";

        [JsonPropertyName("providerRequestId")]
        [GoOmitEmpty]
        public string ProviderRequestID { get; set; } = "";

        [JsonPropertyName("error")]
        [GoOmitEmpty]
        public string Error { get; set; } = "";

        [JsonPropertyName("attempts")]
        [GoOmitEmpty]
        public int Attempts { get; set; }

        [JsonPropertyName("startedAt")]
        [GoOmitEmpty]
        public DateTime? StartedAt { get; set; }

        [JsonPropertyName("completedAt")]
        [GoOmitEmpty]
        public DateTime? CompletedAt { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime UpdatedAt { get; set; }
    }

    internal sealed class DiagnosticTaskLogRecord
    {
        [JsonPropertyName("id")]
        public string ID { get; set; } = "";

        [JsonPropertyName("taskId")]
        [GoOmitEmpty]
        public string TaskID { get; set; } = "";

        [JsonPropertyName("traceId")]
        [GoOmitEmpty]
        public string TraceID { get; set; } = "";

        [JsonPropertyName("requestId")]
        [GoOmitEmpty]
        public string RequestID { get; set; } = "";

        [JsonPropertyName("level")]
        [GoOmitEmpty]
        public string Level { get; set; } = "";

        [JsonPropertyName("message")]
        [GoOmitEmpty]
        public string Message { get; set; } = "";

        [JsonPropertyName("payload")]
        [GoOmitEmpty]
        public string Payload { get; set; } = "";

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }
    }

    internal sealed class DiagnosticAPICallRecord
    {
        [JsonPropertyName("id")]
        public string ID { get; set; } = "";

        [JsonPropertyName("traceId")]
        [GoOmitEmpty]
        public string TraceID { get; set; } = "";

        [JsonPropertyName("requestId")]
        [GoOmitEmpty]
        public string RequestID { get; set; } = "";

        [JsonPropertyName("channelId")]
        [GoOmitEmpty]
        public string ChannelID { get; set; } = "";

        [JsonPropertyName("taskId")]
        [GoOmitEmpty]
        public string TaskID { get; set; } = "";

        [JsonPropertyName("source")]
        [GoOmitEmpty]
        public string Source { get; set; } = "";

        [JsonPropertyName("capability")]
        [GoOmitEmpty]
        public string Capability { get; set; } = "";

        [JsonPropertyName("operation")]
        [GoOmitEmpty]
        public string Operation { get; set; } = "";

        [JsonPropertyName("requestKind")]
        [GoOmitEmpty]
        public string RequestKind { get; set; } = "";

        [JsonPropertyName("apiFormat")]
        [GoOmitEmpty]
        public string APIFormat { get; set; } = "";

        [JsonPropertyName("method")]
        [GoOmitEmpty]
        public string Method { get; set; } = "";

        [JsonPropertyName("path")]
        [GoOmitEmpty]
        public string Path { get; set; } = "";

        [JsonPropertyName("model")]
        [GoOmitEmpty]
        public string Model { get; set; } = "";

        [JsonPropertyName("status")]
        [GoOmitEmpty]
        public string Status { get; set; } = "";

        [JsonPropertyName("statusCode")]
        [GoOmitEmpty]
        public int StatusCode { get; set; }

        [JsonPropertyName("durationMs")]
        [GoOmitEmpty]
        public long DurationMs { get; set; }

        [JsonPropertyName("pollCount")]
        [GoOmitEmpty]
        public int PollCount { get; set; }

        [JsonPropertyName("providerStatus")]
        [GoOmitEmpty]
        public string ProviderStatus { get; set; } = "";

        [JsonPropertyName("providerRequestId")]
        [GoOmitEmpty]
        public string ProviderRequestID { get; set; } = "";

        [JsonPropertyName("errorCode")]
        [GoOmitEmpty]
        public string ErrorCode { get; set; } = "";

        [JsonPropertyName("error")]
        [GoOmitEmpty]
        public string Error { get; set; } = "";

        [JsonPropertyName("startedAt")]
        public DateTime StartedAt { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }
    }

    private static DiagnosticClientEventRecord SanitizeClientEvent(DiagnosticClientEventDto evt) => new()
    {
        ID = SanitizeIdentifier(evt.ID),
        Timestamp = RedactText(evt.Timestamp, 80),
        Level = RedactText(evt.Level, 24),
        Category = RedactText(evt.Category, 32),
        Code = RedactText(evt.Code, 120),
        Message = RedactText(evt.Message, DiagnosticMaxEventText),
        Route = SanitizePath(evt.Route),
        DurationMs = Bound(evt.DurationMs, 0, 86_400_000),
        HTTPStatus = (int)Bound(evt.HTTPStatus, 0, 599),
        RequestID = SanitizeIdentifier(evt.RequestID),
        TraceID = SanitizeIdentifier(evt.TraceID),
        TaskID = SanitizeIdentifier(evt.TaskID),
        ProjectID = SanitizeIdentifier(evt.ProjectID),
        CanvasID = SanitizeIdentifier(evt.CanvasID),
        Stack = RedactText(evt.Stack, DiagnosticMaxEventText),
    };

    private static DiagnosticTaskRecord SanitizeTask(TaskEntity task) => new()
    {
        ID = task.ID,
        TraceID = SanitizeIdentifier(task.TraceID),
        RequestID = SanitizeIdentifier(task.RequestID),
        ProjectID = SanitizeIdentifier(task.ProjectID),
        Type = RedactText(task.Type, 80),
        Status = RedactText(task.Status, 32),
        Stage = RedactText(task.Stage, 160),
        Progress = (int)Bound(task.Progress, 0, 100),
        Operation = RedactText(task.Operation, 120),
        Provider = RedactText(task.Provider, 120),
        Model = RedactText(task.Model, 160),
        LogicalModelID = SanitizeIdentifier(task.LogicalModelID),
        ProviderRequestID = SanitizeIdentifier(task.ProviderRequestID),
        Error = RedactText(task.Error, DiagnosticMaxEventText),
        Attempts = (int)Bound(task.Attempts, 0, 100),
        StartedAt = task.StartedAt,
        CompletedAt = task.CompletedAt,
        CreatedAt = task.CreatedAt,
        UpdatedAt = task.UpdatedAt,
    };

    private static DiagnosticTaskLogRecord SanitizeTaskLog(TaskLog taskLog) => new()
    {
        ID = taskLog.ID,
        TaskID = SanitizeIdentifier(taskLog.TaskID),
        TraceID = SanitizeIdentifier(taskLog.TraceID),
        RequestID = SanitizeIdentifier(taskLog.RequestID),
        Level = RedactText(taskLog.Level, 24),
        Message = RedactText(taskLog.Message, DiagnosticMaxEventText),
        Payload = RedactText(taskLog.Payload, DiagnosticMaxEventText),
        CreatedAt = taskLog.CreatedAt,
    };

    private static DiagnosticAPICallRecord SanitizeAPICall(ApiCallLog log) => new()
    {
        ID = log.ID,
        TraceID = SanitizeIdentifier(log.TraceID),
        RequestID = SanitizeIdentifier(log.RequestID),
        ChannelID = SanitizeIdentifier(log.ChannelID),
        TaskID = SanitizeIdentifier(log.TaskID),
        Source = RedactText(log.Source, 80),
        Capability = RedactText(log.Capability, 48),
        Operation = RedactText(log.Operation, 120),
        RequestKind = RedactText(log.RequestKind, 48),
        APIFormat = RedactText(log.APIFormat, 48),
        Method = RedactText(log.Method, 16),
        Path = SanitizePath(log.Path),
        Model = RedactText(log.Model, 160),
        Status = RedactText(log.Status, 32),
        StatusCode = (int)Bound(log.StatusCode, 0, 599),
        DurationMs = Bound(log.DurationMs, 0, 86_400_000),
        PollCount = (int)Bound(log.PollCount, 0, 10_000),
        ProviderStatus = RedactText(log.ProviderStatus, 80),
        ProviderRequestID = SanitizeIdentifier(log.ProviderRequestID),
        ErrorCode = RedactText(log.ErrorCode, 120),
        Error = RedactText(log.Error, DiagnosticMaxEventText),
        StartedAt = log.StartedAt,
        CreatedAt = log.CreatedAt,
    };

    // ------------------------------------------------------------ ZIP 构建

    /// <summary>构建诊断 ZIP。对应 Go: <c>buildDiagnosticZIP</c>。</summary>
    private static byte[] BuildDiagnosticZIP(
        string brandName, string bundleID, DiagnosticCollection collection)
    {
        using MemoryStream buffer = new();
        using ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true);
        WriteZipFile(archive, "manifest.json",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                ManifestOf(bundleID, collection), ProjectCharacterService.GoPayloadOptions) + "\n"));
        string newline = "\n";
        string readme =
            $"{brandName} 用户诊断包{newline}{newline}诊断编号：{bundleID}{newline}" +
            $"时间范围：{ProjectService.FormatRfc3339Nano(collection.From)} 至 {ProjectService.FormatRfc3339Nano(collection.To)}{newline}" +
            $"{newline}{newline}该文件由用户主动导出，仅包含有限时间范围内的脱敏诊断摘要。{newline}";
        WriteZipFile(archive, "README.txt", Encoding.UTF8.GetBytes(readme));
        WriteJSONL(archive, "client/events.jsonl", collection.ClientEvents);
        WriteJSONL(archive, "backend/tasks.jsonl", collection.Tasks);
        WriteJSONL(archive, "backend/task-logs.jsonl", collection.TaskLogs);
        WriteJSONL(archive, "backend/upstream-calls.jsonl", collection.APICalls);
        WriteZipFile(archive, "context/runtime.json",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                collection.Runtime, ProjectCharacterService.GoPayloadOptions) + "\n"));
        archive.Dispose();
        return buffer.ToArray();
    }

    private static object ManifestOf(string bundleID, DiagnosticCollection collection) => new
    {
        schemaVersion = DiagnosticSchemaVersion,
        bundleID,
        generatedAt = ProjectService.FormatRfc3339Nano(DateTime.UtcNow),
        appVersion = BlankOr(collection.Runtime.AppVersion),
        buildCommit = BlankOr(collection.Runtime.BuildCommit),
        timeRange = new
        {
            from = ProjectService.FormatRfc3339Nano(collection.From),
            to = ProjectService.FormatRfc3339Nano(collection.To),
        },
        taskId = BlankOr(collection.TaskID),
        projectId = BlankOr(collection.ProjectID),
        description = BlankOr(collection.Description),
        redactionVersion = DiagnosticRedactionVersion,
        truncated = BlankBool(collection.Truncated),
        counts = new
        {
            clientEvents = collection.ClientEvents.Count,
            tasks = collection.Tasks.Count,
            taskLogs = collection.TaskLogs.Count,
            upstreamCalls = collection.APICalls.Count,
        },
    };

    private static string BlankOr(string value) => value.Length > 0 ? value : null!;

    private static bool? BlankBool(bool value) => value ? value : null;

    private static void WriteJSONL<T>(ZipArchive archive, string name, List<T> records)
    {
        StringBuilder data = new();
        foreach (T record in records)
        {
            data.Append(JsonSerializer.Serialize(record, ProjectCharacterService.GoPayloadOptions));
            data.Append('\n');
        }
        WriteZipFile(archive, name, Encoding.UTF8.GetBytes(data.ToString()));
    }

    private static void WriteZipFile(ZipArchive archive, string name, byte[] data)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }

    // ------------------------------------------------------------ 窗口与脱敏

    /// <summary>诊断时间窗归一。对应 Go: <c>normalizeDiagnosticWindow</c>。</summary>
    private static (DateTime From, DateTime To) NormalizeWindow(string fromRaw, string toRaw)
    {
        DateTime now = DateTime.UtcNow;
        DateTime from = now.AddMinutes(-30);
        DateTime to = now;
        if (fromRaw.Trim().Length > 0)
        {
            if (!DateTime.TryParse(fromRaw.Trim(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out from))
            {
                throw AppError.BadAuthRequest("诊断开始时间格式无效");
            }
        }
        if (toRaw.Trim().Length > 0)
        {
            if (!DateTime.TryParse(toRaw.Trim(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out to))
            {
                throw AppError.BadAuthRequest("诊断结束时间格式无效");
            }
        }
        from = from.ToUniversalTime();
        to = to.ToUniversalTime();
        if (to > now)
        {
            to = now;
        }
        if (to <= from)
        {
            throw AppError.BadAuthRequest("诊断时间范围无效");
        }
        if (to - from > TimeSpan.FromHours(24))
        {
            throw AppError.BadAuthRequest("诊断时间范围不能超过 24 小时");
        }
        return (from, to);
    }

    /// <summary>文本脱敏。对应 Go: <c>redactDiagnosticText</c>。</summary>
    internal static string RedactText(string value, int limit)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return "";
        }
        foreach (string marker in RedactMarkers)
        {
            value = RedactMarker(value, marker);
        }
        return KernelUtil.TruncateRunes(RedactURLs(value), limit);
    }

    /// <summary>对应 Go: <c>redactDiagnosticMarker</c>。命中标记后值段替换为 [REDACTED]。</summary>
    private static string RedactMarker(string value, string marker)
    {
        int searchFrom = 0;
        while (searchFrom < value.Length)
        {
            int index = value.IndexOf(marker, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }
            int end = index + marker.Length;
            while (end < value.Length && IsDiagnosticSeparator(value[end]))
            {
                end++;
            }
            int valueEnd = end;
            while (valueEnd < value.Length && !IsDiagnosticValueDelimiter(value[valueEnd]))
            {
                valueEnd++;
            }
            if (valueEnd == end)
            {
                searchFrom = end;
                continue;
            }
            value = value[..end] + "[REDACTED]" + value[valueEnd..];
            searchFrom = end + "[REDACTED]".Length;
        }
        return value;
    }

    /// <summary>剥离 http(s) 链接的 query/fragment。对应 Go: <c>redactDiagnosticURLs</c>。</summary>
    private static string RedactURLs(string value)
    {
        int searchFrom = 0;
        while (searchFrom < value.Length)
        {
            int startHTTP = value.IndexOf("http://", searchFrom, StringComparison.Ordinal);
            int startHTTPS = value.IndexOf("https://", searchFrom, StringComparison.Ordinal);
            int start = -1;
            if (startHTTP >= 0)
            {
                start = startHTTP;
            }
            if (startHTTPS >= 0 && (start < 0 || startHTTPS < start))
            {
                start = startHTTPS;
            }
            if (start < 0)
            {
                break;
            }
            int end = start;
            while (end < value.Length && !IsURLDelimiter(value[end]))
            {
                end++;
            }
            string raw = value[start..end];
            if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? parsed))
            {
                searchFrom = end;
                continue;
            }
            string safe = new UriBuilder(parsed) { Query = "", Fragment = "" }.Uri.ToString();
            value = value[..start] + safe + value[end..];
            searchFrom = start + safe.Length;
        }
        return value;
    }

    private static bool IsDiagnosticSeparator(char value) => value is ' ' or '\t' or ':' or '=';

    private static bool IsDiagnosticValueDelimiter(char value) =>
        value is ' ' or '\t' or '\r' or '\n' or ',' or ';' or '"' or '\'' or '<' or '>' or '}' or ']';

    private static bool IsURLDelimiter(char value) =>
        value is ' ' or '\t' or '\r' or '\n' or '"' or '\'' or '<' or '>';

    private static string SanitizeIdentifier(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Length > 96)
        {
            return "";
        }
        foreach (char ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or ':' or '-')
            {
                continue;
            }
            return "";
        }
        return value;
    }

    private static string SanitizePath(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return "";
        }
        int index = value.IndexOfAny(['?', '#']);
        if (index >= 0)
        {
            value = value[..index];
        }
        return RedactText(value, 300);
    }

    private static int Bound(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    private static long Bound(long value, long min, long max) =>
        value < min ? min : value > max ? max : value;

}
