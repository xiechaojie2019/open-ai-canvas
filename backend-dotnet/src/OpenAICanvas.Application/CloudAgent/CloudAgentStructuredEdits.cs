#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAICanvas.Domain.Canvas.Capability;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>
/// 结构化节点编辑：分镜脚本创建/编辑与批量创作表编辑的干跑规划与执行。
/// 对应 Go: <c>app/cloud_agent_storyboard.go</c>、<c>app/cloud_agent_batch_table.go</c>。
/// </summary>
public static class CloudAgentStructuredEdits
{
    public const int MaxStoryboardRows = CloudAgentTools.MaxStoryboardRows;
    public const int MaxBatchRows = 500;
    public const int MaxBatchReferences = 6;
    public const int MaxBatchPromptRunes = 20000;

    public const string TryOnBatchPrompt =
        "参考图1是人物原图，参考图2是目标服装。保持人物身份、五官、姿态和背景不变，将人物服装替换为参考图2中的款式。准确还原服装版型、颜色、材质、纹理和装饰细节，穿着关系自然，光影与原图一致。";
    public const string CreativeBatchPrompt =
        "基于参考图创作一张新的商业图片，保留主体身份和关键产品细节，画面构图完整，光影自然。";

    // ------------------------------------------------------------ 分镜脚本

    public sealed class StoryboardCreateArgs
    {
        public string SnapshotHash { get; set; } = "";
        public string NodeID { get; set; } = "";
        public string Title { get; set; } = "";
        public List<JsonObject> Rows { get; set; } = [];
        public double X { get; set; }
        public double Y { get; set; }
    }

    public sealed class StoryboardEditArgs
    {
        public string SnapshotHash { get; set; } = "";
        public string NodeID { get; set; } = "";
        public string Action { get; set; } = "";
        public string RowID { get; set; } = "";
        public Dictionary<string, JsonElement> Patch { get; set; } = new(StringComparer.Ordinal);
    }

