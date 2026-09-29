#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>提交回执。对应 Go: <c>app.cloudAgentSubmissionReceipt</c>（struct 字段序）。</summary>
public sealed class CloudAgentSubmissionReceiptDto
{
    [JsonPropertyName("taskId")]
    public string TaskID { get; set; } = "";

    [JsonPropertyName("generationId")]
    [Domain.Serialization.GoOmitEmpty]
    public string GenerationID { get; set; } = "";

    [JsonPropertyName("recordedAt")]
    public DateTime RecordedAt { get; set; }
}

/// <summary>订单计费事实。对应 Go: <c>app.cloudAgentTaskBillingFacts</c>（struct 字段序）。</summary>
public sealed class CloudAgentTaskBillingFactsDto
{
    [JsonPropertyName("orderId")]
    public string OrderID { get; set; } = "";

    [JsonPropertyName("status")]
    [Domain.Serialization.GoOmitEmpty]
    public string Status { get; set; } = "";

    [JsonPropertyName("authorizedChargeMicrocredits")]
    public long AuthorizedChargeMicrocredits { get; set; }

    [JsonPropertyName("chargeLimitSet")]
    public bool ChargeLimitSet { get; set; }

    [JsonPropertyName("chargeLimitMicrocredits")]
    public long ChargeLimitMicrocredits { get; set; }

    [JsonPropertyName("reservedAmountMicrocredits")]
    [Domain.Serialization.GoOmitEmpty]
    public long ReservedAmountMicrocredits { get; set; }

    [JsonPropertyName("actualAmountMicrocredits")]
    [Domain.Serialization.GoOmitEmpty]
    public long ActualAmountMicrocredits { get; set; }

    [JsonPropertyName("refundedAmountMicrocredits")]
    [Domain.Serialization.GoOmitEmpty]
    public long RefundedAmountMicrocredits { get; set; }

    [JsonPropertyName("stateAvailable")]
    public bool StateAvailable { get; set; }
}

