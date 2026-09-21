#nullable enable
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Providers;

/// <summary>
/// 工作流协议的纯值函数：配置校验、宽高比/尺寸/分辨率/时长归一、
/// 数值边界与步长校验、输出 URL 解析、状态码归一。
/// 对应 Go: <c>internal/app/workflow_provider.go</c> 的同名函数。
/// </summary>
public static class ProviderWorkflowValues
{
    /// <summary>对应 Go: <c>WorkflowPluginRunningHub</c>。</summary>
    public const string WorkflowPluginRunningHub = "runninghub";

    /// <summary>对应 Go: <c>workflowPluginIDForInterface</c>。</summary>
    public static (string PluginID, bool Ok) WorkflowPluginIDForInterface(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            ChannelInterfaceType.ChannelInterfaceRunningHubImage
                or ChannelInterfaceType.ChannelInterfaceRunningHubVideo
                or ChannelInterfaceType.ChannelInterfaceRunningHubAudio
                => (WorkflowPluginRunningHub, true),
            _ => ("", false),
        };

    /// <summary>对应 Go: <c>isRunningHubInterface</c>。</summary>
    public static bool IsRunningHubInterface(string? value) =>
        WorkflowPluginIDForInterface(value).Ok;

    /// <summary>对应 Go: <c>isWorkflowProviderInterface</c>。</summary>
    public static bool IsWorkflowProviderInterface(string value) => IsRunningHubInterface(value);

    /// <summary>对应 Go: <c>workflowInterfaceSupportsMode</c>。</summary>
    public static bool WorkflowInterfaceSupportsMode(string interfaceType, string mode) => mode switch
    {
        "image" => interfaceType == ChannelInterfaceType.ChannelInterfaceRunningHubImage,
        "video" => interfaceType == ChannelInterfaceType.ChannelInterfaceRunningHubVideo,
        "audio" => interfaceType == ChannelInterfaceType.ChannelInterfaceRunningHubAudio,
        _ => false,
    };

