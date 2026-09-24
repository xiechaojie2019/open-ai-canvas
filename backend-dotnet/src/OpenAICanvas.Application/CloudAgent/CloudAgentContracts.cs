#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Canvas.Capability;
using OpenAICanvas.Domain.Entities;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;
using System.Text.Json.Nodes;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>一轮 Agent 运行的创建请求。对应 Go: <c>app.CloudAgentRequest</c>。</summary>
/// <remarks>字段顺序与 Go struct 声明一致——指纹哈希依赖该顺序。</remarks>
public sealed class CloudAgentRequestDto
{
    [JsonPropertyName("reasoningMode")]
    [GoOmitEmpty]
    public string ReasoningMode { get; set; } = "";

    [JsonPropertyName("profileRevision")]
    [GoOmitEmpty]
    public string ProfileRevision { get; set; } = "";

    [JsonPropertyName("canvasId")]
    public string CanvasID { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("model")]
    [GoOmitEmpty]
    public string Model { get; set; } = "";

    [JsonPropertyName("logicalModelId")]
    [GoOmitEmpty]
    public string LogicalModelID { get; set; } = "";

    [JsonPropertyName("channelId")]
    [GoOmitEmpty]
    public string ChannelID { get; set; } = "";

    [JsonPropertyName("channelModelKey")]
    [GoOmitEmpty]
    public string ChannelModelKey { get; set; } = "";

    [JsonPropertyName("permissionMode")]
    public string PermissionMode { get; set; } = "";

    [JsonPropertyName("skillIds")]
    [GoOmitEmpty]
    public List<string> SkillIDs { get; set; } = [];

    [JsonPropertyName("contextScope")]
    public List<string> ContextScope { get; set; } = [];

    [JsonPropertyName("budget")]
    public CloudAgentBudgetDto Budget { get; set; } = new();

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; set; } = "";
}

public sealed class CloudAgentBudgetDto
{
    [JsonPropertyName("maxCredits")]
    public double MaxCredits { get; set; }

    [JsonPropertyName("maxGenerationTasks")]
    [GoOmitEmpty]
    public int MaxGenerationTasks { get; set; }

    [JsonPropertyName("maxVideoSeconds")]
    [GoOmitEmpty]
    public int MaxVideoSeconds { get; set; }
}

/// <summary>服务端编译产物。对应 Go: <c>app.cloudAgentPolicySnapshot</c>。</summary>
public sealed class CloudAgentPolicySnapshotDto
{
    [JsonPropertyName("systemPolicyId")]
    public string SystemPolicyID { get; set; } = "";

    [JsonPropertyName("systemPolicyVersion")]
    public int SystemPolicyVersion { get; set; }

    [JsonPropertyName("systemPolicyHash")]
    public string SystemPolicyHash { get; set; } = "";

    [JsonPropertyName("mediaPolicyId")]
    public string MediaPolicyID { get; set; } = "";

    [JsonPropertyName("mediaPolicyVersion")]
    public int MediaPolicyVersion { get; set; }

    [JsonPropertyName("mediaPolicyHash")]
    public string MediaPolicyHash { get; set; } = "";

    [JsonPropertyName("capabilitySetVersion")]
    public string CapabilitySetVersion { get; set; } = "";

    [JsonPropertyName("capabilitySetHash")]
    public string CapabilitySetHash { get; set; } = "";

    [JsonPropertyName("reasoningMode")]
    public string ReasoningMode { get; set; } = "";

    [JsonPropertyName("compilerVersion")]
    public string CompilerVersion { get; set; } = "";

    [JsonPropertyName("profileRevision")]
    [GoOmitEmpty]
    public string ProfileRevision { get; set; } = "";

    [JsonPropertyName("profileHash")]
    [GoOmitEmpty]
    public string ProfileHash { get; set; } = "";
}

/// <summary>对应 Go: <c>app.cloudAgentProfileSnapshot</c>。</summary>
public sealed class CloudAgentProfileSnapshotDto
{
    [JsonPropertyName("revision")]
    public string Revision { get; set; } = "";

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("layers")]
    public List<AgentProfileLayerDto> Layers { get; set; } = [];
}

