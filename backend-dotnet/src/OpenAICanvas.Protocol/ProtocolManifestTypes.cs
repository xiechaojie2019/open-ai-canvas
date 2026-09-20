#nullable enable
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Protocol;

/// <summary>协议能力。对应 Go: <c>protocol.Capability</c>。</summary>
public static class ProtocolCapability
{
    public const string Text = "text";
    public const string Image = "image";
    public const string Video = "video";
    public const string Audio = "audio";
}

/// <summary>协议可选中的界面。对应 Go: <c>protocol.Surface</c>。</summary>
public static class ProtocolSurface
{
    public const string AdminSystemChannel = "admin.system-channel";
    public const string UserCustomChannel = "user.custom-channel";
    public const string Canvas = "canvas";
    public const string Creation = "creation";
    public const string Agent = "agent";
}

/// <summary>
/// Provider 无关的输出契约。对应 Go: <c>protocol.OutputOptions</c>。
/// 遗留的顶层字段仍保留在 <see cref="GenerationRequest"/> 上；清单同时拿到两份投影，
/// 新插件应优先读取 <c>request.output</c>。
/// </summary>
public sealed class OutputOptions
{
    [JsonPropertyName("count")]
    [OpenAICanvas.Domain.Serialization.GoOmitEmpty]
    public int Count { get; set; }

    [JsonPropertyName("duration")]
    [OpenAICanvas.Domain.Serialization.GoOmitEmpty]
    public int Duration { get; set; }

    [JsonPropertyName("aspectRatio")]
    [OpenAICanvas.Domain.Serialization.GoOmitEmpty]
    public string AspectRatio { get; set; } = "";

    [JsonPropertyName("width")]
    [OpenAICanvas.Domain.Serialization.GoOmitEmpty]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    [OpenAICanvas.Domain.Serialization.GoOmitEmpty]
    public int Height { get; set; }

    [JsonPropertyName("resolution")]
    [OpenAICanvas.Domain.Serialization.GoOmitEmpty]
    public string Resolution { get; set; } = "";

    [JsonPropertyName("quality")]
    [OpenAICanvas.Domain.Serialization.GoOmitEmpty]
    public string Quality { get; set; } = "";

    [JsonPropertyName("fps")]
    [OpenAICanvas.Domain.Serialization.GoOmitEmpty]
    public double FPS { get; set; }

    [JsonPropertyName("generateAudio")]
    [OpenAICanvas.Domain.Serialization.GoOmitEmpty]
    public bool GenerateAudio { get; set; }

    [JsonPropertyName("watermark")]
    [OpenAICanvas.Domain.Serialization.GoOmitEmpty]
    public bool Watermark { get; set; }

    [JsonPropertyName("format")]
    [OpenAICanvas.Domain.Serialization.GoOmitEmpty]
    public string Format { get; set; } = "";

    [JsonPropertyName("options")]
    [OpenAICanvas.Domain.Serialization.GoOmitEmpty]
    public Dictionary<string, object?> Options { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 历史对话消息。对应 Go: <c>protocol.Message</c>。
/// <c>Content</c> 在 Go 是 <c>any</c>（字符串或多模态数组），这里保持 JSON 原形。
/// </summary>
public sealed class Message
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public object? Content { get; set; }
}

/// <summary>画布、创作与 Agent 共用的平台请求契约。对应 Go: <c>protocol.GenerationRequest</c>。</summary>
public sealed class GenerationRequest
{
    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("instructions")]
    public string Instructions { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<Message> Messages { get; set; } = [];

    [JsonPropertyName("inputs")]
    public List<MediaReference> Inputs { get; set; } = [];

    [JsonPropertyName("images")]
    public List<MediaReference> Images { get; set; } = [];

    [JsonPropertyName("videos")]
    public List<MediaReference> Videos { get; set; } = [];

    [JsonPropertyName("audios")]
    public List<MediaReference> Audios { get; set; } = [];

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("aspectRatio")]
    public string AspectRatio { get; set; } = "";

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = "";

    [JsonPropertyName("quality")]
    public string Quality { get; set; } = "";

    [JsonPropertyName("generateAudio")]
    public bool GenerateAudio { get; set; }

