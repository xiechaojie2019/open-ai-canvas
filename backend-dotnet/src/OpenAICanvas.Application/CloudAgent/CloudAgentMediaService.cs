#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAICanvas.Domain.Canvas.Capability;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>generate_media 参数。对应 Go: <c>app.cloudAgentMediaArgs</c>。</summary>
public sealed class CloudAgentMediaArgs
{
    public string DraftRunID { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string LogicalModelID { get; set; } = "";
    public string ChannelID { get; set; } = "";
    public string ChannelModelKey { get; set; } = "";
    public int Duration { get; set; }
    public string Size { get; set; } = "";
    public string Quality { get; set; } = "";
    public bool? VideoGenerateAudio { get; set; }
    public string SnapshotHash { get; set; } = "";
    public string NodeID { get; set; } = "";
    public string Title { get; set; } = "";
    public string SourceNodeID { get; set; } = "";
    public List<string> ReferenceNodeIDs { get; set; } = [];
}

/// <summary>媒体计划。对应 Go: <c>app.cloudAgentMediaPlan</c>。</summary>
public sealed class CloudAgentMediaPlan
{
    public CloudAgentMediaArgs Args { get; init; } = new();
    public string CallID { get; init; } = "";
}

/// <summary>审批边界允许修改的图片参数。对应 Go: <c>app.CloudAgentMediaSettings</c>。</summary>
public sealed class CloudAgentMediaSettings
{
    public string LogicalModelID { get; set; } = "";
    public string ChannelID { get; set; } = "";
    public string ChannelModelKey { get; set; } = "";
    public string Size { get; set; } = "";
    public string Quality { get; set; } = "";
}

/// <summary>
/// 云 Agent 媒体生成：模型目录筛选、参考解析、草稿节点与完成回写。
/// 对应 Go: <c>app/cloud_agent_media.go</c>、<c>cloud_agent_media_approval.go</c>。
/// </summary>
public sealed class CloudAgentMediaService
{
    private readonly Repository _repository;
    private readonly ModelCatalogService _modelCatalog;

    public CloudAgentMediaService(Repository repository, ModelCatalogService modelCatalog)
    {
        _repository = repository;
        _modelCatalog = modelCatalog;
    }

    public static bool GenerationModeSupported(string mode) => CloudAgentNodes.GenerationModeSupported(mode);

    public static string Operation(string mode, IReadOnlyDictionary<string, List<JsonObject>> references)
    {
        static int Count(IReadOnlyDictionary<string, List<JsonObject>> refs, string key) =>
            refs.TryGetValue(key, out List<JsonObject>? list) ? list.Count : 0;
        switch (mode)
        {
            case "video":
                if (Count(references, "referenceVideos") > 0)
                {
                    return "reference_to_video";
                }
                if (Count(references, "referenceAudios") > 0)
                {
                    return "audio_to_video";
                }
                if (Count(references, "referenceImages") > 0)
                {
                    return "image_to_video";
                }
                break;
            case "image":
                if (Count(references, "referenceImages") > 0)
                {
                    return "image_to_image";
                }
                break;
        }
        return mode is "image" or "video" or "audio" ? "text_to_" + mode : "";
    }

    public static void ValidateReferences(string mode, IReadOnlyDictionary<string, List<JsonObject>> references)
    {
        static int Count(IReadOnlyDictionary<string, List<JsonObject>> refs, string key) =>
            refs.TryGetValue(key, out List<JsonObject>? list) ? list.Count : 0;
        int imageCount = Count(references, "referenceImages");
        int videoCount = Count(references, "referenceVideos");
        int audioCount = Count(references, "referenceAudios");
        switch (mode)
        {
            case "image" when videoCount > 0 || audioCount > 0:
                throw AppError.BadAuthRequest("图片生成仅支持图片参考资产");
            case "audio" when imageCount > 0 || videoCount > 0 || audioCount > 0:
                throw AppError.BadAuthRequest("当前音频生成只支持文本输入，暂不支持媒体参考资产");
            case "video":
                // 视频参考准入由 CreateTask 按所选模型能力合同完成。
                break;
            default:
                if (mode is not ("image" or "video" or "audio"))
                {
                    throw AppError.BadAuthRequest("生成模式尚未实现媒体任务适配器");
                }
                break;
        }
    }

    /// <summary>模型展示名。对应 Go: <c>cloudAgentMediaModelName</c>。</summary>
    public async Task<string> ModelNameAsync(CloudAgentMediaArgs args, CancellationToken cancellationToken)
    {
        if (!GenerationModeSupported(args.Mode))
        {
            throw AppError.BadAuthRequest("生成模式当前不受 Agent 支持");
        }
        ModelCatalogResponseDto catalog = await _modelCatalog.CatalogAsync(null, cancellationToken).ConfigureAwait(false);
        foreach (PublicLogicalModelDto model in catalog.Models)
        {
            if (args.LogicalModelID.Length > 0 && model.ID == args.LogicalModelID
                && model.Available
                && CapabilityMatches(model.Capability, args.Mode))
            {
                return model.Name;
            }
        }
        foreach (PublicChannelCatalogDto channel in catalog.Channels)
        {
            if (channel.ID != args.ChannelID)
            {
                continue;
            }
            foreach (PublicChannelModelDto model in channel.Models)
            {
                if (model.ModelKey == args.ChannelModelKey && model.Available
                    && CapabilityMatches(model.Capability, args.Mode))
                {
                    return model.DisplayName;
                }
            }
        }
        throw AppError.BadAuthRequest("模型目录已变化，请重新读取目录并询问用户选择模型");
    }

