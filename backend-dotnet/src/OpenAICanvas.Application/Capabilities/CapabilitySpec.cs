#nullable enable
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Application.Capabilities;

/// <summary>路由能力规格。对应 Go: <c>app.CapabilitySpec</c>（model_router.go）。</summary>
/// <remarks>
/// 字段声明顺序即 Go 结构体顺序；<c>inputs</c> / <c>options</c> 是 map，
/// Go 的 encoding/json 按 key 字典序输出，因此本侧构造字典时必须以
/// <see cref="StringComparer.Ordinal"/> 排序插入。
/// </remarks>
public sealed class CapabilitySpec
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";

    [JsonPropertyName("operations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Operations { get; set; }

    [JsonPropertyName("inputs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, InputConstraint>? Inputs { get; set; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, OptionConstraint>? Options { get; set; }

    [JsonPropertyName("imageSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CapabilityImageSize? ImageSize { get; set; }

    public CapabilitySpec Clone() => new()
    {
        Version = Version,
        Capability = Capability,
        Operations = Operations is null ? null : [.. Operations],
        Inputs = Inputs is null
            ? null
            : new Dictionary<string, InputConstraint>(Inputs, StringComparer.Ordinal),
        Options = Options is null
            ? null
            : new Dictionary<string, OptionConstraint>(Options, StringComparer.Ordinal),
        ImageSize = ImageSize is null ? null : new CapabilityImageSize
        {
            Parameter = ImageSize.Parameter,
            AllowCustom = ImageSize.AllowCustom,
            Presets = ImageSize.Presets is null ? null : [.. ImageSize.Presets],
        },
    };
}

public sealed class CapabilityImageSize
{
    [JsonPropertyName("parameter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Parameter { get; set; } = "";

    [JsonPropertyName("allowCustom")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowCustom { get; set; }

    [JsonPropertyName("presets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CapabilityImageSizePreset>? Presets { get; set; }
}

public sealed class CapabilityImageSizePreset
{
    [JsonPropertyName("size")]
    public string Size { get; set; } = "";

    [JsonPropertyName("tier")]
    public string Tier { get; set; } = "";

    [JsonPropertyName("ratio")]
    public string Ratio { get; set; } = "";

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

public sealed class InputConstraint
{
    [JsonPropertyName("min")]
    public long Min { get; set; }

    [JsonPropertyName("max")]
    public long Max { get; set; }
}

public sealed class OptionConstraint
{
    [JsonPropertyName("values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<JsonElement>? Values { get; set; }

    [JsonPropertyName("min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Min { get; set; }

    [JsonPropertyName("max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Max { get; set; }

    [JsonPropertyName("step")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Step { get; set; }

    public OptionConstraint Clone() => new()
    {
        Values = Values is null ? null : [.. Values],
        Min = Min,
        Max = Max,
        Step = Step,
    };
}

/// <summary>创作端路由意图。对应 Go: <c>app.ModelRequestIntent</c>。</summary>
public sealed class ModelRequestIntent
{
    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";

    [JsonPropertyName("operation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Operation { get; set; } = "";

    [JsonPropertyName("inputs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, long>? Inputs { get; set; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Options { get; set; }
}

/// <summary>能力匹配结果。对应 Go: <c>app.CapabilityMatch</c>。</summary>
public sealed class CapabilityMatch
{
    [JsonPropertyName("matched")]
    public bool Matched { get; init; }

    [JsonPropertyName("reasons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Reasons { get; init; }
}

/// <summary>
/// 能力规格的解码、归一化与匹配。对应 Go: <c>model_router.go</c> 与
/// <c>logical_models.go</c> 中的纯函数部分。
/// </summary>
public static class CapabilitySpecOps
{
    /// <summary>与 Go 一致的最小/最大标量容差。</summary>
    private const double Epsilon = 1e-9;

    public static readonly IComparer<string> OrdinalKey = StringComparer.Ordinal;

    /// <summary>构造 key 有序字典，模拟 Go map 的 JSON 字典序输出。</summary>
    public static Dictionary<string, TValue> SortedMap<TValue>() =>
        new(StringComparer.Ordinal);

    /// <summary>对应 Go: <c>DecodeCapabilitySpec</c>。</summary>
    public static CapabilitySpec DecodeCapabilitySpec(string raw)
    {
        CapabilitySpec spec;
        try
        {
            spec = JsonSerializer.Deserialize<CapabilitySpec>(raw, CapabilityJson.ReadOptions)
                ?? new CapabilitySpec();
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("能力配置不是有效 JSON");
        }

        return NormalizeCapabilitySpec(spec);
    }

    /// <summary>对应 Go: <c>NormalizeCapabilitySpec</c>。</summary>
    public static CapabilitySpec NormalizeCapabilitySpec(CapabilitySpec spec)
    {
        spec.Capability = NormalizeCapability(spec.Capability);
        if (spec.Version != 1)
        {
            throw AppError.BadAuthRequest("能力配置 version 必须为 1");
        }
        if (spec.Capability.Length == 0)
        {
            throw AppError.BadAuthRequest("能力配置必须声明 capability");
        }

        List<string> operations = [];
        HashSet<string> seenOperations = new(StringComparer.Ordinal);
        foreach (string operation in spec.Operations ?? [])
        {
            string normalized = NormalizeCapabilityValue(operation);
            if (normalized.Length != 0 && seenOperations.Add(normalized))
            {
                operations.Add(normalized);
            }
        }
        spec.Operations = operations.Count == 0 ? null : operations;

        Dictionary<string, InputConstraint> normalizedInputs = SortedMap<InputConstraint>();
        foreach ((string rawName, InputConstraint constraint) in spec.Inputs ?? [])
        {
            string name = NormalizeCapabilityValue(rawName);
            if (name.Length == 0 || constraint.Min < 0 || constraint.Max < constraint.Min)
            {
                throw AppError.BadAuthRequest("输入能力范围无效");
            }
            if (normalizedInputs.ContainsKey(name))
            {
                throw AppError.BadAuthRequest("输入能力存在重复名称：" + name);
            }
            normalizedInputs[name] = constraint;
        }
        spec.Inputs = normalizedInputs.Count == 0 ? null : normalizedInputs;

        Dictionary<string, OptionConstraint> normalizedOptions = SortedMap<OptionConstraint>();
        foreach ((string rawName, OptionConstraint constraint) in spec.Options ?? [])
        {
            string name = CanonicalCapabilityOptionName(rawName);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw AppError.BadAuthRequest("能力参数名称不能为空");
            }
            if (normalizedOptions.ContainsKey(name))
            {
                throw AppError.BadAuthRequest("能力参数存在重复别名：" + name);
            }
            bool hasValues = constraint.Values is { Count: > 0 };
            bool hasRange = constraint.Min is not null || constraint.Max is not null || constraint.Step is not null;
            if (!hasValues && !hasRange)
            {
                throw AppError.BadAuthRequest("能力参数必须声明 values 或数值范围");
            }
            if (hasValues && hasRange)
            {
                throw AppError.BadAuthRequest("能力参数不能同时声明 values 和数值范围");
            }
            if (hasRange && (constraint.Min is null || constraint.Max is null))
            {
                throw AppError.BadAuthRequest("数值范围必须同时声明 min 和 max");
            }
            if (constraint.Min is not null && constraint.Max is not null && constraint.Max < constraint.Min)
            {
                throw AppError.BadAuthRequest("能力参数数值范围无效");
            }
            if (constraint.Step is not null && constraint.Step <= 0)
            {
                throw AppError.BadAuthRequest("能力参数 step 必须大于 0");
            }
            normalizedOptions[name] = constraint;
        }
        spec.Options = normalizedOptions.Count == 0 ? null : normalizedOptions;
        return spec;
    }

    /// <summary>对应 Go: <c>decodeLogicalDefaults</c>。</summary>
    public static Dictionary<string, JsonElement> DecodeLogicalDefaults(string raw, CapabilitySpec spec)
    {
        Dictionary<string, JsonElement> defaults;
        try
        {
            defaults = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw, CapabilityJson.ReadOptions)
                ?? [];
        }
        catch (JsonException)
        {
            throw AppError.New(500, "默认参数 JSON 无效");
        }
        return NormalizeLogicalDefaults(spec, defaults);
    }

    /// <summary>对应 Go: <c>normalizeLogicalDefaults</c>。</summary>
    public static Dictionary<string, JsonElement> NormalizeLogicalDefaults(
        CapabilitySpec spec,
        Dictionary<string, JsonElement> defaults)
    {
        Dictionary<string, JsonElement> result = SortedMap<JsonElement>();
        foreach ((string rawName, JsonElement value) in defaults)
        {
            string name = CanonicalCapabilityOptionName(rawName);
            if (result.ContainsKey(name))
            {
                throw AppError.BadAuthRequest("默认参数存在重复别名：" + name);
            }
            if (spec.Options is null || !spec.Options.TryGetValue(name, out OptionConstraint? constraint) ||
                !MatchOptionConstraint(name, constraint, value))
            {
                throw AppError.BadAuthRequest("默认参数 " + name + " 不在前台模型能力范围内");
            }

            JsonElement resolved = value;
            // `*` 只表示允许任意自定义值，不能作为创作端默认参数发送。
            if (NormalizedScalar(resolved) == "*")
            {
                foreach (JsonElement candidate in constraint.Values ?? [])
                {
                    if (NormalizedScalar(candidate) != "*")
                    {
                        resolved = candidate;
                        break;
                    }
                }
            }
            result[name] = resolved;
        }
        return result;
    }

    /// <summary>对应 Go: <c>mergeIntentDefaults</c>。defaults 先入，options 覆盖。</summary>
    public static Dictionary<string, JsonElement> MergeIntentDefaults(
        Dictionary<string, JsonElement>? options,
        Dictionary<string, JsonElement>? defaults)
    {
        Dictionary<string, JsonElement> result = SortedMap<JsonElement>();
        foreach ((string key, JsonElement value) in defaults ?? [])
        {
            result[key] = value;
        }
        foreach ((string key, JsonElement value) in options ?? [])
        {
            result[key] = value;
        }
        return result;
    }

    /// <summary>对应 Go: <c>MatchCapability</c>。</summary>
    public static CapabilityMatch MatchCapability(CapabilitySpec spec, ModelRequestIntent intent)
    {
        List<string> reasons = [];
        if (NormalizeCapability(intent.Capability) != NormalizeCapability(spec.Capability))
        {
            reasons.Add("能力类型不匹配");
        }
        string operation = NormalizeCapabilityValue(intent.Operation);
        if (operation.Length != 0 && (spec.Operations?.Count ?? 0) > 0 && !ContainsNormalized(spec.Operations!, operation))
        {
            reasons.Add("不支持操作 " + intent.Operation);
        }

        foreach ((string inputType, long count) in intent.Inputs ?? [])
        {
            if (count < 0)
            {
                reasons.Add("输入数量不能小于 0");
                continue;
            }
            if (spec.Inputs is null || !spec.Inputs.TryGetValue(inputType, out InputConstraint? declared))
            {
                if (count > 0)
                {
                    reasons.Add("不支持 " + CapabilityInputLabel(inputType) + "输入");
                }
                continue;
            }
            if (count < declared.Min || count > declared.Max)
            {
                reasons.Add($"{CapabilityInputLabel(inputType)}数量需在 {declared.Min}-{declared.Max} 之间");
            }
        }

        foreach ((string inputType, InputConstraint constraint) in spec.Inputs ?? [])
        {
            long count = intent.Inputs is not null && intent.Inputs.TryGetValue(inputType, out long value) ? value : 0;
            if (count < constraint.Min)
            {
                reasons.Add($"至少需要 {constraint.Min} 个{CapabilityInputLabel(inputType)}");
            }
        }

        foreach ((string name, JsonElement value) in intent.Options ?? [])
        {
            string canonical = CanonicalCapabilityOptionName(name);
            if (spec.Options is null || !spec.Options.TryGetValue(canonical, out OptionConstraint? constraint))
            {
                reasons.Add("不支持参数 " + CapabilityOptionLabel(name));
                continue;
            }
            if (!MatchOptionConstraint(name, constraint, value))
            {
                reasons.Add("参数 " + CapabilityOptionLabel(name) + "超出支持范围");
            }
        }

        return new CapabilityMatch { Matched = reasons.Count == 0, Reasons = reasons.Count == 0 ? null : reasons };
    }

    /// <summary>对应 Go: <c>validateProductSpecWithinRoutes</c>。</summary>
    public static void ValidateProductSpecWithinRoutes(CapabilitySpec product, IReadOnlyList<CapabilitySpec> routeSpecs)
    {
        foreach (CapabilitySpec routeSpec in routeSpecs)
        {
            if (NormalizeCapability(routeSpec.Capability) != NormalizeCapability(product.Capability))
            {
                throw AppError.BadAuthRequest("供应线路能力类型与前台模型不一致");
            }
        }

        if ((product.Operations?.Count ?? 0) == 0)
        {
            bool unrestricted = false;
            foreach (CapabilitySpec routeSpec in routeSpecs)
            {
                if ((routeSpec.Operations?.Count ?? 0) == 0)
                {
                    unrestricted = true;
                    break;
                }
            }
            if (!unrestricted)
            {
                throw AppError.BadAuthRequest("创作端生成方式必须从供应线路支持的选项中选择");
            }
        }
        else
        {
            foreach (string operation in product.Operations!)
            {
                bool supported = false;
                foreach (CapabilitySpec routeSpec in routeSpecs)
                {
                    if ((routeSpec.Operations?.Count ?? 0) == 0 || routeSpec.Operations!.Contains(operation, StringComparer.Ordinal))
                    {
                        supported = true;
                        break;
                    }
                }
                if (!supported)
                {
                    throw AppError.BadAuthRequest("创作端生成方式不受任何供应线路支持：" + operation);
                }
            }
        }

        foreach ((string name, InputConstraint constraint) in product.Inputs ?? [])
        {
            if (!InputConstraintCovered(constraint, name, routeSpecs))
            {
                throw AppError.BadAuthRequest("创作端输入范围超出供应线路能力：" + name);
            }
        }

        foreach ((string name, OptionConstraint constraint) in product.Options ?? [])
        {
            if (!OptionConstraintCovered(constraint, name, routeSpecs))
            {
                throw AppError.BadAuthRequest("创作端参数超出供应线路能力：" + name);
            }
        }
    }

    /// <summary>对应 Go: <c>inputConstraintCovered</c>。</summary>
    private static bool InputConstraintCovered(InputConstraint candidate, string name, IReadOnlyList<CapabilitySpec> routeSpecs)
    {
        long next = candidate.Min;
        while (next <= candidate.Max)
        {
            long coveredUntil = next - 1;
            foreach (CapabilitySpec routeSpec in routeSpecs)
            {
                InputConstraint constraint = routeSpec.Inputs is not null && routeSpec.Inputs.TryGetValue(name, out InputConstraint? found)
                    ? found
                    : new InputConstraint { Min = 0, Max = 0 };
                if (constraint.Min <= next && constraint.Max >= next && constraint.Max > coveredUntil)
                {
                    coveredUntil = constraint.Max;
                }
            }
            if (coveredUntil < next)
            {
                return false;
            }
            next = coveredUntil + 1;
        }
        return true;
    }

    /// <summary>对应 Go: <c>optionConstraintCovered</c>。</summary>
    private static bool OptionConstraintCovered(OptionConstraint candidate, string name, IReadOnlyList<CapabilitySpec> routeSpecs)
    {
        List<OptionConstraint> routeConstraints = [];
        foreach (CapabilitySpec routeSpec in routeSpecs)
        {
            if (routeSpec.Options is not null && routeSpec.Options.TryGetValue(name, out OptionConstraint? constraint))
            {
                routeConstraints.Add(constraint);
            }
        }
        if (routeConstraints.Count == 0)
        {
            return false;
        }
        foreach (OptionConstraint routeConstraint in routeConstraints)
        {
            if (IsWildcardOptionConstraint(routeConstraint))
            {
                return true;
            }
        }

        if (candidate.Values is { Count: > 0 })
        {
            foreach (JsonElement value in candidate.Values)
            {
                if (!OptionValueSupported(name, value, routeConstraints))
                {
                    return false;
                }
            }
            return true;
        }

        if (candidate.Min is null || candidate.Max is null)
        {
            return false;
        }
        if (Math.Abs(candidate.Max.Value - candidate.Min.Value) < Epsilon)
        {
            return OptionValueSupported(name, JsonSerializer.SerializeToElement(candidate.Min.Value), routeConstraints);
        }
        if (candidate.Step is null)
        {
            return ContinuousOptionRangeCovered(candidate.Min.Value, candidate.Max.Value, routeConstraints);
        }

        double step = candidate.Step.Value;
        int count = (int)Math.Floor((candidate.Max.Value - candidate.Min.Value) / step + Epsilon) + 1;
        if (count <= 10000)
        {
            for (int index = 0; index < count; index++)
            {
                double value = candidate.Min.Value + index * step;
                if (!OptionValueSupported(name, JsonSerializer.SerializeToElement(value), routeConstraints))
                {
                    return false;
                }
            }
            return true;
        }

        // 超大离散范围不逐点展开；只有单条连续范围或步长完全兼容的线路才能作为可靠来源。
        foreach (OptionConstraint routeConstraint in routeConstraints)
        {
            if (routeConstraint.Min is null || routeConstraint.Max is null ||
                routeConstraint.Min > candidate.Min || routeConstraint.Max < candidate.Max)
            {
                continue;
            }
            if (routeConstraint.Step is null)
            {
                return true;
            }
            double startSteps = (candidate.Min.Value - routeConstraint.Min.Value) / routeConstraint.Step.Value;
            double stepRatio = step / routeConstraint.Step.Value;
            if (Math.Abs(startSteps - Math.Round(startSteps)) < Epsilon && Math.Abs(stepRatio - Math.Round(stepRatio)) < Epsilon)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>对应 Go: <c>optionValueSupported</c>。</summary>
    private static bool OptionValueSupported(string name, JsonElement value, IReadOnlyList<OptionConstraint> constraints)
    {
        foreach (OptionConstraint constraint in constraints)
        {
            if (IsWildcardOptionConstraint(constraint))
            {
                return true;
            }
            if (MatchOptionConstraint(name, constraint, value))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>对应 Go: <c>isWildcardOptionConstraint</c>。</summary>
    public static bool IsWildcardOptionConstraint(OptionConstraint constraint)
    {
        foreach (JsonElement value in constraint.Values ?? [])
        {
            if (NormalizedScalar(value) == "*")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>对应 Go: <c>continuousOptionRangeCovered</c>。</summary>
    private static bool ContinuousOptionRangeCovered(double minimum, double maximum, IReadOnlyList<OptionConstraint> constraints)
    {
        double next = minimum;
        while (next <= maximum + Epsilon)
        {
            double coveredUntil = next;
            bool advanced = false;
            foreach (OptionConstraint constraint in constraints)
            {
                if (constraint.Min is null || constraint.Max is null || constraint.Step is not null)
                {
                    continue;
                }
                if (constraint.Min <= next + Epsilon && constraint.Max >= next - Epsilon && constraint.Max > coveredUntil)
                {
                    coveredUntil = constraint.Max.Value;
                    advanced = true;
                }
            }
            if (coveredUntil >= maximum - Epsilon)
            {
                return true;
            }
            if (!advanced)
            {
                return false;
            }
            next = coveredUntil;
        }
        return true;
    }

    /// <summary>对应 Go: <c>matchOptionConstraint</c>。</summary>
    public static bool MatchOptionConstraint(string name, OptionConstraint constraint, JsonElement value)
    {
        if (constraint.Values is { Count: > 0 })
        {
            foreach (JsonElement candidate in constraint.Values)
            {
                if (NormalizedScalar(candidate) == "*")
                {
                    return true;
                }
                if (CapabilityOptionValuesEqual(name, candidate, value))
                {
                    return true;
                }
            }
            return false;
        }

        if (!TryNumericScalar(value, out double number))
        {
            return false;
        }
        if (constraint.Min is not null && number < constraint.Min)
        {
            return false;
        }
        if (constraint.Max is not null && number > constraint.Max)
        {
            return false;
        }
        if (constraint.Step is not null && constraint.Min is not null)
        {
            double steps = (number - constraint.Min.Value) / constraint.Step.Value;
            return Math.Abs(steps - Math.Round(steps)) < Epsilon;
        }
        return true;
    }

    /// <summary>对应 Go: <c>capabilityOptionValuesEqual</c>。</summary>
    private static bool CapabilityOptionValuesEqual(string name, JsonElement candidate, JsonElement value)
    {
        string left = NormalizedScalar(candidate);
        string right = NormalizedScalar(value);
        if (CanonicalCapabilityOptionName(name) == "vquality")
        {
            // 与请求意图和价格档使用同一组别名比较（"480"/"low" → "480p" → "480"）。
            left = NormalizedScalar(NormalizeModelRequestOption(name, JsonSerializer.SerializeToElement(left))).TrimSuffix("p");
            right = NormalizedScalar(NormalizeModelRequestOption(name, JsonSerializer.SerializeToElement(right))).TrimSuffix("p");
        }
        return left == right;
    }

    /// <summary>对应 Go: <c>normalizedScalar</c>。所有比较都经此归一。</summary>
    public static string NormalizedScalar(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case JsonElement element:
                return NormalizedScalarOfElement(element);
            case string text:
                return NormalizeCapabilityValue(text);
            case bool flag:
                return flag ? "true" : "false";
            case int number:
                return number.ToString(CultureInfo.InvariantCulture);
            case long number:
                return number.ToString(CultureInfo.InvariantCulture);
            case double number:
                return GoFormatFloat(number);
            case float number:
                return GoFormatFloat(number);
            default:
                return JsonSerializer.Serialize(value);
        }
    }

    private static string NormalizedScalarOfElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => NormalizeCapabilityValue(element.GetString() ?? ""),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Number => GoFormatFloat(element.GetDouble()),
        _ => JsonSerializer.Serialize(element),
    };

    /// <summary>
    /// 模拟 Go <c>strconv.FormatFloat(v, 'f', -1, 64)</c>：最短往返十进制、不使用指数。
    /// 领域取值（秒数、数量、价格）远离 double 极端量级，安全。
    /// </summary>
    public static string GoFormatFloat(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }
        if (double.IsPositiveInfinity(value))
        {
            return "+Inf";
        }
        if (double.IsNegativeInfinity(value))
        {
            return "-Inf";
        }
        // "0.##…#"（330 位）等价于最短表示并去掉尾随零；.NET 的 "R" 会输出指数形式。
        string text = value.ToString("0." + new string('#', 330), CultureInfo.InvariantCulture);
        return text;
    }

    /// <summary>对应 Go: <c>numericScalar</c>。</summary>
    private static bool TryNumericScalar(JsonElement value, out double number)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            number = value.GetDouble();
            return true;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(
                value.GetString()?.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number);
        }
        number = 0;
        return false;
    }

    /// <summary>对应 Go: <c>containsNormalized</c>。</summary>
    private static bool ContainsNormalized(List<string> values, string expected)
    {
        foreach (string value in values)
        {
            if (NormalizeCapabilityValue(value) == expected)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>对应 Go: <c>normalizeCapabilityValue</c>。</summary>
    public static string NormalizeCapabilityValue(string value) => value.Trim().ToLowerInvariant();

    /// <summary>对应 Go: <c>normalizeCapability</c>（analytics.go）。</summary>
    public static string NormalizeCapability(string value)
    {
        string trimmed = value.Trim().ToLowerInvariant();
        return trimmed is "text" or "image" or "video" or "audio" ? trimmed : "";
    }

    /// <summary>对应 Go: <c>canonicalCapabilityOptionName</c>。</summary>
    public static string CanonicalCapabilityOptionName(string value) => value.Trim() switch
    {
        "duration" => "videoSeconds",
        "aspectRatio" => "size",
        "resolution" => "vquality",
        _ => value.Trim(),
    };

    /// <summary>对应 Go: <c>capabilityInputLabel</c>。</summary>
    private static string CapabilityInputLabel(string name) => NormalizeCapabilityValue(name) switch
    {
        "image" => "参考图片",
        "video" => "参考视频",
        "audio" => "参考音频",
        "mask" => "蒙版",
        _ => name,
    };

    /// <summary>对应 Go: <c>capabilityOptionLabel</c>。</summary>
    private static string CapabilityOptionLabel(string name) => CanonicalCapabilityOptionName(name) switch
    {
        "size" => "画面尺寸",
        "quality" => "生成质量",
        "transparentBackground" => "透明背景",
        "count" => "输出数量",
        "videoSeconds" => "视频时长",
        "vquality" => "输出分辨率",
        "videoGenerateAudio" => "同步音频",
        "videoWatermark" => "水印设置",
        "audioVoice" => "音色",
        "audioFormat" => "音频格式",
        "audioSpeed" => "语速",
        "audioInstructions" => "朗读指令",
        _ => name,
    };

    /// <summary>对应 Go: <c>isCapabilityOptionFor</c>。</summary>
    public static bool IsCapabilityOptionFor(string capability, string name)
    {
        switch (NormalizeCapability(capability))
        {
            case "image":
                return name is "size" or "quality" or "transparentBackground" or "count";
            case "video":
                return name is "size" or "videoSeconds" or "vquality" or "videoGenerateAudio" or "videoWatermark";
            case "audio":
                return name is "audioVoice" or "audioFormat" or "audioSpeed" or "audioInstructions";
            // systemPrompt 是请求内容，不是供应线路能力维度，不能参与路由匹配。
            case "text":
                return false;
            default:
                return false;
        }
    }

    /// <summary>对应 Go: <c>normalizeModelRequestOption</c>。</summary>
    public static JsonElement NormalizeModelRequestOption(string name, JsonElement value)
    {
        string canonicalName = CanonicalCapabilityOptionName(name);
        if (canonicalName is "quality" or "size")
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                return JsonSerializer.SerializeToElement(
                    (value.GetString() ?? "").Trim().ToLowerInvariant());
            }
            return value;
        }
        if (canonicalName != "vquality" || value.ValueKind != JsonValueKind.String)
        {
            return value;
        }

        switch ((value.GetString() ?? "").Trim().ToLowerInvariant())
        {
            case "low" or "480" or "480p":
                return JsonSerializer.SerializeToElement("480p");
            case "720" or "720p":
                return JsonSerializer.SerializeToElement("720p");
            case "1080" or "1080p":
                return JsonSerializer.SerializeToElement("1080p");
            case "2k" or "1440" or "1440p":
                return JsonSerializer.SerializeToElement("1440p");
            case "4k" or "2160" or "2160p":
                return JsonSerializer.SerializeToElement("2160p");
            default:
                return value;
        }
    }

    /// <summary>对应 Go: <c>anyValues</c>。</summary>
    public static OptionConstraint AnyValues(IEnumerable<string> values)
    {
        List<JsonElement> result = [];
        foreach (string value in values)
        {
            result.Add(JsonSerializer.SerializeToElement(value));
        }
        return new OptionConstraint { Values = result.Count == 0 ? null : result };
    }

    /// <summary>对应 Go: <c>boolValues</c>。</summary>
    public static OptionConstraint BoolValues(bool supportsTrue)
    {
        List<JsonElement> values = [JsonSerializer.SerializeToElement(false)];
        if (supportsTrue)
        {
            values.Add(JsonSerializer.SerializeToElement(true));
        }
        return new OptionConstraint { Values = values };
    }

    /// <summary>对应 Go: <c>numericRange</c>。</summary>
    public static OptionConstraint NumericRange(double minimum, double maximum, double step) => new()
    {
        Min = minimum,
        Max = maximum,
        Step = step,
    };

    /// <summary>对应 Go: <c>capabilityFingerprint</c>。用规范化结构去重能力画像。</summary>
    public static string CapabilityFingerprint(CapabilitySpec spec)
    {
        CapabilitySpec copy = spec.Clone();
        copy.Operations = copy.Operations is null ? null : [.. copy.Operations.Order(StringComparer.Ordinal)];
        if (copy.Options is not null)
        {
            foreach (string name in copy.Options.Keys.ToList())
            {
                OptionConstraint constraint = copy.Options[name];
                List<JsonElement>? values = constraint.Values;
                if (values is { Count: > 0 })
                {
                    List<JsonElement> sorted = [.. values];
                    sorted.Sort((left, right) => string.CompareOrdinal(
                        NormalizedScalar(left), NormalizedScalar(right)));
                    constraint.Values = sorted;
                    copy.Options[name] = constraint;
                }
            }
        }
        return JsonSerializer.Serialize(copy);
    }
}

/// <summary>Application 内部使用的 JSON 读取配置（大小写不敏感，与 Go json.Unmarshal 一致）。</summary>
public static class CapabilityJson
{
    public static JsonSerializerOptions ReadOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>字符串后缀裁剪的小工具（Go strings.TrimSuffix 语义）。</summary>
internal static class TrimSuffixExtensions
{
    public static string TrimSuffix(this string value, string suffix)
    {
        if (suffix.Length > 0 && value.EndsWith(suffix, StringComparison.Ordinal))
        {
            return value[..^suffix.Length];
        }
        return value;
    }
}