    /// <summary>对应 Go: <c>validateWorkflowProviderConfig</c>。</summary>
    public static async System.Threading.Tasks.Task ValidateWorkflowProviderConfig(string mode, ProviderConfig config)
    {
        if (mode is not ("image" or "video" or "audio"))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture, "工作流协议暂不支持{0}生成", mode));
        }
        string interfaceType = (config.InterfaceType ?? "").Trim().ToLowerInvariant();
        if (!WorkflowInterfaceSupportsMode(interfaceType, mode))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture, "接口类型 {0} 不支持{1}生成", config.InterfaceType, mode));
        }
        if (IsRunningHubInterface(config.InterfaceType))
        {
            if (RunningHubApiKey(config).Length == 0)
            {
                throw new InvalidOperationException("RunningHub 工作流缺少积分 API Key");
            }
            await OutboundGuard.ValidateOutboundUrlAsync(RunningHubRootURL(config.BaseURL)).ConfigureAwait(false);
            if (config.WorkflowID.Trim().Length == 0
                && config.WebappID.Trim().Length == 0
                && config.Model.Trim().Length == 0)
            {
                throw new InvalidOperationException("RunningHub 缺少 workflowId 或 webappId");
            }
            return;
        }
        throw new InvalidOperationException("未知工作流协议");
    }

    /// <summary>对应 Go: <c>runningHubRootURL</c>。</summary>
    public static string RunningHubRootURL(string value)
    {
        string baseValue = (value ?? "").Trim().TrimEnd('/');
        if (baseValue.Length == 0)
        {
            baseValue = "https://www.runninghub.cn";
        }
        // 设置页可能保存根地址、/openapi/v2 或带尾部斜杠的任一形式；任务
        // OpenAPI 使用根地址下的 /task/openapi/*，统一剥掉 API 前缀。
        string lower = baseValue.ToLowerInvariant();
        foreach (string suffix in new[] { "/openapi/v2", "/openapi" })
        {
            if (lower.EndsWith(suffix, StringComparison.Ordinal))
            {
                baseValue = baseValue[..^suffix.Length];
                break;
            }
        }
        return baseValue.TrimEnd('/');
    }

    /// <summary>
    /// RunningHub 工作流的管理、提交和轮询固定使用积分 API Key；
    /// 企业级 Key 仅由上传接口单独读取。对应 Go: <c>runningHubAPIKey</c>。
    /// </summary>
    public static string RunningHubApiKey(ProviderConfig config) => (config.APIKey ?? "").Trim();

    // ------------------------------------------------------------ 字段来源归一

    /// <summary>对应 Go: <c>normalizeWorkflowFieldSource</c>。</summary>
    public static string NormalizeWorkflowFieldSource(WorkflowField field)
    {
        string source = (field.Source ?? "").Trim().ToLowerInvariant();
        if (source.Length == 0 && field.BindPrompt)
        {
            return "prompt";
        }
        if (source.Length > 0)
        {
            return source;
        }
        if (!field.SourceFromUpstream)
        {
            return "";
        }
        string fieldName = field.FieldName.Trim().ToLowerInvariant();
        if (fieldName is "prompt" or "text" or "positive_prompt" or "positiveprompt")
        {
            return "prompt";
        }
        return (field.FieldType ?? "").Trim().ToLowerInvariant() switch
        {
            "image" or "img" or "photo" or "picture" => "referenceimage",
            "video" or "movie" => "referencevideo",
            "audio" or "sound" or "music" or "voice" => "referenceaudio",
            _ => "",
        };
    }

    /// <summary>对应 Go: <c>workflowFieldsForMode</c>。</summary>
    public static List<WorkflowField> WorkflowFieldsForMode(List<WorkflowField> fields, string mode)
    {
        if (fields.Count == 0)
        {
            return fields;
        }
        foreach (WorkflowField field in fields)
        {
            string canonical = WorkflowFieldInference.WorkflowNamedDynamicSource(field.Source ?? "", mode);
            if (canonical.Length > 0)
            {
                field.Source = canonical;
            }
            string inferred = WorkflowFieldInference.WorkflowDynamicSourceForMode(
                field.FieldName, field.FieldType ?? "", mode);
            if ((field.Source ?? "").Trim().Length == 0 && !field.SourceConfigured && !field.BindPrompt)
            {
                field.Source = inferred;
                if (inferred.Length > 0)
                {
                    field.SourceAutomatic = true;
                }
                continue;
            }
            if (ShouldRepairLegacyWorkflowSource(field.FieldName, field.Source ?? "", inferred, mode))
            {
                field.Source = inferred;
            }
        }
        return fields;
    }

    /// <summary>对应 Go: <c>shouldRepairLegacyWorkflowSource</c>。</summary>
    public static bool ShouldRepairLegacyWorkflowSource(
        string fieldName, string source, string inferred, string mode)
    {
        string sourceKey = WorkflowFieldInference.NormalizeFieldName(source);
        string inferredKey = WorkflowFieldInference.NormalizeFieldName(inferred);
        if (sourceKey.Length == 0 || inferredKey.Length == 0 || sourceKey == inferredKey)
        {
            return false;
        }
        string fieldKey = WorkflowFieldInference.NormalizeFieldName(fieldName);
        bool wasMistakenForMedia = sourceKey is "referenceimage" or "referencevideo" or "referenceaudio";
        if (wasMistakenForMedia
            && (WorkflowFieldInference.WorkflowDimensionNameSource(fieldKey).Length > 0
                || inferredKey is "aspectratio" or "vquality"))
        {
            return true;
        }
        mode = mode.Trim().ToLowerInvariant();
        return (fieldKey == "resolution" && (mode == "video" && sourceKey == "size" || mode == "image" && sourceKey == "vquality"))
            || (fieldKey == "quality" && sourceKey == "vquality");
    }

    // ------------------------------------------------------------ 字段值校验

    /// <summary>对应 Go: <c>validateRunningHubWorkflowFieldValue</c>。</summary>
    public static void ValidateRunningHubFieldValue(WorkflowField field, object? value)
    {
        string fieldID = ProviderHelpers.FirstNonEmpty(field.ID.Trim(), field.NodeID.Trim() + "." + field.FieldName.Trim());
        string fieldType = (field.FieldType ?? "").Trim().ToUpperInvariant();
        if (fieldType is "NUMBER" or "FLOAT" or "INTEGER" or "INT" or "SLIDER")
        {
            bool hasNumeric = TryNumericBound(value, out double numeric);
            if (!hasNumeric)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.InvariantCulture, "工作流字段 {0} 不是有效数字", fieldID));
            }
            bool hasMin = TryNumericBound(field.Min, out double minValue);
            bool hasMax = TryNumericBound(field.Max, out double maxValue);
            bool hasStep = TryNumericBound(field.Step, out double stepValue);
            if (hasMin && hasMax && minValue > maxValue)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.InvariantCulture, "工作流字段 {0} 的最小值不能大于最大值", fieldID));
            }
            if (hasStep && stepValue <= 0)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.InvariantCulture, "工作流字段 {0} 的步长必须大于 0", fieldID));
            }
            if ((hasMin && numeric < minValue) || (hasMax && numeric > maxValue))
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.InvariantCulture, "工作流字段 {0} 的值超出允许范围", fieldID));
            }
            if (hasStep)
            {
                double start = hasMin ? minValue : 0;
                double steps = (numeric - start) / stepValue;
                if (Math.Abs(steps - Math.Round(steps)) > 1e-7)
                {
                    throw new InvalidOperationException(
                        string.Format(CultureInfo.InvariantCulture, "工作流字段 {0} 的值不符合步长", fieldID));
                }
            }
        }
        if (fieldType is "BOOLEAN" or "BOOL")
        {
            switch (value)
            {
                case bool:
                    break;
                case string item:
                {
                    string normalized = item.Trim().ToLowerInvariant();
                    if (normalized is not ("true" or "false"))
                    {
                        throw new InvalidOperationException(
                            string.Format(CultureInfo.InvariantCulture, "工作流字段 {0} 不是有效开关值", fieldID));
                    }
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        string.Format(CultureInfo.InvariantCulture, "工作流字段 {0} 不是有效开关值", fieldID));
            }
        }
        List<object?> options = WorkflowFieldAllowedOptions(field);
        if (options.Count > 0)
        {
            string encoded = WorkflowScalarString(value).Trim();
            bool matched = false;
            bool hasScalarOption = false;
            foreach (object? option in options)
            {
                if (WorkflowOptionIsRange(option))
                {
                    continue;
                }
                string candidate = WorkflowOptionString(option).Trim();
                if (candidate.Length == 0 || candidate == "<nil>")
                {
                    continue;
                }
                hasScalarOption = true;
                if (candidate == encoded)
                {
                    matched = true;
                    break;
                }
            }
            if (hasScalarOption && !matched)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.InvariantCulture, "工作流字段 {0} 的值不在允许选项中", fieldID));
            }
        }
    }

    /// <summary>对应 Go: <c>workflowOptionIsRange</c>。</summary>
    public static bool WorkflowOptionIsRange(object? value)
    {
        if (value is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        foreach (string key in new[] { "min", "max", "step", "minValue", "maxValue", "stepValue" })
        {
            if (element.TryGetProperty(key, out _))
            {
                return true;
            }
        }
        if (element.TryGetProperty("range", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            return WorkflowOptionIsRange(nested);
        }
        return false;
    }

    /// <summary>对应 Go: <c>workflowOptionString</c>。</summary>
    public static string WorkflowOptionString(object? value)
    {
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in new[] { "value", "id", "key", "label", "name" })
            {
                if (element.TryGetProperty(key, out JsonElement candidate)
                    && candidate.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    string text = WorkflowFieldCodec.GenericString(candidate).Trim();
                    if (text.Length > 0 && text != "<nil>")
                    {
                        return text;
                    }
                }
            }
        }
        return WorkflowFieldCodec.GenericString(value) is { } generic ? generic : "";
    }

    /// <summary>对应 Go: <c>workflowFieldAllowedOptions</c>（ResolutionSelector 枚举 + 自带 options）。</summary>
    public static List<object?> WorkflowFieldAllowedOptions(WorkflowField field)
    {
        string classType = WorkflowFieldInference.NormalizeFieldName(field.ClassType ?? "");
        string fieldName = WorkflowFieldInference.NormalizeFieldName(field.FieldName);
        if (classType == "resolutionselector" && fieldName == "aspectratio")
        {
            // RunningHub 的工作流 API 不返回 object_info；ResolutionSelector 的完整枚举
            // 是节点协议的一部分，通用比例预设（9:16 等）不能直接提交给该节点。
            return new List<object?>
            {
                "1:1 (Square)",
                "2:3 (Portrait Photo)",
                "3:2 (Photo)",
                "3:4 (Portrait Standard)",
                "4:3 (Standard)",
                "9:16 (Portrait Widescreen)",
                "16:9 (Widescreen)",
                "21:9 (Ultrawide)",
            };
        }
        return field.Options ?? [];
    }

    /// <summary>对应 Go: <c>workflowFieldConfiguredDefault</c>。</summary>
    public static string WorkflowFieldConfiguredDefault(WorkflowField field)
    {
        object? value = field.FieldValue ?? field.Value;
        return value is null ? "" : WorkflowOptionString(value).Trim();
    }

    /// <summary>对应 Go: <c>workflowScalarString</c>。</summary>
    public static string WorkflowScalarString(object? value) => value switch
    {
        null => "",
        string text => text,
        JsonElement element => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            _ => JsonSerializer.Serialize(element),
        },
        bool flag => flag ? "true" : "false",
        _ => JsonSerializer.Serialize(value),
    };

    /// <summary>对应 Go: <c>workflowNumericBound</c>。</summary>
    public static bool TryNumericBound(object? value, out double parsed)
    {
        parsed = 0;
        if (value is JsonElement { ValueKind: JsonValueKind.Number } element)
        {
            if (!element.TryGetDouble(out parsed))
            {
                return false;
            }
            return !double.IsNaN(parsed) && !double.IsInfinity(parsed);
        }
        string text = WorkflowFieldCodec.GenericString(value).Trim();
        if (text.Length == 0 || text == "<nil>")
        {
            return false;
        }
        bool ok = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
        return ok && !double.IsNaN(parsed) && !double.IsInfinity(parsed);
    }

    /// <summary>对应 Go: <c>workflowIntegerBound</c>。</summary>
    public static long WorkflowIntegerBound(object? value, long fallback)
    {
        string text = WorkflowFieldCodec.GenericString(value).Trim();
        if (value is null || text.Length == 0)
        {
            return fallback;
        }
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }
        throw new InvalidOperationException(
            string.Format(CultureInfo.InvariantCulture, "工作流随机值范围不是有效整数：{0}", text));
    }

    /// <summary>对应 Go: <c>randomWorkflowInteger</c>。</summary>
    public static long RandomWorkflowInteger(object? rawMin, object? rawMax)
    {
        const long defaultMax = 9007199254740991;
        long minValue = WorkflowIntegerBound(rawMin, 0);
        long maxValue = WorkflowIntegerBound(rawMax, defaultMax);
        if (maxValue < minValue)
        {
            throw new InvalidOperationException("工作流随机值最大值不能小于最小值");
        }
        // Go 用 big.Int 抗模偏差；.NET 用 BigInteger 实现等价的拒绝采样。
        BigInteger rangeSize = new BigInteger(maxValue) - new BigInteger(minValue) + 1;
        byte[] bytes = new byte[rangeSize.GetByteCount(true)];
        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            BigInteger candidate = new(bytes, isUnsigned: true);
            if (candidate < rangeSize)
            {
                return (long)(candidate + new BigInteger(minValue));
            }
        }
    }

    // ------------------------------------------------------------ 比例 / 尺寸 / 分辨率

    /// <summary>对应 Go: <c>workflowPixelDimensions</c>。</summary>
    public static bool TryPixelDimensions(string value, out int width, out int height)
    {
        width = height = 0;
        string normalized = (value ?? "").Trim().ToLowerInvariant();
        if (!normalized.Contains('x'))
        {
            return false;
        }
        string[] parts = normalized.Split(['x', ' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }
        if (!int.TryParse(parts[0].Trim(), out width) || width <= 0
            || !int.TryParse(parts[1].Trim(), out height) || height <= 0)
        {
            width = height = 0;
            return false;
        }
        return true;
    }

    /// <summary>对应 Go: <c>workflowAspectRatio</c>。</summary>
    public static string WorkflowAspectRatio(string value)
    {
        string normalized = (value ?? "").Trim().ToLowerInvariant();
        // RunningHub 的 ResolutionSelector 选项带有展示文案（例如
        // "9:16 (Portrait Widescreen)"），协议比较只需要比例前缀。
        int cut = normalized.IndexOfAny([' ', '(']);
        if (cut >= 0)
        {
            normalized = normalized[..cut].Trim();
        }
        foreach (string suffix in new[] { "-1k", "-2k", "-4k" })
        {
            if (normalized.EndsWith(suffix, StringComparison.Ordinal))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }
        if (TryRatioParts(normalized, out int width, out int height))
        {
            int gcd = GreatestCommonDivisor(width, height);
            return string.Create(CultureInfo.InvariantCulture, $"{width / gcd}:{height / gcd}");
        }
        if (!TryPixelDimensions(normalized, out int pixelWidth, out int pixelHeight))
        {
            return "";
        }
        double ratio = (double)pixelWidth / pixelHeight;
        int[,] known = { { 1, 1 }, { 3, 2 }, { 2, 3 }, { 4, 3 }, { 3, 4 }, { 4, 5 }, { 5, 4 }, { 16, 9 }, { 9, 16 }, { 2, 1 }, { 1, 2 }, { 21, 9 } };
        double bestDifference = double.MaxValue;
        int bestWidth = 0, bestHeight = 0;
        for (int i = 0; i < known.GetLength(0); i++)
        {
            double expected = (double)known[i, 0] / known[i, 1];
            double difference = Math.Abs(ratio - expected) / expected;
            if (difference < bestDifference)
            {
                bestDifference = difference;
                bestWidth = known[i, 0];
                bestHeight = known[i, 1];
            }
        }
        if (bestDifference <= 0.03)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bestWidth}:{bestHeight}");
        }
        int divisor = GreatestCommonDivisor(pixelWidth, pixelHeight);
        return string.Create(CultureInfo.InvariantCulture, $"{pixelWidth / divisor}:{pixelHeight / divisor}");
    }

    /// <summary>对应 Go: <c>workflowAspectRatioValue</c>。</summary>
    public static string WorkflowAspectRatioValue(WorkflowField field, string value)
    {
        string raw = value.Trim();
        List<object?> options = WorkflowFieldAllowedOptions(field);
        if (options.Count > 0)
        {
            string requested = WorkflowAspectRatio(raw);
            foreach (object? option in options)
            {
                string candidate = WorkflowOptionString(option).Trim();
                if (candidate.Length > 0
                    && string.Equals(WorkflowAspectRatio(candidate), requested, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            string fallback = WorkflowFieldConfiguredDefault(field);
            return fallback.Length > 0 ? fallback : raw;
        }
        // "auto/adaptive" 是工作流的真实模式，不应被画布当前像素尺寸反推成
        // 一个并不存在的比例；宽高字段仍按工作流自身的默认值或显式尺寸处理。
        string configured = WorkflowFieldConfiguredDefault(field).Trim();
        if (configured.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || configured.Equals("adaptive", StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }
        return WorkflowAspectRatio(raw);
    }

    /// <summary>对应 Go: <c>workflowRatioParts</c>。</summary>
    public static bool TryRatioParts(string value, out int width, out int height)
    {
        width = height = 0;
        string[] parts = value.Trim().Split(':');
        if (parts.Length != 2)
        {
            return false;
        }
        bool widthOk = int.TryParse(parts[0].Trim(), out width);
        bool heightOk = int.TryParse(parts[1].Trim(), out height);
        return widthOk && heightOk && width > 0 && height > 0;
    }

    /// <summary>对应 Go: <c>workflowGreatestCommonDivisor</c>。</summary>
    public static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }
        return left <= 0 ? 1 : left;
    }

    /// <summary>对应 Go: <c>workflowImageDimensions</c>。</summary>
    public static bool TryImageDimensions(string value, out int width, out int height)
    {
        width = height = 0;
        string normalized = (value ?? "").Trim().ToLowerInvariant();
        (string Key, int W, int H)[] presets =
        {
            ("1:1", 1024, 1024), ("3:2", 1536, 1024), ("2:3", 1024, 1536),
            ("4:3", 1360, 1024), ("3:4", 1024, 1360), ("4:5", 1024, 1280), ("5:4", 1280, 1024),
            ("16:9", 1824, 1024), ("9:16", 1024, 1824), ("2:1", 2048, 1024), ("1:2", 1024, 2048),
            ("21:9", 2352, 1008), ("1:1-2k", 2048, 2048), ("16:9-2k", 2048, 1152),
            ("9:16-2k", 1152, 2048), ("16:9-4k", 3840, 2160), ("9:16-4k", 2160, 3840),
        };
        foreach ((string key, int presetWidth, int presetHeight) in presets)
        {
            if (normalized == key)
            {
                width = presetWidth;
                height = presetHeight;
                return true;
            }
        }
        string ratio = WorkflowAspectRatio(normalized);
        if (!TryRatioParts(ratio, out int widthRatio, out int heightRatio))
        {
            return false;
        }
        string tier = "1k";
        if (normalized.EndsWith("-2k", StringComparison.Ordinal))
        {
            tier = "2k";
        }
        else if (normalized.EndsWith("-4k", StringComparison.Ordinal))
        {
            tier = "4k";
        }
        if (tier == "1k")
        {
            if (widthRatio >= heightRatio)
            {
                width = RoundToStep(1024d * widthRatio / heightRatio, 16);
                height = 1024;
            }
            else
            {
                width = 1024;
                height = RoundToStep(1024d * heightRatio / widthRatio, 16);
            }
            return true;
        }
        int longEdge = tier == "4k" ? 3840 : 2048;
        if (widthRatio >= heightRatio)
        {
            width = longEdge;
            height = RoundToStep((double)longEdge * heightRatio / widthRatio, 16);
        }
        else
        {
            width = RoundToStep((double)longEdge * widthRatio / heightRatio, 16);
            height = longEdge;
        }
        return true;
    }

    /// <summary>对应 Go: <c>workflowVideoDimensions</c>。</summary>
    public static bool TryVideoDimensions(string size, string quality, out int width, out int height)
    {
        width = height = 0;
        string ratio = WorkflowAspectRatio(size);
        if (!TryRatioParts(ratio, out int widthRatio, out int heightRatio))
        {
            return false;
        }
        int shortEdge = VideoResolutionPixels(quality);
        if (shortEdge <= 0)
        {
            return false;
        }
        if (widthRatio >= heightRatio)
        {
            width = RoundToStep((double)shortEdge * widthRatio / heightRatio, 2);
            height = shortEdge;
        }
        else
        {
            width = shortEdge;
            height = RoundToStep((double)shortEdge * heightRatio / widthRatio, 2);
        }
        return true;
    }

    /// <summary>对应 Go: <c>workflowRoundToStep</c>。</summary>
    public static int RoundToStep(double value, int step) =>
        step <= 1 ? (int)Math.Round(value) : (int)Math.Round(value / step) * step;

    /// <summary>对应 Go: <c>workflowVideoResolutionPixels</c>。</summary>
    public static int VideoResolutionPixels(string value)
    {
        string normalized = (value ?? "").Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "low": return 480;
            case "auto" or "default" or "medium" or "high": return 720;
            case "2k": return 1440;
            case "4k": return 2160;
        }
        string digits = normalized.EndsWith("p", StringComparison.Ordinal) ? normalized[..^1] : normalized;
        return int.TryParse(digits, out int parsed) && parsed > 0 ? parsed : 0;
    }

    /// <summary>对应 Go: <c>normalizeWorkflowResolutionToken</c>。</summary>
    public static string NormalizeResolutionToken(string value)
    {
        string normalized = (value ?? "").Trim();
        if (normalized.EndsWith("p", StringComparison.Ordinal)
            || normalized.EndsWith("P", StringComparison.Ordinal))
        {
            normalized = normalized[..^1];
        }
        return normalized.ToLowerInvariant();
    }

    /// <summary>对应 Go: <c>normalizeWorkflowDurationToken</c>。</summary>
    public static string NormalizeDurationToken(string value)
    {
        string normalized = (value ?? "").Trim();
        if (normalized.EndsWith("s", StringComparison.Ordinal))
        {
            normalized = normalized[..^1];
        }
        if (normalized.EndsWith("秒", StringComparison.Ordinal))
        {
            normalized = normalized[..^"秒".Length];
        }
        return normalized.ToLowerInvariant();
    }

    /// <summary>对应 Go: <c>workflowNumericValue</c>。</summary>
    private static bool TryNumericValue(string value, out double parsed)
    {
        string normalized = (value ?? "").Trim();
        if (normalized.EndsWith("p", StringComparison.Ordinal)
            || normalized.EndsWith("P", StringComparison.Ordinal))
        {
            normalized = normalized[..^1];
        }
        parsed = 0;
        return double.TryParse(
            normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
            && !double.IsNaN(parsed) && !double.IsInfinity(parsed);
    }

    /// <summary>对应 Go: <c>workflowResolutionShapeCompatible</c>。</summary>
    public static bool ResolutionShapeCompatible(string defaultValue, string requested)
    {
        bool defaultNumeric = TryNumericValue(defaultValue, out _);
        bool requestedNumeric = TryNumericValue(requested, out _);
        if (defaultNumeric && requestedNumeric)
        {
            return true;
        }
        return string.Equals(defaultValue.Trim(), requested.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>对应 Go: <c>workflowNumericResolutionValue</c>。</summary>
    public static object? NumericResolutionValue(WorkflowField field, string raw)
    {
        if (!TryNumericValue(raw, out double rawValue))
        {
            return null;
        }
        bool hasMin = TryNumericBound(field.Min, out double min);
        bool hasMax = TryNumericBound(field.Max, out double max);
        bool hasStep = TryNumericBound(field.Step, out double step);
        if (!hasMin && !hasMax && !hasStep
            && !string.Equals(field.FieldType?.Trim(), "NUMBER", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        double value = rawValue;
        if (hasMin && value < min)
        {
            value = min;
        }
        if (hasMax && value > max)
        {
            value = max;
        }
        if (hasStep && step > 0)
        {
            double anchor = hasMin ? min : 0;
            value = anchor + Math.Round((value - anchor) / step) * step;
        }
        if (string.Equals(field.FieldType?.Trim(), "NUMBER", StringComparison.OrdinalIgnoreCase)
            && Math.Truncate(value) == value)
        {
            return (long)value;
        }
        return value;
    }

    /// <summary>对应 Go: <c>workflowVideoResolutionValue</c>。</summary>
    public static object? VideoResolutionValue(WorkflowField field, string value)
    {
        string raw = value.Trim();
        if (raw.Length == 0)
        {
            return "";
        }
        List<object?> options = field.Options ?? [];
        if (options.Count > 0)
        {
            string requested = NormalizeResolutionToken(raw);
            foreach (object? option in options)
            {
                string candidate = WorkflowOptionString(option).Trim();
                if (candidate.Length > 0 && NormalizeResolutionToken(candidate) == requested)
                {
                    // nodeInfoList 的 fieldValue 是字符串；对象选项只取其 value/id 等标量。
                    return candidate;
                }
            }
            // 工作流明确声明了可选项但画布值不在其中时，保留工作流默认值，
            // 不再把 2K/4K/720 等普通模型别名硬塞给工作流。
            string fallback = WorkflowFieldConfiguredDefault(field);
            if (fallback.Length > 0)
            {
                foreach (object? option in options)
                {
                    string candidate = WorkflowOptionString(option).Trim();
                    if (candidate.Length > 0
                        && NormalizeResolutionToken(candidate) == NormalizeResolutionToken(fallback))
                    {
                        return candidate;
                    }
                }
                return fallback;
            }
            return raw;
        }
        if (NumericResolutionValue(field, raw) is { } numeric)
        {
            return numeric;
        }
        string configured = WorkflowFieldConfiguredDefault(field);
        if (configured.Length > 0 && !ResolutionShapeCompatible(configured, raw))
        {
            return configured;
        }
        return raw;
    }

    /// <summary>对应 Go: <c>workflowVideoDurationValue</c>。</summary>
    public static object? VideoDurationValue(WorkflowField field, string value)
    {
        string raw = value.Trim();
        if (raw.Length == 0)
        {
            return "";
        }
        List<object?> options = field.Options ?? [];
        if (options.Count > 0)
        {
            string requested = NormalizeDurationToken(raw);
            foreach (object? option in options)
            {
                string candidate = WorkflowOptionString(option).Trim();
                if (candidate.Length > 0 && NormalizeDurationToken(candidate) == requested)
                {
                    return candidate;
                }
            }
            string fallback = WorkflowFieldConfiguredDefault(field);
            if (fallback.Length > 0)
            {
                foreach (object? option in options)
                {
                    string candidate = WorkflowOptionString(option).Trim();
                    if (candidate.Length > 0
                        && NormalizeDurationToken(candidate) == NormalizeDurationToken(fallback))
                    {
                        return candidate;
                    }
                }
                return fallback;
            }
            return raw;
        }
        if (NumericResolutionValue(field, raw) is { } numeric)
        {
            return numeric;
        }
        string configured = WorkflowFieldConfiguredDefault(field);
        if (configured.Length > 0 && !ResolutionShapeCompatible(configured, raw))
        {
            return configured;
        }
        return raw;
    }

    /// <summary>对应 Go: <c>workflowDimensionPart</c>。</summary>
    public static string WorkflowDimensionPart(string mode, string size, string videoQuality, int index)
    {
        if (index is < 0 or > 1)
        {
            return "";
        }
        if (TryPixelDimensions(size, out int pixelWidth, out int pixelHeight))
        {
            return (index == 0 ? pixelWidth : pixelHeight)
                .ToString(CultureInfo.InvariantCulture);
        }
        int videoWidth = 0, videoHeight = 0, imageWidth = 0, imageHeight = 0;
        bool ok = mode.Trim().Equals("video", StringComparison.OrdinalIgnoreCase)
            ? TryVideoDimensions(size, videoQuality, out videoWidth, out videoHeight)
            : TryImageDimensions(size, out imageWidth, out imageHeight);
        if (!ok)
        {
            return "";
        }
        return (index == 0
                ? (mode.Trim().Equals("video", StringComparison.OrdinalIgnoreCase) ? videoWidth : imageWidth)
                : (mode.Trim().Equals("video", StringComparison.OrdinalIgnoreCase) ? videoHeight : imageHeight))
            .ToString(CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------ 输出 URL / 状态码

    /// <summary>对应 Go: <c>resolveRunningHubOutputURL</c>。</summary>
    public static string ResolveRunningHubOutputURL(string root, string rawURL)
    {
        rawURL = RewriteRunningHubOutputHost(rawURL.Trim());
        if (rawURL.Length == 0 || rawURL.StartsWith("data:", StringComparison.Ordinal)
            || ProviderHelpers.IsPublicMediaURL(rawURL))
        {
            return rawURL;
        }
        if (rawURL.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + rawURL;
        }
        if (rawURL.StartsWith("/", StringComparison.Ordinal))
        {
            return root.TrimEnd('/') + rawURL;
        }
        // API 可能返回 output/foo、assets/foo 这类无前导斜杠的相对路径。
        foreach (string prefix in new[] { "output/", "assets/", "input/" })
        {
            if (rawURL.ToLowerInvariant().StartsWith(prefix, StringComparison.Ordinal))
            {
                return root.TrimEnd('/') + "/" + rawURL;
            }
        }
        if (RunningHubRelativeOutputPath(rawURL))
        {
            return root.TrimEnd('/') + "/" + rawURL.TrimStart('/');
        }
        return rawURL;
    }

    /// <summary>
    /// RunningHub 某些区域会返回旧 COS 域名，参考项目已将其迁移到可访问域名；
    /// 这里只做固定 host 映射，不接受响应内容提供任意代理目标。
    /// 对应 Go: <c>rewriteRunningHubOutputHost</c>。
    /// </summary>
    public static string RewriteRunningHubOutputHost(string rawURL)
    {
        if (!Uri.TryCreate(rawURL.Trim(), UriKind.Absolute, out Uri? parsed) || parsed.Host.Length == 0)
        {
            return rawURL;
        }
        if (string.Equals(
                parsed.Host, "rh-images-1252422369.cos.ap-beijing.myqcloud.com", StringComparison.OrdinalIgnoreCase))
        {
            return "https://rh-images.xiaoyaoyou.com" + parsed.PathAndQuery;
        }
        return rawURL;
    }

    /// <summary>对应 Go: <c>runningHubRelativeOutputPath</c>。</summary>
    public static bool RunningHubRelativeOutputPath(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (value.Length == 0 || value.Contains("://") || value.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (string segment in value.Split('?')[0].Split('/'))
        {
            if (segment == "..")
            {
                return false;
            }
        }
        foreach (string prefix in new[] { "output/", "assets/", "input/" })
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        string path = value.Split('?')[0];
        foreach (string suffix in new[]
                 {
                     ".png", ".jpg", ".jpeg", ".webp", ".gif", ".mp4", ".webm",
                     ".mov", ".m4v", ".mp3", ".wav", ".ogg", ".m4a", ".flac",
                 })
        {
            if (path.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>对应 Go: <c>runningHubTaskID</c>。</summary>
    public static string RunningHubTaskID(IReadOnlyDictionary<string, object?>? payload)
    {
        foreach (string[] keys in new[]
                 {
                     new[] { "data", "taskId" }, new[] { "data", "task_id" },
                     new[] { "data", "taskID" }, new[] { "data", "id" },
                     new[] { "taskId" }, new[] { "task_id" }, new[] { "taskID" }, new[] { "id" },
                 })
        {
            if (keys.Length == 2)
            {
                string value = JsonFields.OptionalString(
                    JsonFields.NestedObject(payload, keys[0]), keys[1]).Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
            else
            {
                string value = JsonFields.OptionalString(payload, keys[0]).Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }
        return "";
    }

    /// <summary>对应 Go: <c>runningHubFileName</c>。</summary>
    public static string RunningHubFileName(IReadOnlyDictionary<string, object?>? payload)
    {
        foreach (string key in new[] { "fileName", "file_name", "filename", "name" })
        {
            string value = JsonFields.OptionalString(payload, key).Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }
        foreach (string container in new[] { "data", "result", "file", "files" })
        {
            IReadOnlyDictionary<string, object?>? nested = JsonFields.NestedObject(payload, container);
            if (nested is null)
            {
                continue;
            }
            foreach (string key in new[] { "fileName", "file_name", "filename", "name" })
            {
                string value = JsonFields.OptionalString(nested, key).Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }
        return "";
    }

    /// <summary>对应 Go: <c>runningHubOutputURLs</c>。</summary>
    public static List<string> RunningHubOutputURLs(object? value)
    {
        List<string> result = [];
        void Visit(object? current)
        {
            switch (current)
            {
                case string item:
                    if (item.StartsWith("http://", StringComparison.Ordinal)
                        || item.StartsWith("https://", StringComparison.Ordinal)
                        || item.StartsWith("data:", StringComparison.Ordinal)
                        || (item.StartsWith("/", StringComparison.Ordinal)
                            && !item.StartsWith("//", StringComparison.Ordinal))
                        || RunningHubRelativeOutputPath(item))
                    {
                        result.Add(item);
                    }
                    break;
                case JsonElement { ValueKind: JsonValueKind.Array } array:
                    foreach (JsonElement child in array.EnumerateArray())
                    {
                        Visit(child.ValueKind == JsonValueKind.Null ? null : JsonFields.FromElement(child));
                    }
                    break;
                case List<object?> items:
                    foreach (object? child in items)
                    {
                        Visit(child);
                    }
                    break;
                case JsonElement { ValueKind: JsonValueKind.Object } element:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        VisitObjectKey(property.Name, JsonFields.FromElement(property.Value));
                    }
                    break;
                case Dictionary<string, object?> map:
                    foreach (KeyValuePair<string, object?> pair in map)
                    {
                        VisitObjectKey(pair.Key, pair.Value);
                    }
                    break;
            }
        }

        void VisitObjectKey(string key, object? child)
        {
            string lowerKey = key.Trim().ToLowerInvariant();
            switch (lowerKey)
            {
                case "fileurl" or "file_url" or "url" or "downloadurl" or "download_url"
                    or "src" or "output" or "outputs" or "results" or "files"
                    or "data" or "images" or "videos" or "audio" or "audios" or "result":
                    Visit(child);
                    break;
            }
        }

        Visit(value);
        return result.Distinct().ToList();
    }

    /// <summary>对应 Go: <c>runningHubFailureMessage</c>。</summary>
    public static string RunningHubFailureMessage(IReadOnlyDictionary<string, object?>? payload)
    {
        foreach (string key in new[] { "msg", "message", "error", "failReason", "failedReason", "errorMessage" })
        {
            string value = JsonFields.OptionalString(payload, key).Trim();
            if (value.Length > 0)
            {
                return RunningHubActionableFailureMessage(value);
            }
        }
        if (JsonFields.NestedObject(payload, "data") is { } nested)
        {
            return RunningHubFailureMessage(nested);
        }
        return "上游未提供失败原因";
    }

    /// <summary>对应 Go: <c>runningHubWorkflowFailureMessage</c>。</summary>
    public static string RunningHubWorkflowFailureMessage(IReadOnlyDictionary<string, object?>? payload)
    {
        string message = RunningHubFailureMessage(payload);
        string normalized = message.ToLowerInvariant();
        if (message.Contains("企业版余额不足")
            || (normalized.Contains("enterprise") && normalized.Contains("balance")))
        {
            return message
                + "；工作流提交固定使用积分 API Key，请确认提交 Key 不是企业级素材上传 Key";
        }
        return message;
    }

    /// <summary>对应 Go: <c>runningHubActionableFailureMessage</c>。</summary>
    public static string RunningHubActionableFailureMessage(string message)
    {
        string normalized = message.ToLowerInvariant();
        if (normalized.Contains("node_info_mismatch") || normalized.Contains("node_not_found_in_workflow"))
        {
            return "RunningHub AI 应用的公开参数已变化，请到“设置 → RunningHub 工作流”重新选择该 App 并点击“拉取参数”后再试（上游详情："
                + message + "）";
        }
        return message;
    }

    /// <summary>对应 Go: <c>isWorkflowSeedField</c>。</summary>
    public static bool IsWorkflowSeedField(string value) => WorkflowFieldInference.IsSeedField(value);

    /// <summary>对应 Go: <c>runningHubPayloadCode</c>（兼容 code/statusCode 与字符串状态）。</summary>
    public static (int Code, bool Valid) RunningHubPayloadCode(IReadOnlyDictionary<string, object?>? payload)
    {
        if (payload is null)
        {
            return (0, false);
        }
        (int topCode, bool topValid) = RunningHubDirectCode(payload);
        if (JsonFields.NestedObject(payload, "data") is { } nested)
        {
            (int nestedCode, bool nestedValid) = RunningHubPayloadCode(nested);
            if (nestedValid)
            {
                // 有些网关把 HTTP 成功包装成顶层 code=0，同时把真实任务状态放在
                // data.code/status；真实任务状态优先，避免把排队任务误判成完成。
                if (!topValid || topCode == 0 || nestedCode != 0)
                {
                    return (nestedCode, true);
                }
            }
        }
        return (topCode, topValid);
    }

    /// <summary>对应 Go: <c>runningHubDirectCode</c>。</summary>
    public static (int Code, bool Valid) RunningHubDirectCode(IReadOnlyDictionary<string, object?>? payload)
    {
        int primaryCode = 0;
        bool primaryValid = false;
        foreach (string key in new[] { "code", "statusCode", "status_code" })
        {
            if (payload is not null && payload.TryGetValue(key, out object? value))
            {
                (int code, bool valid) = RunningHubCode(value);
                if (valid)
                {
                    if ((key is "statusCode" or "status_code") && code is >= 200 and < 300)
                    {
                        code = 0;
                    }
                    primaryCode = code;
                    primaryValid = true;
                    break;
                }
            }
        }
        foreach (string key in new[] { "status", "state", "taskStatus", "task_status" })
        {
            if (payload is not null && payload.TryGetValue(key, out object? value))
            {
                (int code, bool valid) = RunningHubStatusCode(value);
                if (valid)
                {
                    if (!primaryValid || primaryCode == 0 || code != 0)
                    {
                        return (code, true);
                    }
                    break;
                }
            }
        }
        if (primaryValid)
        {
            return (primaryCode, true);
        }
        // errorCode=0 通常只是"无错误"标记，不能覆盖 data.status=running。
        foreach (string key in new[] { "errorCode", "error_code" })
        {
            if (payload is not null && payload.TryGetValue(key, out object? value))
            {
                (int code, bool valid) = RunningHubCode(value);
                if (valid && code != 0)
                {
                    return (code, true);
                }
            }
        }
        return (0, false);
    }

    /// <summary>对应 Go: <c>runningHubCode</c>。</summary>
    public static (int Code, bool Valid) RunningHubCode(object? value)
    {
        switch (value)
        {
            case int item: return item < 0 ? (0, false) : (item, true);
            case long item: return item < 0 ? (0, false) : ((int)item, true);
            case double item:
            {
                int code = (int)item;
                return item < 0 || code != item ? (0, false) : (code, true);
            }
            case JsonElement { ValueKind: JsonValueKind.Number } element:
            {
                if (element.TryGetInt32(out int parsed))
                {
                    return parsed < 0 ? (0, false) : (parsed, true);
                }
                if (element.TryGetDouble(out double raw) && raw == Math.Floor(raw) && raw >= 0)
                {
                    return ((int)raw, true);
                }
                return (0, false);
            }
            case JsonElement { ValueKind: JsonValueKind.String } element:
            {
                // 字符串数字按 Go mapNumber 语义解析；非数字返回 -1 视为无效，
                // 否则 "queued" 会被 AtoiOrZero 兜底成 0 而误判为成功。
                int parsed = ProviderHelpers.AtoiOrInvalid(element.GetString());
                return parsed >= 0 ? (parsed, true) : (0, false);
            }
            case string:
            {
                int parsed = ProviderHelpers.AtoiOrInvalid(value.ToString());
                return parsed >= 0 ? (parsed, true) : (0, false);
            }
            default:
                return (0, false);
        }
    }

    /// <summary>对应 Go: <c>runningHubStatusCode</c>（字符串状态 → 公开状态码）。</summary>
    public static (int Code, bool Valid) RunningHubStatusCode(object? value)
    {
        string text = WorkflowFieldCodec.GenericString(value).Trim().ToLowerInvariant();
        if (text.Length == 0 || text == "<nil>")
        {
            return (0, false);
        }
        (int numeric, bool valid) = RunningHubCode(text);
        if (valid)
        {
            switch (numeric)
            {
                case 0 or 804 or 813 or 805 or 806:
                    return (numeric, true);
            }
        }
        text = text.Replace("_", "").Replace("-", "").Replace(" ", "");
        return text switch
        {
            "success" or "succeeded" or "complete" or "completed" or "done"
                or "finished" or "finish" or "3" => (0, true),
            "queued" or "queue" or "pending" or "waiting" or "created" or "submitted" => (813, true),
            "running" or "processing" or "executing" or "inprogress" or "started"
                or "working" or "1" or "2" => (804, true),
            "failed" or "failure" or "error" or "rejected" or "cancelled" or "canceled"
                or "expired" or "aborted" or "4" or "5" => (805, true),
            _ => (0, false),
        };
    }

    /// <summary>对应 Go: <c>runningHubOutputMimeType</c>。</summary>
    public static string RunningHubOutputMimeType(string rawURL, string declared)
    {
        declared = declared.Split(';')[0].Trim();
        if (declared.Length > 0 && declared != "application/octet-stream")
        {
            return declared;
        }
        string pathValue = rawURL;
        if (Uri.TryCreate(rawURL, UriKind.Absolute, out Uri? parsed) && parsed.AbsolutePath.Length > 0)
        {
            pathValue = parsed.AbsolutePath;
        }
        pathValue = pathValue.Split('?')[0].ToLowerInvariant();
        foreach ((string suffix, string mimeType) in new[]
                 {
                     (".png", "image/png"), (".jpg", "image/jpeg"), (".jpeg", "image/jpeg"),
                     (".webp", "image/webp"), (".gif", "image/gif"),
                     (".mp4", "video/mp4"), (".webm", "video/webm"), (".mov", "video/quicktime"),
                     (".m4v", "video/mp4"),
                     (".mp3", "audio/mpeg"), (".wav", "audio/wav"), (".ogg", "audio/ogg"),
                     (".m4a", "audio/mp4"), (".flac", "audio/flac"),
                 })
        {
            if (pathValue.EndsWith(suffix, StringComparison.Ordinal))
            {
                return mimeType;
            }
        }
        return declared;
    }

    /// <summary>对应 Go: <c>workflowOutputValue</c>。</summary>
    public static Dictionary<string, object?>? WorkflowOutputValue(string mimeType, byte[] data)
    {
        mimeType = mimeType.Split(';')[0].Trim();
        if (mimeType.Length == 0)
        {
            mimeType = "application/octet-stream";
        }
        if (data.Length == 0)
        {
            return null;
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dataUrl"] = ProviderHelpers.DataUrl(mimeType, data),
            ["mimeType"] = mimeType,
            ["bytes"] = (long)data.Length,
        };
    }

    /// <summary>对应 Go: <c>workflowNodeMetaTitle</c>。</summary>
    public static string WorkflowNodeMetaTitle(IReadOnlyDictionary<string, object?>? node)
    {
        IReadOnlyDictionary<string, object?>? meta = JsonFields.NestedObject(node, "_meta");
        return meta is null ? "<nil>" : WorkflowFieldCodec.GenericString(
            meta.TryGetValue("title", out object? title) ? title : null);
    }
}
