#nullable enable
using System.Text;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Providers;

/// <summary>
/// 文本任务的出站执行（请求 → 传输 → 解析 → 结果）。
/// 对应 Go: <c>internal/app/provider_text.go</c> 的
/// <c>requestTextProvider</c> / <c>postStreamingTextResult</c> / <c>postStreamingAgent</c> /
/// <c>runTextTask</c> 的三协议分支。
/// </summary>
/// <remarks>
/// 与 Go 的差异：渠道并发槽、熔断、计费审计（<c>providerAnalyticsContext</c>）属运行时接线，
/// 通过 <see cref="IProviderRequestContext"/> 抽象注入；未提供时退化为纯传输行为。
/// </remarks>
public sealed class ProviderTextTask
{
    private readonly Func<HttpClient>? _clientFactory;
    private readonly IProviderRequestContext? _context;

    /// <param name="context">运行时上下文（渠道并发/熔断/响应上限）；可为 <c>null</c>。</param>
    /// <param name="clientFactory">测试注入的出站客户端工厂；<c>null</c> 走生产路径。</param>
    public ProviderTextTask(IProviderRequestContext? context = null, Func<HttpClient>? clientFactory = null)
    {
        _context = context;
        _clientFactory = clientFactory;
    }

    /// <summary>上游响应字节上限。对应 Go 读取运行时策略后的 <c>responseLimit</c>。</summary>
    private long MaxResponseBytes => _context?.MaxResponseBytes ?? ProviderTransport.DefaultMaxResponseBytes;

    /// <summary>
    /// 文本任务总入口：按渠道 <c>interfaceType</c> 分发到对应协议实现。
    /// 对应 Go: <c>runTextTask</c>。
    /// </summary>
    /// <remarks>
    /// 未识别（含空）的 interfaceType 走 legacy 分支 —— 即先试 Responses、
    /// 遇到路径不存在再回落 Chat Completions。这与 Go 的 <c>default</c> 分支一致。
    /// </remarks>
    public Task<Dictionary<string, object?>> RunTextTaskAsync(
        TextTaskInput input,
        Action<string>? onDelta = null,
        Action<string>? onReasoningDelta = null,
        CancellationToken cancellationToken = default)
    {
        string interfaceType = (input.Config.InterfaceType ?? "").Trim();
        IProtocolAdapter? declarative = _context?.DeclarativeAdapter?.Resolve(interfaceType);
        if (declarative is not null
            && declarative.Metadata().Enabled
            && declarative.Metadata().UnavailableReason.Trim().Length == 0
            && declarative.Metadata().Execution == "declarative"
            && declarative.Metadata().Categories.Contains(ProtocolCapability.Text, StringComparer.Ordinal))
        {
            return RunDeclarativeAsync(input, declarative, onDelta, onReasoningDelta, cancellationToken);
        }
        return interfaceType switch
        {
            ProviderTextOrchestration.ChatCompletionProtocol =>
                RunAsync(input, ProviderTextOrchestration.ChatCompletionProtocol, onDelta, onReasoningDelta, cancellationToken),
            "openai-response" =>
                RunAsync(input, ProviderTextOrchestration.ResponsesProtocol, onDelta, onReasoningDelta, cancellationToken),
            ProviderTextOrchestration.ClaudeProtocol =>
                RunAsync(input, ProviderTextOrchestration.ClaudeProtocol, onDelta, onReasoningDelta, cancellationToken),
            _ => RunLegacyAsync(input, onDelta, onReasoningDelta, cancellationToken),
        };
    }

