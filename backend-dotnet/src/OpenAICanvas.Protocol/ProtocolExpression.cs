#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Protocol;

/// <summary>
/// manifest 表达式引擎。对应 Go: <c>internal/protocol/expression.go</c>。
/// </summary>
/// <remarks>
/// 表达式是 JSON 值上的一小组 <c>$</c> 前缀操作符，用途是在不执行插件代码、不暴露宿主对象的前提下
/// 拼装上游请求体、读取上游响应。语义逐条对齐 Go：<c>manifestEmpty</c> 决定 <c>$coalesce</c> 是否跳过空串，
/// <c>manifestTruthy</c> 决定 <c>$filter</c>/<c>$if</c> 的分支，<c>$eq</c> 比较的是序列化后的字节，
/// 因此这些判定不能凭直觉改写。
/// 环境是 <c>map[string]any</c> 的直译：Ordinal 大小写敏感的字典，值为 JSON 形态的
/// <c>null/bool/string/double/long/List&lt;object?&gt;/Dictionary&lt;string, object?&gt;</c>。
/// </remarks>
public static class ProtocolExpression
{
    /// <summary>新建表达式环境（对应 Go 的 <c>map[string]any</c>）。</summary>
    public static Dictionary<string, object?> NewEnvironment() => new(StringComparer.Ordinal);

    /// <summary>求值一个 manifest 模板。对应 Go: <c>evaluateManifestValue</c>。</summary>
    public static object? Evaluate(object? template, IReadOnlyDictionary<string, object?> env)
    {
        switch (template)
        {
            case null:
            case bool:
            case double:
            case float:
            case int:
            case long:
                return template;
            case string text:
            {
                string trimmed = text.Trim();
                if (trimmed.StartsWith("${", StringComparison.Ordinal) &&
                    trimmed.EndsWith("}", StringComparison.Ordinal))
                {
                    return PathValue(env, trimmed[2..^1].Trim());
                }
                // 只有「整串就是一个引用」才替换，其余返回未 trim 的原串。
                return text;
            }
            case List<object?> items:
            {
                List<object?> result = new(items.Count);
                foreach (object? item in items)
                {
                    object? evaluated = Evaluate(item, env);
                    if (evaluated is not null)
                    {
                        result.Add(evaluated);
                    }
                }
                return result;
            }
            case Dictionary<string, object?> map:
                return EvaluateMap(map, env);
            case JsonElement element:
                return Evaluate(JsonFields.FromElement(element), env);
            default:
            {
                // 其余类型先序列化回普通 JSON 值再求值（等价 Go 的 marshal/unmarshal 往返）。
                string encoded;
                try
                {
                    encoded = ProtocolJson.Serialize(template);
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException(
                        $"unsupported manifest template value {template.GetType().Name}", error);
                }
                using JsonDocument document = JsonDocument.Parse(encoded);
                return Evaluate(JsonFields.FromElement(document.RootElement), env);
            }
        }
    }

