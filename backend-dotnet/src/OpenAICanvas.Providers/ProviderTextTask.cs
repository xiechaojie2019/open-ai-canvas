#nullable enable
using System.Text;
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
