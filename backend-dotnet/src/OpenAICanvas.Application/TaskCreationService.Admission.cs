#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Application;

/// <summary>
/// 队列任务 admission：模型选路（前台/系统渠道/自定义渠道）、计费预留、项目守卫。
/// 对应 Go: <c>task_creation.go</c> 的 CreateTask 队列分支、<c>model_router.go</c>、
/// <c>finance.go</c> 的 taskBillingOrder。
/// </summary>
/// <remarks>
/// 已知取舍（PENDING #59）：Go 的 <c>ValidateTaskCapability</c>（图片/视频参数逐值校验）
/// 未移植——能力 OPTION 级约束已由逻辑模型 MatchCapability 与渠道能力合同匹配覆盖。
/// </remarks>
public sealed partial class TaskCreationService
{
    /// <summary>RunningHub 插件的 interfaceType 集合（插件注册表未移植，一律视为未启用）。</summary>
    private static bool IsRunningHubInterface(string value) =>
        value.Trim().ToLowerInvariant() is "runninghub" or "runninghub_workflow";

    /// <summary>队列任务创建。对应 Go: <c>CreateTask</c> 的队列分支。</summary>
    /// <summary>工作流协议或文本回放判定。对应 Go creation.go 的合并条件。</summary>
    internal static bool CreationUsesWorkflowOrReplay(Dictionary<string, JsonElement> input) =>
        TaskInputUsesWorkflowProvider(input) || IsTextReplayTaskRequest(input);

    internal async Task<TaskEntity> CreateQueuedAsync(
        string userId,
        CreateTaskRequestDto request,
        Dictionary<string, JsonElement> input,
        string taskType,
        string prompt,
        string traceId,
        string requestId,
        CancellationToken cancellationToken)
    {
        bool workflowProviderTask = TaskInputUsesWorkflowProvider(input);
        if (workflowProviderTask)
        {
            // 插件注册表未移植：RunningHub 工作流一律未启用（与默认部署行为一致）。
            string interfaceType = InputConfigString(input, "interfaceType").Trim().ToLowerInvariant();
            if (interfaceType.Length == 0 || IsRunningHubInterface(interfaceType))
            {
                throw AppError.Forbidden("RunningHub 工作流插件未启用");
            }
            throw AppError.Forbidden("未知工作流插件");
        }

        bool frontendEnabled = false;
        if (_features is not null)
        {
            frontendEnabled = await _features.FeatureEnabledAsync(
                OpenAICanvas.Platform.FeatureNames.FrontendModels, cancellationToken).ConfigureAwait(false);
        }

        RoutedModel? routed = null;
        string logicalModelId = request.LogicalModelID.Trim();
        if (!workflowProviderTask)
        {
            (routed, input) = await ResolveTaskModelSelectionAsync(
                input, logicalModelId, taskType, request.Operation, frontendEnabled, cancellationToken)
                .ConfigureAwait(false);
        }

        if (taskType.StartsWith("video_", StringComparison.Ordinal) && !HasExecutableProviderVideoConfig(input))
        {
            if (InputString(input, "mode") != "video")
            {
                throw new InvalidOperationException("视频任务必须使用 video 模式");
            }
            throw new InvalidOperationException("视频任务缺少可执行的模型配置");
        }

        if (TaskInputUsesCustomChannel(input) && _features is not null)
        {
            await _features.RequireFeatureAsync(
                OpenAICanvas.Platform.FeatureNames.CustomChannels, cancellationToken).ConfigureAwait(false);
        }

        // 内嵌媒体检查（Go: containsInlineMediaDataURL）。
        JsonElement inputElement = JsonSerializer.SerializeToElement(input);
        if (UserDataService.ContainsInlineMediaDataURL(inputElement))
        {
            throw AppError.BadAuthRequest("任务输入不能包含内嵌媒体，请先上传到资源存储");
        }

        Platform.RuntimePolicySetting policy = _runtimePolicy.Current();
        long activeTasks = await _repository
            .ActiveTaskCountForUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (activeTasks >= policy.Task.ActiveTaskLimit)
        {
            throw AppError.BadAuthRequest(
                $"同时排队或运行的任务最多 {policy.Task.ActiveTaskLimit} 个，请等待已有任务完成");
        }

        TaskEntity task = new()
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            TraceID = traceId,
            RequestID = requestId,
            ProjectID = request.ProjectID,
            Type = taskType,
            Status = TaskStatus.TaskStatusQueued,
            Stage = "等待队列调度",
            Progress = 5,
            Prompt = prompt,
            Operation = request.Operation,
            Provider = request.Provider,
            Model = request.Model,
        };
        if (routed is not null)
        {
            task.LogicalModelID = routed.LogicalModel.ID;
            task.LogicalModelRevisionID = routed.Revision.ID;
            task.RouteID = routed.Route.ID;
            task.ChannelModelID = routed.ChannelModel.ID;
            task.RouteRun = 1;
            task.Model = routed.LogicalModel.Code;
            task.Provider = "managed";
        }

        await EnsureTaskProjectActiveAsync(userId, request.ProjectID, cancellationToken).ConfigureAwait(false);

        BillingOrder? billingOrder = await TaskBillingOrderAsync(userId, task, input, cancellationToken)
            .ConfigureAwait(false);

