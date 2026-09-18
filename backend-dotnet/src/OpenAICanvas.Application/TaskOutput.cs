#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Serialization;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application;

/// <summary>
/// 任务列表/详情读模型。对应 Go: <c>app.TaskSummary</c>。
/// </summary>
/// <remarks>
/// 不复用数据库实体，避免把渠道模型、供应线路和受保护输入泄露到普通用户接口。
/// 字段顺序与 Go 声明顺序一致（Go struct 按声明顺序输出 JSON）。
/// </remarks>
public sealed class TaskSummaryDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("projectId")]
    [GoOmitEmpty]
    public string ProjectID { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("stage")]
    public string Stage { get; init; } = "";

    [JsonPropertyName("progress")]
    public long Progress { get; init; }

    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = "";

    [JsonPropertyName("operation")]
    [GoOmitEmpty]
    public string Operation { get; init; } = "";

    [JsonPropertyName("provider")]
    [GoOmitEmpty]
    public string Provider { get; init; } = "";

    [JsonPropertyName("model")]
    [GoOmitEmpty]
    public string Model { get; init; } = "";

    [JsonPropertyName("providerRequestId")]
    [GoOmitEmpty]
    public string ProviderRequestID { get; init; } = "";

    [JsonPropertyName("providerCancelStatus")]
    [GoOmitEmpty]
    public string ProviderCancelStatus { get; init; } = "";

    [JsonPropertyName("providerCancelError")]
    [GoOmitEmpty]
    public string ProviderCancelError { get; init; } = "";

    [JsonPropertyName("providerCancelAttempts")]
    [GoOmitEmpty]
    public long ProviderCancelAttempts { get; init; }

    [JsonPropertyName("providerCancelRequestedAt")]
    [GoOmitEmpty]
    public DateTime? ProviderCancelRequestedAt { get; init; }

    [JsonPropertyName("providerCancelledAt")]
    [GoOmitEmpty]
    public DateTime? ProviderCancelledAt { get; init; }

    [JsonPropertyName("errorCode")]
    [GoOmitEmpty]
    public string ErrorCode { get; init; } = "";

    [JsonPropertyName("previewUrl")]
    [GoOmitEmpty]
    public string PreviewURL { get; init; } = "";

    [JsonPropertyName("previewKind")]
    [GoOmitEmpty]
    public string PreviewKind { get; init; } = "";

    [JsonPropertyName("previewPosterUrl")]
    [GoOmitEmpty]
    public string PreviewPosterURL { get; init; } = "";

    [JsonPropertyName("attempts")]
    public long Attempts { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    [JsonPropertyName("billing")]
    [GoOmitEmpty]
    public TaskBillingSummaryDto? Billing { get; set; }

    [JsonPropertyName("clientContext")]
    [GoOmitEmpty]
    public TaskClientContextDto? ClientContext { get; init; }
}

/// <summary>任务客户端上下文。对应 Go: <c>app.TaskClientContext</c>。全部字段带 omitempty。</summary>
public sealed class TaskClientContextDto
{
    [JsonPropertyName("nodeId")]
    [GoOmitEmpty]
    public string NodeID { get; init; } = "";

    [JsonPropertyName("conversationId")]
    [GoOmitEmpty]
    public string ConversationID { get; init; } = "";

    [JsonPropertyName("messageId")]
    [GoOmitEmpty]
    public string MessageID { get; init; } = "";

    [JsonPropertyName("batchIndex")]
    [GoOmitEmpty]
    public int BatchIndex { get; init; }

    [JsonPropertyName("batchCount")]
    [GoOmitEmpty]
    public int BatchCount { get; init; }

    [JsonPropertyName("domainProjectId")]
    [GoOmitEmpty]
    public string DomainProjectID { get; init; } = "";

    [JsonPropertyName("chapterId")]
    [GoOmitEmpty]
    public string ChapterID { get; init; } = "";

    [JsonPropertyName("chapterOperation")]
    [GoOmitEmpty]
    public string ChapterOperation { get; init; } = "";

