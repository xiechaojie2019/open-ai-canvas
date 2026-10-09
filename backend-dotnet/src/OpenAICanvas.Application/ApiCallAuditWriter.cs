#nullable enable
using System.Text;
using System.Text.Json;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;
using OpenAICanvas.Providers;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application;

/// <summary>
/// 任务链路的调用审计写入器：把每次 Provider 出站请求落为 <c>api_call_logs</c> 行。
/// 对应 Go: <c>recordProviderRequest</c>（provider_http_client.go）+ <c>LogAPICall</c>
/// （admin.go）+ <c>EnrichAPICallLog</c>（analytics.go）+ <c>ensureFailedProviderAttemptLogged</c>
/// （task_api_call_log.go）。
/// </summary>
/// <remarks>
/// 写入失败只影响计费状态转「待核对」（与 Go 一致），绝不影响上游调用与任务结果。
/// 与 Go 的差异：不写任务的 pollStage/nextPollAt（.NET 轮询在任务执行内阻塞进行），
/// 不做图片/视频的观测性进度同步（ProviderProgress 同步链路未移植）。
/// </remarks>
public sealed class ApiCallAuditWriter
{
    private readonly Repository _repository;
    private readonly IRuntimePolicyProvider _policy;

    /// <summary>配额检查与写入串行化。对应 Go: <c>Service.storageMu</c>。</summary>
    private static readonly SemaphoreSlim StorageGate = new(1, 1);

    public ApiCallAuditWriter(Repository repository, IRuntimePolicyProvider policy)
    {
        _repository = repository;
        _policy = policy;
    }

    // ------------------------------------------------------------- 上游调用观测