    [JsonPropertyName("watermark")]
    public bool Watermark { get; set; }

    [JsonPropertyName("imageCount")]
    public int ImageCount { get; set; }

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "";

    [JsonPropertyName("output")]
    public OutputOptions Output { get; set; } = new();

    [JsonPropertyName("providerOptions")]
    public Dictionary<string, Dictionary<string, object?>> ProviderOptions { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("extra")]
    public Dictionary<string, object?> Extra { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>创建请求上下文。对应 Go: <c>protocol.RequestContext</c>。</summary>
public sealed class RequestContext
{
    public string BaseURL { get; set; } = "";
    public GenerationRequest Request { get; set; } = new();
}

/// <summary>轮询/取消/结果请求上下文。对应 Go: <c>protocol.PollContext</c>。</summary>
public sealed class PollContext
{
    public string BaseURL { get; set; } = "";
    public string Model { get; set; } = "";
    public GenerationRequest Request { get; set; } = new();
    public string TaskID { get; set; } = "";
    public Dictionary<string, object?> Metadata { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Agent 请求上下文。对应 Go: <c>protocol.AgentRequestContext</c>。</summary>
public sealed class AgentRequestContext
{
    public string BaseURL { get; set; } = "";
    public string Model { get; set; } = "";
    public Dictionary<string, object?> Request { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>创建结果。对应 Go: <c>protocol.CreateResult</c>。</summary>
public sealed class CreateResult
{
    public string TaskID { get; set; } = "";
    public string Status { get; set; } = "";
    public Result? Result { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>轮询结果。对应 Go: <c>protocol.PollResult</c>。</summary>
public sealed class PollResult
{
    public string TaskID { get; set; } = "";
    public string Status { get; set; } = "";
    public Result? Result { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>协议结果实体。对应 Go: <c>protocol.Result</c>。</summary>
public sealed class Result
{
    public List<MediaReference> Images { get; set; } = [];
    public List<MediaReference> Videos { get; set; } = [];
    public List<MediaReference> Audios { get; set; } = [];
    public string Text { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public Dictionary<string, object?>? Usage { get; set; }
}

/// <summary>协议声明的可配置参数。对应 Go: <c>protocol.Parameter</c>。</summary>
public sealed class Parameter
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("required")]
    [GoOmitEmpty]
    public bool Required { get; set; }

    [JsonPropertyName("description")]
    [GoOmitEmpty]
    public string Description { get; set; } = "";

    [JsonPropertyName("values")]
    [GoOmitEmpty]
    public List<string> Values { get; set; } = [];

    [JsonPropertyName("mapping")]
    [GoOmitEmpty]
    public string Mapping { get; set; } = "";
}

/// <summary>
/// 协议元数据（运行时投影）。对应 Go: <c>protocol.Metadata</c>。
/// 带 <c>json:"-"</c> 的字段是宿主在归一化时填入的执行摘要，不属于插件线格式。
/// </summary>
public sealed class Metadata
{
    public string ID { get; set; } = "";
    public string Version { get; set; } = "";
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Categories { get; set; } = [];
    public List<string> Scopes { get; set; } = [];
    public List<Parameter> Parameters { get; set; } = [];
    public string Documentation { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Installable { get; set; }

    /// <summary>归一化产出的创建操作摘要（<c>POST /v1/...</c>）。对应 Go: <c>Metadata.Create</c>。</summary>
    public string Create { get; set; } = "";

    public string Poll { get; set; } = "";

    public string Cancel { get; set; } = "";

    public string ContentType { get; set; } = "";

    /// <summary>历史别名。对应 Go: <c>Metadata.LegacyAliases</c>。</summary>
    public List<string> LegacyAliases { get; set; } = [];

    /// <summary>执行后端（<c>declarative</c> / <c>host:xxx</c>）。对应 Go: <c>Metadata.Execution</c>。</summary>
    public string Execution { get; set; } = "";

    public bool RequiresPublicMediaURLs { get; set; }

    public string UnavailableReason { get; set; } = "";

    public Metadata Clone() => new()
    {
        ID = ID,
        Version = Version,
        Name = Name,
        Vendor = Vendor,
        Description = Description,
        Categories = [.. Categories],
        Scopes = [.. Scopes],
        Parameters = Parameters,
        Documentation = Documentation,
        Enabled = Enabled,
        Installable = Installable,
        Create = Create,
        Poll = Poll,
        Cancel = Cancel,
        ContentType = ContentType,
        LegacyAliases = [.. LegacyAliases],
        Execution = Execution,
        RequiresPublicMediaURLs = RequiresPublicMediaURLs,
        UnavailableReason = UnavailableReason,
    };
}

/// <summary>协议返回的工具调用。对应 Go: <c>protocol.AgentToolCall</c>。</summary>
public sealed class AgentToolCall
{
    public string ID { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string ThoughtSignature { get; set; } = "";
}

/// <summary>Agent 调用结果。对应 Go: <c>protocol.AgentResult</c>。</summary>
public sealed class AgentResult
{
    public string Text { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public List<AgentToolCall> ToolCalls { get; set; } = [];
}

/// <summary>
/// 协议适配器只负责协议翻译。凭证、出站安全、轮询租约、计费与结果下载都由宿主负责。
/// 对应 Go: <c>protocol.Adapter</c>。
/// </summary>
public interface IProtocolAdapter
{
    Metadata Metadata();

    RequestSpec BuildCreate(RequestContext context);

    CreateResult ParseCreate(byte[] body);

    RequestSpec BuildPoll(PollContext context);

    PollResult ParsePoll(PollContext context, byte[] body);

    RequestSpec BuildCancel(PollContext context);
}

/// <summary>可选面：可调用工具的文本协议。对应 Go: <c>protocol.AgentAdapter</c>。</summary>
public interface IAgentProtocolAdapter
{
    RequestSpec BuildAgent(AgentRequestContext context);

    AgentResult ParseAgent(byte[] body);
}

/// <summary>
/// 让宿主区分「真的声明了 agent 操作」和「只是通过共享实现满足了方法集」的声明式 provider。
/// 对应 Go: <c>protocol.AgentCapability</c>。
/// </summary>
public interface IAgentCapability
{
    bool AgentAvailable();
}

/// <summary>可选面：异步任务成功后用带鉴权的请求取回二进制结果。对应 Go: <c>protocol.ResultAdapter</c>。</summary>
public interface IResultAdapter
{
    RequestSpec BuildResult(PollContext context);
}

public interface IResultCapability
{
    bool ResultAvailable();
}

/// <summary>协议不可用错误。对应 Go: <c>protocol.UnavailableError</c>。</summary>
public sealed class ProtocolUnavailableException : Exception
{
    public ProtocolUnavailableException(string protocol, string reason)
        : base(reason.Length == 0 ? $"protocol {protocol} is unavailable" : $"protocol {protocol} is unavailable: {reason}")
    {
        Protocol = protocol;
        Reason = reason;
    }

    public string Protocol { get; }

    public string Reason { get; }
}

/// <summary>占位适配器：元数据可用但执行后端缺失。对应 Go: <c>protocol.UnavailableAdapter</c>。</summary>
public sealed class UnavailableAdapter : IProtocolAdapter
{
    public UnavailableAdapter(Metadata info) => Info = info;

    public Metadata Info { get; }

    public Metadata Metadata() => Info;

    public RequestSpec BuildCreate(RequestContext context) => throw Unavailable();

    public CreateResult ParseCreate(byte[] body) => throw Unavailable();

    public RequestSpec BuildPoll(PollContext context) => throw Unavailable();

    public PollResult ParsePoll(PollContext context, byte[] body) => throw Unavailable();

    public RequestSpec BuildCancel(PollContext context) => throw Unavailable();

    private ProtocolUnavailableException Unavailable() => new(Info.ID, Info.UnavailableReason);
}

/// <summary>
/// 清单里的一次 HTTP 操作。对应 Go: <c>protocol.ManifestOperation</c>。
/// <c>Body</c>/<c>Fields</c> 是两代表达式写法：<c>Body</c> 是 JSON 模板，
/// <c>Fields</c> 是已安装 1.0.x 插件的点路径表达式。
/// </summary>
public sealed class ManifestOperation
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("pathTemplate")]
    [GoOmitEmpty]
    public object? PathTemplate { get; set; }

    [JsonPropertyName("originPath")]
    [GoOmitEmpty]
    public bool OriginPath { get; set; }

    [JsonPropertyName("contentType")]
    [GoOmitEmpty]
    public string ContentType { get; set; } = "";

    [JsonPropertyName("contentTypeTemplate")]
    [GoOmitEmpty]
    public object? ContentTypeTemplate { get; set; }

    [JsonPropertyName("headers")]
    [GoOmitEmpty]
    public Dictionary<string, object?>? Headers { get; set; }

    [JsonPropertyName("query")]
    [GoOmitEmpty]
    public Dictionary<string, object?>? Query { get; set; }

    [JsonPropertyName("body")]
    [GoOmitEmpty]
    public object? Body { get; set; }

    [JsonPropertyName("fields")]
    [GoOmitEmpty]
    public Dictionary<string, string>? Fields { get; set; }

    [JsonPropertyName("files")]
    [GoOmitEmpty]
    public List<ManifestFilePart>? Files { get; set; }
}

/// <summary>multipart 文件分片声明。对应 Go: <c>protocol.ManifestFilePart</c>。</summary>
public sealed class ManifestFilePart
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("source")]
    public object? Source { get; set; }

    [JsonPropertyName("filename")]
    [GoOmitEmpty]
    public object? Filename { get; set; }

    [JsonPropertyName("mimeType")]
    [GoOmitEmpty]
    public object? MIMEType { get; set; }
}

/// <summary>上游响应到平台结果的映射声明。对应 Go: <c>protocol.ManifestResponse</c>。</summary>
public sealed class ManifestResponse
{
    [JsonPropertyName("taskIdPaths")]
    [GoOmitEmpty]
    public List<string> TaskIDPaths { get; set; } = [];

    [JsonPropertyName("statusPaths")]
    [GoOmitEmpty]
    public List<string> StatusPaths { get; set; } = [];

    [JsonPropertyName("errorPaths")]
    [GoOmitEmpty]
    public List<string> ErrorPaths { get; set; } = [];

    [JsonPropertyName("messagePaths")]
    [GoOmitEmpty]
    public List<string> MessagePaths { get; set; } = [];

    [JsonPropertyName("textPaths")]
    [GoOmitEmpty]
    public List<string> TextPaths { get; set; } = [];

    [JsonPropertyName("reasoningPaths")]
    [GoOmitEmpty]
    public List<string> ReasoningPaths { get; set; } = [];

    [JsonPropertyName("resultUrlPaths")]
    [GoOmitEmpty]
    public List<string> ResultURLPaths { get; set; } = [];

    [JsonPropertyName("resultPaths")]
    [GoOmitEmpty]
    public List<string> ResultPaths { get; set; } = [];

    [JsonPropertyName("resultKind")]
    [GoOmitEmpty]
    public string ResultKind { get; set; } = "";

    [JsonPropertyName("resultEphemeral")]
    [GoOmitEmpty]
    public bool ResultEphemeral { get; set; }

    /// <summary>
    /// create 同步返回二进制媒体（如 <c>/audio/speech</c> 的音频流）。
    /// 声明式解析会把整个响应体包装为对应能力的单个媒体结果，不做 JSON 路径提取。
    /// </summary>
    [JsonPropertyName("binaryPayload")]
    [GoOmitEmpty]
    public bool BinaryPayload { get; set; }

    [JsonPropertyName("taskId")]
    [GoOmitEmpty]
    public object? TaskID { get; set; }

    [JsonPropertyName("status")]
    [GoOmitEmpty]
    public object? Status { get; set; }

    [JsonPropertyName("message")]
    [GoOmitEmpty]
    public object? Message { get; set; }

    [JsonPropertyName("text")]
    [GoOmitEmpty]
    public object? Text { get; set; }

    [JsonPropertyName("reasoning")]
    [GoOmitEmpty]
    public object? Reasoning { get; set; }

    [JsonPropertyName("images")]
    [GoOmitEmpty]
    public object? Images { get; set; }

    [JsonPropertyName("videos")]
    [GoOmitEmpty]
    public object? Videos { get; set; }

    [JsonPropertyName("audios")]
    [GoOmitEmpty]
    public object? Audios { get; set; }

    [JsonPropertyName("usage")]
    [GoOmitEmpty]
    public object? Usage { get; set; }
}

/// <summary>
/// 可调用工具的文本请求的响应形状。工具调用路径针对 <c>ToolCallsPath</c> 的每一项解析。
/// 对应 Go: <c>protocol.ManifestAgentResponse</c>。
/// </summary>
public sealed class ManifestAgentResponse
{
    [JsonPropertyName("textPaths")]
    [GoOmitEmpty]
    public List<string> TextPaths { get; set; } = [];

    [JsonPropertyName("reasoningPaths")]
    [GoOmitEmpty]
    public List<string> ReasoningPaths { get; set; } = [];

    [JsonPropertyName("toolCallsPath")]
    [GoOmitEmpty]
    public string ToolCallsPath { get; set; } = "";

    [JsonPropertyName("toolCallIdPaths")]
    [GoOmitEmpty]
    public List<string> ToolCallIDPaths { get; set; } = [];

    [JsonPropertyName("toolCallNamePaths")]
    [GoOmitEmpty]
    public List<string> ToolCallNamePaths { get; set; } = [];

    [JsonPropertyName("toolCallArgumentsPaths")]
    [GoOmitEmpty]
    public List<string> ToolCallArgsPaths { get; set; } = [];

    [JsonPropertyName("toolCallThoughtSignaturePaths")]
    [GoOmitEmpty]
    public List<string> ToolCallSignaturePaths { get; set; } = [];
}

/// <summary>插件声明的 provider 贡献点。对应 Go: <c>protocol.ManifestProvider</c>。</summary>
public sealed class ManifestProvider
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; set; } = [];

    [JsonPropertyName("baseUrl")]
    [GoOmitEmpty]
    public string BaseURL { get; set; } = "";

    [JsonPropertyName("requiresPublicMediaUrls")]
    [GoOmitEmpty]
    public bool RequiresPublicMediaURLs { get; set; }

    [JsonPropertyName("auth")]
    [GoOmitEmpty]
    public ManifestAuth Auth { get; set; } = new();

    [JsonPropertyName("parameters")]
    [GoOmitEmpty]
    public List<Parameter> Parameters { get; set; } = [];

    [JsonPropertyName("validations")]
    [GoOmitEmpty]
    public List<ManifestValidation> Validations { get; set; } = [];

    [JsonPropertyName("create")]
    public ManifestOperation Create { get; set; } = new();

    [JsonPropertyName("agent")]
    [GoOmitEmpty]
    public ManifestOperation? Agent { get; set; }

    [JsonPropertyName("poll")]
    [GoOmitEmpty]
    public ManifestOperation? Poll { get; set; }

    [JsonPropertyName("cancel")]
    [GoOmitEmpty]
    public ManifestOperation? Cancel { get; set; }

    [JsonPropertyName("result")]
    [GoOmitEmpty]
    public ManifestOperation? Result { get; set; }

    [JsonPropertyName("response")]
    public ManifestResponse Response { get; set; } = new();

    [JsonPropertyName("agentResponse")]
    [GoOmitEmpty]
    public ManifestAgentResponse? AgentResponse { get; set; }
}

/// <summary>插件运行时声明。对应 Go: <c>protocol.ManifestRuntime</c>。</summary>
public sealed class ManifestRuntime
{
    [JsonPropertyName("backend")]
    [GoOmitEmpty]
    public string Backend { get; set; } = "";

    [JsonPropertyName("backendEntry")]
    [GoOmitEmpty]
    public string BackendEntry { get; set; } = "";

    [JsonPropertyName("web")]
    [GoOmitEmpty]
    public string Web { get; set; } = "";
}

/// <summary>插件清单的贡献点集合。对应 Go: <c>protocol.ManifestContributions</c>。</summary>
public sealed class ManifestContributions
{
    [JsonPropertyName("providers")]
    [GoOmitEmpty]
    public List<ManifestProvider> Providers { get; set; } = [];

    [JsonPropertyName("paymentProviders")]
    [GoOmitEmpty]
    public List<ManifestPaymentProvider> PaymentProviders { get; set; } = [];

    [JsonPropertyName("workflows")]
    [GoOmitEmpty]
    public List<ManifestWorkflow> Workflows { get; set; } = [];

    [JsonPropertyName("canvasNodes")]
    [GoOmitEmpty]
    public List<ManifestCanvasNode> CanvasNodes { get; set; } = [];

    [JsonPropertyName("transforms")]
    [GoOmitEmpty]
    public List<ManifestTransform> Transforms { get; set; } = [];

    [JsonPropertyName("commands")]
    [GoOmitEmpty]
    public List<ManifestCommand> Commands { get; set; } = [];

    [JsonPropertyName("assetSources")]
    [GoOmitEmpty]
    public List<string> AssetSources { get; set; } = [];

    [JsonPropertyName("usageObservers")]
    [GoOmitEmpty]
    public List<string> UsageObservers { get; set; } = [];

    [JsonPropertyName("aiCapabilities")]
    [GoOmitEmpty]
    public List<string> AICapabilities { get; set; } = [];

    [JsonPropertyName("agents")]
    [GoOmitEmpty]
    public List<string> Agents { get; set; } = [];

    [JsonPropertyName("importExport")]
    [GoOmitEmpty]
    public List<string> ImportExport { get; set; } = [];
}

/// <summary>provider 声明的前置参数校验。对应 Go: <c>protocol.ManifestValidation</c>。</summary>
public sealed class ManifestValidation
{
    [JsonPropertyName("assert")]
    public object? Assert { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>工作流贡献点。对应 Go: <c>protocol.ManifestWorkflow</c>。</summary>
public sealed class ManifestWorkflow
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("providerId")]
    public string ProviderID { get; set; } = "";

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";

    [JsonPropertyName("parameters")]
    public List<Parameter> Parameters { get; set; } = [];

    [JsonPropertyName("defaults")]
    [GoOmitEmpty]
    public Dictionary<string, object?> Defaults { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>画布节点贡献点。对应 Go: <c>protocol.ManifestCanvasNode</c>。</summary>
public sealed class ManifestCanvasNode
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("defaultTitle")]
    public string DefaultTitle { get; set; } = "";

    [JsonPropertyName("defaultSize")]
    public Dictionary<string, int> DefaultSize { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("schema")]
    public Dictionary<string, object?> Schema { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("renderer")]
    public string Renderer { get; set; } = "";
}

/// <summary>数据变换贡献点。对应 Go: <c>protocol.ManifestTransform</c>。</summary>
public sealed class ManifestTransform
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("input")]
    public string Input { get; set; } = "";

    [JsonPropertyName("output")]
    public string Output { get; set; } = "";

    [JsonPropertyName("runtime")]
    public string Runtime { get; set; } = "";
}

/// <summary>命令贡献点。对应 Go: <c>protocol.ManifestCommand</c>。</summary>
public sealed class ManifestCommand
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

/// <summary>
/// 唯一的插件契约。对应 Go: <c>protocol.Manifest</c>。
/// 顶层 <c>id</c>/<c>name</c>/<c>version</c>… 是线格式，<c>Metadata</c> 及其下方的
/// provider 投影字段只在运行时使用，不参与序列化（由 <see cref="ProtocolManifestCodec"/> 负责映射）。
/// </summary>
public sealed class Manifest
{
    public string APIVersion { get; set; } = "";

    public Metadata Metadata { get; set; } = new();

    public string Entry { get; set; } = "";

    public List<string> Surfaces { get; set; } = [];

    public ManifestRuntime Runtime { get; set; } = new();

    public List<string> Permissions { get; set; } = [];

    public ManifestConfiguration Configuration { get; set; } = new();

    public ManifestContributions Contributes { get; set; } = new();

    // 归一化后的 provider 投影：provider 执行只由 contributes.providers 描述，从不序列化。
    public ManifestOperation Create { get; set; } = new();

    public ManifestOperation? Agent { get; set; }

    public ManifestOperation? Poll { get; set; }

    public ManifestOperation? Cancel { get; set; }

    public ManifestOperation? ResultOperation { get; set; }

    public ManifestResponse Response { get; set; } = new();

    public ManifestAgentResponse? AgentResponse { get; set; }

    public ManifestAuth Auth { get; set; } = new();

    public List<ManifestValidation> Validations { get; set; } = [];
}
