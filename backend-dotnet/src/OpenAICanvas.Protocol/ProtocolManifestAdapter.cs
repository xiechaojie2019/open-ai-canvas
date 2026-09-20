#nullable enable

using System.Text.Json;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Protocol;

/// <summary>
/// 声明式清单适配器：把 create/poll/cancel/result/agent 操作经表达式引擎翻译成出站请求。
/// 对应 Go: <c>protocol.manifestAdapter</c>。只做协议翻译，凭证与执行由宿主负责。
/// </summary>
public sealed class ManifestAdapter(
    Manifest manifest) : IProtocolAdapter, IAgentProtocolAdapter, IAgentCapability, IResultAdapter, IResultCapability
{
    private readonly Manifest _manifest = manifest;

    public Metadata Metadata() => _manifest.Metadata;

    public bool AgentAvailable() => _manifest.Agent is not null && _manifest.AgentResponse is not null;

    public bool ResultAvailable() => _manifest.ResultOperation is not null;

    public RequestSpec BuildCreate(RequestContext context)
    {
        if (_manifest.Contributes.Providers.Count == 0)
        {
            throw new InvalidOperationException($"plugin {_manifest.Metadata.ID} does not provide a provider");
        }
        ProtocolManifestRuntime.ValidateManifestRequest(_manifest.Validations, context.Request);
        return ProtocolManifestOperation.Build(_manifest.Create, _manifest.Auth, context.Request, "");
    }

    public RequestSpec BuildAgent(AgentRequestContext context)
    {
        if (_manifest.Agent is null)
        {
            throw new InvalidOperationException($"protocol {_manifest.Metadata.ID} has no agent operation");
        }
        GenerationRequest request = new()
        {
            Model = context.Model,
            Extra = new Dictionary<string, object?>(StringComparer.Ordinal) { ["agent"] = context.Request },
        };
        return ProtocolManifestOperation.Build(_manifest.Agent, _manifest.Auth, request, "");
    }

    public AgentResult ParseAgent(byte[] body)
    {
        if (_manifest.AgentResponse is null)
        {
            throw new InvalidOperationException($"protocol {_manifest.Metadata.ID} has no agent response mapping");
        }
        Dictionary<string, object?> payload = ProtocolManifestRuntime.DecodeObject(body);
        ManifestAgentResponse response = _manifest.AgentResponse;
        AgentResult result = new()
        {
            Text = ProtocolManifestValues.FirstPathValue(payload, response.TextPaths),
            Reasoning = ProtocolManifestValues.FirstPathValue(payload, response.ReasoningPaths),
        };
        if (response.ToolCallsPath.Length == 0)
        {
            return result;
        }
        int index = 0;
        foreach (object? item in ProtocolManifestValues.ArrayValue(
                     ProtocolManifestValues.PathValue(payload, response.ToolCallsPath)))
        {
            if (item is Dictionary<string, object?> call)
            {
                AgentToolCall toolCall = new()
                {
                    ID = ProtocolManifestValues.FirstPathValue(call, response.ToolCallIDPaths),
                    Name = ProtocolManifestValues.FirstPathValue(call, response.ToolCallNamePaths),
                    Arguments = ProtocolManifestValues.FirstPathJsonValue(call, response.ToolCallArgsPaths),
                    ThoughtSignature = ProtocolManifestValues.FirstPathValue(call, response.ToolCallSignaturePaths),
                };
                if (toolCall.Name.Trim().Length != 0)
                {
                    if (toolCall.ID.Trim().Length == 0)
                    {
                        toolCall.ID = ProtocolManifestRuntime.SyntheticAgentToolCallID(body, index);
                    }
                    result.ToolCalls.Add(toolCall);
                }
            }
            index++;
        }
        return result;
    }

    public CreateResult ParseCreate(byte[] body)
    {
        if (_manifest.Response.BinaryPayload)
        {
            return ProtocolManifestRuntime.BinaryPayloadCreateResult(_manifest.Response.ResultKind, body);
        }
        Dictionary<string, object?> payload = ProtocolManifestRuntime.DecodeObject(body);
        return Parse(payload, new PollContext());
    }

    public RequestSpec BuildPoll(PollContext context)
    {
        if (_manifest.Poll is null)
        {
            throw new InvalidOperationException($"protocol {_manifest.Metadata.ID} has no poll operation");
        }
        return ProtocolManifestOperation.Build(
            _manifest.Poll, _manifest.Auth, WithModel(context.Request, context.Model), context.TaskID);
    }

    public PollResult ParsePoll(PollContext context, byte[] body)
    {
        Dictionary<string, object?> payload = ProtocolManifestRuntime.DecodeObject(body);
        CreateResult result = Parse(payload, context);
        return new PollResult
        {
            TaskID = result.TaskID,
            Status = result.Status,
            Result = result.Result,
            Message = result.Message,
        };
    }

    public RequestSpec BuildCancel(PollContext context)
    {
        if (_manifest.Cancel is null)
        {
            throw new InvalidOperationException($"protocol {_manifest.Metadata.ID} does not support cancellation");
        }
        return ProtocolManifestOperation.Build(
            _manifest.Cancel, _manifest.Auth, WithModel(context.Request, context.Model), context.TaskID);
    }

    public RequestSpec BuildResult(PollContext context)
    {
        if (_manifest.ResultOperation is null)
        {
            throw new InvalidOperationException($"protocol {_manifest.Metadata.ID} has no result operation");
        }
        return ProtocolManifestOperation.Build(
            _manifest.ResultOperation, _manifest.Auth, WithModel(context.Request, context.Model), context.TaskID);
    }

    /// <summary>
    /// 对应 Go: <c>manifestAdapter.parse</c>。顺序敏感：状态先归一，再叠加错误位，
    /// 最后按「Pending 但已有输出」提升为 Succeeded。
    /// </summary>
    internal CreateResult Parse(Dictionary<string, object?> payload, PollContext context)
    {
        ManifestResponse response = _manifest.Response;
        Dictionary<string, object?> env = new(StringComparer.Ordinal)
        {
            ["response"] = payload,
            ["taskId"] = context.TaskID,
            ["request"] = ProtocolManifestValues.RequestValues(context.Request),
        };
        string id = ProtocolManifestResponse.ManifestResponseString(response.TaskID, env);
        if (id.Length == 0)
        {
            id = ProtocolManifestValues.FirstPathValue(payload, response.TaskIDPaths);
        }
        if (id.Length == 0)
        {
            id = context.TaskID;
        }
        string statusText = ProtocolManifestResponse.ManifestResponseString(response.Status, env);
        if (statusText.Length == 0)
        {
            statusText = ProtocolManifestValues.FirstPathValue(payload, response.StatusPaths);
        }
        string status = ProtocolManifestRuntime.NormalizeStatus(statusText);
        if (status.Length == 0)
        {
            status = ProtocolStatus.Pending;
        }
        string message = ProtocolManifestResponse.ManifestResponseString(response.Message, env);
        if (message.Length == 0)
        {
            message = ProtocolManifestValues.FirstPathValue(payload, response.MessagePaths);
        }
        if (ProtocolManifestValues.ManifestError(payload, response.ErrorPaths))
        {
            status = ProtocolStatus.Failed;
        }

        Result? result = new()
        {
            Text = ProtocolManifestResponse.ManifestResponseString(response.Text, env),
            Reasoning = ProtocolManifestResponse.ManifestResponseString(response.Reasoning, env),
        };
        if (result.Text.Length == 0)
        {
            result.Text = ProtocolManifestValues.FirstPathValue(payload, response.TextPaths);
        }
        if (result.Reasoning.Length == 0)
        {
            result.Reasoning = ProtocolManifestValues.FirstPathValue(payload, response.ReasoningPaths);
        }
        result.Images = ProtocolManifestResponse.ManifestResponseMedia(response.Images, env, "image", response.ResultEphemeral);
        result.Videos = ProtocolManifestResponse.ManifestResponseMedia(response.Videos, env, "video", response.ResultEphemeral);
        result.Audios = ProtocolManifestResponse.ManifestResponseMedia(response.Audios, env, "audio", response.ResultEphemeral);
        if (response.Usage is not null)
        {
            try
            {
                result.Usage = ProtocolExpression.ObjectOf(ProtocolExpression.Evaluate(response.Usage, env));
            }
            catch (Exception)
            {
                // Go 侧同样忽略 usage 表达式错误，保留其余结果。
            }
        }
        foreach (string path in response.ResultURLPaths.Concat(response.ResultPaths))
        {
            foreach (string value in ProtocolManifestResponse.MediaPathValues(payload, path))
            {
                MediaReference item = new()
                {
                    URL = value,
                    Kind = response.ResultKind,
                    Ephemeral = response.ResultEphemeral,
                };
                switch (response.ResultKind)
                {
                    case "image":
                        result.Images.Add(item);
                        break;
                    case "audio":
                        result.Audios.Add(item);
                        break;
                    default:
                        result.Videos.Add(item);
                        break;
                }
            }
        }
        if (status == ProtocolStatus.Pending &&
            (result.Text.Length != 0 || result.Images.Count > 0 || result.Videos.Count > 0 || result.Audios.Count > 0))
        {
            status = ProtocolStatus.Succeeded;
        }
        if (result.Text.Length == 0 && result.Reasoning.Length == 0 &&
            result.Images.Count == 0 && result.Videos.Count == 0 && result.Audios.Count == 0)
        {
            result = null;
        }
        return new CreateResult { TaskID = id, Status = status, Result = result, Message = message };
    }

    /// <summary>Go 的 <c>request := c.Request</c> 是值拷贝；这里浅拷贝后再覆盖 Model，避免改动调用方对象。</summary>
    private static GenerationRequest WithModel(GenerationRequest source, string model) => new()
    {
        Capability = source.Capability,
        Model = model,
        Prompt = source.Prompt,
        Instructions = source.Instructions,
        Messages = source.Messages,
        Inputs = source.Inputs,
        Images = source.Images,
        Videos = source.Videos,
        Audios = source.Audios,
        Duration = source.Duration,
        AspectRatio = source.AspectRatio,
        Resolution = source.Resolution,
        Quality = source.Quality,
        GenerateAudio = source.GenerateAudio,
        Watermark = source.Watermark,
        ImageCount = source.ImageCount,
        Operation = source.Operation,
        Output = source.Output,
        ProviderOptions = source.ProviderOptions,
        Extra = source.Extra,
    };
}