/// <summary>对应 Go: <c>app.cloudAgentSkill</c>。</summary>
public sealed class CloudAgentSkillDto
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("instruction")]
    [GoOmitEmpty]
    public string Instruction { get; set; } = "";

    [JsonPropertyName("files")]
    [GoOmitEmpty]
    public Dictionary<string, string>? Files { get; set; }
}

/// <summary>创作任务锚点。对应 Go: <c>app.cloudAgentCreativeAnchor</c>。</summary>
public sealed class CloudAgentCreativeAnchorDto
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("userPrompt")]
    public string UserPrompt { get; set; } = "";

    [JsonPropertyName("referenceMode")]
    [GoOmitEmpty]
    public string ReferenceMode { get; set; } = "";

    [JsonPropertyName("referenceNodeIds")]
    [GoOmitEmpty]
    public List<string>? ReferenceNodeIDs { get; set; }

    [JsonPropertyName("referenceAssets")]
    [GoOmitEmpty]
    public List<CloudAgentReferenceAnchorDto>? ReferenceAssets { get; set; }

    [JsonPropertyName("lockedRequirements")]
    [GoOmitEmpty]
    public List<string>? LockedRequirements { get; set; }

    [JsonPropertyName("freelyDecidable")]
    [GoOmitEmpty]
    public List<string>? FreelyDecidable { get; set; }
}

public sealed class CloudAgentReferenceAnchorDto
{
    [JsonPropertyName("nodeId")]
    public string NodeID { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("title")]
    [GoOmitEmpty]
    public string Title { get; set; } = "";

    [JsonPropertyName("prompt")]
    [GoOmitEmpty]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("assetTags")]
    [GoOmitEmpty]
    public List<string>? AssetTags { get; set; }

    [JsonPropertyName("referenceReady")]
    public bool ReferenceReady { get; set; }

    [JsonPropertyName("visualIdentity")]
    public string VisualIdentity { get; set; } = "";

    [JsonPropertyName("requiresVisualInspection")]
    public bool RequiresVisualInspection { get; set; }

    [JsonPropertyName("width")]
    [GoOmitEmpty]
    public JsonElement? Width { get; set; }

    [JsonPropertyName("height")]
    [GoOmitEmpty]
    public JsonElement? Height { get; set; }
}

/// <summary>任务 InputJSON 里的 Agent 状态。对应 Go: <c>app.cloudAgentState</c>。</summary>
public sealed class CloudAgentStateDto
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("request")]
    public CloudAgentRequestDto Request { get; set; } = new();

    [JsonPropertyName("parentId")]
    public string ParentID { get; set; } = "";

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = "";

    [JsonPropertyName("creativeAnchor")]
    [GoOmitEmpty]
    public CloudAgentCreativeAnchorDto? CreativeAnchor { get; set; }

    [JsonPropertyName("skills")]
    [GoOmitEmpty]
    public List<CloudAgentSkillDto>? Skills { get; set; }

    [JsonPropertyName("profile")]
    public CloudAgentProfileSnapshotDto Profile { get; set; } = new();

    [JsonPropertyName("policy")]
    public CloudAgentPolicySnapshotDto Policy { get; set; } = new();
}

/// <summary>工具调用。对应 Go: <c>app.cloudAgentCall</c>。</summary>
public sealed class CloudAgentCallDto
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("function")]
    public CloudAgentCallFunctionDto Function { get; set; } = new();
}

public sealed class CloudAgentCallFunctionDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "";
}

/// <summary>审批预览条目。对应 Go: <c>app.cloudAgentApprovalPreviewItem</c>。</summary>
public sealed class CloudAgentApprovalPreviewItemDto
{
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "";

    [JsonPropertyName("nodeId")]
    [GoOmitEmpty]
    public string NodeID { get; set; } = "";

    [JsonPropertyName("nodeTitle")]
    [GoOmitEmpty]
    public string NodeTitle { get; set; } = "";

    [JsonPropertyName("resultTitle")]
    [GoOmitEmpty]
    public string ResultTitle { get; set; } = "";

    [JsonPropertyName("nodeType")]
    [GoOmitEmpty]
    public string NodeType { get; set; } = "";