    /// <summary>
    /// 执行声明式文本协议。清单负责请求字段和响应路径，宿主仍统一负责限流、熔断、鉴权和错误传播。
    /// </summary>
    private async Task<Dictionary<string, object?>> RunDeclarativeAsync(
        TextTaskInput input,
        IProtocolAdapter adapter,
        Action<string>? onDelta,
        Action<string>? onReasoningDelta,
        CancellationToken cancellationToken)
    {
        string channelId = input.Config.ChannelID;
        if (_context is not null
            && channelId.Length > 0
            && await _context.IsCircuitOpenAsync(channelId, cancellationToken).ConfigureAwait(false))
        {
            throw new ProviderCircuitOpenException();
        }

        Func<ValueTask>? release = _context is null
            ? null
            : await _context.AcquireChannelSlotAsync(channelId, "", cancellationToken).ConfigureAwait(false);
        try
        {
            ProtocolGenerationRequest source = ProviderProtocolPayload.FromInput(input);
            // 文本后台任务必须拿到最终 JSON；即使调用方要求流式，也把完整结果一次性回调，
            // 避免把不声明 SSE 的插件响应误判为永久等待。
            source.Extra["stream"] = false;
            GenerationRequest request = ToProtocolRequest(source);
            RequestSpec spec = adapter.BuildCreate(new RequestContext
            {
                BaseURL = input.Config.BaseURL,
                Request = request,
            });
            byte[] body = await ProviderProtocolExecutor.ExecuteAsync(
                input.Config, spec, cancellationToken: cancellationToken, clientFactory: _clientFactory)
                .ConfigureAwait(false);
            CreateResult created = adapter.ParseCreate(body);
            if (created.Status is ProtocolStatus.Failed or ProtocolStatus.Cancelled)
            {
                throw ProviderProtocolPayload.ResultError(created.Message, created.TaskID);
            }
            if (created.Result is null || created.Result.Text.Trim().Length == 0)
            {
                throw new InvalidOperationException("声明式文本接口没有返回内容");
            }
            ProviderTextResult result = new(created.Result.Text, created.Result.Reasoning);
            onReasoningDelta?.Invoke(result.Reasoning);
            onDelta?.Invoke(result.Text);
            if (_context is not null && channelId.Length > 0)
            {
                await _context.RecordChannelResultAsync(channelId, false, cancellationToken).ConfigureAwait(false);
            }
            return ProviderTextOrchestration.TextTaskResult(result);
        }
        catch (Exception)
        {
            if (_context is not null && channelId.Length > 0)
            {
                try
                {
                    await _context.RecordChannelResultAsync(channelId, true, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // 熔断记账是旁路，不能覆盖上游错误。
                }
            }
            throw;
        }
        finally
        {
            if (release is not null)
            {
                await release().ConfigureAwait(false);
            }
        }
    }

    private static GenerationRequest ToProtocolRequest(ProtocolGenerationRequest source) => new()
    {
        Capability = source.Capability,
        Model = source.Model,
        Prompt = source.Prompt,
        Instructions = source.Instructions,
        Messages = [.. source.Messages.Select(message => new Message
        {
            Role = message.Role,
            Content = message.Content,
        })],
        Inputs = [.. source.Inputs],
        Images = [.. source.Images],
        Videos = [.. source.Videos],
        Audios = [.. source.Audios],
        AspectRatio = source.AspectRatio,
        Resolution = source.Resolution,
        Quality = source.Quality,
        GenerateAudio = source.GenerateAudio,
        Watermark = source.Watermark,
        Operation = source.Operation,
        Duration = source.Duration,
        ImageCount = source.ImageCount,
        Output = source.Output,
        ProviderOptions = source.ProviderOptions,
        Extra = source.Extra,
    };

    /// <summary>
    /// 执行文本任务：按协议构造请求体并解析结果。
    /// 对应 Go: <c>runResponsesTextTask</c> / <c>runChatCompletionsTextTask</c> / <c>runClaudeTextTask</c>。
    /// </summary>
    /// <remarks>流式与否取自 <see cref="TextTaskInput"/> 的 <c>TextOptions.Stream</c>，与 Go 的 <c>input.StreamText</c> 对应。</remarks>
    public async Task<Dictionary<string, object?>> RunAsync(
        TextTaskInput input,
        string protocol,
        Action<string>? onDelta = null,
        Action<string>? onReasoningDelta = null,
        CancellationToken cancellationToken = default)
    {
        ProviderTextResult result = await RequestAsync(
            input, protocol, input.TextOptions.Stream ?? false, onDelta, onReasoningDelta, cancellationToken)
            .ConfigureAwait(false);
        return ProviderTextOrchestration.TextTaskResult(result);
    }

    /// <summary>
    /// 单次上游请求：非流式解析 JSON，流式消费 SSE；两者都要求非空正文。
    /// 对应 Go: <c>requestTextProvider</c>。
    /// </summary>
    public async Task<ProviderTextResult> RequestAsync(
        TextTaskInput input,
        string protocol,
        bool stream,
        Action<string>? onDelta = null,
        Action<string>? onReasoningDelta = null,
        CancellationToken cancellationToken = default)
    {
        string channelId = input.Config.ChannelID;

        // 熔断前置检查：打开时直接短路，不再占用并发槽、不再发出请求。
        if (_context is not null
            && channelId.Length > 0
            && await _context.IsCircuitOpenAsync(channelId, cancellationToken).ConfigureAwait(false))
        {
            throw new ProviderCircuitOpenException();
        }

        // 并发槽可能为 null（无协调器或未配置限流），此时直接执行。
        Func<ValueTask>? release = _context is null
            ? null
            : await _context.AcquireChannelSlotAsync(channelId, "", cancellationToken).ConfigureAwait(false);

        try
        {
            ProviderTextResult result = stream
                ? await StreamingAsync(input, protocol, onDelta, onReasoningDelta, cancellationToken).ConfigureAwait(false)
                : await NonStreamingAsync(input, protocol, cancellationToken).ConfigureAwait(false);

            if (_context is not null && channelId.Length > 0)
            {
                await _context.RecordChannelResultAsync(channelId, false, cancellationToken).ConfigureAwait(false);
            }
            return result;
        }
        catch (Exception)
        {
            // 与 Go 一致：记录失败但不吞掉原始异常（熔断记账失败也不影响主流程）。
            if (_context is not null && channelId.Length > 0)
            {
                try
                {
                    await _context.RecordChannelResultAsync(channelId, true, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略：熔断记账是旁路，不能影响原始错误的传播。
                }
            }
            throw;
        }
        finally
        {
            if (release is not null)
            {
                await release().ConfigureAwait(false);
            }
        }
    }

    /// <summary>非流式：<c>postJSON</c> + <c>parseAgentToolPayload</c>。</summary>
    private async Task<ProviderTextResult> NonStreamingAsync(
        TextTaskInput input, string protocol, CancellationToken cancellationToken)
    {
        // 与 Go 一致：非流式请求必须移除 stream（避免上游按 SSE 返回）。
        Dictionary<string, object?> body = ProviderTextRequestBuilder.BuildBody(input, protocol);
        body.Remove("stream");

        using HttpRequestMessage request = ProviderTransport.BuildJsonPost(input.Config, PathFor(protocol), body);
        Dictionary<string, object?> payload =
            await ProviderTransport
                .SendJsonAsync(request, MaxResponseBytes, cancellationToken, _clientFactory)
                .ConfigureAwait(false);

        Dictionary<string, object?> parsed = AgentToolPayload.Parse(payload, protocol);
        return ProviderTextOrchestration.RequireText(parsed, streaming: false);
    }

    /// <summary>流式：<c>postStreamingBinary</c> + <c>streamingAgentParser</c>。</summary>
    private async Task<ProviderTextResult> StreamingAsync(
        TextTaskInput input,
        string protocol,
        Action<string>? onDelta,
        Action<string>? onReasoningDelta,
        CancellationToken cancellationToken)
    {
        Dictionary<string, object?> body = ProviderTextRequestBuilder.BuildBody(input, protocol);
        body["stream"] = true;
        if (protocol == ProviderTextOrchestration.ChatCompletionProtocol)
        {
            ProviderTextOrchestration.EnsureChatCompletionStreamUsage(body);
        }

        StreamingAgentParser parser = new(protocol, onDelta) { EmitReasoning = onReasoningDelta };
        using HttpRequestMessage request =
            ProviderTransport.BuildJsonPost(input.Config, PathFor(protocol), body, streaming: true);

        ProviderTransport.OutboundResult result = await ProviderTransport.SendAsync(
            request,
            MaxResponseBytes,
            (mimeType, chunk) => parser.Consume(mimeType, chunk),
            cancellationToken,
            _clientFactory).ConfigureAwait(false);

        // 上游可能忽略 stream 参数直接回 JSON，此时按非流式解析（与 Go 一致）。
        if (!result.MIMEType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            Dictionary<string, object?>? payload = ProviderTransport.ParseObject(result.Data);
            if (payload is null)
            {
                throw new InvalidOperationException("Agent 接口返回格式无效：响应不是 JSON 对象");
            }
            Dictionary<string, object?> parsed = AgentToolPayload.Parse(payload, protocol);
            return ProviderTextOrchestration.RequireText(parsed, streaming: true);
        }

        parser.Flush();
        Dictionary<string, object?> streamed = parser.Result();
        return ProviderTextOrchestration.RequireText(streamed, streaming: true);
    }

    /// <summary>
    /// 文本任务的 legacy 回落：Responses 失败且上游明确表示路径不存在时改用 Chat Completions。
    /// 对应 Go: <c>runLegacyTextTask</c>。
    /// </summary>
    public async Task<Dictionary<string, object?>> RunLegacyAsync(
        TextTaskInput input,
        Action<string>? onDelta = null,
        Action<string>? onReasoningDelta = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunAsync(
                input, ProviderTextOrchestration.ResponsesProtocol,
                onDelta, onReasoningDelta, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (ProviderTextOrchestration.ShouldFallbackTextToChat(error))
        {
            try
            {
                return await RunAsync(
                    input, ProviderTextOrchestration.ChatCompletionProtocol,
                    onDelta, onReasoningDelta, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception chatError)
            {
                throw new InvalidOperationException(
                    $"文本接口请求失败：Responses API {error.Message}；Chat Completions {chatError.Message}",
                    chatError);
            }
        }
    }

    /// <summary>协议到上游路径的映射。对应 Go 的 <c>path</c> 变量。</summary>
    public static string PathFor(string protocol) => protocol switch
    {
        ProviderTextOrchestration.ResponsesProtocol => "/responses",
        ProviderTextOrchestration.ClaudeProtocol => "/messages",
        _ => "/chat/completions",
    };

    // ------------------------------------------------------------ Agent 工具循环

    /// <summary>
    /// 执行 Agent 工具任务：协议中立请求只在此刻展开为选中渠道的供应商请求体，
    /// 防止供应商请求体反向污染任务记录，也避免切换模型时复用错误协议。
    /// 对应 Go: <c>runAgentToolTask</c>。
    /// </summary>
    public async Task<Dictionary<string, object?>> RunAgentToolAsync(
        TextTaskInput input,
        bool stream,
        Action<string>? onDelta = null,
        Action<string>? onReasoningDelta = null,
        CancellationToken cancellationToken = default)
    {
        string interfaceType = (input.Config.InterfaceType ?? "").Trim();
        IAgentProtocolAdapter? agentAdapter = AgentDeclarativeAdapter(interfaceType, out IProtocolAdapter? adapter);
        if (input.AgentRequests?.Canonical is { } canonical)
        {
            input.AgentRequests = ProviderAgentProtocol.ExpandCanonicalAgentRequest(
                canonical, input.Config, agentAdapter is not null);
        }
        if (agentAdapter is not null)
        {
            return await RunDeclarativeAgentAsync(
                input, stream, adapter!, agentAdapter, onDelta, onReasoningDelta,
                cancellationToken).ConfigureAwait(false);
        }
        if (input.AgentRequests is null)
        {
            throw new InvalidOperationException("画布 Agent 工具请求缺少协议参数");
        }
        string protocol = ProviderTextOrchestration.ChatCompletionProtocol;
        string path = "/chat/completions";
        Dictionary<string, object?>? request = input.AgentRequests.ChatCompletion;
        if (interfaceType == ChannelInterfaceType.ChannelInterfaceOpenAIResponse)
        {
            request = input.AgentRequests.Responses;
            path = "/responses";
            protocol = ProviderTextOrchestration.ResponsesProtocol;
        }
        else if (interfaceType == ChannelInterfaceType.ChannelInterfaceClaudeAPI)
        {
            request = input.AgentRequests.Claude ?? request;
            path = "/messages";
            protocol = ProviderTextOrchestration.ClaudeProtocol;
        }
        if (request is null)
        {
            throw new InvalidOperationException("画布 Agent 工具请求缺少协议参数");
        }
        Dictionary<string, object?> body = new(request, StringComparer.Ordinal);
        if (protocol == ProviderTextOrchestration.ClaudeProtocol && input.AgentRequests.Claude is null)
        {
            // 渠道切换后只有 Chat 形态请求时的回落改写（与 Go 一致）。
            body = ProviderAgentProtocol.ClaudeAgentBody(body);
        }
        body["model"] = input.Config.Model;
        ProviderTextOrchestration.ApplyTextThinking(body, input.TextOptions, protocol);
        ApplyAgentOutputLimit(body, AgentStepOutputLimit(input), protocol);
        ProviderTextOrchestration.NormalizeAgentToolChoice(body, input.TextOptions, protocol);

        Dictionary<string, object?>? result = null;
        try
        {
            result = await PostAgentRequestAsync(
                input, stream, path, body, protocol, onDelta, onReasoningDelta,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (
            protocol == ProviderTextOrchestration.ChatCompletionProtocol
            && ProviderTextOrchestration.IsAgentToolChoiceCompatibilityError(error))
        {
            if (!ProviderTextOrchestration.IsAutoAgentToolChoice(
                    body.TryGetValue("tool_choice", out object? toolChoice) ? toolChoice : null))
            {
                Dictionary<string, object?> autoBody = new(body, StringComparer.Ordinal)
                {
                    ["tool_choice"] = "auto",
                };
                try
                {
                    return await PostAgentRequestAsync(
                        input, stream, path, autoBody, protocol, onDelta, onReasoningDelta,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception autoError)
                    when (ProviderTextOrchestration.IsAgentToolChoiceCompatibilityError(autoError))
                {
                    // 与 Go 一致：自动选择仍不兼容时去掉 tool_choice 再试。
                }
            }
            Dictionary<string, object?> plainBody = new(body, StringComparer.Ordinal);
            plainBody.Remove("tool_choice");
            result = await PostAgentRequestAsync(
                input, stream, path, plainBody, protocol, onDelta, onReasoningDelta,
                cancellationToken).ConfigureAwait(false);
        }
        return result!;
    }

    /// <summary>对应 Go: <c>postAgentRequest</c>：流式走 SSE 解析，非流式走 JSON + 工具载荷解析。</summary>
    private async Task<Dictionary<string, object?>> PostAgentRequestAsync(
        TextTaskInput input,
        bool stream,
        string path,
        Dictionary<string, object?> body,
        string protocol,
        Action<string>? onDelta,
        Action<string>? onReasoningDelta,
        CancellationToken cancellationToken)
    {
        if (stream)
        {
            body["stream"] = true;
            if (protocol == ProviderTextOrchestration.ChatCompletionProtocol)
            {
                ProviderTextOrchestration.EnsureChatCompletionStreamUsage(body);
            }
            StreamingAgentParser parser = new(protocol, onDelta) { EmitReasoning = onReasoningDelta };
            using HttpRequestMessage request =
                ProviderTransport.BuildJsonPost(input.Config, path, body, streaming: true);
            ProviderTransport.OutboundResult outbound = await ProviderTransport.SendAsync(
                request,
                MaxResponseBytes,
                (mimeType, chunk) => parser.Consume(mimeType, chunk),
                cancellationToken,
                _clientFactory).ConfigureAwait(false);
            // 上游可能忽略 stream 参数直接回 JSON，此时按非流式解析（与 Go 一致）。
            if (!outbound.MIMEType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object?>? payload = ProviderTransport.ParseObject(outbound.Data)
                    ?? throw new InvalidOperationException("Agent 接口返回格式无效：响应不是 JSON 对象");
                return AgentToolPayload.Parse(payload, protocol);
            }
            parser.Flush();
            return parser.Result();
        }
        body.Remove("stream");
        using HttpRequestMessage jsonRequest = ProviderTransport.BuildJsonPost(input.Config, path, body);
        Dictionary<string, object?> jsonPayload = await ProviderTransport
            .SendJsonAsync(jsonRequest, MaxResponseBytes, cancellationToken, _clientFactory)
            .ConfigureAwait(false);
        return AgentToolPayload.Parse(jsonPayload, protocol);
    }

    /// <summary>
    /// 声明式 Agent 渠道：清单负责请求体与响应解析，宿主负责鉴权、限流与思考/上限收尾。
    /// 对应 Go: <c>runDeclarativeAgentTask</c>。
    /// </summary>
    private async Task<Dictionary<string, object?>> RunDeclarativeAgentAsync(
        TextTaskInput input,
        bool stream,
        IProtocolAdapter adapter,
        IAgentProtocolAdapter agentAdapter,
        Action<string>? onDelta,
        Action<string>? onReasoningDelta,
        CancellationToken cancellationToken)
    {
        string wire = (input.Config.InterfaceType ?? "").Trim();
        if (wire == ChannelInterfaceType.ChannelInterfaceOpenAIResponse)
        {
            wire = ProviderTextOrchestration.ResponsesProtocol;
        }
        bool knownWire = wire is ProviderTextOrchestration.ChatCompletionProtocol
            or ProviderTextOrchestration.ResponsesProtocol
            or ProviderTextOrchestration.ClaudeProtocol;
        if (input.TextOptions.Thinking && !knownWire)
        {
            throw new InvalidOperationException("当前声明式 Agent 渠道尚不支持思考模式，请关闭思考模式或切换内置协议渠道");
        }
        AgentToolRequestsInput requests = input.AgentRequests
            ?? throw new InvalidOperationException("画布 Agent 工具请求缺少协议参数");
        Dictionary<string, object?> request = new(StringComparer.Ordinal)
        {
            ["chatCompletion"] = requests.ChatCompletion,
            ["responses"] = requests.Responses,
            ["claude"] = requests.Claude,
            ["gemini"] = requests.Gemini,
        };
        RequestSpec spec = agentAdapter.BuildAgent(new AgentRequestContext
        {
            BaseURL = input.Config.BaseURL,
            Model = input.Config.Model,
            Request = request,
        });
        if (knownWire)
        {
            if (spec.Body is not Dictionary<string, object?> body)
            {
                throw new InvalidOperationException("声明式 Agent 请求体必须是 JSON 对象");
            }
            ProviderTextOrchestration.ApplyTextThinking(body, input.TextOptions, wire);
            ApplyAgentOutputLimit(body, AgentStepOutputLimit(input), wire);
            ProviderTextOrchestration.NormalizeAgentToolChoice(body, input.TextOptions, wire);
            if (stream)
            {
                body["stream"] = true;
                if (wire == ProviderTextOrchestration.ChatCompletionProtocol)
                {
                    ProviderTextOrchestration.EnsureChatCompletionStreamUsage(body);
                }
                StreamingAgentParser parser = new(wire, onDelta) { EmitReasoning = onReasoningDelta };
                (byte[] data, string mimeType) = await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
                    input.Config, spec, (chunkType, chunk) => parser.Consume(chunkType, chunk),
                    cancellationToken: cancellationToken, clientFactory: _clientFactory).ConfigureAwait(false);
                if (!mimeType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, object?>? payload = ProviderTransport.ParseObject(data)
                        ?? throw new InvalidOperationException("Agent 接口返回格式无效：响应不是 JSON 对象");
                    return AgentToolPayload.Parse(payload, wire);
                }
                parser.Flush();
                return parser.Result();
            }
        }
        byte[] raw = await ProviderProtocolExecutor.ExecuteAsync(
            input.Config, spec, cancellationToken: cancellationToken, clientFactory: _clientFactory)
            .ConfigureAwait(false);
        AgentResult parsed = agentAdapter.ParseAgent(raw);
        Dictionary<string, object?> result = new(StringComparer.Ordinal)
        {
            ["mode"] = "text",
            ["text"] = parsed.Text,
            ["toolCalls"] = new List<object?>(),
        };
        if (parsed.Reasoning.Length > 0)
        {
            result["reasoning"] = parsed.Reasoning;
        }
        List<object?> calls = [];
        foreach (AgentToolCall call in parsed.ToolCalls)
        {
            Dictionary<string, object?> mapped = new(StringComparer.Ordinal)
            {
                ["id"] = call.ID,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.Arguments,
                },
            };
            if (call.ThoughtSignature.Length > 0)
            {
                mapped["thoughtSignature"] = call.ThoughtSignature;
            }
            calls.Add(mapped);
        }
        result["toolCalls"] = calls;
        if (parsed.Text.Trim().Length == 0 && calls.Count == 0)
        {
            throw new InvalidOperationException("声明式 Agent 接口没有返回内容");
        }
        return result;
    }

    /// <summary>
    /// 按接口类型解析可用的声明式 Agent 适配器。
    /// 对应 Go: <c>generation.AgentProtocolAdapterForContext</c>（Execution=declarative + AgentAvailable）。
    /// </summary>
    private IAgentProtocolAdapter? AgentDeclarativeAdapter(
        string interfaceType, out IProtocolAdapter? adapter)
    {
        adapter = _context?.DeclarativeAdapter?.Resolve(interfaceType);
        if (adapter is null
            || adapter.Metadata().Execution != "declarative"
            || adapter is not IAgentProtocolAdapter agentAdapter)
        {
            return null;
        }
        return adapter is not IAgentCapability capability || capability.AgentAvailable() ? agentAdapter : null;
    }

    /// <summary>对应 Go: <c>agentStepOutputLimit</c>：策略上限与模型声明上限取较小者。</summary>
    private static int AgentStepOutputLimit(TextTaskInput input)
    {
        int limit = 0;
        foreach (int candidate in new[] { input.TextOptions.MaxOutputTokens, input.MaxOutputTokens })
        {
            if (candidate <= 0)
            {
                continue;
            }
            if (limit == 0 || candidate < limit)
            {
                limit = candidate;
            }
        }
        return limit;
    }

    /// <summary>按协议写入输出上限字段名：Claude 与 Chat 用 max_tokens，Responses 用 max_output_tokens。</summary>
    private static void ApplyAgentOutputLimit(Dictionary<string, object?> body, int limit, string protocol)
    {
        if (limit <= 0)
        {
            return;
        }
        ProviderTextOrchestration.ApplyTextOutputLimit(
            body, limit, protocol == ProviderTextOrchestration.ResponsesProtocol ? "max_output_tokens" : "max_tokens");
    }
}

/// <summary>
/// Provider 出站请求的运行时上下文（渠道并发、熔断、响应上限、计费审计）。
/// 对应 Go 的 <c>providerAnalyticsContext</c>。
/// </summary>
/// <remarks>
/// 由 <c>Application</c> 层的适配器实现（那里能同时引用 <c>Platform</c> 与 <c>Providers</c>）。
/// </remarks>
public interface IProviderRequestContext
{
    /// <summary>上游响应字节上限（取自运行时策略的生成文件大小限制）。</summary>
    long MaxResponseBytes { get; }

    /// <summary>
    /// 渠道熔断是否打开。对应 Go: <c>Coordinator.CircuitOpen</c>。
    /// </summary>
    /// <remarks>默认实现返回未打开，便于无熔断场景（如本地测试）直接构造。</remarks>
    Task<bool> IsCircuitOpenAsync(string channelId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    /// <summary>
    /// 获取渠道并发槽。返回的委托用于释放；<c>null</c> 表示不限流。
    /// 对应 Go: <c>Service.AcquireChannelSlot</c>。
    /// </summary>
    Task<Func<ValueTask>?> AcquireChannelSlotAsync(
        string channelId, string fallbackScope, CancellationToken cancellationToken) =>
        Task.FromResult<Func<ValueTask>?>(null);

    /// <summary>
    /// 记录一次渠道调用结果（驱动熔断状态机）。
    /// 对应 Go: <c>Service.RecordChannelResult</c>。
    /// </summary>
    Task RecordChannelResultAsync(string channelId, bool failed, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// 当前生效的声明式协议适配器注册表；<c>null</c> 表示未注入（等价 Go 的裸 ctx，
    /// 图片走手写协议）。生产实现返回官方插件包注册表，测试保持 <c>null</c> 或注入自定义表。
    /// </summary>
    ProtocolAdapterRegistry? DeclarativeAdapter => null;
}
