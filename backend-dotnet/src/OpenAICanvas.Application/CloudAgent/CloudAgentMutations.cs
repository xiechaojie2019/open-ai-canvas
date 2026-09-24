#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAICanvas.Domain.Canvas.Capability;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>画布变更记录入参。对应 Go: <c>app.cloudAgentMutationInput</c>。</summary>
public sealed class CloudAgentMutationInput
{
    public string RunID { get; set; } = "";
    public string UserID { get; set; } = "";
    public string CanvasID { get; set; } = "";
    public string StepID { get; set; } = "";
    public string Operation { get; set; } = "";
    public string BeforeSnapshotHash { get; set; } = "";
    public string AfterSnapshotHash { get; set; } = "";
    public string BeforeJSON { get; set; } = "";
    public bool HasSubmittedTask { get; set; }
    public CloudAgentApprovalPreviewDto? Preview { get; set; }
}

/// <summary>变更记录回调。对应 Go: <c>cloudAgentMutationRecorder</c>（事务内执行）。</summary>
public delegate Task CloudAgentMutationRecorder(
    CloudAgentMutationContext context, CloudAgentMutationInput input);

/// <summary>
/// 画布变更：记录持久化、事件投影与 canvas_apply_ops 执行。
/// 对应 Go: <c>app/cloud_agent_mutation.go</c>、<c>cloud_agent_canvas_events.go</c>、
/// <c>cloud_agent_approval_preview.go</c> 的画布部分与 <c>applyCloudAgentCanvas</c>。
/// </summary>
public static class CloudAgentMutations
{
    private const long SnapshotLimit = 1 << 20;

