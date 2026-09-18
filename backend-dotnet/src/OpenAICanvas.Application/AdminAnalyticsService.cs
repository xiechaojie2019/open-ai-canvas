#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>日志分页。对应 Go: <c>app.APICallLogPage</c>。</summary>
public sealed class ApiCallLogPageDto
{
    [JsonPropertyName("logs")]
    public IReadOnlyList<ApiCallLog> Logs { get; init; } = [];

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("page")]
    public long Page { get; init; }

    [JsonPropertyName("pageSize")]
    public long PageSize { get; init; }
}

/// <summary>存储资源视图。对应 Go: <c>app.AdminStorageResourceView</c>（字段顺序即输出顺序）。</summary>
public sealed class AdminStorageResourceViewDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("userId")]
    public string UserID { get; init; } = "";

    [JsonPropertyName("userName")]
    public string UserName { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "";

    [JsonPropertyName("bucket")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string Bucket { get; init; } = "";

    [JsonPropertyName("objectKey")]
    public string ObjectKey { get; init; } = "";

    [JsonPropertyName("mimeType")]
    public string MimeType { get; init; } = "";

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("physicalBytes")]
    public long PhysicalBytes { get; init; }

    [JsonPropertyName("width")]
    public long Width { get; init; }

    [JsonPropertyName("height")]
    public long Height { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("fileUrl")]
    public string FileURL { get; init; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }
}

/// <summary>存储资源分页。对应 Go: <c>app.AdminResourcePage</c>。</summary>
public sealed class AdminResourcePageDto
{
    [JsonPropertyName("items")]
    public IReadOnlyList<AdminStorageResourceViewDto> Items { get; init; } = [];

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("page")]
    public long Page { get; init; }

    [JsonPropertyName("pageSize")]
    public long PageSize { get; init; }
}

/// <summary>存储统计。对应 Go: <c>app.AdminStorageStats</c>（内嵌 summary 字段在前）。</summary>
public sealed class AdminStorageStatsDto
{
    [JsonPropertyName("resourceCount")]
    public long ResourceCount { get; init; }

    [JsonPropertyName("readyCount")]
    public long ReadyCount { get; init; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; init; }

    [JsonPropertyName("physicalBytes")]
    public long PhysicalBytes { get; init; }

    [JsonPropertyName("byKind")]
    public IReadOnlyList<ResourceKindStat> ByKind { get; init; } = [];

    [JsonPropertyName("byProvider")]
    public IReadOnlyList<ResourceProviderStat> ByProvider { get; init; } = [];
}

/// <summary>
/// 管理后台分析/日志/存储服务。
/// 对应 Go: <c>app/analytics.go</c> 的日志部分与 <c>app/admin_storage.go</c>。
/// </summary>
/// <remarks>
/// 完整分析总览（overview/users/models，依赖 user_daily_activities 与任务聚合）
/// 属后续节点；日志列表/详情/导出与存储统计/列表已完整移植。
/// </remarks>
public sealed partial class AdminAnalyticsService
{
    private readonly Repository _repository;

    public AdminAnalyticsService(Repository repository)
    {
        _repository = repository;
    }