    /// <summary>
    /// 记录一次上游调用。对应 Go: <c>recordProviderRequest</c>。
    /// </summary>
    public async Task RecordAsync(
        ProviderCallAudit audit, ProviderCallObservation observation, CancellationToken cancellationToken = default)
    {
        (string status, string errorCode, string errorText) = Classify(observation, out bool slotFailure);
        ApiCallLog log = BuildLog(audit, observation, status, errorCode, errorText);
        // 与 Go 一致：富化（用量/上游请求 ID/媒体数/失败摘要）先于落库管线。
        Enrich(log, observation.ResponseBody);
        try
        {
            await LogAsync(log, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // 与 Go 一致：日志写失败（含配额超限）把订单转待核对，交给对账流程继续处理。
            // 渠道槽位失败意味着请求根本没发出去，不存在需要核对的费用。
            if (!slotFailure && audit.BillingOrderID.Length > 0)
            {
                try
                {
                    await _repository.MarkBillingUncertainAsync(
                        audit.BillingOrderID, "上游调用日志写入失败，费用状态待核对", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception uncertainError)
                {
                    Console.Error.WriteLine(
                        $"provider billing uncertainty update failed: task_id={audit.TaskID} error={uncertainError.Message}");
                }
            }
            Console.Error.WriteLine(
                $"provider call log write failed: task_id={audit.TaskID} error={error.GetType().Name} {error.Message}");
        }
    }

    private static ApiCallLog BuildLog(
        ProviderCallAudit audit, ProviderCallObservation observation,
        string status, string errorCode, string errorText)
    {
        string requestKind = audit.RequestKindOverride.Length > 0
            ? audit.RequestKindOverride
            : ProviderCallAuditLog.RequestKindFor(observation.Method, observation.Path);
        string model = audit.Model;
        int videoSeconds = audit.VideoSeconds;
        if (requestKind == "create" && audit.Capability == "video")
        {
            // 与 Go 一致：上游在创建响应里不返回时长时按默认档估算（Seedance 5s，其余 6s）。
            if (videoSeconds <= 0)
            {
                bool seedance = model.Contains("seedance", StringComparison.OrdinalIgnoreCase)
                    || observation.Path.Contains("/contents/generations/tasks", StringComparison.Ordinal);
                videoSeconds = seedance ? 5 : 6;
            }
        }
        return new ApiCallLog
        {
            UserID = audit.UserID,
            TraceID = audit.TraceID,
            RequestID = audit.RequestID,
            ChannelID = audit.ChannelID,
            TaskID = audit.TaskID,
            BillingOrderID = audit.BillingOrderID,
            Source = "backend-task",
            Capability = audit.Capability,
            Operation = audit.Operation,
            RequestKind = requestKind,
            Billable = string.Equals(observation.Method, "POST", StringComparison.OrdinalIgnoreCase)
                && requestKind != "cancel",
            APIFormat = observation.APIFormat,
            Method = observation.Method,
            Path = observation.Path,
            Model = model,
            Status = status,
            StatusCode = observation.StatusCode,
            DurationMs = Math.Max(0, observation.DurationMs),
            ErrorCode = errorCode,
            Error = errorText,
            UpstreamURL = observation.UpstreamURL,
            RequestContentType = observation.RequestContentType,
            RequestBody = ApiCallPayload.Sanitize(observation.RequestBody, observation.RequestContentType),
            ResponseBody = ApiCallPayload.Sanitize(observation.ResponseBody, ""),
            VideoSeconds = videoSeconds,
        };
    }

    /// <summary>失败归类。对应 Go: <c>providerRequestErrorDetails</c> + 业务失败判定 + 槽位错误覆盖。</summary>
    private static (string Status, string ErrorCode, string ErrorText) Classify(
        ProviderCallObservation observation, out bool slotFailure)
    {
        slotFailure = false;
        string status = ApiCallStatus.ApiCallStatusSucceeded;
        string errorCode = "";
        string errorText = "";
        if (observation.Failure is not null
            || observation.StatusCode < 200 || observation.StatusCode >= 300)
        {
            status = ApiCallStatus.ApiCallStatusFailed;
            (errorCode, errorText) = RequestErrorDetails(observation.Failure);
        }
        else if (BusinessFailure(observation.ResponseBody) is (string code, string message, true))
        {
            status = ApiCallStatus.ApiCallStatusFailed;
            errorCode = code;
            errorText = message;
        }
        if (observation.Failure is ProviderChannelSlotException slotError)
        {
            slotFailure = true;
            errorCode = slotError.Code;
            errorText = slotError.Message;
        }
        return (status, errorCode, errorText);
    }

    /// <summary>对应 Go: <c>providerRequestErrorDetails</c> + <c>safeProviderLogError</c>。</summary>
    private static (string Code, string Text) RequestErrorDetails(Exception? error)
    {
        if (error is null)
        {
            return ("", "");
        }
        if (error is OperationCanceledException)
        {
            return ("request_cancelled", "任务取消，中断上游请求");
        }
        if (error is TimeoutException)
        {
            return ("upstream_timeout", "等待上游响应超时");
        }
        if (error is ProviderHttpException httpError)
        {
            return ("", $"上游 HTTP {httpError.StatusCode}");
        }
        return ("", KernelUtil.TruncateRunes(error.Message, 500));
    }

    // ------------------------------------------------------------- LogAPICall

    /// <summary>落库管线。对应 Go: <c>Service.LogAPICall</c> 的执行顺序。</summary>
    private async Task LogAsync(ApiCallLog log, CancellationToken cancellationToken)
    {
        if (log.ID.Length == 0)
        {
            log.ID = IdGenerator.NewId();
        }
        log.CreatedAt = DateTime.UtcNow;
        if (log.StartedAt == default)
        {
            log.StartedAt = log.CreatedAt.AddMilliseconds(-Math.Max(log.DurationMs, 0));
        }
        await EstimateCallCostAsync(log, cancellationToken).ConfigureAwait(false);
        if (log.BillingOrderID.Length > 0 && log.ProviderRequestID.Length > 0)
        {
            try
            {
                // 账单关联是请求日志的辅助状态，写失败不能丢已发生的上游调用记录。
                await _repository.UpdateBillingProviderRequestIDAsync(
                    log.BillingOrderID, log.ProviderRequestID, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"provider billing request id update failed: billing_order_id={log.BillingOrderID} error={error.Message}");
            }
        }
        if (log.TaskID.Length > 0
            && log.RequestKind is "create" or "poll" or "cancel")
        {
            try
            {
                await _repository.UpdateTaskProviderRequestIDAsync(
                    log.TaskID, log.ProviderRequestID, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"provider task state update failed: task_id={log.TaskID} error={error.Message}");
            }
        }
        if (await MergeVideoApiCallLogAsync(log, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        await StorageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeResourcePolicy resource = _policy.Current().Resource;
            UserStorageUsage usage = await _repository
                .UserStorageUsageAsync(log.UserID, cancellationToken).ConfigureAwait(false);
            if (usage.ApiCallCount >= resource.ApiCallLogCount)
            {
                throw AppError.QuotaExceeded(
                    $"账号上游请求日志已达到 {resource.ApiCallLogCount} 条上限，请联系管理员归档");
            }
            long incomingBytes = 0;
            foreach (string part in new[]
            {
                log.Path, log.Model, log.ProviderRequestID, log.ErrorCode, log.Error,
                log.UpstreamURL, log.RequestContentType, log.RequestBody, log.ResponseBody,
            })
            {
                incomingBytes += Encoding.UTF8.GetByteCount(part);
            }
            long taskDataLimit = resource.TaskDataGB * (1L << 30);
            if (usage.TaskBytes + incomingBytes > taskDataLimit)
            {
                throw AppError.QuotaExceeded(
                    $"账号任务历史数据已达到 {resource.TaskDataGB}GB 上限，请联系管理员归档");
            }
            await _repository.CreateApiCallLogAsync(log, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StorageGate.Release();
        }
    }

    private async Task EstimateCallCostAsync(ApiCallLog log, CancellationToken cancellationToken)
    {
        if (log.Status == ApiCallStatus.ApiCallStatusFailed && !log.UsageAvailable)
        {
            return;
        }
        ModelPricing? pricing = await _repository.ModelPricingAsync(
            log.ChannelID, log.Model, log.Capability, cancellationToken).ConfigureAwait(false);
        if (pricing is null)
        {
            return;
        }
        long cost = log.Billable ? pricing.PerRequestMicros : 0;
        cost += log.InputTokens * pricing.InputPerMillionMicros / 1_000_000;
        cost += log.OutputTokens * pricing.OutputPerMillionMicros / 1_000_000;
        cost += log.CachedTokens * pricing.CachedPerMillionMicros / 1_000_000;
        cost += log.MediaCount * pricing.PerMediaMicros;
        cost += log.VideoSeconds * pricing.PerVideoSecondMicros;
        log.EstimatedCostMicros = cost;
        log.CostAvailable = true;
        log.Currency = pricing.Currency;
    }

    /// <summary>
    /// 视频 poll 合并回 create 根行：下载失败不改写已成功的生成请求。
    /// 对应 Go: <c>mergeVideoAPICallLog</c>。
    /// </summary>
    private async Task<bool> MergeVideoApiCallLogAsync(ApiCallLog log, CancellationToken cancellationToken)
    {
        if (log.Capability != "video" || log.RequestKind != "poll")
        {
            return false;
        }
        if (log.TaskID.Length == 0 && log.ProviderRequestID.Length == 0)
        {
            return false;
        }
        ApiCallLog? root = await _repository.VideoApiCallRootAsync(
            log.TaskID, log.UserID, log.ChannelID, log.ProviderRequestID, cancellationToken)
            .ConfigureAwait(false);
        if (root is null)
        {
            return false;
        }
        root.PollCount++;
        if (log.ResponseBody.Length > 0)
        {
            root.ResponseBody = log.ResponseBody;
        }
        if (log.ProviderRequestID.Length > 0)
        {
            root.ProviderRequestID = log.ProviderRequestID;
        }
        if (log.ProviderStatus.Length > 0)
        {
            root.ProviderStatus = log.ProviderStatus;
        }
        DateTime startedAt = root.StartedAt;
        if (startedAt == default)
        {
            startedAt = root.CreatedAt.AddMilliseconds(-Math.Max(root.DurationMs, 0));
            root.StartedAt = startedAt;
        }
        root.DurationMs = Math.Max(root.DurationMs, (long)(log.CreatedAt - startedAt).TotalMilliseconds);
        root.StatusCode = log.StatusCode;
        root.ConcurrencyLimit = log.ConcurrencyLimit;
        if (log.Status == ApiCallStatus.ApiCallStatusFailed)
        {
            root.Status = log.Status;
            root.ErrorCode = log.ErrorCode;
            root.Error = log.Error;
        }
        else
        {
            root.Status = ApiCallStatus.ApiCallStatusSucceeded;
            root.ErrorCode = "";
            root.Error = "";
        }
        if (log.UsageAvailable)
        {
            root.UsageAvailable = true;
            root.InputTokens = log.InputTokens;
            root.OutputTokens = log.OutputTokens;
            root.CachedTokens = log.CachedTokens;
        }
        await _repository.SaveApiCallLogAsync(root, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------- 失败兜底

    /// <summary>
    /// 任务失败且完全无调用日志时补一条兜底记录（预检失败通常没有出站请求）。
    /// 对应 Go: <c>ensureFailedProviderAttemptLogged</c>。
    /// </summary>
    public async Task EnsureFailedAttemptLoggedAsync(
        TaskEntity task, Exception? error, CancellationToken cancellationToken = default)
    {
        if (error is null || task.ID.Trim().Length == 0)
        {
            return;
        }
        if (await _repository.HasApiCallLogForTaskAsync(task.ID, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        DateTime now = DateTime.UtcNow;
        DateTime startedAt = task.StartedAt is { } started && started != default ? started : now;
        string path = "/task/provider-preflight";
        string errorCode = "request_not_sent";
        int statusCode = 0;
        if (error is ProviderHttpException httpError)
        {
            path = "/task/provider-request";
            errorCode = "provider_request_unlogged";
            statusCode = httpError.StatusCode;
        }
        else if (task.ProviderRequestID.Trim().Length > 0)
        {
            path = "/task/provider-state";
            errorCode = "provider_state_unlogged";
        }
        ApiCallLog log = new()
        {
            UserID = task.UserID,
            TraceID = task.TraceID,
            RequestID = task.RequestID,
            TaskID = task.ID,
            BillingOrderID = task.BillingOrderID,
            Source = "backend-task",
            Capability = CapabilityFromTaskType(task.Type),
            Operation = task.Operation,
            RequestKind = "create",
            Billable = false,
            APIFormat = "internal",
            Method = "INTERNAL",
            Path = path,
            Model = task.Model,
            Status = ApiCallStatus.ApiCallStatusFailed,
            StatusCode = statusCode,
            DurationMs = Math.Max(0, (long)(now - startedAt).TotalMilliseconds),
            ProviderRequestID = task.ProviderRequestID,
            ErrorCode = errorCode,
            Error = KernelUtil.TruncateRunes(ProviderErrorMessages.UserFacing(error), 2_000),
            StartedAt = startedAt,
            CreatedAt = now,
        };
        try
        {
            await LogAsync(log, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception logError)
        {
            Console.Error.WriteLine(
                $"provider attempt log write failed: task_id={task.ID} error={logError.Message}");
        }
    }

    // ------------------------------------------------------------- 富化

    /// <summary>从响应正文提取用量、上游请求 ID、上游状态与媒体数。对应 Go: <c>EnrichAPICallLog</c>。</summary>
    private static void Enrich(ApiCallLog log, byte[] responseBody)
    {
        if (log.ProviderRequestID.Trim().Length == 0)
        {
            log.ProviderRequestID = ProviderRequestIDFromPath(log.Path);
        }
        foreach (JsonElement payload in ResponsePayloads(responseBody))
        {
            EnrichPayload(log, payload);
        }
        EnrichFailureSummary(log, responseBody);
    }

    private static void EnrichPayload(ApiCallLog log, JsonElement payload)
    {
        string nestedTaskID = "";
        if (payload.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object)
        {
            if (log.Capability == "video" && log.Path.Contains("/v1/video/generations", StringComparison.Ordinal))
            {
                nestedTaskID = FirstString(data, "task_id", "taskId") ?? "";
            }
        }
        bool failed = log.Status == ApiCallStatus.ApiCallStatusFailed;
        PayloadView view = new(payload);

        if (failed)
        {
            (string code, string message) = FailureDetails(view);
            log.ErrorCode = code;
            if (message.Length > 0)
            {
                log.Error = message;
            }
        }

        bool arkVideo = log.Capability == "video"
            && log.Path.Contains("/contents/generations/tasks", StringComparison.Ordinal);
        if (log.Capability == "video")
        {
            log.InputTokens = 0;
            log.CachedTokens = 0;
            (log.OutputTokens, log.UsageAvailable) = VideoCompletionTokens(view, arkVideo);
        }
        else if (view.TryGet("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
        {
            long inputTokens = FirstInt64(usage, out bool inputAvailable, "input_tokens", "prompt_tokens");
            long outputTokens = FirstInt64(usage, out bool outputAvailable, "output_tokens", "completion_tokens");
            if (inputAvailable)
            {
                log.InputTokens = inputTokens;
            }
            if (outputAvailable)
            {
                log.OutputTokens = outputTokens;
            }
            if (inputAvailable || outputAvailable)
            {
                log.UsageAvailable = true;
            }
            if (usage.TryGetProperty("input_tokens_details", out JsonElement inputDetails)
                && inputDetails.ValueKind == JsonValueKind.Object)
            {
                log.CachedTokens = FirstInt64(inputDetails, out _, "cached_tokens", "cache_read_input_tokens");
            }
            if (usage.TryGetProperty("prompt_tokens_details", out JsonElement promptDetails)
                && promptDetails.ValueKind == JsonValueKind.Object && log.CachedTokens == 0)
            {
                log.CachedTokens = FirstInt64(promptDetails, out _, "cached_tokens", "cache_read_input_tokens");
            }
            if (log.CachedTokens == 0)
            {
                log.CachedTokens = FirstInt64(usage, out _, "cached_tokens", "cache_read_input_tokens", "prompt_cache_hit_tokens");
            }
        }
        if (!log.Capability.Equals("video", StringComparison.Ordinal)
            && view.TryGet("usageMetadata", out JsonElement usageMetadata)
            && usageMetadata.ValueKind == JsonValueKind.Object)
        {
            long inputTokens = FirstInt64(usageMetadata, out bool inputAvailable, "promptTokenCount");
            long outputTokens = FirstInt64(usageMetadata, out bool outputAvailable, "candidatesTokenCount");
            if (inputAvailable)
            {
                log.InputTokens = inputTokens;
            }
            if (outputAvailable)
            {
                log.OutputTokens = outputTokens;
            }
            if (inputAvailable || outputAvailable)
            {
                log.UsageAvailable = true;
            }
            log.CachedTokens = FirstInt64(usageMetadata, out _, "cachedContentTokenCount");
        }

        string? extracted = FirstString(payload, "task_id", "id", "request_id", "name");
        log.ProviderRequestID = FirstNonEmpty(nestedTaskID, extracted ?? "", log.ProviderRequestID);
        string providerStatus = (view.TryGet("status", out JsonElement statusValue)
                && statusValue.ValueKind == JsonValueKind.String
                ? statusValue.GetString() ?? ""
                : "").Trim().ToLowerInvariant();
        log.ProviderStatus = FirstNonEmpty(providerStatus, log.ProviderStatus).ToLowerInvariant();
        if (log.ProviderStatus is "failed" or "cancelled" or "expired")
        {
            log.Status = ApiCallStatus.ApiCallStatusFailed;
            (string errorCode, string errorMessage) = FailureDetails(view);
            log.ErrorCode = FirstNonEmpty(errorCode, log.ErrorCode);
            log.Error = FirstNonEmpty(errorMessage, log.Error);
        }
        if (log.Capability == "image")
        {
            if (payload.TryGetProperty("data", out JsonElement imageData)
                && imageData.ValueKind == JsonValueKind.Array)
            {
                log.MediaCount = imageData.GetArrayLength();
            }
            else if (payload.TryGetProperty("images", out JsonElement images)
                && images.ValueKind == JsonValueKind.Array)
            {
                log.MediaCount = images.GetArrayLength();
            }
        }
    }

    /// <summary>失败摘要并保留安全上游详情。对应 Go: <c>enrichAPICallLogFailureSummary</c>。</summary>
    private static void EnrichFailureSummary(ApiCallLog log, byte[] responseBody)
    {
        if (log.Status != ApiCallStatus.ApiCallStatusFailed || log.StatusCode < 400)
        {
            return;
        }
        string body = Encoding.UTF8.GetString(responseBody);
        int statusCode = (int)Math.Min(log.StatusCode, int.MaxValue);
        string userMessage = statusCode is 400 or 422
            ? ProviderErrorMessages.WithDetail(ProviderHttpException.BuildMessage(statusCode), body)
            : ProviderErrorMessages.AppendDetail(ProviderHttpException.BuildMessage(statusCode), body);
        string detail = log.Error.Trim();
        if (detail.Length == 0
            || detail == userMessage
            || userMessage.Contains("；上游：" + detail, StringComparison.Ordinal))
        {
            log.Error = userMessage;
            return;
        }
        if (detail.Contains(userMessage, StringComparison.Ordinal))
        {
            return;
        }
        log.Error = KernelUtil.TruncateRunes(userMessage + "；上游：" + detail, 2_000);
    }

    /// <summary>
    /// 响应载荷拆分：整体是 JSON 对象则单载荷，否则按 SSE data 行拆分。
    /// 对应 Go: <c>providerResponsePayloads</c>。
    /// </summary>
    private static List<JsonElement> ResponsePayloads(byte[] responseBody)
    {
        List<JsonElement> result = [];
        if (responseBody.Length == 0)
        {
            return result;
        }
        if (TryParseObject(responseBody, out JsonElement single))
        {
            result.Add(single);
            return result;
        }
        List<string> dataLines = [];
        void Flush()
        {
            string raw = string.Join("\n", dataLines).Trim();
            dataLines.Clear();
            if (raw.Length == 0 || raw == "[DONE]")
            {
                return;
            }
            if (TryParseObject(Encoding.UTF8.GetBytes(raw), out JsonElement payload))
            {
                result.Add(payload);
            }
        }
        foreach (string rawLine in Encoding.UTF8.GetString(responseBody).Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                string value = line["data:".Length..];
                dataLines.Add(value.StartsWith(' ') ? value[1..] : value);
            }
        }
        Flush();
        return result;
    }

    private static bool TryParseObject(byte[] data, out JsonElement payload)
    {
        payload = default;
        try
        {
            using JsonDocument document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>载荷读取视图：顶层优先，缺失时回落 data/response 内层（Go 的合并语义）。</summary>
    private readonly struct PayloadView
    {
        private readonly JsonElement _root;
        private readonly JsonElement _data;
        private readonly JsonElement _response;

        public PayloadView(JsonElement root)
        {
            _root = root;
            if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object)
            {
                _data = data;
            }
            if (root.TryGetProperty("response", out JsonElement response) && response.ValueKind == JsonValueKind.Object)
            {
                _response = response;
            }
        }

        public bool TryGet(string name, out JsonElement value)
        {
            if (_root.TryGetProperty(name, out value))
            {
                return true;
            }
            if (_data.ValueKind != JsonValueKind.Undefined && _data.TryGetProperty(name, out value))
            {
                return true;
            }
            if (_response.ValueKind != JsonValueKind.Undefined && _response.TryGetProperty(name, out value))
            {
                return true;
            }
            return false;
        }
    }

    /// <summary>对应 Go: <c>videoCompletionTokens</c>。completion_tokens 一旦返回就是唯一结算依据。</summary>
    private static (long Tokens, bool Available) VideoCompletionTokens(PayloadView payload, bool arkProtocol)
    {
        if (payload.TryGet("status", out JsonElement status))
        {
            string text = status.ValueKind == JsonValueKind.String ? status.GetString() ?? "" : "";
            string normalized = text.Trim().ToLowerInvariant();
            bool terminal = normalized is "succeeded" or "completed" or "success";
            if (!terminal && (arkProtocol || normalized is not ("completed" or "success")))
            {
                return (0, false);
            }
        }
        if (!payload.TryGet("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return (0, false);
        }
        string[] candidates = arkProtocol
            ? ["completion_tokens"]
            : ["completion_tokens", "output_tokens"];
        JsonElement value = default;
        bool exists = false;
        foreach (string candidate in candidates)
        {
            if (usage.TryGetProperty(candidate, out value))
            {
                exists = true;
                break;
            }
        }
        if (!exists && usage.TryGetProperty("total_tokens", out value))
        {
            exists = true;
        }
        if (!exists || value.ValueKind != JsonValueKind.Number)
        {
            return (0, false);
        }
        if (!value.TryGetInt64(out long tokens) || tokens <= 0)
        {
            return (0, false);
        }
        return (tokens, true);
    }

    /// <summary>对应 Go: <c>providerFailureDetails</c>。内层通常是供应商业务错误，外层 code 可能只是包装码。</summary>
    private static (string Code, string Message) FailureDetails(PayloadView payload)
    {
        string code = "";
        string message = "";
        foreach (PayloadView candidate in CandidateViews(payload))
        {
            if (code.Length == 0)
            {
                code = NormalizedErrorCode(candidate, "code");
            }
            if (message.Length == 0)
            {
                message = StringField(candidate, "message");
                if (message.Length == 0)
                {
                    message = StringField(candidate, "msg");
                }
                message = message.Trim();
            }
        }
        return (code, KernelUtil.TruncateRunes(message, 500));
    }

    private static List<PayloadView> CandidateViews(PayloadView payload)
    {
        List<PayloadView> candidates = [];
        if (payload.TryGet("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
        {
            candidates.Add(new PayloadView(error));
        }
        if (payload.TryGet("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object)
        {
            candidates.Add(new PayloadView(data));
        }
        candidates.Add(payload);
        return candidates;
    }

    /// <summary>对应 Go: <c>normalizedProviderErrorCode</c>。"0" 视为无错误码。</summary>
    private static string NormalizedErrorCode(PayloadView payload, string name)
    {
        if (!payload.TryGet(name, out JsonElement value))
        {
            return "";
        }
        string code = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        };
        code = code.Trim();
        return code == "0" ? "" : KernelUtil.TruncateRunes(code, 80);
    }

    /// <summary>
    /// 上游 2xx 响应体的业务失败判定。对应 Go: <c>providerResponseBusinessFailure</c>
    /// （含 DashScope 的 output 内层）。
    /// </summary>
    private static (string Code, string Message, bool Failed)? BusinessFailure(byte[] responseBody)
    {
        if (!TryParseObject(responseBody, out JsonElement payload))
        {
            return null;
        }
        (string code, string message, bool failed) = BusinessFailureOf(new PayloadView(payload));
        if (failed)
        {
            return (code, message, true);
        }
        if (payload.TryGetProperty("output", out JsonElement output) && output.ValueKind == JsonValueKind.Object)
        {
            (code, message, failed) = BusinessFailureOf(new PayloadView(output));
            if (failed)
            {
                return (code, message, true);
            }
        }
        return null;
    }

    private static (string Code, string Message, bool Failed) BusinessFailureOf(PayloadView payload)
    {
        if (payload.TryGet("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
        {
            (string code, string message) = FailureDetails(new PayloadView(error));
            if (code.Length > 0 || message.Length > 0)
            {
                return (code, message, true);
            }
        }
        string status = NormalizedErrorCode(payload, "code").ToLowerInvariant();
        if (status.Length > 0
            && status is not ("0" or "success" or "succeeded" or "ok" or "<nil>"))
        {
            (string code, string message) = FailureDetails(payload);
            return (code, message, true);
        }
        string taskStatus = StringField(payload, "task_status").Trim().ToLowerInvariant();
        switch (taskStatus)
        {
            case "failed" or "failure" or "error" or "expired":
                (string code, string message) = FailureDetails(payload);
                return (code.Length > 0 ? code : "task_failed", message, true);
        }
        return ("", "", false);
    }

    private static string StringField(PayloadView payload, string name) =>
        payload.TryGet(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string? FirstString(JsonElement payload, params string[] names)
    {
        foreach (string name in names)
        {
            if (payload.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString() ?? "";
                if (text.Trim().Length > 0)
                {
                    return text;
                }
            }
        }
        return null;
    }

    private static long FirstInt64(JsonElement payload, out bool available, params string[] names)
    {
        foreach (string name in names)
        {
            if (payload.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetInt64(out long parsed))
                {
                    available = true;
                    return parsed;
                }
            }
        }
        available = false;
        return 0;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (value.Trim().Length > 0)
            {
                return value;
            }
        }
        return "";
    }

    /// <summary>对应 Go: <c>providerRequestIDFromPath</c>。</summary>
    private static string ProviderRequestIDFromPath(string path)
    {
        string[] parts = path.Trim().Trim('/').Split('/');
        for (int index = parts.Length - 1; index >= 0; index--)
        {
            string part = parts[index].Trim();
            if (part.Length == 0 || part is "content" or "download")
            {
                continue;
            }
            if (index > 0 && parts[index - 1] is "videos" or "tasks")
            {
                return part;
            }
            break;
        }
        return "";
    }

    /// <summary>对应 Go: <c>capabilityFromTaskType</c>。</summary>
    public static string CapabilityFromTaskType(string taskType)
    {
        string value = taskType.ToLowerInvariant();
        foreach (string capability in (string[])["video", "image", "audio", "text"])
        {
            if (value.Contains(capability, StringComparison.Ordinal))
            {
                return capability;
            }
        }
        if (value.Contains("storyboard", StringComparison.Ordinal)
            || value.Contains("agent", StringComparison.Ordinal))
        {
            return "text";
        }
        return "";
    }

    /// <summary>对应 Go: <c>normalizeCapability</c>。仅接受四个标准能力值。</summary>
    public static string NormalizeCapability(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized is "text" or "image" or "video" or "audio" ? normalized : "";
    }

    /// <summary>
    /// 从渠道 BaseURL 提取系统渠道 ID（<c>/api/ai/system/&lt;id&gt;</c> 自代理形态）。
    /// 对应 Go: <c>systemChannelIDFromBaseURL</c>。
    /// </summary>
    public static string SystemChannelIDFromBaseURL(string baseURL)
    {
        string value = baseURL.Trim();
        string lowerValue = value.ToLowerInvariant();
        foreach (string marker in new[] { "/api/ai/system/", "/api/" })
        {
            int index = lowerValue.LastIndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }
            string id = value[(index + marker.Length)..].Trim('/');
            int queryIndex = id.IndexOfAny(['?', '#']);
            if (queryIndex >= 0)
            {
                id = id[..queryIndex];
            }
            int slash = id.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                continue;
            }
            id = id.Trim();
            if (id.Length == 0)
            {
                continue;
            }
            if (id.ToLowerInvariant() is "v1" or "v1beta" or "v2" or "v3" or "plan" or "ai")
            {
                continue;
            }
            return id;
        }
        return "";
    }
}