    [JsonPropertyName("nodeTypeLabel")]
    [GoOmitEmpty]
    public string NodeTypeLabel { get; set; } = "";

    [JsonPropertyName("targetNodeId")]
    [GoOmitEmpty]
    public string TargetNodeID { get; set; } = "";

    [JsonPropertyName("targetNodeTitle")]
    [GoOmitEmpty]
    public string TargetNodeTitle { get; set; } = "";

    [JsonPropertyName("targetNodeType")]
    [GoOmitEmpty]
    public string TargetNodeType { get; set; } = "";

    [JsonPropertyName("fields")]
    [GoOmitEmpty]
    public List<string>? Fields { get; set; }

    [JsonPropertyName("details")]
    [GoOmitEmpty]
    public List<string>? Details { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";
}

/// <summary>审批预览。对应 Go: <c>app.cloudAgentApprovalPreview</c>。</summary>
public sealed class CloudAgentApprovalPreviewDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("items")]
    public List<CloudAgentApprovalPreviewItemDto> Items { get; set; } = [];
}

/// <summary>待审批调用。对应 Go: <c>app.cloudAgentApproval</c>。</summary>
public sealed class CloudAgentApprovalDto
{
    [JsonPropertyName("modelName")]
    [GoOmitEmpty]
    public string ModelName { get; set; } = "";

    [JsonPropertyName("approvalId")]
    public string ID { get; set; } = "";

    [JsonPropertyName("call")]
    public CloudAgentCallDto Call { get; set; } = new();

    [JsonPropertyName("callHash")]
    [GoOmitEmpty]
    public string CallHash { get; set; } = "";

    [JsonPropertyName("preview")]
    public CloudAgentApprovalPreviewDto Preview { get; set; } = new();

    [JsonPropertyName("decision")]
    [GoOmitEmpty]
    public string Decision { get; set; } = "";

    [JsonPropertyName("reason")]
    [GoOmitEmpty]
    public string Reason { get; set; } = "";
}

/// <summary>Agent 事件。对应 Go: <c>app.CloudAgentEvent</c>。</summary>
public sealed class CloudAgentEventDto
{
    [JsonPropertyName("eventId")]
    public string EventID { get; set; } = "";

    [JsonPropertyName("runId")]
    public string RunID { get; set; } = "";

    [JsonPropertyName("seq")]
    public int Seq { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("payload")]
    public Dictionary<string, JsonElement> Payload { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>持久执行状态（cloud_agent_executions.state_json）。对应 Go: <c>app.cloudAgentRuntime</c>。</summary>
/// <remarks>字段顺序与 Go struct 声明一致。</remarks>
public sealed class CloudAgentRuntimeDto
{
    [JsonPropertyName("request")]
    public CloudAgentRequestDto Request { get; set; } = new();

    [JsonPropertyName("policy")]
    public CloudAgentPolicySnapshotDto Policy { get; set; } = new();

    [JsonPropertyName("parentId")]
    [GoOmitEmpty]
    public string ParentID { get; set; } = "";

    [JsonPropertyName("fingerprint")]
    [GoOmitEmpty]
    public string Fingerprint { get; set; } = "";

    [JsonPropertyName("creativeAnchor")]
    [GoOmitEmpty]
    public CloudAgentCreativeAnchorDto? CreativeAnchor { get; set; }

    [JsonPropertyName("textHistory")]
    [GoOmitEmpty]
    public List<CloudAgentTextMessageDto>? TextHistory { get; set; }

    [JsonPropertyName("skills")]
    public List<CloudAgentSkillDto> Skills { get; set; } = [];

    [JsonPropertyName("skillReads")]
    [GoOmitEmpty]
    public Dictionary<string, bool>? SkillReads { get; set; }

    [JsonPropertyName("profile")]
    public CloudAgentProfileSnapshotDto Profile { get; set; } = new();

    [JsonPropertyName("profileReads")]
    [GoOmitEmpty]
    public Dictionary<string, bool>? ProfileReads { get; set; }

    [JsonPropertyName("canonical")]
    public CloudAgentCanonicalRequestDto Canonical { get; set; } = new();

    [JsonPropertyName("activeTaskId")]
    public string ActiveTaskID { get; set; } = "";

    [JsonPropertyName("activeTextDraft")]
    [GoOmitEmpty]
    public string ActiveTextDraft { get; set; } = "";

    [JsonPropertyName("mediaTaskId")]
    [GoOmitEmpty]
    public string MediaTaskID { get; set; } = "";

    [JsonPropertyName("taskIds")]
    public List<string> TaskIDs { get; set; } = [];

    [JsonPropertyName("step")]
    public int Step { get; set; }

    [JsonPropertyName("generations")]
    public int Generations { get; set; }

    [JsonPropertyName("videoSeconds")]
    public int VideoSeconds { get; set; }

    [JsonPropertyName("calls")]
    public List<CloudAgentCallDto> Calls { get; set; } = [];

    [JsonPropertyName("callIndex")]
    public int CallIndex { get; set; }

    [JsonPropertyName("approval")]
    [GoOmitEmpty]
    public CloudAgentApprovalDto? Approval { get; set; }

    [JsonPropertyName("decisions")]
    public Dictionary<string, string> Decisions { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("decisionSettings")]
    [GoOmitEmpty]
    public Dictionary<string, string>? DecisionSettings { get; set; }

    [JsonPropertyName("events")]
    public List<CloudAgentEventDto> Events { get; set; } = [];
}

/// <summary>对应 Go: <c>app.providerTextMessage</c>。</summary>
public sealed class CloudAgentTextMessageDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    public CloudAgentTextMessageDto() { }

    public CloudAgentTextMessageDto(string role, string content)
    {
        Role = role;
        Content = content;
    }
}

/// <summary>模型请求规范形。对应 Go: <c>app.canonicalAgentRequest</c>。</summary>
public sealed class CloudAgentCanonicalRequestDto
{
    [JsonPropertyName("messages")]
    public List<Dictionary<string, JsonElement>> Messages { get; set; } = [];