/// <summary>
/// 元数据包装适配器：host 后端插件的清单元数据覆盖宿主适配器的自描述元数据。
/// 对应 Go: <c>protocol.metadataAdapter</c>。能力探测按方法集委派，缺失面抛不可用错误。
/// </summary>
public sealed class MetadataAdapter(
    Metadata metadata,
    IProtocolAdapter @delegate) : IProtocolAdapter, IAgentProtocolAdapter, IAgentCapability, IResultAdapter, IResultCapability
{
    private readonly Metadata _metadata = metadata;
    private readonly IProtocolAdapter _delegate = @delegate;

    public Metadata Metadata() => _metadata;

    public bool AgentAvailable() => _delegate is IAgentCapability capability && capability.AgentAvailable();

    public bool ResultAvailable() => _delegate is IResultCapability capability && capability.ResultAvailable();

    public RequestSpec BuildCreate(RequestContext context) => _delegate.BuildCreate(context);

    public CreateResult ParseCreate(byte[] body) => _delegate.ParseCreate(body);

    public RequestSpec BuildPoll(PollContext context) => _delegate.BuildPoll(context);

    public PollResult ParsePoll(PollContext context, byte[] body) => _delegate.ParsePoll(context, body);

    public RequestSpec BuildCancel(PollContext context) => _delegate.BuildCancel(context);

    public RequestSpec BuildResult(PollContext context) =>
        _delegate is IResultAdapter adapter
            ? adapter.BuildResult(context)
            : throw new ProtocolUnavailableException(_metadata.ID, _metadata.UnavailableReason);

    public RequestSpec BuildAgent(AgentRequestContext context) =>
        _delegate is IAgentProtocolAdapter adapter
            ? adapter.BuildAgent(context)
            : throw new ProtocolUnavailableException(_metadata.ID, _metadata.UnavailableReason);

    public AgentResult ParseAgent(byte[] body) =>
        _delegate is IAgentProtocolAdapter adapter
            ? adapter.ParseAgent(body)
            : throw new ProtocolUnavailableException(_metadata.ID, _metadata.UnavailableReason);
}