        ProtectTaskSecrets(input);
        task.InputJSON = SerializeInput(input);
        if (billingOrder is not null)
        {
            task.BillingOrderID = billingOrder.ID;
        }
        await CreateWithinStorageQuotaAsync(task, billingOrder, cancellationToken).ConfigureAwait(false);
        await _repository.CreateTaskLogAsync(userId, task.ID, "info", "任务已进入队列", "", cancellationToken)
            .ConfigureAwait(false);
        return TaskForOutput(task);
    }

    /// <summary>
    /// 队列任务 admission（不落库、不计活动/日志）：供创作报价使用。
    /// 返回已脱敏前的任务（InputJSON 已含加密后的密钥）与计费单。
    /// 对应 Go: <c>prepareCreationTask</c> 里的 CreateTask creationPrepare 分支。
    /// </summary>
    internal async Task<(TaskEntity Task, BillingOrder? Order)> AdmitQueuedAsync(
        string userId,
        CreateTaskRequestDto request,
        Dictionary<string, JsonElement> input,
        string taskType,
        string prompt,
        string traceId,
        string requestId,
        CancellationToken cancellationToken)
    {
        (RoutedModel? routed, input) = await ResolveTaskModelSelectionAsync(
            input, request.LogicalModelID.Trim(), taskType, request.Operation,
            _features is not null && await _features.FeatureEnabledAsync(
                OpenAICanvas.Platform.FeatureNames.FrontendModels, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        TaskEntity task = new()
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            TraceID = traceId,
            RequestID = requestId,
            ProjectID = request.ProjectID,
            Type = taskType,
            Status = TaskStatus.TaskStatusQueued,
            Stage = "等待队列调度",
            Progress = 5,
            Prompt = prompt,
            Operation = request.Operation,
            Provider = request.Provider,
            Model = request.Model,
        };
        if (routed is not null)
        {
            task.LogicalModelID = routed.LogicalModel.ID;
            task.LogicalModelRevisionID = routed.Revision.ID;
            task.RouteID = routed.Route.ID;
            task.ChannelModelID = routed.ChannelModel.ID;
            task.RouteRun = 1;
            task.Model = routed.LogicalModel.Code;
            task.Provider = "managed";
        }

        await EnsureTaskProjectActiveAsync(userId, request.ProjectID, cancellationToken).ConfigureAwait(false);
        BillingOrder? billingOrder = await TaskBillingOrderAsync(userId, task, input, cancellationToken)
            .ConfigureAwait(false);
        ProtectTaskSecrets(input);
        task.InputJSON = SerializeInput(input);
        if (billingOrder is not null)
        {
            task.BillingOrderID = billingOrder.ID;
        }
        return (task, billingOrder);
    }

    // ------------------------------------------------------------ 选路

    /// <summary>按请求携带的模型选择决定路由方式。对应 Go: <c>resolveTaskModelSelection</c>。</summary>
    internal async Task<(RoutedModel? Routed, Dictionary<string, JsonElement> Input)> ResolveTaskModelSelectionAsync(
        Dictionary<string, JsonElement> input,
        string logicalModelId,
        string taskType,
        string operation,
        bool frontendEnabled,
        CancellationToken cancellationToken)
    {
        bool customChannelTask = TaskInputUsesCustomChannel(input);
        if (frontendEnabled && !TaskInputUsesSystemChannel(input) && !customChannelTask)
        {
            if (logicalModelId.Length == 0)
            {
                throw ModelSelectionError("前台模型模式下必须指定 logicalModelId");
            }
            ModelRequestIntent intent = ModelRequestIntentFromTaskInput(input, taskType, operation);
            RoutedModel routed = await LogicalModels.ResolveLogicalModelAsync(
                logicalModelId, intent, cancellationToken).ConfigureAwait(false);
            return (routed, ApplyRoutedProviderSelection(input, routed));
        }

        if (logicalModelId.Length > 0)
        {
            throw ModelSelectionError("模型目录已更新，请重新选择");
        }
        if (!customChannelTask)
        {
            Dictionary<string, JsonElement> resolved = await ResolveSystemChannelModelSelectionAsync(
                input, taskType, operation, cancellationToken).ConfigureAwait(false);
            return (null, resolved);
        }
        return (null, input);
    }

    /// <summary>模型选择类错误（ModelErrorCode → 机器可读 reason）。对应 Go: <c>InvalidModelSelection</c> 等。</summary>
    private static AppError ModelSelectionError(string message) =>
        AppError.New(400, message);

    /// <summary>应用路由结果到执行配置。对应 Go: <c>applyRoutedProviderSelection</c>。</summary>
    private static Dictionary<string, JsonElement> ApplyRoutedProviderSelection(
        Dictionary<string, JsonElement> input, RoutedModel routed)
    {
        Dictionary<string, JsonElement> config = InputConfig(input);
        Dictionary<string, JsonElement> nextConfig = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonElement> pair in config)
        {
            switch (pair.Key)
            {
                case "channelId":
                case "channelModelKey":
                case "priceTierId":
                case "providerModelKey":
                case "apiFormat":
                case "interfaceType":
                case "baseUrl":
                case "apiKey":
                case "secretKey":
                case "headers":
                case "model":
                case "capabilityConfig":
                    continue;
                default:
                    nextConfig[pair.Key] = pair.Value.Clone();
                    break;
            }
        }
        foreach (KeyValuePair<string, JsonElement> pair in routed.Defaults)
        {
            string canonical = CapabilitySpecOps.CanonicalCapabilityOptionName(pair.Key);
            if (!nextConfig.TryGetValue(canonical, out JsonElement existing)
                || existing.ValueKind is JsonValueKind.Null
                || BlankScalar(existing))
            {
                nextConfig[canonical] = ProviderConfigOptionValue(pair.Value);
            }
        }
        if (input.TryGetValue("capabilityOptions", out JsonElement optionsElement)
            && optionsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in optionsElement.EnumerateObject())
            {
                string canonical = CapabilitySpecOps.CanonicalCapabilityOptionName(property.Name);
                if (CapabilitySpecOps.IsCapabilityOptionFor("image", canonical)
                    || CapabilitySpecOps.IsCapabilityOptionFor("video", canonical)
                    || CapabilitySpecOps.IsCapabilityOptionFor("audio", canonical)
                    || CapabilitySpecOps.IsCapabilityOptionFor("text", canonical))
                {
                    nextConfig[canonical] = ProviderConfigOptionValue(property.Value);
                }
            }
        }
        nextConfig["channelId"] = JsonSerializer.SerializeToElement(routed.ChannelModel.ChannelID);
        nextConfig["model"] = JsonSerializer.SerializeToElement(routed.ChannelModel.ModelKey);
        nextConfig["channelModelKey"] = JsonSerializer.SerializeToElement(routed.ChannelModel.ModelKey);
        if (routed.PriceTier is not null)
        {
            nextConfig["priceTierId"] = JsonSerializer.SerializeToElement(routed.PriceTier.ID);
            nextConfig["providerModelKey"] = JsonSerializer.SerializeToElement(routed.PriceTier.ProviderModelKey);
        }
        input["config"] = JsonSerializer.SerializeToElement(nextConfig);
        return input;
    }

    /// <summary>系统渠道任务 admission。对应 Go: <c>resolveSystemChannelModelSelection</c>。</summary>
    private async Task<Dictionary<string, JsonElement>> ResolveSystemChannelModelSelectionAsync(
        Dictionary<string, JsonElement> input,
        string taskType,
        string operation,
        CancellationToken cancellationToken)
    {
        Dictionary<string, JsonElement> config = InputConfig(input);
        if (config.Count == 0 && !input.TryGetValue("config", out _))
        {
            throw ModelSelectionError("缺少模型配置");
        }

        string channelID = InputConfigString(input, "channelId").Trim();
        string modelKey = InputConfigString(input, "model").Trim();
        if (modelKey.StartsWith("models/", StringComparison.Ordinal))
        {
            modelKey = modelKey["models/".Length..];
        }
        if (channelID.Length == 0 || modelKey.Length == 0)
        {
            throw ModelSelectionError("必须指定系统渠道和模型");
        }

        ModelChannel? channel = await _repository
            .SystemChannelByIDAsync(channelID, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            throw ModelSelectionError("指定的渠道不存在");
        }
        if (!channel.Enabled || channel.Scope != "system")
        {
            throw ModelSelectionError("指定的渠道不可用");
        }

        ChannelModel? channelModel = await _repository
            .ChannelModelByKeyAsync(channelID, modelKey, cancellationToken).ConfigureAwait(false);
        if (channelModel is null)
        {
            throw ModelSelectionError("指定的模型不存在");
        }
        if (!channelModel.Enabled)
        {
            throw ModelSelectionError("指定的模型已停用");
        }
        if (channelModel.Protocol.Length == 0)
        {
            throw ModelSelectionError("指定的模型未配置请求协议");
        }

        Dictionary<string, JsonElement> nextConfig = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonElement> pair in config)
        {
            switch (pair.Key)
            {
                case "channelId":
                case "channelModelKey":
                case "priceTierId":
                case "providerModelKey":
                case "apiFormat":
                case "interfaceType":
                case "baseUrl":
                case "apiKey":
                case "secretKey":
                case "headers":
                case "model":
                case "capabilityConfig":
                    continue;
                default:
                    nextConfig[pair.Key] = pair.Value.Clone();
                    break;
            }
        }
        if (input.TryGetValue("capabilityOptions", out JsonElement optionsElement)
            && optionsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in optionsElement.EnumerateObject())
            {
                string canonical = CapabilitySpecOps.CanonicalCapabilityOptionName(property.Name);
                if (CapabilitySpecOps.IsCapabilityOptionFor(channelModel.Capability, canonical))
                {
                    nextConfig[canonical] = property.Value.Clone();
                }
            }
        }

        ModelCapabilityConfig? capabilityConfig = NormalizedChannelModelCapability(channelModel);
        CapabilitySpec? capabilitySpec = null;
        if (CapabilitySpecOps.NormalizeCapability(channelModel.Capability) != "audio")
        {
            if (capabilityConfig is null)
            {
                throw ModelSelectionError("指定的模型能力配置无效，请联系管理员");
            }
            try
            {
                capabilitySpec = ModelCapabilityConfigOps.CapabilitySpecFromModelCapabilityConfig(
                    capabilityConfig, channelModel.Capability);
            }
            catch (AppError)
            {
                throw ModelSelectionError("指定的模型能力配置无效，请联系管理员");
            }
        }
        ApplyChannelCapabilityDefaults(nextConfig, channelModel.Capability, capabilityConfig);
        input["config"] = JsonSerializer.SerializeToElement(nextConfig);

        System.Collections.Generic.IReadOnlyDictionary<string, OptionConstraint>? declaredOptions = capabilitySpec?.Options;
        input["capabilityOptions"] = JsonSerializer.SerializeToElement(
            CapabilityOptionsFromConfig(channelModel.Capability, nextConfig, declaredOptions));

        ModelRequestIntent intent = ModelRequestIntentFromTaskInput(input, taskType, operation);
        if (CapabilitySpecOps.NormalizeCapability(intent.Capability)
            != CapabilitySpecOps.NormalizeCapability(channelModel.Capability))
        {
            throw new AppError(400, "所选模型与任务能力不匹配", reason: "model_capability_not_supported");
        }
        if (CapabilitySpecOps.NormalizeCapability(channelModel.Capability) != "audio")
        {
            if (capabilitySpec is null)
            {
                throw ModelSelectionError("指定的模型能力配置无效，请联系管理员");
            }
            CapabilityMatch match = CapabilitySpecOps.MatchCapability(capabilitySpec, intent);
            if (!match.Matched)
            {
                throw new AppError(
                    400,
                    "所选模型不支持当前请求：" + string.Join("；", match.Reasons ?? []),
                    reason: "model_capability_not_supported");
            }
        }

        ModelRequestIntent pricingIntent = intent;
        if (CapabilitySpecOps.NormalizeCapability(channelModel.Capability) == "image")
        {
            Dictionary<string, JsonElement> pricingOptions = new(intent.Options, StringComparer.Ordinal);
            string rawQuality = ScalarText(GetValueOrDefault(nextConfig, "quality")).ToLowerInvariant();
            if (rawQuality.Length > 0 && rawQuality != "<nil>" && rawQuality != "auto" && rawQuality != "any")
            {
                pricingOptions["quality"] = JsonSerializer.SerializeToElement(rawQuality);
            }
            else if (!pricingOptions.TryGetValue("quality", out JsonElement currentQuality)
                || currentQuality.ValueKind == JsonValueKind.Null
                || (currentQuality.ValueKind == JsonValueKind.String
                    && (currentQuality.GetString() ?? "").Length == 0)
                || (currentQuality.ValueKind == JsonValueKind.String && currentQuality.GetString() == "auto"))
            {
                pricingOptions["quality"] = JsonSerializer.SerializeToElement("1k");
            }
            if (ScalarText(GetValueOrDefault(nextConfig, "size")).ToLowerInvariant() is { Length: > 0 } rawSize
                && rawSize != "<nil>" && rawSize != "auto")
            {
                pricingOptions["size"] = JsonSerializer.SerializeToElement(rawSize);
            }
            pricingIntent.Options = pricingOptions;
        }

        ChannelModelPriceTier? priceTier = ModelSku.ChannelModelPriceTierForIntent(channelModel, pricingIntent)
            ?? ModelSku.ChannelModelPriceTierForIntent(channelModel, intent);
        if (priceTier is null
            || !ValidatePriceTierPrice(priceTier, channelModel.Capability, channelModel.Protocol))
        {
            throw new AppError(400, "指定的模型未配置当前规格的有效价格", reason: "model_price_not_configured");
        }

        nextConfig["channelId"] = JsonSerializer.SerializeToElement(channel.ID);
        nextConfig["model"] = JsonSerializer.SerializeToElement(channelModel.ModelKey);
        nextConfig["channelModelKey"] = JsonSerializer.SerializeToElement(channelModel.ModelKey);
        nextConfig["priceTierId"] = JsonSerializer.SerializeToElement(priceTier.ID);
        nextConfig["providerModelKey"] = JsonSerializer.SerializeToElement(
            LogicalModelService.FirstNonEmpty(priceTier.ProviderModelKey, channelModel.ProviderModelKey, channelModel.ModelKey));
        nextConfig["interfaceType"] = JsonSerializer.SerializeToElement(channelModel.Protocol);
        nextConfig["apiFormat"] = JsonSerializer.SerializeToElement(
            ChannelAPIFormatForProtocol(channel.APIFormat, channelModel.Protocol));
        input["config"] = JsonSerializer.SerializeToElement(nextConfig);
        return input;
    }

    // ------------------------------------------------------------ 计费

    /// <summary>任务计费单。对应 Go: <c>taskBillingOrder</c>（credits 关闭时为 null）。</summary>
    internal async Task<BillingOrder?> TaskBillingOrderAsync(
        string userId, TaskEntity task, Dictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        if (_features is null
            || !await _features.FeatureEnabledAsync(
                OpenAICanvas.Platform.FeatureNames.Credits, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        if (task.LogicalModelID.Length > 0)
        {
            return await NewLogicalModelBillingOrderAsync(userId, task, input, cancellationToken).ConfigureAwait(false);
        }
        Dictionary<string, JsonElement> config = InputConfig(input);
        if (config.Count == 0 && !input.ContainsKey("config"))
        {
            return null;
        }
        string channelID = ScalarText(GetValueOrDefault(config, "channelId")).Trim();
        if (channelID.Length == 0)
        {
            channelID = SystemChannelIDFromBaseURL(ScalarText(GetValueOrDefault(config, "baseUrl")));
        }
        if (channelID.Length == 0)
        {
            return null;
        }
        string modelKey = ScalarText(GetValueOrDefault(config, "model")).Trim();
        if (modelKey.StartsWith("models/", StringComparison.Ordinal))
        {
            modelKey = modelKey["models/".Length..];
        }
        string capability = ResolveCapability(input, task.Type);
        string scene = LogicalModelService.FirstNonEmpty(task.Operation.Trim(), task.Type);
        ModelRequestIntent intent = ModelRequestIntentFromTaskInput(input, task.Type, task.Operation);
        string priceTierId = ScalarText(GetValueOrDefault(config, "priceTierId")).Trim();
        JsonElement? videoSeconds = config.TryGetValue("videoSeconds", out JsonElement vs) ? vs : null;
        return await LogicalModels.BuildBillingOrderWithPriceTierAsync(
            userId,
            task.ID,
            "task:" + task.ID + ":" + IdGenerator.NewId(),
            channelID,
            modelKey,
            capability,
            scene,
            BillingCalc.BillingQuantity(capability, videoSeconds),
            BillingCalc.EstimateTaskBillingTokens(
                BillingCalc.QuoteInput(capability, modelKey, config, IntentInputs(intent)), capability),
            priceTierId,
            intent,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>前台模型计费单。对应 Go: <c>newLogicalModelBillingOrder</c>。</summary>
    private async Task<BillingOrder?> NewLogicalModelBillingOrderAsync(
        string userId, TaskEntity task, Dictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        LogicalModel? logicalModel = await _repository
            .LogicalModelAsync(task.LogicalModelID, cancellationToken).ConfigureAwait(false);
        if (logicalModel is null
            || (!logicalModel.Enabled && logicalModel.ArchivedAt is null)
            || logicalModel.ActiveRevisionID != task.LogicalModelRevisionID)
        {
            throw AppError.BadAuthRequest("所选模型计费配置已失效，请重新选择");
        }
        LogicalModelRoute? route = await _repository
            .LogicalModelRouteAsync(task.RouteID, cancellationToken).ConfigureAwait(false);
        if (route is null
            || route.LogicalModelRevisionID != task.LogicalModelRevisionID
            || route.ChannelModelID != task.ChannelModelID)
        {
            throw AppError.BadAuthRequest("所选模型供应线路已更新，请重新选择");
        }
        ChannelModel? channelModel = await _repository
            .ChannelModelAsync(task.ChannelModelID, cancellationToken).ConfigureAwait(false);
        if (channelModel is null)
        {
            throw AppError.BadAuthRequest("所选模型供应线路已更新，请重新选择");
        }
        Dictionary<string, JsonElement> config = InputConfig(input);
        string capability = ResolveCapability(input, task.Type);
        string scene = LogicalModelService.FirstNonEmpty(task.Operation.Trim(), task.Type);
        ModelRequestIntent intent = ModelRequestIntentFromTaskInput(input, task.Type, task.Operation);
        if (logicalModel.PricePolicy == "channel")
        {
            string priceTierId = ScalarText(GetValueOrDefault(config, "priceTierId")).Trim();
            JsonElement? videoSeconds = config.TryGetValue("videoSeconds", out JsonElement vs) ? vs : null;
            BillingOrder order = await LogicalModels.BuildBillingOrderWithPriceTierAsync(
                userId,
                task.ID,
                "task:" + task.ID + ":" + IdGenerator.NewId(),
                channelModel.ChannelID,
                channelModel.ModelKey,
                capability,
                scene,
                BillingCalc.BillingQuantity(capability, videoSeconds),
                BillingCalc.EstimateTaskBillingTokens(
                    BillingCalc.QuoteInput(capability, channelModel.ModelKey, config, IntentInputs(intent)), capability),
                priceTierId,
                intent,
                cancellationToken).ConfigureAwait(false);
            order.Model = logicalModel.Code;
            return order;
        }
        if (logicalModel.PricePolicy != "unified")
        {
            throw AppError.BadAuthRequest("当前模型价格策略无效");
        }
        long quantity = 1;
        BillingCalc.TokenBillingEstimate tokenEstimate = BillingCalc.EstimateTaskBillingTokens(
            BillingCalc.QuoteInput(capability, channelModel.ModelKey, config, IntentInputs(intent)), capability);
        long amount;
        switch (logicalModel.BillingMode)
        {
            case "fixed_request":
                amount = logicalModel.UnitPriceMicrocredits;
                break;
            case "per_second":
                JsonElement? videoSeconds = config.TryGetValue("videoSeconds", out JsonElement vs) ? vs : null;
                quantity = BillingCalc.BillingQuantity(capability, videoSeconds);
                if (capability != "video" || quantity <= 0)
                {
                    throw AppError.BadAuthRequest("当前模型按时长计费，但请求未提供有效时长");
                }
                amount = BillingCalc.CreditAmount(logicalModel.UnitPriceMicrocredits, quantity, 10_000);
                break;
            case "token":
                if (channelModel.Capability != capability
                    || !ModelCapabilityConfigOps.SupportsTokenBilling(capability, channelModel.Protocol))
                {
                    throw AppError.BadAuthRequest("当前供应线路不支持前台模型的 Token 计费方式");
                }
                ChannelModel pricing = new()
                {
                    InputTokenPriceMicrocredits = logicalModel.InputPriceMicrocredits,
                    OutputTokenPriceMicrocredits = logicalModel.OutputPriceMicrocredits,
                    CachedTokenPriceMicrocredits = logicalModel.CachedPriceMicrocredits,
                };
                amount = BillingCalc.TokenEstimateAmount(pricing, tokenEstimate, 10_000);
                quantity = tokenEstimate.InputTokens + tokenEstimate.OutputTokens;
                break;
            default:
                throw AppError.BadAuthRequest("当前模型计费方式暂不支持");
        }
        if (amount <= 0)
        {
            throw AppError.BadAuthRequest("当前模型尚未配置有效的用户价格");
        }
        LogicalModelRevision? revision = await _repository
            .LogicalModelRevisionAsync(task.LogicalModelRevisionID, cancellationToken).ConfigureAwait(false);
        return new BillingOrder
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            IdempotencyKey = "task:" + task.ID + ":" + IdGenerator.NewId(),
            TaskID = task.ID,
            ChannelID = channelModel.ChannelID,
            ChannelModelID = channelModel.ID,
            Model = logicalModel.Code,
            Capability = capability,
            Scene = LogicalModelService.TruncateRunes(scene, 80),
            BillingMode = logicalModel.BillingMode,
            PriceVersion = revision?.Version ?? 0,
            UnitPriceMicrocredits = logicalModel.UnitPriceMicrocredits,
            MultiplierBasisPoints = 10_000,
            Quantity = quantity,
            AmountMicrocredits = amount,
            ReservedAmountMicrocredits = amount,
            InputTokenPriceMicrocredits = logicalModel.InputPriceMicrocredits,
            OutputTokenPriceMicrocredits = logicalModel.OutputPriceMicrocredits,
            CachedTokenPriceMicrocredits = logicalModel.CachedPriceMicrocredits,
            Status = "reserved",
        };
    }

    // ------------------------------------------------------------ 项目守卫与谓词

    /// <summary>项目/画布归属与归档守卫。对应 Go: <c>ensureTaskProjectActive</c>。</summary>
    internal async Task EnsureTaskProjectActiveAsync(
        string userId, string canvasOrProjectId, CancellationToken cancellationToken)
    {
        string id = canvasOrProjectId.Trim();
        if (id.Length == 0)
        {
            return;
        }
        Domain.Entities.CanvasProject? canvas = await _repository
            .CanvasProjectForUserAsync(userId, id, cancellationToken).ConfigureAwait(false);
        if (canvas is not null)
        {
            if (canvas.ProjectID.Length == 0)
            {
                return;
            }
            Domain.Entities.Project? owner = await _repository
                .ProjectForUserAsync(userId, canvas.ProjectID, cancellationToken).ConfigureAwait(false);
            if (owner is not null && owner.Status == ProjectStatus.ProjectStatusArchived)
            {
                throw AppError.BadAuthRequest("项目已归档，无法创建生成任务");
            }
            return;
        }
        Domain.Entities.Project? project = await _repository
            .ProjectForUserAsync(userId, id, cancellationToken).ConfigureAwait(false);
        if (project is not null && project.Status == ProjectStatus.ProjectStatusArchived)
        {
            throw AppError.BadAuthRequest("项目已归档，无法创建生成任务");
        }
    }

    /// <summary>自定义渠道判定。对应 Go: <c>taskInputUsesCustomChannel</c>。</summary>
    private static bool TaskInputUsesCustomChannel(Dictionary<string, JsonElement> input)
    {
        if (TaskInputUsesWorkflowProvider(input))
        {
            return false;
        }
        if (!input.TryGetValue("config", out JsonElement element) || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        string channelId = ObjectString(element, "channelId").Trim();
        string baseUrl = ObjectString(element, "baseUrl").Trim();
        string apiKey = ObjectString(element, "apiKey").Trim();
        if (channelId.Length > 0 || SystemChannelIDFromBaseURL(baseUrl).Length > 0)
        {
            return false;
        }
        return baseUrl.Length > 0 && apiKey.Length > 0;
    }

    /// <summary>系统渠道判定。对应 Go: <c>taskInputUsesSystemChannel</c>。</summary>
    private static bool TaskInputUsesSystemChannel(Dictionary<string, JsonElement> input)
    {
        if (!input.TryGetValue("config", out JsonElement element) || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        return ObjectString(element, "channelId").Trim().Length > 0;
    }

    /// <summary>工作流协议判定。对应 Go: <c>taskInputUsesWorkflowProvider</c>。</summary>
    private static bool TaskInputUsesWorkflowProvider(Dictionary<string, JsonElement> input)
    {
        if (!input.TryGetValue("config", out JsonElement element) || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (ObjectString(element, "channelId").Trim().Length > 0)
        {
            return false;
        }
        return IsRunningHubInterface(ObjectString(element, "interfaceType").Trim());
    }

    /// <summary>视频任务可执行配置。对应 Go: <c>hasExecutableProviderVideoConfig</c>。</summary>
    private static bool HasExecutableProviderVideoConfig(Dictionary<string, JsonElement> input)
    {
        if (!input.TryGetValue("mode", out JsonElement mode) || mode.GetString() != "video")
        {
            return false;
        }
        if (!input.TryGetValue("config", out JsonElement element) || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        string interfaceType = ObjectString(element, "interfaceType");
        if (IsRunningHubInterface(interfaceType))
        {
            if (ObjectString(element, "workflowId").Length == 0
                && ObjectString(element, "webappId").Length == 0
                && ObjectString(element, "model").Length == 0)
            {
                return false;
            }
            return ObjectString(element, "baseUrl").Length > 0 && ObjectString(element, "apiKey").Length > 0;
        }
        if (ObjectString(element, "model").Length == 0)
        {
            return false;
        }
        return ObjectString(element, "channelId").Length > 0
            || (ObjectString(element, "baseUrl").Length > 0 && ObjectString(element, "apiKey").Length > 0);
    }

    /// <summary>中转地址里的系统渠道 ID。对应 Go: <c>systemChannelIDFromBaseURL</c>。</summary>
    private static string SystemChannelIDFromBaseURL(string baseURL)
    {
        string value = baseURL.Trim();
        string lower = value.ToLowerInvariant();
        foreach (string marker in new[] { "/api/ai/system/", "/api/" })
        {
            int index = lower.LastIndexOf(marker, StringComparison.Ordinal);
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
            int slash = id.IndexOf('/');
            if (slash >= 0)
            {
                continue;
            }
            id = id.Trim();
            if (id.Length == 0)
            {
                continue;
            }
            switch (id.ToLowerInvariant())
            {
                case "v1":
                case "v1beta":
                case "v2":
                case "v3":
                case "plan":
                case "ai":
                    continue;
                default:
                    return id;
            }
        }
        return "";
    }

    /// <summary>渠道 API 格式推导。对应 Go: <c>channelAPIFormatForProtocol</c>。</summary>
    private static string ChannelAPIFormatForProtocol(string channelDefault, string protocol) => protocol switch
    {
        ChannelInterfaceType.ChannelInterfaceGeminiVeo
            or ChannelInterfaceType.ChannelInterfaceGeminiImage => "gemini",
        ChannelInterfaceType.ChannelInterfaceClaudeAPI => "claude",
        "" => channelDefault.Trim(),
        _ => "openai",
    };

    /// <summary>能力默认值回填。对应 Go: <c>applyChannelCapabilityDefaults</c>。</summary>
    private static void ApplyChannelCapabilityDefaults(
        Dictionary<string, JsonElement> config, string capability, ModelCapabilityConfig? profile)
    {
        void SetDefault(string key, string value)
        {
            if (!config.TryGetValue(key, out JsonElement existing)
                || existing.ValueKind == JsonValueKind.Null
                || BlankScalar(existing))
            {
                config[key] = JsonSerializer.SerializeToElement(value);
            }
        }
        switch (CapabilitySpecOps.NormalizeCapability(capability))
        {
            case "image":
                if (profile?.Image is null)
                {
                    return;
                }
                if (profile.Image.Size.Parameter != "none")
                {
                    SetDefault("size", profile.Image.Size.Default);
                }
                if (profile.Image.Quality.Supported)
                {
                    SetDefault("quality", profile.Image.Quality.Default);
                }
                SetDefault("transparentBackground", profile.Image.TransparentBackground.Default
                    ? "true"
                    : "false");
                SetDefault("count", "1");
                break;
            case "video":
                if (profile?.Video is null)
                {
                    return;
                }
                if (profile.Video.DurationSupported is null or true)
                {
                    SetDefault("videoSeconds", profile.Video.Duration.Default.ToString());
                }
                SetDefault("size", profile.Video.DefaultRatio);
                SetDefault("vquality", profile.Video.DefaultResolution);
                SetDefault("videoGenerateAudio", profile.Video.GenerateAudio.Default
                    ? "true"
                    : "false");
                SetDefault("videoWatermark", profile.Video.Watermark.Default
                    ? "true"
                    : "false");
                break;
        }
    }

    /// <summary>从执行配置提取能力参数。对应 Go: <c>capabilityOptionsFromConfig</c>。</summary>
    private static Dictionary<string, JsonElement> CapabilityOptionsFromConfig(
        string capability,
        Dictionary<string, JsonElement> config,
        IReadOnlyDictionary<string, OptionConstraint>? declared)
    {
        Dictionary<string, JsonElement> options = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonElement> pair in config)
        {
            string canonical = CapabilitySpecOps.CanonicalCapabilityOptionName(pair.Key);
            if (!CapabilitySpecOps.IsCapabilityOptionFor(capability, canonical)
                || pair.Value.ValueKind == JsonValueKind.Null
                || BlankScalar(pair.Value))
            {
                continue;
            }
            if (declared is not null && !declared.ContainsKey(canonical))
            {
                continue;
            }
            options[canonical] = pair.Value.Clone();
        }
        return options;
    }

    /// <summary>任务输入 → 路由意图。对应 Go: <c>ModelRequestIntentFromTaskInput</c>。</summary>
    internal static ModelRequestIntent ModelRequestIntentFromTaskInput(
        Dictionary<string, JsonElement> input, string taskType, string operation)
    {
        string capability = CapabilitySpecOps.NormalizeCapability(ScalarText(GetValueOrDefault(input, "mode")));
        if (capability.Length == 0)
        {
            capability = CapabilityFromTaskType(taskType);
        }
        ModelRequestIntent intent = new()
        {
            Capability = capability,
            Operation = operation.Trim(),
            Inputs = new Dictionary<string, long>(StringComparer.Ordinal),
            Options = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
        };
        foreach ((string inputType, string key) in new[]
                 {
                     ("image", "referenceImages"),
                     ("video", "referenceVideos"),
                     ("audio", "referenceAudios"),
                 })
        {
            if (input.TryGetValue(key, out JsonElement values) && values.ValueKind == JsonValueKind.Array)
            {
                intent.Inputs[inputType] = values.GetArrayLength();
            }
        }
        if (input.TryGetValue("mask", out JsonElement mask) && mask.ValueKind != JsonValueKind.Null)
        {
            intent.Inputs["mask"] = 1;
        }
        bool explicitOptions = false;
        if (input.TryGetValue("capabilityOptions", out JsonElement optionsElement)
            && optionsElement.ValueKind == JsonValueKind.Object)
        {
            explicitOptions = true;
            foreach (JsonProperty property in optionsElement.EnumerateObject())
            {
                string name = CapabilitySpecOps.CanonicalCapabilityOptionName(property.Name);
                if (name == "quality"
                    && property.Value.ValueKind == JsonValueKind.String
                    && (string.Equals((property.Value.GetString() ?? "").Trim(), "auto", StringComparison.OrdinalIgnoreCase)
                        || string.Equals((property.Value.GetString() ?? "").Trim(), "any", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                intent.Options[name] = CapabilitySpecOps.NormalizeModelRequestOption(name, property.Value);
            }
        }
        if (!explicitOptions && input.TryGetValue("config", out JsonElement configElement)
            && configElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in configElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "channelId":
                    case "apiFormat":
                    case "interfaceType":
                    case "baseUrl":
                    case "apiKey":
                    case "secretKey":
                    case "headers":
                    case "model":
                    case "capabilityConfig":
                        continue;
                    default:
                    {
                        string canonical = CapabilitySpecOps.CanonicalCapabilityOptionName(property.Name);
                        if (CapabilitySpecOps.IsCapabilityOptionFor(capability, canonical)
                            && property.Value.ValueKind != JsonValueKind.Null
                            && !BlankScalar(property.Value))
                        {
                            intent.Options[canonical] = CapabilitySpecOps.NormalizeModelRequestOption(
                                canonical, property.Value);
                        }
                        break;
                    }
                }
            }
        }
        return intent;
    }

    /// <summary>任务类型 → 能力。对应 Go: <c>capabilityFromTaskType</c>。</summary>
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
        if (value.Contains("storyboard", StringComparison.Ordinal)
            || value.Contains("agent", StringComparison.Ordinal))
        {
            return "text";
        }
        return "";
    }

    private static string ResolveCapability(Dictionary<string, JsonElement> input, string taskType)
    {
        string capability = CapabilitySpecOps.NormalizeCapability(ScalarText(GetValueOrDefault(input, "mode")));
        return capability.Length > 0 ? capability : CapabilityFromTaskType(taskType);
    }

    private static Dictionary<string, long>? IntentInputs(ModelRequestIntent intent) =>
        intent.Inputs.Count > 0 ? intent.Inputs : null;

    // ------------------------------------------------------------ 取值辅助

    private static Dictionary<string, JsonElement> InputConfig(Dictionary<string, JsonElement> input)
    {
        if (!input.TryGetValue("config", out JsonElement element) || element.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
        Dictionary<string, JsonElement> config = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            config[property.Name] = property.Value.Clone();
        }
        return config;
    }

    private static string InputConfigString(Dictionary<string, JsonElement> input, string key) =>
        input.TryGetValue("config", out JsonElement element) && element.ValueKind == JsonValueKind.Object
            ? ObjectString(element, key)
            : "";

    private static string ObjectString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(key, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

    private static string ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };

    private static JsonElement GetValueOrDefault(Dictionary<string, JsonElement> map, string key) =>
        map.TryGetValue(key, out JsonElement value) ? value : JsonSerializer.SerializeToElement(JsonNull.Value);

    private static bool BlankScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => (value.GetString() ?? "").Trim().Length == 0,
        JsonValueKind.Undefined or JsonValueKind.Null => true,
        _ => false,
    };

    private static JsonElement ProviderConfigOptionValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => JsonSerializer.SerializeToElement(value.GetString() ?? ""),
        JsonValueKind.Number => JsonSerializer.SerializeToElement(value.GetRawText()),
        JsonValueKind.True => JsonSerializer.SerializeToElement("true"),
        JsonValueKind.False => JsonSerializer.SerializeToElement("false"),
        _ => value.Clone(),
    };

    /// <summary>从持久化记录恢复权威能力合同（与 ModelCatalogService 共用语义）。</summary>
    private static ModelCapabilityConfig? NormalizedChannelModelCapability(ChannelModel channelModel)
    {
        string capability = CapabilitySpecOps.NormalizeCapability(channelModel.Capability);
        if (capability == "audio")
        {
            return null;
        }
        if (capability is not ("text" or "image" or "video"))
        {
            throw new InvalidOperationException($"不支持的渠道模型能力：{channelModel.Capability}");
        }
        ModelCapabilityConfig? config;
        try
        {
            config = ModelCapabilityConfigOps.DecodeModelCapabilityConfig(channelModel.CapabilityConfigJSON);
        }
        catch (AppError error)
        {
            throw new InvalidOperationException($"解析渠道模型能力配置失败：{error.Message}");
        }
        return ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
            capability,
            channelModel.Protocol,
            LogicalModelService.FirstNonEmpty(channelModel.ProviderModelKey, channelModel.ModelKey),
            config);
    }

    /// <summary>价格档价格自洽校验（与 ModelCatalogService 一致）。对应 Go: <c>ValidatePriceTierPrice</c>。</summary>
    private static bool ValidatePriceTierPrice(
        ChannelModelPriceTier tier, string capability, string protocol)
    {
        switch (tier.BillingMode)
        {
            case "fixed_request":
            case "per_second":
                return tier.UnitPriceMicrocredits >= 0;
            case "token":
                if (capability == "video")
                {
                    return protocol == ChannelInterfaceType.ChannelInterfaceVolcengineArkVideo
                        && tier.InputTokenPriceMicrocredits >= 0
                        && tier.OutputTokenPriceMicrocredits >= 0
                        && tier.CachedTokenPriceMicrocredits >= 0;
                }
                if (capability.Length > 0 && capability != "text")
                {
                    return false;
                }
                return tier.InputTokenPriceMicrocredits >= 0
                    && tier.OutputTokenPriceMicrocredits >= 0
                    && tier.CachedTokenPriceMicrocredits >= 0;
            default:
                return false;
        }
    }
}

/// <summary>JSON null 占位（供 GetValueOrDefault 返回）。</summary>
internal static class JsonNull
{
    public static readonly object? Value = null;
}
