#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAICanvas.Domain.Canvas.Capability;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>
/// 运行时读工具：偏好层、节点能力卡、画布状态、分镜/批量表读取、技能文件、任务查询。
/// 对应 Go: <c>cloudAgentReadTool</c>、<c>cloudAgentSkillPage</c>、<c>cloudAgentNodeTypes</c>。
/// </summary>
public sealed partial class CloudAgentRuntimeService
{
    /// <summary>读工具分发。对应 Go: <c>cloudAgentReadTool</c>（检查点事务内调用）。</summary>
    private async Task<(JsonObject? Result, Exception? Error)> ReadToolAsync(
        CloudAgentMutationContext context, string userID, CloudAgentRuntimeDto state,
        CloudAgentCallDto call)
    {
        try
        {
            switch (call.Function.Name)
            {
                case "agent_profile_read":
                    return (ProfileRead(state, call), null);
                case "canvas_list_node_types":
                    CloudAgentContracts.DecodeObject<Dictionary<string, JsonElement>>(call.Function.Arguments);
                    return (NodeTypesData(), null);
                case "canvas_get_state":
                {
                    CanvasStateArgs args = CloudAgentContracts.DecodeObject<CanvasStateArgs>(call.Function.Arguments);
                    CanvasProject? canvas = await context.CanvasProjectForUserAsync(
                        userID, state.Request.CanvasID).ConfigureAwait(false)
                        ?? throw AppError.NotFound("画布不存在");
                    JsonObject doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
                    return (await CloudAgentCanvasState.ReadAsync(
                        context, userID, doc, args.Offset, args.NodeIDs, args.StoryboardOffset,
                        CancellationToken.None).ConfigureAwait(false), null);
                }
                case "canvas_read_storyboard":
                {
                    NodeReadArgs args = CloudAgentContracts.DecodeObject<NodeReadArgs>(call.Function.Arguments);
                    CloudAgentContracts.ValidateCloudAgentID(args.NodeID, "分镜节点ID", 80);
                    if (args.Offset < 0)
                    {
                        throw AppError.BadAuthRequest("分镜节点ID或分页参数无效");
                    }
                    CanvasProject? canvas = await context.CanvasProjectForUserAsync(
                        userID, state.Request.CanvasID).ConfigureAwait(false)
                        ?? throw AppError.NotFound("画布不存在");
                    JsonObject doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
                    CloudAgentStructuredEdits.StoryboardNode(doc, args.NodeID);
                    JsonObject view = await CloudAgentCanvasState.ReadAsync(
                        context, userID, doc, 0, [args.NodeID], args.Offset, CancellationToken.None)
                        .ConfigureAwait(false);
                    return (CloudAgentStructuredEdits.StoryboardReadResult(view, args.NodeID), null);
                }
                case "canvas_read_batch_table":
                {
                    NodeReadArgs args = CloudAgentContracts.DecodeObject<NodeReadArgs>(call.Function.Arguments);
                    CloudAgentContracts.ValidateCloudAgentID(args.NodeID, "批量创作表节点ID", 80);
                    if (args.Offset < 0)
                    {
                        throw AppError.BadAuthRequest("批量创作表节点ID或分页参数无效");
                    }
                    CanvasProject? canvas = await context.CanvasProjectForUserAsync(
                        userID, state.Request.CanvasID).ConfigureAwait(false)
                        ?? throw AppError.NotFound("画布不存在");
                    JsonObject doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
                    CloudAgentStructuredEdits.BatchTableNode(doc, args.NodeID);
                    JsonObject view = await CloudAgentCanvasState.ReadAsync(
                        context, userID, doc, 0, [args.NodeID], args.Offset, CancellationToken.None)
                        .ConfigureAwait(false);
                    return (CloudAgentStructuredEdits.BatchTableReadResult(view, args.NodeID), null);
                }
                case "task_get":
                {
                    TaskGetArgs args = CloudAgentContracts.DecodeObject<TaskGetArgs>(call.Function.Arguments);
                    TaskEntity? task = await context.TaskForUserAsync(userID, args.TaskID).ConfigureAwait(false)
                        ?? throw AppError.NotFound("record not found");
                    if (task.ProjectID != state.Request.CanvasID)
                    {
                        throw AppError.BadAuthRequest("不能读取其他画布的任务");
                    }
                    return (new JsonObject
                    {
                        ["taskId"] = task.ID,
                        ["status"] = task.Status,
                        ["text"] = CloudAgentContracts.TruncateRunes(TaskResultText(task.ResultJSON), 4000),
                    }, null);
                }
                default:
                    throw AppError.BadAuthRequest("未知工具");
            }
        }
        catch (Exception error) when (error is AppError or CloudAgentArgumentException or InvalidOperationException)
        {
            return (null, error);
        }
    }

    private sealed class CanvasStateArgs
    {
        public int Offset { get; set; }
        public List<string> NodeIDs { get; set; } = [];
        public int StoryboardOffset { get; set; }
    }

    private sealed class NodeReadArgs
    {
        public string NodeID { get; set; } = "";
        public int Offset { get; set; }
    }