    [JsonPropertyName("shotId")]
    [GoOmitEmpty]
    public string ShotID { get; init; } = "";

    [JsonPropertyName("workflowStepId")]
    [GoOmitEmpty]
    public string WorkflowStepID { get; init; } = "";

    [JsonPropertyName("artifactType")]
    [GoOmitEmpty]
    public string ArtifactType { get; init; } = "";
}

/// <summary>任务计费摘要。对应 Go: <c>app.TaskBillingSummary</c>。无 omitempty。</summary>
public sealed class TaskBillingSummaryDto
{
    [JsonPropertyName("amountMicrocredits")]
    public long AmountMicrocredits { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";
}

/// <summary>任务输出投影。对应 Go: <c>app/task_output.go</c> 的纯函数部分。</summary>
public static class TaskOutput
{
    /// <summary>内容审核失败的稳定错误码。对应 Go: <c>contentModerationErrorCode</c>。</summary>
    public const string ContentModerationErrorCode = "sensitive_words_detected";

    /// <summary>任务列表投影（带计费信息）。对应 Go: <c>taskSummariesForOutputWithBilling</c>。</summary>
    public static List<TaskSummaryDto> SummariesWithBilling(
        IReadOnlyList<TaskEntity> tasks,
        IReadOnlyDictionary<string, BillingOrder>? orders)
    {
        List<TaskSummaryDto> result = new(tasks.Count);
        foreach (TaskEntity task in tasks)
        {
            TaskSummaryDto summary = Summary(task);
            if (orders is not null && orders.TryGetValue(task.ID, out BillingOrder? order))
            {
                summary.Billing = new TaskBillingSummaryDto
                {
                    AmountMicrocredits = order.AmountMicrocredits,
                    Status = order.Status,
                };
                if (string.IsNullOrEmpty(summary.ProviderRequestID))
                {
                    // 记录：Go 直接改写 summary.ProviderRequestID，这里同样就地覆盖。
                    summary = WithProviderRequestID(summary, order.ProviderRequestID);
                }
            }
            result.Add(summary);
        }
        return result;
    }

