#nullable enable
using System.Globalization;
using System.Linq;
using System.Text.Json;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Providers;

/// <summary>
/// RunningHub 工作流设置页的字段收集与默认值推断。
/// 对应 Go: <c>internal/app/runninghub_management.go</c> 的
/// <c>collectManagementWorkflowFields</c> / <c>applyManagementFieldDefaults</c> 与
/// <c>workflow_provider.go</c> 的 <c>runningHubPromptFallback</c> / <c>upsertRunningHubNodeInfo</c>。
/// </summary>
internal static class WorkflowProviderManagement
{
    /// <summary>对应 Go: <c>workflowFieldsFromManagement</c>（收集 + 默认值，不再经 JSON 往返）。</summary>
    internal static List<WorkflowField> FieldsFromManagement(
        Dictionary<string, object?> workflow, string capability)
    {
        List<Dictionary<string, object?>> promptFields = PromptFallback(workflow, "prompt");
        string promptNodeID = promptFields.Count > 0
            ? MapString(promptFields[0], "nodeId")
            : "";
        string promptFieldName = promptFields.Count > 0
            ? MapString(promptFields[0], "fieldName")
            : "";

        List<string> nodeIDs = [.. workflow.Keys];
        nodeIDs.Sort((left, right) => NodeIDCompare(left, right));

        List<WorkflowField> fields = [];
        foreach (string nodeID in nodeIDs)
        {
            if (workflow[nodeID] is not Dictionary<string, object?> node)
            {
                continue;
            }
            if (JsonFields.NestedObject(node, "inputs") is not { } inputs)
            {
                continue;
            }
            string classType = MapString(node, "class_type");
            string metaTitle = ProviderWorkflowValues.WorkflowNodeMetaTitle(node).Trim();
            List<string> fieldNames = [.. inputs.Keys];
            fieldNames.Sort(StringComparer.Ordinal);
            foreach (string fieldName in fieldNames)
            {
                object? value = inputs[fieldName];
                if (ProviderWorkflowTask.IsLinkValue(value))
                {
                    continue;
                }
                string fieldType = WorkflowFieldType(fieldName, value, classType);
                string label = metaTitle.Length > 0 ? metaTitle + " · " + fieldName : fieldName;
                bool safeToOverride = SafeToOverride(classType, fieldName);
                string role = FieldRole(classType, fieldName, fieldType, "");
                WorkflowField field = new()
                {
                    ID = nodeID + "::" + fieldName,
                    NodeID = nodeID,
                    ClassType = classType,
                    FieldName = fieldName,
                    FieldValue = value,
                    FieldType = fieldType,
                    Label = label,
                    Role = role,
                    SafeToOverride = safeToOverride,
                    Enabled = safeToOverride && role != "internal",
                    Source = "",
                    SourceAutomatic = false,
                };
                if (nodeID == promptNodeID && fieldName == promptFieldName)
                {
                    field.Source = "prompt";
                    field.SourceAutomatic = true;
                    field.Required = true;
                    field.Role = "prompt";
                    field.Enabled = true;
                }
                else if (AutomaticInputSource(fieldName, fieldType) is { Length: > 0 } source)
                {
                    field.Source = source;
                    field.SourceAutomatic = true;
                    field.Role = "media";
                    field.Enabled = true;
                }
                if (WorkflowFieldInference.IsSeedField(fieldName))
                {
                    field.RandomEnabled = true;
                }
                fields.Add(field);
            }
        }
        return ApplyFieldDefaults(fields, capability);
    }

