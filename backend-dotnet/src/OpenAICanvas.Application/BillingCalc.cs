#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Application;

/// <summary>
/// 计费估算纯函数。对应 Go: <c>finance.go</c> 的
/// <c>billingQuantity</c> / <c>estimateTaskBillingTokens</c> / <c>creditAmount</c> /
/// <c>tokenEstimateAmount</c> 与 <c>logical_model_quote.go</c> 的 <c>quoteInput</c>。
/// </summary>
public static class BillingCalc
{
    /// <summary>报价输入上下文（Go 用 map[string]any，这里收紧为显式形状）。</summary>
    public sealed record QuoteInputContext(
        string Mode,
        string ModelKey,
        Dictionary<string, JsonElement> Config,
        long ReferenceVideoCount)
    {
        /// <summary>对应 Go: <c>inputConfigValue(input, "videoSeconds")</c>。缺失时为 null。</summary>
        public JsonElement? VideoSeconds =>
            Config.TryGetValue("videoSeconds", out JsonElement value) ? value : null;
    }

    public readonly record struct TokenBillingEstimate(long InputTokens, long OutputTokens);

    /// <summary>
    /// 构造报价用的输入结构。对应 Go: <c>quoteInput</c>。
    /// 参考视频只放占位（Go 为 nil 元素），供方舟 token 公式按“未知时长 → 15 秒上限”预留。
    /// </summary>
    public static QuoteInputContext QuoteInput(
        string capability,
        string modelKey,
        Dictionary<string, JsonElement> options,
        Dictionary<string, long>? inputs)
    {
        Dictionary<string, JsonElement> config = new(options, StringComparer.Ordinal);
        long referenceVideos = inputs is not null && inputs.TryGetValue("video", out long count) ? count : 0;
        return new QuoteInputContext(capability, modelKey, config, referenceVideos);
    }