    private static object? EvaluateMap(
        Dictionary<string, object?> map,
        IReadOnlyDictionary<string, object?> env)
    {
        // 单键且键以 $ 开头才视为操作符；多键对象一律按普通对象递归。
        if (map.Count == 1)
        {
            foreach ((string key, object? operand) in map)
            {
                if (key.StartsWith("$", StringComparison.Ordinal))
                {
                    return EvaluateOperator(key, operand, env);
                }
            }
        }
        Dictionary<string, object?> result = new(map.Count, StringComparer.Ordinal);
        foreach ((string key, object? item) in map)
        {
            object? evaluated;
            try
            {
                evaluated = Evaluate(item, env);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"evaluate {key}: {error.Message}", error);
            }
            // 求值为 null 的键会被丢弃。
            if (evaluated is not null)
            {
                result[key] = evaluated;
            }
        }
        return result;
    }

    private static object? EvaluateOperator(
        string op,
        object? operand,
        IReadOnlyDictionary<string, object?> env)
    {
        switch (op)
        {
            case "$ref":
            {
                if (operand is not string path)
                {
                    throw new InvalidOperationException("$ref requires a string path");
                }
                return PathValue(env, path);
            }
            case "$literal":
                return operand;
            case "$coalesce":
            case "$default":
            {
                foreach (object? item in RequireArray(operand, op))
                {
                    object? value = Evaluate(item, env);
                    if (!Empty(value))
                    {
                        return value;
                    }
                }
                return null;
            }
            case "$concat":
            {
                StringBuilder result = new();
                foreach (object? item in RequireArray(operand, op))
                {
                    object? value = Evaluate(item, env);
                    if (value is not null)
                    {
                        result.Append(TextOf(value));
                    }
                }
                return result.ToString();
            }
            case "$concatArrays":
            {
                List<object?> result = [];
                foreach (object? item in RequireArray(operand, op))
                {
                    result.AddRange(Array(Evaluate(item, env)));
                }
                return result;
            }
            case "$map":
            case "$filter":
            {
                Dictionary<string, object?> spec = RequireObject(operand, op);
                object? from = Evaluate(spec.GetValueOrDefault("from"), env);
                string alias = TextOf(spec.GetValueOrDefault("as")).Trim();
                if (alias.Length == 0)
                {
                    alias = "item";
                }
                List<object?> items = Array(from);
                List<object?> result = new(items.Count);
                for (int index = 0; index < items.Count; index++)
                {
                    Dictionary<string, object?> child = CloneEnvironment(env);
                    child[alias] = items[index];
                    child[alias + "Index"] = index;
                    if (op == "$filter")
                    {
                        if (Truthy(Evaluate(spec.GetValueOrDefault("where"), child)))
                        {
                            result.Add(items[index]);
                        }
                        continue;
                    }
                    object? mapped = Evaluate(spec.GetValueOrDefault("in"), child);
                    if (mapped is not null)
                    {
                        result.Add(mapped);
                    }
                }
                return result;
            }
            case "$indexObject":
            {
                Dictionary<string, object?> spec = RequireObject(operand, op);
                object? from = Evaluate(spec.GetValueOrDefault("from"), env);
                string alias = TextOf(spec.GetValueOrDefault("as")).Trim();
                if (alias.Length == 0)
                {
                    alias = "item";
                }
                string prefix = TextOf(Evaluate(spec.GetValueOrDefault("prefix"), env));
                List<object?> items = Array(from);
                int maxItems = items.Count;
                if (spec.TryGetValue("max", out object? maxRaw) && maxRaw is not null)
                {
                    long configured = IntOf(Evaluate(maxRaw, env));
                    if (configured >= 0 && configured < maxItems)
                    {
                        maxItems = (int)configured;
                    }
                }
                Dictionary<string, object?> result = new(maxItems, StringComparer.Ordinal);
                for (int index = 0; index < maxItems; index++)
                {
                    Dictionary<string, object?> child = CloneEnvironment(env);
                    child[alias] = items[index];
                    child[alias + "Index"] = index;
                    object? value = Evaluate(spec.GetValueOrDefault("value"), child);
                    // 空值不建键，保证产出的数组/对象不会带空洞。
                    if (value is not null && !Empty(value))
                    {
                        result[prefix + index.ToString(CultureInfo.InvariantCulture)] = value;
                    }
                }
                return result;
            }
            case "$first":
            case "$last":
            {
                List<object?> items = Array(Evaluate(operand, env));
                if (items.Count == 0)
                {
                    return null;
                }
                return op == "$last" ? items[^1] : items[0];
            }
            case "$at":
            {
                if (operand is not List<object?> values || values.Count != 2)
                {
                    throw new InvalidOperationException("$at requires [array, index]");
                }
                List<object?> items = Array(Evaluate(values[0], env));
                long index = IntOf(Evaluate(values[1], env));
                if (index < 0 || index >= items.Count)
                {
                    return null;
                }
                return items[(int)index];
            }
            case "$len":
            {
                object? value = Evaluate(operand, env);
                return value switch
                {
                    List<object?> items => (long)items.Count,
                    Dictionary<string, object?> map => (long)map.Count,
                    // Go 的 len([]rune(s)) 按 rune 计数。
                    string text => (long)text.EnumerateRunes().Count(),
                    _ => 0L,
                };
            }
            case "$split":
            {
                if (operand is not List<object?> spec || spec.Count != 2)
                {
                    throw new InvalidOperationException("$split requires [value, separator]");
                }
                object? value = Evaluate(spec[0], env);
                string separator = TextOf(Evaluate(spec[1], env));
                if (separator.Length == 0)
                {
                    throw new InvalidOperationException("$split separator must not be empty");
                }
                string[] parts = TextOf(value).Split(separator, StringSplitOptions.None);
                List<object?> result = new(parts.Length);
                foreach (string part in parts)
                {
                    result.Add(part);
                }
                return result;
            }
            case "$if":
            {
                Dictionary<string, object?> spec = RequireObject(operand, op);
                if (Truthy(Evaluate(spec.GetValueOrDefault("condition"), env)))
                {
                    return Evaluate(spec.GetValueOrDefault("then"), env);
                }
                return Evaluate(spec.GetValueOrDefault("else"), env);
            }
            case "$switch":
            {
                Dictionary<string, object?> spec = RequireObject(operand, op);
                foreach (object? rawCase in Array(spec.GetValueOrDefault("cases")))
                {
                    Dictionary<string, object?> item = ObjectOf(rawCase) ?? [];
                    if (Truthy(Evaluate(item.GetValueOrDefault("when"), env)))
                    {
                        return Evaluate(item.GetValueOrDefault("then"), env);
                    }
                }
                return Evaluate(spec.GetValueOrDefault("default"), env);
            }
            case "$omitEmpty":
            {
                object? value = Evaluate(operand, env);
                return Empty(value) ? null : value;
            }
            case "$lower":
            case "$upper":
            case "$trim":
            case "$toString":
            case "$toInt":
            case "$toFloat":
            case "$toBool":
            case "$dataMime":
            case "$dataPayload":
            case "$json":
            {
                object? value = Evaluate(operand, env);
                string text = TextOf(value);
                switch (op)
                {
                    case "$lower":
                        return text.ToLowerInvariant();
                    case "$upper":
                        return text.ToUpperInvariant();
                    case "$trim":
                        return text.Trim();
                    case "$toString":
                        return text;
                    case "$toInt":
                        return IntOf(value);
                    case "$toFloat":
                    {
                        (double parsed, bool ok) = FloatValue(value);
                        return ok ? parsed : null;
                    }
                    case "$toBool":
                        return Truthy(value);
                    case "$dataMime":
                        return DataMime(text);
                    case "$dataPayload":
                        return DataPayload(text);
                    default:
                        return ProtocolJsonValue.Encode(value);
                }
            }
            case "$merge":
            {
                Dictionary<string, object?> result = new(StringComparer.Ordinal);
                foreach (object? item in RequireArray(operand, op))
                {
                    Dictionary<string, object?>? entry = ObjectOf(Evaluate(item, env));
                    if (entry is null)
                    {
                        continue;
                    }
                    foreach ((string key, object? value) in entry)
                    {
                        result[key] = value;
                    }
                }
                return result;
            }
            case "$sortByOrder":
            {
                // OrderBy 是稳定排序，等价 Go 的 sort.SliceStable。
                return Array(Evaluate(operand, env))
                    .OrderBy(item => IntOf(PathValue(item, "order")))
                    .ToList();
            }
            case "$add":
            case "$multiply":
            case "$divide":
            case "$min":
            case "$max":
            {
                List<object?> values = RequireArray(operand, op);
                if (values.Count == 0)
                {
                    throw new InvalidOperationException($"{op} requires an array");
                }
                if (op == "$divide")
                {
                    if (values.Count != 2)
                    {
                        throw new InvalidOperationException("$divide requires [numerator, denominator]");
                    }
                    object? numerator = Evaluate(values[0], env);
                    object? denominator = Evaluate(values[1], env);
                    double divisor = FloatOf(denominator);
                    if (divisor == 0)
                    {
                        throw new InvalidOperationException("$divide denominator must not be zero");
                    }
                    return NormalizeNumber(FloatOf(numerator) / divisor);
                }
                double result = op switch
                {
                    "$multiply" => 1,
                    "$min" => double.PositiveInfinity,
                    "$max" => double.NegativeInfinity,
                    _ => 0,
                };
                foreach (object? item in values)
                {
                    double number = FloatOf(Evaluate(item, env));
                    switch (op)
                    {
                        case "$multiply":
                            result *= number;
                            break;
                        case "$min":
                            result = Math.Min(result, number);
                            break;
                        case "$max":
                            result = Math.Max(result, number);
                            break;
                        default:
                            result += number;
                            break;
                    }
                }
                return NormalizeNumber(result);
            }
            case "$ceilStep":
            {
                if (operand is not List<object?> values || values.Count != 2)
                {
                    throw new InvalidOperationException("$ceilStep requires [value, step]");
                }
                object? value = Evaluate(values[0], env);
                double step = FloatOf(Evaluate(values[1], env));
                if (step <= 0)
                {
                    throw new InvalidOperationException("$ceilStep step must be positive");
                }
                return NormalizeNumber(Math.Ceiling(FloatOf(value) / step) * step);
            }
            case "$eq":
            case "$ne":
            case "$gt":
            case "$gte":
            case "$lt":
            case "$lte":
            case "$in":
            case "$and":
            case "$or":
                return EvaluateComparison(op, operand, env);
            case "$not":
                return !Truthy(Evaluate(operand, env));
            default:
                throw new InvalidOperationException($"unsupported manifest operator \"{op}\"");
        }
    }

    /// <summary>对应 Go: <c>evaluateManifestComparison</c>。</summary>
    private static object? EvaluateComparison(
        string op,
        object? operand,
        IReadOnlyDictionary<string, object?> env)
    {
        if (operand is not List<object?> values)
        {
            throw new InvalidOperationException($"{op} requires an array");
        }
        List<object?> evaluated = new(values.Count);
        foreach (object? item in values)
        {
            evaluated.Add(Evaluate(item, env));
        }
        if (op is "$and" or "$or")
        {
            // 空数组时 $and 为 true、$or 为 false。
            bool result = op == "$and";
            foreach (object? value in evaluated)
            {
                if (op == "$and")
                {
                    result = result && Truthy(value);
                }
                else
                {
                    result = result || Truthy(value);
                }
            }
            return result;
        }
        if (evaluated.Count != 2)
        {
            throw new InvalidOperationException($"{op} requires two operands");
        }
        object? left = evaluated[0];
        object? right = evaluated[1];
        switch (op)
        {
            case "$eq":
            case "$ne":
            {
                bool equal = ComparisonJson(left) == ComparisonJson(right);
                return op == "$ne" ? !equal : equal;
            }
            case "$gt":
                return FloatOf(left) > FloatOf(right);
            case "$gte":
                return FloatOf(left) >= FloatOf(right);
            case "$lt":
                return FloatOf(left) < FloatOf(right);
            case "$lte":
                return FloatOf(left) <= FloatOf(right);
            case "$in":
            {
                string needle = ComparisonJson(left);
                foreach (object? item in Array(right))
                {
                    if (needle == ComparisonJson(item))
                    {
                        return true;
                    }
                }
                return false;
            }
            default:
                return false;
        }
    }

    /// <summary>比较用的 JSON 文本。Go 在比较路径上忽略序列化错误（得到空串），这里保持一致。</summary>
    private static string ComparisonJson(object? value)
    {
        try
        {
            return ProtocolJsonValue.Encode(value);
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>对应 Go: <c>interpolateManifestString</c>（<c>{{path}}</c> 内联替换）。</summary>
    public static string Interpolate(string value, IReadOnlyDictionary<string, object?> env)
    {
        string result = value;
        while (true)
        {
            int start = result.IndexOf("{{", StringComparison.Ordinal);
            if (start < 0)
            {
                return result;
            }
            int end = result.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                return result;
            }
            string path = result[(start + 2)..end].Trim();
            result = result[..start] + TextOf(PathValue(env, path)) + result[(end + 2)..];
        }
    }

    /// <summary>对应 Go: <c>manifestPathValue</c>（<c>a.b.0</c> 逐段取值；越界或类型不符返回 null）。</summary>
    public static object? PathValue(object? root, string path)
    {
        if (path.Trim().Length == 0)
        {
            return root;
        }
        object? value = root;
        foreach (string part in path.Trim('.').Split('.'))
        {
            switch (value)
            {
                case Dictionary<string, object?> map:
                    value = map.GetValueOrDefault(part);
                    break;
                case List<object?> items:
                {
                    // Go 的 strconv.Atoi 不接受空白，因此只放行前导符号。
                    if (!int.TryParse(
                            part,
                            NumberStyles.AllowLeadingSign,
                            CultureInfo.InvariantCulture,
                            out int index) ||
                        index < 0 ||
                        index >= items.Count)
                    {
                        return null;
                    }
                    value = items[index];
                    break;
                }
                default:
                    return null;
            }
        }
        return value;
    }

    /// <summary>对应 Go: <c>cloneManifestEnv</c>。</summary>
    public static Dictionary<string, object?> CloneEnvironment(IReadOnlyDictionary<string, object?> env) =>
        new(env, StringComparer.Ordinal);

    /// <summary>对应 Go: <c>manifestArray</c>（null → 空、非数组 → 单元素数组）。</summary>
    public static List<object?> Array(object? value) => value switch
    {
        null => [],
        List<object?> items => items,
        _ => [value],
    };

    /// <summary>对应 Go: <c>manifestObject</c>（非对象返回 null）。</summary>
    public static Dictionary<string, object?>? ObjectOf(object? value) =>
        value as Dictionary<string, object?>;

    /// <summary>对应 Go: <c>manifestEmpty</c>（null / 空白串 / 空数组 / 空对象）。</summary>
    public static bool Empty(object? value) => value switch
    {
        null => true,
        string text => text.Trim().Length == 0,
        List<object?> items => items.Count == 0,
        Dictionary<string, object?> map => map.Count == 0,
        _ => false,
    };

    /// <summary>对应 Go: <c>manifestTruthy</c>（未列举的类型一律为 true）。</summary>
    public static bool Truthy(object? value)
    {
        switch (value)
        {
            case null:
                return false;
            case bool flag:
                return flag;
            case string text:
            {
                string normalized = text.Trim().ToLowerInvariant();
                return normalized.Length != 0 &&
                    normalized != "false" &&
                    normalized != "0" &&
                    normalized != "no" &&
                    normalized != "null";
            }
            case double number:
                return number != 0;
            case float number:
                return number != 0;
            case int number:
                return number != 0;
            case long number:
                return number != 0;
            case List<object?> items:
                return items.Count > 0;
            case Dictionary<string, object?> map:
                return map.Count > 0;
            default:
                return true;
        }
    }

    /// <summary>
    /// 对应 Go: <c>manifestString</c>。浮点用定点最短写法（签名与 payload 都对写法敏感），
    /// 其余类型回落 Go 风格的 JSON 文本。
    /// </summary>
    public static string TextOf(object? value)
    {
        switch (value)
        {
            case null:
                return "";
            case string text:
                return text;
            case double number:
                return ProtocolRequestBuilder.FormatFloatGo(number);
            case float number:
                return FormatFloat32(number);
            case int number:
                return number.ToString(CultureInfo.InvariantCulture);
            case long number:
                return number.ToString(CultureInfo.InvariantCulture);
            case bool flag:
                return flag ? "true" : "false";
            default:
                try
                {
                    return ProtocolJsonValue.Encode(value);
                }
                catch (InvalidOperationException)
                {
                    return "";
                }
        }
    }

    /// <summary>对应 Go: <c>manifestFloatValue</c>。</summary>
    public static (double Value, bool Ok) FloatValue(object? value)
    {
        switch (value)
        {
            case double number:
                return (number, true);
            case float number:
                return (number, true);
            case int number:
                return (number, true);
            case long number:
                return (number, true);
            case string text:
                return ParseFloatGo(text.Trim());
            default:
                return (0, false);
        }
    }

    /// <summary>对应 Go: <c>manifestFloat</c>（不可解析时为 0）。</summary>
    public static double FloatOf(object? value) => FloatValue(value).Value;

    /// <summary>对应 Go: <c>manifestInt</c>（向零截断）。</summary>
    public static long IntOf(object? value) => (long)FloatOf(value);

    /// <summary>对应 Go: <c>normalizeManifestNumber</c>（整数值回落整数类型，否则浮点）。</summary>
    public static object NormalizeNumber(double value)
    {
        if (Math.Truncate(value) == value && value >= long.MinValue && value <= long.MaxValue)
        {
            return (long)value;
        }
        return value;
    }

    /// <summary>对应 Go: <c>dataMIME</c>。</summary>
    public static string DataMime(string value)
    {
        int index = value.IndexOf(';');
        if (value.StartsWith("data:", StringComparison.Ordinal) && index > 5)
        {
            return value[5..index];
        }
        return "application/octet-stream";
    }

    /// <summary>对应 Go: <c>dataPayload</c>。</summary>
    public static string DataPayload(string value)
    {
        int index = value.IndexOf(',');
        if (value.StartsWith("data:", StringComparison.Ordinal) && index >= 0)
        {
            return value[(index + 1)..];
        }
        return value;
    }

    private static List<object?> RequireArray(object? operand, string op) =>
        operand as List<object?> ?? throw new InvalidOperationException($"{op} requires an array");

    private static Dictionary<string, object?> RequireObject(object? operand, string op) =>
        operand as Dictionary<string, object?> ?? throw new InvalidOperationException($"{op} requires an object");

    /// <summary>对应 Go 的 <c>strconv.ParseFloat</c>（额外接受 inf/nan 字面量）。</summary>
    private static (double Value, bool Ok) ParseFloatGo(string text)
    {
        switch (text.ToLowerInvariant())
        {
            case "inf" or "+inf" or "infinity" or "+infinity":
                return (double.PositiveInfinity, true);
            case "-inf" or "-infinity":
                return (double.NegativeInfinity, true);
            case "nan":
                return (double.NaN, true);
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? (parsed, true)
            : (0, false);
    }

    /// <summary>float32 的 Go 定点写法（<c>strconv.FormatFloat(v, 'f', -1, 32)</c>）。</summary>
    private static string FormatFloat32(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        if (text.Contains('E') || text.Contains('e'))
        {
            try
            {
                return ((decimal)value).ToString(CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                return text;
            }
        }
        return text;
    }
}
