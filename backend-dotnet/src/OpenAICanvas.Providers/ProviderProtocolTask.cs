#nullable enable
using System.Text.Json;
using OpenAICanvas.Outbound;
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Providers;

/// <summary>
/// 声明式插件协议任务的编排：create、poll、download 三阶段。
/// 对应 Go: <c>internal/app/provider_protocol.go</c> 的
/// <c>runProtocolAdapterTaskWithPolicy</c> / <c>queryProtocolAdapterVideoTask</c> /
/// <c>finishProtocolAdapterResult</c> / <c>finishProtocolResult</c> /
/// <c>protocolMediaBytes</c> / <c>retryableProtocolMediaDownload</c> /
/// <c>extractProviderTaskID</c> / <c>declarativeProtocolPollPolicy</c>。
/// </summary>
/// <remarks>
/// create、poll、download 是同一个外部任务的三个阶段：任一阶段失败都向上返回真实错误，
/// 不把已提交但结果未知伪装成成功，也不把下载失败降级成空结果。
/// </remarks>
public sealed class ProviderProtocolTask(
    IProviderRequestContext? context = null,
    Func<HttpClient>? clientFactory = null)
{
    private readonly IProviderRequestContext? _context = context;
    private readonly Func<HttpClient>? _clientFactory = clientFactory;

    /// <summary>
    /// 完整链路：create（或复用 taskID）然后 poll 循环直到终态并下载结果。
    /// 对应 Go: <c>runProtocolAdapterTaskWithPolicy</c>。
    /// </summary>
    public async Task<Dictionary<string, object?>> RunAsync(
        TextTaskInput input,
        IProtocolAdapter adapter,
        string resumedProviderRequestId = "",
        VideoPollPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= DeclarativePollPolicy(input.Mode);
        GenerationRequest request = ToProtocolRequest(ProviderProtocolPayload.FromInput(input));
        string taskID = resumedProviderRequestId.Trim();

        if (taskID.Length == 0)
        {
            // 幂等键只存在于宿主请求元数据中，声明式插件可以把它映射到 Header，
            // 但不能把宿主控制字段泄漏到供应商 JSON body。恢复已有 taskID 时不进入 create 分支。
            string key = ProviderHelpers.MetadataString(input.Metadata, "providerSubmissionKey");
            if (key.Length == 0)
            {
                key = Guid.NewGuid().ToString();
            }
            request.Extra["idempotencyKey"] = key;

            RequestSpec spec = adapter.BuildCreate(new RequestContext
            {
                BaseURL = input.Config.BaseURL,
                Request = request,
            });
            byte[] body = await ExecuteCreateOrPollAsync(
                input.Config, spec, cancellationToken).ConfigureAwait(false);
            CreateResult created = adapter.ParseCreate(body);
            taskID = created.TaskID;
            if (taskID.Length == 0)
            {
                taskID = ExtractProviderTaskID(body);
            }
            if (created.Status == ProtocolStatus.Failed || created.Status == ProtocolStatus.Cancelled)
            {
                throw ProviderProtocolPayload.ResultError(created.Message, taskID);
            }
            if (created.Status == ProtocolStatus.Succeeded)
            {
                return await FinishAdapterResultAsync(
                    input, adapter, request, taskID, created.Result, policy, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (taskID.Length == 0)
            {
                throw new InvalidOperationException("声明式协议创建请求没有返回任务 ID");
            }
        }

        return await ProviderVideoPolling.RunPollLoopAsync(
            taskID,
            policy,
            async token =>
            {
                PollContext pollContext = new()
                {
                    BaseURL = input.Config.BaseURL,
                    Model = request.Model,
                    Request = request,
                    TaskID = taskID,
                };
                RequestSpec spec = adapter.BuildPoll(pollContext);
                byte[] body = await ExecuteCreateOrPollAsync(
                    input.Config, spec, token).ConfigureAwait(false);
                PollResult state = adapter.ParsePoll(pollContext, body);
                if (state.TaskID.Length > 0)
                {
                    taskID = state.TaskID;
                }
                if (state.Status == ProtocolStatus.Succeeded)
                {
                    Dictionary<string, object?> result = await FinishAdapterResultAsync(
                        input, adapter, request, taskID, state.Result, policy, token)
                        .ConfigureAwait(false);
                    return new VideoPollOutcome(true, result);
                }
                if (state.Status == ProtocolStatus.Failed || state.Status == ProtocolStatus.Cancelled)
                {
                    throw ProviderProtocolPayload.ResultError(state.Message, taskID);
                }
                return new VideoPollOutcome(false, null);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 只查询一次已有的声明式 Provider 任务。人工恢复走此路径，
    /// 检查本地失败任务时绝不会创建第二个计费任务。
    /// 对应 Go: <c>queryProtocolAdapterVideoTask</c>。
    /// </summary>
    public async Task<(Dictionary<string, object?>? Result, string Status)> QueryAsync(
        TextTaskInput input,
        IProtocolAdapter adapter,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        GenerationRequest request = ToProtocolRequest(ProviderProtocolPayload.FromInput(input));
        PollContext pollContext = new()
        {
            BaseURL = input.Config.BaseURL,
            Model = request.Model,
            Request = request,
            TaskID = taskId,
        };
        RequestSpec spec = adapter.BuildPoll(pollContext);
        byte[] body = await ExecuteCreateOrPollAsync(input.Config, spec, cancellationToken)
            .ConfigureAwait(false);
        PollResult state = adapter.ParsePoll(pollContext, body);
        string providerStatus = state.Status;
        switch (state.Status)
        {
            case ProtocolStatus.Succeeded:
                Dictionary<string, object?> result = await FinishAdapterResultAsync(
                    input, adapter, request, taskId, state.Result,
                    DeclarativePollPolicy("video"), cancellationToken).ConfigureAwait(false);
                return (result, providerStatus);
            case ProtocolStatus.Failed:
            case ProtocolStatus.Cancelled:
                throw ProviderProtocolPayload.ResultError(state.Message, taskId);
            case ProtocolStatus.Pending:
            case ProtocolStatus.Processing:
                return (null, providerStatus);
            default:
                throw new InvalidOperationException(
                    $"声明式协议任务 {taskId} 返回未知状态：{providerStatus}");
        }
    }

    // ------------------------------------------------------------ 结果下载

    /// <summary>
    /// 优先消费 create/poll 已返回的内联或 URL 结果，只有插件明确声明独立结果端点时才下载。
    /// 对应 Go: <c>finishProtocolAdapterResult</c>。
    /// </summary>
    private async Task<Dictionary<string, object?>> FinishAdapterResultAsync(
        TextTaskInput input,
        IProtocolAdapter adapter,
        GenerationRequest request,
        string taskID,
        Result? result,
        VideoPollPolicy policy,
        CancellationToken cancellationToken)
    {
        if (ProviderProtocolPayload.ResultHasOutput(input.Mode, MapResult(result)))
        {
            return await FinishResultAsync(
                input.Config, input.Mode, taskID, result, policy, cancellationToken)
                .ConfigureAwait(false);
        }
        if (adapter is not IResultAdapter resultAdapter
            || adapter is not IResultCapability capability
            || !capability.ResultAvailable())
        {
            return await FinishResultAsync(
                input.Config, input.Mode, taskID, result, policy, cancellationToken)
                .ConfigureAwait(false);
        }

        RequestSpec spec = resultAdapter.BuildResult(new PollContext
        {
            BaseURL = input.Config.BaseURL,
            Model = request.Model,
            Request = request,
            TaskID = taskID,
        });
        async Task<(byte[] Data, string MIMEType)> Download(CancellationToken token) =>
            await ProviderProtocolExecutor.ExecuteWithMimeTypeAsync(
                input.Config, spec, null, null, null, token, _clientFactory).ConfigureAwait(false);

        byte[] data;
        string mimeType;
        if (input.Mode == "video")
        {
            (data, mimeType) = await ProviderVideoPolling.RunDownloadAsync(
                taskID, policy, Download, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            (data, mimeType) = await Download(cancellationToken).ConfigureAwait(false);
        }
        if (data.Length == 0)
        {
            throw new InvalidOperationException("声明式协议结果下载返回空内容");
        }
        mimeType = mimeType.Split(';')[0].Trim();
        if (mimeType.Length == 0)
        {
            mimeType = "application/octet-stream";
        }
        MediaReference reference = new()
        {
            DataURL = ProviderHelpers.DataUrl(mimeType, data),
            MIMEType = mimeType,
        };
        Result downloaded = new();
        switch (input.Mode)
        {
            case "image":
                downloaded.Images.Add(reference);
                break;
            case "video":
                downloaded.Videos.Add(reference);
                break;
            case "audio":
                downloaded.Audios.Add(reference);
                break;
            default:
                throw new InvalidOperationException(
                    $"声明式协议结果下载不支持生成模式 {input.Mode}");
        }
        return await FinishResultAsync(
            input.Config, input.Mode, taskID, downloaded, policy, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 结果整形为任务载荷并下载媒体。对应 Go: <c>finishProtocolResult</c>。
    /// </summary>
    private async Task<Dictionary<string, object?>> FinishResultAsync(
        ProviderConfig config,
        string mode,
        string taskID,
        Result? result,
        VideoPollPolicy policy,
        CancellationToken cancellationToken)
    {
        if (result is null)
        {
            throw new InvalidOperationException("声明式协议已完成但没有返回结果");
        }
        if (mode == "text")
        {
            Dictionary<string, object?> output = new(StringComparer.Ordinal)
            {
                ["mode"] = "text",
                ["text"] = result.Text,
            };
            if (result.Reasoning.Trim().Length > 0)
            {
                output["reasoning"] = result.Reasoning;
            }
            return output;
        }

        List<MediaReference> references = mode switch
        {
            "image" => result.Images,
            "video" => result.Videos,
            "audio" => result.Audios,
            _ => throw new InvalidOperationException($"声明式协议不支持生成模式 {mode}"),
        };
        if (references.Count == 0)
        {
            throw new InvalidOperationException("声明式协议已完成但没有返回媒体地址");
        }

        List<Dictionary<string, object?>> items = [];
        foreach (MediaReference reference in references)
        {
            byte[] data;
            string mimeType;
            if (mode == "video")
            {
                (data, mimeType) = await ProviderVideoPolling.RunDownloadAsync(
                    taskID, policy,
                    token => ProtocolMediaBytesOnce(config, reference, token),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                (data, mimeType) = await ProtocolMediaBytes(config, reference, cancellationToken)
                    .ConfigureAwait(false);
            }
            items.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["dataUrl"] = ProviderHelpers.DataUrl(mimeType, data),
                ["mimeType"] = mimeType,
            });
        }
        return mode switch
        {
            "image" => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mode"] = "image",
                ["images"] = items,
            },
            "video" => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mode"] = "video",
                ["video"] = items[0],
            },
            _ => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mode"] = "audio",
                ["audio"] = items[0],
            },
        };
    }

    /// <summary>媒体下载（最多 3 次，递增等待）。对应 Go: <c>protocolMediaBytes</c>。</summary>
    private async Task<(byte[] Data, string MIMEType)> ProtocolMediaBytes(
        ProviderConfig config, MediaReference reference, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await ProtocolMediaBytesOnce(config, reference, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                lastError = error;
                if (attempt == 2 || !RetryableProtocolMediaDownload(error))
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException(
            "声明式协议媒体结果下载失败：" + (lastError?.Message ?? "未知错误"), lastError);
    }

    /// <summary>单次媒体获取：dataURL 解码或外部二进制下载。对应 Go: <c>protocolMediaBytesOnce</c>。</summary>
    private async Task<(byte[] Data, string MIMEType)> ProtocolMediaBytesOnce(
        ProviderConfig config, MediaReference reference, CancellationToken cancellationToken)
    {
        if (reference.DataURL.Trim().Length > 0)
        {
            (string mime, byte[] decoded) = ProtocolRequestBuilder.DecodeDataURL(reference.DataURL);
            return (decoded, mime);
        }
        string value = reference.URL.Trim();
        if (value.Length == 0)
        {
            throw new InvalidOperationException("声明式协议媒体结果地址为空");
        }
        HttpRequestMessage request = new(HttpMethod.Get, value);
        // 跨源下载不带渠道鉴权，避免把密钥泄露给第三方 CDN。
        if (ProviderHelpers.IsSameProviderOrigin(config.BaseURL, value))
        {
            ProviderTransport.ApplyProviderAuth(request, config);
            OutboundHttpClient.ApplyHeaders(request, config.Headers);
        }
        ProviderTransport.ApplyDefaultHeaders(request);
        using (request)
        {
            ProviderTransport.OutboundResult outbound = await ProviderTransport.SendAsync(
                request,
                _context?.MaxResponseBytes ?? ProviderTransport.DefaultMaxResponseBytes,
                null,
                cancellationToken,
                _clientFactory).ConfigureAwait(false);
            return (outbound.Data, ProviderMediaCodec.NormalizedMediaMimeType(outbound.MIMEType, outbound.Data));
        }
    }

    /// <summary>判定媒体下载错误是否可重试。对应 Go: <c>retryableProtocolMediaDownload</c>。</summary>
    internal static bool RetryableProtocolMediaDownload(Exception error)
    {
        if (error is OperationCanceledException or TimeoutException)
        {
            return false;
        }
        if (error is ProviderHttpException { StatusCode: 408 or 429 or >= 500 })
        {
            return true;
        }
        string message = error.Message.ToLowerInvariant();
        foreach (string marker in s_retryMarkers)
        {
            if (message.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly string[] s_retryMarkers =
        ["tls handshake timeout", "connection reset", "unexpected eof", "broken pipe"];

    /// <summary>
    /// 从 create 响应提取任务 ID。对应 Go: <c>extractProviderTaskID</c>。
    /// JSON 解析失败返回空串不报错（上游可能返回空体）。
    /// </summary>
    internal static string ExtractProviderTaskID(byte[] body)
    {
        Dictionary<string, object?>? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(body);
        }
        catch (JsonException)
        {
            return "";
        }
        if (payload is null)
        {
            return "";
        }
        string id = FirstJsonString(payload, "id", "task_id", "taskId", "request_id", "name");
        if (id.Length > 0)
        {
            return id;
        }
        if (payload.GetValueOrDefault("data") is Dictionary<string, object?> data)
        {
            string nested = FirstJsonString(data, "id", "task_id", "taskId", "request_id");
            if (nested.Length > 0)
            {
                return nested;
            }
        }
        return "";
    }

    private static string FirstJsonString(Dictionary<string, object?> payload, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (payload.GetValueOrDefault(key) is string text && text.Trim().Length > 0)
            {
                return text.Trim();
            }
        }
        return "";
    }

    /// <summary>
    /// Providers 层请求投影到 Protocol 层契约。两侧字段同构，仅类型系统分隔。
    /// </summary>
    private static GenerationRequest ToProtocolRequest(ProtocolGenerationRequest source) => new()
    {
        Capability = source.Capability,
        Model = source.Model,
        Prompt = source.Prompt,
        Instructions = source.Instructions,
        Messages = [.. source.Messages.Select(m => new Message { Role = m.Role, Content = m.Content })],
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

    /// <summary>Protocol 层结果映射回 Providers 层视图（用于输出判定）。</summary>
    private static ProtocolResult? MapResult(Result? result) => result is null
        ? null
        : new ProtocolResult
        {
            Text = result.Text,
            Reasoning = result.Reasoning,
            Images = result.Images,
            Videos = result.Videos,
            Audios = result.Audios,
            Usage = result.Usage,
        };

    private async Task<byte[]> ExecuteCreateOrPollAsync(
        ProviderConfig config, RequestSpec spec, CancellationToken cancellationToken) =>
        await ProviderProtocolExecutor.ExecuteAsync(
            config, spec, null, cancellationToken, _clientFactory).ConfigureAwait(false);

    /// <summary>
    /// 声明式协议的轮询策略：视频走默认视频策略，其他模式用短间隔快速轮询。
    /// 对应 Go: <c>declarativeProtocolPollPolicy</c>。
    /// </summary>
    public static VideoPollPolicy DeclarativePollPolicy(string mode)
    {
        if (mode == "video")
        {
            return new VideoPollPolicy();
        }
        return new VideoPollPolicy
        {
            InitialDelay = TimeSpan.Zero,
            Interval = TimeSpan.FromMilliseconds(2500),
            MaxNotFoundMisses = 1,
            MaxDownloadTries = 3,
            RetryTransient = false,
        };
    }
}