    private static string NormalizeCapability(string value) => AdminCapabilityNormalize(value);

    private static string AdminCapabilityNormalize(string value)
    {
        value = value.Trim().ToLowerInvariant();
        return value.Length == 0 ? "" : value switch
        {
            "image_generation" or "image-to-image" or "text-to-image" => "image",
            "video_generation" or "text-to-video" or "image-to-video" => "video",
            "audio_generation" or "text-to-audio" or "text-to-speech" => "audio",
            _ => value.Split('/')[0].Split('-')[0],
        };
    }

    private static bool CapabilityMatches(string capability, string mode) =>
        NormalizeCapability(capability) == NormalizeCapability(mode);

    /// <summary>解析后的媒体规格校验。对应 Go: <c>validateCloudAgentResolvedMediaOptions</c>。</summary>
    public static void ValidateResolvedOptions(
        Dictionary<string, JsonElement> requested, Dictionary<string, JsonElement> resolved)
    {
        foreach (string key in new[] { "size", "videoSeconds", "vquality", "quality", "count", "videoGenerateAudio" })
        {
            string requestedValue = ElementString(requested, key);
            if (requestedValue.Length > 0
                && !string.Equals(requestedValue, ElementString(resolved, key), StringComparison.OrdinalIgnoreCase))
            {
                throw CloudAgentSessionService.CreationConflict("模型解析后的生成规格与审批参数不同，请重新读取目录并审批");
            }
        }
    }