    [JsonPropertyName("tools")]
    public List<Dictionary<string, JsonElement>> Tools { get; set; } = [];

    [JsonPropertyName("toolChoice")]
    public JsonElement ToolChoice { get; set; } = JsonSerializer.SerializeToElement("auto");

    [JsonPropertyName("systemPrompt")]
    public string SystemPrompt { get; set; } = "";

    [JsonPropertyName("promptCacheKey")]
    [GoOmitEmpty]
    public string PromptCacheKey { get; set; } = "";
}

/// <summary>对外输出。对应 Go: <c>app.CloudAgentRun</c>。</summary>
public sealed class CloudAgentRunDto
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("canvasId")]
    public string CanvasID { get; set; } = "";

    [JsonPropertyName("parentId")]
    [GoOmitEmpty]
    public string ParentID { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("cleanupPending")]
    [GoOmitEmpty]
    public bool CleanupPending { get; set; }

    [JsonPropertyName("failureMessage")]
    [GoOmitEmpty]
    public string FailureMessage { get; set; } = "";

    [JsonPropertyName("permissionMode")]
    public string PermissionMode { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("events")]
    [GoOmitEmpty]
    public List<CloudAgentEventDto>? Events { get; set; }

    [JsonPropertyName("skills")]
    [GoOmitEmpty]
    public List<CloudAgentSkillDto>? Skills { get; set; }

    [JsonPropertyName("approval")]
    [GoOmitEmpty]
    public CloudAgentApprovalDto? Approval { get; set; }

    [JsonPropertyName("spentCredits")]
    public double SpentCredits { get; set; }

    [JsonPropertyName("step")]
    public int Step { get; set; }

    [JsonPropertyName("activeMessage")]
    [GoOmitEmpty]
    public Dictionary<string, string>? ActiveMessage { get; set; }
}

/// <summary>
/// 云 Agent 共享契约：常量、校验、ID/指纹/哈希与状态 (反) 序列化。
/// 对应 Go: <c>cloud_agent.go</c> 的 validate 部分与 <c>cloud_agent_runtime.go</c> 的
/// decode/save/validate 部分（本批覆盖会话读路径；运行时推进随后续批次）。
/// </summary>
public static partial class CloudAgentContracts
{
    public const string CloudAgentOperation = "cloud_agent";
    public const string CompilerVersion = "cloud-agent-policy-compiler/v3";
    public const string SkillEntryPath = "SKILL.md";

