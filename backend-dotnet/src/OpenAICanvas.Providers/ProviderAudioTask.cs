#nullable enable
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Outbound;
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Providers;

/// <summary>
/// 音频生成任务的渠道实现（遗留手写协议）。
/// 对应 Go: <c>internal/app/provider_audio.go</c> 的 <c>runAudioTask</c>。
/// </summary>
/// <remarks>
/// <b>已有官方声明式插件的接口类型不在此处理</b>：调用方应先查上下文注入的适配器
/// 注册表（与图片一致），未命中才进入这里的 OpenAI 风格 / 异步音频手写协议。
/// </remarks>
public sealed class ProviderAudioTask
{
    private readonly Func<HttpClient>? _clientFactory;
    private readonly IProviderRequestContext? _context;

    public ProviderAudioTask(
        IProviderRequestContext? context = null,
        Func<HttpClient>? clientFactory = null)
    {
        _context = context;
        _clientFactory = clientFactory;
    }

    /// <summary>音频任务入口。对应 Go: <c>runAudioTask</c>。</summary>
    public async Task<Dictionary<string, object?>> RunAsync(
        TextTaskInput input,
        CancellationToken cancellationToken = default)
    {
        // 与 Go 一致：声明式分支只查上下文注入的注册表（无视频的 official-fallback 报错路径）。
        IProtocolAdapter? declarative = _context?.DeclarativeAdapter?.Resolve(input.Config.InterfaceType ?? "");
        if (declarative is not null)
        {
            return await new ProviderProtocolTask(_context, _clientFactory)
                .RunAsync(input, declarative, "", ProviderProtocolTask.DeclarativePollPolicy("audio"), cancellationToken)
                .ConfigureAwait(false);
        }

        string format = ProviderMediaCodec.DefaultString(input.Config.AudioFormat, "mp3");
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["model"] = input.Config.Model,
            ["input"] = input.Prompt,
            ["voice"] = ProviderMediaCodec.DefaultString(input.Config.AudioVoice, "alloy"),
            ["response_format"] = format,
            ["speed"] = 1.0,
        };
        if ((input.Config.AudioSpeed ?? "").Length > 0)
        {
            body["speed"] = ProviderVideoOptions.ParseFloat(input.Config.AudioSpeed, 1);
        }
        if ((input.Config.AudioInstructions ?? "").Length > 0)
        {
            body["instructions"] = input.Config.AudioInstructions;
        }
        if (input.Config.InterfaceType == ChannelInterfaceType.ChannelInterfaceAsyncAudio)
        {
            return await RunAsyncAudioAsync(input, body, format, cancellationToken).ConfigureAwait(false);
        }