    private static string ElementString(Dictionary<string, JsonElement>? payload, string key)
    {
        if (payload is not null && payload.TryGetValue(key, out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "";
        }
        return "";
    }

    private static string ElementString(JsonObject payload, string key) =>
        CloudAgentJsonHelpers.Get(payload, key) is JsonValue v && v.TryGetValue<string>(out string? text)
            ? text
            : "";

    /// <summary>
    /// 媒体画布文档校验：快照、目标草稿、来源节点与 prospective 连线准入。
    /// 对应 Go: <c>cloudAgentMediaDocument</c>。返回（画布, 文档, 参考）。
    /// </summary>
    public async Task<(CanvasProject Canvas, JsonObject Doc, Dictionary<string, List<JsonObject>> References)>
        MediaDocumentAsync(
            CloudAgentMutationContext context, string userID, string canvasID,
            CloudAgentMediaArgs args, CancellationToken cancellationToken)
    {
        CanvasProject? canvas = await context.CanvasProjectForUserAsync(userID, canvasID).ConfigureAwait(false)
            ?? throw AppError.NotFound("画布不存在");
        JsonObject doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
        bool unchanged = args.SnapshotHash.Length > 0
            && (CloudAgentContracts.CanvasHash(doc) == args.SnapshotHash
                || CloudAgentContracts.MediaContentHash(doc) == args.SnapshotHash);
        if (!unchanged)
        {
            throw CloudAgentSessionService.CreationConflict("画布已变化，请重新读取画布并重新审批；未提交生成任务");
        }
        Dictionary<string, JsonObject> nodes =
            CloudAgentJsonHelpers.Objects(CloudAgentJsonHelpers.Get(doc, "nodes"));
        nodes.TryGetValue(args.NodeID, out JsonObject? existing);
        JsonObject existingMeta = existing?["metadata"] as JsonObject ?? new JsonObject();
        (CapabilityDescriptor targetDescriptor, bool supported) =
            CloudAgentNodes.ForGenerationMode(args.Mode);
        if (!supported)
        {
            throw AppError.BadAuthRequest("生成模式当前不受 Agent 支持");
        }
        CloudAgentContracts.ValidateCloudAgentID(args.NodeID, "生成节点 ID", 80);
        if (existing is not null)
        {
            if (IsTruthy(existingMeta, "locked")
                || ElementString(existingMeta, "generationTaskId").Length > 0)
            {
                throw AppError.BadAuthRequest("目标节点已锁定或已关联任务，不能提交生成");
            }
            if (args.DraftRunID.Length == 0
                || CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(existing, "type")) != targetDescriptor.Type
                || ElementString(existingMeta, "taskId").Length > 0
                || ElementString(existingMeta, "storageKey").Length > 0
                || ElementString(existingMeta, "content").Length > 0
                || ElementString(existingMeta, "status") != "idle")
            {
                throw AppError.BadAuthRequest("目标不是可续用的未提交媒体草稿；不要覆盖已有任务或成品，也不要循环创建替代节点");
            }
            string ownerID = ElementString(existingMeta, "agentDraftRunId");
            if (ownerID.Length > 0 && ownerID != args.DraftRunID)
            {
                CloudAgentExecution? owner = await context.CloudAgentAsync(userID, ownerID, cancellationToken)
                    .ConfigureAwait(false);
                if (owner is null)
                {
                    throw AppError.BadAuthRequest("无法确认草稿所属运行，请停止重试并检查原草稿");
                }
                if (owner.CanvasID != canvasID || !CloudAgentContracts.IsRunTerminal(owner.Status) || owner.CleanupPending)
                {
                    throw AppError.BadAuthRequest("草稿仍由另一运行处理，请先完成或取消原运行；不要另建节点绕过审批");
                }
            }
        }
        if (args.SourceNodeID.Length > 0 && !nodes.ContainsKey(args.SourceNodeID))
        {
            throw AppError.BadAuthRequest("来源镜头节点不在当前画布");
        }
        if (args.SourceNodeID.Length > 0 && nodes.TryGetValue(args.SourceNodeID, out JsonObject? source))
        {
            (CapabilityDescriptor sourceDescriptor, bool sourceKnown) =
                CloudAgentNodes.ForType(CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(source, "type")));
            if (!sourceKnown || !sourceDescriptor.Connection.CanSource || sourceDescriptor.InputKind != "text")
            {
                throw AppError.BadAuthRequest("sourceNodeId 仅接受可作为文本输入的节点；媒体资产请放入 referenceNodeIds，并将 sourceNodeId 留空，不要重复传入");
            }
        }
        List<JsonObject> prospectiveConnections =
            MediaConnections(CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "connections")), args);
        List<JsonObject> prospectiveNodes = [.. nodes.Values];
        if (existing is null)
        {
            prospectiveNodes.Add(new JsonObject
            {
                ["id"] = args.NodeID,
                ["type"] = targetDescriptor.Type,
            });
        }
        HashSet<string> prospectiveSeen = new(StringComparer.Ordinal);
        foreach (string sourceID in args.ReferenceNodeIDs.Append(args.SourceNodeID))
        {
            if (sourceID.Length == 0 || !prospectiveSeen.Add(sourceID))
            {
                continue;
            }
            bool alreadyConnected = prospectiveConnections.Any(edge =>
                CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "fromNodeId")) == sourceID
                && CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "toNodeId")) == args.NodeID);
            if (alreadyConnected)
            {
                continue;
            }
            CloudAgentMutations.ValidateConnection(prospectiveNodes, sourceID, args.NodeID, prospectiveConnections);
            prospectiveConnections.Add(new JsonObject
            {
                ["fromNodeId"] = sourceID,
                ["toNodeId"] = args.NodeID,
            });
        }
        Dictionary<string, List<JsonObject>> references = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string id in args.ReferenceNodeIDs)
        {
            if (id.Length == 0 || !seen.Add(id) || !nodes.TryGetValue(id, out JsonObject? node))
            {
                throw AppError.BadAuthRequest("参考节点不存在或重复");
            }
            (JsonObject? reference, string payloadField, AppError? error) =
                await CloudAgentJsonHelpers.ReferenceAsync(_repository, userID, node, cancellationToken)
                    .ConfigureAwait(false);
            if (error is not null)
            {
                throw error;
            }
            if (reference is not null)
            {
                if (!references.TryGetValue(payloadField, out List<JsonObject>? list))
                {
                    list = [];
                    references[payloadField] = list;
                }
                list.Add(reference);
            }
        }
        return (canvas, doc, references);
    }

    private static bool IsTruthy(JsonObject obj, string key) =>
        CloudAgentJsonHelpers.Get(obj, key) is JsonValue v && v.TryGetValue<bool>(out bool b) && b;

    /// <summary>草稿入边必须描述新批准的输入。对应 Go: <c>cloudAgentMediaConnections</c>。</summary>
    public static List<JsonObject> MediaConnections(List<JsonObject> edges, CloudAgentMediaArgs args)
    {
        HashSet<string> wanted = new(args.ReferenceNodeIDs, StringComparer.Ordinal);
        if (args.SourceNodeID.Length > 0)
        {
            wanted.Add(args.SourceNodeID);
        }
        List<JsonObject> result = [];
        foreach (JsonObject edge in edges)
        {
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "toNodeId")) != args.NodeID
                || wanted.Contains(CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "fromNodeId"))))
            {
                result.Add(edge);
            }
        }
        return result;
    }

    /// <summary>generate_media 参数校验。对应 Go: <c>validateCloudAgentMediaArgs</c>。</summary>
    public static void ValidateArgs(CloudAgentMediaArgs args, CloudAgentRuntimeDto state)
    {
        string mode = args.Mode.Trim().ToLowerInvariant();
        if (!GenerationModeSupported(mode))
        {
            throw AppError.BadAuthRequest("生成模式当前不受 Agent 支持");
        }
        args.Mode = mode;
        if (args.SnapshotHash.Length == 0)
        {
            throw AppError.BadAuthRequest("缺少画布快照，请先读取当前画布");
        }
        if (args.SnapshotHash.Length != 64 || args.SnapshotHash.Any(c => !Uri.IsHexDigit(c)))
        {
            throw AppError.BadAuthRequest("画布快照无效，请重新读取当前画布");
        }
        CloudAgentContracts.ValidateCloudAgentID(args.NodeID, "生成节点 ID", 80);
        if (args.SourceNodeID.Length > 0)
        {
            CloudAgentContracts.ValidateCloudAgentID(args.SourceNodeID, "来源节点 ID", 80);
        }
        foreach (string id in args.ReferenceNodeIDs)
        {
            CloudAgentContracts.ValidateCloudAgentID(id, "参考节点 ID", 80);
        }
        if (args.Prompt.Trim().Length == 0)
        {
            throw AppError.BadAuthRequest("生成提示词不能为空");
        }
        if (args.Title.Trim().Length == 0 || args.Title.EnumerateRunes().Count() > 240)
        {
            throw AppError.BadAuthRequest("生成节点标题不能为空且不能超过 240 个字符");
        }
        if (args.Prompt.EnumerateRunes().Count() > 16000)
        {
            throw AppError.BadAuthRequest(
                $"提示词共{args.Prompt.EnumerateRunes().Count()}字符，超过16000字符上限；请先告知用户，不要擅自删改关键内容");
        }
        if ((mode == "image" || mode == "video") && args.Size.Trim().Length == 0)
        {
            throw AppError.BadAuthRequest("请填写模型支持的具体画幅；用户授权默认时沿用参考图比例或目录默认画幅，无需重复询问");
        }
        if (args.Duration < 0)
        {
            throw AppError.BadAuthRequest("生成时长不能为负数");
        }
        switch (mode)
        {
            case "video" when args.Duration == 0:
                throw AppError.BadAuthRequest("视频生成必须明确 durationSeconds");
            case "image" or "audio":
                if (args.Duration != 0)
                {
                    throw AppError.BadAuthRequest("只有视频生成允许设置 durationSeconds");
                }
                if (args.VideoGenerateAudio is not null)
                {
                    throw AppError.BadAuthRequest("videoGenerateAudio 仅适用于视频生成");
                }
                break;
        }
        if (args.ReferenceNodeIDs.Count > 16)
        {
            throw AppError.BadAuthRequest("参考节点最多 16 个");
        }
        if (args.SourceNodeID.Length > 0 && args.ReferenceNodeIDs.Contains(args.SourceNodeID))
        {
            throw AppError.BadAuthRequest("sourceNodeId 与 referenceNodeIds 不能重复；参考图片、视频、音频请仅保留在 referenceNodeIds 并清空 sourceNodeId，文本镜头节点则仅放 sourceNodeId");
        }
        if (state.Request.Budget.MaxGenerationTasks > 0
            && state.Generations >= state.Request.Budget.MaxGenerationTasks)
        {
            throw AppError.BadAuthRequest("已达到本轮媒体生成次数上限");
        }
        if (mode == "video" && state.Request.Budget.MaxVideoSeconds > 0
            && state.VideoSeconds > state.Request.Budget.MaxVideoSeconds - args.Duration)
        {
            throw AppError.BadAuthRequest("已超过本轮视频时长预算");
        }
    }

    /// <summary>组装媒体任务请求。对应 Go: <c>prepareCloudAgentMedia</c>。</summary>
    public async Task<(CreateTaskRequestDto Request, CloudAgentMediaPlan Plan)> PrepareAsync(
        CloudAgentMutationContext context, CloudAgentExecution run, CloudAgentRuntimeDto state,
        CloudAgentCallDto call, CancellationToken cancellationToken)
    {
        CloudAgentMediaArgs args;
        try
        {
            args = CloudAgentContracts.DecodeObject<CloudAgentMediaArgs>(call.Function.Arguments);
        }
        catch (CloudAgentArgumentException)
        {
            throw AppError.BadAuthRequest("生成参数必须是只含支持字段的单个JSON对象");
        }
        catch (AppError)
        {
            throw AppError.BadAuthRequest("生成参数必须是只含支持字段的单个JSON对象");
        }
        args.Mode = args.Mode.Trim().ToLowerInvariant();
        args.DraftRunID = run.ID;
        ValidateArgs(args, state);
        if ((args.LogicalModelID.Length == 0 && (args.ChannelID.Length == 0 || args.ChannelModelKey.Length == 0))
            || (args.LogicalModelID.Length > 0 && (args.ChannelID.Length > 0 || args.ChannelModelKey.Length > 0)))
        {
            throw AppError.BadAuthRequest("请复制 model_list 的 selection：逻辑模型或系统渠道二选一，不得混用");
        }
        (_, _, Dictionary<string, List<JsonObject>> refs) = await MediaDocumentAsync(
            context, run.UserID, state.Request.CanvasID, args, cancellationToken).ConfigureAwait(false);
        JsonObject config = new() { ["count"] = "1" };
        if (args.ChannelID.Length > 0)
        {
            config["channelId"] = args.ChannelID;
            config["channelModelKey"] = args.ChannelModelKey;
            config["model"] = args.ChannelModelKey;
        }
        if (args.Mode == "video")
        {
            config["videoSeconds"] = args.Duration.ToString();
        }
        if (args.Size.Length > 0)
        {
            config["size"] = args.Size;
        }
        if (args.Quality.Length > 0)
        {
            config[args.Mode == "video" ? "vquality" : "quality"] = args.Quality;
        }
        if (args.VideoGenerateAudio is not null)
        {
            config["videoGenerateAudio"] = args.VideoGenerateAudio.Value ? "true" : "false";
        }
        Dictionary<string, JsonElement> input = refs.ToDictionary(
            pair => pair.Key,
            pair => JsonSerializer.SerializeToElement(pair.Value, OpenAICanvas.Domain.Serialization.GoJson.WriteOptions),
            StringComparer.Ordinal);
        input["mode"] = JsonSerializer.SerializeToElement(args.Mode);
        input["prompt"] = JsonSerializer.SerializeToElement(args.Prompt);
        input["config"] = JsonSerializer.SerializeToElement(config);
        ValidateReferences(args.Mode, refs);
        string operation = Operation(args.Mode, refs);
        if (operation.Length == 0)
        {
            throw AppError.BadAuthRequest("生成模式尚未实现媒体任务适配器");
        }
        JsonObject metadata = new()
        {
            ["nodeId"] = args.NodeID,
            ["source"] = "cloud_agent",
        };
        if (args.Mode == "video")
        {
            metadata["videoEditOperation"] = operation;
        }
        input["metadata"] = JsonSerializer.SerializeToElement(metadata);
        return (new CreateTaskRequestDto
        {
            ProjectID = state.Request.CanvasID,
            Type = "canvas_" + args.Mode,
            Operation = operation,
            Prompt = args.Prompt,
            LogicalModelID = args.LogicalModelID,
            Model = args.ChannelModelKey,
            Input = input,
        }, new CloudAgentMediaPlan { Args = args, CallID = call.ID });
    }

    /// <summary>媒体审批预览。对应 Go: <c>cloudAgentMediaApprovalPreview</c>。</summary>
    public static CloudAgentApprovalPreviewDto ApprovalPreview(CloudAgentMediaPlan plan, string modelName)
    {
        CloudAgentMediaArgs args = plan.Args;
        (CapabilityDescriptor descriptor, bool _) = CloudAgentNodes.ForGenerationMode(args.Mode);
        string nodeTitle = CloudAgentContracts.TruncateRunes(args.Title.Trim(), 120);
        if (nodeTitle.Length == 0)
        {
            nodeTitle = "未命名" + descriptor.Label;
        }
        List<string> details = new(6);
        if (modelName.Length > 0)
        {
            details.Add("模型：" + CloudAgentContracts.TruncateRunes(modelName, 120));
        }
        details.Add(args.ReferenceNodeIDs.Count > 0
            ? $"引用 {args.ReferenceNodeIDs.Count} 个画布资产并建立连线"
            : "不引用画布媒体资产");
        if (args.Duration > 0)
        {
            details.Add($"时长：{args.Duration} 秒");
        }
        if (args.Size.Length > 0)
        {
            details.Add("画幅：" + CloudAgentContracts.TruncateRunes(args.Size, 40));
        }
        if (args.Quality.Length > 0)
        {
            details.Add("质量：" + CloudAgentContracts.TruncateRunes(args.Quality, 40));
        }
        if (args.VideoGenerateAudio is not null)
        {
            details.Add(args.VideoGenerateAudio.Value ? "音频：开启" : "音频：关闭");
        }
        return new CloudAgentApprovalPreviewDto
        {
            Kind = "media_generation",
            Title = "确认生成" + descriptor.Label,
            Description = descriptor.Label + "草稿节点和引用连线已创建，尚未提交生成。确认规格后批准才会提交收费任务；拒绝则保留草稿，结果自动回写画布。",
            Items =
            [
                new CloudAgentApprovalPreviewItemDto
                {
                    Operation = "generate_media",
                    NodeID = args.NodeID,
                    NodeTitle = nodeTitle,
                    NodeType = descriptor.Type,
                    NodeTypeLabel = descriptor.Label,
                    Details = details,
                    Summary = "生成" + descriptor.Label + "《" + nodeTitle + "》",
                },
            ],
        };
    }

    /// <summary>媒体任务模型目录意图。对应 Go: <c>cloudAgentModelIntent</c>。</summary>
    public async Task<ModelRequestIntent?> ModelIntentAsync(
        string userID, string canvasID, string arguments, CancellationToken cancellationToken)
    {
        CloudAgentMediaIntentArgs args;
        try
        {
            args = CloudAgentContracts.DecodeObject<CloudAgentMediaIntentArgs>(arguments);
        }
        catch (CloudAgentArgumentException)
        {
            throw AppError.BadAuthRequest("模型查询参数无效");
        }
        catch (AppError)
        {
            throw AppError.BadAuthRequest("模型查询参数无效");
        }
        if (args.Mode.Length == 0 && args.ReferenceNodeIDs.Count == 0)
        {
            return null;
        }
        if (!GenerationModeSupported(args.Mode) || args.ReferenceNodeIDs.Count > 16)
        {
            throw AppError.BadAuthRequest("请指定支持的生成模式，参考节点最多16个");
        }
        CanvasProject? canvas = await _repository.CanvasProjectForUserAsync(userID, canvasID, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.NotFound("画布不存在");
        JsonObject doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
        Dictionary<string, JsonObject> nodes =
            CloudAgentJsonHelpers.Objects(CloudAgentJsonHelpers.Get(doc, "nodes"));
        Dictionary<string, List<JsonObject>> references = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string id in args.ReferenceNodeIDs)
        {
            if (id.Length == 0 || !seen.Add(id) || !nodes.TryGetValue(id, out JsonObject? node))
            {
                throw AppError.BadAuthRequest("参考节点不存在或重复");
            }
            (JsonObject? reference, string payloadField, AppError? error) =
                await CloudAgentJsonHelpers.ReferenceAsync(_repository, userID, node, cancellationToken)
                    .ConfigureAwait(false);
            if (error is not null)
            {
                throw error;
            }
            if (reference is not null)
            {
                if (!references.TryGetValue(payloadField, out List<JsonObject>? list))
                {
                    references[payloadField] = list = [];
                }
                list.Add(reference);
            }
        }
        ValidateReferences(args.Mode, references);
        Dictionary<string, JsonElement> input = references.ToDictionary(
            pair => pair.Key,
            pair => JsonSerializer.SerializeToElement(pair.Value, OpenAICanvas.Domain.Serialization.GoJson.WriteOptions),
            StringComparer.Ordinal);
        input["mode"] = JsonSerializer.SerializeToElement(args.Mode);
        return TaskCreationService.ModelRequestIntentFromTaskInputPublic(
            input, "canvas_" + args.Mode, Operation(args.Mode, references));
    }

    private sealed class CloudAgentMediaIntentArgs
    {
        public string Mode { get; set; } = "";
        public List<string> ReferenceNodeIDs { get; set; } = [];
    }

    /// <summary>模型目录投影。对应 Go: <c>cloudAgentModelList</c>。</summary>
    public async Task<JsonObject> ModelListAsync(
        ModelRequestIntent? intent, CancellationToken cancellationToken)
    {
        ModelCatalogResponseDto catalog = await _modelCatalog.CatalogAsync(intent, cancellationToken).ConfigureAwait(false);
        JsonArray items = [];
        foreach (PublicLogicalModelDto model in catalog.Models)
        {
            if (model.Available && GenerationModeSupported(NormalizeCapability(model.Capability)))
            {
                items.Add(new JsonObject
                {
                    ["name"] = model.Name,
                    ["capability"] = model.Capability,
                    ["selection"] = new JsonObject { ["logicalModelId"] = model.ID },
                    ["priceLabel"] = model.PriceLabel,
                    ["priceTiers"] = JsonSerializer.SerializeToNode(model.PriceTiers, OpenAICanvas.Domain.Serialization.GoJson.WriteOptions),
                    ["options"] = JsonSerializer.SerializeToNode(model.CapabilitySpec, OpenAICanvas.Domain.Serialization.GoJson.WriteOptions),
                    ["profiles"] = JsonSerializer.SerializeToNode(model.CapabilityProfiles, OpenAICanvas.Domain.Serialization.GoJson.WriteOptions),
                    ["defaults"] = JsonSerializer.SerializeToNode(model.DefaultOptions, OpenAICanvas.Domain.Serialization.GoJson.WriteOptions),
                });
            }
        }
        foreach (PublicChannelCatalogDto channel in catalog.Channels)
        {
            foreach (PublicChannelModelDto model in channel.Models)
            {
                if (model.Available && GenerationModeSupported(NormalizeCapability(model.Capability)))
                {
                    items.Add(new JsonObject
                    {
                        ["name"] = model.DisplayName,
                        ["capability"] = model.Capability,
                        ["selection"] = new JsonObject
                        {
                            ["channelId"] = channel.ID,
                            ["channelModelKey"] = model.ModelKey,
                        },
                        ["priceLabel"] = model.PriceLabel,
                        ["priceTiers"] = JsonSerializer.SerializeToNode(model.PriceTiers, OpenAICanvas.Domain.Serialization.GoJson.WriteOptions),
                        ["options"] = JsonSerializer.SerializeToNode(model.CapabilityConfig, OpenAICanvas.Domain.Serialization.GoJson.WriteOptions),
                    });
                }
            }
        }
        return new JsonObject
        {
            ["source"] = catalog.Source,
            ["models"] = items,
            ["intent"] = intent is null
                ? null
                : JsonSerializer.SerializeToNode(intent, OpenAICanvas.Domain.Serialization.GoJson.WriteOptions),
        };
    }

    /// <summary>草稿/提交节点写入。对应 Go: <c>createCloudAgentMediaNode</c>。</summary>
    public async Task CreateMediaNodeAsync(
        CloudAgentMutationContext context, string userID, string canvasID,
        CloudAgentMediaPlan plan, TaskEntity? task, RuntimePolicySetting policy,
        CloudAgentMutationRecorder? recorder, CloudAgentRuntimeDto state, string runID)
    {
        CloudAgentMediaArgs args = plan.Args;
        (CanvasProject canvas, JsonObject doc, Dictionary<string, List<JsonObject>> _) =
            await MediaDocumentAsync(context, userID, canvasID, args, cancellationToken: default)
                .ConfigureAwait(false);
        string beforeJSON = canvas.PayloadJSON;
        string beforeHash = CloudAgentContracts.CanvasHash(doc);
        List<JsonObject> nodes = CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "nodes"));
        double x = 80, y = 80;
        foreach (JsonObject node in nodes)
        {
            JsonObject? position = node["position"] as JsonObject;
            double nx = position?["x"]?.GetValueKind() == JsonValueKind.Number
                ? position!["x"]!.GetValue<double>() : 0;
            double width = node["width"]?.GetValueKind() == JsonValueKind.Number
                ? node["width"]!.GetValue<double>() : 0;
            if (nx + width + 80 > x)
            {
                x = nx + width + 80;
            }
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "id")) == args.SourceNodeID)
            {
                y = position?["y"]?.GetValueKind() == JsonValueKind.Number
                    ? position!["y"]!.GetValue<double>() : 0;
            }
        }
        JsonObject meta = new()
        {
            ["status"] = "idle",
            ["agentDraftRunId"] = args.DraftRunID,
            ["prompt"] = args.Prompt,
            ["composerContent"] = args.Prompt,
            ["referenceNodeIds"] = new JsonArray(
                args.ReferenceNodeIDs.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        };
        if (args.Size.Length > 0 && (args.Mode == "image" || args.Mode == "video"))
        {
            meta["size"] = args.Size;
        }
        if (args.Mode == "video")
        {
            meta["videoSeconds"] = args.Duration.ToString();
        }
        if (args.Quality.Length > 0)
        {
            meta[args.Mode == "video" ? "vquality" : "quality"] = args.Quality;
        }
        if (args.VideoGenerateAudio is not null)
        {
            meta["videoGenerateAudio"] = args.VideoGenerateAudio.Value ? "true" : "false";
        }
        Dictionary<string, JsonElement> inputConfig = new(StringComparer.Ordinal);
        if (task is not null)
        {
            meta["status"] = "loading";
            meta["taskId"] = task.ID;
            meta["taskStatus"] = "queued";
            meta.Remove("agentDraftRunId");
            if (!string.IsNullOrEmpty(task.InputJSON))
            {
                try
                {
                    Dictionary<string, JsonElement>? input = JsonSerializer.Deserialize<
                        Dictionary<string, JsonElement>>(task.InputJSON, OpenAICanvas.Domain.Serialization.GoJson.ReadOptions);
                    if (input is not null
                        && input.TryGetValue("config", out JsonElement configElement)
                        && configElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty property in configElement.EnumerateObject())
                        {
                            inputConfig[property.Name] = property.Value.Clone();
                        }
                    }
                }
                catch (JsonException cause)
                {
                    throw new InvalidOperationException(cause.Message, cause);
                }
            }
        }
        // 只持久化公开选择器与请求规格，绝不写入供应商凭证。
        foreach (string key in new[] { "size", "quality", "vquality", "videoSeconds", "videoGenerateAudio" })
        {
            if (inputConfig.TryGetValue(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                meta[key] = value.GetString() ?? "";
            }
        }
        if (args.Mode != "video")
        {
            meta.Remove("videoSeconds");
            meta.Remove("videoGenerateAudio");
        }
        if (args.LogicalModelID.Length > 0)
        {
            meta["logicalModelId"] = args.LogicalModelID;
        }
        else
        {
            meta["channelId"] = args.ChannelID;
            meta["channelModelKey"] = args.ChannelModelKey;
            meta["model"] = args.ChannelModelKey;
        }
        (CapabilityDescriptor descriptor, bool supported) = CloudAgentNodes.ForGenerationMode(args.Mode);
        if (!supported || !GenerationModeSupported(args.Mode))
        {
            throw AppError.BadAuthRequest("生成模式当前不受 Agent 支持");
        }
        JsonObject addedNode = CloudAgentMutations.AddedNode(args.NodeID, descriptor.Type, args.Title, x, y, meta);
        if (args.Size == "9:16")
        {
            addedNode["width"] = 360d;
            addedNode["height"] = 640d;
        }
        bool replaced = false;
        foreach (JsonObject existing in nodes)
        {
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(existing, "id")) == args.NodeID)
            {
                existing["metadata"] = meta.DeepClone();
                existing["title"] = args.Title;
                replaced = true;
                break;
            }
        }
        if (!replaced)
        {
            nodes.Add(addedNode);
        }
        doc["nodes"] = new JsonArray(nodes.Select(n => (JsonNode?)n.DeepClone()).ToArray());
        List<JsonObject> edges = MediaConnections(
            CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "connections")), args);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonObject edge in edges)
        {
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "toNodeId")) == args.NodeID)
            {
                seen.Add(CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "fromNodeId")));
            }
        }
        foreach (string id in args.ReferenceNodeIDs.Append(args.SourceNodeID))
        {
            if (id.Length == 0 || !seen.Add(id))
            {
                continue;
            }
            edges.Add(new JsonObject
            {
                ["id"] = "agent-" + IdGenerator.NewId(),
                ["fromNodeId"] = id,
                ["toNodeId"] = args.NodeID,
            });
        }
        doc["connections"] = new JsonArray(edges.Select(e => (JsonNode?)e.DeepClone()).ToArray());
        await CloudAgentMutations.SaveDocumentAsync(context, canvas, doc, policy).ConfigureAwait(false);
        if (recorder is not null)
        {
            string stepID = plan.CallID.Length > 0 ? plan.CallID : args.NodeID;
            string operation = task is not null ? "generate_media_submit" : "generate_media_draft";
            CloudAgentApprovalPreviewDto preview = ApprovalPreview(plan, "");
            await recorder(context, new CloudAgentMutationInput
            {
                RunID = args.DraftRunID,
                UserID = userID,
                CanvasID = canvasID,
                StepID = stepID,
                Operation = operation,
                BeforeSnapshotHash = beforeHash,
                AfterSnapshotHash = CloudAgentContracts.CanvasHash(doc),
                BeforeJSON = beforeJSON,
                HasSubmittedTask = task is not null,
                Preview = preview,
            }).ConfigureAwait(false);
        }
    }

    /// <summary>生成完成回写。对应 Go: <c>completeCloudAgentMediaNode</c>。返回节点 ID。</summary>
    public async Task<string> CompleteMediaNodeAsync(
        CloudAgentMutationContext context, string userID, string canvasID,
        TaskEntity task, RuntimePolicySetting policy)
    {
        CanvasProject? canvas = await context.CanvasProjectForUserAsync(userID, canvasID).ConfigureAwait(false)
            ?? throw AppError.NotFound("画布不存在");
        JsonObject doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
        foreach (JsonObject node in CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "nodes")))
        {
            JsonObject meta = node["metadata"] as JsonObject ?? new JsonObject();
            if (ElementString(meta, "taskId") != task.ID)
            {
                continue;
            }
            meta["taskStatus"] = task.Status;
            meta["status"] = "error";
            meta["errorDetails"] = "媒体任务" + task.Status + "：" + SafeMediaTaskError(task);
            if (task.Status == TaskStatus.TaskStatusSucceeded)
            {
                (string id, string _) = TaskOutputResource(task.ResultJSON, task.Type);
                Resource? resource = id.Length == 0
                    ? null
                    : await context.ResourceForUserAsync(userID, id).ConfigureAwait(false);
                if (resource is null || resource.Status != "ready"
                    || !resource.MimeType.StartsWith(
                        CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "type")) + "/",
                        StringComparison.Ordinal))
                {
                    meta["status"] = "error";
                    meta["errorDetails"] = "生成结果没有可用的账号资源，未写入媒体地址";
                    await CloudAgentMutations.SaveDocumentAsync(context, canvas, doc, policy).ConfigureAwait(false);
                    throw AppError.BadAuthRequest("生成结果没有可用的账号资源，未写入媒体地址");
                }
                meta["content"] = "/api/resources/" + id + "/file";
                meta["storageKey"] = "resource:" + id;
                meta["status"] = "success";
                meta["naturalWidth"] = resource.Width;
                meta["naturalHeight"] = resource.Height;
                if (resource.Width > 0 && resource.Height > 0
                    && node["width"]?.GetValueKind() == JsonValueKind.Number)
                {
                    double width = node["width"]!.GetValue<double>();
                    if (width > 0)
                    {
                        node["height"] = width * resource.Height / (double)resource.Width;
                    }
                }
                meta.Remove("errorDetails");
            }
            await CloudAgentMutations.SaveDocumentAsync(context, canvas, doc, policy).ConfigureAwait(false);
            return CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "id"));
        }
        throw CloudAgentSessionService.CreationConflict("生成节点已删除或已绑定其他任务；结果仍保留在任务中心，未重建节点");
    }

    /// <summary>任务结果中的资源引用。对应 Go: <c>taskOutputResource</c>。</summary>
    public static (string ResourceID, string MediaType) TaskOutputResource(string raw, string taskType)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ("", "");
        }
        JsonNode? value;
        try
        {
            value = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return ("", "");
        }
        string mediaType = taskType.ToLowerInvariant().Contains("video") ? "video" : "image";
        return FindTaskOutputResource(value, mediaType);
    }

    private static (string, string) FindTaskOutputResource(JsonNode? value, string mediaType)
    {
        switch (value)
        {
            case JsonArray array:
                foreach (JsonNode? child in array)
                {
                    (string id, string kind) = FindTaskOutputResource(child, mediaType);
                    if (id.Length > 0)
                    {
                        return (id, kind);
                    }
                }
                break;
            case JsonObject obj:
                foreach (string key in new[] { "resourceId", "storageKey", "url", "dataUrl", "resultUrl", "outputUrl" })
                {
                    if (CloudAgentJsonHelpers.Get(obj, key) is JsonValue v && v.TryGetValue<string>(out string? text))
                    {
                        text = text.Trim();
                        if (text.StartsWith("resource:", StringComparison.Ordinal))
                        {
                            return (text["resource:".Length..], mediaType);
                        }
                        string id = ResourceIDFromFileURL(text);
                        if (id.Length > 0)
                        {
                            return (id, mediaType);
                        }
                    }
                }
                foreach ((string _, JsonNode? child) in obj)
                {
                    (string id, string kind) = FindTaskOutputResource(child, mediaType);
                    if (id.Length > 0)
                    {
                        return (id, kind);
                    }
                }
                break;
        }
        return ("", "");
    }

    /// <summary>资源文件 URL 反解。对应 Go: <c>resourceIDFromFileURL</c>。</summary>
    private static string ResourceIDFromFileURL(string value)
    {
        const string marker = "/api/resources/";
        int index = value.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return "";
        }
        string rest = value[(index + marker.Length)..];
        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        string id = slash > 0 ? rest[..slash] : rest;
        return id.Length is 32 or 36 && id.All(c => Uri.IsHexDigit(c) || c == '-') ? id : "";
    }

    /// <summary>媒体任务安全诊断。对应 Go: <c>cloudAgentSafeMediaTaskError</c>。</summary>
    public static string SafeMediaTaskError(TaskEntity task)
    {
        string detail = task.Error.Trim();
        if (detail.Length == 0 || detail.Contains('\r') || detail.Contains('\n') || detail.Contains('\0'))
        {
            return "媒体任务未成功";
        }
        string lower = detail.ToLowerInvariant();
        foreach (string marker in new[]
                 {
                     "http://", "https://", "ftp://", "authorization", "cookie", "secret", "token",
                     "api_key", "apikey", "x-api-key",
                 })
        {
            if (lower.Contains(marker))
            {
                return "媒体任务未成功";
            }
        }
        List<char> runes = [.. detail];
        return runes.Count > 240 ? new string(runes.Take(240).ToArray()) + "…" : detail;
    }
}