    /// <summary>对应 Go: <c>runningHubPromptFallback</c>。</summary>
    internal static List<Dictionary<string, object?>> PromptFallback(
        Dictionary<string, object?>? workflow, string prompt)
    {
        if (prompt.Trim().Length == 0 || workflow is null || workflow.Count == 0)
        {
            return [];
        }
        string bestNodeID = "";
        string bestFieldName = "";
        int bestScore = -1000;
        List<string> nodeIDs = [.. workflow.Keys];
        nodeIDs.Sort((left, right) => NodeIDCompare(left, right));
        foreach (string nodeID in nodeIDs)
        {
            if (workflow[nodeID] is not Dictionary<string, object?> node
                || JsonFields.NestedObject(node, "inputs") is not { } inputs)
            {
                continue;
            }
            string classType = MapString(node, "class_type").ToLowerInvariant();
            string metaTitle = ProviderWorkflowValues.WorkflowNodeMetaTitle(node)
                .Trim().ToLowerInvariant();
            List<string> fieldNames = [.. inputs.Keys];
            fieldNames.Sort(StringComparer.Ordinal);
            foreach (string fieldName in fieldNames)
            {
                if (!IsPromptCandidate(fieldName, classType, metaTitle)
                    || ProviderWorkflowTask.IsLinkValue(inputs[fieldName]))
                {
                    continue;
                }
                int score = PromptCandidateScore(fieldName, classType, metaTitle);
                if (bestNodeID.Length == 0 || score > bestScore)
                {
                    bestNodeID = nodeID;
                    bestFieldName = fieldName;
                    bestScore = score;
                }
            }
        }
        if (bestNodeID.Length == 0)
        {
            return [];
        }
        return
        [
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["nodeId"] = bestNodeID,
                ["fieldName"] = bestFieldName,
                ["fieldValue"] = prompt,
            },
        ];
    }

    /// <summary>对应 Go: <c>upsertRunningHubNodeInfo</c>（同节点同字段替换，否则追加）。</summary>
    internal static List<Dictionary<string, object?>> UpsertNodeInfo(
        List<Dictionary<string, object?>> items,
        List<Dictionary<string, object?>> overrides)
    {
        foreach (Dictionary<string, object?> overrideItem in overrides)
        {
            string nodeID = MapString(overrideItem, "nodeId");
            string fieldName = MapString(overrideItem, "fieldName");
            bool replaced = false;
            for (int index = 0; index < items.Count; index++)
            {
                if (MapString(items[index], "nodeId") == nodeID
                    && MapString(items[index], "fieldName") == fieldName)
                {
                    items[index] = overrideItem;
                    replaced = true;
                    break;
                }
            }
            if (!replaced)
            {
                items.Add(overrideItem);
            }
        }
        return items;
    }

    /// <summary>对应 Go: <c>applyManagementFieldDefaults</c>（工作流收集路径）。</summary>
    private static List<WorkflowField> ApplyFieldDefaults(
        List<WorkflowField> fields, string capability)
    {
        fields = fields
            .OrderBy(field => field.NodeID.Trim(), Comparer<string>.Create(NodeIDCompare))
            .ThenBy(field => field.FieldName.Trim(), StringComparer.Ordinal)
            .ToList();
        int imageOrder = 0;
        int videoOrder = 0;
        int audioOrder = 0;
        foreach (WorkflowField field in fields)
        {
            string fieldName = field.FieldName.Trim();
            string fieldType = (field.FieldType ?? "").Trim().ToUpperInvariant();
            string source = (field.Source ?? "").Trim();
            if (source.Length == 0 && field.SourceAutomatic is null or true)
            {
                source = WorkflowFieldInference.WorkflowDynamicSourceForMode(fieldName, fieldType, capability);
                if (source.Length > 0)
                {
                    field.Source = source;
                    field.SourceAutomatic = true;
                }
            }
            switch (WorkflowFieldInference.NormalizeFieldName(source))
            {
                case "prompt" or "text" or "positiveprompt" or "positive":
                    field.Required = true;
                    break;
                case "referenceimage" or "image":
                {
                    int configuredOrder = field.ImageOrder;
                    if (configuredOrder <= 0)
                    {
                        imageOrder++;
                        configuredOrder = imageOrder;
                        field.ImageOrder = configuredOrder;
                    }
                    else if (configuredOrder > imageOrder)
                    {
                        imageOrder = configuredOrder;
                    }
                    field.SourceIndex = configuredOrder - 1;
                    field.Required = configuredOrder == 1;
                    break;
                }
                case "referencevideo" or "video":
                {
                    int index = field.SourceIndex != 0
                        ? field.SourceIndex
                        : videoOrder;
                    field.SourceIndex = index;
                    if (index >= videoOrder)
                    {
                        videoOrder = index + 1;
                    }
                    field.Required = index == 0;
                    break;
                }
                case "referenceaudio" or "audio":
                {
                    int index = field.SourceIndex != 0
                        ? field.SourceIndex
                        : audioOrder;
                    field.SourceIndex = index;
                    if (index >= audioOrder)
                    {
                        audioOrder = index + 1;
                    }
                    field.Required = index == 0;
                    break;
                }
                case "mask":
                    field.Required = false;
                    break;
            }
            if (WorkflowFieldInference.IsSeedField(fieldName))
            {
                field.RandomEnabled = true;
            }
        }
        return fields;
    }

    /// <summary>对应 Go: <c>managementWorkflowFieldSafeToOverride</c>。</summary>
    private static bool SafeToOverride(string classType, string fieldName)
    {
        string classKey = WorkflowFieldInference.NormalizeFieldName(classType);
        string fieldKey = WorkflowFieldInference.NormalizeFieldName(fieldName);
        if (classKey == "int" && fieldKey == "value")
        {
            return false;
        }
        return classKey != "imageresize+" || fieldKey is not ("width" or "height" or "multipleof");
    }

    /// <summary>对应 Go: <c>managementWorkflowFieldRole</c>。</summary>
    private static string FieldRole(string classType, string fieldName, string fieldType, string source)
    {
        string normalizedSource = WorkflowFieldInference.NormalizeFieldName(source);
        if (normalizedSource is "prompt" or "text" or "positiveprompt" or "positive")
        {
            return "prompt";
        }
        if (normalizedSource is "referenceimage" or "image" or "referencevideo" or "video"
            or "referenceaudio" or "audio" or "mask")
        {
            return "media";
        }
        if (fieldType is "IMAGE" or "VIDEO" or "AUDIO")
        {
            return "media";
        }
        if (!SafeToOverride(classType, fieldName))
        {
            return "internal";
        }
        string key = WorkflowFieldInference.NormalizeFieldName(fieldName);
        if (key is "aspectratio" or "ratio" or "duration" or "durationseconds" or "seconds"
            or "videoseconds" or "quality" or "resolution" or "seed" or "noiseseed" or "steps"
            or "step" or "sigmapoints" or "cfg" or "cfgscale" or "guidance" or "guidancescale"
            or "sampler" or "samplername" or "scheduler" or "fps" or "count" or "batch"
            or "batchsize" or "generateaudio" or "watermark" or "negativeprompt" or "systemprompt")
        {
            return "business";
        }
        if (classType.Trim().Length == 0)
        {
            return "business";
        }
        return "internal";
    }

    /// <summary>对应 Go: <c>managementAutomaticInputSource</c>。</summary>
    private static string AutomaticInputSource(string fieldName, string fieldType)
    {
        if (WorkflowFieldInference.NormalizeFieldName(fieldName).Contains("mask", StringComparison.Ordinal))
        {
            return "mask";
        }
        return fieldType.Trim().ToUpperInvariant() switch
        {
            "IMAGE" => "referenceImage",
            "VIDEO" => "referenceVideo",
            "AUDIO" => "referenceAudio",
            _ => "",
        };
    }

    /// <summary>对应 Go: <c>managementWorkflowFieldType</c>。</summary>
    private static string WorkflowFieldType(string name, object? value, string classType)
    {
        string key = WorkflowFieldInference.NormalizeFieldName(name);
        string classKey = WorkflowFieldInference.NormalizeFieldName(classType);
        if (key.Contains("mask", StringComparison.Ordinal))
        {
            return "IMAGE";
        }
        if (classKey.Contains("loadimage", StringComparison.Ordinal) && key is "file" or "path")
        {
            return "IMAGE";
        }
        if (classKey.Contains("loadvideo", StringComparison.Ordinal) && key is "file" or "path")
        {
            return "VIDEO";
        }
        if (classKey.Contains("loadaudio", StringComparison.Ordinal) && key is "file" or "path")
        {
            return "AUDIO";
        }
        if (WorkflowFieldInference.WorkflowNamedDynamicSource(name, "") is
                { Length: > 0 } named && named != "mask")
        {
            return ScalarFieldType(value);
        }
        if (IsBoolValue(value))
        {
            return "BOOLEAN";
        }
        if (IsNumberValue(value))
        {
            return "NUMBER";
        }
        if (MediaFieldName(key, "image"))
        {
            return "IMAGE";
        }
        if (MediaFieldName(key, "video"))
        {
            return "VIDEO";
        }
        if (MediaFieldName(key, "audio"))
        {
            return "AUDIO";
        }
        return MediaValueType(value) is { Length: > 0 } mediaType ? mediaType : "TEXT";
    }

    /// <summary>对应 Go: <c>managementMediaValueType</c>（按默认值扩展名嗅探媒体类型）。</summary>
    private static string MediaValueType(object? value)
    {
        if (value is List<object?> { Count: > 0 } items)
        {
            value = items[0];
        }
        string text = MapString(new Dictionary<string, object?> { ["v"] = value }, "v")
            .ToLowerInvariant();
        if (text.Length == 0)
        {
            return "";
        }
        int cutoff = text.IndexOfAny(['?', '#']);
        if (cutoff >= 0)
        {
            text = text[..cutoff];
        }
        if (text.EndsWith(".png", StringComparison.Ordinal)
            || text.EndsWith(".jpg", StringComparison.Ordinal)
            || text.EndsWith(".jpeg", StringComparison.Ordinal)
            || text.EndsWith(".webp", StringComparison.Ordinal)
            || text.EndsWith(".gif", StringComparison.Ordinal)
            || text.EndsWith(".bmp", StringComparison.Ordinal)
            || text.EndsWith(".avif", StringComparison.Ordinal))
        {
            return "IMAGE";
        }
        if (text.EndsWith(".mp4", StringComparison.Ordinal)
            || text.EndsWith(".webm", StringComparison.Ordinal)
            || text.EndsWith(".mov", StringComparison.Ordinal)
            || text.EndsWith(".m4v", StringComparison.Ordinal)
            || text.EndsWith(".mkv", StringComparison.Ordinal))
        {
            return "VIDEO";
        }
        if (text.EndsWith(".mp3", StringComparison.Ordinal)
            || text.EndsWith(".wav", StringComparison.Ordinal)
            || text.EndsWith(".ogg", StringComparison.Ordinal)
            || text.EndsWith(".m4a", StringComparison.Ordinal)
            || text.EndsWith(".flac", StringComparison.Ordinal)
            || text.EndsWith(".aac", StringComparison.Ordinal))
        {
            return "AUDIO";
        }
        return "";
    }

    /// <summary>对应 Go: <c>managementScalarFieldType</c>。</summary>
    private static string ScalarFieldType(object? value)
    {
        if (IsBoolValue(value))
        {
            return "BOOLEAN";
        }
        return IsNumberValue(value) ? "NUMBER" : "TEXT";
    }

    /// <summary>对应 Go: <c>managementMediaFieldName</c>。</summary>
    private static bool MediaFieldName(string key, string mediaType)
    {
        if (key == mediaType)
        {
            return true;
        }
        foreach (string prefix in new[]
                 {
                     "input", "reference", "ref", "source", "init", "start",
                     "end", "first", "last", "control", "controlnet", "style", "subject",
                 })
        {
            if (key == prefix + mediaType)
            {
                return true;
            }
        }
        foreach (string suffix in new[] { "file", "path", "filename", "upload" })
        {
            if (key == mediaType + suffix)
            {
                return true;
            }
        }
        if (mediaType == "image")
        {
            return key is "firstframe" or "lastframe" or "startframe" or "endframe";
        }
        return false;
    }

    /// <summary>对应 Go: <c>isWorkflowPromptCandidate</c>。</summary>
    private static bool IsPromptCandidate(string fieldName, string classType, string metaTitle) =>
        IsPromptFieldName(fieldName)
        || (fieldName.Trim().ToLowerInvariant() == "value"
            && (classType.Contains("text", StringComparison.Ordinal)
                || classType.Contains("string", StringComparison.Ordinal)
                || classType.Contains("prompt", StringComparison.Ordinal)
                || metaTitle.Contains("text", StringComparison.Ordinal)
                || metaTitle.Contains("prompt", StringComparison.Ordinal)
                || metaTitle.Contains("提示词", StringComparison.Ordinal)
                || metaTitle.Contains("文本", StringComparison.Ordinal)));

    /// <summary>对应 Go: <c>isWorkflowPromptFieldName</c>。</summary>
    private static bool IsPromptFieldName(string value) =>
        WorkflowFieldInference.NormalizeFieldName(value)
            is "text" or "prompt" or "positiveprompt" or "positive" or "caption" or "description";

    /// <summary>对应 Go: <c>workflowPromptCandidateScore</c>。</summary>
    private static int PromptCandidateScore(string fieldName, string classType, string metaTitle)
    {
        string descriptor = (fieldName + " " + metaTitle).Trim().ToLowerInvariant();
        int score = 0;
        if (descriptor.Contains("negative", StringComparison.Ordinal)
            || descriptor.Contains("neg prompt", StringComparison.Ordinal)
            || descriptor.Contains("负面", StringComparison.Ordinal)
            || descriptor.Contains("反向", StringComparison.Ordinal)
            || descriptor.Contains("负向", StringComparison.Ordinal))
        {
            score -= 100;
        }
        if (descriptor.Contains("positive", StringComparison.Ordinal)
            || descriptor.Contains("正面", StringComparison.Ordinal)
            || descriptor.Contains("正向", StringComparison.Ordinal))
        {
            score += 20;
        }
        if (classType.Contains("cliptextencode", StringComparison.Ordinal))
        {
            score += 5;
        }
        if (IsPromptFieldName(fieldName))
        {
            score += 2;
        }
        return score;
    }

    /// <summary>对应 Go: <c>managementNodeIDLess</c> 的比较器形态（数字优先，字符串次之）。</summary>
    private static int NodeIDCompare(string left, string right)
    {
        string leftText = left.Trim();
        string rightText = right.Trim();
        bool leftParsed = long.TryParse(leftText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long leftNumber);
        bool rightParsed = long.TryParse(rightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long rightNumber);
        if (leftParsed && rightParsed)
        {
            return leftNumber.CompareTo(rightNumber);
        }
        return string.CompareOrdinal(leftText, rightText);
    }

    private static string MapString(IReadOnlyDictionary<string, object?> payload, string key)
    {
        string text = WorkflowFieldCodec.GenericString(
            payload.TryGetValue(key, out object? value) ? value : null).Trim();
        return text == "<nil>" ? "" : text;
    }

    private static bool IsBoolValue(object? value) => value switch
    {
        bool => true,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => true,
        _ => false,
    };

    private static bool IsNumberValue(object? value) => value switch
    {
        sbyte or byte or short or ushort or int or uint or long or ulong
            or float or double or decimal => true,
        JsonElement { ValueKind: JsonValueKind.Number } => true,
        _ => false,
    };
}