        (byte[] data, string mimeType) = await PostBinaryAsync(
            input.Config, "/audio/speech", body, cancellationToken).ConfigureAwait(false);
        (mimeType, Exception? validationError) = ValidateGeneratedAudio(mimeType, data, format);
        if (validationError is not null)
        {
            throw validationError;
        }
        return AudioResult(mimeType, data, format);
    }

    // ------------------------------------------------------------ 异步音频

    /// <summary>对应 Go: <c>runAsyncAudioTask</c>。</summary>
    private async Task<Dictionary<string, object?>> RunAsyncAudioAsync(
        TextTaskInput input,
        Dictionary<string, object?> body,
        string format,
        CancellationToken cancellationToken)
    {
        string id = "";
        Dictionary<string, object?> state;
        // 对应 Go 的 providerPollingDeadline 无 ctx deadline 分支：固定 1 小时预算。
        DateTime deadline = DateTime.UtcNow + VideoPollPolicy.PollTimeout;

        Dictionary<string, object?> created = await PostJsonAsync(
            input.Config, "/audio/tasks", body, cancellationToken).ConfigureAwait(false);
        state = AsyncAudioPayload(created);
        (string extracted, Exception? idError) = ProviderVideoTask.ExtractTaskId(state, "id", "task_id", "request_id");
        if (idError is not null)
        {
            throw new InvalidOperationException($"异步音频接口任务 ID 无效：{idError.Message}");
        }
        id = extracted;
        if (id.Length == 0)
        {
            throw new InvalidOperationException("异步音频接口没有返回任务 ID");
        }
        if (AsyncAudioSucceeded(state))
        {
            return await AsyncAudioResultAsync(input.Config, id, state, format, cancellationToken).ConfigureAwait(false);
        }

        while (DateTime.UtcNow < deadline)
        {
            state = await GetJsonAsync(
                input.Config, "/audio/tasks/" + Uri.EscapeDataString(id), cancellationToken).ConfigureAwait(false);
            state = AsyncAudioPayload(state);
            if (AsyncAudioSucceeded(state))
            {
                return await AsyncAudioResultAsync(input.Config, id, state, format, cancellationToken).ConfigureAwait(false);
            }
            string status = JsonFields.StringField(state, "status").ToLowerInvariant();
            if (status is "failed" or "cancelled" or "canceled" or "expired" or "error")
            {
                throw new InvalidOperationException($"异步音频生成失败（任务 {id}）：{AsyncAudioErrorMessage(state)}");
            }
            await Task.Delay(2500, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"异步音频生成超时（任务 {id}）");
    }

    /// <summary>
    /// 上游常见的 <c>{data:{...}}</c> 包装展开：外层键并入内层后只看内层。
    /// 对应 Go: <c>asyncAudioPayload</c>。
    /// </summary>
    public static Dictionary<string, object?> AsyncAudioPayload(Dictionary<string, object?> payload)
    {
        foreach (string key in new[] { "data", "result", "output" })
        {
            if (JsonFields.NestedObject(payload, key) is not { } nested)
            {
                continue;
            }
            foreach ((string parentKey, object? parentValue) in payload)
            {
                if (parentKey is "data" or "result" or "output")
                {
                    continue;
                }
                nested.TryAdd(parentKey, parentValue);
            }
            return nested;
        }
        return payload;
    }

    /// <summary>对应 Go: <c>asyncAudioSucceeded</c>。</summary>
    public static bool AsyncAudioSucceeded(Dictionary<string, object?> state)
    {
        string status = JsonFields.StringField(state, "status").ToLowerInvariant();
        bool done = state.TryGetValue("done", out object? value) && value is true;
        return done
            || status is "completed" or "succeeded" or "success" or "done"
            || (status.Length == 0 && AsyncAudioResultURL(state).Length > 0);
    }

    private async Task<Dictionary<string, object?>> AsyncAudioResultAsync(
        ProviderConfig config,
        string id,
        Dictionary<string, object?> state,
        string format,
        CancellationToken cancellationToken)
    {
        string resultURL = AsyncAudioResultURL(state);
        byte[] data;
        string mimeType;
        if (resultURL.StartsWith("data:", StringComparison.Ordinal))
        {
            (mimeType, data) = DecodeProviderDataURL(resultURL);
            long limit = _context?.MaxResponseBytes ?? ProviderTransport.DefaultMaxResponseBytes;
            if (data.LongLength > limit)
            {
                throw new InvalidOperationException($"异步音频结果超过 {ProviderTransport.FormatStorageLimit(limit)} 限制");
            }
        }
        else if (ProviderHelpers.IsPublicMediaURL(resultURL))
        {
            (data, mimeType) = await GetExternalBinaryAsync(config, resultURL, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            (data, mimeType) = await GetBinaryAsync(
                config, "/audio/tasks/" + Uri.EscapeDataString(id) + "/content", cancellationToken).ConfigureAwait(false);
        }
        (mimeType, Exception? validationError) = ValidateGeneratedAudio(mimeType, data, format);
        if (validationError is not null)
        {
            throw new InvalidOperationException($"异步音频结果无效（任务 {id}）：{validationError.Message}", validationError);
        }
        return AudioResult(mimeType, data, format);
    }

    /// <summary>对应 Go: <c>asyncAudioResultURL</c>（键序敏感，逐层下钻两层结构）。</summary>
    public static string AsyncAudioResultURL(Dictionary<string, object?> state)
    {
        foreach (string key in new[]
                 {
                     "audio_url", "audioUrl", "result_url", "resultUrl", "output_url", "outputUrl", "url", "data",
                 })
        {
            string value = JsonFields.StringField(state, key).Trim();
            if (value.StartsWith("data:", StringComparison.Ordinal) || ProviderHelpers.IsPublicMediaURL(value))
            {
                return value;
            }
        }
        foreach (string key in new[] { "audio", "data", "result", "output" })
        {
            if (JsonFields.NestedObject(state, key) is not { } nested)
            {
                continue;
            }
            string value = AsyncAudioResultURL(nested);
            if (value.Length > 0)
            {
                return value;
            }
        }
        return "";
    }

    /// <summary>对应 Go: <c>asyncAudioErrorMessage</c>。</summary>
    private static string AsyncAudioErrorMessage(Dictionary<string, object?> state)
    {
        (_, string message) = ProviderError.FailureDetails(state);
        return ProviderMediaCodec.DefaultString(
            message,
            ProviderHelpers.FirstNonEmpty(JsonFields.StringField(state, "message"), "上游返回失败状态"));
    }

    /// <summary>对应 Go: <c>decodeProviderDataURL</c>。</summary>
    public static (string MIMEType, byte[] Data) DecodeProviderDataURL(string value)
    {
        int separator = value.IndexOf(',');
        if (separator <= 0)
        {
            throw new InvalidOperationException("音频 data URL 格式无效");
        }
        string header = value[..separator];
        string encoded = value[(separator + 1)..];
        if (!header.StartsWith("data:", StringComparison.Ordinal)
            || !header.ToLowerInvariant().EndsWith(";base64", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("音频 data URL 格式无效");
        }
        string mimeType = header["data:".Length..^";base64".Length];
        try
        {
            return (mimeType, Convert.FromBase64String(encoded));
        }
        catch (FormatException error)
        {
            throw new InvalidOperationException("音频 data URL 格式无效", error);
        }
    }

    // ------------------------------------------------------------ 音频校验

    /// <summary>对应 Go: <c>validateGeneratedAudio</c>。</summary>
    public static (string MIMEType, Exception? Error) ValidateGeneratedAudio(
        string declared, byte[] data, string format)
    {
        if (data.Length == 0)
        {
            return ("", new InvalidOperationException("音频内容为空"));
        }
        string detected = OpenAICanvas.Outbound.ContentTypeSniffer.Sniff(data).Split(';')[0].Trim().ToLowerInvariant();
        if (detected.Contains("json", StringComparison.Ordinal)
            || detected.StartsWith("text/", StringComparison.Ordinal)
            || detected.StartsWith("image/", StringComparison.Ordinal)
            || detected.StartsWith("video/", StringComparison.Ordinal))
        {
            return ("", new InvalidOperationException($"上游返回了非音频内容：{detected}"));
        }
        string mimeType = declared.Split(';')[0].Trim().ToLowerInvariant();
        string resolved;
        if (mimeType.StartsWith("audio/", StringComparison.Ordinal))
        {
            resolved = mimeType;
        }
        else if (detected.StartsWith("audio/", StringComparison.Ordinal))
        {
            resolved = detected;
        }
        else if (AudioFormatMimeType(format) is { } fallback
                 && fallback.Length > 0
                 && (mimeType.Length == 0 || mimeType == "application/octet-stream"))
        {
            resolved = fallback;
        }
        else
        {
            resolved = "";
        }
        if (resolved.Length == 0)
        {
            return ("", new InvalidOperationException(
                $"上游响应类型不是音频：{ProviderMediaCodec.DefaultString(mimeType, detected)}"));
        }
        if (!AudioSignatureMatches(resolved, data))
        {
            return ("", new InvalidOperationException($"音频内容与格式不匹配：{resolved}"));
        }
        return (resolved, null);
    }

    /// <summary>对应 Go: <c>audioSignatureMatches</c>（mp3/wav/ogg/flac/aac 魔数；pcm 不校验）。</summary>
    public static bool AudioSignatureMatches(string mimeType, byte[] data)
    {
        if (mimeType.Contains("pcm", StringComparison.Ordinal) || mimeType == "audio/l16")
        {
            return data.Length > 0;
        }
        if (mimeType.Contains("mpeg", StringComparison.Ordinal) || mimeType.Contains("mp3", StringComparison.Ordinal))
        {
            return data.Length >= 2
                && (data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'3'
                    || (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0));
        }
        if (mimeType.Contains("wav", StringComparison.Ordinal) || mimeType.Contains("wave", StringComparison.Ordinal))
        {
            return data.Length >= 12
                && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
                && data[8] == (byte)'W' && data[9] == (byte)'A' && data[10] == (byte)'V' && data[11] == (byte)'E';
        }
        if (mimeType.Contains("opus", StringComparison.Ordinal) || mimeType.Contains("ogg", StringComparison.Ordinal))
        {
            return data.Length >= 4
                && data[0] == (byte)'O' && data[1] == (byte)'g' && data[2] == (byte)'g' && data[3] == (byte)'S';
        }
        if (mimeType.Contains("flac", StringComparison.Ordinal))
        {
            return data.Length >= 4
                && data[0] == (byte)'f' && data[1] == (byte)'L' && data[2] == (byte)'a' && data[3] == (byte)'C';
        }
        if (mimeType.Contains("aac", StringComparison.Ordinal))
        {
            return data.Length >= 2
                && (data[0] == (byte)'A' && data[1] == (byte)'D' && data[2] == (byte)'I' && data[3] == (byte)'F'
                    || (data[0] == 0xFF && (data[1] & 0xF0) == 0xF0));
        }
        return false;
    }

    /// <summary>对应 Go: <c>audioFormatMimeType</c>。</summary>
    public static string AudioFormatMimeType(string format) => (format ?? "").Trim().ToLowerInvariant() switch
    {
        "wav" => "audio/wav",
        "opus" => "audio/opus",
        "aac" => "audio/aac",
        "flac" => "audio/flac",
        "pcm" => "audio/pcm",
        "mp3" => "audio/mpeg",
        _ => "",
    };

    private static Dictionary<string, object?> AudioResult(string mimeType, byte[] data, string format) =>
        new(StringComparer.Ordinal)
        {
            ["mode"] = "audio",
            ["audio"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["dataUrl"] = ProviderHelpers.DataUrl(mimeType, data),
                ["mimeType"] = mimeType,
                ["format"] = format,
            },
        };

    // ------------------------------------------------------------ HTTP 工具

    private async Task<Dictionary<string, object?>> PostJsonAsync(
        ProviderConfig config, string path, object body, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            ProviderTransport.ChannelApiUrl(config.BaseURL, path))
        {
            Content = new StringContent(
                ProtocolJson.Serialize(body), Encoding.UTF8, "application/json"),
        };
        ProviderTransport.ApplyProviderAuth(request, config);
        ProviderTransport.ApplyDefaultHeaders(request);
        OutboundHttpClient.ApplyHeaders(request, config.Headers);
        return await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(byte[] Data, string MIMEType)> PostBinaryAsync(
        ProviderConfig config, string path, object body, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            ProviderTransport.ChannelApiUrl(config.BaseURL, path))
        {
            Content = new StringContent(
                ProtocolJson.Serialize(body), Encoding.UTF8, "application/json"),
        };
        ProviderTransport.ApplyProviderAuth(request, config);
        ProviderTransport.ApplyDefaultHeaders(request);
        OutboundHttpClient.ApplyHeaders(request, config.Headers);
        ProviderTransport.OutboundResult result = await ProviderTransport.SendAsync(
            request,
            _context?.MaxResponseBytes ?? ProviderTransport.DefaultMaxResponseBytes,
            null,
            cancellationToken,
            _clientFactory).ConfigureAwait(false);
        return (result.Data, result.MIMEType);
    }

    private async Task<Dictionary<string, object?>> GetJsonAsync(
        ProviderConfig config, string path, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(
            HttpMethod.Get, ProviderTransport.ChannelApiUrl(config.BaseURL, path));
        ProviderTransport.ApplyProviderAuth(request, config);
        ProviderTransport.ApplyDefaultHeaders(request);
        OutboundHttpClient.ApplyHeaders(request, config.Headers);
        return await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(byte[] Data, string MIMEType)> GetBinaryAsync(
        ProviderConfig config, string path, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(
            HttpMethod.Get, ProviderTransport.ChannelApiUrl(config.BaseURL, path));
        ProviderTransport.ApplyProviderAuth(request, config);
        ProviderTransport.ApplyDefaultHeaders(request);
        OutboundHttpClient.ApplyHeaders(request, config.Headers);
        ProviderTransport.OutboundResult result = await ProviderTransport.SendAsync(
            request,
            _context?.MaxResponseBytes ?? ProviderTransport.DefaultMaxResponseBytes,
            null,
            cancellationToken,
            _clientFactory).ConfigureAwait(false);
        return (result.Data, result.MIMEType);
    }

    /// <summary>下载外部（可能是第三方 CDN）的结果，跨源不带渠道鉴权。</summary>
    private async Task<(byte[] Data, string MIMEType)> GetExternalBinaryAsync(
        ProviderConfig config, string rawUrl, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(HttpMethod.Get, rawUrl);
        if (ProviderHelpers.IsSameProviderOrigin(config.BaseURL, rawUrl))
        {
            ProviderTransport.ApplyProviderAuth(request, config);
            OutboundHttpClient.ApplyHeaders(request, config.Headers);
        }
        ProviderTransport.ApplyDefaultHeaders(request);
        ProviderTransport.OutboundResult result = await ProviderTransport.SendAsync(
            request,
            _context?.MaxResponseBytes ?? ProviderTransport.DefaultMaxResponseBytes,
            null,
            cancellationToken,
            _clientFactory).ConfigureAwait(false);
        return (result.Data, result.MIMEType);
    }

    private async Task<Dictionary<string, object?>> SendJsonAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            return await ProviderTransport.SendJsonAsync(
                request,
                _context?.MaxResponseBytes ?? ProviderTransport.DefaultMaxResponseBytes,
                cancellationToken,
                _clientFactory).ConfigureAwait(false);
        }
    }
}