    /// <summary>对应 Go: <c>normalizeAnalyticsFilter</c>。</summary>
    public static AnalyticsFilter NormalizeFilter(
        string from, string to, string userId, string model, string channelId, string capability)
    {
        DateTime now = DateTime.UtcNow;
        DateTime toDate = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);
        DateTime fromDate = toDate.AddDays(-30);
        if (TryParseAnalyticsTime(from, out DateTime parsedFrom))
        {
            fromDate = parsedFrom;
        }
        if (TryParseAnalyticsTime(to, out DateTime parsedTo))
        {
            toDate = parsedTo;
            if (to.Trim().Length == "2006-01-02".Length)
            {
                toDate = toDate.AddDays(1);
            }
        }
        if (toDate <= fromDate)
        {
            toDate = fromDate.AddDays(1);
        }
        if (toDate - fromDate > TimeSpan.FromDays(366))
        {
            fromDate = toDate.AddYears(-1);
        }
        return new AnalyticsFilter(
            fromDate, toDate, userId.Trim(), model.Trim(), channelId.Trim(),
            CapabilitySpecOps.NormalizeCapability(capability));
    }

    private static bool TryParseAnalyticsTime(string value, out DateTime parsed)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            parsed = default;
            return false;
        }
        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
        {
            parsed = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
            return true;
        }
        if (DateTime.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
        {
            return true;
        }
        return false;
    }

    /// <summary>日志分页（含渠道/用户/账单/任务装饰）。对应 Go: <c>AdminAPICallLogs</c>。</summary>
    public async Task<ApiCallLogPageDto> ApiCallLogsAsync(
        User actor, ApiCallLogFilter filter, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        if (filter.RecordType.Length > 0 && filter.RecordType is not ("request" or "download" or "all"))
        {
            throw AppError.BadAuthRequest("请求明细类型无效");
        }
        (IReadOnlyList<ApiCallLog> logs, long total) = await _repository.QueryApiCallLogsAsync(
            filter, cancellationToken).ConfigureAwait(false);
        await DecorateApiCallLogsAsync(logs, cancellationToken).ConfigureAwait(false);
        foreach (ApiCallLog log in logs)
        {
            // 原始报文只允许通过详情接口按单条读取。
            log.RequestBody = "";
            log.ResponseBody = "";
        }
        long page = filter.Page <= 0 ? 1 : filter.Page;
        long limit = filter.Limit is <= 0 or > 200 ? 50 : filter.Limit;
        return new ApiCallLogPageDto { Logs = logs, Total = total, Page = page, PageSize = limit };
    }

    /// <summary>日志详情（含原始报文）。对应 Go: <c>AdminAPICallLog</c>。</summary>
    public async Task<ApiCallLog> ApiCallLogAsync(
        User actor, string id, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        ApiCallLog? log = await _repository.ApiCallLogAsync(id.Trim(), cancellationToken).ConfigureAwait(false);
        if (log is null)
        {
            throw AppError.NotFound("请求日志不存在");
        }
        await DecorateApiCallLogsAsync([log], cancellationToken).ConfigureAwait(false);
        return log;
    }

    /// <summary>日志 CSV 导出。对应 Go: <c>AdminAPICallLogsExportCSV</c>。</summary>
    public async Task<string> ApiCallLogsExportCsvAsync(
        User actor, ApiCallLogFilter filter, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        IReadOnlyList<ApiCallLog> logs = await _repository.ApiCallLogsForExportAsync(filter, cancellationToken)
            .ConfigureAwait(false);
        await DecorateApiCallLogsAsync(logs, cancellationToken).ConfigureAwait(false);

        StringBuilder builder = new();
        builder.Append('\uFEFF');
        builder.AppendLine("时间,用户,渠道,模型,能力,状态,耗时(ms),提示词Token,补全Token,计费状态,计费积分,错误");
        foreach (ApiCallLog log in logs)
        {
            builder.Append(CsvField(log.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(',')
                .Append(CsvField(log.UserAccount)).Append(',')
                .Append(CsvField(log.ChannelName)).Append(',')
                .Append(CsvField(log.Model)).Append(',')
                .Append(CsvField(log.Capability)).Append(',')
                .Append(CsvField(log.Status)).Append(',')
                .Append(log.DurationMs.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(log.InputTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(log.OutputTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(CsvField(log.BillingStatus)).Append(',')
                .Append(log.BillingAmount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(CsvField(log.Error))
                .Append('\n');
        }
        return builder.ToString();
    }

    private static string CsvField(string value)
    {
        if (value.Length == 0)
        {
            return "";
        }
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return '"' + value.Replace("\"", "\"\"") + '"';
        }
        return value;
    }

    /// <summary>对应 Go: <c>decorateAPICallLogs</c>。</summary>
    private async Task DecorateApiCallLogsAsync(
        IReadOnlyList<ApiCallLog> logs, CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelChannel> channels = await _repository
            .HistoricalSystemChannelReferencesAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> channelNames = new(StringComparer.Ordinal);
        foreach (ModelChannel channel in channels)
        {
            channelNames[channel.ID] = channel.Name;
        }
        IReadOnlyList<User> users = await _repository.UsersAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, User> userByID = new(StringComparer.Ordinal);
        foreach (User user in users)
        {
            userByID[user.ID] = user;
        }

        List<string> billingOrderIds = [];
        List<string> taskIds = [];
        HashSet<string> seenBilling = new(StringComparer.Ordinal);
        HashSet<string> seenTasks = new(StringComparer.Ordinal);
        foreach (ApiCallLog log in logs)
        {
            if (log.Billable && log.BillingOrderID.Length > 0 && seenBilling.Add(log.BillingOrderID))
            {
                billingOrderIds.Add(log.BillingOrderID);
            }
            if ((log.Capability is "image" or "video") && log.TaskID.Length > 0 && seenTasks.Add(log.TaskID))
            {
                taskIds.Add(log.TaskID);
            }
        }
        IReadOnlyList<Domain.Entities.Task> tasks = await _repository.ApiCallLogTasksAsync(
            taskIds, cancellationToken).ConfigureAwait(false);
        Dictionary<string, Domain.Entities.Task> taskByID = new(StringComparer.Ordinal);
        foreach (Domain.Entities.Task task in tasks)
        {
            taskByID[task.ID] = task;
        }
        Dictionary<string, BillingOrder> billingOrderByID = await _repository
            .BillingOrdersByIDsAsync(billingOrderIds, cancellationToken).ConfigureAwait(false);

        foreach (ApiCallLog log in logs)
        {
            if (log.StartedAt == default)
            {
                log.StartedAt = log.CreatedAt;
            }
            if (log.ChannelID.Length == 0)
            {
                log.ChannelName = "自定义渠道";
            }
            else if (channelNames.TryGetValue(log.ChannelID, out string? name) && name.Length > 0)
            {
                log.ChannelName = name;
            }
            else
            {
                log.ChannelName = "已删除渠道";
            }
            if (userByID.TryGetValue(log.UserID, out User? user))
            {
                log.UserDisplayName = user.DisplayName;
                log.UserAccount = user.Username;
            }
            if (log.Billable &&
                billingOrderByID.TryGetValue(log.BillingOrderID, out BillingOrder? order) &&
                order.UserID == log.UserID)
            {
                log.BillingAvailable = true;
                log.BillingStatus = order.Status;
                if (order.Status == "settled")
                {
                    log.BillingAmount = order.ActualAmountMicrocredits;
                }
                else if (order.Status != "refunded")
                {
                    log.BillingAmount = order.ReservedAmountMicrocredits;
                }
            }
            if (taskByID.TryGetValue(log.TaskID, out Domain.Entities.Task? task) && task.UserID == log.UserID)
            {
                log.TaskStatus = task.Status;
                (string previewURL, string previewKind) = TaskMediaPreview(task.ResultJSON, task.Type);
                if (CanvasResourceID(previewURL).Length > 0)
                {
                    log.MediaPreviewURL = "/api/admin/api-logs/" + log.ID + "/media";
                    log.MediaPreviewKind = previewKind;
                }
                else if (previewURL.StartsWith("https://", StringComparison.Ordinal) ||
                         previewURL.StartsWith("http://", StringComparison.Ordinal))
                {
                    log.MediaPreviewURL = previewURL;
                    log.MediaPreviewKind = previewKind;
                }
            }
        }
    }

    /// <summary>对应 Go: <c>taskMediaPreview</c>（结果 JSON 的首个媒体 URL 与类型）。</summary>
    internal static (string URL, string Kind) TaskMediaPreview(string resultJSON, string taskType)
    {
        if (string.IsNullOrWhiteSpace(resultJSON))
        {
            return ("", "");
        }
        try
        {
            System.Text.Json.JsonElement root = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(resultJSON);
            return FindMedia(root, taskType);
        }
        catch (System.Text.Json.JsonException)
        {
            return ("", "");
        }
    }

    private static (string URL, string Kind) FindMedia(System.Text.Json.JsonElement element, string taskType)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
            {
                string text = element.GetString() ?? "";
                if (text.StartsWith("http://", StringComparison.Ordinal) ||
                    text.StartsWith("https://", StringComparison.Ordinal) ||
                    text.StartsWith("/api/resources/", StringComparison.Ordinal) ||
                    text.StartsWith("resource:", StringComparison.Ordinal))
                {
                    return (text, NormalizePreviewKind(taskType));
                }
                return ("", "");
            }
            case System.Text.Json.JsonValueKind.Array:
                foreach (System.Text.Json.JsonElement child in element.EnumerateArray())
                {
                    (string url, string kind) = FindMedia(child, taskType);
                    if (url.Length > 0)
                    {
                        return (url, kind);
                    }
                }
                return ("", "");
            case System.Text.Json.JsonValueKind.Object:
                foreach (string key in new[] { "url", "dataUrl", "content", "storageKey", "videoUrl", "imageUrl" })
                {
                    if (element.TryGetProperty(key, out System.Text.Json.JsonElement value))
                    {
                        (string url, string kind) = FindMedia(value, taskType);
                        if (url.Length > 0)
                        {
                            return (url, kind);
                        }
                    }
                }
                foreach (System.Text.Json.JsonProperty property in element.EnumerateObject())
                {
                    (string url, string kind) = FindMedia(property.Value, taskType);
                    if (url.Length > 0)
                    {
                        return (url, kind);
                    }
                }
                return ("", "");
            default:
                return ("", "");
        }
    }

    private static string NormalizePreviewKind(string taskType) =>
        taskType.Contains("video", StringComparison.OrdinalIgnoreCase) ? "video"
        : taskType.Contains("image", StringComparison.OrdinalIgnoreCase) ? "image"
        : "";

    /// <summary>对应 Go: <c>canvasResourceID</c>。</summary>
    internal static string CanvasResourceID(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith("resource:", StringComparison.Ordinal))
        {
            return UserDataService.ValidID(trimmed["resource:".Length..]);
        }
        return UserDataService.ValidID(UserDataService.IDFromFileURL(trimmed));
    }

    /// <summary>存储统计。对应 Go: <c>AdminStorageStats</c>。</summary>
    public async Task<AdminStorageStatsDto> StorageStatsAsync(
        User actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        ResourceStorageSummary summary = await _repository.ResourceStorageSummaryAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ResourceKindStat> byKind = await _repository.ResourceKindStatsAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ResourceProviderStat> byProvider = await _repository
            .ResourceProviderStatsAsync(cancellationToken).ConfigureAwait(false);
        return new AdminStorageStatsDto
        {
            ResourceCount = summary.ResourceCount,
            ReadyCount = summary.ReadyCount,
            TotalBytes = summary.TotalBytes,
            PhysicalBytes = summary.PhysicalBytes,
            ByKind = byKind,
            ByProvider = byProvider,
        };
    }
}