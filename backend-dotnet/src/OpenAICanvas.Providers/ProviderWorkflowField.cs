#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAICanvas.Providers;

/// <summary>
/// 云端工作流字段描述。
/// 对应 Go: <c>internal/app/workflow_provider.go</c> 的 <c>WorkflowField</c>。
/// </summary>
/// <remarks>
/// Value 与 FieldValue 兼容来源项目的两种命名；Source 可取
/// referenceImage/referenceVideo/referenceAudio/mask 等。
/// <see cref="SourceAutomatic"/> 用 <c>null</c> 兼容旧配置；<see cref="SourceConfigured"/>
/// 只在反序列化期间由别名推断填充，用于区分"未配置"与"用户明确选择"。
/// </remarks>
public sealed class WorkflowField
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("nodeId")]
    public string NodeID { get; set; } = "";

    [JsonPropertyName("classType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClassType { get; set; }

    [JsonPropertyName("fieldName")]
    public string FieldName { get; set; } = "";

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Value { get; set; }

    [JsonPropertyName("fieldValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? FieldValue { get; set; }

    [JsonPropertyName("fieldType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FieldType { get; set; }

    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    [JsonPropertyName("safeToOverride")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SafeToOverride { get; set; }

    [JsonPropertyName("optionsSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OptionsSource { get; set; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<object?>? Options { get; set; }

    [JsonPropertyName("min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Min { get; set; }

    [JsonPropertyName("max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Max { get; set; }

    [JsonPropertyName("step")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Step { get; set; }

    [JsonPropertyName("randomEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool RandomEnabled { get; set; }

    [JsonPropertyName("bindPrompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool BindPrompt { get; set; }

    /// <summary>null 兼容旧配置（默认启用）；false 表示用户明确停用。</summary>
    [JsonPropertyName("enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; set; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    [JsonPropertyName("sourceIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int SourceIndex { get; set; }

    [JsonPropertyName("imageOrder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int ImageOrder { get; set; }

    [JsonPropertyName("sourceFromUpstream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool SourceFromUpstream { get; set; }

    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool Required { get; set; }

    /// <summary>null 兼容旧配置；true 表示来源由字段名推断，false 表示用户明确选择。</summary>
    [JsonPropertyName("sourceAutomatic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SourceAutomatic { get; set; }

    /// <summary>仅在本次反序列化期间保留，区分旧数据缺字段与明确选择"保留默认值"。</summary>
    [JsonIgnore]
    public bool SourceConfigured { get; set; }
}

/// <summary>
/// WorkflowField 的反序列化归一化：兼容来源项目别名（node/input/default/bind_prompt 等），
/// 并在入口统一恢复动态来源推断。对应 Go: <c>WorkflowField.UnmarshalJSON</c>。
/// </summary>
public static class WorkflowFieldCodec
{
    /// <summary>对应 Go 的 <c>json.Unmarshal(data, &amp;fields)</c>（单个字段）。</summary>
    public static WorkflowField DecodeField(JsonElement element)
    {
        WorkflowField field = new();
        JsonElement.ObjectEnumerator enumerator = element.EnumerateObject();
        foreach (JsonProperty property in enumerator)
        {
            switch (property.Name)
            {
                case "id": field.ID = RawString(property.Value) ?? field.ID; break;
                case "nodeId": field.NodeID = RawString(property.Value) ?? field.NodeID; break;
                case "classType": field.ClassType = RawString(property.Value); break;
                case "fieldName": field.FieldName = RawString(property.Value) ?? field.FieldName; break;
                case "value": field.Value = RawAny(property.Value); break;
                case "fieldValue": field.FieldValue = RawAny(property.Value); break;
                case "fieldType": field.FieldType = RawString(property.Value); break;
                case "label": field.Label = RawString(property.Value); break;
                case "role": field.Role = RawString(property.Value); break;
                case "safeToOverride": field.SafeToOverride = RawBool(property.Value); break;
                case "optionsSource": field.OptionsSource = RawString(property.Value); break;
                case "options": field.Options = RawAnyList(property.Value); break;
                case "min": field.Min = RawAny(property.Value); break;
                case "max": field.Max = RawAny(property.Value); break;
                case "step": field.Step = RawAny(property.Value); break;
                case "randomEnabled": field.RandomEnabled = RawBool(property.Value) ?? field.RandomEnabled; break;
                case "bindPrompt": field.BindPrompt = RawBool(property.Value) ?? field.BindPrompt; break;
                case "enabled": field.Enabled = RawBool(property.Value); break;
                case "source": field.Source = RawString(property.Value); break;
                case "sourceIndex": field.SourceIndex = RawInt(property.Value) ?? field.SourceIndex; break;
                case "imageOrder": field.ImageOrder = RawInt(property.Value) ?? field.ImageOrder; break;
                case "sourceFromUpstream": field.SourceFromUpstream = RawBool(property.Value) ?? field.SourceFromUpstream; break;
                case "required": field.Required = RawBool(property.Value) ?? field.Required; break;
                case "sourceAutomatic": field.SourceAutomatic = RawBool(property.Value); break;
            }
        }

        // ---- 来源项目别名（Go UnmarshalJSON 的逐分支等价）----
        if (field.NodeID.Length == 0)
        {
            field.NodeID = RawAliasString(element, "node", "node_id", "nodeID") ?? "";
        }
        if ((field.ClassType ?? "").Length == 0)
        {
            field.ClassType = RawAliasString(element, "class_type", "typeName", "nodeType");
        }
        if (field.FieldName.Length == 0)
        {
            field.FieldName = RawAliasString(element, "input", "inputName", "input_name", "name") ?? "";
        }
        if (field.ID.Length == 0)
        {
            field.ID = RawAliasString(element, "fieldId", "field_id", "key") ?? "";
        }
        if ((field.FieldType ?? "").Length == 0)
        {
            field.FieldType = RawAliasString(element, "type");
        }
        if (field.Options is null or { Count: 0 })
        {
            if (RawAliasAny(element, "options", "values", "choices", "enum", "fieldOptions", "field_options") is { } options)
            {
                field.Options = AsObjectList(options);
            }
        }
        if (field.Min is null)
        {
            field.Min = RawAliasAny(element, "min", "minValue", "min_value")
                ?? RawAliasNestedAny(element, "min", "minValue", "min_value");
        }
        if (field.Max is null)
        {
            field.Max = RawAliasAny(element, "max", "maxValue", "max_value")
                ?? RawAliasNestedAny(element, "max", "maxValue", "max_value");
        }
        if (field.Step is null)
        {
            field.Step = RawAliasAny(element, "step", "stepValue", "step_value")
                ?? RawAliasNestedAny(element, "step", "stepValue", "step_value");
        }
        if ((field.Label ?? "").Length == 0)
        {
            field.Label = RawAliasString(element, "title");
        }

        bool sourceConfigured = HasKey(element, "source") || HasKey(element, "bind") || HasKey(element, "from");
        if ((field.Source ?? "").Length == 0)
        {
            field.Source = RawAliasString(element, "bind", "from");
        }
        if (!HasKey(element, "fieldValue"))
        {
            if (RawAliasAny(element, "defaultValue", "default_value", "default") is { } defaultValue)
            {
                field.FieldValue = defaultValue;
            }
        }
        if (!HasKey(element, "sourceIndex"))
        {
            field.SourceIndex = RawAliasInt(element, "source_index", "index") ?? field.SourceIndex;
        }
        if (!HasKey(element, "imageOrder"))
        {
            field.ImageOrder = RawAliasInt(element, "image_order") ?? field.ImageOrder;
        }
        if (!HasKey(element, "required"))
        {
            field.Required = RawAliasBool(element, "isRequired", "is_required") ?? field.Required;
        }
        if (!HasKey(element, "randomEnabled"))
        {
            field.RandomEnabled = RawAliasBool(element, "random_enabled") ?? field.RandomEnabled;
        }
        if (!HasKey(element, "bindPrompt"))
        {
            field.BindPrompt = RawAliasBool(element, "bind_prompt") ?? field.BindPrompt;
        }
        if (field.BindPrompt && (field.Source ?? "").Trim().Length == 0 && !sourceConfigured)
        {
            field.Source = "prompt";
        }
        bool sourceFromUpstreamConfigured =
            HasKey(element, "sourceFromUpstream") || HasKey(element, "source_from_upstream");
        field.SourceConfigured = sourceConfigured || sourceFromUpstreamConfigured;
        if (!HasKey(element, "sourceFromUpstream") && HasKey(element, "source_from_upstream"))
        {
            field.SourceFromUpstream =
                RawAliasBool(element, "source_from_upstream") ?? field.SourceFromUpstream;
        }
        if (!sourceConfigured && !sourceFromUpstreamConfigured && (field.Source ?? "").Trim().Length == 0)
        {
            // 旧工作流没有保存动态来源时，按字段语义恢复宽高、数量、媒体等画布输入绑定。
            field.Source = WorkflowFieldInference.WorkflowDynamicSource(field.FieldName, field.FieldType ?? "");
        }
        if (!sourceConfigured && !sourceFromUpstreamConfigured && !field.SourceFromUpstream)
        {
            switch ((field.FieldType ?? "").Trim().ToLowerInvariant())
            {
                case "image" or "video" or "audio":
                    // 来源配置默认把媒体字段绑定到上游参考素材。
                    field.SourceFromUpstream = true;
                    break;
            }
        }
        if (field.ID.Length == 0 && field.NodeID.Length > 0 && field.FieldName.Length > 0)
        {
            field.ID = field.NodeID + "::" + field.FieldName;
        }
        return field;
    }

    /// <summary>对应 Go 的 <c>json.Unmarshal(data, &amp;fields)</c>（字段数组）。</summary>
    public static List<WorkflowField> DecodeFields(JsonElement element)
    {
        List<WorkflowField> fields = [];
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                fields.Add(DecodeField(item));
            }
        }
        return fields;
    }

    /// <summary>序列化为 Go 形态的 JSON（保留 camelCase 别名语义）。</summary>
    public static string EncodeFields(IEnumerable<WorkflowField> fields)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartArray();
            foreach (WorkflowField field in fields)
            {
                WriteField(writer, field);
            }
            writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>把原始 <c>workflowFields</c> JSON（数组）归一化并反序列化。</summary>
    public static List<WorkflowField> FromJsonElement(JsonElement element) => DecodeFields(element);

    internal static void WriteField(Utf8JsonWriter writer, WorkflowField field)
    {
        writer.WriteStartObject();
        writer.WriteString("id", field.ID);
        writer.WriteString("nodeId", field.NodeID);
        if (field.ClassType is not null) writer.WriteString("classType", field.ClassType);
        writer.WriteString("fieldName", field.FieldName);
        if (field.Value is not null) WriteAny(writer, "value", field.Value);
        if (field.FieldValue is not null) WriteAny(writer, "fieldValue", field.FieldValue);
        if (field.FieldType is not null) writer.WriteString("fieldType", field.FieldType);
        if (field.Label is not null) writer.WriteString("label", field.Label);
        if (field.Role is not null) writer.WriteString("role", field.Role);
        if (field.SafeToOverride is not null) writer.WriteBoolean("safeToOverride", field.SafeToOverride.Value);
        if (field.OptionsSource is not null) writer.WriteString("optionsSource", field.OptionsSource);
        if (field.Options is not null)
        {
            writer.WriteStartArray("options");
            foreach (object? item in field.Options)
            {
                WriteAnyValue(writer, item);
            }
            writer.WriteEndArray();
        }
        if (field.Min is not null) WriteAny(writer, "min", field.Min);
        if (field.Max is not null) WriteAny(writer, "max", field.Max);
        if (field.Step is not null) WriteAny(writer, "step", field.Step);
        if (field.RandomEnabled) writer.WriteBoolean("randomEnabled", true);
        if (field.BindPrompt) writer.WriteBoolean("bindPrompt", true);
        if (field.Enabled is not null) writer.WriteBoolean("enabled", field.Enabled.Value);
        if (field.Source is not null) writer.WriteString("source", field.Source);
        if (field.SourceIndex != 0) writer.WriteNumber("sourceIndex", field.SourceIndex);
        if (field.ImageOrder != 0) writer.WriteNumber("imageOrder", field.ImageOrder);
        if (field.SourceFromUpstream) writer.WriteBoolean("sourceFromUpstream", true);
        if (field.Required) writer.WriteBoolean("required", true);
        if (field.SourceAutomatic is not null) writer.WriteBoolean("sourceAutomatic", field.SourceAutomatic.Value);
        writer.WriteEndObject();
    }

    private static void WriteAny(Utf8JsonWriter writer, string name, object? value)
    {
        writer.WritePropertyName(name);
        WriteAnyValue(writer, value);
    }

    private static void WriteAnyValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case int number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case double number: writer.WriteNumberValue(number); break;
            case decimal number: writer.WriteNumberValue(number); break;
            case JsonElement element: element.WriteTo(writer); break;
            default:
                writer.WriteStringValue(JsonSerializer.Serialize(value));
                break;
        }
    }

    private static string? RawString(JsonElement value)
    {
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return text is not null && text.Trim().Length > 0 ? text.Trim() : text;
    }

    private static bool? RawBool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => bool.TryParse(value.GetString() ?? "", out bool parsed) ? parsed : null,
        _ => null,
    };

    private static int? RawInt(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetInt32(out int parsed) ? parsed : null,
        JsonValueKind.String => int.TryParse((value.GetString() ?? "").Trim(), out int parsed) ? parsed : null,
        _ => null,
    };

    private static object? RawAny(JsonElement value) => value.ValueKind == JsonValueKind.Null
        ? null
        : value.Clone();

    private static List<object?>? RawAnyList(JsonElement value) => AsObjectList(value);

    private static List<object?>? AsObjectList(object? value) => value switch
    {
        List<object?> list => list,
        JsonElement { ValueKind: JsonValueKind.Array } element
            => element.EnumerateArray().Select(item => RawAny(item)).ToList(),
        JsonElement element when element.ValueKind == JsonValueKind.Object => NestedList(element),
        _ => null,
    };

    private static List<object?>? NestedList(JsonElement element)
    {
        foreach (string key in new[] { "choices", "options", "values", "enum" })
        {
            if (element.TryGetProperty(key, out JsonElement items)
                && items.ValueKind == JsonValueKind.Array)
            {
                return items.EnumerateArray().Select(item => RawAny(item)).ToList();
            }
        }
        return null;
    }

    private static string? RawAliasString(JsonElement element, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (!element.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            if (value.ValueKind == JsonValueKind.String
                && (value.GetString() ?? "").Trim().Length > 0)
            {
                return (value.GetString() ?? "").Trim();
            }
            object? generic = RawAny(value);
            if (generic is null)
            {
                continue;
            }
            string text = GenericString(generic).Trim();
            if (text.Length > 0 && text != "<nil>")
            {
                return text;
            }
        }
        return null;
    }

    private static object? RawAliasAny(JsonElement element, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (element.TryGetProperty(key, out JsonElement value) && value.ValueKind != JsonValueKind.Null)
            {
                return RawAny(value);
            }
        }
        return null;
    }

    private static bool? RawAliasBool(JsonElement element, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (element.TryGetProperty(key, out JsonElement value))
            {
                bool? parsed = RawBool(value);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
        }
        return null;
    }

    private static int? RawAliasInt(JsonElement element, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (element.TryGetProperty(key, out JsonElement value))
            {
                int? parsed = RawInt(value);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
        }
        return null;
    }

    private static object? RawAliasNestedAny(JsonElement element, params string[] keys)
    {
        foreach (string containerKey in new[] { "options", "values", "choices", "range", "fieldOptions", "field_options" })
        {
            if (!element.TryGetProperty(containerKey, out JsonElement container))
            {
                continue;
            }
            if (NestedFieldValue(RawAny(container), keys) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static object? NestedFieldValue(object? value, string[] keys)
    {
        if (value is List<object?> items)
        {
            foreach (object? item in items)
            {
                if (NestedFieldValue(item, keys) is { } found)
                {
                    return found;
                }
            }
            return null;
        }
        if (value is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (string key in keys)
        {
            if (element.TryGetProperty(key, out JsonElement found) && found.ValueKind != JsonValueKind.Null)
            {
                return RawAny(found);
            }
        }
        if (element.TryGetProperty("range", out JsonElement nested) && nested.ValueKind != JsonValueKind.Null)
        {
            return NestedFieldValue(RawAny(nested), keys);
        }
        return null;
    }

    private static bool HasKey(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out _);

    /// <summary>对应 Go 的 <c>fmt.Sprint</c>（普通标量转字符串）。</summary>
    public static string GenericString(object? value) => value switch
    {
        null => "<nil>",
        string text => text,
        bool flag => flag ? "true" : "false",
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
        JsonElement { ValueKind: JsonValueKind.True } => "true",
        JsonElement { ValueKind: JsonValueKind.False } => "false",
        JsonElement { ValueKind: JsonValueKind.Number } element => element.GetRawText(),
        JsonElement => "<nil>",
        byte[] data => System.Text.Encoding.UTF8.GetString(data),
        _ => value.ToString() ?? "",
    };
}

/// <summary>字段来源推断（runninghub_management.go 的按名/按类型推断子集）。</summary>
public static class WorkflowFieldInference
{
    /// <summary>对应 Go: <c>workflowDynamicSource</c>。</summary>
    public static string WorkflowDynamicSource(string name, string fieldType) =>
        WorkflowDynamicSourceForMode(name, fieldType, "");

    /// <summary>对应 Go: <c>workflowDynamicSourceForMode</c>。</summary>
    public static string WorkflowDynamicSourceForMode(string name, string fieldType, string mode)
    {
        string source = WorkflowNamedDynamicSource(name, mode);
        if (source.Length > 0)
        {
            return source;
        }
        return (fieldType ?? "").Trim().ToUpperInvariant() switch
        {
            "IMAGE" => "referenceImage",
            "VIDEO" => "referenceVideo",
            "AUDIO" => "referenceAudio",
            _ => "",
        };
    }

    /// <summary>对应 Go: <c>workflowNamedDynamicSource</c>。</summary>
    public static string WorkflowNamedDynamicSource(string name, string mode)
    {
        string key = NormalizeFieldName(name);
        if (key.Contains("mask", StringComparison.Ordinal))
        {
            return "mask";
        }
        string dimension = WorkflowDimensionNameSource(key);
        if (dimension.Length > 0)
        {
            return dimension;
        }
        switch (key)
        {
            case "ratio" or "aspectratio" or "imageaspectratio" or "imageratio"
                or "videoaspectratio" or "videoratio":
                return "aspectRatio";
            case "videoresolution" or "videoquality" or "vquality":
                return "vquality";
            case "size" or "imagesize" or "imageresolution":
                return "size";
            case "resolution" when string.Equals(mode.Trim(), "video", StringComparison.OrdinalIgnoreCase):
                return "vquality";
            case "resolution" when string.Equals(mode.Trim(), "image", StringComparison.OrdinalIgnoreCase):
                return "size";
            case "batch" or "batchsize" or "count" or "numimages" or "numberofimages"
                or "imagecount" or "imagescount":
                return "count";
            case "quality":
                return "quality";
            case "duration" or "seconds" or "durationseconds" or "videoseconds" or "videoduration"
                or "videodurationseconds" or "videolength" or "clipduration":
                return "videoSeconds";
            case "generateaudio" or "videogenerateaudio":
                return "videoGenerateAudio";
            case "watermark" or "videowatermark":
                return "videoWatermark";
            case "audioformat":
                return "audioFormat";
            case "voice" or "audiovoice":
                return "audioVoice";
            case "audiospeed":
                return "audioSpeed";
            case "audioinstructions":
                return "audioInstructions";
            case "transparentbackground" or "transparent":
                return "transparentBackground";
            default:
                return "";
        }
    }

    /// <summary>对应 Go: <c>workflowDimensionNameSource</c>。</summary>
    public static string WorkflowDimensionNameSource(string key)
    {
        foreach (string prefix in new[]
                 {
                     "", "image", "video", "size", "output", "target", "latent",
                     "frame", "canvas", "source", "resolution", "final",
                 })
        {
            if (key == prefix + "width")
            {
                return "width";
            }
            if (key == prefix + "height")
            {
                return "height";
            }
        }
        if (key is "pixelwidth" or "widthpixels")
        {
            return "width";
        }
        if (key is "pixelheight" or "heightpixels")
        {
            return "height";
        }
        return "";
    }

    /// <summary>对应 Go: <c>normalizeManagementFieldName</c>。</summary>
    public static string NormalizeFieldName(string value) =>
        (value ?? "").Trim().Replace("_", "").Replace("-", "").Replace(" ", "").ToLowerInvariant();

    /// <summary>对应 Go: <c>isManagementSeedField</c>。</summary>
    public static bool IsSeedField(string value)
    {
        string key = NormalizeFieldName(value);
        return key == "seed" || key.EndsWith("seed", StringComparison.Ordinal)
            || key.Contains("noiseseed", StringComparison.Ordinal);
    }
}