    /// <summary>UTF-8 SHA256 十六进制小写摘要。</summary>
    public static string Sha256Hex(string value)
    {
        byte[] sum = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(sum).ToLowerInvariant();
    }

    /// <summary>确定性运行/任务 ID。对应 Go: <c>cloudAgentID</c>。</summary>
    public static string AgentID(string userId, string key)
    {
        byte[] sum = SHA256.HashData(Encoding.UTF8.GetBytes(userId + "\x00" + key));
        return "ag" + Convert.ToHexString(sum, 0, 16).ToLowerInvariant();
    }

    /// <summary>请求指纹。对应 Go: <c>cloudAgentFingerprint</c>。</summary>
    public static string Fingerprint(CloudAgentRequestDto request, string parent)
    {
        // 包装 struct 无 json tag：键为 "Request"/"Parent"，字段顺序按声明。
        string inner = JsonSerializer.Serialize(request, GoJson.WriteOptions);
        string parentJson = JsonSerializer.Serialize(parent, GoJson.WriteOptions);
        string json = $"{{\"Request\":{inner},\"Parent\":{parentJson}}}";
        byte[] sum = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(sum).ToLowerInvariant();
    }

    /// <summary>请求校验。对应 Go: <c>validateCloudAgentRequest</c>。</summary>
    public static void ValidateRequest(CloudAgentRequestDto request)
    {
        ValidateCloudAgentID(request.CanvasID, "画布 ID", 80);
        request.CanvasID = request.CanvasID.Trim();
        ValidatePrompt(request.Prompt, 16000);
        request.Prompt = request.Prompt.Trim();
        ValidateCloudAgentID(request.IdempotencyKey, "幂等键", 128);
        if (request.IdempotencyKey.Length < 8)
        {
            throw AppError.BadAuthRequest("需要 8–128 个字符的幂等键");
        }
        if (request.PermissionMode is not ("read_only" or "request_approval" or "auto"))
        {
            throw AppError.BadAuthRequest("无效的 Agent 执行权限");
        }
        ValidateIDIfPresent(request.Model, "模型标识", 160);
        ValidateIDIfPresent(request.LogicalModelID, "逻辑模型 ID", 80);
        ValidateIDIfPresent(request.ChannelID, "渠道 ID", 80);
        ValidateIDIfPresent(request.ChannelModelKey, "渠道模型标识", 160);
        if (request.ReasoningMode is not ("" or "off" or "auto" or "deep"))
        {
            throw AppError.BadAuthRequest("无效的 Agent 推理模式");
        }
        if (request.ProfileRevision.Length > 0)
        {
            ValidateCloudAgentID(request.ProfileRevision, "偏好版本", 120);
        }
        if (request.LogicalModelID.Length > 0)
        {
            if (request.ChannelID.Length > 0 || request.ChannelModelKey.Length > 0)
            {
                throw AppError.BadAuthRequest("逻辑模型和系统渠道不能混用");
            }
        }
        else if (request.ChannelID.Length == 0 || request.ChannelModelKey.Length == 0)
        {
            throw AppError.BadAuthRequest("请选择后端受管文本模型；Agent 不接受浏览器自定义密钥或上游地址");
        }
        else if (request.Model.Length > 0 && request.Model != request.ChannelModelKey)
        {
            throw AppError.BadAuthRequest("渠道模型标识与 model 不一致");
        }
        if (request.Budget.MaxGenerationTasks < 0 || request.Budget.MaxVideoSeconds < 0)
        {
            throw AppError.BadAuthRequest("生成任务和视频秒数预算不能为负数");
        }
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int index = 0; index < request.SkillIDs.Count; index++)
        {
            string id = request.SkillIDs[index].Trim();
            ValidateCloudAgentID(id, "技能 ID", 80);
            if (!seen.Add(id))
            {
                throw AppError.BadAuthRequest("技能 ID 无效或重复");
            }
            request.SkillIDs[index] = id;
        }
        if (request.PermissionMode == "read_only"
            && (request.Budget.MaxGenerationTasks != 0 || request.Budget.MaxVideoSeconds != 0))
        {
            throw AppError.BadAuthRequest("只读模式不能设置生成预算");
        }
        if (request.ContextScope.Count > 1
            || (request.ContextScope.Count == 1 && request.ContextScope[0] != "canvas"))
        {
            throw AppError.BadAuthRequest("当前仅支持已保存画布摘要，其他上下文尚未开放");
        }
        if (double.IsNaN(request.Budget.MaxCredits) || double.IsInfinity(request.Budget.MaxCredits)
            || request.Budget.MaxCredits <= 0 || request.Budget.MaxCredits > 1000000)
        {
            throw AppError.BadAuthRequest("本轮积分上限必须大于 0 且不超过 1000000");
        }
    }

    private static void ValidateIDIfPresent(string value, string label, int maxRunes)
    {
        if (value.Length > 0)
        {
            ValidateCloudAgentID(value, label, maxRunes);
        }
    }

    /// <summary>提示词校验。对应 Go: <c>validateCloudAgentPrompt</c>。</summary>
    public static void ValidatePrompt(string value, int maxRunes)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.EnumerateRunes().Count() > maxRunes)
        {
            throw AppError.BadAuthRequest("需要有效画布和 1–16000 个字符的提示词");
        }
        foreach (System.Text.Rune rune in value.EnumerateRunes())
        {
            if (System.Text.Rune.IsControl(rune) && rune.Value != 10 && rune.Value != 13 && rune.Value != 9)
            {
                throw AppError.BadAuthRequest("提示词不能包含控制字符");
            }
        }
    }

    /// <summary>通用 ID 校验。对应 Go: <c>validateCloudAgentID</c>。</summary>
    public static void ValidateCloudAgentID(string value, string label, int maxRunes)
    {
        if (value.Length == 0 || value.Trim() != value)
        {
            throw AppError.BadAuthRequest($"{label}不能为空、不能包含首尾空白或无效字符");
        }
        if (value.EnumerateRunes().Count() > maxRunes)
        {
            throw AppError.BadAuthRequest($"{label}不能超过 {maxRunes} 个字符");
        }
        foreach (System.Text.Rune rune in value.EnumerateRunes())
        {
            if (System.Text.Rune.IsControl(rune))
            {
                throw AppError.BadAuthRequest($"{label}不能包含控制字符");
            }
        }
    }

    public static bool IsTaskTerminal(string status) => status is "succeeded" or "failed" or "cancelled";

    public static bool IsRunTerminal(string status) =>
        status is "completed" or "failed" or "cancelled" or "rejected";

    /// <summary>向运行状态追加事件。对应 Go: <c>(*cloudAgentRuntime).event</c>。</summary>
    public static void AddEvent(CloudAgentRuntimeDto state, string id, string kind, Dictionary<string, JsonElement> payload)
    {
        int seq = state.Events.Count + 1;
        state.Events.Add(new CloudAgentEventDto
        {
            EventID = $"{id}:{seq}",
            RunID = id,
            Seq = seq,
            Type = kind,
            Payload = payload,
            CreatedAt = DateTime.UtcNow,
        });
    }

    public static Dictionary<string, JsonElement> Payload(params (string Key, object? Value)[] items)
    {
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
        foreach ((string key, object? value) in items)
        {
            result[key] = value switch
            {
                null => JsonSerializer.SerializeToElement((string?)null),
                JsonElement element => element,
                _ => JsonSerializer.SerializeToElement(value, GoJson.WriteOptions),
            };
        }
        return result;
    }

    /// <summary>反序列化运行状态。对应 Go: <c>cloudAgentDecode</c>（校验随运行时批次）。</summary>
    public static CloudAgentRuntimeDto Decode(CloudAgentExecution run)
    {
        if (run.StateJSON.Trim().Length == 0)
        {
            throw new InvalidOperationException("Agent runtime state is empty");
        }
        CloudAgentRuntimeDto state;
        try
        {
            state = JsonSerializer.Deserialize<CloudAgentRuntimeDto>(run.StateJSON, GoJson.ReadOptions)
                ?? throw new InvalidOperationException("Agent runtime state is empty");
        }
        catch (JsonException cause)
        {
            throw new InvalidOperationException($"decode Agent runtime state: {cause.Message}", cause);
        }
        return state;
    }

    /// <summary>序列化并裁剪运行状态到执行记录。对应 Go: <c>cloudAgentSave</c>。</summary>
    public static void Save(CloudAgentExecution run, CloudAgentRuntimeDto state)
    {
        string raw = JsonSerializer.Serialize(state, GoJson.WriteOptions);
        if (raw.Length > 512 * 1024)
        {
            throw new CloudAgentCheckpointException("Agent 状态超过 512KB 上限");
        }
        run.CanvasID = state.Request.CanvasID;
        run.ActiveTaskID = state.ActiveTaskID;
        run.MediaTaskID = state.MediaTaskID;
        run.StateJSON = raw;
    }

    /// <summary>截断到指定 rune 数。对应 Go: <c>truncateRunes</c>。</summary>
    public static string TruncateRunes(string value, int limit)
    {
        if (limit < 0)
        {
            limit = 0;
        }
        List<char> chars = new(Math.Min(value.Length, limit * 2));
        int count = 0;
        foreach (System.Text.Rune rune in value.EnumerateRunes())
        {
            if (count >= limit)
            {
                break;
            }
            chars.AddRange(rune.ToString());
            count++;
        }
        return new string(chars.ToArray());
    }

    public static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => v.Length > 0) ?? "";

    /// <summary>安全的 JSON 对象参数解码（拒绝未知字段/非对象/多余 JSON）。对应 Go: <c>decodeCloudAgentJSONObject</c>。</summary>
    public static T DecodeObject<T>(string raw) where T : class
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            throw CloudAgentJson.SingleObjectError();
        }
        JsonSerializerOptions options = new(GoJson.ReadOptions)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        try
        {
            // JsonDocument.Parse 会拒绝尾随 JSON；根必须是 object。
            using JsonDocument document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw CloudAgentJson.SingleObjectError();
            }
            return document.RootElement.Deserialize<T>(options)
                ?? throw CloudAgentJson.SingleObjectError();
        }
        catch (JsonException cause)
        {
            throw CloudAgentJson.ArgumentError(cause.Message);
        }
    }

    /// <summary>画布哈希（忽略视口/更新时间）。对应 Go: <c>cloudAgentCanvasHash</c>。</summary>
    public static string CanvasHash(JsonObject document)
    {
        JsonObject content = new();
        foreach ((string key, JsonNode? value) in document)
        {
            if (key is not ("viewport" or "updatedAt"))
            {
                content[key] = value?.DeepClone();
            }
        }
        return CreationHashOf(content);
    }

    /// <summary>媒体内容哈希（忽略节点位置）。对应 Go: <c>cloudAgentMediaContentHash</c>。</summary>
    public static string MediaContentHash(JsonObject document)
    {
        JsonObject content = new();
        foreach ((string key, JsonNode? value) in document)
        {
            content[key] = value?.DeepClone();
        }
        if (content["nodes"] is JsonArray nodes)
        {
            JsonArray projected = new();
            foreach (JsonNode? node in nodes)
            {
                if (node is not JsonObject nodeObject)
                {
                    continue;
                }
                JsonObject item = new();
                foreach ((string key, JsonNode? value) in nodeObject)
                {
                    if (key != "position")
                    {
                        item[key] = value?.DeepClone();
                    }
                }
                projected.Add(item);
            }
            content["nodes"] = projected;
        }
        return CanvasHash(content);
    }

    /// <summary>
    /// Go json.Marshal(map[string]any) 的确定性序列化（键递归排序）+ SHA256。
    /// 对应 Go: <c>creationHash</c>。数字按 double 最短表示归一。
    /// </summary>
    public static string CreationHashOf(JsonNode? value)
    {
        StringBuilder json = new();
        WriteCanonicalJson(value, json);
        byte[] sum = SHA256.HashData(Encoding.UTF8.GetBytes(json.ToString()));
        return Convert.ToHexString(sum).ToLowerInvariant();
    }

    private static void WriteCanonicalJson(JsonNode? node, StringBuilder json)
    {
        switch (node)
        {
            case null:
                json.Append("null");
                break;
            case JsonObject obj:
                json.Append('{');
                bool first = true;
                foreach ((string key, JsonNode? child) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        json.Append(',');
                    }
                    first = false;
                    WriteCanonicalString(key, json);
                    json.Append(':');
                    WriteCanonicalJson(child, json);
                }
                json.Append('}');
                break;
            case JsonArray array:
                json.Append('[');
                for (int index = 0; index < array.Count; index++)
                {
                    if (index > 0)
                    {
                        json.Append(',');
                    }
                    WriteCanonicalJson(array[index], json);
                }
                json.Append(']');
                break;
            case JsonValue value:
                if (value.TryGetValue<string>(out string? text))
                {
                    WriteCanonicalString(text, json);
                }
                else if (value.TryGetValue<bool>(out bool flag))
                {
                    json.Append(flag ? "true" : "false");
                }
                else if (value.TryGetValue<double>(out double number))
                {
                    json.Append(GoNumber(number));
                }
                else if (value.TryGetValue<long>(out long integer))
                {
                    json.Append(integer.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    json.Append("null");
                }
                break;
            default:
                json.Append("null");
                break;
        }
    }

    private static string GoNumber(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static void WriteCanonicalString(string value, StringBuilder json)
    {
        json.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                case '<': json.Append("\\u003c"); break;
                case '>': json.Append("\\u003e"); break;
                case '&': json.Append("\\u0026"); break;
                case '\u2028': json.Append("\\u2028"); break;
                case '\u2029': json.Append("\\u2029"); break;
                default:
                    if (c < 0x20)
                    {
                        json.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        json.Append(c);
                    }
                    break;
            }
        }
        json.Append('"');
    }
}