    private sealed class TaskGetArgs
    {
        public string TaskID { get; set; } = "";
    }

    private sealed class ProfileReadArgs
    {
        public string Scope { get; set; } = "";
    }

    private sealed class SkillReadArgs
    {
        public string SkillID { get; set; } = "";
        public string Path { get; set; } = "";
        public int Offset { get; set; }
    }

    /// <summary>偏好层读取。对应 Go: <c>agent_profile_read</c> 分支。</summary>
    private static JsonObject ProfileRead(CloudAgentRuntimeDto state, CloudAgentCallDto call)
    {
        ProfileReadArgs args = CloudAgentContracts.DecodeObject<ProfileReadArgs>(call.Function.Arguments);
        if (args.Scope is not ("user" or "project" or "canvas"))
        {
            throw AppError.BadAuthRequest("长期偏好作用域无效");
        }
        state.ProfileReads ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        if (state.ProfileReads.TryGetValue(args.Scope, out bool read) && read)
        {
            throw AppError.BadAuthRequest("本轮已读取该长期偏好层，请使用历史工具结果，不要重复读取");
        }
        foreach (AgentProfileLayerDto layer in state.Profile.Layers)
        {
            if (layer.Scope != args.Scope)
            {
                continue;
            }
            state.ProfileReads[args.Scope] = true;
            return new JsonObject
            {
                ["scope"] = layer.Scope,
                ["revision"] = layer.Revision,
                ["hash"] = layer.Hash,
                ["content"] = layer.Content,
            };
        }
        throw AppError.BadAuthRequest("本轮固定快照中不存在该长期偏好层；请只读取系统清单列出的层");
    }

    /// <summary>
    /// 技能文件读取：在检查点事务之外执行（使用领域仓储与文件系统）。
    /// 对应 Go: <c>cloudAgentReadTool</c> 的 skill_read_file 事务外预读。
    /// </summary>
    private async Task<(JsonObject? Result, Exception? Error)> ReadSkillFileAsync(
        string userID, CloudAgentRuntimeDto state, CloudAgentCallDto call,
        CancellationToken cancellationToken)
    {
        try
        {
            SkillReadArgs args = CloudAgentContracts.DecodeObject<SkillReadArgs>(call.Function.Arguments);
            if (args.Offset < 0 || (args.Path.Length == 0 && args.Offset != 0))
            {
                throw AppError.BadAuthRequest("技能读取偏移无效");
            }
            foreach (CloudAgentSkillDto skill in state.Skills)
            {
                if (skill.ID != args.SkillID)
                {
                    continue;
                }
                string key = JsonSerializer.Serialize(
                    args.Offset > 0
                        ? new object[] { args.SkillID, args.Path, args.Offset }
                        : new object[] { args.SkillID, args.Path },
                    GoJson.WriteOptions);
                if (state.SkillReads is not null && state.SkillReads.TryGetValue(key, out bool already) && already)
                {
                    throw AppError.BadAuthRequest("本轮已请求过该技能路径，请使用历史工具结果；不要重复读取或猜测文件路径");
                }
                state.SkillReads ??= new Dictionary<string, bool>(StringComparer.Ordinal);
                state.SkillReads[key] = true;
                if (args.Path.Length == 0)
                {
                    return (new JsonObject
                    {
                        ["version"] = skill.Version,
                        ["entryPath"] = CloudAgentContracts.SkillEntryPath,
                        ["files"] = CloudAgentSkills.PathsArray(skill),
                        ["guidance"] = "先读取 SKILL.md，再只读取入口明确引用且当前任务需要的参考文件。只能读取 files 中列出的路径；不要重复列目录或猜测路径",
                    }, null);
                }
                SkillItemDto detail = await _skills.SkillDetailAsync(userID, skill.ID, cancellationToken)
                    .ConfigureAwait(false);
                if (!detail.IsAdded || detail.Status != 1
                    || detail.VersionID != skill.Version || detail.ContentHash != skill.Hash)
                {
                    throw CloudAgentSessionService.CreationConflict("技能已更新或不可用，请重试");
                }
                if (args.Path == CloudAgentContracts.SkillEntryPath)
                {
                    return ((JsonObject)SkillPage(skill.Version, args.Path, detail.Instruction, args.Offset)!, null);
                }
                if (skill.Files is null || !skill.Files.ContainsKey(args.Path))
                {
                    throw AppError.BadAuthRequest("参考文件未包含在本轮固定快照中");
                }
                SkillPackageFileContentDto file = await _skills.SkillPackageFileAsync(
                    userID, skill.ID, args.Path, cancellationToken).ConfigureAwait(false);
                if (file.Binary)
                {
                    throw AppError.BadAuthRequest("不支持读取二进制技能文件");
                }
                SkillItemDto latest = await _skills.SkillDetailAsync(userID, skill.ID, cancellationToken)
                    .ConfigureAwait(false);
                if (!latest.IsAdded || latest.Status != 1
                    || latest.VersionID != skill.Version || latest.ContentHash != skill.Hash)
                {
                    throw CloudAgentSessionService.CreationConflict("技能已更新或不可用，请重试");
                }
                return ((JsonObject)SkillPage(skill.Version, args.Path, file.Content, args.Offset)!, null);
            }
            throw AppError.BadAuthRequest("技能未在本轮启用，或参考文件未包含在固定快照中");
        }
        catch (Exception error) when (error is AppError or CloudAgentArgumentException)
        {
            return (null, error);
        }
    }

