#nullable enable
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Outbound;
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Providers;

/// <summary>
/// RunningHub 工作流协议任务的完整链路：拉取工作流参数、推断字段映射、
/// 上传参考素材、组装 nodeInfoList、提交任务并轮询下载产物。
/// 对应 Go: <c>internal/app/workflow_provider.go</c> 的
/// <c>runRunningHubWorkflow</c> / <c>pollRunningHubWorkflow</c> /
/// <c>downloadWorkflowOutputs</c> / <c>uploadRunningHubMedia</c>。
/// </summary>
/// <remarks>
/// 与 4.10 的声明式任务一致：create、poll、download 任一阶段失败都向上返回真实错误；
/// 轮询状态落库由 Worker 的租约/终态管道承担，这里只保留结果与 taskId。
/// </remarks>
public sealed class ProviderWorkflowTask(
    IProviderRequestContext? context = null,
    Func<HttpClient>? clientFactory = null)
{
    /// <summary>对应 Go: <c>pollRunningHubWorkflowLegacy</c> 的 2.5 秒固定间隔。</summary>
    private static readonly TimeSpan LegacyPollInterval = TimeSpan.FromMilliseconds(2500);

    private readonly IProviderRequestContext? _context = context;
    private readonly Func<HttpClient>? _clientFactory = clientFactory;

    /// <summary>RunningHub 工作流任务入口。对应 Go: <c>runWorkflowProviderTask</c>。</summary>
    /// <remarks>
    /// <paramref name="pollPolicy"/> 仅供测试注入短间隔策略；生产调用方（Worker）
    /// 保持 null，走默认的 30 秒间隔 / 1 小时超时预算。
    /// </remarks>
    public async Task<Dictionary<string, object?>> RunAsync(
        TextTaskInput input,
        string resumedProviderRequestId = "",
        VideoPollPolicy? pollPolicy = null,
        CancellationToken cancellationToken = default)
    {
        string mode = (input.Mode ?? "").Trim();
        await ProviderWorkflowValues.ValidateWorkflowProviderConfig(mode, input.Config).ConfigureAwait(false);
        ProviderConfig config = input.Config;
        string root = ProviderWorkflowValues.RunningHubRootURL(config.BaseURL);
        string resumed = resumedProviderRequestId.Trim();
        if (resumed.Length > 0)
        {
            return await PollAsync(config, root, resumed, mode, pollPolicy ?? new VideoPollPolicy(), cancellationToken)
                .ConfigureAwait(false);
        }

        string workflowID = config.WorkflowID.Trim();
        string webappID = config.WebappID.Trim();
        if (workflowID.Length == 0)
        {
            workflowID = config.Model.Trim();
        }
        Dictionary<string, object?>? mappingWorkflow = Objectify(config.WorkflowJSON);
        if (webappID.Length == 0 && (mappingWorkflow is null || mappingWorkflow.Count == 0) && workflowID.Length > 0)
        {
            // 获取当前 API 工作流既用于旧配置的字段推断，也用于删除缺失的可选媒体默认值。
            // 某些兼容网关没有该接口，失败时仍允许仅依赖用户已经保存的字段映射提交。
            try
            {
                mappingWorkflow = Objectify(
                    await FetchWorkflowJSONAsync(config, root, workflowID, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 与 Go 一致：fetch 错误不阻断提交。
            }
        }

        List<WorkflowField> workflowFields = config.WorkflowFields ?? [];
        if (workflowFields.Count == 0 && mappingWorkflow is { Count: > 0 })
        {
            workflowFields = WorkflowProviderManagement.FieldsFromManagement(mappingWorkflow, mode);
        }
        workflowFields = ProviderWorkflowValues.WorkflowFieldsForMode(workflowFields, mode);
        ValidateMediaInputs(workflowFields, input);

        Dictionary<string, string> files = new(StringComparer.Ordinal);
        foreach (ProviderMedia media in input.ReferenceImages
                     .Concat(input.ReferenceVideos)
                     .Concat(input.ReferenceAudios))
        {
            files[media.ID] = await UploadMediaAsync(config, root, media, cancellationToken).ConfigureAwait(false);
        }
        if (input.Mask is { } mask)
        {
            files[mask.ID] = await UploadMediaAsync(config, root, mask, cancellationToken).ConfigureAwait(false);
        }

        List<Dictionary<string, object?>> nodeInfo =
            BuildNodeInfo(workflowFields, files, input, mappingWorkflow);
        // 旧条目可能保存了全部默认字段，却没有把文本节点绑定到任务 Prompt。只在没有任何显式
        // Prompt 映射时回退，并替换同节点同字段的默认值，避免 nodeInfoList 出现互相冲突的重复项。
        if (!WorkflowFieldsBindPrompt(workflowFields))
        {
            nodeInfo = WorkflowProviderManagement.UpsertNodeInfo(
                nodeInfo, WorkflowProviderManagement.PromptFallback(mappingWorkflow, input.Prompt));
        }

        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["apiKey"] = ProviderWorkflowValues.RunningHubApiKey(config),
        };
        string endpoint = root + "/task/openapi/create";
        if (webappID.Length > 0)
        {
            body["webappId"] = webappID;
            endpoint = root + "/task/openapi/ai-app/run";
        }
        else
        {
            body["workflowId"] = workflowID;
        }
        if (nodeInfo.Count > 0)
        {
            body["nodeInfoList"] = nodeInfo;
        }

        Dictionary<string, object?> submitted;
        try
        {
            submitted = await PostJsonAsync(config, endpoint, body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw Wrap(error, "RunningHub 工作流提交失败");
        }
        (int code, bool validCode) = ProviderWorkflowValues.RunningHubPayloadCode(submitted);
        if (!validCode)
        {
            // 部分 RunningHub 兼容网关省略 code，但已经返回 taskId，按成功提交处理。
            validCode = ProviderWorkflowValues.RunningHubTaskID(submitted).Length > 0;
            code = 0;
        }
        if (!validCode || code != 0)
        {
            throw new InvalidOperationException(
                "RunningHub 工作流提交失败：" + ProviderWorkflowValues.RunningHubWorkflowFailureMessage(submitted));
        }
        string taskID = ProviderWorkflowValues.RunningHubTaskID(submitted);
        if (taskID.Length == 0)
        {
            throw new InvalidOperationException("RunningHub 未返回 taskId");
        }
        // 对应 Go 的 recordWorkflowProviderRequest：供应商请求状态由 Worker 终态管道统一写入。
        return await PollAsync(config, root, taskID, mode, pollPolicy ?? new VideoPollPolicy(), cancellationToken)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 轮询与下载

    /// <summary>对应 Go: <c>pollRunningHubWorkflow</c>（video 走策略循环，其余 2.5s 固定间隔）。</summary>
    private async Task<Dictionary<string, object?>> PollAsync(
        ProviderConfig config,
        string root,
        string taskID,
        string mode,
        VideoPollPolicy policy,
        CancellationToken cancellationToken)
    {
        if (mode == "video")
        {
            return await ProviderVideoPolling.RunPollLoopAsync(
                taskID,
                policy,
                token => PollStepAsync(config, root, taskID, taskID, policy, token),
                cancellationToken).ConfigureAwait(false);
        }
        return await PollLegacyAsync(config, root, taskID, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>单次查询并推进状态机。对应 Go 轮询循环体。</summary>
    private async Task<VideoPollOutcome> PollStepAsync(
        ProviderConfig config,
        string root,
        string taskID,
        string downloadTaskID,
        VideoPollPolicy? downloadPolicy,
        CancellationToken cancellationToken)
    {
        Dictionary<string, object?> response =
            await QueryOutputsAsync(config, root, taskID, cancellationToken).ConfigureAwait(false);
        (int code, bool validCode) = ProviderWorkflowValues.RunningHubPayloadCode(response);
        object? data = response.TryGetValue("data", out object? dataValue) ? dataValue : null;
        if (!validCode)
        {
            // 成功响应有时只有 outputs/results，没有显式状态码。
            validCode = ProviderWorkflowValues.RunningHubOutputURLs(data).Count > 0;
            code = 0;
        }
        if (!validCode)
        {
            throw new InvalidOperationException("RunningHub 查询响应缺少可识别状态");
        }
        if (code == 0)
        {
            List<string> urls = ProviderWorkflowValues.RunningHubOutputURLs(data);
            if (urls.Count == 0)
            {
                throw new InvalidOperationException("RunningHub 任务成功但没有返回产物");
            }
            for (int index = 0; index < urls.Count; index++)
            {
                urls[index] = ProviderWorkflowValues.ResolveRunningHubOutputURL(root, urls[index]);
            }
            Dictionary<string, object?> result = await DownloadOutputsAsync(
                urls, downloadTaskID, downloadPolicy, cancellationToken).ConfigureAwait(false);
            return new VideoPollOutcome(true, result);
        }
        if (code is 805 or 806)
        {
            throw new InvalidOperationException(
                "RunningHub 任务失败：" + ProviderWorkflowValues.RunningHubFailureMessage(response));
        }
        // RunningHub 会扩展暂态码；只对明确失败码结束任务，其余状态继续轮询到终态或超时。
        return new VideoPollOutcome(false, null);
    }

    /// <summary>对应 Go: <c>pollRunningHubWorkflowLegacy</c>。</summary>
    private async Task<Dictionary<string, object?>> PollLegacyAsync(
        ProviderConfig config,
        string root,
        string taskID,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = ProviderVideoPolling.PollingDeadline(
            cancellationToken, VideoPollPolicy.PollTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            VideoPollOutcome outcome = await PollStepAsync(
                config, root, taskID, taskID, null, cancellationToken).ConfigureAwait(false);
            if (outcome.Done)
            {
                return outcome.Result
                    ?? throw new InvalidOperationException("RunningHub 任务成功但没有返回产物");
            }
            await System.Threading.Tasks.Task.Delay(LegacyPollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"RunningHub 任务超时（{taskID}）");
    }

    /// <summary>对应 Go: <c>runningHubJSON(... "/task/openapi/outputs" ...)</c>。</summary>
    private async Task<Dictionary<string, object?>> QueryOutputsAsync(
        ProviderConfig config,
        string root,
        string taskID,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PostJsonAsync(config, root + "/task/openapi/outputs", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["apiKey"] = ProviderWorkflowValues.RunningHubApiKey(config),
                ["taskId"] = taskID,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw Wrap(error, "RunningHub 查询任务失败");
        }
    }

    /// <summary>对应 Go: <c>downloadWorkflowOutputsWithPolicy</c>。</summary>
    private async Task<Dictionary<string, object?>> DownloadOutputsAsync(
        List<string> urls,
        string taskID,
        VideoPollPolicy? policy,
        CancellationToken cancellationToken)
    {
        List<Dictionary<string, object?>> images = [];
        Dictionary<string, object?>? video = null;
        Dictionary<string, object?>? audio = null;
        foreach (string rawURL in urls)
        {
            if (rawURL.StartsWith("data:", StringComparison.Ordinal))
            {
                (string dataMIMEType, byte[] data) = DecodeDataURL(rawURL);
                AppendOutput(dataMIMEType, data, images, ref video, ref audio);
                continue;
            }
            if (!IsPublicMediaURL(rawURL))
            {
                continue;
            }
            byte[] payload;
            string mimeType;
            try
            {
                if (policy is null)
                {
                    (payload, mimeType) = await GetExternalBinaryAsync(rawURL, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    (payload, mimeType) = await ProviderVideoPolling.RunDownloadAsync(
                        taskID,
                        policy,
                        _ => GetExternalBinaryAsync(rawURL, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw Wrap(error, "下载 RunningHub 产物失败");
            }
            mimeType = ProviderWorkflowValues.RunningHubOutputMimeType(rawURL, mimeType);
            AppendOutput(mimeType, payload, images, ref video, ref audio);
        }
        Dictionary<string, object?> result = new(StringComparer.Ordinal) { ["mode"] = "image" };
        if (images.Count > 0)
        {
            result["images"] = images;
        }
        if (video is not null)
        {
            result["mode"] = "video";
            result["video"] = video;
        }
        if (audio is not null)
        {
            result["mode"] = "audio";
            result["audio"] = audio;
        }
        if (images.Count == 0 && video is null && audio is null)
        {
            throw new InvalidOperationException("RunningHub 返回的产物类型不受支持");
        }
        return result;
    }

    private static void AppendOutput(
        string mimeType,
        byte[] data,
        List<Dictionary<string, object?>> images,
        ref Dictionary<string, object?>? video,
        ref Dictionary<string, object?>? audio)
    {
        Dictionary<string, object?>? item = ProviderWorkflowValues.WorkflowOutputValue(mimeType, data);
        if (item is null)
        {
            return;
        }
        if (mimeType.StartsWith("image/", StringComparison.Ordinal))
        {
            images.Add(item);
        }
        else if (mimeType.StartsWith("video/", StringComparison.Ordinal))
        {
            video = item;
        }
        else if (mimeType.StartsWith("audio/", StringComparison.Ordinal))
        {
            audio = item;
        }
    }

    /// <summary>对应 Go: <c>decodeProviderDataURL</c>。</summary>
    internal static (string MIMEType, byte[] Data) DecodeDataURL(string value)
    {
        int cut = value.IndexOf(',');
        string header = cut >= 0 ? value[..cut] : "";
        if (cut < 0
            || !header.StartsWith("data:", StringComparison.Ordinal)
            || !header.ToLowerInvariant().EndsWith(";base64", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("音频 data URL 格式无效");
        }
        string mimeType = header["data:".Length..^";base64".Length];
        try
        {
            return (mimeType, Convert.FromBase64String(value[(cut + 1)..]));
        }
        catch (FormatException error)
        {
            throw new InvalidOperationException(error.Message, error);
        }
    }

    internal static bool IsPublicMediaURL(string value)
    {
        string lower = value.ToLowerInvariant();
        return lower.StartsWith("http://", StringComparison.Ordinal)
            || lower.StartsWith("https://", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 上传与提交

    /// <summary>对应 Go: <c>fetchRunningHubWorkflowJSON</c>。</summary>
    private async Task<Dictionary<string, object?>> FetchWorkflowJSONAsync(
        ProviderConfig config,
        string root,
        string workflowID,
        CancellationToken cancellationToken)
    {
        Dictionary<string, object?> response = await PostJsonAsync(
            config,
            root + "/api/openapi/getJsonApiFormat",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["apiKey"] = ProviderWorkflowValues.RunningHubApiKey(config),
                ["workflowId"] = workflowID,
            },
            cancellationToken).ConfigureAwait(false);
        (int code, bool valid) = ProviderWorkflowValues.RunningHubPayloadCode(response);
        if (valid && code != 0)
        {
            throw new InvalidOperationException(ProviderWorkflowValues.RunningHubFailureMessage(response));
        }
        Dictionary<string, object?>? data = JsonFields.NestedObject(response, "data");
        if (data is null)
        {
            throw new InvalidOperationException("RunningHub 工作流参数响应缺少 data");
        }
        if (!data.TryGetValue("prompt", out object? raw) || raw is null)
        {
            throw new InvalidOperationException("RunningHub 工作流参数响应缺少 prompt");
        }
        if (raw is string text)
        {
            return JsonFields.ParseObject(text)
                ?? throw new InvalidOperationException("RunningHub 工作流参数格式无效");
        }
        if (Objectify(AsMap(raw)) is Dictionary<string, object?> parsed)
        {
            return parsed;
        }
        throw new InvalidOperationException("RunningHub 工作流参数格式无效");
    }

    /// <summary>对应 Go: <c>uploadRunningHubMedia</c>。</summary>
    private async Task<string> UploadMediaAsync(
        ProviderConfig config,
        string root,
        ProviderMedia media,
        CancellationToken cancellationToken)
    {
        byte[] raw;
        string mimeType;
        try
        {
            (raw, mimeType) = ProviderMediaCodec.Bytes(media);
        }
        catch (Exception) when (IsPublicMediaURL(PublicMediaURL(media) ?? ""))
        {
            // 任务里可能只携带了已公开的参考图/视频 URL；RunningHub 上传接口需要字节，
            // 这里在下载一次，不把外部 URL 直接交给供应商。
            (raw, mimeType) = await GetExternalBinaryAsync(
                PublicMediaURL(media) ?? "", cancellationToken).ConfigureAwait(false);
        }
        if (raw.Length == 0)
        {
            throw new InvalidOperationException("RunningHub 参考素材为空");
        }
        string apiKey = config.RunningHubUploadKey.Trim();
        if (apiKey.Length == 0)
        {
            throw new InvalidOperationException(
                "RunningHub 参考素材上传需要企业级 API Key，请在 RunningHub 设置中填写“素材上传 API Key（企业级）”");
        }
        string boundary = "canvas" + Guid.NewGuid().ToString("N");
        using MemoryStream buffer = new();
        ProviderMultipart.WriteField(buffer, boundary, "apiKey", apiKey);
        ProviderMultipart.WriteField(buffer, boundary, "fileType", "input");
        string filename = ProviderMediaCodec.MediaFilename(media, mimeType);
        ProviderMultipart.WritePart(buffer, boundary, "file", filename, mimeType, raw);
        ProviderMultipart.Close(buffer, boundary);

        using HttpRequestMessage request = new(HttpMethod.Post, root + "/task/openapi/upload")
        {
            Content = new ByteArrayContent(buffer.ToArray()),
        };
        request.Content.Headers.TryAddWithoutValidation(
            "Content-Type", "multipart/form-data; boundary=" + boundary);
        OutboundHttpClient.ApplyHeaders(request, config.Headers);
        ProviderTransport.OutboundResult result;
        try
        {
            result = await ProviderTransport.SendAsync(
                request,
                _context?.MaxResponseBytes ?? ProviderTransport.DefaultMaxResponseBytes,
                null,
                cancellationToken,
                _clientFactory).ConfigureAwait(false);
        }
        catch (ProviderHttpException error) when (UploadAuthFailureMessage(error) is { Length: > 0 } message)
        {
            throw new InvalidOperationException(message, error);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw Wrap(error, "RunningHub 参考素材上传失败");
        }
        Dictionary<string, object?>? rawResponse = JsonFields.ParseObject(Encoding.UTF8.GetString(result.Data));
        if (rawResponse is null)
        {
            throw new InvalidOperationException("RunningHub 上传响应不是有效 JSON");
        }
        (int code, bool validCode) = ProviderWorkflowValues.RunningHubPayloadCode(rawResponse);
        if (!validCode)
        {
            // 上传接口有少量代理只返回 data.fileName；文件名存在即可视为成功。
            validCode = ProviderWorkflowValues.RunningHubFileName(rawResponse).Length > 0;
            code = 0;
        }
        string fileName = ProviderWorkflowValues.RunningHubFileName(rawResponse);
        if (!validCode || code != 0 || fileName.Length == 0)
        {
            throw new InvalidOperationException(
                "RunningHub 上传素材失败：" + ProviderWorkflowValues.RunningHubFailureMessage(rawResponse));
        }
        return fileName;
    }

    /// <summary>对应 Go: <c>runningHubUploadAuthFailure</c>。</summary>
    internal static string UploadAuthFailureMessage(ProviderHttpException error)
    {
        if (error.StatusCode != 401
            || !error.Body.ToLowerInvariant().Contains("apikey verification failed", StringComparison.Ordinal))
        {
            return "";
        }
        return "RunningHub 参考素材上传接口认证失败（HTTP 401）：ApiKey verification failed。"
            + "请确认“素材上传 API Key（企业级）”有效，并且它与 Base URL 属于同一个 RunningHub 站点";
    }

    // ------------------------------------------------------------ nodeInfo 组装

    /// <summary>对应 Go: <c>runningHubNodeInfoWithWorkflow</c>。</summary>
    private static List<Dictionary<string, object?>> BuildNodeInfo(
        List<WorkflowField> fields,
        Dictionary<string, string> files,
        TextTaskInput input,
        Dictionary<string, object?>? workflow)
    {
        List<Dictionary<string, object?>> items = [];
        foreach (WorkflowField field in fields)
        {
            if (field.Enabled is false
                || field.NodeID.Trim().Length == 0
                || field.FieldName.Trim().Length == 0)
            {
                continue;
            }
            // RunningHub 的 nodeInfoList 只接受字符串 fieldValue。部分内部节点不会把
            // 字符串恢复为数值，因此安全边界必须由服务端强制执行，不能依赖前端隐藏。
            if (IsUnsafeInternalField(workflow, field))
            {
                continue;
            }
            (object? value, bool present, Exception? error) = ResolveFieldValue(field, files, input);
            if (error is not null)
            {
                throw error;
            }
            if (!present)
            {
                if (field.Required)
                {
                    throw new InvalidOperationException(
                        "工作流字段 " + FirstNonEmpty(field.ID, field.FieldName) + " 缺少值");
                }
                continue;
            }
            ProviderWorkflowValues.ValidateRunningHubFieldValue(field, value);
            string encoded = ProviderWorkflowValues.WorkflowScalarString(value);
            if (!ShouldSendField(workflow, field, encoded))
            {
                continue;
            }
            items.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["nodeId"] = field.NodeID,
                ["fieldName"] = field.FieldName,
                ["fieldValue"] = encoded,
            });
        }
        return items;
    }

    /// <summary>对应 Go: <c>resolveWorkflowFieldValue</c>。</summary>
    private static (object? Value, bool Present, Exception? Error) ResolveFieldValue(
        WorkflowField field,
        Dictionary<string, string> files,
        TextTaskInput input)
    {
        string source = ProviderWorkflowValues.NormalizeWorkflowFieldSource(field);
        ProviderConfig config = input.Config;
        // 画布参数按 nodeId + fieldName 独立保存时 source 可能为空；枚举字段仍必须
        // 使用工作流原始 options 的完整值，不能把用户界面上的比例简称直接发给 ComfyUI。
        if (source.Length == 0 && field.FieldName.Trim().Equals("aspect_ratio", StringComparison.OrdinalIgnoreCase))
        {
            // 旧版前端只把比例写入 Config.Size，没有同步改写字段映射。仅对非 1:1 的
            // 显式比例做回填，避免没有动态参数时覆盖工作流自己的 1:1 默认值。
            string requested = config.Size.Trim();
            if (requested.Length > 0
                && !ProviderWorkflowValues.WorkflowAspectRatio(requested).Equals("1:1", StringComparison.OrdinalIgnoreCase))
            {
                string value = ProviderWorkflowValues.WorkflowAspectRatioValue(field, requested);
                if (WorkflowFieldCodec.GenericString(value).Trim().Length > 0)
                {
                    return (value, true, null);
                }
            }
            string raw = ProviderWorkflowValues.WorkflowOptionString(
                FirstNonNil(field.FieldValue, field.Value)).Trim();
            if (raw.Length > 0)
            {
                string value = ProviderWorkflowValues.WorkflowAspectRatioValue(field, raw);
                return (value, true, null);
            }
        }
        if (source.Length > 0)
        {
            switch (StripSourceKey(source))
            {
                case "prompt" or "text" or "positiveprompt" or "positive":
                    return (input.Prompt, input.Prompt.Trim().Length > 0, null);
                case "size" or "imagesize":
                    return (config.Size, config.Size.Trim().Length > 0, null);
                case "resolution":
                    if (input.Mode.Trim().Equals("video", StringComparison.OrdinalIgnoreCase))
                    {
                        object? value = ProviderWorkflowValues.VideoResolutionValue(field, config.VQuality);
                        return (value, WorkflowFieldCodec.GenericString(value).Trim().Length > 0, null);
                    }
                    return (config.Size, config.Size.Trim().Length > 0, null);
                case "aspectratio" or "ratio" or "imageaspectratio" or "imageratio" or "videoaspectratio" or "videoratio":
                {
                    string value = ProviderWorkflowValues.WorkflowAspectRatioValue(field, config.Size);
                    return (value, value.Length > 0, null);
                }
                case "sizewidth" or "width" or "imagewidth" or "videowidth":
                {
                    string value = ProviderWorkflowValues.WorkflowDimensionPart(input.Mode, config.Size, config.VQuality, 0);
                    return (value, value.Length > 0, null);
                }
                case "sizeheight" or "height" or "imageheight" or "videoheight":
                {
                    string value = ProviderWorkflowValues.WorkflowDimensionPart(input.Mode, config.Size, config.VQuality, 1);
                    return (value, value.Length > 0, null);
                }
                case "quality":
                    return (config.Quality, config.Quality.Trim().Length > 0, null);
                case "count" or "batch" or "batchsize":
                    return (config.Count, config.Count.Trim().Length > 0, null);
                case "videoseconds" or "duration":
                {
                    object? value = ProviderWorkflowValues.VideoDurationValue(field, config.VideoSeconds);
                    return (value, WorkflowFieldCodec.GenericString(value).Trim().Length > 0, null);
                }
                case "vquality" or "videoquality":
                {
                    object? value = ProviderWorkflowValues.VideoResolutionValue(field, config.VQuality);
                    return (value, WorkflowFieldCodec.GenericString(value).Trim().Length > 0, null);
                }
                case "videogenerateaudio" or "generateaudio":
                    return (config.VideoGenerateAudio, config.VideoGenerateAudio.Trim().Length > 0, null);
                case "videowatermark" or "watermark":
                    return (config.VideoWatermark, config.VideoWatermark.Trim().Length > 0, null);
                case "audioformat":
                    return (config.AudioFormat, config.AudioFormat.Trim().Length > 0, null);
                case "systemprompt":
                    return (config.SystemPrompt, config.SystemPrompt.Trim().Length > 0, null);
                case "transparentbackground":
                    return (config.TransparentBackground, config.TransparentBackground.Trim().Length > 0, null);
                case "audiovoice" or "voice":
                    return (config.AudioVoice, config.AudioVoice.Trim().Length > 0, null);
                case "audiospeed":
                    return (config.AudioSpeed, config.AudioSpeed.Trim().Length > 0, null);
                case "audioinstructions":
                    return (config.AudioInstructions, config.AudioInstructions.Trim().Length > 0, null);
            }
            int index = field.SourceIndex;
            if (field.ImageOrder > 0)
            {
                index = field.ImageOrder - 1;
            }
            ProviderMedia media;
            switch (StripSourceKey(source))
            {
                case "referenceimage" or "image" or "referenceimages":
                    if (index < 0 || index >= input.ReferenceImages.Count)
                    {
                        return (null, false, null);
                    }
                    media = input.ReferenceImages[index];
                    break;
                case "referencevideo" or "video" or "referencevideos":
                    if (index < 0 || index >= input.ReferenceVideos.Count)
                    {
                        return (null, false, null);
                    }
                    media = input.ReferenceVideos[index];
                    break;
                case "referenceaudio" or "audio" or "referenceaudios":
                    if (index < 0 || index >= input.ReferenceAudios.Count)
                    {
                        return (null, false, null);
                    }
                    media = input.ReferenceAudios[index];
                    break;
                case "mask":
                    if (input.Mask is null)
                    {
                        return (null, false, null);
                    }
                    media = input.Mask;
                    break;
                default:
                    return (null, false, new InvalidOperationException("不支持的工作流字段来源：" + field.Source));
            }
            if (!files.TryGetValue(media.ID, out string? name) || name.Length == 0)
            {
                return (null, false, new InvalidOperationException("工作流参考素材尚未上传"));
            }
            return (name, true, null);
        }
        if (field.RandomEnabled)
        {
            object? randomMax = field.Max;
            if (ProviderWorkflowValues.IsRunningHubInterface(config.InterfaceType)
                && ProviderWorkflowValues.IsWorkflowSeedField(field.FieldName))
            {
                // RunningHub 的随机 Seed 按 uint32 传输，不能沿用 JavaScript 安全整数上限。
                const long seedMax = (1L << 32) - 1;
                long maxValue = ProviderWorkflowValues.WorkflowIntegerBound(field.Max, seedMax);
                if (maxValue > seedMax)
                {
                    maxValue = seedMax;
                }
                randomMax = maxValue;
            }
            try
            {
                long random = ProviderWorkflowValues.RandomWorkflowInteger(field.Min, randomMax);
                return (random, true, null);
            }
            catch (Exception randomError)
            {
                return (null, false, randomError);
            }
        }
        if (field.FieldValue is not null)
        {
            return (field.FieldValue, true, null);
        }
        if (field.Value is not null)
        {
            return (field.Value, true, null);
        }
        return (null, false, null);
    }

    /// <summary>对应 Go: <c>shouldSendRunningHubWorkflowField</c>。</summary>
    private static bool ShouldSendField(
        Dictionary<string, object?>? workflow,
        WorkflowField field,
        string encoded)
    {
        if (workflow is null || workflow.Count == 0)
        {
            // AI App 只有公开参数列表，没有可用于比较的工作流 JSON。
            return true;
        }
        if (!workflow.TryGetValue(field.NodeID.Trim(), out object? nodeObject)
            || nodeObject is not IReadOnlyDictionary<string, object?> node)
        {
            return true;
        }
        Dictionary<string, object?>? inputs = JsonFields.NestedObject(node, "inputs");
        if (inputs is null)
        {
            return true;
        }
        bool exists = inputs.TryGetValue(field.FieldName.Trim(), out object? original);
        if (exists && IsLinkValue(original))
        {
            // 已连接输入属于工作流拓扑，不能被旧字段映射拆成字符串覆盖。
            return false;
        }
        if (ProviderWorkflowValues.NormalizeWorkflowFieldSource(field).Length > 0 || field.RandomEnabled)
        {
            return !IsAutomaticInternalBinding(node, field);
        }
        if (!exists)
        {
            return true;
        }
        // 拉取参数时会保存所有 widget 的默认值；未修改项无需重复覆盖工作流。
        return encoded != ProviderWorkflowValues.WorkflowScalarString(original);
    }

    /// <summary>对应 Go: <c>isRunningHubAutomaticInternalBinding</c>。</summary>
    private static bool IsAutomaticInternalBinding(IReadOnlyDictionary<string, object?> node, WorkflowField field)
    {
        if (field.SourceAutomatic is false)
        {
            return false;
        }
        string classType = NodeStringValue(node, "class_type").ToLowerInvariant();
        if (classType != "imageresize+")
        {
            return false;
        }
        // 旧配置没有 sourceAutomatic，但 ImageResize+ 的宽高来源是按同名字段自动误判的。
        // 保留节点自身尺寸链；用户仍可将来源设为默认并明确修改静态值。
        string source = StripSourceKey(ProviderWorkflowValues.NormalizeWorkflowFieldSource(field));
        return source
            is "size" or "imagesize" or "sizewidth" or "width" or "imagewidth" or "videowidth"
                or "sizeheight" or "height" or "imageheight" or "videoheight";
    }

    /// <summary>对应 Go: <c>isRunningHubUnsafeInternalField</c>。</summary>
    private static bool IsUnsafeInternalField(Dictionary<string, object?>? workflow, WorkflowField field)
    {
        if (field.SafeToOverride is false)
        {
            return true;
        }
        string classType = (field.ClassType ?? "").Trim().ToLowerInvariant();
        if (workflow is not null
            && workflow.TryGetValue(field.NodeID.Trim(), out object? nodeObject)
            && nodeObject is IReadOnlyDictionary<string, object?> node)
        {
            classType = NodeStringValue(node, "class_type").ToLowerInvariant();
        }
        string fieldName = field.FieldName.Trim().ToLowerInvariant();
        if (classType == "int" && fieldName == "value")
        {
            return true;
        }
        return classType == "imageresize+"
            && fieldName is "width" or "height" or "multiple_of";
    }

    /// <summary>对应 Go: <c>validateWorkflowMediaInputs</c>。</summary>
    private static void ValidateMediaInputs(List<WorkflowField> fields, TextTaskInput input)
    {
        Dictionary<string, int> capacities = new(StringComparer.Ordinal)
        {
            ["referenceimage"] = 0,
            ["referencevideo"] = 0,
            ["referenceaudio"] = 0,
        };
        bool hasMaskMapping = false;
        foreach (WorkflowField field in fields)
        {
            if (field.Enabled is false)
            {
                continue;
            }
            string source = StripSourceKey(ProviderWorkflowValues.NormalizeWorkflowFieldSource(field));
            if (source == "mask")
            {
                hasMaskMapping = true;
                continue;
            }
            string key = source switch
            {
                "referenceimage" or "image" or "referenceimages" => "referenceimage",
                "referencevideo" or "video" or "referencevideos" => "referencevideo",
                "referenceaudio" or "audio" or "referenceaudios" => "referenceaudio",
                _ => "",
            };
            if (key.Length == 0)
            {
                continue;
            }
            int index = field.SourceIndex;
            if (field.ImageOrder > 0)
            {
                index = field.ImageOrder - 1;
            }
            if (index < 0)
            {
                index = 0;
            }
            if (index + 1 > capacities[key])
            {
                capacities[key] = index + 1;
            }
        }
        (string Label, int Count, int Capacity)[] checks =
        {
            ("参考图片", input.ReferenceImages.Count, capacities["referenceimage"]),
            ("参考视频", input.ReferenceVideos.Count, capacities["referencevideo"]),
            ("参考音频", input.ReferenceAudios.Count, capacities["referenceaudio"]),
        };
        foreach ((string label, int count, int capacity) in checks)
        {
            if (count > capacity)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "工作流只配置了 {0} 个{1}槽位，但画布传入了 {2} 个；请在工作流字段映射中补齐槽位",
                    capacity, label, count));
            }
        }
        if (input.Mask is not null && !hasMaskMapping)
        {
            throw new InvalidOperationException("画布传入了蒙版，但工作流没有配置蒙版字段映射");
        }
    }

    /// <summary>对应 Go: <c>workflowFieldsBindPrompt</c>。</summary>
    private static bool WorkflowFieldsBindPrompt(List<WorkflowField> fields)
    {
        foreach (WorkflowField field in fields)
        {
            if (field.Enabled is false
                || field.NodeID.Trim().Length == 0
                || field.FieldName.Trim().Length == 0)
            {
                continue;
            }
            switch (StripSourceKey(ProviderWorkflowValues.NormalizeWorkflowFieldSource(field)))
            {
                case "prompt" or "text" or "positiveprompt" or "positive":
                    return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------ 基础传输

    /// <summary>对应 Go: <c>runningHubJSON</c>（仅自定义头，无渠道鉴权）。</summary>
    private async Task<Dictionary<string, object?>> PostJsonAsync(
        ProviderConfig config,
        string endpoint,
        object body,
        CancellationToken cancellationToken)
    {
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(body, ProviderRequestTypes.WriteOptions);
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(encoded),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        OutboundHttpClient.ApplyHeaders(request, config.Headers);
        ProviderTransport.OutboundResult result = await ProviderTransport.SendAsync(
            request,
            _context?.MaxResponseBytes ?? ProviderTransport.DefaultMaxResponseBytes,
            null,
            cancellationToken,
            _clientFactory).ConfigureAwait(false);
        Dictionary<string, object?>? payload = JsonFields.ParseObject(Encoding.UTF8.GetString(result.Data));
        if (payload is null)
        {
            throw new ProviderResponseDecodeException(new InvalidOperationException("接口返回的 JSON 不是对象"));
        }
        return payload;
    }

    /// <summary>对应 Go: <c>getExternalBinary</c>（跨源下载不带渠道鉴权）。</summary>
    private async Task<(byte[] Data, string MIMEType)> GetExternalBinaryAsync(
        string rawUrl,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, rawUrl);
        ProviderTransport.ApplyDefaultHeaders(request);
        ProviderTransport.OutboundResult result = await ProviderTransport.SendAsync(
            request,
            _context?.MaxResponseBytes ?? ProviderTransport.DefaultMaxResponseBytes,
            null,
            cancellationToken,
            _clientFactory).ConfigureAwait(false);
        return (result.Data, result.MIMEType);
    }

    /// <summary>对应 Go: <c>fmt.Errorf("前缀：%w", err)</c> 的错误包装。</summary>
    private static Exception Wrap(Exception error, string prefix) =>
        new InvalidOperationException(prefix + "：" + error.Message, error);

    private static string? PublicMediaURL(ProviderMedia media) =>
        ((media.DataURL.Length > 0 ? media.DataURL : media.URL) ?? "").Trim();

    private static string StripSourceKey(string source) =>
        source.Replace("_", "").Replace("-", "");

    private static string FirstNonEmpty(string left, string right) =>
        left.Trim().Length > 0 ? left : right;

    private static object? FirstNonNil(params object?[] values) =>
        values.FirstOrDefault(value => value is not null);

    /// <summary>把反序列化得到的 JsonElement 树归一化为 Go 风格 map 形态。</summary>
    private static Dictionary<string, object?>? Objectify(Dictionary<string, object?>? workflow)
    {
        if (workflow is null)
        {
            return null;
        }
        string json = JsonSerializer.Serialize(workflow, ProviderRequestTypes.WriteOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        return JsonFields.FromElement(document.RootElement) as Dictionary<string, object?>;
    }

    private static Dictionary<string, object?>? AsMap(object? value) => value switch
    {
        Dictionary<string, object?> map => map,
        IReadOnlyDictionary<string, object?> readOnly => new Dictionary<string, object?>(readOnly, StringComparer.Ordinal),
        _ => null,
    };

    /// <summary>对应 Go: <c>isWorkflowLinkValue</c>。</summary>
    internal static bool IsLinkValue(object? value) =>
        value is List<object?> { Count: 2 } items
        && WorkflowFieldCodec.GenericString(items[0]) != ""
        && IsIntegerLike(items[1]);

    private static bool IsIntegerLike(object? value) => value switch
    {
        sbyte or byte or short or ushort or int or uint or long or ulong => true,
        double number => number == Math.Truncate(number) && Math.Abs(number) < 9.2e18,
        JsonElement { ValueKind: JsonValueKind.Number } element =>
            int.TryParse(element.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        JsonElement { ValueKind: JsonValueKind.String } element =>
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        _ => false,
    };

    /// <summary>对应 Go: <c>stringValue</c>。</summary>
    internal static string NodeStringValue(IReadOnlyDictionary<string, object?> payload, string key)
    {
        string text = WorkflowFieldCodec.GenericString(
            payload.TryGetValue(key, out object? value) ? value : null).Trim();
        return text == "<nil>" ? "" : text;
    }
}
