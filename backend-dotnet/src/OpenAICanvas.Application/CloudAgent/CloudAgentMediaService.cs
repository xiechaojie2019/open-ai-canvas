#nullable enable
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// <remarks>
/// <b>JSON 字段名以 Go 的 struct tag 为准，不能靠 C# 属性名的 camelCase 推断。</b>
/// <c>CloudAgentContracts.DecodeObject</c> 用 <c>UnmappedMemberHandling.Disallow</c>，
/// 下发给模型的 schema（<c>CloudAgentTools.BuildTools</c>）写的也是 Go 名字
/// （<c>durationSeconds</c> / <c>channelId</c> / <c>nodeId</c> …）。
/// 2026-10-10 线上事故：<c>Duration</c> 少写 <c>[JsonPropertyName("durationSeconds")]</c>，
/// 隐式绑成 <c>duration</c>，模型按 schema 传 <c>durationSeconds</c> 被当场拒收
/// ⇒ 「生成参数必须是只含支持字段的单个JSON对象」。
/// 视频必带 duration ⇒ 生视频 100% 失败；生图不带 duration ⇒ 侥幸能过。
/// <c>PropertyNameCaseInsensitive</c> 只能救大小写（channelId↔channelID），
/// 救不了 <c>durationSeconds</c> ↔ <c>duration</c> 这种词形差异。
/// </remarks>
public sealed class CloudAgentMediaArgs
{
    // json tag 与 Go 的 cloudAgentMediaArgs 逐字对齐：DecodeObject 开启
    // UnmappedMemberHandling.Disallow，缺标签的字段（如 durationSeconds）会被当作
    // 未知字段拒绝，视频生成因此全部挂在参数解码。
    [JsonPropertyName("draftRunID")]
    public string DraftRunID { get; set; } = "";
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";
    [JsonPropertyName("logicalModelId")]
    public string LogicalModelID { get; set; } = "";
    [JsonPropertyName("channelId")]
    public string ChannelID { get; set; } = "";
    [JsonPropertyName("channelModelKey")]
    public string ChannelModelKey { get; set; } = "";
    [JsonPropertyName("durationSeconds")]
    public int Duration { get; set; }
    [JsonPropertyName("size")]
    public string Size { get; set; } = "";
    [JsonPropertyName("quality")]
    public string Quality { get; set; } = "";
    [JsonPropertyName("videoGenerateAudio")]
    public bool? VideoGenerateAudio { get; set; }
    [JsonPropertyName("snapshotHash")]
    public string SnapshotHash { get; set; } = "";

    /// <summary>
    /// 生成依赖哈希（服务端在审批规划时计算并固化，模型不可见）。
    /// 对应 Go: <c>cloudAgentPreparedMedia.DependencyHash</c>——准入时依赖匹配可替代
    /// 全画布快照匹配，使同画布的其他生成回写不致连坐判"画布已变化"。
    /// </summary>
    [JsonPropertyName("dependencyHash")]
    public string DependencyHash { get; set; } = "";
    [JsonPropertyName("nodeId")]
    public string NodeID { get; set; } = "";
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";
    [JsonPropertyName("sourceNodeId")]
    public string SourceNodeID { get; set; } = "";
    [JsonPropertyName("referenceNodeIds")]
    public List<string> ReferenceNodeIDs { get; set; } = [];

    /// <summary>
    /// 本次要出的产物数量（只有图片生成支持 &gt;1）。
    /// ⚠️ 本 fork 扩展，<b>Go 的 <c>cloudAgentMediaArgs</c> 没有这个字段</b>：
    /// Go 一次调用只出一个产物。这里由服务端展开成 <b>N 个任务</b>，
    /// 与前端「直接图片生成选 N 张」的行为对齐（<c>canvas-image-generation-executor.ts</c>）。
    /// <para>
    /// ⚠️ 2026-10-13 起这 N 个产物<b>共用同一个结果节点</b>（同源归并，见
    /// <see cref="CloudAgentMediaAggregation"/>），不再派生 <c>nodeId-1…-N</c>。
    /// </para>
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>
    /// 分批提示词：<c>prompts[k]</c> 是第 k+1 张图独立、完整的提示词。
    /// ⚠️ 本 fork 扩展，<b>Go 没有</b>。
    /// <para>
    /// 为什么需要它：若 <c>count&gt;1</c> 时 N 张图共用同一段 prompt，而那段 prompt
    /// 本身描述的是「一组多张」（如「本组共三张：第1张…第2张…第3张…」），
    /// 每次调用都会把那组描述整体画进单张图 ⇒ **每张都是拼贴图**。
    /// 正解是让模型把多张拆成 N 段互不相同的提示词后逐张下发。
    /// </para>
    /// <para>
    /// 缺省（空列表）时回落到 <see cref="Prompt"/>（旧行为，向后兼容、旧 run 状态仍可解码）。
    /// </para>
    /// </summary>
    [JsonPropertyName("prompts")]
    public List<string> Prompts { get; set; } = [];
}

