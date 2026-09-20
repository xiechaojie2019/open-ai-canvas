#nullable enable
using System.Text.Json;

namespace OpenAICanvas.Outbound;

/// <summary>
/// 异构上游 JSON 的字段读取工具。
/// 对应 Go: <c>internal/platform/json_fields.go</c>。
/// </summary>
/// <remarks>
/// 语义要点与 Go 一致：<b>缺失或 null 视为空</b>，但<b>存在却类型不符一律报错、不做强制转换</b>。
/// 这避免了把上游的 <c>{"id": 123}</c> 静默当成 <c>"123"</c> 而掩盖协议变更。
/// </remarks>
public static class JsonFields
{
    /// <summary>
    /// 读取可选的 JSON 字符串。缺失或 null 返回 <c>""</c>；
    /// 存在但非字符串是协议错误，不强制转换。
    /// 对应 Go: <c>OptionalJSONString</c>。
    /// </summary>
    public static string OptionalString(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out object? value) || value is null)
        {
            return "";
        }
        if (value is string text)
        {
            return text;
        }
        throw new InvalidOperationException($"field {key}: expected string, got {TypeName(value)}");
    }

    /// <summary>
    /// 读取必填的非空 JSON 字符串（去空白后判空）。
    /// 对应 Go: <c>RequireJSONString</c>。
    /// </summary>
    public static string RequireString(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        string text = OptionalString(payload, key).Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException($"missing field: {key}");
        }
        return text;
    }

    /// <summary>
    /// 返回 <paramref name="keys"/> 中第一个存在且非空的字符串。
    /// 缺失的键跳过；类型不符会先记下，若后续键提供了合法字符串则以合法值为准。
    /// 对应 Go: <c>FirstJSONString</c>。
    /// </summary>
    public static string FirstString(IReadOnlyDictionary<string, object?>? payload, params string[] keys)
    {
        InvalidOperationException? typeError = null;
        foreach (string key in keys)
        {
            string text;
            try
            {
                text = OptionalString(payload, key);
            }
            catch (InvalidOperationException error)
            {
                typeError ??= error;
                continue;
            }
            text = text.Trim();
            if (text.Length > 0)
            {
                return text;
            }
        }
        if (typeError is not null)
        {
            throw typeError;
        }
        return "";
    }

    /// <summary>
    /// 把 <see cref="JsonElement"/> 递归转换为 Go 风格的 <c>map[string]any</c> 形态。
    /// 用于让 <c>OptionalString</c> 等工具能处理未知结构的上游响应。
    /// </summary>
    public static object? FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Dictionary<string, object?> map = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    map[property.Name] = FromElement(property.Value);
                }
                return map;
            case JsonValueKind.Array:
                List<object?> list = [];
                foreach (JsonElement child in element.EnumerateArray())
                {
                    list.Add(FromElement(child));
                }
                return list;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                // 与 Go 的 encoding/json 默认行为一致：数字统一按 float64 承载。
                return element.TryGetDouble(out double number) ? number : element.GetRawText();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                return null;
        }
    }

    /// <summary>把任意 JSON 文本解析为 Go 风格载荷；非法 JSON 返回 <c>null</c>。</summary>
    public static Dictionary<string, object?>? ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return FromElement(document.RootElement) as Dictionary<string, object?>;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把嵌套对象按键取出为另一个字典（对应 Go 的 <c>payload[key].(map[string]any)</c> 断言）。</summary>
    public static Dictionary<string, object?>? NestedObject(
        IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out object? value))
        {
            return null;
        }
        return value as Dictionary<string, object?>;
    }

    /// <summary>返回载荷中的字符串字段（缺失/类型不符均返回空串）。对应 Go 的 <c>stringField</c>。</summary>
    public static string StringField(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        try
        {
            return OptionalString(payload, key);
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private static string TypeName(object value) => value switch
    {
        string => "string",
        bool => "bool",
        double or float or int or long or decimal => "float64",
        List<object?> => "[]interface {}",
        Dictionary<string, object?> => "map[string]interface {}",
        _ => value.GetType().Name,
    };
}