    /// <summary>
    /// 需要参与计费查询的任务 ID。对应 Go: <c>taskBillingTaskIDs</c>。
    /// 只保留有计费订单的任务，并按任务 ID 去重。
    /// </summary>
    public static List<string> BillingTaskIDs(IReadOnlyList<TaskEntity> tasks)
    {
        List<string> ids = new(tasks.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (TaskEntity task in tasks)
        {
            if (string.IsNullOrEmpty(task.BillingOrderID) || !seen.Add(task.ID))
            {
                continue;
            }
            ids.Add(task.ID);
        }
        return ids;
    }

    /// <summary>单任务列表投影。对应 Go: <c>taskSummaryForOutput</c>。</summary>
    public static TaskSummaryDto Summary(TaskEntity task)
    {
        string errorCode = IsContentModerationFailure(task.Error) ? ContentModerationErrorCode : "";
        (string previewURL, string previewKind, string previewPosterURL) =
            MediaPreviewWithPoster(task.ResultJSON, task.Type);

        return new TaskSummaryDto
        {
            ID = task.ID,
            ProjectID = task.ProjectID,
            Type = task.Type,
            Status = task.Status,
            Stage = task.Stage,
            Progress = task.Progress,
            Prompt = TruncateRunes(task.Prompt, 500),
            Operation = task.Operation,
            Provider = task.Provider,
            Model = task.Model,
            ProviderRequestID = task.ProviderRequestID,
            ProviderCancelStatus = task.ProviderCancelStatus,
            ProviderCancelError = task.ProviderCancelError,
            ProviderCancelAttempts = task.ProviderCancelAttempts,
            ProviderCancelRequestedAt = task.ProviderCancelRequestedAt,
            ProviderCancelledAt = task.ProviderCancelledAt,
            ErrorCode = errorCode,
            PreviewURL = previewURL,
            PreviewKind = previewKind,
            PreviewPosterURL = previewPosterURL,
            Attempts = task.Attempts,
            StartedAt = task.StartedAt,
            CompletedAt = task.CompletedAt,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
            ClientContext = ClientContext(task.InputJSON),
        };
    }

    /// <summary>
    /// 任务详情投影：只暴露前台模型身份，抹掉渠道模型与供应线路。
    /// 对应 Go: <c>taskForOutput</c>。
    /// </summary>
    public static TaskEntity ForOutput(TaskEntity task)
    {
        task.InputJSON = PublicTaskInputJSON(task.InputJSON);
        task.LogicalModelRevisionID = "";
        task.RouteID = "";
        task.ChannelModelID = "";
        return task;
    }

    /// <summary>
    /// 列表只暴露页面恢复所需的非敏感关联 ID，不下发完整任务输入。
    /// 对应 Go: <c>taskClientContext</c>。
    /// </summary>
    public static TaskClientContextDto? ClientContext(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        TaskMetadata? metadata;
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("metadata", out JsonElement metadataElement)
                || metadataElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            metadata = metadataElement.Deserialize<TaskMetadata>(CanvasJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (metadata is null)
        {
            return null;
        }

        TaskClientContextDto context = new() { NodeID = metadata.NodeID };

        // 创建页的对话式任务：只需要会话与消息定位。
        if (metadata.Source == "create-page"
            && !string.IsNullOrEmpty(metadata.ConversationID)
            && !string.IsNullOrEmpty(metadata.MessageID))
        {
            return new TaskClientContextDto
            {
                NodeID = metadata.NodeID,
                ConversationID = metadata.ConversationID,
                MessageID = metadata.MessageID,
                BatchIndex = metadata.BatchIndex,
                BatchCount = metadata.BatchCount,
            };
        }

        // 分镜工作流任务：需要镜头与步骤定位。
        if (!string.IsNullOrEmpty(metadata.ShotID) && !string.IsNullOrEmpty(metadata.WorkflowStepID))
        {
            return new TaskClientContextDto
            {
                NodeID = metadata.NodeID,
                DomainProjectID = metadata.DomainProjectID,
                ShotID = metadata.ShotID,
                WorkflowStepID = metadata.WorkflowStepID,
                ArtifactType = metadata.ArtifactType,
            };
        }

        string chapterOperation = metadata.Operation switch
        {
            "chapter_character_breakdown" => "characters",
            _ => metadata.Source == "short-drama-chapter-storyboard" ? "storyboard" : "",
        };

        if (string.IsNullOrEmpty(chapterOperation)
            || string.IsNullOrEmpty(metadata.DomainProjectID)
            || string.IsNullOrEmpty(metadata.ChapterID))
        {
            return string.IsNullOrEmpty(context.NodeID) ? null : context;
        }

        return new TaskClientContextDto
        {
            NodeID = metadata.NodeID,
            DomainProjectID = metadata.DomainProjectID,
            ChapterID = metadata.ChapterID,
            ChapterOperation = chapterOperation,
        };
    }

    /// <summary>
    /// 列表只暴露首个可访问媒体地址。对应 Go: <c>taskMediaPreviewWithPoster</c>。
    /// 返回 (预览地址, 类型, 海报地址)。
    /// </summary>
    public static (string PreviewURL, string PreviewKind, string PosterURL) MediaPreviewWithPoster(
        string raw, string taskType)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ("", "", "");
        }

        object? payload;
        try
        {
            payload = JsonSerializer.Deserialize<object>(raw);
        }
        catch (JsonException)
        {
            return ("", "", "");
        }

        string defaultKind = taskType.ToLowerInvariant().Contains("video", StringComparison.Ordinal)
            ? "video"
            : "image";

        (string previewURL, string previewKind) = FindMediaPreview(payload, defaultKind);
        string posterURL = FindMediaPoster(payload);
        if (previewKind == "image" && string.IsNullOrEmpty(posterURL))
        {
            posterURL = previewURL;
        }
        return (previewURL, previewKind, posterURL);
    }

