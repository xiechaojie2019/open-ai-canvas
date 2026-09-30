#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>
/// 渠道模型报价请求。对应 Go: <c>app.ChannelModelQuoteRequest</c>。
/// </summary>
public sealed class ChannelModelQuoteRequestDto
{
    [JsonPropertyName("channelId")]
    public string ChannelID { get; set; } = "";

    [JsonPropertyName("modelKey")]
    public string ModelKey { get; set; } = "";

    [JsonPropertyName("intent")]
    public ModelRequestIntent Intent { get; set; } = new();
}

public sealed partial class LogicalModelService
{
    /// <summary>
    /// 按当前参数命中的实际供应线路报价（与任务创建同一套 admission/默认值/SKU 选择，
    /// 但不预留积分、不提交供应商请求）。对应 Go: <c>QuoteChannelModel</c>。
    /// </summary>
    public async Task<LogicalModelQuoteDto> QuoteChannelModelAsync(
        ChannelModelQuoteRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request.ChannelID.Trim().Length == 0 || request.ModelKey.Trim().Length == 0)
        {
            throw AppError.New(422, "报价必须指定系统渠道和模型");
        }
        ValidateQuoteIntent(request.Intent);

        // quoteInput：options → config + 参考素材计数占位。
        Dictionary<string, JsonElement> config = new((request.Intent.Options ?? [])
            .Select(pair => new KeyValuePair<string, JsonElement>(pair.Key, pair.Value))
            .ToList())
        {
            ["model"] = JsonSerializer.SerializeToElement(request.ModelKey),
            ["channelId"] = JsonSerializer.SerializeToElement(request.ChannelID),
        };
        Dictionary<string, JsonElement> input = new(StringComparer.Ordinal)
        {
            ["mode"] = JsonSerializer.SerializeToElement(request.Intent.Capability),
            ["config"] = JsonSerializer.SerializeToElement(config),
        };
        foreach ((string kind, string key) in new[]
                 {
                     ("image", "referenceImages"), ("video", "referenceVideos"), ("audio", "referenceAudios"),
                 })
        {
            long count = request.Intent.Inputs.GetValueOrDefault(kind, 0);
            if (count > 0)
            {
                // 占位数组：报价只看数量（计费按引用数），不读内容。
                JsonElement[] placeholders = new JsonElement[count];
                Array.Fill(placeholders, JsonSerializer.SerializeToElement((string?)null));
                input[key] = JsonSerializer.SerializeToElement(placeholders);
            }
        }

        // 与任务创建一致的渠道模型解析（价格档/能力合同/默认规格收窄）。
        TaskCreationService admission = new(_repository);
        Dictionary<string, JsonElement> resolved = await admission.ResolveForQuoteAsync(
            input, "canvas_" + request.Intent.Capability, request.Intent.Operation, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, JsonElement> resolvedConfig = ConfigOf(resolved);
        BillingCalc.TokenBillingEstimate estimate = BillingCalc.EstimateTaskBillingTokens(
            QuoteInputContext(resolved, request.Intent),
            request.Intent.Capability);
        ModelRequestIntent effectiveIntent = TaskCreationService.ModelRequestIntentFromTaskInput(
            resolved, "canvas_" + request.Intent.Capability, request.Intent.Operation);
        string priceTierID = StringOf(resolvedConfig, "priceTierId");
        long videoSeconds = VideoSecondsOf(resolvedConfig);

        BillingOrder order = await BuildBillingOrderWithPriceTierAsync(
            userID: "",
            taskId: "",
            idempotencyKey: "quote",
            channelId: request.ChannelID,
            modelKey: request.ModelKey,
            capability: request.Intent.Capability,
            scene: "model_quote",
            requestedQuantity: BillingCalc.BillingQuantity(
                request.Intent.Capability,
                resolvedConfig.TryGetValue("videoSeconds", out JsonElement seconds) ? seconds : null),
            tokenEstimate: estimate,
            priceTierId: priceTierID,
            intent: effectiveIntent,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return QuoteFromOrder("", order, estimate);
    }

    /// <summary>报价意图校验。对应 Go: <c>validateQuoteIntent</c>。</summary>
    internal static void ValidateQuoteIntent(ModelRequestIntent intent)
    {
        // Inputs/Options 是可空字典（Go 反序列化零值等价空表）。
        intent.Inputs ??= [];
        intent.Options ??= [];
        if (intent.Capability is not ("text" or "image" or "video" or "audio"))
        {
            throw AppError.BadAuthRequest("报价请求缺少有效的模型能力类型");
        }
        foreach ((_, long count) in intent.Inputs)
        {
            if (count is < 0 or > 128)
            {
                throw AppError.BadAuthRequest("报价参考素材数量必须为 0 到 128 的整数");
            }
        }
    }

    internal static LogicalModelQuoteDto QuoteFromOrder(
        string logicalModelID, BillingOrder order, BillingCalc.TokenBillingEstimate estimate) =>
        new()
        {
            LogicalModelID = logicalModelID,
            BillingMode = order.BillingMode,
            Quantity = order.Quantity,
            AmountMicrocredits = order.AmountMicrocredits,
            Estimated = order.BillingMode == "token",
        };

    private static Dictionary<string, JsonElement> ConfigOf(Dictionary<string, JsonElement> input) =>
        input.TryGetValue("config", out JsonElement config) && config.ValueKind == JsonValueKind.Object
            ? config.EnumerateObject().ToDictionary(
                property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal)
            : [];

    private static string StringOf(Dictionary<string, JsonElement> config, string key) =>
        config.TryGetValue(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long VideoSecondsOf(Dictionary<string, JsonElement> config) =>
        config.TryGetValue("videoSeconds", out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long seconds)
            ? seconds
            : 0;

    private static BillingCalc.QuoteInputContext QuoteInputContext(
        Dictionary<string, JsonElement> resolved, ModelRequestIntent intent) =>
        BillingCalc.QuoteInput(
            intent.Capability,
            StringOf(ConfigOf(resolved), "model"),
            ConfigOf(resolved),
            intent.Inputs);
}