    /// <summary>技能分页。对应 Go: <c>cloudAgentSkillPage</c>。</summary>
    private static JsonObject SkillPage(string version, string path, string content, int offset)
    {
        List<char> runes = [.. content];
        if (offset < 0 || offset > runes.Count)
        {
            throw AppError.BadAuthRequest("技能读取偏移超出文件范围");
        }
        int end = offset + Math.Min(12000, runes.Count - offset);
        return new JsonObject
        {
            ["version"] = version,
            ["path"] = path,
            ["content"] = new string(runes.GetRange(offset, end - offset).ToArray()),
            ["offset"] = offset,
            ["nextOffset"] = end,
            ["hasMore"] = end < runes.Count,
        };
    }

    /// <summary>任务结果文本。对应 Go: <c>taskResultText</c>。</summary>
    private static string TaskResultText(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("text", out JsonElement text)
                   && text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>节点能力卡。对应 Go: <c>cloudAgentNodeTypes</c>。</summary>
    private static JsonObject NodeTypesData()
    {
        JsonArray types = [];
        foreach (CapabilityDescriptor capability in CloudAgentNodes.Registry.List())
        {
            JsonObject item = new()
            {
                ["type"] = capability.Type,
                ["label"] = capability.Label,
                ["purpose"] = capability.Purpose,
                ["defaultSize"] = new JsonObject
                {
                    ["width"] = capability.DefaultWidth,
                    ["height"] = capability.DefaultHeight,
                },
                ["canUpdate"] = capability.CanUpdate,
            };
            if (capability.GoodFor.Length > 0)
            {
                item["goodFor"] = new JsonArray(capability.GoodFor.Select(g => JsonValue.Create(g)).ToArray());
            }
            if (capability.NotIdealFor.Length > 0)
            {
                item["notIdealFor"] = new JsonArray(capability.NotIdealFor.Select(g => JsonValue.Create(g)).ToArray());
            }
            if (capability.Tradeoffs.Length > 0)
            {
                item["tradeoffs"] = new JsonArray(capability.Tradeoffs.Select(g => JsonValue.Create(g)).ToArray());
            }
            if (capability.Actions.Length > 0)
            {
                item["actions"] = new JsonArray(capability.Actions.Select(g => JsonValue.Create(g)).ToArray());
            }
            if (capability.CanUpdate)
            {
                JsonObject fields = new();
                foreach ((string key, PatchField field) in capability.PatchFields)
                {
                    JsonObject definition = new()
                    {
                        ["type"] = field.Kind,
                        ["label"] = field.Label,
                        ["displayOrder"] = field.Order,
                        ["maxCharacters"] = field.MaxRunes,
                    };
                    if (field.Description.Length > 0)
                    {
                        definition["description"] = field.Description;
                    }
                    fields[key] = definition;
                }
                item["updateFields"] = fields;
            }
            if (capability.InputKind.Length > 0)
            {
                item["inputKind"] = capability.InputKind;
            }
            if (capability.GenerationMode.Length > 0
                && CloudAgentNodes.GenerationModeSupported(capability.GenerationMode))
            {
                item["generationMode"] = capability.GenerationMode;
            }
            if (capability.Connection.AcceptedInputKinds.Length > 0)
            {
                item["acceptedInputKinds"] = new JsonArray(
                    capability.Connection.AcceptedInputKinds.Select(k => JsonValue.Create(k)).ToArray());
            }
            if (capability.Connection.RejectedInputKinds.Length > 0)
            {
                item["rejectedInputKinds"] = new JsonArray(
                    capability.Connection.RejectedInputKinds.Select(k => JsonValue.Create(k)).ToArray());
            }
            if (capability.Connection.MaxInputCount > 0)
            {
                item["maxInputCount"] = capability.Connection.MaxInputCount;
            }
            item["canSource"] = capability.Connection.CanSource;
            item["canTarget"] = capability.Connection.CanTarget;
            item["canReference"] = capability.Connection.CanReference;
            types.Add(item);
        }
        return new JsonObject
        {
            ["schemaVersion"] = 2,
            ["nodes"] = types,
            ["selectionGuide"] = new JsonArray(
                "单个画面、一次性提示词或快速试验通常使用文本/Markdown与媒体节点更轻量。",
                "多镜头、连续性、逐镜审查、逐镜生成或需要后续维护时，分镜脚本通常更合适。",
                "媒体节点只承载单个生成目标，不替代多镜头结构；选择媒体节点后还要用 model_list 按生成模式和本次真实参考节点筛选模型。",
                "节点选择由Agent结合用户目标决定；不要为了形式创建复杂节点，也不要用普通文本伪装成结构化分镜。"),
        };
    }
}