    /// <summary>
    /// 过滤任务输入里的敏感字段。对应 Go: <c>publicTaskInputJSON</c>。
    /// 保留恢复项目产物归属所需的非敏感 ID，密钥等配置继续被过滤。
    /// </summary>
    public static string PublicTaskInputJSON(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        Dictionary<string, JsonElement>? input;
        try
        {
            input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw);
        }
        catch (JsonException)
        {
            return "";
        }

        if (input is null)
        {
            return "";
        }

        Dictionary<string, JsonElement> @public = new(StringComparer.Ordinal);
        foreach (string key in PublicTaskInputKeys)
        {
            if (input.TryGetValue(key, out JsonElement value))
            {
                @public[key] = value;
            }
        }

        return @public.Count == 0 ? "" : JsonSerializer.Serialize(@public);
    }

    /// <summary>任务输入允许公开的键。对应 Go 的白名单字面量。</summary>
    private static readonly string[] PublicTaskInputKeys =
    [
        "mode", "metadata", "workflowStepId", "domainProjectId",
        "assetVersionId", "resourceId", "mediaType", "role",
    ];

    private static readonly JsonSerializerOptions CanvasJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static TaskSummaryDto WithProviderRequestID(TaskSummaryDto summary, string providerRequestID) => new()
    {
        ID = summary.ID,
        ProjectID = summary.ProjectID,
        Type = summary.Type,
        Status = summary.Status,
        Stage = summary.Stage,
        Progress = summary.Progress,
        Prompt = summary.Prompt,
        Operation = summary.Operation,
        Provider = summary.Provider,
        Model = summary.Model,
        ProviderRequestID = providerRequestID,
        ProviderCancelStatus = summary.ProviderCancelStatus,
        ProviderCancelError = summary.ProviderCancelError,
        ProviderCancelAttempts = summary.ProviderCancelAttempts,
        ProviderCancelRequestedAt = summary.ProviderCancelRequestedAt,
        ProviderCancelledAt = summary.ProviderCancelledAt,
        ErrorCode = summary.ErrorCode,
        PreviewURL = summary.PreviewURL,
        PreviewKind = summary.PreviewKind,
        PreviewPosterURL = summary.PreviewPosterURL,
        Attempts = summary.Attempts,
        StartedAt = summary.StartedAt,
        CompletedAt = summary.CompletedAt,
        CreatedAt = summary.CreatedAt,
        UpdatedAt = summary.UpdatedAt,
        Billing = summary.Billing,
        ClientContext = summary.ClientContext,
    };

    /// <summary>是否内容审核失败。对应 Go: <c>isContentModerationFailure</c>。</summary>
    private static bool IsContentModerationFailure(string value) =>
        value.ToLowerInvariant().Contains(ContentModerationErrorCode, StringComparison.Ordinal);

    /// <summary>海报地址查找。对应 Go: <c>findTaskMediaPoster</c>。</summary>
    private static string FindMediaPoster(object? value)
    {
        switch (value)
        {
            case List<object?> list:
                foreach (object? child in list)
                {
                    string poster = FindMediaPoster(child);
                    if (!string.IsNullOrEmpty(poster))
                    {
                        return poster;
                    }
                }
                break;

            case Dictionary<string, object?> map:
                foreach (string key in PosterKeys)
                {
                    if (!map.TryGetValue(key, out object? child))
                    {
                        continue;
                    }
                    (string previewURL, string previewKind) = FindMediaPreview(child, "image");
                    if (!string.IsNullOrEmpty(previewURL) && previewKind == "image")
                    {
                        return previewURL;
                    }
                }
                foreach (object? child in map.Values)
                {
                    string poster = FindMediaPoster(child);
                    if (!string.IsNullOrEmpty(poster))
                    {
                        return poster;
                    }
                }
                break;
        }
        return "";
    }

