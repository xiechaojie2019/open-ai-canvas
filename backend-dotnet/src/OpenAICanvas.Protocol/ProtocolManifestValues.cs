#nullable enable
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Protocol;

/// <summary>
/// 声明式清单的「值层」：平台请求到 JSON 形态的投影、点路径读取、旧版字段表达式与字符串变换。
/// 对应 Go: <c>internal/protocol/manifest.go</c> 的值区段与 <c>builtin.go</c> 的小工具。
/// </summary>
public static class ProtocolManifestValues
{
    /// <summary>对应 Go: <c>defaultValue</c>（空白串按空处理）。</summary>
    public static string DefaultValue(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>对应 Go: <c>firstString</c>。</summary>
    public static string FirstString(Dictionary<string, object?>? payload, params string[] keys)
    {
        if (payload is null)
        {
            return "";
        }
        foreach (string key in keys)
        {
            if (payload.TryGetValue(key, out object? value) && value is string text && text.Trim().Length != 0)
            {
                return text.Trim();
            }
        }
        return "";
    }

    /// <summary>对应 Go: <c>pathValue</c>（即 <c>manifestPathValue</c>）。</summary>
    public static object? PathValue(object? payload, string path) => ProtocolExpression.PathValue(payload, path);

    /// <summary>对应 Go: <c>arrayValue</c>（非数组返回空）。</summary>
    public static List<object?> ArrayValue(object? value) => value as List<object?> ?? [];

    /// <summary>对应 Go: <c>firstPathValue</c>（第一个非空字符串命中）。</summary>
    public static string FirstPathValue(Dictionary<string, object?>? payload, IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (PathValue(payload, path) is string text && text.Trim().Length != 0)
            {
                return text.Trim();
            }
        }
        return "";
    }

