#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAICanvas.Domain.Canvas.Capability;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>
/// 画布状态投影：canvas_get_state / 画布摘要共用的安全读取路径。
/// 对应 Go: <c>app/cloud_agent_canvas_state.go</c>。
/// </summary>
public static class CloudAgentCanvasState
{
    private delegate JsonNode? StructuredProjector(JsonNode? value, int offset, bool precise);

    /// <summary>结构化投影注册表。对应 Go: <c>cloudAgentStructuredProjectors</c>。</summary>
    private static readonly Dictionary<string, StructuredProjector> StructuredProjectors =
        new(StringComparer.Ordinal)
        {
            ["storyboard"] = (value, offset, precise) =>
                value is JsonObject storyboard ? StoryboardState(storyboard, offset, precise) : null,
            ["batch_table"] = (value, offset, precise) =>
                value is JsonObject table ? BatchTableState(table, offset, precise) : null,
        };

    /// <summary>
    /// 画布状态读取（分页摘要或按 nodeIds 精读）。
    /// 对应 Go: <c>cloudAgentCanvasState</c>。
    /// </summary>
    public static async Task<JsonObject> ReadAsync(
        Repository repository,
        string userID,
        JsonObject doc,
        int offset,
        IReadOnlyList<string> ids,
        int storyboardOffset,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || storyboardOffset < 0 || ids.Count > 8)
        {
            throw AppError.BadAuthRequest("画布读取分页参数无效");
        }
        List<JsonObject> all = CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "nodes"));
        HashSet<string> wanted = new(ids, StringComparer.Ordinal);
        int limit = ids.Count > 0 ? 16000 : 2000;
        JsonArray nodes = [];
        HashSet<string> included = new(StringComparer.Ordinal);
        int next = 0;
        for (int index = 0; index < all.Count; index++)
        {
            JsonObject node = all[index];
            string id = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "id"));
            if (ids.Count > 0)
            {
                if (!wanted.Contains(id))
                {
                    continue;
                }
            }
            else
            {
                if (index < offset)
                {
                    continue;
                }
                if (nodes.Count == 40)
                {
                    next = index;
                    break;
                }
            }
            JsonObject meta = node["metadata"] as JsonObject ?? new JsonObject();
            JsonObject item = new()
            {
                ["id"] = id,
                ["type"] = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "type")),
            };
            if (CloudAgentJsonHelpers.Get(node, "title") is JsonValue titleValue
                && titleValue.TryGetValue<string>(out string? title))
            {
                item["title"] = CloudAgentContracts.TruncateRunes(title, 300);
            }
            if (node["position"] is JsonObject position)
            {
                JsonObject safePosition = new();
                foreach (string axis in new[] { "x", "y" })
                {
                    if (SafeNumber(CloudAgentJsonHelpers.Get(position, axis)) is { } axisValue)
                    {
                        safePosition[axis] = axisValue;
                    }
                }
                item["position"] = safePosition;
            }
            foreach (string dimension in new[] { "width", "height" })
            {
                if (SafeNumber(CloudAgentJsonHelpers.Get(node, dimension)) is { } dimensionValue)
                {
                    item[dimension] = dimensionValue;
                }
            }
            if (CloudAgentJsonHelpers.Get(meta, "status") is JsonValue statusValue
                && statusValue.TryGetValue<string>(out string? status))
            {
                item["status"] = CloudAgentContracts.TruncateRunes(status, 40);
            }
            (CapabilityDescriptor capability, bool known) =
                CloudAgentNodes.ForType(CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "type")));
            if (!known)
            {
                // 读取可见不等于可变更或可用作媒体参考。
                item["agentSupported"] = false;
                item["agentUnsupportedReason"] = "仅展示基础信息；当前 Agent 不支持操作此类型节点";
                nodes.Add(item);
                included.Add(id);
                continue;
            }
            string[] fields = ids.Count > 0 ? capability.DetailFields : capability.SummaryFields;
            JsonObject projected = ProjectNodeFields(
                node, meta, capability, fields, limit, ids.Count > 0, storyboardOffset);
            foreach ((string key, JsonNode? value) in projected)
            {
                item[key] = value?.DeepClone();
            }
            string draftRunID = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(meta, "agentDraftRunId"));
            if (draftRunID.Length > 0
                && CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(meta, "taskId")).Length == 0)
            {
                JsonObject draft = new()
                {
                    ["submitted"] = false,
                    ["requiresApproval"] = true,
                    ["ownerStatus"] = "unknown",
                };
                CloudAgentExecution? owner = await repository.CloudAgentAsync(
                    userID, draftRunID, cancellationToken).ConfigureAwait(false);
                if (owner is not null && owner.CanvasID.Length > 0)
                {
                    draft["ownerStatus"] = owner.Status;
                    draft["cleanupPending"] = owner.CleanupPending;
                }
                item["generationDraft"] = draft;
            }
            if (capability.Connection.CanReference)
            {
                (JsonObject? reference, string _, AppError? referenceError) =
                    await CloudAgentJsonHelpers.ReferenceAsync(repository, userID, node, cancellationToken)
                        .ConfigureAwait(false);
                item["referenceReady"] = referenceError is null;
                if (referenceError is not null)
                {
                    item["referenceIssue"] = referenceError.Message;
                }
                else if (reference is not null)
                {
                    // 只回传已验证的公开特征；上游 payload 与存储定位绝不进入读取工具。
                    item["asset"] = new JsonObject
                    {
                        ["mimeType"] = reference["mimeType"]?.DeepClone(),
                        ["bytes"] = reference["bytes"]?.DeepClone(),
                        ["width"] = reference["width"]?.DeepClone(),
                        ["height"] = reference["height"]?.DeepClone(),
                        ["durationMs"] = reference["durationMs"]?.DeepClone(),
                        ["inputKind"] = reference["inputKind"]?.DeepClone(),
                    };
                }
            }
            nodes.Add(item);
            included.Add(id);
        }
        if (ids.Count > 0)
        {
            foreach (string id in ids)
            {
                if (!included.Contains(id))
                {
                    throw AppError.BadAuthRequest("指定节点不在当前画布");
                }
            }
        }
        JsonArray edges = [];
        foreach (JsonObject edge in CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "connections")))
        {
            string from = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "fromNodeId"));
            string to = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "toNodeId"));
            if (included.Contains(from) || included.Contains(to))
            {
                edges.Add(new JsonObject
                {
                    ["id"] = CloudAgentJsonHelpers.Get(edge, "id")?.DeepClone(),
                    ["fromNodeId"] = CloudAgentJsonHelpers.Get(edge, "fromNodeId")?.DeepClone(),
                    ["toNodeId"] = CloudAgentJsonHelpers.Get(edge, "toNodeId")?.DeepClone(),
                });
            }
        }
        return new JsonObject
        {
            ["snapshotHash"] = CloudAgentContracts.CanvasHash(doc),
            ["mediaSnapshotHash"] = CloudAgentContracts.MediaContentHash(doc),
            ["nodes"] = nodes,
            ["connections"] = edges,
            ["totalNodes"] = all.Count,
            ["nextOffset"] = next,
            ["hasMore"] = next > 0,
        };
    }

    private static JsonNode? SafeNumber(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<double>(out double d) => d,
        JsonValue v when v.TryGetValue<long>(out long l) => l,
        _ => null,
    };

    /// <summary>分镜投影：摘要定位镜头，精读逐行返回。对应 Go: <c>cloudAgentStoryboardState</c>。</summary>
    public static JsonObject StoryboardState(JsonObject storyboard, int offset, bool precise)
    {
        List<JsonObject> all = CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(storyboard, "rows"));
        int count = precise ? 1 : 20;
        int textLimit = precise ? 16000 : 200;
        JsonArray rows = [];
        int next = 0;
        string[] baseFields =
            ["id", "shotNumber", "durationSeconds", "plotDescription", "imageNodeId", "videoNodeId"];
        for (int index = 0; index < all.Count; index++)
        {
            JsonObject row = all[index];
            if (index < offset)
            {
                continue;
            }
            if (rows.Count == count)
            {
                next = index;
                break;
            }
            JsonObject item = new();
            List<string> fields = [.. baseFields];
            if (precise)
            {
                fields.AddRange(
                [
                    "videoMotionPrompt", "imageGenerationPrompt", "dialogue", "narrativeIntent", "viewerPOV",
                    "performanceBlocking", "shotSize", "emotion", "lightingAndAtmosphere", "audioEffects",
                    "camera", "motion", "timeBeats", "mustHave", "optionalDetails", "continuityOut", "negativePrompt",
                ]);
            }
            int budget = 24000;
            foreach (string key in fields)
            {
                JsonNode? raw = CloudAgentJsonHelpers.Get(row, key);
                if (raw is JsonValue v && v.TryGetValue<string>(out string? text))
                {
                    string clipped = CloudAgentContracts.TruncateRunes(text, Math.Min(textLimit, budget));
                    budget -= clipped.EnumerateRunes().Count();
                    item[key] = clipped;
                    if (clipped != text)
                    {
                        item[key + "Truncated"] = true;
                    }
                }
                else if (raw is JsonValue numberValue && numberValue.TryGetValue<double>(out double number))
                {
                    item[key] = number;
                }
            }
            foreach ((string collection, string[] keys) in new Dictionary<string, string[]>(StringComparer.Ordinal)
                     {
                         ["assetBindings"] = ["nodeId", "role", "priority"],
                         ["characters"] =
                             ["characterName", "characterAssetId", "characterVersionId", "characterImageNodeId"],
                     })
            {
                JsonArray values = [];
                List<JsonObject> entries = CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(row, collection));
                foreach (JsonObject entry in entries.Take(16))
                {
                    JsonObject value = new();
                    foreach (string key in keys)
                    {
                        JsonNode? raw = CloudAgentJsonHelpers.Get(entry, key);
                        if (raw is JsonValue v && v.TryGetValue<string>(out string? text))
                        {
                            value[key] = CloudAgentContracts.TruncateRunes(text, 200);
                        }
                        else if (raw is JsonValue n && n.TryGetValue<double>(out double number))
                        {
                            value[key] = number;
                        }
                    }
                    values.Add(value);
                }
                item[collection] = values;
                item[collection + "Truncated"] = entries.Count > 16;
            }
            rows.Add(item);
        }
        return new JsonObject
        {
            ["rows"] = rows,
            ["totalRows"] = all.Count,
            ["nextOffset"] = next,
            ["hasMore"] = next > 0,
        };
    }

    /// <summary>
    /// 批量创作表投影：只暴露组件渲染的字段。对应 Go: <c>cloudAgentBatchTableState</c>。
    /// </summary>
    public static JsonObject BatchTableState(JsonObject table, int offset, bool precise)
    {
        string operation = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(table, "operation"));
        if (operation != "creative")
        {
            operation = "try_on";
        }
        int concurrency = 10;
        if (Integer(CloudAgentJsonHelpers.Get(table, "concurrency")) is { } parsedConcurrency
            && parsedConcurrency is 1 or 5 or 10)
        {
            concurrency = parsedConcurrency;
        }
        List<JsonObject> allColumns = CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(table, "referenceColumns"));
        JsonArray columns = [];
        foreach ((JsonObject column, int index) in allColumns.Take(6).Select((c, i) => (c, i)))
        {
            string id = CloudAgentContracts.TruncateRunes(
                CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(column, "id")), 120);
            string label = CloudAgentContracts.TruncateRunes(
                CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(column, "label")), 120);
            if (id.Length > 0 && label.Length > 0)
            {
                columns.Add(new JsonObject
                {
                    ["id"] = id,
                    ["label"] = label,
                    ["mentionToken"] = $"@参考图{index + 1}",
                });
            }
        }
        if (columns.Count == 0)
        {
            columns = DefaultBatchReferenceColumns();
        }

        List<JsonObject> all = CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(table, "rows"));
        int count = 20;
        int textLimit = precise ? 16000 : 240;
        JsonArray rows = [];
        int next = 0;
        int ready = 0, enabled = 0, missingPrompt = 0, missingReferences = 0, outputLinked = 0;
        foreach (JsonObject row in all)
        {
            bool rowEnabled = CloudAgentJsonHelpers.Get(row, "enabled") is JsonValue b
                && b.TryGetValue<bool>(out bool enabledValue) && enabledValue;
            string prompt = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(row, "prompt")).Trim();
            List<JsonNode?> inputs = BatchInputIDs(
                CloudAgentJsonHelpers.Get(row, "inputNodeIds"), columns.Count);
            if (rowEnabled)
            {
                enabled++;
                if (prompt.Length == 0)
                {
                    missingPrompt++;
                }
                int minimumInputs = operation == "try_on" ? 2 : 1;
                if (inputs.Count < minimumInputs)
                {
                    missingReferences++;
                }
                if (prompt.Length > 0 && inputs.Count >= minimumInputs)
                {
                    ready++;
                }
            }
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(row, "outputNodeId")).Length > 0)
            {
                outputLinked++;
            }
        }
        for (int index = 0; index < all.Count; index++)
        {
            JsonObject row = all[index];
            if (index < offset)
            {
                continue;
            }
            if (rows.Count == count)
            {
                next = index;
                break;
            }
            JsonObject item = new()
            {
                ["id"] = CloudAgentContracts.TruncateRunes(
                    CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(row, "id")), 120),
                ["enabled"] = CloudAgentJsonHelpers.Get(row, "enabled") is JsonValue e
                    && e.TryGetValue<bool>(out bool ev) && ev,
                ["inputNodeIds"] = new JsonArray(
                    BatchInputIDs(CloudAgentJsonHelpers.Get(row, "inputNodeIds"), columns.Count)
                        .Where(node => node is not null)
                        .Select(node => node!.DeepClone())
                        .ToArray()),
            };
            string prompt = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(row, "prompt"));
            item["prompt"] = CloudAgentContracts.TruncateRunes(prompt, textLimit);
            if (prompt.EnumerateRunes().Count() > textLimit)
            {
                item["promptTruncated"] = true;
            }
            string outputNodeID = CloudAgentContracts.TruncateRunes(
                CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(row, "outputNodeId")), 120);
            if (outputNodeID.Length > 0)
            {
                item["outputNodeId"] = outputNodeID;
            }
            rows.Add(item);
        }
        return new JsonObject
        {
            ["operation"] = operation,
            ["concurrency"] = concurrency,
            ["referenceColumns"] = columns,
            ["rows"] = rows,
            ["totalRows"] = all.Count,
            ["nextOffset"] = next,
            ["hasMore"] = next > 0,
            ["generationPreview"] = new JsonObject
            {
                ["enabledRows"] = enabled,
                ["readyRows"] = ready,
                ["missingPromptRows"] = missingPrompt,
                ["missingReferenceRows"] = missingReferences,
                ["outputLinkedRows"] = outputLinked,
            },
        };
    }

    public static JsonArray DefaultBatchReferenceColumns() => new()
    {
        new JsonObject { ["id"] = "reference-1", ["label"] = "参考图 1" },
        new JsonObject { ["id"] = "reference-2", ["label"] = "参考图 2" },
        new JsonObject { ["id"] = "reference-3", ["label"] = "参考图 3" },
    };

    /// <summary>行参考图去重截断。对应 Go: <c>cloudAgentBatchInputIDs</c>。</summary>
    public static List<JsonNode?> BatchInputIDs(JsonNode? value, int limit)
    {
        if (limit <= 0 || limit > 6)
        {
            limit = 6;
        }
        List<JsonNode?> output = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        if (value is not JsonArray items)
        {
            return output;
        }
        foreach (JsonNode? item in items)
        {
            string id = CloudAgentContracts.TruncateRunes(CloudAgentJsonHelpers.StringValue(item), 120);
            if (id.Length == 0 || !seen.Add(id) || output.Count == limit)
            {
                continue;
            }
            output.Add(id);
        }
        return output;
    }

    private static int? Integer(JsonNode? value)
    {
        if (value is not JsonValue v)
        {
            return null;
        }
        if (v.TryGetValue<long>(out long integer) && integer is >= int.MinValue and <= int.MaxValue)
        {
            return (int)integer;
        }
        return null;
    }

    /// <summary>
    /// 节点字段投影（摘要/画布读取共用路径）。对应 Go: <c>cloudAgentProjectNodeFields</c>。
    /// 结构化投影由能力描述符的 ProjectionKind 路由。
    /// </summary>
    public static JsonObject ProjectNodeFields(
        JsonObject node,
        JsonObject meta,
        CapabilityDescriptor descriptor,
        string[] fields,
        int textLimit,
        bool precise,
        int structuredOffset)
    {
        JsonObject projected = new();
        foreach (string key in fields)
        {
            if (descriptor.ProjectionKind.Length > 0 && key == descriptor.ProjectionField)
            {
                if (!StructuredProjectors.TryGetValue(descriptor.ProjectionKind, out StructuredProjector? projector))
                {
                    throw AppError.BadAuthRequest($"节点 {descriptor.Label} 的结构化读取能力未注册");
                }
                JsonNode? value = ProjectionValue(node, meta, descriptor.ProjectionField);
                if (value is null)
                {
                    continue;
                }
                JsonNode? structured;
                try
                {
                    structured = projector(value, structuredOffset, precise);
                }
                catch (AppError)
                {
                    throw AppError.BadAuthRequest($"节点 {descriptor.Label} 的结构化数据无法读取");
                }
                if (structured is not null)
                {
                    projected[key] = structured;
                }
                continue;
            }
            JsonNode? raw = CloudAgentJsonHelpers.Get(node, key) ?? CloudAgentJsonHelpers.Get(meta, key);
            if (raw is null || (key == "content" && descriptor.GenerationMode.Length > 0))
            {
                continue;
            }
            (JsonNode? safe, bool truncated) = SafeProjection(raw, textLimit);
            if (safe is not null)
            {
                projected[key] = safe;
                if (truncated)
                {
                    projected[key + "Truncated"] = true;
                }
            }
        }
        return projected;
    }

    /// <summary>按点路径取值。对应 Go: <c>cloudAgentProjectionValue</c>。</summary>
    private static JsonNode? ProjectionValue(JsonObject node, JsonObject meta, string path)
    {
        string[] parts = path.Split('.');
        foreach (JsonObject root in new[] { node, meta })
        {
            JsonNode? current = root;
            bool found = true;
            foreach (string part in parts)
            {
                if (current is not JsonObject obj || !obj.TryGetPropertyValue(part, out JsonNode? child))
                {
                    found = false;
                    break;
                }
                current = child;
            }
            if (found)
            {
                return current;
            }
        }
        return null;
    }

    /// <summary>安全投影。对应 Go: <c>cloudAgentSafeProjection</c>。</summary>
    private static (JsonNode? Safe, bool Truncated) SafeProjection(JsonNode? value, int textLimit)
    {
        switch (value)
        {
            case JsonValue v when v.TryGetValue<string>(out string text):
                string truncatedText = CloudAgentContracts.TruncateRunes(text, textLimit);
                return (truncatedText, text.EnumerateRunes().Count() > textLimit);
            case JsonValue v when v.TryGetValue<bool>(out bool flag):
                return (flag, false);
            case JsonValue v when v.TryGetValue<double>(out double number):
                return (number, false);
            case JsonArray array:
            {
                int limit = Math.Min(array.Count, 32);
                JsonArray items = [];
                for (int index = 0; index < limit; index++)
                {
                    (JsonNode? safe, _) = SafeProjection(array[index], Math.Min(textLimit, 200));
                    if (safe is not null)
                    {
                        items.Add(safe);
                    }
                }
                return (items, array.Count > items.Count);
            }
            default:
                return (null, false);
        }
    }
}