/// <summary>
/// 模型可见的任务事实投影。对应 Go: <c>app.cloudAgentTaskFacts</c>（struct 字段序）。
/// canvas 读取、task_get 与 fresh context frame 共用同一投影，不各自发明字符串契约。
/// </summary>
public static class CloudAgentTaskFacts
{
    /// <summary>按任务行组装事实（含订单计费事实回读）。对应 Go: <c>cloudAgentTaskDiagnostic</c>。</summary>
    public static Dictionary<string, JsonElement> Diagnostic(
        Repository repository, TaskEntity task, CancellationToken cancellationToken = default)
    {
        if (task is null)
        {
            return Minimal("unavailable");
        }
        var facts = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["taskId"] = JsonSerializer.SerializeToElement(task.ID),
            ["taskStatus"] = JsonSerializer.SerializeToElement(task.Status),
            ["taskType"] = JsonSerializer.SerializeToElement(task.Type),
            ["operation"] = JsonSerializer.SerializeToElement(task.Operation),
            ["agentRunId"] = JsonSerializer.SerializeToElement(task.AgentRunID),
            ["generationId"] = JsonSerializer.SerializeToElement(task.GenerationID),
            ["approvalId"] = JsonSerializer.SerializeToElement(task.ApprovalID),
            ["taskSubmitted"] = JsonSerializer.SerializeToElement(true),
            ["submissionOutcome"] = JsonSerializer.SerializeToElement(SubmissionOutcome(task)),
            ["cancellationSource"] = JsonSerializer.SerializeToElement(task.CancellationSource),
            ["cancellationRequestedAt"] = task.CancellationRequestedAt is null
                ? JsonSerializer.SerializeToElement<JsonElement?>(null)
                : JsonSerializer.SerializeToElement(task.CancellationRequestedAt.Value),
            ["submissionReceipt"] = JsonSerializer.SerializeToElement(new CloudAgentSubmissionReceiptDto
            {
                TaskID = task.ID,
                GenerationID = task.GenerationID,
                RecordedAt = task.CreatedAt,
            }, Domain.Serialization.GoJson.WriteOptions),
        };
        TaskClientContextDto? context = TaskOutput.ClientContext(task.InputJSON);
        if (context is not null && context.NodeID.Length > 0)
        {
            facts["nodeId"] = JsonSerializer.SerializeToElement(context.NodeID);
        }
        TaskExecutionDiagnosticDto? diagnostic = TaskExecutionDiagnostic(task);
        if (diagnostic is not null)
        {
            facts["taskSubmitted"] = JsonSerializer.SerializeToElement(diagnostic.TaskSubmitted);
            facts["submissionOutcome"] = JsonSerializer.SerializeToElement(diagnostic.SubmissionOutcome);
            if (diagnostic.Phase.Length > 0) facts["phase"] = JsonSerializer.SerializeToElement(diagnostic.Phase);
            if (diagnostic.Code.Length > 0) facts["diagnosticCode"] = JsonSerializer.SerializeToElement(diagnostic.Code);
            if (diagnostic.DiagnosticID.Length > 0) facts["diagnosticId"] = JsonSerializer.SerializeToElement(diagnostic.DiagnosticID);
            if (diagnostic.FieldPath.Length > 0) facts["fieldPath"] = JsonSerializer.SerializeToElement(diagnostic.FieldPath);
            if (diagnostic.RetryClass.Length > 0) facts["retryClass"] = JsonSerializer.SerializeToElement(diagnostic.RetryClass);
            if (diagnostic.SafeMessage.Length > 0) facts["error"] = JsonSerializer.SerializeToElement(diagnostic.SafeMessage);
            if (diagnostic.WritebackOutcome.Length > 0) facts["writebackOutcome"] = JsonSerializer.SerializeToElement(diagnostic.WritebackOutcome);
            if (diagnostic.WritebackReason.Length > 0) facts["writebackReason"] = JsonSerializer.SerializeToElement(diagnostic.WritebackReason);
            if (diagnostic.WritebackNodeID.Length > 0) facts["writebackNodeId"] = JsonSerializer.SerializeToElement(diagnostic.WritebackNodeID);
            if (diagnostic.WritebackMessage.Length > 0) facts["writebackError"] = JsonSerializer.SerializeToElement(diagnostic.WritebackMessage);
        }
        else if (task.Status is TaskStatus.TaskStatusFailed or TaskStatus.TaskStatusCancelled)
        {
            facts["error"] = JsonSerializer.SerializeToElement(SafeMediaTaskError(task.ResultJSON));
        }
        if (task.BillingOrderID.Length > 0)
        {
            var billing = new CloudAgentTaskBillingFactsDto
            {
                OrderID = task.BillingOrderID,
                AuthorizedChargeMicrocredits = task.AuthorizedChargeMicrocredits,
            };
            BillingOrder? order = repository.BillingOrderAsync(task.BillingOrderID, cancellationToken).ConfigureAwait(false)
                .GetAwaiter().GetResult();
            if (order is not null && order.UserID == task.UserID
                && (order.TaskID.Length == 0 || order.TaskID == task.ID))
            {
                billing.Status = order.Status;
                billing.ChargeLimitSet = order.ChargeLimitSet;
                billing.ChargeLimitMicrocredits = order.ChargeLimitMicrocredits;
                billing.ReservedAmountMicrocredits = order.ReservedAmountMicrocredits;
                billing.ActualAmountMicrocredits = order.ActualAmountMicrocredits;
                billing.RefundedAmountMicrocredits = order.RefundedAmountMicrocredits;
                billing.StateAvailable = true;
            }
            facts["billing"] = JsonSerializer.SerializeToElement(billing, Domain.Serialization.GoJson.WriteOptions);
        }
        return facts;
    }

    /// <summary>投影为模型可见的 JsonObject。</summary>
    public static System.Text.Json.Nodes.JsonObject ToJson(Repository repository, TaskEntity task)
    {
        Dictionary<string, JsonElement> facts = Diagnostic(repository, task, CancellationToken.None);
        JsonObject node = [];
        foreach ((string key, JsonElement value) in facts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            node[key] = JsonNode.Parse(value.GetRawText());
        }
        return node;
    }

    /// <summary>空任务行的最小事实。对应 Go 的 unavailable 分支。</summary>
    public static Dictionary<string, JsonElement> Minimal(string status)
    {
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal)
        {
            ["taskStatus"] = JsonSerializer.SerializeToElement(status),
            ["taskSubmitted"] = JsonSerializer.SerializeToElement(false),
            ["submissionOutcome"] = JsonSerializer.SerializeToElement("unavailable"),
        };
        if (status != "unavailable")
        {
            result["taskId"] = JsonSerializer.SerializeToElement("");
        }
        return result;
    }

    private static Dictionary<string, JsonElement>? TaskClientContext(string inputJSON)
    {
        if (inputJSON.Trim().Length == 0)
        {
            return null;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(inputJSON);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("metadata", out JsonElement metadata)
                || metadata.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in metadata.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>读取执行诊断。对应 Go: <c>taskExecutionDiagnostic</c>。</summary>
    public static TaskExecutionDiagnosticDto? TaskExecutionDiagnostic(TaskEntity task)
    {
        if (task.ExecutionDiagnosticJSON.Length == 0)
        {
            return null;
        }
        try
        {
            TaskExecutionDiagnosticDto? diagnostic = JsonSerializer.Deserialize<TaskExecutionDiagnosticDto>(
                task.ExecutionDiagnosticJSON, Domain.Serialization.GoJson.ReadOptions);
            if (diagnostic is not null && task.Status == TaskStatus.TaskStatusSucceeded)
            {
                diagnostic.SafeMessage = "";
            }
            return diagnostic;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>提交结果。对应 Go: <c>cloudAgentTaskSubmissionOutcome</c>。</summary>
    public static string SubmissionOutcome(TaskEntity task)
    {
        if (task.ProviderRequestID.Length > 0)
        {
            return "accepted";
        }
        return task.Status switch
        {
            TaskStatus.TaskStatusQueued => "queued",
            TaskStatus.TaskStatusRunning => "submitted",
            _ => "unknown",
        };
    }

    /// <summary>安全任务错误文案。对应 Go: <c>cloudAgentSafeMediaTaskError(task)</c> 的 task.Error 读取。</summary>
    private static string SafeMediaTaskError(string resultJSON)
    {
        if (string.IsNullOrWhiteSpace(resultJSON))
        {
            return "";
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(resultJSON);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("error", out JsonElement error)
                   && error.ValueKind == JsonValueKind.String
                ? error.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>取消链诊断写入。对应 Go: <c>recordTaskDiagnostic(task, "cancellation", ...)</c>。</summary>
    public static string CancellationDiagnostic(TaskEntity task, string originalStatus)
    {
        bool notSent = originalStatus == TaskStatus.TaskStatusQueued;
        string outcome = SubmissionOutcome(task, notSent);
        var diagnostic = new TaskExecutionDiagnosticDto
        {
            Code = "task_cancelled",
            Phase = "cancellation",
            TaskSubmitted = outcome is "accepted" or "submitted",
            SubmissionOutcome = outcome is "accepted" or "submitted" ? "submitted" : "not_submitted",
            RetryClass = "reconcile_cancellation",
            DiagnosticID = task.ID,
        };
        return JsonSerializer.Serialize(diagnostic, Domain.Serialization.GoJson.WriteOptions);
    }

    /// <summary>执行失败诊断写入。对应 Go: <c>recordTaskDiagnostic(task, "execution", "provider_execution_failed", ...)</c>。</summary>
    public static string ExecutionFailureDiagnostic(
        TaskEntity task, string outcome, string retryClass, Exception cause, string appErrorReason)
    {
        string code = appErrorReason.Length > 0 ? appErrorReason : "provider_execution_failed";
        var diagnostic = new TaskExecutionDiagnosticDto
        {
            Code = code,
            Phase = "execution",
            TaskSubmitted = outcome is "accepted" or "submitted",
            SubmissionOutcome = outcome,
            RetryClass = retryClass,
            SafeMessage = SafeDiagnosticMessage(task.Error),
            DiagnosticID = task.ID,
        };
        return JsonSerializer.Serialize(diagnostic, Domain.Serialization.GoJson.WriteOptions);
    }

    /// <summary>取消链的提交结果。对应 Go: <c>taskSubmissionOutcome(task, notSent)</c>。</summary>
    public static string SubmissionOutcome(TaskEntity task, bool notSent)
    {
        if (notSent)
        {
            return "not_submitted";
        }
        return SubmissionOutcome(task);
    }

    /// <summary>安全诊断文案：截断 240 rune 并剔除敏感标记。对应 Go: <c>safeTaskDiagnosticMessage</c>。</summary>
    public static string SafeDiagnosticMessage(string? message)
    {
        string text = (message ?? "").Trim();
        if (text.Length == 0 || text.AsSpan().ContainsAny("\r\n\u0000"))
        {
            return "任务未完成，请在任务中心查看诊断";
        }
        string lower = text.ToLowerInvariant();
        foreach (string marker in new[]
                 {
                     "http://", "https://", "file://", "ftp://", "authorization", "cookie", "secret",
                     "token", "api_key", "apikey", "x-api-key", "/var/", "/tmp/", "\\", "stack trace",
                     "traceback", "sql:", "sqlite", "postgres",
                 })
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
            {
                return "任务未完成，请在任务中心查看诊断";
            }
        }
        return CloudAgentContracts.TruncateRunes(text, 240);
    }
}

/// <summary>执行诊断。对应 Go: <c>model.TaskExecutionDiagnostic</c>（JSON 契约一致）。</summary>
public sealed class TaskExecutionDiagnosticDto
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "";

    [JsonPropertyName("fieldPath")]
    [Domain.Serialization.GoOmitEmpty]
    public string FieldPath { get; set; } = "";

    [JsonPropertyName("taskSubmitted")]
    public bool TaskSubmitted { get; set; }

    [JsonPropertyName("submissionOutcome")]
    public string SubmissionOutcome { get; set; } = "";

    [JsonPropertyName("retryClass")]
    public string RetryClass { get; set; } = "";

    [JsonPropertyName("safeMessage")]
    public string SafeMessage { get; set; } = "";

    [JsonPropertyName("diagnosticId")]
    public string DiagnosticID { get; set; } = "";

    [JsonPropertyName("writebackOutcome")]
    [Domain.Serialization.GoOmitEmpty]
    public string WritebackOutcome { get; set; } = "";

    [JsonPropertyName("writebackReason")]
    [Domain.Serialization.GoOmitEmpty]
    public string WritebackReason { get; set; } = "";

    [JsonPropertyName("writebackNodeId")]
    [Domain.Serialization.GoOmitEmpty]
    public string WritebackNodeID { get; set; } = "";

    [JsonPropertyName("writebackMessage")]
    [Domain.Serialization.GoOmitEmpty]
    public string WritebackMessage { get; set; } = "";
}