/// <summary>媒体计划。对应 Go: <c>app.cloudAgentMediaPlan</c>。</summary>
/// <remarks>
/// <see cref="Items"/> 恒有 ≥1 项：count==1 时就是 1 项（与 Go 完全一致），
/// count&gt;1 时是服务端展开的 N 项，且各项共享同一个 <c>NodeID</c>
/// （2026-10-13 起同源归并到一个节点，见 <see cref="CloudAgentMediaAggregation"/>）。
/// 所有既有调用点用的 <see cref="Args"/> 即首项，因此单产物路径行为不变。
/// </remarks>
public sealed class CloudAgentMediaPlan
{
    public List<CloudAgentMediaArgs> Items { get; init; } = [];
    public string CallID { get; init; } = "";

    /// <summary>首项（单产物路径等价于「那个」参数集）。</summary>
    public CloudAgentMediaArgs Args => Items[0];

    /// <summary>是否是一次出多个产物的批量计划。</summary>
    public bool IsBatch => Items.Count > 1;
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

    /// <summary>一次 generate_media 最多出多少个图片产物。本 fork 扩展（Go 恒为 1）。</summary>
    public const int MaxBatchCount = 8;

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

    /// <summary>生成合同涉及的元数据键。对应 Go: <c>contract.NodeGenerationProjection</c> 的已知键集合。</summary>
    private static readonly string[] GenerationKnownMetadataKeys =
    {
        "generationSpec", "prompt", "composerContent", "model", "logicalModelId", "channelId",
        "channelModelKey", "videoStartFrameNodeId", "videoEndFrameNodeId", "videoEditOperation",
        "referenceNodeIds",
    };

    /// <summary>资源身份相关元数据键。对应 Go: <c>cloudAgentMediaDependencyHash</c> 的 resourceProjection。</summary>
    private static readonly string[] ResourceProjectionKeys =
    {
        "storageKey", "assetId", "resourceId", "generationTaskId", "outputReference", "status",
        "mimeType", "naturalWidth", "naturalHeight", "width", "height", "durationMs", "etag",
    };