    /// <summary>Go time.RFC3339Nano（UTC，裁剪末尾零）。</summary>
    private static string Rfc3339Nano(DateTime value)
    {
        string baseText = value.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        long ticksOfSecond = value.Ticks % TimeSpan.TicksPerSecond;
        string fraction = ticksOfSecond == 0
            ? ""
            : "." + (ticksOfSecond * 100).ToString("D9", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0');
        return baseText + fraction + "Z";
    }

    /// <summary>记录变更（快照超限则标记不可撤销）。对应 Go: <c>recordCloudAgentCanvasMutation</c>。</summary>
    public static async Task RecordAsync(
        CloudAgentMutationContext context, CloudAgentMutationInput input)
    {
        if (input.RunID.Length == 0 || input.UserID.Length == 0 || input.CanvasID.Length == 0
            || input.StepID.Length == 0 || input.Operation.Length == 0)
        {
            throw AppError.BadAuthRequest("画布变更缺少可追踪的 Agent 操作信息");
        }
        if (input.BeforeSnapshotHash.Length == 0 || input.AfterSnapshotHash.Length == 0)
        {
            throw AppError.BadAuthRequest("画布变更缺少有效快照");
        }
        CloudAgentCanvasMutation mutation = new()
        {
            ID = IdGenerator.NewId(),
            RunID = input.RunID,
            UserID = input.UserID,
            CanvasID = input.CanvasID,
            StepID = input.StepID,
            Operation = input.Operation,
            BeforeSnapshotHash = input.BeforeSnapshotHash,
            AfterSnapshotHash = input.AfterSnapshotHash,
            HasSubmittedTask = input.HasSubmittedTask,
            Status = "applied",
            CreatedAt = DateTime.UtcNow,
        };
        if (input.BeforeJSON.Length <= SnapshotLimit)
        {
            mutation.BeforeJSON = input.BeforeJSON;
        }
        else
        {
            mutation.Status = "not_undoable";
        }
        await context.CreateCloudAgentCanvasMutationAsync(mutation).ConfigureAwait(false);
    }

    /// <summary>run 绑定的事件记录回调。对应 Go: <c>cloudAgentCanvasEventRecorder</c>。</summary>
    public static CloudAgentMutationRecorder RecorderForRun(
        string runID, CloudAgentRuntimeDto state) =>
        (context, input) => RecordWithEventAsync(context, runID, state, input);

    private static async Task RecordWithEventAsync(
        CloudAgentMutationContext context, string runID, CloudAgentRuntimeDto state,
        CloudAgentMutationInput input)
    {
        input.RunID = runID;
        await RecordAsync(context, input).ConfigureAwait(false);
        await EmitCanvasChangeAsync(context, runID, state, input).ConfigureAwait(false);
    }

    /// <summary>前后对象差异。对应 Go: <c>cloudAgentObjectChanges</c>。</summary>
    private static List<(JsonObject? Before, JsonObject After)> ObjectChanges(JsonNode? before, JsonNode? after)
    {
        Dictionary<string, JsonObject> previous = new(StringComparer.Ordinal);
        foreach (JsonObject item in CloudAgentJsonHelpers.Maps(before))
        {
            previous[CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(item, "id"))] = item;
        }
        List<(JsonObject?, JsonObject)> changes = [];
        foreach (JsonObject item in CloudAgentJsonHelpers.Maps(after))
        {
            string id = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(item, "id"));
            previous.TryGetValue(id, out JsonObject? old);
            if (!SameJson(old, item))
            {
                changes.Add((old, item));
            }
        }
        return changes;
    }

    private static bool SameJson(JsonNode? left, JsonNode? right)
    {
        string? leftJson = left?.ToJsonString();
        string? rightJson = right?.ToJsonString();
        if (leftJson == rightJson)
        {
            return true;
        }
        // 键序无关比较：用规范化哈希（Go reflect.DeepEqual 对解析后 map 的语义）。
        return CloudAgentContracts.CreationHashOf(left) == CloudAgentContracts.CreationHashOf(right);
    }

    /// <summary>画布变更事件投影。对应 Go: <c>emitCloudAgentCanvasChange</c>。</summary>
    public static async Task EmitCanvasChangeAsync(
        CloudAgentMutationContext context, string runID, CloudAgentRuntimeDto state,
        CloudAgentMutationInput input)
    {
        JsonObject before = CloudAgentJsonHelpers.Document(input.BeforeJSON);
        CanvasProject? canvas = await context.CanvasProjectForUserAsync(
            input.UserID, input.CanvasID).ConfigureAwait(false)
            ?? throw AppError.NotFound("画布不存在");
        JsonObject after = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
        List<(JsonObject? Before, JsonObject After)> nodeChanges =
            ObjectChanges(CloudAgentJsonHelpers.Get(before, "nodes"), CloudAgentJsonHelpers.Get(after, "nodes"));
        List<(JsonObject? Before, JsonObject After)> edgeChanges =
            ObjectChanges(CloudAgentJsonHelpers.Get(before, "connections"), CloudAgentJsonHelpers.Get(after, "connections"));
        if (nodeChanges.Count == 0 && edgeChanges.Count == 0)
        {
            return;
        }
        JsonArray actions = [];
        Dictionary<string, CloudAgentApprovalPreviewItemDto> previewByOperationAndNode = new(StringComparer.Ordinal);
        Dictionary<string, CloudAgentApprovalPreviewItemDto> previewByUniqueNode = new(StringComparer.Ordinal);
        HashSet<string> ambiguousPreviewNode = new(StringComparer.Ordinal);
        if (input.Preview is not null)
        {
            foreach (CloudAgentApprovalPreviewItemDto item in input.Preview.Items)
            {
                if (item.NodeID.Length == 0)
                {
                    continue;
                }
                previewByOperationAndNode[item.Operation + "\x00" + item.NodeID] = item;
                if (!previewByUniqueNode.TryAdd(item.NodeID, item))
                {
                    ambiguousPreviewNode.Add(item.NodeID);
                }
            }
        }
        CloudAgentApprovalPreviewItemDto? FindPreview(string operation, string nodeID)
        {
            if (previewByOperationAndNode.TryGetValue(operation + "\x00" + nodeID, out CloudAgentApprovalPreviewItemDto? byOp))
            {
                return byOp;
            }
            return !ambiguousPreviewNode.Contains(nodeID)
                && previewByUniqueNode.TryGetValue(nodeID, out CloudAgentApprovalPreviewItemDto? byNode)
                    ? byNode
                    : null;
        }
        foreach ((JsonObject? beforeNode, JsonObject node) in nodeChanges)
        {
            string action = beforeNode is null ? "created" : "updated";
            JsonObject entry = new()
            {
                ["action"] = action,
                ["nodeId"] = CloudAgentJsonHelpers.Get(node, "id")?.DeepClone(),
                ["title"] = CloudAgentJsonHelpers.Get(node, "title")?.DeepClone(),
                ["nodeType"] = CloudAgentJsonHelpers.Get(node, "type")?.DeepClone(),
            };
            string previewOperation = action == "created" ? "add_node" : "update_node";
            CloudAgentApprovalPreviewItemDto? preview = FindPreview(
                previewOperation, CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "id")));
            if (preview is not null)
            {
                if (preview.NodeTitle.Length > 0)
                {
                    entry["title"] = preview.NodeTitle;
                }
                if (preview.Fields is { Count: > 0 })
                {
                    entry["fields"] = new JsonArray(preview.Fields.Select(f => JsonValue.Create(f)).ToArray());
                }
                if (preview.ResultTitle.Length > 0)
                {
                    entry["resultTitle"] = preview.ResultTitle;
                }
                if (preview.Summary.Length > 0)
                {
                    entry["summary"] = preview.Summary;
                }
            }
            actions.Add(entry);
        }
        Dictionary<string, JsonObject> byID = CloudAgentJsonHelpers.Objects(CloudAgentJsonHelpers.Get(after, "nodes"));
        foreach ((JsonObject? _, JsonObject edge) in edgeChanges)
        {
            string fromID = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "fromNodeId"));
            if (!byID.TryGetValue(fromID, out JsonObject? node))
            {
                continue;
            }
            JsonObject entry = new()
            {
                ["action"] = "referenced",
                ["nodeId"] = CloudAgentJsonHelpers.Get(node, "id")?.DeepClone(),
                ["title"] = CloudAgentJsonHelpers.Get(node, "title")?.DeepClone(),
                ["nodeType"] = CloudAgentJsonHelpers.Get(node, "type")?.DeepClone(),
                ["targetNodeId"] = CloudAgentJsonHelpers.Get(edge, "toNodeId")?.DeepClone(),
            };
            string toID = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "toNodeId"));
            if (byID.TryGetValue(toID, out JsonObject? target))
            {
                entry["targetTitle"] = CloudAgentJsonHelpers.Get(target, "title")?.DeepClone();
                entry["targetNodeType"] = CloudAgentJsonHelpers.Get(target, "type")?.DeepClone();
            }
            CloudAgentApprovalPreviewItemDto? preview = FindPreview("connect_nodes", fromID);
            if (preview is not null && preview.TargetNodeTitle.Length > 0)
            {
                entry["targetTitle"] = preview.TargetNodeTitle;
                entry["targetNodeType"] = preview.TargetNodeType;
                if (preview.Summary.Length > 0)
                {
                    entry["summary"] = preview.Summary;
                }
            }
            actions.Add(entry);
        }
        JsonObject payload = new()
        {
            ["canvasId"] = input.CanvasID,
            ["operation"] = input.Operation,
            ["actions"] = actions,
            ["canvasPatch"] = new JsonObject
            {
                ["canvasId"] = input.CanvasID,
                ["updatedAt"] = CloudAgentJsonHelpers.Get(after, "updatedAt")?.DeepClone(),
                ["nodes"] = new JsonArray(nodeChanges.Select(c => (JsonNode?)ChangeJson(c)).ToArray()),
                ["connections"] = new JsonArray(edgeChanges.Select(c => (JsonNode?)ChangeJson(c)).ToArray()),
            },
        };
        if (input.Preview is not null)
        {
            payload["preview"] = JsonSerializer.SerializeToNode(input.Preview, GoJson.WriteOptions);
            payload["text"] = input.Preview.Description;
        }
        if (state.CallIndex >= 0 && state.CallIndex < state.Calls.Count)
        {
            payload["callId"] = state.Calls[state.CallIndex].ID;
        }
        CloudAgentContracts.AddEvent(
            state, runID, "canvas_updated",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                payload.ToJsonString(), GoJson.ReadOptions)!);
    }

    private static JsonObject ChangeJson((JsonObject? Before, JsonObject After) change) => new()
    {
        ["before"] = change.Before?.DeepClone(),
        ["after"] = change.After.DeepClone(),
    };

    // ------------------------------------------------------------ canvas_apply_ops

    /// <summary>画布操作参数。对应 Go: <c>agentCanvasArgs/agentCanvasOp</c>。</summary>
    public sealed class CanvasArgs
    {
        public string SnapshotHash { get; set; } = "";
        public List<CanvasOp> Ops { get; set; } = [];
    }

    public sealed class CanvasOp
    {
        public string Type { get; set; } = "";
        public string ID { get; set; } = "";
        public string NodeType { get; set; } = "";
        public string? Title { get; set; }
        public string? Content { get; set; }
        public Dictionary<string, JsonElement> Patch { get; set; } = new(StringComparer.Ordinal);
        public double X { get; set; }
        public double Y { get; set; }
        public string FromNodeID { get; set; } = "";
        public string ToNodeID { get; set; } = "";
    }

    /// <summary>新增节点形态。对应 Go: <c>creationAddedNode</c>。</summary>
    public static JsonObject AddedNode(string id, string nodeType, string title, double x, double y, JsonObject? metadata)
    {
        (CapabilityDescriptor descriptor, bool known) = CloudAgentNodes.ForType(nodeType);
        double width = 340, height = 240;
        string resolvedTitle = "Note";
        if (known)
        {
            width = descriptor.DefaultWidth;
            height = descriptor.DefaultHeight;
            resolvedTitle = descriptor.Label;
        }
        JsonObject resolvedMetadata = known
            ? descriptor.Metadata("") ?? new JsonObject()
            : new JsonObject { ["content"] = "", ["status"] = "idle" };
        if (title.Length > 0)
        {
            resolvedTitle = title;
        }
        return new JsonObject
        {
            ["id"] = id,
            ["type"] = nodeType,
            ["title"] = resolvedTitle,
            ["position"] = new JsonObject { ["x"] = x, ["y"] = y },
            ["width"] = width,
            ["height"] = height,
            ["metadata"] = MergeMaps(resolvedMetadata, metadata),
        };
    }

    private static JsonObject MergeMaps(JsonObject left, JsonObject? right)
    {
        JsonObject merged = new();
        foreach ((string key, JsonNode? value) in left)
        {
            merged[key] = value?.DeepClone();
        }
        if (right is not null)
        {
            foreach ((string key, JsonNode? value) in right)
            {
                merged[key] = value?.DeepClone();
            }
        }
        return merged;
    }

    /// <summary>连线校验。对应 Go: <c>validateCloudAgentConnection</c>。</summary>
    public static void ValidateConnection(
        List<JsonObject> nodes, string fromID, string toID, List<JsonObject>? existingConnections = null)
    {
        CloudAgentContracts.ValidateCloudAgentID(fromID, "来源节点 ID", 80);
        CloudAgentContracts.ValidateCloudAgentID(toID, "目标节点 ID", 80);
        if (fromID == toID)
        {
            throw AppError.BadAuthRequest("连线不能指向自身");
        }
        JsonObject? from = null;
        JsonObject? to = null;
        foreach (JsonObject node in nodes)
        {
            string id = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "id"));
            if (id == fromID)
            {
                from = node;
            }
            if (id == toID)
            {
                to = node;
            }
        }
        if (from is null || to is null)
        {
            throw AppError.BadAuthRequest("连线端点不存在");
        }
        (CapabilityDescriptor fromCapability, bool fromKnown) =
            CloudAgentNodes.ForType(CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(from, "type")));
        (CapabilityDescriptor toCapability, bool toKnown) =
            CloudAgentNodes.ForType(CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(to, "type")));
        if (!fromKnown || !toKnown)
        {
            throw AppError.BadAuthRequest("连线包含当前 Agent 不支持的节点类型");
        }
        string fromKind = fromCapability.InputKind;
        if (fromKind.Length == 0 || !fromCapability.Connection.CanSource)
        {
            throw AppError.BadAuthRequest("来源节点不能作为参考输入");
        }
        if (!toCapability.Connection.CanTarget)
        {
            throw AppError.BadAuthRequest("目标节点不能接收参考输入");
        }
        List<JsonObject> connections = existingConnections ?? [];
        foreach (JsonObject edge in connections)
        {
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "toNodeId")) == toID
                && CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "fromNodeId")) == fromID)
            {
                throw AppError.BadAuthRequest("连线重复");
            }
        }
        try
        {
            toCapability.ValidateConnection(fromKind);
        }
        catch (ArgumentException cause)
        {
            throw AppError.BadAuthRequest(cause.Message);
        }
        int maxInputs = toCapability.Connection.MaxInputCount;
        if (maxInputs > 0)
        {
            HashSet<string> inputIDs = new(StringComparer.Ordinal) { fromID };
            foreach (JsonObject edge in connections)
            {
                if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "toNodeId")) == toID)
                {
                    inputIDs.Add(CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "fromNodeId")));
                }
            }
            if (inputIDs.Count > maxInputs)
            {
                throw AppError.BadAuthRequest($"{toCapability.Label}最多连接 {maxInputs} 个输入");
            }
        }
    }

    /// <summary>审批节点标题。对应 Go: <c>cloudAgentApprovalNodeTitle</c>。</summary>
    public static string ApprovalNodeTitle(JsonObject node, string typeLabel)
    {
        string title = CloudAgentContracts.TruncateRunes(
            CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "title")).Trim(), 120);
        return title.Length > 0 ? title : "未命名" + typeLabel;
    }

    /// <summary>patch 字段标签（按展示序）。对应 Go: <c>cloudAgentApprovalPatchLabels</c>。</summary>
    public static List<string> ApprovalPatchLabels(
        IReadOnlyDictionary<string, PatchField> fields, Dictionary<string, JsonElement> patch) =>
        [.. patch.Keys
            .OrderBy(k => fields.TryGetValue(k, out PatchField? f) ? f.Order : int.MaxValue)
            .ThenBy(k => k, StringComparer.Ordinal)
            .Where(k => fields.ContainsKey(k))
            .Select(k => fields[k].Label)];

    /// <summary>审批调用哈希。对应 Go: <c>cloudAgentApprovalCallHash</c>。</summary>
    public static string ApprovalCallHash(CloudAgentCallDto call) =>
        CloudAgentContracts.Sha256Hex(JsonSerializer.Serialize(call, GoJson.WriteOptions));

    /// <summary>
    /// 干跑与执行共用的画布操作规划器。对应 Go: <c>prepareCloudAgentCanvasMutation</c>。
    /// 返回（画布, 文档, 变更前 JSON, 变更前哈希, 预览, 参数）。
    /// </summary>
    public static async Task<(CanvasProject Canvas, JsonObject Document, string BeforeJSON, string BeforeHash, CloudAgentApprovalPreviewDto Preview, CanvasArgs Args)>
        PrepareCanvasMutationAsync(
            CloudAgentMutationContext context, string userID, string canvasID, CloudAgentCallDto call)
    {
        CanvasArgs args;
        try
        {
            args = CloudAgentContracts.DecodeObject<CanvasArgs>(call.Function.Arguments);
        }
        catch (CloudAgentArgumentException)
        {
            throw CloudAgentJson.CanvasArgumentError();
        }
        catch (AppError)
        {
            throw CloudAgentJson.CanvasArgumentError();
        }
        if (args.Ops.Count is < 1 or > 20 || args.SnapshotHash.Length == 0)
        {
            throw AppError.BadAuthRequest("画布操作数量或快照无效");
        }
        CanvasProject? canvas = await context.CanvasProjectForUserAsync(userID, canvasID).ConfigureAwait(false)
            ?? throw AppError.NotFound("画布不存在");
        JsonObject doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
        string beforeHash = CloudAgentContracts.CanvasHash(doc);
        if (beforeHash != args.SnapshotHash)
        {
            throw CloudAgentSessionService.CreationConflict("画布已变化，本次未写入；请重新读取并重新申请审批");
        }
        List<CloudAgentApprovalPreviewItemDto> items = ApplyCanvasPlan(doc, args.Ops);
        return (canvas, doc, canvas.PayloadJSON, beforeHash, CanvasApprovalPreview(items), args);
    }

    /// <summary>操作计划应用。对应 Go: <c>applyCloudAgentCanvasPlan</c>。</summary>
    public static List<CloudAgentApprovalPreviewItemDto> ApplyCanvasPlan(
        JsonObject doc, IReadOnlyList<CanvasOp> ops)
    {
        List<JsonObject> nodes = CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "nodes"));
        List<JsonObject> edges = CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "connections"));
        List<CloudAgentApprovalPreviewItemDto> items = new(ops.Count);
        foreach (CanvasOp op in ops)
        {
            string title = op.Title ?? "";
            string content = op.Content ?? "";
            CloudAgentContracts.ValidateCloudAgentID(op.ID, "节点或连线 ID", 80);
            if (op.Type == "add_node"
                && (content.EnumerateRunes().Count() > 16000 || title.EnumerateRunes().Count() > 240))
            {
                throw AppError.BadAuthRequest("节点标题或正文超出限制");
            }
            int index = NodeIndex(nodes, op.ID);
            switch (op.Type)
            {
                case "add_node":
                {
                    if (index >= 0)
                    {
                        throw AppError.BadAuthRequest("新增节点ID重复");
                    }
                    if (op.NodeType.Trim().Length == 0)
                    {
                        throw AppError.BadAuthRequest("新增节点缺少 nodeType");
                    }
                    (CapabilityDescriptor capability, bool known) = CloudAgentNodes.ForType(op.NodeType);
                    if (!known)
                    {
                        throw AppError.BadAuthRequest("不支持的节点类型");
                    }
                    JsonObject node = AddedNode(
                        op.ID, op.NodeType, title, op.X, op.Y, capability.Metadata(content));
                    nodes.Add(node);
                    string nodeTitle = ApprovalNodeTitle(node, capability.Label);
                    items.Add(new CloudAgentApprovalPreviewItemDto
                    {
                        Operation = "add_node",
                        NodeID = op.ID,
                        NodeTitle = nodeTitle,
                        NodeType = capability.Type,
                        NodeTypeLabel = capability.Label,
                        Summary = $"新增{capability.Label}《{nodeTitle}》",
                    });
                    break;
                }
                case "connect_nodes":
                {
                    CloudAgentContracts.ValidateCloudAgentID(op.FromNodeID, "来源节点 ID", 80);
                    CloudAgentContracts.ValidateCloudAgentID(op.ToNodeID, "目标节点 ID", 80);
                    int fromIndex = NodeIndex(nodes, op.FromNodeID);
                    int toIndex = NodeIndex(nodes, op.ToNodeID);
                    if (fromIndex < 0 || toIndex < 0 || op.FromNodeID == op.ToNodeID)
                    {
                        throw AppError.BadAuthRequest("连线端点不存在或指向自身");
                    }
                    ValidateConnection(nodes, op.FromNodeID, op.ToNodeID, edges);
                    foreach (JsonObject edge in edges)
                    {
                        if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "id")) == op.ID
                            || (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "fromNodeId")) == op.FromNodeID
                                && CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "toNodeId")) == op.ToNodeID))
                        {
                            throw AppError.BadAuthRequest("连线重复");
                        }
                    }
                    (CapabilityDescriptor fromCapability, _) =
                        CloudAgentNodes.ForType(CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(nodes[fromIndex], "type")));
                    (CapabilityDescriptor toCapability, _) =
                        CloudAgentNodes.ForType(CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(nodes[toIndex], "type")));
                    string fromTitle = ApprovalNodeTitle(nodes[fromIndex], fromCapability.Label);
                    string toTitle = ApprovalNodeTitle(nodes[toIndex], toCapability.Label);
                    edges.Add(new JsonObject
                    {
                        ["id"] = op.ID,
                        ["fromNodeId"] = op.FromNodeID,
                        ["toNodeId"] = op.ToNodeID,
                    });
                    items.Add(new CloudAgentApprovalPreviewItemDto
                    {
                        Operation = "connect_nodes",
                        NodeID = op.FromNodeID,
                        NodeTitle = fromTitle,
                        NodeType = fromCapability.Type,
                        NodeTypeLabel = fromCapability.Label,
                        TargetNodeID = op.ToNodeID,
                        TargetNodeTitle = toTitle,
                        TargetNodeType = toCapability.Type,
                        Summary = $"建立《{fromTitle}》→《{toTitle}》的引用连线",
                    });
                    break;
                }
                case "update_node":
                {
                    if (op.Patch.Count == 0)
                    {
                        throw AppError.BadAuthRequest("更新节点必须提供 patch");
                    }
                    if (index < 0)
                    {
                        throw AppError.BadAuthRequest("只能更新现有且受 Agent 支持的节点");
                    }
                    (CapabilityDescriptor capability, bool known) =
                        CloudAgentNodes.ForType(CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(nodes[index], "type")));
                    if (!known || !capability.CanUpdate)
                    {
                        throw AppError.BadAuthRequest("该节点类型不支持 Agent 更新");
                    }
                    JsonObject meta = nodes[index]["metadata"] as JsonObject ?? new JsonObject();
                    if (CloudAgentJsonHelpers.Get(meta, "locked") is JsonValue lockedValue
                        && lockedValue.TryGetValue<bool>(out bool locked) && locked)
                    {
                        throw AppError.BadAuthRequest("不能修改锁定节点");
                    }
                    string beforeTitle = ApprovalNodeTitle(nodes[index], capability.Label);
                    List<string> fields = ApprovalPatchLabels(capability.PatchFields, op.Patch);
                    JsonObject patchObject = new();
                    foreach ((string key, JsonElement value) in op.Patch)
                    {
                        patchObject[key] = JsonSerializer.SerializeToNode(value);
                    }
                    try
                    {
                        capability.ApplyPatch(nodes[index], patchObject);
                    }
                    catch (ArgumentException cause)
                    {
                        throw AppError.BadAuthRequest(cause.Message);
                    }
                    string afterTitle = ApprovalNodeTitle(nodes[index], capability.Label);
                    items.Add(new CloudAgentApprovalPreviewItemDto
                    {
                        Operation = "update_node",
                        NodeID = op.ID,
                        NodeTitle = beforeTitle,
                        ResultTitle = afterTitle != beforeTitle ? afterTitle : "",
                        NodeType = capability.Type,
                        NodeTypeLabel = capability.Label,
                        Fields = fields,
                        Summary = $"修改{capability.Label}《{beforeTitle}》的{string.Join("、", fields)}",
                    });
                    break;
                }
                default:
                    throw AppError.BadAuthRequest("不支持的画布写操作");
            }
        }
        doc["nodes"] = new JsonArray(nodes.Select(n => (JsonNode?)n.DeepClone()).ToArray());
        doc["connections"] = new JsonArray(edges.Select(e => (JsonNode?)e.DeepClone()).ToArray());
        return items;
    }

    private static int NodeIndex(List<JsonObject> nodes, string id)
    {
        for (int index = 0; index < nodes.Count; index++)
        {
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(nodes[index], "id")) == id)
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>画布操作审批预览。对应 Go: <c>cloudAgentCanvasApprovalPreview</c>。</summary>
    public static CloudAgentApprovalPreviewDto CanvasApprovalPreview(
        List<CloudAgentApprovalPreviewItemDto> items)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (CloudAgentApprovalPreviewItemDto item in items)
        {
            counts[item.Operation] = counts.TryGetValue(item.Operation, out int count) ? count + 1 : 1;
        }
        List<string> parts = [];
        if (counts.TryGetValue("add_node", out int addCount) && addCount > 0)
        {
            parts.Add($"新增 {addCount} 个节点");
        }
        if (counts.TryGetValue("update_node", out int updateCount) && updateCount > 0)
        {
            parts.Add($"修改 {updateCount} 个节点");
        }
        if (counts.TryGetValue("connect_nodes", out int connectCount) && connectCount > 0)
        {
            parts.Add($"建立 {connectCount} 条引用连线");
        }
        return new CloudAgentApprovalPreviewDto
        {
            Kind = "canvas_mutation",
            Title = "确认画布修改",
            Description = $"Agent 准备{string.Join("，", parts)}。请确认目标节点和修改字段；批准后才会写入画布。",
            Items = items,
        };
    }

    /// <summary>事务内保存画布文档。对应 Go: <c>saveCloudAgentDocument</c>。</summary>
    public static async Task SaveDocumentAsync(
        CloudAgentMutationContext context, CanvasProject canvas, JsonObject doc,
        RuntimePolicySetting policy)
    {
        doc["updatedAt"] = Rfc3339Nano(DateTime.UtcNow);
        string raw = CloudAgentContracts.CanonicalJson(doc);
        if (raw.Length > 8 << 20)
        {
            throw AppError.BadAuthRequest("画布大小超限");
        }
        UserStorageUsage usage = await context.UserStorageUsageAsync(canvas.UserID).ConfigureAwait(false);
        long structuredBytes = usage.AssetBytes + usage.CanvasBytes;
        long limitMB = policy.Resource.StructuredDataMB;
        if (structuredBytes + (raw.Length - canvas.PayloadJSON.Length) > limitMB * 1024L * 1024L)
        {
            throw AppError.QuotaExceeded($"账号画布和素材数据已达到 {limitMB}MB 上限，请先删除不需要的内容");
        }
        string before = canvas.PayloadJSON;
        canvas.PayloadJSON = raw;
        await context.CompareSaveCreationCanvasAsync(canvas, before).ConfigureAwait(false);
    }
}
