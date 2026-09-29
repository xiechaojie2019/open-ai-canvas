#nullable enable
using System.Text.Json;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application;

/// <summary>
/// 前台模型任务的备用线路切换与成本重算。
/// 对应 Go: <c>switchTaskToNextRoute</c>（model_router.go）的核心编排；
/// RouteAttempt 状态机与执行器循环仍由任务执行层承载（.NET 直连执行，见 PENDING）。
/// </summary>
public sealed partial class LogicalModelService
{
    /// <summary>切线路结果：新输入与替换订单由调用方随任务 CAS 写回。</summary>
    public sealed record TaskRouteSwitchResult(
        RoutedModel Routed,
        Dictionary<string, JsonElement> Input,
        BillingOrder? Replacement,
        TaskRouteCostSnapshot Cost);

    /// <summary>
    /// 为失败的运行中任务选择备用线路并重算成本：排除已尝试路由 → 重放路由选型 →
    /// 校验能力与密钥 → channel 计费时创建替换订单 → 事务写回（任务 CAS + 成本快照 + 积分差额）。
    /// 抛出 <see cref="TaskRouteChargeLimitException"/>（超批准上限）、
    /// <see cref="InsufficientCreditsException"/>（余额不足）或 <see cref="TaskRouteConflictException"/>。
    /// </summary>
    public async Task<TaskRouteSwitchResult> SwitchTaskToNextRouteAsync(
        TaskEntity task,
        IReadOnlyCollection<string> triedRouteIDs,
        CancellationToken cancellationToken = default)
    {
        if (task.LogicalModelID.Length == 0)
        {
            throw AppError.BadAuthRequest("任务未绑定前台模型，无法切换线路");
        }
        Dictionary<string, JsonElement> input;
        try
        {
            input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                task.InputJSON, ProjectCharacterService.GoPayloadOptions) ?? [];
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("任务输入格式无效，无法恢复模型服务");
        }
        LogicalModel? logicalModel = await _repository.LogicalModelAsync(task.LogicalModelID, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("任务使用的模型服务不存在");
        LogicalModelRevision? revision = await _repository
            .LogicalModelRevisionAsync(task.LogicalModelRevisionID, cancellationToken).ConfigureAwait(false);
        if (revision is null || revision.LogicalModelID != logicalModel.ID)
        {
            throw AppError.BadAuthRequest("任务前台模型版本不存在或归属不一致");
        }
        CapabilitySpec productSpec = CapabilitySpecOps.DecodeCapabilitySpec(revision.CapabilitySpecJSON);
        Dictionary<string, JsonElement> defaults =
            CapabilitySpecOps.DecodeLogicalDefaults(revision.DefaultOptionsJSON, productSpec);
        ModelRequestIntent intent = TaskCreationService.ModelRequestIntentFromTaskInput(
            input, task.Type, task.Operation);
        intent.Options = CapabilitySpecOps.MergeIntentDefaults(intent.Options, defaults);
        CapabilityMatch match = CapabilitySpecOps.MatchCapability(productSpec, intent);
        if (!match.Matched)
        {
            throw AppError.BadAuthRequest("任务参数不再符合前台模型能力：" + string.Join("；", match.Reasons ?? []));
        }

        // 备用线路选型：排除本轮已尝试路由（与 Go 的 eligibleLogicalRoutes(tried) 一致）。
        RoutedModel routed = await ResolveLogicalModelWithTriedRoutesAsync(
            logicalModel, revision, productSpec, intent, triedRouteIDs, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, JsonElement> nextInput =
            TaskCreationService.ApplyRoutedProviderSelectionPublic(input, routed);

        BillingOrder? replacement = null;
        TaskRouteCostSnapshot cost;
        if (logicalModel.PricePolicy == "channel" && task.BillingOrderID.Length > 0)
        {
            // 与 Go 一致：优先 intent.Capability（归一化后的能力），回退任务类型推导。
            string capability = CapabilitySpecOps.NormalizeCapability(intent.Capability);
            if (capability.Length == 0)
            {
                capability = TaskCreationService.CapabilityFromTaskTypePublic(task.Type);
            }
            long quantity = BillingCalc.BillingQuantity(
                capability, BillingCalc.QuoteInput(intent.Capability, routed.ChannelModel.ModelKey, intent.Options, intent.Inputs).VideoSeconds);
            BillingCalc.TokenBillingEstimate tokenEstimate = BillingCalc.EstimateTaskBillingTokens(
                BillingCalc.QuoteInput(intent.Capability, routed.ChannelModel.ModelKey, intent.Options, intent.Inputs),
                capability);
            replacement = await BuildBillingOrderWithPriceTierAsync(
                userID: task.UserID,
                taskId: task.ID,
                idempotencyKey: "route-switch:" + task.ID + ":" + routed.Route.ID,
                channelId: routed.ChannelModel.ChannelID,
                modelKey: routed.ChannelModel.ModelKey,
                capability: capability,
                scene: FirstNonEmpty(task.Operation, task.Type),
                requestedQuantity: quantity,
                tokenEstimate: tokenEstimate,
                priceTierId: routed.PriceTier?.ID ?? "",
                intent: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            replacement.Model = logicalModel.Code;
            cost = TaskRouteCostSnapshot.FromOrder(replacement);
        }
        else if (task.BillingOrderID.Length > 0)
        {
            // 统一价格：成本快照按备用线路的渠道模型重算，不反查当前价格表。
            BillingOrder snapshot = new()
            {
                CostConfigured = routed.PriceTier?.CostConfigured ?? false,
                CostUnitPriceMicrocredits = routed.PriceTier?.CostUnitPriceMicrocredits ?? 0,
                CostInputTokenPriceMicrocredits = routed.PriceTier?.CostInputTokenPriceMicrocredits ?? 0,
                CostOutputTokenPriceMicrocredits = routed.PriceTier?.CostOutputTokenPriceMicrocredits ?? 0,
                CostCachedTokenPriceMicrocredits = routed.PriceTier?.CostCachedTokenPriceMicrocredits ?? 0,
                CostBillingMode = routed.PriceTier?.BillingMode ?? "",
                CostQuantity = 1,
            };
            string capability = routed.ChannelModel.Capability;
            if (capability == "video" && routed.PriceTier is { BillingMode: "per_second" })
            {
                snapshot.CostQuantity = BillingCalc.BillingQuantity(
                    capability,
                    BillingCalc.QuoteInput(intent.Capability, routed.ChannelModel.ModelKey, intent.Options, intent.Inputs).VideoSeconds);
            }
            CreditCostOps.Snapshot(
                snapshot, routed.PriceTier,
                BillingCalc.BillingQuantity(
                    capability,
                    BillingCalc.QuoteInput(intent.Capability, routed.ChannelModel.ModelKey, intent.Options, intent.Inputs).VideoSeconds),
                BillingCalc.EstimateTaskBillingTokens(
                    BillingCalc.QuoteInput(intent.Capability, routed.ChannelModel.ModelKey, intent.Options, intent.Inputs),
                    capability));
            cost = TaskRouteCostSnapshot.FromOrder(snapshot);
        }
        else
        {
            cost = new TaskRouteCostSnapshot(false, 0, 0, 0, 0, "", 0, 0);
        }

        await _repository.SwitchTaskLogicalRouteAsync(
            task.ID,
            expectedRouteID: task.RouteID,
            routeID: routed.Route.ID,
            inputJSON: TaskCreationService.SerializeInput(nextInput),
            billingOrderID: task.BillingOrderID,
            channelID: routed.ChannelModel.ChannelID,
            channelModelID: routed.ChannelModel.ID,
            replacement,
            cost,
            cancellationToken).ConfigureAwait(false);

        task.RouteID = routed.Route.ID;
        task.ChannelModelID = routed.ChannelModel.ID;
        task.InputJSON = TaskCreationService.SerializeInput(nextInput);
        task.ProviderRequestID = "";
        return new TaskRouteSwitchResult(routed, nextInput, replacement, cost);
    }

    /// <summary>带已尝试排除的线路解析。对应 Go: <c>eligibleLogicalRoutes(candidates, intent, tried)</c>。</summary>
    private async Task<RoutedModel> ResolveLogicalModelWithTriedRoutesAsync(
        LogicalModel logicalModel,
        LogicalModelRevision revision,
        CapabilitySpec productSpec,
        ModelRequestIntent intent,
        IReadOnlyCollection<string> triedRouteIDs,
        CancellationToken cancellationToken)
    {
        RouteCatalogSnapshot snapshot = await RouteCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Models.TryGetValue(logicalModel.ID, out CachedLogicalModel? cached)
            || cached.Revision.ID != revision.ID)
        {
            throw AppError.BadAuthRequest("任务使用的模型服务价格配置已失效");
        }
        bool requirePriceTier = logicalModel.PricePolicy == "channel";
        List<CachedLogicalRoute> eligible = EligibleLogicalRoutes(
            cached.Routes, intent, new HashSet<string>(triedRouteIDs, StringComparer.Ordinal), requirePriceTier);
        if (eligible.Count == 0)
        {
            throw AppError.BadAuthRequest("当前模型暂时无法满足这组输入和参数");
        }
        CachedLogicalRoute selected = WeightedRoute(eligible);
        ChannelModelPriceTier? priceTier = null;
        if (requirePriceTier)
        {
            priceTier = ModelSku.ChannelModelPriceTierForIntent(selected.ChannelModel, intent);
            if (priceTier is null)
            {
                throw AppError.BadAuthRequest("当前模型尚未配置所选规格的价格");
            }
        }
        return new RoutedModel
        {
            LogicalModel = cached.Model,
            Revision = cached.Revision,
            Route = selected.Route,
            ChannelModel = selected.ChannelModel,
            PriceTier = priceTier,
            Defaults = cached.Defaults,
        };
    }

    private static string FirstNonEmpty(string first, string second) =>
        first.Trim().Length > 0 ? first : second;
}