    /// <summary>
    /// 生成依赖哈希：只投影本次生成真正依赖的图结构（目标节点 + 参考绑定 + 来源 + 入边）。
    /// 对应 Go: <c>cloudAgentMediaDependencyHash</c>（version 2）。画布呈现与自动保存簿记、
    /// 以及无关节点的任何变化都不会改变该哈希，使同画布的其他生成互不连坐。
    /// </summary>
    public static string MediaDependencyHash(JsonObject doc, CloudAgentMediaArgs args)
    {
        Dictionary<string, JsonObject> nodes = new(StringComparer.Ordinal);
        foreach (JsonObject node in CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "nodes")))
        {
            string id = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "id"));
            if (id.Length > 0)
            {
                nodes[id] = node;
            }
        }

        JsonObject? TargetInput(JsonObject node)
        {
            JsonObject input = new();
            if (node["metadata"] is not JsonObject metadata)
            {
                return input;
            }
            foreach ((string key, JsonNode? value) in metadata)
            {
                if (Array.IndexOf(GenerationKnownMetadataKeys, key) >= 0
                    || key is "locked" or "taskId" or "generationTaskId" or "agentDraftRunId" or "status")
                {
                    input[key] = value?.DeepClone();
                }
            }
            return input;
        }

        JsonObject? ResourceProjection(JsonObject? node)
        {
            if (node?["metadata"] is not JsonObject metadata)
            {
                return null;
            }
            JsonObject resource = new();
            foreach (string key in ResourceProjectionKeys)
            {
                if (metadata.TryGetPropertyValue(key, out JsonNode? value))
                {
                    resource[key] = value?.DeepClone();
                }
            }
            return resource;
        }

        JsonObject? target = nodes.GetValueOrDefault(args.NodeID);
        JsonObject? targetInput = null;
        if (target is not null)
        {
            targetInput = new JsonObject
            {
                ["id"] = CloudAgentJsonHelpers.Get(target, "id")?.DeepClone(),
                ["type"] = CloudAgentJsonHelpers.Get(target, "type")?.DeepClone(),
                ["locked"] = CloudAgentJsonHelpers.Get(target, "locked")?.DeepClone(),
                ["input"] = TargetInput(target),
                ["resource"] = ResourceProjection(target),
            };
        }
        JsonArray bindings = [];
        foreach (string id in args.ReferenceNodeIDs)
        {
            JsonObject binding = new()
            {
                ["id"] = id,
                ["exists"] = nodes.ContainsKey(id),
            };
            if (nodes.TryGetValue(id, out JsonObject? reference))
            {
                binding["type"] = CloudAgentJsonHelpers.Get(reference, "type")?.DeepClone();
                binding["input"] = TargetInput(reference);
                binding["resource"] = ResourceProjection(reference);
            }
            bindings.Add(binding);
        }
        JsonNode? source = null;
        if (args.SourceNodeID.Length > 0)
        {
            JsonObject? sourceNode = nodes.GetValueOrDefault(args.SourceNodeID);
            source = new JsonObject
            {
                ["id"] = args.SourceNodeID,
                ["type"] = sourceNode is null
                    ? null
                    : CloudAgentJsonHelpers.Get(sourceNode, "type")?.DeepClone(),
                ["content"] = sourceNode?["metadata"]?["content"]?.DeepClone(),
                ["prompt"] = sourceNode?["metadata"]?["prompt"]?.DeepClone(),
            };
        }
        JsonArray edges = [];
        foreach (JsonObject edge in CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "connections")))
        {
            if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(edge, "toNodeId")) == args.NodeID)
            {
                edges.Add(new JsonObject
                {
                    ["fromNodeId"] = CloudAgentJsonHelpers.Get(edge, "fromNodeId")?.DeepClone(),
                    ["toNodeId"] = CloudAgentJsonHelpers.Get(edge, "toNodeId")?.DeepClone(),
                    ["fromPort"] = CloudAgentJsonHelpers.Get(edge, "fromPort")?.DeepClone(),
                    ["role"] = CloudAgentJsonHelpers.Get(edge, "role")?.DeepClone(),
                });
            }
        }
        JsonObject content = new()
        {
            ["version"] = 2,
            ["target"] = targetInput,
            ["bindings"] = bindings,
            ["source"] = source,
            ["edges"] = edges,
        };
        return CloudAgentContracts.CreationHashOf(content);
    }

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
        if (!unchanged && args.DependencyHash.Length > 0)
        {
            // 对应 Go: Prepared != nil 时用依赖哈希替代全画布快照——只要求本次生成
            // 真正依赖的节点（目标 + 参考 + 来源 + 入边）没有变化。
            unchanged = MediaDependencyHash(doc, args) == args.DependencyHash;
        }
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
        // ⚠️ 2026-10-14：Agent 路径不再做「同源归并」——一次生成多张 = 每张一个独立节点，
        // 与手动「直接图片生成选 N 张」的行为对齐（用户要求：agent 不要合并）。
        // 因此这里恢复「目标必须是从未提交过的草稿」的完整准入，不再放行成品节点复用。
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
        if (args.Count < 0)
        {
            throw AppError.BadAuthRequest("生成数量不能为负数");
        }
        if (args.Count > MaxBatchCount)
        {
            throw AppError.BadAuthRequest($"一次最多生成 {MaxBatchCount} 个产物");
        }
        if (args.Count > 1 && mode != "image")
        {
            throw AppError.BadAuthRequest("只有图片生成支持一次出多张；视频与音频请固定为 1");
        }
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
        // prompts：逐张独立提示词。给了就必须与 count 等长，否则服务端不知道该把哪段发给谁。
        if (args.Prompts.Count > 0)
        {
            if (args.Count <= 1 || mode != "image")
            {
                throw AppError.BadAuthRequest("prompts 仅在 mode=image 且 count>1 时使用；单张生成请直接写 prompt");
            }
            if (args.Prompts.Count != args.Count)
            {
                throw AppError.BadAuthRequest(
                    $"prompts 段数（{args.Prompts.Count}）必须与 count（{args.Count}）一致，每张图各一段完整提示词");
            }
            foreach (string item in args.Prompts)
            {
                if (item.Trim().Length == 0)
                {
                    throw AppError.BadAuthRequest("prompts 里不能有空段，每张图都要有完整提示词");
                }
                if (item.EnumerateRunes().Count() > 16000)
                {
                    throw AppError.BadAuthRequest(
                        $"prompts 中某段提示词超过16000字符上限；请先告知用户，不要擅自删改关键内容");
                }
            }
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
    /// <remarks>
    /// count&gt;1 时返回 N 个请求（每项的 <c>config.count</c> 固定为 "1"——服务端硬约束
    /// 「每个获批执行项只能生成一个产物」），<c>Plan.Items[k]</c> 与 <c>Requests[k]</c> 一一对应。
    /// count==1 时与 Go 完全同形：1 个请求、1 项 Items。
    /// </remarks>
    public async Task<(List<CreateTaskRequestDto> Requests, CloudAgentMediaPlan Plan)> PrepareAsync(
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
        return await PrepareCoreAsync(context, run, state, args, call.ID, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 按已固化参数重走校验与请求组装。批量路径专用：参数存在
    /// <see cref="CloudAgentRuntimeDto.MediaBatch"/> 里，不再是 <c>call.Function.Arguments</c>。
    /// </summary>
    public Task<(List<CreateTaskRequestDto> Requests, CloudAgentMediaPlan Plan)> PrepareItemAsync(
        CloudAgentMutationContext context, CloudAgentExecution run, CloudAgentRuntimeDto state,
        CloudAgentMediaArgs args, string callID, CancellationToken cancellationToken) =>
        PrepareCoreAsync(context, run, state, args, callID, cancellationToken);

    private async Task<(List<CreateTaskRequestDto> Requests, CloudAgentMediaPlan Plan)> PrepareCoreAsync(
        CloudAgentMutationContext context, CloudAgentExecution run, CloudAgentRuntimeDto state,
        CloudAgentMediaArgs args, string callID, CancellationToken cancellationToken)
    {
        args.Mode = args.Mode.Trim().ToLowerInvariant();
        args.DraftRunID = run.ID;
        if (args.Count <= 0)
        {
            args.Count = 1; // 未传 count 等价于 1，单产物语义与 Go 一致。
        }
        ValidateArgs(args, state);
        if ((args.LogicalModelID.Length == 0 && (args.ChannelID.Length == 0 || args.ChannelModelKey.Length == 0))
            || (args.LogicalModelID.Length > 0 && (args.ChannelID.Length > 0 || args.ChannelModelKey.Length > 0)))
        {
            throw AppError.BadAuthRequest("请复制 model_list 的 selection：逻辑模型或系统渠道二选一，不得混用");
        }
        EnsureBatchBudget(args, state);
        (_, _, Dictionary<string, List<JsonObject>> refs) = await MediaDocumentAsync(
            context, run.UserID, state.Request.CanvasID, args, cancellationToken).ConfigureAwait(false);
        ValidateReferences(args.Mode, refs);
        string operation = Operation(args.Mode, refs);
        if (operation.Length == 0)
        {
            throw AppError.BadAuthRequest("生成模式尚未实现媒体任务适配器");
        }
        // 一次生成多张时每个产物一个独立节点（派生 "-1/-2/…"）：与手动「直接图片生成选 N 张」
        // 的行为一致，N 张各自成节点、可分别连线，不再塞进同一个节点的 mediaHistory 里。
        List<string> nodeIDs = BatchNodeIDs(args);
        List<CloudAgentMediaArgs> items = new(nodeIDs.Count);
        List<CreateTaskRequestDto> requests = new(nodeIDs.Count);
        for (int index = 0; index < nodeIDs.Count; index++)
        {
            CloudAgentMediaArgs item = CloneMediaArgs(args, nodeIDs[index], index, nodeIDs.Count);
            items.Add(item);
            requests.Add(BuildMediaRequest(state, item, refs, operation));
        }
        return (requests, new CloudAgentMediaPlan { Items = items, CallID = callID });
    }

    /// <summary>把一次调用要出的每个产物映射到自己的节点 ID。count==1 时原样返回模型给的 ID。</summary>
    /// <remarks>
    /// 2026-10-14 恢复：Agent 一次出多张改为「每张一个独立节点」，不再共用同一节点。
    /// 派生而不是服务端自造随机 ID：模型才能在后续轮次里按 ID 指认/复用这些草稿。
    /// </remarks>
    internal static List<string> BatchNodeIDs(CloudAgentMediaArgs args)
    {
        if (args.Count <= 1)
        {
            return [args.NodeID];
        }
        if (args.NodeID.Length + 3 > 80)
        {
            throw AppError.BadAuthRequest(
                "一次生成多张时 nodeId 不能超过 77 个字符（服务端要追加 -1、-2 …）");
        }
        List<string> ids = new(args.Count);
        for (int index = 1; index <= args.Count; index++)
        {
            string id = args.NodeID + "-" + index.ToString(CultureInfo.InvariantCulture);
            CloudAgentContracts.ValidateCloudAgentID(id, "生成节点 ID", 80);
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>批量产物数是否超出本轮预算。必须在建草稿前拦，避免建了节点却提交不了。</summary>
    internal static void EnsureBatchBudget(CloudAgentMediaArgs args, CloudAgentRuntimeDto state)
    {
        if (state.Request.Budget.MaxGenerationTasks > 0
            && state.Generations + args.Count > state.Request.Budget.MaxGenerationTasks)
        {
            int remaining = Math.Max(0, state.Request.Budget.MaxGenerationTasks - state.Generations);
            throw AppError.BadAuthRequest(
                $"本轮剩余生成次数不足：还可生成 {remaining} 个，本次请求 {args.Count} 个");
        }
        if (args.Mode == "video" && state.Request.Budget.MaxVideoSeconds > 0
            && state.VideoSeconds + args.Count * args.Duration > state.Request.Budget.MaxVideoSeconds)
        {
            throw AppError.BadAuthRequest("已超过本轮视频时长预算");
        }
    }

    /// <summary>
    /// 派生单个产物的参数副本：换节点 ID/标题/提示词，其余（含读时快照）沿用父参数。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>提示词必须逐项不同</b>：批量时若 N 张共用同一段 prompt，而那段 prompt 描述的是
    /// 「一组多张」（如「本组共三张：第1张…第2张…第3张」），每次调用都会把整组画进单张图
    /// ⇒ 拼贴图。因此当 <c>prompts.Count == total</c> 时取对应那一段；否则（旧调用/旧 run 状态）
    /// 回落父 prompt，行为与改动前完全一致。
    /// </remarks>
    internal static CloudAgentMediaArgs CloneMediaArgs(
        CloudAgentMediaArgs source, string nodeID, int index, int total)
    {
        // 一次出多张时每张各自成节点，标题追加「 1/2/3」序号以区分（2026-10-14 起不再共用节点）。
        string title = source.Title.Trim();
        if (total > 1)
        {
            string suffix = " " + (index + 1).ToString(CultureInfo.InvariantCulture);
            title = CloudAgentContracts.TruncateRunes(title, 240 - suffix.Length) + suffix;
        }
        string prompt = source.Prompts.Count == total ? source.Prompts[index] : source.Prompt;
        return new CloudAgentMediaArgs
        {
            DraftRunID = source.DraftRunID,
            Mode = source.Mode,
            Prompt = prompt,
            LogicalModelID = source.LogicalModelID,
            ChannelID = source.ChannelID,
            ChannelModelKey = source.ChannelModelKey,
            Duration = source.Duration,
            Size = source.Size,
            Quality = source.Quality,
            VideoGenerateAudio = source.VideoGenerateAudio,
            // 读时快照先原样带过来（草稿准入要校验它），SnapshotHash/DependencyHash
            // 由 BakeMediaDraftAsync 在建完草稿后逐项重算覆盖。
            SnapshotHash = source.SnapshotHash,
            NodeID = nodeID,
            Title = title,
            SourceNodeID = source.SourceNodeID,
            ReferenceNodeIDs = [.. source.ReferenceNodeIDs],
            Count = 1,
        };
    }

    private static CreateTaskRequestDto BuildMediaRequest(
        CloudAgentRuntimeDto state, CloudAgentMediaArgs args,
        IReadOnlyDictionary<string, List<JsonObject>> refs, string operation)
    {
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
        return new CreateTaskRequestDto
        {
            ProjectID = state.Request.CanvasID,
            Type = "canvas_" + args.Mode,
            Operation = operation,
            Prompt = args.Prompt,
            LogicalModelID = args.LogicalModelID,
            Model = args.ChannelModelKey,
            Input = input,
        };
    }

    /// <summary>
    /// 媒体审批预览。对应 Go: <c>cloudAgentMediaApprovalPreview</c>。
    /// count&gt;1（本 fork 扩展）时按节点逐项列出，用户一张卡授权整批。
    /// </summary>
    public static CloudAgentApprovalPreviewDto ApprovalPreview(CloudAgentMediaPlan plan, string modelName)
    {
        CloudAgentMediaArgs args = plan.Args;
        (CapabilityDescriptor descriptor, bool _) = CloudAgentNodes.ForGenerationMode(args.Mode);
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
        // 批量且逐张提示词不同时点明，否则用户看到同一套 details 会以为 N 张是同一张。
        if (plan.IsBatch && plan.Items.Select(item => item.Prompt).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            details.Add("每张提示词各不相同");
        }
        List<CloudAgentApprovalPreviewItemDto> items = new(plan.Items.Count);
        foreach (CloudAgentMediaArgs item in plan.Items)
        {
            string nodeTitle = CloudAgentContracts.TruncateRunes(item.Title.Trim(), 120);
            if (nodeTitle.Length == 0)
            {
                nodeTitle = "未命名" + descriptor.Label;
            }
            items.Add(new CloudAgentApprovalPreviewItemDto
            {
                Operation = "generate_media",
                NodeID = item.NodeID,
                NodeTitle = nodeTitle,
                NodeType = descriptor.Type,
                NodeTypeLabel = descriptor.Label,
                Details = details,
                Summary = "生成" + descriptor.Label + "《" + nodeTitle + "》",
            });
        }
        string prefix = plannerCountPrefix(plan, descriptor);
        return new CloudAgentApprovalPreviewDto
        {
            Kind = "media_generation",
            Title = "确认生成" + descriptor.Label + (plan.IsBatch ? $" × {plan.Items.Count}" : ""),
            Description = prefix + descriptor.Label
                + "草稿节点和引用连线已创建，尚未提交生成。确认规格后批准才会提交收费任务；拒绝则保留草稿，结果自动回写画布。",
            Items = items,
        };

        static string plannerCountPrefix(CloudAgentMediaPlan plan, CapabilityDescriptor descriptor) =>
            plan.IsBatch ? $"本次将生成 {plan.Items.Count} 个{descriptor.Label}，" : "";
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

    /// <summary>
    /// Agent 新建媒体节点的落点：与前端图片生成同一套规则。
    /// 起点是「来源节点右侧 + 120px、纵向对齐中心」；与既有节点冲突时（横向 ≥120px、
    /// 纵向 ≥48px 安全间距）先向右逐列平移，一行扫完再整体下移一行。
    /// 只决定新节点位置，绝不移动既有节点。
    /// </summary>
    /// <remarks>
    /// ⚠️ 2026-10-19：原实现取「全画布所有节点的最右边缘 + 80」，画布上只要有靠右的节点
    /// （哪怕很远），Agent 生成的图就会飞到那一带，用户反馈「生成位置偏得很远」。现改为
    /// 以来源节点为锚的就近空位，行为对齐 <c>canvas-generation-layout.ts</c>。
    /// </remarks>
    private static (double X, double Y) ResolveMediaNodePlacement(
        IReadOnlyList<JsonObject> nodes, string sourceNodeID, IReadOnlyList<string> referenceNodeIDs, string mode, string size)
    {
        const double gapX = 120;
        (CapabilityDescriptor descriptor, bool known) = CloudAgentNodes.ForGenerationMode(mode);
        double width = known ? descriptor.DefaultWidth : 340;
        double height = known ? descriptor.DefaultHeight : 240;
        if (size == "9:16")
        {
            width = 360;
            height = 640;
        }

        double sourceX = 0, sourceY = 0, sourceWidth = width, sourceHeight = height;
        bool sourceFound = false;
        // ⚠️ 2026-10-19：锚点必须覆盖「只有参考图、没有来源节点」的场景。
        // 图生图（image_to_image）时模型只传 referenceNodeIds、sourceNodeId 为空，
        // 原实现找不到锚点就退回 (80,80)，于是结果节点被放到画布右上角，
        // 与真正的参考图节点相距上万像素（线上实测：源在 x=-10330，结果落在 x=920）。
        // 优先级：来源节点 → 第一个参考图节点（参考图通常就是用户盯着的那张）。
        foreach (string anchorID in AnchorCandidates(sourceNodeID, referenceNodeIDs))
        {
            foreach (JsonObject node in nodes)
            {
                if (CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "id")) != anchorID)
                {
                    continue;
                }
                JsonObject? sourcePosition = node["position"] as JsonObject;
                sourceX = CloudAgentMutations.NumberField(sourcePosition, "x");
                sourceY = CloudAgentMutations.NumberField(sourcePosition, "y");
                sourceWidth = CloudAgentMutations.NumberField(node, "width") <= 0
                    ? sourceWidth
                    : CloudAgentMutations.NumberField(node, "width");
                sourceHeight = CloudAgentMutations.NumberField(node, "height") <= 0
                    ? sourceHeight
                    : CloudAgentMutations.NumberField(node, "height");
                sourceFound = true;
                break;
            }
            if (sourceFound)
            {
                break;
            }
        }

        double baseX = sourceFound ? sourceX + sourceWidth + gapX : 80;
        double baseY = sourceFound ? sourceY + sourceHeight / 2 - height / 2 : 80;
        return CloudAgentMutations.NearestFreeSlot(nodes, baseX, baseY, width, height);
    }

    /// <summary>落点锚点候选：来源节点优先，其次第一个参考图节点。</summary>
    private static IEnumerable<string> AnchorCandidates(string sourceNodeID, IReadOnlyList<string> referenceNodeIDs)
    {
        if (sourceNodeID.Length > 0)
        {
            yield return sourceNodeID;
        }
        foreach (string referenceID in referenceNodeIDs)
        {
            if (referenceID.Length > 0 && referenceID != sourceNodeID)
            {
                yield return referenceID;
            }
        }
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
        (double x, double y) = ResolveMediaNodePlacement(nodes, args.SourceNodeID, args.ReferenceNodeIDs, args.Mode, args.Size);
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
        // ⚠️ 2026-10-14：Agent 建的节点**不再**打 generationSourceKey。
        // 该标记是给前端「直接图片生成」找同源归并目标用的；打上之后，手动生成会把结果
        // 复用到 Agent 建的节点上（线上表现：「在手动节点上发任务，结果跑到 Agent 图片上」）。
        // Agent 侧本身已不做同源归并（一次多张 = 每张独立节点），因此这里不再需要这个标记。
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
                // 归并复用：整段替换 metadata 会把展示中的图和历史一起抹掉，
                // 所以先把该留的字段（产物 + mediaHistory + 任务账本）搬过来再覆盖。
                JsonObject previous = existing["metadata"] as JsonObject ?? new JsonObject();
                CloudAgentMediaAggregation.PreserveAcrossRewrite(previous, meta, task?.ID ?? "");
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
            // 归并后一个节点会先后承接多个任务，完成回写必须按任务账本匹配，不能只看 taskId
            // （后者只保留最新一次提交的那一个）。
            if (!CloudAgentMediaAggregation.OwnsTask(meta, task.ID))
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
                // 先把展示中的旧产物压进历史，再写新结果：节点始终展示最新，历史保留旧图。
                CloudAgentMediaAggregation.PushCurrentMediaToHistory(
                    meta, CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "title")));
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