    /// <summary>首个可访问媒体地址查找。对应 Go: <c>findTaskMediaPreview</c>。</summary>
    private static (string PreviewURL, string PreviewKind) FindMediaPreview(object? value, string hint)
    {
        switch (value)
        {
            case string text:
            {
                text = text.Trim();
                bool isResourceFile = IsResourceFileURL(text);
                if (!isResourceFile
                    && !text.StartsWith("http://", StringComparison.Ordinal)
                    && !text.StartsWith("https://", StringComparison.Ordinal))
                {
                    return ("", "");
                }

                string kind = hint;
                string lower = text.ToLowerInvariant();
                if (lower.Contains(".mp4", StringComparison.Ordinal)
                    || lower.Contains(".webm", StringComparison.Ordinal)
                    || lower.Contains(".mov", StringComparison.Ordinal))
                {
                    kind = "video";
                }
                else if (kind != "video")
                {
                    kind = "image";
                }
                return (text, kind);
            }

            case List<object?> list:
                foreach (object? child in list)
                {
                    (string previewURL, string previewKind) = FindMediaPreview(child, hint);
                    if (!string.IsNullOrEmpty(previewURL))
                    {
                        return (previewURL, previewKind);
                    }
                }
                break;

            case Dictionary<string, object?> map:
                foreach (string key in PreviewKeys)
                {
                    if (!map.TryGetValue(key, out object? child))
                    {
                        continue;
                    }
                    string childHint = key switch
                    {
                        "video" => "video",
                        "images" or "image" => "image",
                        _ => hint,
                    };
                    (string previewURL, string previewKind) = FindMediaPreview(child, childHint);
                    if (!string.IsNullOrEmpty(previewURL))
                    {
                        return (previewURL, previewKind);
                    }
                }
                break;
        }
        return ("", "");
    }

    /// <summary>资源文件 URL 判定。对应 Go: <c>assets.IsFileURL</c>。</summary>
    private static bool IsResourceFileURL(string value) => ResourceIDFromFileURL(value).Length > 0;

    /// <summary>从 <c>/api/resources/&lt;id&gt;</c> 提取资源 ID。对应 Go: <c>assets.IDFromFileURL</c>。</summary>
    private static string ResourceIDFromFileURL(string value)
    {
        const string prefix = "/api/resources/";
        value = value.Trim();
        int index = value.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0)
        {
            return "";
        }

        string remainder = value[(index + prefix.Length)..];
        if (remainder.Length == 0)
        {
            return "";
        }

        int end = remainder.IndexOfAny(['/', '?', '#']);
        if (end >= 0)
        {
            remainder = remainder[..end];
        }
        return remainder;
    }

    /// <summary>按 rune 截断。对应 Go: <c>kernel.TruncateRunes</c>。</summary>
    private static string TruncateRunes(string value, int limit) =>
        Domain.Kernel.KernelUtil.TruncateRunes(value, limit);

    private static readonly string[] PosterKeys =
    [
        "posterUrl", "posterURL", "thumbnailUrl", "thumbnailURL",
        "coverUrl", "coverURL", "poster", "thumbnail", "cover",
    ];

    private static readonly string[] PreviewKeys =
    [
        "images", "image", "video", "dataUrl", "url", "resultUrl", "outputUrl",
    ];

    /// <summary>任务输入里的 metadata 片段。对应 Go 的内联匿名结构。</summary>
    private sealed class TaskMetadata
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("nodeId")]
        public string NodeID { get; set; } = "";

        [JsonPropertyName("conversationId")]
        public string ConversationID { get; set; } = "";

        [JsonPropertyName("messageId")]
        public string MessageID { get; set; } = "";

        [JsonPropertyName("batchIndex")]
        public int BatchIndex { get; set; }

        [JsonPropertyName("batchCount")]
        public int BatchCount { get; set; }

        [JsonPropertyName("domainProjectId")]
        public string DomainProjectID { get; set; } = "";

        [JsonPropertyName("chapterId")]
        public string ChapterID { get; set; } = "";

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = "";

        [JsonPropertyName("shotId")]
        public string ShotID { get; set; } = "";

        [JsonPropertyName("workflowStepId")]
        public string WorkflowStepID { get; set; } = "";

        [JsonPropertyName("artifactType")]
        public string ArtifactType { get; set; } = "";
    }
}
