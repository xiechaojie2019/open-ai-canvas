#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;
using OpenAICanvas.Application.Capabilities;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Application;

/// <summary>时间线渲染请求。对应 Go: <c>app.TimelineRenderCreateRequest</c>。</summary>
public sealed class TimelineRenderRequestDto
{
    [JsonPropertyName("projectId")]
    public string ProjectID { get; set; } = "";

    [JsonPropertyName("timeline")]
    public JsonElement Timeline { get; set; }
}

/// <summary>
/// 任务重试与取消。对应 Go: <c>internal/app/task_lifecycle.go</c> 的
/// taskLifecycleCoordinator。
/// </summary>
/// <remarks>
/// 取消的上游 HTTP 取消请求（<c>requestProviderCancellation</c>）依赖 provider
/// 引擎，本批跳过——计费侧行为保持一致：带 ProviderRequestID 的取消把费用
/// 留给人工核对，未发起上游调用的排队取消直接退款、运行中取消冻结为待核对。
/// </remarks>
public sealed class TaskLifecycleService
{
    /// <summary>对应 Go: <c>contentModerationErrorCode</c>。</summary>
    private const string ContentModerationErrorCode = "sensitive_words_detected";

    /// <summary>对应 Go: <c>contentModerationRetryMessage</c>。</summary>
    private const string ContentModerationRetryMessage =
        "内容审核未通过，请修改提示词后重新生成；原任务不能直接重试";

    private readonly Repository _repository;
    private readonly TaskCreationService _creations;
    private readonly IRuntimePolicyProvider _runtimePolicy;

    public TaskLifecycleService(
        Repository repository,
        TaskCreationService creations,
        IRuntimePolicyProvider? runtimePolicy = null)
    {
        _repository = repository;
        _creations = creations;
        _runtimePolicy = runtimePolicy ?? new DefaultRuntimePolicyProvider();
    }

