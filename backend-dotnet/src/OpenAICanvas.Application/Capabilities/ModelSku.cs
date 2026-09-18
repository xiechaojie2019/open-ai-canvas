#nullable enable
using System.Globalization;
using System.Text.Json;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Application.Capabilities;

/// <summary>
/// SKU 选择器与价格档匹配。对应 Go: <c>model/model_sku.go</c> 与
/// <c>model_router.go</c> 的 <c>skuSelectorForIntent</c> / <c>skuSelectorForTier</c> /
/// <c>matchSKUSelector</c> / <c>channelModelPriceTierForIntent</c>。
/// </summary>
public static class ModelSku
{
    /// <summary>
    /// 规范化 SKU 选择器并输出规范 JSON。选择器值一律存字符串，
    /// 使同一 SKU 的数字与文本写法不会拆成两行价格。
    /// 对应 Go: <c>model.CanonicalSKUSelector</c>。
    /// </summary>
    public static (Dictionary<string, string> Selector, string Canonical) CanonicalSkuSelector(
        IReadOnlyDictionary<string, string> raw)
    {
        Dictionary<string, string> selector = new(StringComparer.Ordinal);
        foreach ((string rawKey, string rawValue) in raw)
        {
            string key = rawKey.Trim();
            string value = rawValue.Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                continue;
            }
            selector[key] = value;
        }
        // Go 的 encoding/json 按 key 字典序输出；这里保持插入序即构造序。
        var ordered = new Dictionary<string, string>(selector.OrderBy(pair => pair.Key, StringComparer.Ordinal), StringComparer.Ordinal);
        return (ordered, JsonSerializer.Serialize(ordered));
    }

    /// <summary>对应 Go: <c>model.DecodeSKUSelector</c>。损坏 JSON 静默返回空表。</summary>
    public static Dictionary<string, string> DecodeSkuSelector(string? raw)
    {
        Dictionary<string, string> selector = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return selector;
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw, CapabilityJson.ReadOptions);
            if (parsed is null)
            {
                return selector;
            }
            foreach ((string key, string value) in parsed)
            {
                selector[key] = value;
            }
        }
        catch (JsonException)
        {
            // 与 Go 的 `_ = json.Unmarshal(...)` 一致：失败保留空表。
        }
        return selector;
    }

    /// <summary>对应 Go: <c>channelModelPriceTierForIntent</c>。精确规格优先，通配兜底。</summary>
    public static ChannelModelPriceTier? ChannelModelPriceTierForIntent(
        ChannelModel channelModel, ModelRequestIntent intent)
    {
        Dictionary<string, string> selector = SkuSelectorForIntent(intent);
        int bestScore = -1;
        ChannelModelPriceTier? best = null;
        foreach (ChannelModelPriceTier tier in channelModel.PriceTiers)
        {
            if (!tier.Enabled || !tier.PriceConfigured)
            {
                continue;
            }
            (bool matched, int score) = MatchSkuSelector(SkuSelectorForTier(tier), selector);
            if (!matched)
            {
                continue;
            }
            if (score > bestScore)
            {
                best = tier;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>对应 Go: <c>skuSelectorForIntent</c>。</summary>
    public static Dictionary<string, string> SkuSelectorForIntent(ModelRequestIntent intent)
    {
        Dictionary<string, string> selector = new(StringComparer.Ordinal);
        string operation = intent.Operation.Trim().ToLowerInvariant();
        if (operation.Length > 0)
        {
            selector["operation"] = operation;
        }

        long imageInputs = intent.Inputs is not null && intent.Inputs.TryGetValue("image", out long imageCount) ? imageCount : 0;
        long videoInputs = intent.Inputs is not null && intent.Inputs.TryGetValue("video", out long videoCount) ? videoCount : 0;

        switch (CapabilitySpecOps.NormalizeCapability(intent.Capability))
        {
            case "video":
            {
                // 价格档按实际参考素材归类：视频参考优先归为视频生视频，其余图片参考无论数量都归为图生视频。
                if (videoInputs > 0)
                {
                    selector["operation"] = "video_to_video";
                }
                else if (imageInputs > 0)
                {
                    selector["operation"] = "image_to_video";
                }
                if (imageInputs > 0)
                {
                    selector["imageCount"] = imageInputs.ToString(CultureInfo.InvariantCulture);
                }
                string vquality = OptionText(intent.Options, "vquality");
                string resolution = ModelCapabilityConfigOps.NormalizeChannelModelTierResolution(vquality);
                if (resolution != "*")
                {
                    selector["vquality"] = resolution;
                }
                if (long.TryParse(OptionText(intent.Options, "videoSeconds").Trim(), out long seconds) && seconds > 0)
                {
                    selector["videoSeconds"] = seconds.ToString(CultureInfo.InvariantCulture);
                }
                break;
            }
            case "image":
            {
                selector["operation"] = imageInputs > 0 ? "image_to_image" : "text_to_image";
                string rawQuality = OptionString(intent.Options, "quality");
                string rawSize = OptionString(intent.Options, "size");
                string quality = NormalizeImagePriceQuality(rawQuality, rawSize);
                if (quality.Length > 0)
                {
                    selector["quality"] = quality;
                }
                foreach (string key in new[] { "quality", "size" })
                {
                    if (key == "quality" && selector.TryGetValue("quality", out string? existing) && existing.Length > 0)
                    {
                        continue;
                    }
                    string value = OptionString(intent.Options, key).Trim().ToLowerInvariant();
                    if (value.Length > 0 && value != "auto" && value != "any")
                    {
                        selector[key] = value;
                    }
                }
                break;
            }
        }
        return selector;
    }

    /// <summary>读取 intent 选项的文本形式（Go <c>fmt.Sprint</c> 的等价物）。</summary>
    private static string OptionText(Dictionary<string, JsonElement>? options, string key)
    {
        if (options is null || !options.TryGetValue(key, out JsonElement value))
        {
            return "";
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => value.GetRawText(),
        };
    }

    /// <summary>读取 intent 选项的字符串形式；非字符串返回空（Go 的 <c>.(string)</c> 断言）。</summary>
    private static string OptionString(Dictionary<string, JsonElement>? options, string key)
    {
        if (options is null || !options.TryGetValue(key, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return "";
        }
        return value.GetString() ?? "";
    }

    /// <summary>对应 Go: <c>normalizeImagePriceQuality</c>。按像素面积估算价格档位。</summary>
    public static string NormalizeImagePriceQuality(string rawQuality, string rawSize)
    {
        string quality = rawQuality.Trim().ToLowerInvariant();
        if (quality.Length > 0 && quality != "auto" && quality != "any")
        {
            return quality;
        }
        string[] parts = rawSize.Trim().ToLowerInvariant().Split('x');
        if (parts.Length != 2)
        {
            return "";
        }
        if (!long.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long width) ||
            !long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long height) ||
            width <= 0 || height <= 0 || width > (1L << 32) / height)
        {
            return "";
        }
        long pixels = width * height;
        return pixels switch
        {
            <= 2_000_000 => "1k",
            <= 4_300_000 => "2k",
            <= 8_294_400 => "4k",
            _ => "",
        };
    }

    /// <summary>对应 Go: <c>skuSelectorForTier</c>。SelectorJSON 为空时回落到解析度/时长。</summary>
    public static Dictionary<string, string> SkuSelectorForTier(ChannelModelPriceTier tier)
    {
        Dictionary<string, string> selector = DecodeSkuSelector(tier.SelectorJSON);
        if (selector.Count != 0)
        {
            return selector;
        }
        string resolution = ModelCapabilityConfigOps.NormalizeChannelModelTierResolution(tier.Resolution);
        if (resolution != "*")
        {
            selector["vquality"] = resolution;
        }
        if (tier.VideoSeconds > 0)
        {
            selector["videoSeconds"] = tier.VideoSeconds.ToString(CultureInfo.InvariantCulture);
        }
        return selector;
    }

    /// <summary>
    /// 对应 Go: <c>matchSKUSelector</c>。返回是否匹配与命中键数（分数越高越精确）。
    /// </summary>
    public static (bool Matched, int Score) MatchSkuSelector(
        IReadOnlyDictionary<string, string> tier, IReadOnlyDictionary<string, string> requested)
    {
        int score = 0;
        foreach ((string key, string expectedRaw) in tier)
        {
            string expected = expectedRaw.Trim();
            if (expected.Length == 0 || expected == "*")
            {
                continue;
            }
            if (!requested.TryGetValue(key, out string? actual) || actual != expected)
            {
                return (false, 0);
            }
            score++;
        }
        return (true, score);
    }
}