/// <summary>确定性检查点失败，不得像瞬时 DB 错误那样无限重试。对应 Go: <c>errCloudAgentCheckpoint</c>。</summary>
public sealed class CloudAgentCheckpointException(string message) : Exception(message)
{
}

/// <summary>工具参数错误：仅语法/模式可由模型修复；授权错误照常失败。对应 Go: <c>cloudAgentArgumentError</c>。</summary>
public sealed class CloudAgentArgumentException(string message) : Exception(message)
{
}

/// <summary>JSON 参数解码错误工厂。对应 Go: <c>cloud_agent_json.go</c>。</summary>
public static class CloudAgentJson
{
    public static CloudAgentArgumentException SingleObjectError() =>
        new("参数必须是单个 JSON 对象");

    public static CloudAgentArgumentException ArgumentError(string message) =>
        new(message);

    public static AppError CanvasArgumentError() => AppError.BadAuthRequest(
        "画布工具参数无效：仅允许一个 JSON 对象；顶层只含 snapshotHash 和 ops，snapshotHash 不得放入 ops。请按工具 schema 修正后重试");

    public static CloudAgentArgumentException StoryboardArgumentError(string action) => new(
        AppError.BadAuthRequest($"{action}分镜工具参数无效：仅允许工具 schema 中声明的字段，请重新读取分镜并按结构化参数重试").Message);

    public static CloudAgentArgumentException BatchTableArgumentError() => new(
        AppError.BadAuthRequest("批量创作表工具参数无效：仅允许工具 schema 中声明的字段，请重新读取节点并按结构化参数重试").Message);
}

/// <summary>能力注册表入口。对应 Go: <c>app/cloud_agent_nodes.go</c>。</summary>
public static class CloudAgentNodes
{
    public static readonly CapabilityRegistry Registry = BuiltinCanvasCapabilities.BuiltinRegistry();

    public static (CapabilityDescriptor Descriptor, bool Known) ForType(string nodeType) =>
        Registry.Resolve(nodeType);

    public static string[] TypeNames() => Registry.Types();

    public static string[] GenerationModeNames() =>
        Registry.GenerationModeNames();

    public static bool GenerationModeSupported(string mode) => Registry.SupportsGenerationMode(mode);

    public static (CapabilityDescriptor Descriptor, bool Known) ForGenerationMode(string mode) =>
        Registry.ResolveGenerationMode(mode);

    public static (string Version, string Hash, string[] Nodes) CapabilitySetInfo() =>
        (CapabilityRegistry.SetVersion, Registry.Hash(), Registry.Types());
}