/// <summary>
/// 声明式适配器共用的运行时工具：状态归一、JSON 解码、二进制载荷包装与参数校验。
/// 对应 Go: <c>builtin.go</c> 的 <c>normalizeStatus</c>/<c>decodeObject</c> 与
/// <c>manifest.go</c> 的 <c>binaryPayloadCreateResult</c>/<c>validateManifestRequest</c>。
/// </summary>
internal static class ProtocolManifestRuntime
{
    /// <summary>对应 Go: <c>normalizeStatus</c>。未知状态返回空字符串，由调用方回落 Pending。</summary>
    public static string NormalizeStatus(string raw)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "queued":
            case "pending":
            case "created":
            case "submitted":
            case "in_queue":
            case "task_status_queued":
                return ProtocolStatus.Pending;
            case "running":
            case "processing":
            case "in_progress":
            case "executing":
            case "task_status_running":
                return ProtocolStatus.Processing;
            case "succeeded":
            case "success":
            case "completed":
            case "complete":
            case "done":
            case "task_status_succeed":
                return ProtocolStatus.Succeeded;
            case "cancelled":
            case "canceled":
            case "aborted":
                return ProtocolStatus.Cancelled;
            case "failed":
            case "failure":
            case "error":
            case "expired":
                return ProtocolStatus.Failed;
            default:
                return "";
        }
    }

    /// <summary>对应 Go: <c>decodeObject</c>。数字统一按 double 承载，与 Go 的 encoding/json 一致。</summary>
    public static Dictionary<string, object?> DecodeObject(byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"protocol response is not JSON: {error.Message}", error);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("protocol response is not a JSON object");
            }
            return JsonFields.FromElement(document.RootElement) as Dictionary<string, object?> ?? [];
        }
    }

    /// <summary>对应 Go: <c>binaryPayloadCreateResult</c>。MIME 以内容探测为准，空响应必须失败。</summary>
    public static CreateResult BinaryPayloadCreateResult(string resultKind, byte[] body)
    {
        if (body.Length == 0)
        {
            throw new InvalidOperationException("binary payload response is empty");
        }
        string mimeType = ContentTypeSniffer.Sniff(body).Split(';', 2)[0].Trim().ToLowerInvariant();
        MediaReference reference = new()
        {
            DataURL = "data:" + mimeType + ";base64," + Convert.ToBase64String(body),
            MIMEType = mimeType,
        };
        Result result = new();
        switch (resultKind)
        {
            case "audio":
                result.Audios.Add(reference);
                break;
            case "image":
                result.Images.Add(reference);
                break;
            case "video":
                result.Videos.Add(reference);
                break;
            default:
                throw new InvalidOperationException(
                    $"binary payload response requires resultKind image, video or audio, got \"{resultKind}\"");
        }
        return new CreateResult { Status = ProtocolStatus.Succeeded, Result = result };
    }

    /// <summary>对应 Go: <c>validateManifestRequest</c>。断言为假时返回 message，表达式错误带上下文包装。</summary>
    public static void ValidateManifestRequest(List<ManifestValidation> rules, GenerationRequest request)
    {
        if (rules.Count == 0)
        {
            return;
        }
        Dictionary<string, object?> env = new(StringComparer.Ordinal)
        {
            ["request"] = ProtocolManifestValues.RequestValues(request),
        };
        foreach (ManifestValidation rule in rules)
        {
            object? value;
            try
            {
                value = ProtocolExpression.Evaluate(rule.Assert, env);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"协议参数校验表达式错误：{error.Message}", error);
            }
            if (!ProtocolExpression.Truthy(value))
            {
                throw new InvalidOperationException(rule.Message.Trim());
            }
        }
    }

    /// <summary>对应 Go: <c>syntheticAgentToolCallID</c>：sha256(body#index) 前 8 字节的 hex。</summary>
    public static string SyntheticAgentToolCallID(byte[] body, int index)
    {
        string material = $"{System.Text.Encoding.UTF8.GetString(body)}#{index}";
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));
        return "call_" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