    /// <summary>
    /// 对应 Go: <c>manifestError</c>。字符串取一组「不算失败」的字面量，数值按非零判失败；
    /// 其余类型（含 bool、数组、对象）一律判失败 —— 这与直觉相反，不能改成「有值才算失败」。
    /// </summary>
    public static bool ManifestError(Dictionary<string, object?>? payload, IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            object? value = PathValue(payload, path);
            switch (value)
            {
                case null:
                    continue;
                case string text:
                {
                    string normalized = text.Trim().ToLowerInvariant();
                    if (normalized is "" or "0" or "ok" or "success" or "succeeded" or "true")
                    {
                        continue;
                    }
                    return true;
                }
                case double number:
                    if (number == 0)
                    {
                        continue;
                    }
                    return true;
                case float number:
                    if (number == 0)
                    {
                        continue;
                    }
                    return true;
                case int number:
                    if (number == 0)
                    {
                        continue;
                    }
                    return true;
                case long number:
                    if (number == 0)
                    {
                        continue;
                    }
                    return true;
                default:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 对应 Go: <c>firstPathJSONValue</c>。标量字段保持字符串形态，
    /// 但允许上游把工具参数返回为对象（平台工具契约始终存 JSON 文本）。
    /// </summary>
    public static string FirstPathJsonValue(Dictionary<string, object?>? payload, IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            object? value = PathValue(payload, path);
            switch (value)
            {
                case string text:
                    if (text.Trim().Length != 0)
                    {
                        return text.Trim();
                    }
                    break;
                case null:
                    break;
                default:
                    try
                    {
                        string encoded = ProtocolJsonValue.Encode(value);
                        if (encoded.Length != 0)
                        {
                            return encoded;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Go 忽略 json.Marshal 的错误并继续下一个路径。
                    }
                    break;
            }
        }
        return "";
    }

    /// <summary>对应 Go: <c>setMapPath</c>（按点路径写入，中间层不存在则新建）。</summary>
    public static void SetMapPath(Dictionary<string, object?> target, string path, object? value)
    {
        string[] parts = path.Trim('.').Split('.');
        Dictionary<string, object?> current = target;
        for (int index = 0; index + 1 < parts.Length; index++)
        {
            if (current.TryGetValue(parts[index], out object? existing) &&
                existing is Dictionary<string, object?> nested)
            {
                current = nested;
                continue;
            }
            Dictionary<string, object?> created = new(StringComparer.Ordinal);
            current[parts[index]] = created;
            current = created;
        }
        if (parts.Length > 0)
        {
            current[parts[^1]] = value;
        }
    }

    /// <summary>
    /// 对应 Go: <c>isRelativePath</c>。等价于 <c>url.Parse</c> 后要求：解析成功、
    /// <c>Path</c> 非空且以单个 <c>/</c> 开头、无 Host、无 User、无 Scheme。
    /// </summary>
    public static bool IsRelativePath(string? path)
    {
        string trimmed = (path ?? "").Trim();
        if (trimmed.Length == 0 || !trimmed.StartsWith('/') || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }
        // Go 的 url.Parse 会拒绝控制字符与非法百分号转义。
        for (int index = 0; index < trimmed.Length; index++)
        {
            char ch = trimmed[index];
            if (ch < 0x20 || ch == 0x7f)
            {
                return false;
            }
            if (ch == '%')
            {
                if (index + 2 >= trimmed.Length ||
                    !Uri.IsHexDigit(trimmed[index + 1]) ||
                    !Uri.IsHexDigit(trimmed[index + 2]))
                {
                    return false;
                }
            }
        }
        int separator = trimmed.IndexOfAny(['?', '#']);
        string pathOnly = separator < 0 ? trimmed : trimmed[..separator];
        return pathOnly.Length > 0;
    }

    /// <summary>对应 Go: <c>validManifestIdentifier</c>（1..96 位小写标识符，首位不能是分隔符）。</summary>
    public static bool IsValidIdentifier(string? value)
    {
        if (value is null || value.Length == 0 || value.Length > 96)
        {
            return false;
        }
        for (int index = 0; index < value.Length; index++)
        {
            char ch = value[index];
            bool allowed = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_' || ch == '.';
            if (!allowed)
            {
                return false;
            }
            if (index == 0 && (ch == '-' || ch == '_' || ch == '.'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 对应 Go: <c>normalizeManifestValue</c>。键恰好是连续的 <c>"0".."n-1"</c> 时把对象还原成数组 ——
    /// 这是旧版 <c>fields</c> 表达式构造 JSON 数组的唯一途径，不能省略。
    /// </summary>
    public static object? NormalizeValue(object? value)
    {
        switch (value)
        {
            case Dictionary<string, object?> map:
            {
                if (map.Count == 0)
                {
                    return map;
                }
                bool sequential = true;
                for (int index = 0; index < map.Count; index++)
                {
                    if (!map.ContainsKey(index.ToString(CultureInfo.InvariantCulture)))
                    {
                        sequential = false;
                        break;
                    }
                }
                if (sequential)
                {
                    List<object?> array = new(map.Count);
                    for (int index = 0; index < map.Count; index++)
                    {
                        array.Add(NormalizeValue(map[index.ToString(CultureInfo.InvariantCulture)]));
                    }
                    return array;
                }
                Dictionary<string, object?> result = new(map.Count, StringComparer.Ordinal);
                foreach ((string key, object? item) in map)
                {
                    result[key] = NormalizeValue(item);
                }
                return result;
            }
            case List<object?> items:
            {
                List<object?> array = new(items.Count);
                foreach (object? item in items)
                {
                    array.Add(NormalizeValue(item));
                }
                return array;
            }
            default:
                return value;
        }
    }

    /// <summary>
    /// 平台请求的 JSON 形态投影。对应 Go: <c>manifestRequestValues</c>。
    /// 这是刻意做小的视图：让上传的清单能按下标引用素材，同时不暴露宿主结构、也不加 provider 专用代码。
    /// </summary>
    public static Dictionary<string, object?> RequestValues(GenerationRequest request)
    {
        List<object?> userContent = [];
        if (request.Prompt.Trim().Length != 0)
        {
            userContent.Add(Map(("type", "text"), ("text", request.Prompt)));
        }
        foreach (MediaReference item in request.Images)
        {
            string value = DefaultValue(item.URL, item.DataURL);
            if (value.Length != 0)
            {
                userContent.Add(Map(("type", "image_url"), ("image_url", Map(("url", value)))));
            }
        }
        foreach (MediaReference item in request.Videos)
        {
            string value = DefaultValue(item.URL, item.DataURL);
            if (value.Length != 0)
            {
                userContent.Add(Map(("type", "video_url"), ("video_url", Map(("url", value)))));
            }
        }
        foreach (MediaReference item in request.Audios)
        {
            string value = DefaultValue(item.URL, item.DataURL);
            if (value.Length != 0)
            {
                userContent.Add(Map(("type", "audio_url"), ("audio_url", Map(("url", value)))));
            }
        }

        List<object?> messages = new(request.Messages.Count + 2);
        // instructions 是统一的系统指令字段，但多数 OpenAI 系协议只映射 request.messages。
        // 系统指令必须进入消息数组，否则会被静默丢弃；带独立 system 字段的协议（Claude、
        // Gemini、Responses）在各自模板里过滤 system 角色，不会重复发送。
        string instructions = request.Instructions.Trim();
        if (instructions.Length != 0 && !HasSystemMessage(request.Messages))
        {
            messages.Add(Map(("role", "system"), ("content", instructions)));
        }
        foreach (Message message in request.Messages)
        {
            if (message.Role.Trim().Length == 0 || message.Content is null)
            {
                continue;
            }
            messages.Add(Map(("role", message.Role), ("content", message.Content)));
        }
        if (userContent.Count > 0)
        {
            object? content = userContent;
            if (userContent.Count == 1 && request.Images.Count + request.Videos.Count + request.Audios.Count == 0)
            {
                content = request.Prompt;
            }
            messages.Add(Map(("role", "user"), ("content", content)));
        }

        List<MediaReference> inputs = request.Inputs;
        if (inputs.Count == 0)
        {
            inputs = [.. request.Images, .. request.Videos, .. request.Audios];
        }

        OutputOptions output = CopyOutputOptions(request.Output);
        if (output.Count == 0)
        {
            output.Count = request.ImageCount;
        }
        if (output.Duration == 0)
        {
            output.Duration = request.Duration;
        }
        if (output.AspectRatio.Length == 0)
        {
            output.AspectRatio = request.AspectRatio;
        }
        if (output.Resolution.Length == 0)
        {
            output.Resolution = request.Resolution;
        }
        if (output.Quality.Length == 0)
        {
            output.Quality = request.Quality;
        }
        output.GenerateAudio = output.GenerateAudio || request.GenerateAudio;
        output.Watermark = output.Watermark || request.Watermark;
        object? outputValue = RequestAsManifestValue(output);

        return Map(
            ("capability", request.Capability),
            ("model", request.Model),
            ("prompt", request.Prompt),
            ("instructions", request.Instructions),
            ("messages", messages),
            ("inputs", MediaValues(inputs)),
            ("images", MediaValues(request.Images)),
            ("videos", MediaValues(request.Videos)),
            ("audios", MediaValues(request.Audios)),
            ("imageCount", request.ImageCount),
            ("duration", request.Duration),
            ("aspectRatio", request.AspectRatio),
            ("resolution", request.Resolution),
            ("quality", request.Quality),
            ("generateAudio", request.GenerateAudio),
            ("watermark", request.Watermark),
            ("operation", request.Operation),
            ("output", outputValue),
            ("providerOptions", request.ProviderOptions),
            ("extra", request.Extra));
    }

    /// <summary>对应 Go: <c>hasSystemMessage</c>。</summary>
    public static bool HasSystemMessage(IReadOnlyList<Message> messages)
    {
        foreach (Message message in messages)
        {
            if (string.Equals(message.Role.Trim(), "system", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 对应 Go: <c>requestAsManifestValue</c>：先按 Go 的 <c>omitempty</c> 语义序列化，
    /// 再解析回 JSON 形态的值。序列化失败时返回 <c>null</c>（Go 侧同样忽略该错误）。
    /// </summary>
    public static object? RequestAsManifestValue(object? value)
    {
        if (value is null)
        {
            return null;
        }
        try
        {
            string encoded = JsonSerializer.Serialize(value, value.GetType(), ProtocolManifestJson.GoWriteOptions);
            using JsonDocument document = JsonDocument.Parse(encoded);
            return JsonFields.FromElement(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>对应 Go: <c>manifestModelID</c>（取 <c>::</c> 之后的小写模型名）。</summary>
    public static string ModelID(GenerationRequest request)
    {
        string modelID = request.Model.Trim();
        int separator = modelID.LastIndexOf("::", StringComparison.Ordinal);
        if (separator >= 0)
        {
            modelID = modelID[(separator + 2)..];
        }
        return modelID.ToLowerInvariant();
    }

    /// <summary>对应 Go: <c>manifestMediaValues</c>。</summary>
    public static List<object?> MediaValues(IReadOnlyList<MediaReference> values)
    {
        List<object?> result = new(values.Count);
        for (int index = 0; index < values.Count; index++)
        {
            MediaReference value = values[index];
            int order = value.Order;
            if (order == 0 && index > 0)
            {
                order = index;
            }
            string resolved = DefaultValue(value.URL, value.DataURL);
            result.Add(Map(
                ("id", value.ID),
                ("url", value.URL),
                ("dataUrl", value.DataURL),
                ("value", resolved),
                ("kind", value.Kind),
                ("role", value.Role),
                ("mimeType", value.MIMEType),
                ("name", value.Name),
                ("order", order),
                ("weight", value.Weight),
                ("metadata", value.Metadata),
                ("source", Map(
                    ("type", MediaSourceType(value)),
                    ("value", resolved),
                    ("mimeType", value.MIMEType))),
                ("ephemeral", value.Ephemeral)));
        }
        return result;
    }

    /// <summary>对应 Go: <c>manifestMediaSourceType</c>。</summary>
    public static string MediaSourceType(MediaReference value)
    {
        if (value.DataURL.Length != 0)
        {
            return "data";
        }
        if (value.URL.Length != 0)
        {
            return "url";
        }
        return "unknown";
    }

    private static OutputOptions CopyOutputOptions(OutputOptions source) => new()
    {
        Count = source.Count,
        Duration = source.Duration,
        AspectRatio = source.AspectRatio,
        Width = source.Width,
        Height = source.Height,
        Resolution = source.Resolution,
        Quality = source.Quality,
        FPS = source.FPS,
        GenerateAudio = source.GenerateAudio,
        Watermark = source.Watermark,
        Format = source.Format,
        Options = source.Options,
    };

    private static Dictionary<string, object?> Map(params (string Key, object? Value)[] entries)
    {
        Dictionary<string, object?> result = new(entries.Length, StringComparer.Ordinal);
        foreach ((string key, object? value) in entries)
        {
            result[key] = value;
        }
        return result;
    }

    /// <summary>
    /// 对应 Go: <c>applyManifestTransform</c>。顺序敏感：先做类型强转与 <c>omit_zero</c>，
    /// 再做依赖模型名的条件丢弃，最后才轮到字符串变换。返回 <c>null</c> 表示「该字段不写入请求」。
    /// </summary>
    public static object? ApplyTransform(object? value, string transform, GenerationRequest request)
    {
        string normalized = (transform ?? "").Trim().ToLowerInvariant();
        if (normalized is "bool" or "boolean")
        {
            switch (value)
            {
                case bool flag:
                    return flag;
                case string text:
                {
                    string candidate = text.Trim().ToLowerInvariant();
                    return candidate is "true" or "1" or "yes";
                }
                case int number:
                    return number != 0;
                case long number:
                    return number != 0;
                case double number:
                    return number != 0;
                case float number:
                    return number != 0;
                default:
                    return false;
            }
        }
        if (normalized is "int" or "integer")
        {
            switch (value)
            {
                case int number:
                    return number;
                case long number:
                    return (int)number;
                case double number:
                    return (int)number;
                case float number:
                    return (int)number;
                case string text:
                    return long.TryParse(
                        text.Trim(),
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out long parsed) ? parsed : 0L;
                default:
                    return 0L;
            }
        }
        if (normalized == "omit_zero")
        {
            switch (value)
            {
                case int number when number == 0:
                    return null;
                case long number when number == 0:
                    return null;
                case double number when number == 0:
                    return null;
                case float number when number == 0:
                    return null;
                case string text when text.Trim() is "0" or "":
                    return null;
            }
        }

        string cleanModel = ModelID(request);
        const string unlessContains = "omit_unless_model_contains:";
        const string ifContains = "omit_if_model_contains:";
        const string unlessEquals = "omit_unless_model_equals:";
        const string ifEquals = "omit_if_model_equals:";
        if (normalized.StartsWith(unlessContains, StringComparison.Ordinal))
        {
            string needle = normalized[unlessContains.Length..].Trim().ToLowerInvariant();
            if (needle.Length == 0 || !cleanModel.Contains(needle, StringComparison.Ordinal))
            {
                return null;
            }
        }
        if (normalized.StartsWith(ifContains, StringComparison.Ordinal))
        {
            string needle = normalized[ifContains.Length..].Trim().ToLowerInvariant();
            if (needle.Length != 0 && cleanModel.Contains(needle, StringComparison.Ordinal))
            {
                return null;
            }
        }
        if (normalized.StartsWith(unlessEquals, StringComparison.Ordinal))
        {
            string target = normalized[unlessEquals.Length..].Trim().ToLowerInvariant();
            if (target.Length == 0 || !string.Equals(cleanModel, target, StringComparison.Ordinal))
            {
                return null;
            }
        }
        if (normalized.StartsWith(ifEquals, StringComparison.Ordinal))
        {
            string target = normalized[ifEquals.Length..].Trim().ToLowerInvariant();
            if (target.Length != 0 && string.Equals(cleanModel, target, StringComparison.Ordinal))
            {
                return null;
            }
        }

        string? textValue = value as string;
        if (normalized == "omit_empty" && (textValue is null || textValue.Trim().Length == 0))
        {
            return null;
        }
        if (normalized == "omit_auto" &&
            (textValue is null || textValue.Trim().Length == 0 ||
             string.Equals(textValue.Trim(), "auto", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(textValue.Trim(), "default", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        if (textValue is null)
        {
            return value;
        }
        return normalized switch
        {
            "trim" => textValue.Trim(),
            "lower" or "lowercase" => textValue.ToLowerInvariant(),
            "upper" or "uppercase" => textValue.ToUpperInvariant(),
            "resolution_p" => ResolutionDigitsPattern.IsMatch(textValue.Trim()) ? textValue.Trim() + "p" : textValue,
            // 兼容已安装的 1.0.1 清单。新插件应声明精确枚举值并使用通用字符串变换。
            "video-resolution" or "video_resolution" or "videoresolution" => NormalizeLegacyVideoResolution(textValue),
            _ => value,
        };
    }

    /// <summary>对应 Go: <c>normalizeLegacyManifestVideoResolution</c>（纯数字补 <c>p</c>）。</summary>
    public static string NormalizeLegacyVideoResolution(string? value)
    {
        string normalized = (value ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return "";
        }
        foreach (char ch in normalized)
        {
            if (ch < '0' || ch > '9')
            {
                return normalized;
            }
        }
        return normalized + "p";
    }

    /// <summary>用于 <c>resolution_p</c> 的数字判定，对应 Go 的纯数字正则。</summary>
    private static readonly System.Text.RegularExpressions.Regex ResolutionDigitsPattern =
        new("^[0-9]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