    /// <summary>重试失败/取消任务。对应 Go: <c>retryTask</c>。</summary>
    public async Task<TaskEntity> RetryAsync(
        string userId, string taskId, CancellationToken cancellationToken = default)
    {
        TaskEntity task = await _repository.TaskForUserAsync(userId, taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        if (task.CreationSubmissionID is not null && task.CreationSubmissionID.Length > 0)
        {
            throw AppError.New(409, "智能创作重做需要新的报价批准，请回到创作会话继续");
        }
        if (task.Operation.StartsWith("cloud_agent", StringComparison.Ordinal))
        {
            throw AppError.BadAuthRequest("Agent 重试需要新的幂等键和预算校验，请回到 Agent 对话重新发送");
        }
        if (task.Status != TaskStatus.TaskStatusFailed && task.Status != TaskStatus.TaskStatusCancelled)
        {
            throw new InvalidOperationException("only failed or cancelled tasks can be retried");
        }
        if (task.ProviderCancelStatus == "requested")
        {
            throw AppError.BadAuthRequest("上游取消状态仍在确认中，请确认费用结果后再重试");
        }
        await CheckRetryEligibilityAsync(task.BillingOrderID, cancellationToken).ConfigureAwait(false);
        if (task.Error.Contains(ContentModerationErrorCode, StringComparison.OrdinalIgnoreCase))
        {
            throw AppError.BadAuthRequest(ContentModerationRetryMessage);
        }
        string decryptedInput = DecryptTaskInputJSON(task.InputJSON);
        Dictionary<string, JsonElement> billingInput = ParseInput(decryptedInput);
        await PrepareLogicalTaskRetryAsync(task, billingInput, cancellationToken).ConfigureAwait(false);
        if (TaskCreationService.TaskInputUsesCustomChannelPublic(billingInput) && _features is not null)
        {
            await _features.RequireFeatureAsync(
                OpenAICanvas.Platform.FeatureNames.CustomChannels, cancellationToken).ConfigureAwait(false);
        }
        BillingOrder? billingOrder = await _creations.TaskBillingOrderAsync(userId, task, billingInput, cancellationToken)
            .ConfigureAwait(false);
        Platform.RuntimePolicySetting policy = _runtimePolicy.Current();
        await _creations.EnsureTaskProjectActiveAsync(userId, task.ProjectID, cancellationToken).ConfigureAwait(false);
        try
        {
            task = await _repository.RetryTaskWithBillingAsync(
                userId, task, billingOrder, policy.Task.ActiveTaskLimit, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException error) when (error.Message == "insufficient_credits")
        {
            throw AppError.BadAuthRequest("积分不足，请先使用兑换码充值");
        }
        catch (InvalidOperationException error) when (error.Message == "active_task_limit")
        {
            throw AppError.BadAuthRequest(
                $"同时排队或运行的任务最多 {policy.Task.ActiveTaskLimit} 个，请等待已有任务完成");
        }
        catch (InvalidOperationException error) when (error.Message == "task_not_retryable")
        {
            throw AppError.BadAuthRequest("任务已被其他请求重新入队，请勿重复重试");
        }
        await _repository.CreateTaskLogAsync(userId, task.ID, "info", "任务已重新入队", "", cancellationToken)
            .ConfigureAwait(false);
        return TaskCreationService.TaskForOutputPublic(task);
    }

    /// <summary>取消排队/运行任务。对应 Go: <c>cancelTask</c>。</summary>
    public async Task<TaskEntity> CancelAsync(
        string userId, string taskId, CancellationToken cancellationToken = default)
    {
        TaskEntity task = await _repository.TaskForUserAsync(userId, taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        if (task.Status != TaskStatus.TaskStatusQueued && task.Status != TaskStatus.TaskStatusRunning)
        {
            if (task.Status == TaskStatus.TaskStatusCancelled)
            {
                return TaskCreationService.TaskForOutputPublic(task);
            }
            throw new InvalidOperationException($"任务当前状态为 {task.Status}，无法取消");
        }

        // 先从账单和请求日志补齐上游 ID，再做条件更新。
        HydrateProviderRequestID(task);
        string originalStatus = task.Status;
        DateTime now = DateTime.UtcNow;
        bool cancelled = await _repository
            .CancelTaskIfStatusAsync(userId, taskId, task.Status, now, cancellationToken).ConfigureAwait(false);
        if (!cancelled)
        {
            TaskEntity? latest = await _repository
                .TaskForUserAsync(userId, taskId, cancellationToken).ConfigureAwait(false);
            if (latest is not null && latest.Status == TaskStatus.TaskStatusCancelled)
            {
                return TaskCreationService.TaskForOutputPublic(latest);
            }
            throw new InvalidOperationException("任务状态已变化，请刷新后重试");
        }

        task.Status = TaskStatus.TaskStatusCancelled;
        task.Stage = "任务已取消";
        task.Error = "任务已取消";
        task.CompletedAt = now;

        try
        {
            bool keepDraft = true;
            await _repository.CompactTaskTextDeltasAsync(
                taskId, now.AddDays(7), keepDraft, cancellationToken).ConfigureAwait(false);
        }
        catch (AppError error) when (error.Status == 404)
        {
            // Go 把 ErrRecordNotFound 视为无需处理（任务已被删除）。
        }
        await _repository.CreateTaskLogAsync(userId, taskId, "warn", "用户主动取消任务", "", cancellationToken)
            .ConfigureAwait(false);

        if (task.ProviderRequestID.Length == 0)
        {
            // 上游取消 HTTP 请求依赖 provider 引擎（PENDING）；计费侧与 Go 一致。
            try
            {
                if (originalStatus == TaskStatus.TaskStatusQueued)
                {
                    await _repository.RefundBillingOrderAsync(
                        task.BillingOrderID, "用户主动取消，且任务尚未开始执行", cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _repository.MarkBillingUncertainAsync(
                        task.BillingOrderID, "用户取消时上游请求 ID 尚未确认，费用待核对", cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                await _repository.CreateTaskLogAsync(
                    userId, taskId, "error", "取消任务后处理积分失败，已保留人工核对线索",
                    error.Message, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await _repository.CreateTaskLogAsync(
                userId, taskId, "warn", "上游取消请求待 provider 引擎接管（重试对账将处理费用）", "",
                cancellationToken).ConfigureAwait(false);
        }

        return TaskCreationService.TaskForOutputPublic(task);
    }

    /// <summary>人工查询失败视频任务（准入门槛）。对应 Go: <c>QueryFailedVideoTask</c>。</summary>
    /// <remarks>
    /// 上游状态查询依赖 provider 协议引擎；本批移植全部准入门槛与归属校验，
    /// 通过后返回 Go 的协议不支持文案（PENDING #60）。
    /// </remarks>
    public async Task<ProviderTaskQueryResultDto> QueryProviderAsync(
        string userId, string taskId, CancellationToken cancellationToken = default)
    {
        TaskEntity task = await _repository.TaskForUserAsync(userId.Trim(), taskId.Trim(), cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("record not found");
        return await QueryProviderCoreAsync(task, userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>管理员按 API 日志查询失败视频任务。对应 Go: <c>AdminQueryFailedVideoTask</c>。</summary>
    public async Task<ProviderTaskQueryResultDto> AdminQueryProviderAsync(
        User actor, string logId, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        ApiCallLog? log = await _repository.ApiCallLogAsync(logId.Trim(), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        if (log.Capability != "video" || log.TaskID.Trim().Length == 0)
        {
            throw AppError.BadAuthRequest("该请求没有可查询的视频任务");
        }
        TaskEntity? task = await _repository.TaskAsync(log.TaskID, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        if (task.UserID != log.UserID)
        {
            throw AppError.BadAuthRequest("请求与任务归属不一致");
        }
        if (task.ProviderRequestID.Length == 0)
        {
            task.ProviderRequestID = log.ProviderRequestID.Trim();
        }
        return await QueryProviderCoreAsync(task, "", cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderTaskQueryResultDto> QueryProviderCoreAsync(
        TaskEntity task, string claimUserId, CancellationToken cancellationToken)
    {
        if (task.ID.Length == 0)
        {
            throw AppError.BadAuthRequest("任务不存在");
        }
        if (task.Status != TaskStatus.TaskStatusFailed)
        {
            throw AppError.BadAuthRequest("只能人工查询状态为失败的任务");
        }
        if (!task.Type.StartsWith("canvas_video", StringComparison.Ordinal)
            && !task.Type.StartsWith("video_", StringComparison.Ordinal))
        {
            throw AppError.BadAuthRequest("该任务不是视频生成任务");
        }

        HydrateProviderRequestID(task);
        string providerRequestID = task.ProviderRequestID.Trim();
        if (providerRequestID.Length == 0)
        {
            throw AppError.BadAuthRequest("该任务没有可恢复的上游任务 ID");
        }
        if (task.BillingOrderID.Length > 0)
        {
            BillingOrder? order = await _repository
                .BillingOrderAsync(task.BillingOrderID, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("record not found");
            if (order.UserID != task.UserID || order.TaskID != task.ID)
            {
                throw AppError.BadAuthRequest("任务与计费订单归属不一致");
            }
        }
        // 上游协议查询依赖 declarative protocol adapter（provider 引擎，PENDING #60）；
        // 与 Go 对非声明式协议的行为一致返回该文案。
        await Task.CompletedTask.ConfigureAwait(false);
        throw AppError.BadAuthRequest("该任务的请求协议不支持安全查询上游状态");
    }

    // ------------------------------------------------------------ 内部

    private Platform.FeatureAvailabilityService? _features;

    public TaskLifecycleService WithFeatures(Platform.FeatureAvailabilityService features)
    {
        _features = features;
        return this;
    }

    /// <summary>重试前计费核对。对应 Go: <c>CheckRetryEligibility</c>。</summary>
    private async Task CheckRetryEligibilityAsync(string orderId, CancellationToken cancellationToken)
    {
        if (orderId.Trim().Length == 0)
        {
            return;
        }
        BillingOrder? order = await _repository.BillingOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            throw new InvalidOperationException($"任务计费订单不存在：{orderId}");
        }
        if (order.Status == "uncertain")
        {
            throw AppError.BadAuthRequest("上一次调用费用仍在核对中，处理完成前不能重复提交");
        }
    }

    /// <summary>前台模型任务重试准备。对应 Go: <c>prepareLogicalTaskRetry</c>。</summary>
    private async Task PrepareLogicalTaskRetryAsync(
        TaskEntity task, Dictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        if (task.LogicalModelID.Length == 0)
        {
            return;
        }
        ModelRequestIntent intent = TaskCreationService.ModelRequestIntentFromTaskInput(
            input, task.Type, task.Operation);
        LogicalModel? logicalModel = await _repository
            .LogicalModelAsync(task.LogicalModelID, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        RoutedModel routed;
        if (logicalModel.ArchivedAt is not null)
        {
            routed = await ResolveArchivedTaskRouteAsync(task, intent, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            routed = await LogicalModelsForRetry.ResolveLogicalModelAsync(
                task.LogicalModelID, intent, cancellationToken).ConfigureAwait(false);
        }
        input = TaskCreationService.ApplyRoutedProviderSelectionPublic(input, routed);
        ProtectTaskSecrets(input);
        task.LogicalModelRevisionID = routed.Revision.ID;
        task.RouteID = routed.Route.ID;
        task.ChannelModelID = routed.ChannelModel.ID;
        task.Model = routed.LogicalModel.Code;
        task.Provider = "managed";
        task.InputJSON = TaskCreationService.SerializeInput(input);
    }

    /// <summary>归档模型任务按快照重试。对应 Go: <c>resolveArchivedTaskRoute</c>。</summary>
    private async Task<RoutedModel> ResolveArchivedTaskRouteAsync(
        TaskEntity task, ModelRequestIntent intent, CancellationToken cancellationToken)
    {
        if (task.LogicalModelID.Length == 0 || task.LogicalModelRevisionID.Length == 0
            || task.RouteID.Length == 0 || task.ChannelModelID.Length == 0)
        {
            throw AppError.BadAuthRequest("历史任务缺少完整的模型服务快照，无法重试");
        }
        LogicalModel? logicalModel = await _repository
            .LogicalModelAsync(task.LogicalModelID, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        if (logicalModel.ArchivedAt is null)
        {
            throw AppError.BadAuthRequest("任务模型已恢复为可用模型，请重新选择后重试");
        }
        LogicalModelRevision? revision = await _repository
            .LogicalModelRevisionAsync(task.LogicalModelRevisionID, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.BadAuthRequest("历史任务原模型服务已失效，无法重试");
        ChannelModel? channelModel = await _repository
            .ChannelModelAsync(task.ChannelModelID, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.BadAuthRequest("历史任务原模型服务已失效，无法重试");
        ModelChannel? channel = await _repository
            .SystemChannelByIDAsync(channelModel.ChannelID, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.BadAuthRequest("历史任务原模型渠道已失效，无法重试");
        LogicalModelRoute? route = await _repository
            .LogicalModelRouteAsync(task.RouteID, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.BadAuthRequest("历史任务原模型供应线路暂不可用，无法重试");
        if (route.LogicalModelRevisionID != task.LogicalModelRevisionID
            || route.ChannelModelID != task.ChannelModelID)
        {
            throw AppError.BadAuthRequest("历史任务原模型供应线路暂不可用，无法重试");
        }
        if (!channelModel.Enabled)
        {
            throw AppError.BadAuthRequest("历史任务原模型供应线路暂不可用，无法重试");
        }
        CapabilitySpec productSpec =
            CapabilitySpecOps.DecodeCapabilitySpec(revision.CapabilitySpecJSON);
        Dictionary<string, JsonElement> defaults =
            CapabilitySpecOps.DecodeLogicalDefaults(revision.DefaultOptionsJSON, productSpec);
        intent.Options = CapabilitySpecOps.MergeIntentDefaults(intent.Options, defaults);
        CapabilityMatch match = CapabilitySpecOps.MatchCapability(productSpec, intent);
        if (!match.Matched)
        {
            throw AppError.BadAuthRequest("历史任务不再符合前台模型能力：" + string.Join("；", match.Reasons ?? []));
        }
        if (logicalModel.PricePolicy == "channel" && !channelModel.PriceConfigured)
        {
            throw AppError.BadAuthRequest("历史任务原模型价格配置已失效，无法重试");
        }
        _ = channel;
        return new RoutedModel
        {
            LogicalModel = logicalModel,
            Revision = revision,
            Route = route,
            ChannelModel = channelModel,
            Defaults = defaults,
        };
    }

    private LogicalModelService LogicalModelsForRetry =>
        new(_repository);

    /// <summary>补齐上游请求 ID。对应 Go: <c>hydrateTaskProviderRequestID</c>。</summary>
    private async Task HydrateProviderRequestID(TaskEntity task)
    {
        if (task.ProviderRequestID.Length > 0)
        {
            return;
        }
        if (task.BillingOrderID.Length > 0)
        {
            BillingOrder? order = await _repository
                .BillingOrderAsync(task.BillingOrderID).ConfigureAwait(false);
            if (order is not null)
            {
                task.ProviderRequestID = order.ProviderRequestID.Trim();
            }
        }
        if (task.ProviderRequestID.Length == 0)
        {
            task.ProviderRequestID = await _repository
                .LatestProviderRequestIDForTaskAsync(task.ID).ConfigureAwait(false);
        }
    }

    /// <summary>解密任务输入。对应 Go: <c>decryptTaskInputJSON</c>。</summary>
    private string DecryptTaskInputJSON(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !raw.Contains(SettingsCrypto.EncryptedPrefix, StringComparison.Ordinal))
        {
            return raw;
        }
        JsonElement input;
        try
        {
            input = JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch (JsonException)
        {
            return raw;
        }
        return JsonSerializer.Serialize(
            DecryptSecretsElement(input),
            ProjectCharacterService.GoPayloadOptions);
    }

    private JsonElement DecryptSecretsElement(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (TaskCreationService.IsTaskSecretFieldPublic(property.Name)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    result[property.Name] = JsonSerializer.SerializeToElement(
                        SettingsCrypto.DecryptSecret(property.Value.GetString() ?? "", DataDir));
                    continue;
                }
                result[property.Name] = property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? DecryptSecretsElement(property.Value)
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
                    ? DecryptSecretsElement(item)
                    : item.Clone());
            }
            return JsonSerializer.SerializeToElement(items);
        }
        return value.Clone();
    }

    private static Dictionary<string, JsonElement> ParseInput(string raw)
    {
        try
        {
            JsonElement element = JsonSerializer.Deserialize<JsonElement>(raw);
            if (element.ValueKind == JsonValueKind.Object)
            {
                Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    result[property.Name] = property.Value.Clone();
                }
                return result;
            }
        }
        catch (JsonException)
        {
            // 落到下方统一错误。
        }
        throw new InvalidOperationException("任务输入解析失败");
    }

    private void ProtectTaskSecrets(Dictionary<string, JsonElement> input) =>
        _creations.ProtectTaskSecretsPublic(input);

    private string DataDir => "data";
}

/// <summary>上游任务查询结果。对应 Go: <c>app.ProviderTaskQueryResult</c>。</summary>
public sealed class ProviderTaskQueryResultDto
{
    [JsonPropertyName("providerStatus")]
    public string ProviderStatus { get; set; } = "";

    [JsonPropertyName("recovered")]
    public bool Recovered { get; set; }
}