    /// <summary>对应 Go: <c>billingQuantity</c>。非视频恒为 1；视频取时长，无效为 0。</summary>
    public static long BillingQuantity(string capability, JsonElement? value)
    {
        if (capability != "video")
        {
            return 1;
        }
        string text = SprintValue(value);
        if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long quantity) ||
            quantity <= 0)
        {
            return 0;
        }
        return quantity;
    }

    /// <summary>对应 Go: <c>estimateTaskBillingTokens</c>。</summary>
    public static TokenBillingEstimate EstimateTaskBillingTokens(QuoteInputContext input, string capability)
    {
        if (capability == "video")
        {
            return EstimateArkVideoTokens(input);
        }
        return EstimateTaskTokens(input);
    }

    /// <summary>对应 Go: <c>estimateTaskTokens</c>。输入 token 按序列化 JSON 的字符数 /4 估算。</summary>
    private static TokenBillingEstimate EstimateTaskTokens(QuoteInputContext input)
    {
        string encoded = SerializeQuoteInput(input);
        return new TokenBillingEstimate(
            InputTokens: EstimatedTokens(encoded),
            OutputTokens: MaxOutputTokens(input.Config));
    }

    /// <summary>
    /// 与 Go 的 <c>json.Marshal(input)</c> 等价：map 键按字典序、参考视频为 null 占位数组。
    /// </summary>
    private static string SerializeQuoteInput(QuoteInputContext input)
    {
        var references = new object?[input.ReferenceVideoCount];
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["config"] = input.Config,
            ["mode"] = input.Mode,
            ["referenceVideos"] = references,
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>对应 Go: <c>estimatedTokens</c>。</summary>
    private static long EstimatedTokens(string value)
    {
        long count = (value.Length + 3) / 4;
        return count < 1 ? 1 : count;
    }

    /// <summary>对应 Go: <c>maxOutputTokens</c>。查找显式上限键，缺省 4096，封顶 131072。</summary>
    private static long MaxOutputTokens(Dictionary<string, JsonElement> payload)
    {
        foreach (string key in new[] { "max_output_tokens", "max_tokens", "maxOutputTokens" })
        {
            if (payload.TryGetValue(key, out JsonElement value))
            {
                string text = SprintValue(value).Trim();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) &&
                    parsed > 0)
                {
                    return Math.Min(parsed, 131072);
                }
            }
        }
        return 4096;
    }

    // ------------------------------------------------------------ 方舟视频 token 估算

    /// <summary>
    /// 方舟视频成功后才返回真实 completion_tokens；创建任务前按官方像素帧公式预授权，
    /// 并保留少量帧率/取整余量，实际结算时会按 usage 自动退回差额。
    /// 对应 Go: <c>estimateArkVideoTokens</c>。
    /// </summary>
    private static TokenBillingEstimate EstimateArkVideoTokens(QuoteInputContext input)
    {
        JsonElement? videoSeconds = input.VideoSeconds;
        if (videoSeconds is null)
        {
            return default;
        }
        if (!long.TryParse(SprintValue(videoSeconds).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long durationSeconds) ||
            durationSeconds <= 0)
        {
            return default;
        }

        string vquality = input.Config.TryGetValue("vquality", out JsonElement quality)
            ? SprintValue(quality)
            : "";
        string size = input.Config.TryGetValue("size", out JsonElement sizeElement) ? SprintValue(sizeElement) : "";
        long pixels = ArkVideoOutputPixels(vquality, size, input.ModelKey);
        if (pixels <= 0)
        {
            return default;
        }

        if (durationSeconds > long.MaxValue / 1000)
        {
            return default;
        }
        long totalDurationMillis = durationSeconds * 1000;
        long referenceCount = 0;
        if (input.ReferenceVideoCount > 0)
        {
            referenceCount = input.ReferenceVideoCount;
            // 方舟参考视频总时长上限为 15 秒；缺少媒体元数据时按上限预留。
            totalDurationMillis += 15_000;
        }

        long frames = checked((totalDurationMillis * 24 + 999) / 1000) + 1 + referenceCount;
        if (pixels > (long.MaxValue - 1023) / frames)
        {
            return default;
        }
        long tokens = (pixels * frames + 1023) / 1024;
        if (tokens > (long.MaxValue - 99) / 110)
        {
            return default;
        }
        return new TokenBillingEstimate(0, (tokens * 110 + 99) / 100);
    }

    /// <summary>对应 Go: <c>arkVideoOutputPixels</c>。</summary>
    public static long ArkVideoOutputPixels(string resolution, string ratio, string modelName)
    {
        resolution = NormalizeSeedanceResolution(resolution, modelName);
        ratio = NormalizeSeedanceRatio(ratio);

        Dictionary<string, long> values = resolution switch
        {
            "480p" => new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["16:9"] = 864L * 496, ["4:3"] = 752L * 560, ["1:1"] = 640L * 640,
                ["3:4"] = 560L * 752, ["9:16"] = 496L * 864, ["21:9"] = 992L * 432,
            },
            "720p" => new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["16:9"] = 1280L * 720, ["4:3"] = 1112L * 834, ["1:1"] = 960L * 960,
                ["3:4"] = 834L * 1112, ["9:16"] = 720L * 1280, ["21:9"] = 1470L * 630,
            },
            "1080p" => new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["16:9"] = 1920L * 1080, ["4:3"] = 1664L * 1248, ["1:1"] = 1440L * 1440,
                ["3:4"] = 1248L * 1664, ["9:16"] = 1080L * 1920, ["21:9"] = 2206L * 946,
            },
            _ => [],
        };

        if (resolution == "2160p")
        {
            // 与 Go 一致：4K 按 1080p 面积 ×4 推导。
            values = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["16:9"] = 1920L * 1080 * 4, ["4:3"] = 1664L * 1248 * 4, ["1:1"] = 1440L * 1440 * 4,
                ["3:4"] = 1248L * 1664 * 4, ["9:16"] = 1080L * 1920 * 4, ["21:9"] = 2206L * 946 * 4,
            };
        }

        if (values.Count == 0)
        {
            return 0;
        }
        if (ratio != "adaptive")
        {
            return values.TryGetValue(ratio, out long value) ? value : 0;
        }
        long largest = 0;
        foreach (long value in values.Values)
        {
            largest = Math.Max(largest, value);
        }
        return largest;
    }

    /// <summary>对应 Go: <c>normalizeSeedanceResolution</c>（provider_video_options.go）。</summary>
    public static string NormalizeSeedanceResolution(string value, string model)
    {
        string resolution = value.Trim();
        if (resolution.EndsWith("p", StringComparison.Ordinal))
        {
            resolution = resolution[..^1];
        }
        if (string.Equals(resolution, "4k", StringComparison.OrdinalIgnoreCase))
        {
            resolution = "2160";
        }
        switch (resolution)
        {
            case "480":
            case "720":
            case "1080":
            case "2160":
                break;
            default:
                resolution = value == "low" ? "480" : "720";
                break;
        }
        if (model.ToLowerInvariant().Contains("fast", StringComparison.Ordinal) &&
            (resolution == "1080" || resolution == "2160"))
        {
            resolution = "720";
        }
        return resolution + "p";
    }

    /// <summary>对应 Go: <c>normalizeSeedanceRatio</c>。</summary>
    public static string NormalizeSeedanceRatio(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value is "auto" or "adaptive")
        {
            return "adaptive";
        }
        return value switch
        {
            "16:9" or "9:16" or "1:1" or "4:3" or "3:4" or "21:9" => value,
            _ => "adaptive",
        };
    }

    // ------------------------------------------------------------ 金额计算

    /// <summary>
    /// 单价 × 数量 × 倍率，全程整数并向上取整，避免浮点误差造成少扣积分。
    /// 对应 Go: <c>creditAmount</c>。
    /// </summary>
    public static long CreditAmount(long unitPrice, long quantity, long multiplierBps)
    {
        if (unitPrice < 0 || quantity <= 0 || multiplierBps <= 0)
        {
            throw new InvalidOperationException("积分计费参数无效");
        }
        if (unitPrice > long.MaxValue / quantity)
        {
            throw new InvalidOperationException("积分计费金额溢出");
        }
        long basis = unitPrice * quantity;
        if (basis > (long.MaxValue - 9_999) / multiplierBps)
        {
            throw new InvalidOperationException("积分计费金额溢出");
        }
        long amount = (basis * multiplierBps + 9_999) / 10_000;
        if (amount < 0)
        {
            throw new InvalidOperationException($"积分计费金额无效：{amount}");
        }
        return amount;
    }

    /// <summary>
    /// Token 单价按每百万 Token 配置；预授权使用输入价估算缓存 Token，真实结算再按 usage 拆分。
    /// 对应 Go: <c>tokenEstimateAmount</c>。
    /// </summary>
    public static long TokenEstimateAmount(ChannelModel prices, TokenBillingEstimate estimate, long multiplierBps)
    {
        if (estimate.InputTokens < 0 || estimate.OutputTokens <= 0 || multiplierBps <= 0)
        {
            throw new InvalidOperationException("Token 计费参数无效");
        }
        if (!SafeTokenProduct(estimate.InputTokens, prices.InputTokenPriceMicrocredits, out long inputAmount))
        {
            throw new InvalidOperationException("Token 计费金额溢出");
        }
        if (!SafeTokenProduct(estimate.OutputTokens, prices.OutputTokenPriceMicrocredits, out long outputAmount) ||
            inputAmount > long.MaxValue - outputAmount)
        {
            throw new InvalidOperationException("Token 计费金额溢出");
        }
        long basis = inputAmount + outputAmount;
        if (basis > (long.MaxValue - 9_999_999_999) / multiplierBps)
        {
            throw new InvalidOperationException("Token 计费金额溢出");
        }
        long amount = (basis * multiplierBps + 9_999_999_999) / 10_000_000_000;
        if (amount <= 0)
        {
            throw new InvalidOperationException("Token 计费金额必须大于 0");
        }
        return amount;
    }

    private static bool SafeTokenProduct(long tokens, long price, out long product)
    {
        product = 0;
        if (tokens < 0 || price < 0 || (tokens > 0 && price > long.MaxValue / tokens))
        {
            return false;
        }
        product = tokens * price;
        return true;
    }

    /// <summary>Go <c>fmt.Sprint</c> 的标量等价物；缺失/空值返回可识别的占位（解析必失败）。</summary>
    private static string SprintValue(JsonElement? value)
    {
        if (value is null)
        {
            return "<nil>";
        }
        return value.Value.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString() ?? "",
            JsonValueKind.Number => value.Value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "<nil>",
            _ => value.Value.GetRawText(),
        };
    }
}