    private static void ValidateRow(JsonObject row, bool requireDescription)
    {
        JsonNode? duration = CloudAgentJsonHelpers.Get(row, "durationSeconds");
        if (duration is null)
        {
            throw AppError.BadAuthRequest("每个分镜行必须有 durationSeconds");
        }
        if (duration is not JsonValue || !duration.GetValueKind().Equals(JsonValueKind.Number)
            || duration.GetValue<double>() <= 0)
        {
            throw AppError.BadAuthRequest("分镜时长必须是大于零的数字");
        }
        foreach (string key in row.Select(p => p.Key))
        {
            if (key != "durationSeconds" && !CloudAgentTools.IsStoryboardTextField(key))
            {
                throw AppError.BadAuthRequest($"不能通过分镜工具写入字段 {key}");
            }
        }
        foreach (string key in CloudAgentTools.StoryboardTextFields)
        {
            if (CloudAgentJsonHelpers.Get(row, key) is JsonValue textValue
                && textValue.TryGetValue<string>(out string? text))
            {
                if (text.EnumerateRunes().Count() > 20000)
                {
                    throw AppError.BadAuthRequest($"分镜字段 {key} 必须是不超过20000字的文本");
                }
            }
            else if (CloudAgentJsonHelpers.Get(row, key) is not null)
            {
                throw AppError.BadAuthRequest($"分镜字段 {key} 必须是不超过20000字的文本");
            }
        }
        if (requireDescription)
        {
            string plot = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(row, "plotDescription")).Trim();
            string motion = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(row, "videoMotionPrompt")).Trim();
            if (plot.Length == 0 && motion.Length == 0)
            {
                throw AppError.BadAuthRequest("新增镜头需要画面描述或视频提示词");
            }
        }
    }

    private static List<JsonNode?> NormalizeRows(
        List<JsonObject> rows, string userID, string nodeID, string seed)
    {
        if (rows.Count is < 1 or > MaxStoryboardRows)
        {
            throw AppError.BadAuthRequest("分镜脚本必须包含 1 到 100 个镜头");
        }
        List<JsonNode?> output = new(rows.Count);
        for (int index = 0; index < rows.Count; index++)
        {
            JsonObject input = rows[index];
            ValidateRow(input, requireDescription: true);
            JsonObject row = RowDefaults();
            foreach ((string key, JsonNode? value) in input)
            {
                row[key] = value?.DeepClone();
            }
            row["id"] = CloudAgentContracts.AgentID(userID, $"storyboard:{nodeID}:{seed}:{index + 1}");
            row["shotNumber"] = (double)(index + 1);
            output.Add(row.DeepClone());
        }
        return output;
    }

    /// <summary>分镜行默认字段。对应 Go: <c>cloudAgentStoryboardRowDefaults</c>。</summary>
    public static JsonObject RowDefaults()
    {
        JsonObject row = new()
        {
            ["characters"] = new JsonArray(),
            ["mustHave"] = new JsonArray(),
            ["optionalDetails"] = new JsonArray(),
            ["assetBindings"] = new JsonArray(),
            ["status"] = "idle",
        };
        foreach (string field in CloudAgentTools.StoryboardTextFields)
        {
            row[field] = "";
        }
        return row;
    }

    /// <summary>定位分镜节点。对应 Go: <c>storyboardNodeFromDocument</c>。</summary>
    public static (JsonObject Node, JsonObject Storyboard, List<JsonObject> Rows) StoryboardNode(
        JsonObject doc, string nodeID)
    {
        foreach (JsonObject node in CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "nodes")))
        {
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "id")) != nodeID)
            {
                continue;
            }
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "type")) != "script")
            {
                throw AppError.BadAuthRequest("目标节点不是分镜脚本节点");
            }
            if (node["metadata"] is not JsonObject metadata)
            {
                throw AppError.BadAuthRequest("分镜节点数据格式无效");
            }
            if (CloudAgentJsonHelpers.Get(metadata, "storyboard") is not JsonObject storyboard)
            {
                throw AppError.BadAuthRequest("分镜节点缺少结构化表格");
            }
            List<JsonObject> rows = CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(storyboard, "rows"));
            if (rows.Count > MaxStoryboardRows)
            {
                throw AppError.BadAuthRequest("分镜脚本超过100个镜头限制");
            }
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (JsonObject row in rows)
            {
                string rowID = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(row, "id"));
                CloudAgentContracts.ValidateCloudAgentID(rowID, "分镜行ID", 120);
                if (!ids.Add(rowID))
                {
                    throw AppError.BadAuthRequest("分镜节点包含无效或重复的镜头行ID");
                }
            }
            return (node, storyboard, rows);
        }
        throw AppError.BadAuthRequest("未找到当前画布的分镜脚本节点");
    }

    private static string StoryboardFieldText(JsonObject storyboard, string key) =>
        CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(storyboard, key));

    /// <summary>按工具名分发分镜创建/编辑计划。</summary>
    public static Task<(CanvasProject Canvas, JsonObject Document, string BeforeJSON, string BeforeHash, CloudAgentApprovalPreviewDto Preview)>
        PrepareStoryboardAsync(
            CloudAgentMutationContext context, string userID, string canvasID,
            CloudAgentCallDto call, string toolName) =>
        toolName == "canvas_create_storyboard"
            ? PrepareStoryboardCreateAsync(context, userID, canvasID, call)
            : PrepareStoryboardEditAsync(context, userID, canvasID, call);

    /// <summary>批量创作表编辑计划别名。</summary>
    public static Task<(CanvasProject Canvas, JsonObject Document, string BeforeJSON, string BeforeHash, CloudAgentApprovalPreviewDto Preview)>
        PrepareBatchTableAsync(
            CloudAgentMutationContext context, string userID, string canvasID, CloudAgentCallDto call) =>
        PrepareBatchTableEditAsync(context, userID, canvasID, call);

    /// <summary>创建分镜计划。对应 Go: <c>prepareCloudAgentStoryboardCreate</c>。</summary>
    public static async Task<(CanvasProject Canvas, JsonObject Document, string BeforeJSON, string BeforeHash, CloudAgentApprovalPreviewDto Preview)>
        PrepareStoryboardCreateAsync(
            CloudAgentMutationContext context, string userID, string canvasID, CloudAgentCallDto call)
    {
        StoryboardCreateArgs args;
        try
        {
            args = CloudAgentContracts.DecodeObject<StoryboardCreateArgs>(call.Function.Arguments);
        }
        catch (CloudAgentArgumentException)
        {
            throw CloudAgentJson.StoryboardArgumentError("创建");
        }
        catch (AppError)
        {
            throw CloudAgentJson.StoryboardArgumentError("创建");
        }
        if (args.SnapshotHash.Length == 0 || args.NodeID.Length == 0 || args.Title.Trim().Length == 0)
        {
            throw AppError.BadAuthRequest("创建分镜脚本需要快照、节点ID和标题");
        }
        CloudAgentContracts.ValidateCloudAgentID(args.NodeID, "分镜节点ID", 80);
        if (args.Title.EnumerateRunes().Count() > 240)
        {
            throw AppError.BadAuthRequest("分镜标题超出限制");
        }
        CanvasProject? canvas = await context.CanvasProjectForUserAsync(userID, canvasID).ConfigureAwait(false)
            ?? throw AppError.NotFound("画布不存在");
        JsonObject doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
        string beforeHash = CloudAgentContracts.CanvasHash(doc);
        if (beforeHash != args.SnapshotHash)
        {
            throw CloudAgentSessionService.CreationConflict("画布已变化，本次未写入；请重新读取并重新申请审批");
        }
        foreach (JsonObject existingNode in CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "nodes")))
        {
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(existingNode, "id")) == args.NodeID)
            {
                throw AppError.BadAuthRequest("分镜节点ID已存在");
            }
        }
        List<JsonNode?> rows = NormalizeRows(args.Rows, userID, args.NodeID, call.ID);
        JsonObject storyboardMetadata = new()
        {
            ["rows"] = new JsonArray(rows.Where(r => r is not null).Select(r => r!.DeepClone()).ToArray()),
            ["visibleColumns"] = new JsonArray("shotNumber", "durationSeconds", "videoMotionPrompt", "dialogue", "assets"),
            ["referenceNodeIds"] = new JsonArray(),
        };
        JsonObject metadata = new() { ["storyboard"] = storyboardMetadata };
        JsonObject node = CloudAgentMutations.AddedNode(args.NodeID, "script", args.Title, args.X, args.Y, metadata);
        List<JsonObject> nodes = CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "nodes"));
        nodes.Add(node);
        doc["nodes"] = new JsonArray(nodes.Select(n => (JsonNode?)n.DeepClone()).ToArray());
        CloudAgentApprovalPreviewDto preview = new()
        {
            Kind = "canvas_mutation",
            Title = "确认创建分镜脚本",
            Description = $"Agent 准备创建分镜脚本《{args.Title}》，包含 {rows.Count} 个镜头。批准后才会写入画布。",
            Items =
            [
                new CloudAgentApprovalPreviewItemDto
                {
                    Operation = "create_storyboard",
                    NodeID = args.NodeID,
                    NodeTitle = args.Title,
                    NodeType = "script",
                    NodeTypeLabel = "分镜脚本",
                    Summary = $"创建分镜脚本《{args.Title}》（{rows.Count}个镜头）",
                },
            ],
        };
        return (canvas, doc, canvas.PayloadJSON, beforeHash, preview);
    }

    /// <summary>编辑分镜计划。对应 Go: <c>prepareCloudAgentStoryboardEdit</c>。</summary>
    public static async Task<(CanvasProject Canvas, JsonObject Document, string BeforeJSON, string BeforeHash, CloudAgentApprovalPreviewDto Preview)>
        PrepareStoryboardEditAsync(
            CloudAgentMutationContext context, string userID, string canvasID, CloudAgentCallDto call)
    {
        StoryboardEditArgs args;
        try
        {
            args = CloudAgentContracts.DecodeObject<StoryboardEditArgs>(call.Function.Arguments);
        }
        catch (CloudAgentArgumentException)
        {
            throw CloudAgentJson.StoryboardArgumentError("编辑");
        }
        catch (AppError)
        {
            throw CloudAgentJson.StoryboardArgumentError("编辑");
        }
        if (args.SnapshotHash.Length == 0 || args.NodeID.Length == 0)
        {
            throw AppError.BadAuthRequest("编辑分镜需要快照和节点ID");
        }
        if (args.Action is not ("append" or "update" or "remove"))
        {
            throw AppError.BadAuthRequest("分镜操作必须是 append、update 或 remove");
        }
        CloudAgentContracts.ValidateCloudAgentID(args.NodeID, "分镜节点ID", 80);
        if (args.Action == "append" && args.RowID.Length > 0)
        {
            throw AppError.BadAuthRequest("追加镜头不能指定已有 rowId");
        }
        if (args.Action != "append")
        {
            try
            {
                CloudAgentContracts.ValidateCloudAgentID(args.RowID, "分镜行ID", 120);
            }
            catch (AppError)
            {
                throw AppError.BadAuthRequest("修改或删除镜头必须使用最新读取结果中的真实 rowId");
            }
        }
        if (args.Action == "remove" && args.Patch.Count != 0)
        {
            throw AppError.BadAuthRequest("删除镜头不接受 patch");
        }
        CanvasProject? canvas = await context.CanvasProjectForUserAsync(userID, canvasID).ConfigureAwait(false)
            ?? throw AppError.NotFound("画布不存在");
        JsonObject doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
        string beforeHash = CloudAgentContracts.CanvasHash(doc);
        if (beforeHash != args.SnapshotHash)
        {
            throw CloudAgentSessionService.CreationConflict("画布已变化，本次未写入；请重新读取并重新申请审批");
        }
        (JsonObject storyboardNode, JsonObject storyboard, List<JsonObject> rows) =
            StoryboardNode(doc, args.NodeID);
        int index = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(rows[i], "id")) == args.RowID)
            {
                index = i;
                break;
            }
        }
        if (args.Action != "append" && index < 0)
        {
            throw AppError.BadAuthRequest("分镜行不存在，请先读取真实行ID");
        }
        List<JsonObject> next = [.. rows];
        List<string> fields = [];
        switch (args.Action)
        {
            case "remove":
                next.RemoveAt(index);
                fields = ["镜头行"];
                break;
            default:
            {
                if (args.Patch.Count == 0)
                {
                    throw AppError.BadAuthRequest("分镜操作需要 patch");
                }
                JsonObject patch = new();
                foreach ((string key, JsonElement value) in args.Patch)
                {
                    if (key == "durationSeconds")
                    {
                        if (value.ValueKind != JsonValueKind.Number || value.GetDouble() <= 0)
                        {
                            throw AppError.BadAuthRequest("分镜时长必须是大于零的数字");
                        }
                    }
                    else if (CloudAgentTools.IsStoryboardTextField(key))
                    {
                        if (value.ValueKind != JsonValueKind.String
                            || (value.GetString() ?? "").EnumerateRunes().Count() > 20000)
                        {
                            throw AppError.BadAuthRequest($"分镜字段 {key} 必须是不超过20000字的文本");
                        }
                    }
                    else
                    {
                        throw AppError.BadAuthRequest($"不能通过分镜编辑修改字段 {key}");
                    }
                    patch[key] = JsonSerializer.SerializeToNode(value);
                    fields.Add(key);
                }
                fields.Sort(StringComparer.Ordinal);
                if (args.Action == "append")
                {
                    if (rows.Count >= MaxStoryboardRows)
                    {
                        throw AppError.BadAuthRequest("单个分镜表最多100个镜头");
                    }
                    ValidateRow(patch, requireDescription: true);
                    JsonObject row = RowDefaults();
                    foreach ((string key, JsonNode? value) in patch)
                    {
                        row[key] = value?.DeepClone();
                    }
                    row["id"] = CloudAgentContracts.AgentID(
                        userID, $"storyboard:{args.NodeID}:{call.ID}:{rows.Count + 1}");
                    row["shotNumber"] = (double)(rows.Count + 1);
                    next.Add(row);
                }
                else
                {
                    foreach ((string key, JsonNode? value) in patch)
                    {
                        next[index][key] = value?.DeepClone();
                    }
                }
                break;
            }
        }
        for (int i = 0; i < next.Count; i++)
        {
            next[i]["shotNumber"] = (double)(i + 1);
        }
        storyboard["rows"] = new JsonArray(next.Select(r => (JsonNode?)r.DeepClone()).ToArray());
        (storyboardNode["metadata"] as JsonObject)!["storyboard"] = storyboard;
        string nodeTitle = StoryboardFieldText(storyboardNode, "title");
        string verb = args.Action switch
        {
            "append" => "追加镜头到",
            "update" => "修改",
            _ => "删除",
        };
        CloudAgentApprovalPreviewDto preview = new()
        {
            Kind = "canvas_mutation",
            Title = "确认修改分镜脚本",
            Description = "Agent 准备修改分镜脚本。批准后才会写入画布。",
            Items =
            [
                new CloudAgentApprovalPreviewItemDto
                {
                    Operation = "edit_storyboard",
                    NodeID = args.NodeID,
                    NodeTitle = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(storyboardNode, "title")),
                    NodeType = "script",
                    NodeTypeLabel = "分镜脚本",
                    Fields = fields,
                    Summary = $"{verb}分镜脚本《{nodeTitle}》",
                },
            ],
        };
        return (canvas, doc, canvas.PayloadJSON, beforeHash, preview);
    }

    /// <summary>分镜变更执行。对应 Go: <c>applyCloudAgentStoryboardMutation</c>。</summary>
    public static async Task<JsonObject> ApplyStoryboardMutationAsync(
        CloudAgentMutationContext context, string userID, string canvasID, CloudAgentCallDto call,
        RuntimePolicySetting policy, CloudAgentMutationRecorder? recorder)
    {
        (CanvasProject canvas, JsonObject doc, string beforeJSON, string beforeHash, CloudAgentApprovalPreviewDto preview) =
            call.Function.Name == "canvas_create_storyboard"
                ? await PrepareStoryboardCreateAsync(context, userID, canvasID, call).ConfigureAwait(false)
                : await PrepareStoryboardEditAsync(context, userID, canvasID, call).ConfigureAwait(false);
        await CloudAgentMutations.SaveDocumentAsync(context, canvas, doc, policy).ConfigureAwait(false);
        if (recorder is not null)
        {
            await recorder(context, new CloudAgentMutationInput
            {
                UserID = userID,
                CanvasID = canvasID,
                StepID = call.ID,
                Operation = call.Function.Name,
                BeforeSnapshotHash = beforeHash,
                AfterSnapshotHash = CloudAgentContracts.CanvasHash(doc),
                BeforeJSON = beforeJSON,
                Preview = preview,
            }).ConfigureAwait(false);
        }
        string nodeID = preview.Items.Count > 0 ? preview.Items[0].NodeID : "";
        return new JsonObject
        {
            ["canvasId"] = canvasID,
            ["nodeId"] = nodeID,
            ["snapshotHash"] = CloudAgentContracts.CanvasHash(doc),
            ["summary"] = preview.Description,
            ["preview"] = JsonSerializer.SerializeToNode(preview, OpenAICanvas.Domain.Serialization.GoJson.WriteOptions),
        };
    }

    // ------------------------------------------------------------ 批量创作表

    public sealed class BatchTableEditArgs
    {
        public string SnapshotHash { get; set; } = "";
        public string NodeID { get; set; } = "";
        public string Action { get; set; } = "";
        public string RowID { get; set; } = "";
        public Dictionary<string, JsonElement> Patch { get; set; } = new(StringComparer.Ordinal);
        public string Operation { get; set; } = "";
        public int Concurrency { get; set; }
    }

    private static bool ConcurrencyAllowed(int value) => value is 1 or 5 or 10;

    /// <summary>定位批量创作表节点并整体校验。对应 Go: <c>batchTableNodeFromDocument</c>。</summary>
    public static (JsonObject Node, JsonObject Table, List<JsonObject> Rows, List<JsonObject> Columns) BatchTableNode(
        JsonObject doc, string nodeID)
    {
        foreach (JsonObject node in CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "nodes")))
        {
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "id")) != nodeID)
            {
                continue;
            }
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "type")) != "batch-table")
            {
                throw AppError.BadAuthRequest("目标节点不是批量创作表节点");
            }
            if (node["metadata"] is not JsonObject metadata)
            {
                throw AppError.BadAuthRequest("批量创作表节点数据格式无效");
            }
            if (CloudAgentJsonHelpers.Get(metadata, "batchTable") is not JsonObject table)
            {
                throw AppError.BadAuthRequest("批量创作表节点缺少结构化数据");
            }
            string operation = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(table, "operation"));
            if (operation is not ("try_on" or "creative"))
            {
                throw AppError.BadAuthRequest("批量创作表包含无效的任务类型");
            }
            int? concurrency = IntegerValue(CloudAgentJsonHelpers.Get(table, "concurrency"));
            if (concurrency is null || !ConcurrencyAllowed(concurrency.Value))
            {
                throw AppError.BadAuthRequest("批量创作表包含无效的并发数");
            }
            List<JsonObject> columns = CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(table, "referenceColumns"));
            if (columns.Count is < 1 or > MaxBatchReferences)
            {
                throw AppError.BadAuthRequest("批量创作表必须包含 1 到 6 个参考图列");
            }
            HashSet<string> columnIDs = new(StringComparer.Ordinal);
            foreach (JsonObject column in columns)
            {
                string id = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(column, "id"));
                string label = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(column, "label"));
                string? idError = null;
                try
                {
                    CloudAgentContracts.ValidateCloudAgentID(id, "参考图列ID", 120);
                }
                catch (AppError error)
                {
                    idError = error.Message;
                }
                if (idError is not null || label.Trim().Length == 0
                    || label.EnumerateRunes().Count() > 120 || !columnIDs.Add(id))
                {
                    throw AppError.BadAuthRequest("批量创作表包含无效或重复的参考图列");
                }
            }
            List<JsonObject> rows = CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(table, "rows"));
            if (rows.Count > MaxBatchRows)
            {
                throw AppError.BadAuthRequest("单个批量创作表最多500行");
            }
            HashSet<string> rowIDs = new(StringComparer.Ordinal);
            foreach (JsonObject row in rows)
            {
                string rowID = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(row, "id"));
                string? rowIDError = null;
                try
                {
                    CloudAgentContracts.ValidateCloudAgentID(rowID, "批量创作行ID", 120);
                }
                catch (AppError error)
                {
                    rowIDError = error.Message;
                }
                if (rowIDError is not null || !rowIDs.Add(rowID))
                {
                    throw AppError.BadAuthRequest("批量创作表包含无效或重复的行ID");
                }
                if (CloudAgentJsonHelpers.Get(row, "enabled") is not JsonValue enabledValue
                    || !enabledValue.TryGetValue<bool>(out _))
                {
                    throw AppError.BadAuthRequest("批量创作表包含无效的启用状态");
                }
                JsonNode? promptNode = CloudAgentJsonHelpers.Get(row, "prompt");
                if (promptNode is not JsonValue promptValue || !promptValue.TryGetValue<string>(out string? prompt)
                    || prompt.EnumerateRunes().Count() > MaxBatchPromptRunes)
                {
                    throw AppError.BadAuthRequest("批量创作表包含无效或过长的提示词");
                }
                ValidateBatchInputNodeIDs(CloudAgentJsonHelpers.Get(row, "inputNodeIds"), columns.Count);
                string outputNodeID = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(row, "outputNodeId"));
                if (outputNodeID.Length > 0)
                {
                    CloudAgentContracts.ValidateCloudAgentID(outputNodeID, "输出节点ID", 120);
                }
            }
            return (node, table, rows, columns);
        }
        throw AppError.BadAuthRequest("未找到当前画布的批量创作表节点");
    }

    private static int? IntegerValue(JsonNode? value)
    {
        if (value is JsonValue v && v.TryGetValue<long>(out long number)
            && number is >= int.MinValue and <= int.MaxValue)
        {
            return (int)number;
        }
        return null;
    }

    private static List<string> ValidateBatchInputNodeIDs(JsonNode? value, int maxItems)
    {
        if (value is not JsonArray items)
        {
            throw AppError.BadAuthRequest("批量创作行的 inputNodeIds 必须是图片节点ID数组");
        }
        if (items.Count > maxItems || items.Count > MaxBatchReferences)
        {
            throw AppError.BadAuthRequest("每行参考图数量不能超过当前参考图列数");
        }
        List<string> result = new(items.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonNode? item in items)
        {
            string id = CloudAgentJsonHelpers.StringValue(item);
            string? idError = null;
            try
            {
                CloudAgentContracts.ValidateCloudAgentID(id, "参考图片节点ID", 80);
            }
            catch (AppError error)
            {
                idError = error.Message;
            }
            if (idError is not null || !seen.Add(id))
            {
                throw AppError.BadAuthRequest("批量创作行包含无效或重复的图片节点ID");
            }
            result.Add(id);
        }
        return result;
    }

    private static void ValidateBatchInputNodes(JsonObject doc, List<string> inputNodeIDs)
    {
        Dictionary<string, JsonObject> nodes = new(StringComparer.Ordinal);
        foreach (JsonObject node in CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "nodes")))
        {
            nodes[CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "id"))] = node;
        }
        foreach (string id in inputNodeIDs)
        {
            if (!nodes.TryGetValue(id, out JsonObject? node)
                || CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "type")) != "image")
            {
                throw AppError.BadAuthRequest("批量创作表只能引用当前画布内真实存在的图片节点");
            }
        }
    }

    private static (Dictionary<string, JsonElement> Patch, List<string> Labels) ValidateRowPatch(
        JsonObject doc, Dictionary<string, JsonElement> input, int maxInputs)
    {
        Dictionary<string, JsonElement> patch = new(StringComparer.Ordinal);
        List<string> labels = [];
        foreach ((string key, JsonElement value) in input)
        {
            switch (key)
            {
                case "enabled":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw AppError.BadAuthRequest("enabled 必须是布尔值");
                    }
                    patch[key] = value.Clone();
                    labels.Add("启用状态");
                    break;
                case "prompt":
                    if (value.ValueKind != JsonValueKind.String
                        || (value.GetString() ?? "").EnumerateRunes().Count() > MaxBatchPromptRunes)
                    {
                        throw AppError.BadAuthRequest("提示词必须是不超过20000字的文本");
                    }
                    patch[key] = value.Clone();
                    labels.Add("提示词");
                    break;
                case "inputNodeIds":
                {
                    List<string> ids = ValidateBatchInputNodeIDs(JsonSerializer.SerializeToNode(value), maxInputs);
                    ValidateBatchInputNodes(doc, ids);
                    JsonArray values = new(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
                    patch[key] = JsonSerializer.SerializeToElement(values);
                    labels.Add("参考图片");
                    break;
                }
                default:
                    throw AppError.BadAuthRequest($"不能通过批量创作表工具修改字段 {key}");
            }
        }
        labels.Sort(StringComparer.Ordinal);
        return (patch, labels);
    }

    /// <summary>编辑批量创作表计划。对应 Go: <c>prepareCloudAgentBatchTableEdit</c>。</summary>
    public static async Task<(CanvasProject Canvas, JsonObject Document, string BeforeJSON, string BeforeHash, CloudAgentApprovalPreviewDto Preview)>
        PrepareBatchTableEditAsync(
            CloudAgentMutationContext context, string userID, string canvasID, CloudAgentCallDto call)
    {
        BatchTableEditArgs args;
        try
        {
            args = CloudAgentContracts.DecodeObject<BatchTableEditArgs>(call.Function.Arguments);
        }
        catch (CloudAgentArgumentException)
        {
            throw CloudAgentJson.BatchTableArgumentError();
        }
        catch (AppError)
        {
            throw CloudAgentJson.BatchTableArgumentError();
        }
        if (args.SnapshotHash.Length == 0 || args.NodeID.Length == 0)
        {
            throw AppError.BadAuthRequest("编辑批量创作表需要最新快照和节点ID");
        }
        CloudAgentContracts.ValidateCloudAgentID(args.NodeID, "批量创作表节点ID", 80);
        HashSet<string> allowedActions = new(StringComparer.Ordinal)
        { "append", "update", "remove", "set_operation", "set_concurrency", "add_reference_column" };
        if (!allowedActions.Contains(args.Action))
        {
            throw AppError.BadAuthRequest("批量创作表操作无效");
        }
        if (args.Action == "append" && args.RowID.Length > 0)
        {
            throw AppError.BadAuthRequest("追加批量创作行不能指定已有 rowId");
        }
        if ((args.Action == "update" || args.Action == "remove"))
        {
            try
            {
                CloudAgentContracts.ValidateCloudAgentID(args.RowID, "批量创作行ID", 120);
            }
            catch (AppError)
            {
                throw AppError.BadAuthRequest("修改或删除必须使用最新读取结果中的真实 rowId");
            }
        }

        CanvasProject? canvas = await context.CanvasProjectForUserAsync(userID, canvasID).ConfigureAwait(false)
            ?? throw AppError.NotFound("画布不存在");
        JsonObject doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
        string beforeHash = CloudAgentContracts.CanvasHash(doc);
        if (beforeHash != args.SnapshotHash)
        {
            throw CloudAgentSessionService.CreationConflict("画布已变化，本次未写入；请重新读取并重新申请审批");
        }
        (JsonObject node, JsonObject table, List<JsonObject> rows, List<JsonObject> columns) =
            BatchTableNode(doc, args.NodeID);
        JsonObject metadata = (node["metadata"] as JsonObject)!;
        if (CloudAgentJsonHelpers.Get(metadata, "locked") is JsonValue lockedValue
            && lockedValue.TryGetValue<bool>(out bool locked) && locked)
        {
            throw AppError.BadAuthRequest("不能修改锁定的批量创作表");
        }
        int index = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(rows[i], "id")) == args.RowID)
            {
                index = i;
                break;
            }
        }
        if ((args.Action == "update" || args.Action == "remove") && index < 0)
        {
            throw AppError.BadAuthRequest("批量创作行不存在，请先读取真实 rowId");
        }

        List<string> fields = [];
        string summaryVerb = "修改";
        switch (args.Action)
        {
            case "append":
            case "update":
            {
                if (args.Action == "update" && args.Patch.Count == 0)
                {
                    throw AppError.BadAuthRequest("修改批量创作行必须提供 patch");
                }
                (Dictionary<string, JsonElement> patch, List<string> labels) =
                    ValidateRowPatch(doc, args.Patch, columns.Count);
                fields = labels;
                if (args.Action == "append")
                {
                    if (rows.Count >= MaxBatchRows)
                    {
                        throw AppError.BadAuthRequest("单个批量创作表最多500行");
                    }
                    string operation = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(table, "operation"));
                    string prompt = operation == "creative" ? CreativeBatchPrompt : TryOnBatchPrompt;
                    JsonArray inheritedInputNodeIDs = new(
                        CloudAgentCanvasState.BatchInputIDs(
                                CloudAgentJsonHelpers.Get(rows[^1], "inputNodeIds"), columns.Count)
                            .Where(n => n is not null)
                            .Select(n => n!.DeepClone())
                            .ToArray());
                    JsonObject row = new()
                    {
                        ["id"] = CloudAgentContracts.AgentID(
                            userID, $"batch-table:{args.NodeID}:{call.ID}:{rows.Count + 1}"),
                        ["enabled"] = true,
                        ["inputNodeIds"] = inheritedInputNodeIDs,
                        ["prompt"] = prompt,
                    };
                    foreach ((string key, JsonElement value) in patch)
                    {
                        row[key] = JsonSerializer.SerializeToNode(value);
                    }
                    rows.Add(row);
                    summaryVerb = "追加一行到";
                    if (fields.Count == 0)
                    {
                        fields = ["任务行"];
                    }
                }
                else
                {
                    foreach ((string key, JsonElement value) in patch)
                    {
                        rows[index][key] = JsonSerializer.SerializeToNode(value);
                    }
                }
                table["rows"] = new JsonArray(rows.Select(r => (JsonNode?)r.DeepClone()).ToArray());
                break;
            }
            case "remove":
                if (args.Patch.Count != 0 || args.Operation.Length > 0 || args.Concurrency != 0)
                {
                    throw AppError.BadAuthRequest("删除批量创作行不接受其他修改参数");
                }
                rows.RemoveAt(index);
                table["rows"] = new JsonArray(rows.Select(r => (JsonNode?)r.DeepClone()).ToArray());
                fields = ["任务行"];
                summaryVerb = "删除一行自";
                break;
            case "set_operation":
                if (args.Operation is not ("try_on" or "creative"))
                {
                    throw AppError.BadAuthRequest("批量任务类型必须是 try_on 或 creative");
                }
                if (args.RowID.Length > 0 || args.Patch.Count != 0 || args.Concurrency != 0)
                {
                    throw AppError.BadAuthRequest("设置批量任务类型不接受行级修改参数");
                }
                table["operation"] = args.Operation;
                fields = ["任务类型"];
                break;
            case "set_concurrency":
                if (!ConcurrencyAllowed(args.Concurrency))
                {
                    throw AppError.BadAuthRequest("并发数必须是 1、5 或 10");
                }
                if (args.RowID.Length > 0 || args.Patch.Count != 0 || args.Operation.Length > 0)
                {
                    throw AppError.BadAuthRequest("设置并发数不接受行级修改参数");
                }
                table["concurrency"] = (double)args.Concurrency;
                fields = ["并发数"];
                break;
            case "add_reference_column":
                if (columns.Count >= MaxBatchReferences)
                {
                    throw AppError.BadAuthRequest("批量创作表最多支持6组参考图");
                }
                if (args.RowID.Length > 0 || args.Patch.Count != 0
                    || args.Operation.Length > 0 || args.Concurrency != 0)
                {
                    throw AppError.BadAuthRequest("新增参考图列不接受其他修改参数");
                }
                int next = columns.Count + 1;
                columns.Add(new JsonObject
                {
                    ["id"] = CloudAgentContracts.AgentID(userID, $"batch-column:{args.NodeID}:{call.ID}:{next}"),
                    ["label"] = $"参考图 {next}",
                });
                table["referenceColumns"] = new JsonArray(columns.Select(c => (JsonNode?)c.DeepClone()).ToArray());
                fields = ["参考图列"];
                summaryVerb = "新增参考图列到";
                break;
        }
        metadata["batchTable"] = table;
        string title = CloudAgentMutations.ApprovalNodeTitle(node, "批量创作表");
        CloudAgentApprovalPreviewDto preview = new()
        {
            Kind = "canvas_mutation",
            Title = "确认修改批量创作表",
            Description = "Agent 准备修改批量创作表的结构化任务数据。批准后才会写入画布；本操作不会提交生成任务。",
            Items =
            [
                new CloudAgentApprovalPreviewItemDto
                {
                    Operation = "edit_batch_table",
                    NodeID = args.NodeID,
                    NodeTitle = title,
                    NodeType = "batch-table",
                    NodeTypeLabel = "批量创作表",
                    Fields = fields,
                    Summary = $"{summaryVerb}批量创作表《{title}》",
                },
            ],
        };
        return (canvas, doc, canvas.PayloadJSON, beforeHash, preview);
    }

    /// <summary>批量创作表变更执行。对应 Go: <c>applyCloudAgentBatchTableMutation</c>。</summary>
    public static async Task<JsonObject> ApplyBatchTableMutationAsync(
        CloudAgentMutationContext context, string userID, string canvasID, CloudAgentCallDto call,
        RuntimePolicySetting policy, CloudAgentMutationRecorder? recorder)
    {
        (CanvasProject canvas, JsonObject doc, string beforeJSON, string beforeHash, CloudAgentApprovalPreviewDto preview) =
            await PrepareBatchTableEditAsync(context, userID, canvasID, call).ConfigureAwait(false);
        await CloudAgentMutations.SaveDocumentAsync(context, canvas, doc, policy).ConfigureAwait(false);
        if (recorder is not null)
        {
            await recorder(context, new CloudAgentMutationInput
            {
                UserID = userID,
                CanvasID = canvasID,
                StepID = call.ID,
                Operation = call.Function.Name,
                BeforeSnapshotHash = beforeHash,
                AfterSnapshotHash = CloudAgentContracts.CanvasHash(doc),
                BeforeJSON = beforeJSON,
                Preview = preview,
            }).ConfigureAwait(false);
        }
        return new JsonObject
        {
            ["canvasId"] = canvasID,
            ["nodeId"] = preview.Items[0].NodeID,
            ["snapshotHash"] = CloudAgentContracts.CanvasHash(doc),
            ["summary"] = preview.Description,
            ["preview"] = JsonSerializer.SerializeToNode(preview, OpenAICanvas.Domain.Serialization.GoJson.WriteOptions),
        };
    }

    /// <summary>读取工具结果装配：分镜。对应 Go: <c>cloudAgentStoryboardReadResult</c>。</summary>
    public static JsonObject StoryboardReadResult(JsonObject state, string nodeID)
    {
        JsonArray nodes = state["nodes"] as JsonArray ?? [];
        if (nodes.Count != 1 || nodes[0] is not JsonObject node)
        {
            throw AppError.BadAuthRequest("分镜读取结果缺少目标节点");
        }
        if (CloudAgentJsonHelpers.Get(node, "storyboard") is not JsonObject storyboard)
        {
            throw AppError.BadAuthRequest("分镜读取结果缺少镜头行");
        }
        if (storyboard["rows"] is JsonArray rows)
        {
            foreach (JsonNode? row in rows)
            {
                if (row is JsonObject rowObject)
                {
                    rowObject["rowId"] = CloudAgentJsonHelpers.Get(rowObject, "id")?.DeepClone();
                }
            }
        }
        return new JsonObject
        {
            ["nodeId"] = nodeID,
            ["title"] = CloudAgentJsonHelpers.Get(node, "title")?.DeepClone(),
            ["snapshotHash"] = CloudAgentJsonHelpers.Get(state, "snapshotHash")?.DeepClone(),
            ["storyboard"] = storyboard,
        };
    }

    /// <summary>读取工具结果装配：批量创作表。对应 Go: <c>cloudAgentBatchTableReadResult</c>。</summary>
    public static JsonObject BatchTableReadResult(JsonObject state, string nodeID)
    {
        JsonArray nodes = state["nodes"] as JsonArray ?? [];
        if (nodes.Count != 1 || nodes[0] is not JsonObject node)
        {
            throw AppError.BadAuthRequest("批量创作表读取结果缺少目标节点");
        }
        if (CloudAgentJsonHelpers.Get(node, "batchTable") is not JsonObject table)
        {
            throw AppError.BadAuthRequest("批量创作表读取结果缺少结构化任务行");
        }
        if (table["rows"] is JsonArray rows)
        {
            foreach (JsonNode? row in rows)
            {
                if (row is JsonObject rowObject)
                {
                    rowObject["rowId"] = CloudAgentJsonHelpers.Get(rowObject, "id")?.DeepClone();
                }
            }
        }
        return new JsonObject
        {
            ["nodeId"] = nodeID,
            ["title"] = CloudAgentJsonHelpers.Get(node, "title")?.DeepClone(),
            ["snapshotHash"] = CloudAgentJsonHelpers.Get(state, "snapshotHash")?.DeepClone(),
            ["batchTable"] = table,
        };
    }
}
