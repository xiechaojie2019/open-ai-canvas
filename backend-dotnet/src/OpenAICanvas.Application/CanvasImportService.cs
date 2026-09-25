#nullable enable
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Outbound;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>LibTV/TapNow 画布导入。只访问固定官方上游，并复刻 Go 的归属、大小与媒体 URL 守卫。</summary>
public sealed class CanvasImportService
{
    private const string LibTVURL = "https://api.liblib.tv/api/canvas/project/detail";
    private const string TapNowURLPrefix = "https://app.tapnow.media/api/conversation/v1/canvas-share/";
    private const string UserAgent = "InfiniteCanvas/1.0 (+https://github.com/ddcat-ai/open-ai-canvas)";
    private const int LibTVResponseLimit = 8 << 20;
    private const int TapNowResponseLimit = 16 << 20;
    private const int TapNowNodeLimit = 1000;
    private const int TapNowConnectionLimit = 5000;

    private readonly Repository _repository;
    private readonly PlatformSettingsService _settings;
    private readonly string _brandName;

    public CanvasImportService(Repository repository, PlatformSettingsService settings, string brandName = "影策")
    {
        _repository = repository;
        _settings = settings;
        _brandName = string.IsNullOrWhiteSpace(brandName) ? "影策" : brandName.Trim();
    }

    public async Task<LibTVImportResultDto> ImportLibTVAsync(
        string userId, string canvasProjectId, string projectUuid, CancellationToken cancellationToken = default)
    {
        await RequireCanvasAsync(userId, canvasProjectId, "已同步的" + _brandName + "画布", cancellationToken)
            .ConfigureAwait(false);
        string token = await _settings.ReadEnabledLibTVTokenAsync(cancellationToken).ConfigureAwait(false);
        projectUuid = projectUuid.Trim();
        if (!IsLibTVUUID(projectUuid))
        {
            throw AppError.BadAuthRequest("LibTV 画布 UUID 格式无效");
        }
        JsonElement detail = await FetchLibTVAsync(projectUuid, token, cancellationToken).ConfigureAwait(false);
        return AdaptLibTV(detail, projectUuid);
    }

    public async Task<TapNowImportResultDto> ImportTapNowAsync(
        string userId, string canvasProjectId, string shareId, CancellationToken cancellationToken = default)
    {
        await RequireCanvasAsync(userId, canvasProjectId, "已同步的故事创作画布", cancellationToken)
            .ConfigureAwait(false);
        shareId = shareId.Trim();
        if (!IsTapNowShareId(shareId))
        {
            throw AppError.BadAuthRequest("TapNow 分享 ID 格式无效");
        }
        JsonElement detail = await FetchTapNowAsync(shareId, cancellationToken).ConfigureAwait(false);
        return AdaptTapNow(detail, shareId);
    }

    private async Task RequireCanvasAsync(
        string userId, string canvasProjectId, string prompt, CancellationToken cancellationToken)
    {
        userId = userId.Trim();
        canvasProjectId = canvasProjectId.Trim();
        if (userId.Length == 0 || canvasProjectId.Length == 0)
        {
            throw AppError.Unauthorized("请先打开" + prompt);
        }
        if (await _repository.CanvasProjectForUserAsync(userId, canvasProjectId, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }
    }

    private static async Task<JsonElement> FetchLibTVAsync(
        string projectUuid, string token, CancellationToken cancellationToken)
    {
        using HttpClient client = OutboundHttpClient.Create(TimeSpan.FromSeconds(20), allowAutoRedirect: false);
        using HttpRequestMessage request = new(HttpMethod.Get, LibTVURL + "?uuid=" + Uri.EscapeDataString(projectUuid));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("token", token);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException ||
                                       error is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw AppError.Wrap(502, "LibTV 请求失败，请检查网络或 Token", error);
        }
        using (response)
        {
            byte[] body = await ReadBodyAsync(response.Content, LibTVResponseLimit, "LibTV 响应过大", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw AppError.New(502, $"LibTV 请求失败（HTTP {(int)response.StatusCode}）");
            }
            LibTVEnvelope? envelope = Deserialize<LibTVEnvelope>(body, "LibTV 响应格式无效");
            if (envelope is null) throw AppError.New(502, "LibTV 响应格式无效");
            if (envelope.Code != 0)
            {
                throw AppError.New(502, FirstNonEmpty(envelope.Msg, "LibTV 返回业务错误"));
            }
            JsonElement effective = Property(Property(Property(envelope.Data, "projectMeta"), "effective"), "canRead");
            bool canRead = effective.ValueKind == JsonValueKind.True;
            bool canCopy = Property(Property(Property(envelope.Data, "projectMeta"), "effective"), "canCopy").ValueKind == JsonValueKind.True;
            if (!canRead || !canCopy) throw AppError.New(502, "当前 LibTV 画布不允许复制");
            return envelope.Data.Clone();
        }
    }

    private static async Task<JsonElement> FetchTapNowAsync(string shareId, CancellationToken cancellationToken)
    {
        using HttpClient client = OutboundHttpClient.Create(TimeSpan.FromSeconds(20), allowAutoRedirect: false);
        using HttpRequestMessage request = new(HttpMethod.Get, TapNowURLPrefix + Uri.EscapeDataString(shareId));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException ||
                                       error is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw AppError.Wrap(502, "TapNow 请求失败，请检查网络或分享链接", error);
        }
        using (response)
        {
            byte[] body = await ReadBodyAsync(response.Content, TapNowResponseLimit, "TapNow 画布响应过大", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw AppError.New(502, $"TapNow 请求失败（HTTP {(int)response.StatusCode}）");
            }
            TapNowEnvelope? envelope = Deserialize<TapNowEnvelope>(body, "TapNow 响应格式无效");
            if (envelope is null) throw AppError.New(502, "TapNow 响应格式无效");
            if (envelope.Code != 0)
            {
                throw AppError.New(502, FirstNonEmpty(envelope.Msg, envelope.Message, "TapNow 返回业务错误"));
            }
            return envelope.Data.Clone();
        }
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpContent content, int maxBytes, string tooLargeMessage, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maxBytes)
        {
            throw AppError.New(502, tooLargeMessage);
        }
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream body = new();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (body.Length + read > maxBytes) throw AppError.New(502, tooLargeMessage);
            body.Write(buffer, 0, read);
        }
        return body.ToArray();
    }

    private static T? Deserialize<T>(byte[] body, string message)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, GoJson.ReadOptions);
        }
        catch (JsonException error)
        {
            throw AppError.Wrap(502, message, error);
        }
    }

    internal static LibTVImportResultDto AdaptLibTV(JsonElement detail, string requestedUuid = "")
    {
        DateTime now = DateTime.UtcNow;
        string batchId = NewBatchId("");
        JsonElement projectMeta = Property(detail, "projectMeta");
        string projectUuid = FirstNonEmpty(Text(projectMeta, "uuid"), requestedUuid);
        string projectName = Text(projectMeta, "name");
        List<LibTVCanvasNodeDto> nodes = [];
        List<LibTVCanvasConnectionDto> connections = [];
        List<CanvasImportIssueDto> skippedNodes = [];
        List<CanvasImportIssueDto> skippedConnections = [];
        List<CanvasImportWarningDto> warnings = [];
        Dictionary<string, string> mapping = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        int multiResult = 0, stale = 0, reusedFailed = 0, placeholders = 0, converted = 0;

        foreach (JsonElement raw in Array(Property(detail, "nodeList")))
        {
            string nodeKey = Text(raw, "nodeKey").Trim();
            string name = Text(raw, "name").Trim();
            if (nodeKey.Length == 0)
            {
                skippedNodes.Add(new() { Name = EmptyToNull(name), Reason = "节点缺少 nodeKey" });
                continue;
            }
            if (!seen.Add(nodeKey))
            {
                skippedNodes.Add(new() { ID = nodeKey, Name = EmptyToNull(name), Reason = "节点 nodeKey 重复" });
                continue;
            }
            if (!TryParseObject(Text(raw, "data"), out JsonElement data))
            {
                skippedNodes.Add(new() { ID = nodeKey, Name = EmptyToNull(name), Reason = "节点数据格式无效" });
                continue;
            }
            string sourceType = Text(data, "type").Trim().ToLowerInvariant();
            string kind = sourceType == "material-style" ? "image" : sourceType;
            if (kind is not ("image" or "video"))
            {
                skippedNodes.Add(new() { ID = nodeKey, Name = EmptyToNull(name), Reason = "暂不支持的节点类型" });
                continue;
            }
            (string mediaUrl, int mediaIndex) = FirstURL(Property(data, "url"), null);
            if (mediaUrl.Length == 0 && sourceType == "material-style")
            {
                (mediaUrl, mediaIndex) = FirstURL(JsonSerializer.SerializeToElement(new[] { Text(data, "coverUrl") }), null);
            }
            JsonElement position = Property(raw, "position");
            if (!TryStringDouble(position, "positionX", out double x) || !TryStringDouble(position, "positionY", out double y))
            {
                skippedNodes.Add(new() { ID = nodeKey, Name = EmptyToNull(name), Reason = "节点坐标无效" });
                continue;
            }
            JsonElement measured = Property(raw, "measured");
            bool validWidth = TryStringDouble(measured, "width", out double width) && width > 0;
            bool validHeight = TryStringDouble(measured, "height", out double height) && height > 0;
            if (!validWidth) width = 480;
            if (!validHeight) height = 300;
            string title = name.Length == 0 ? kind + " 节点" : name;
            JsonElement parameters = Property(data, "params");
            LibTVCanvasNodeDto node = new()
            {
                ID = "libtv-" + batchId + "-" + nodeKey, Type = kind, Title = title, X = x, Y = y,
                Width = width, Height = height, Content = mediaUrl,
                Prompt = EmptyToNull(Text(parameters, "prompt").Trim()), Model = EmptyToNull(Text(parameters, "model").Trim()),
                Metadata = new LibTVImportMetadataDto
                {
                    ProjectUUID = projectUuid, NodeKey = nodeKey, BatchID = batchId,
                    SourceType = EmptyToNull(sourceType), StyleAssetUUID = EmptyToNull(Text(data, "styleAssetUuid").Trim()),
                    StyleVersionUUID = EmptyToNull(Text(data, "styleVersionUuid").Trim()), StyleName = EmptyToNull(Text(data, "styleName").Trim()),
                },
            };
            JsonElement resourceItems = Property(Property(data, "_resourceMeta"), "items");
            JsonElement resource = mediaIndex >= 0 && mediaIndex < resourceItems.GetArrayLengthSafe()
                ? resourceItems.EnumerateArray().ElementAt(mediaIndex) : default;
            node = WithLibTVMedia(node, PositiveInt(resource, "width"), PositiveInt(resource, "height"),
                PositiveDouble(resource, "durationSec"), InferMime(kind, mediaUrl));
            int taskStatus = Number(Property(Property(data, "taskInfo"), "status"));
            if (taskStatus == 0)
            {
                taskStatus = Number(Property(Property(raw, "taskInfo"), "status"));
            }
            if (mediaUrl.Length == 0)
            {
                placeholders++;
                node = WithLibTVStatus(node, taskStatus == 3 ? "error" : "idle",
                    taskStatus == 3 ? FirstNonEmpty(Text(Property(data, "taskInfo"), "failedReason"), "LibTV 生成任务失败") : null);
            }
            else
            {
                node = WithLibTVStatus(node, "success", null);
                if (taskStatus == 3) reusedFailed++;
            }
            mapping[nodeKey] = node.ID;
            nodes.Add(node);
            if (sourceType == "material-style") converted++;
            if (!validWidth || !validHeight) warnings.Add(new() { ID = nodeKey, Message = $"节点“{title}”尺寸无效，已使用默认尺寸。" });
            if (Property(data, "url").GetArrayLengthSafe() > 1 && mediaUrl.Length > 0)
            {
                multiResult++;
                warnings.Add(new() { ID = nodeKey, Message = $"节点“{title}”包含 {Property(data, "url").GetArrayLengthSafe()} 个结果，已导入第 {mediaIndex + 1} 个。" });
            }
            if (Bool(data, "isStale") || Bool(raw, "isStale"))
            {
                stale++;
                warnings.Add(new() { ID = nodeKey, Message = $"节点“{title}”已标记为过期，但现有资源仍已导入。" });
            }
        }

        HashSet<string> seenConnections = new(StringComparer.Ordinal);
        foreach (JsonElement raw in Array(Property(detail, "connectionList")))
        {
            string connectionId = Text(raw, "connectionId").Trim();
            if (connectionId.Length == 0)
            {
                skippedConnections.Add(new() { Reason = "连接缺少 connectionId" });
                continue;
            }
            if (!seenConnections.Add(connectionId))
            {
                skippedConnections.Add(new() { ID = connectionId, Reason = "连接 connectionId 重复" });
                continue;
            }
            if (!mapping.TryGetValue(Text(raw, "source").Trim(), out string? from) ||
                !mapping.TryGetValue(Text(raw, "target").Trim(), out string? to))
            {
                skippedConnections.Add(new() { ID = connectionId, Reason = "连接端点节点未导入" });
                continue;
            }
            connections.Add(new() { ID = "libtv-" + batchId + "-" + connectionId, FromNodeID = from, ToNodeID = to });
        }
        if (nodes.Count == 0) throw new InvalidOperationException("LibTV 画布没有可导入的有效节点");
        return new LibTVImportResultDto
        {
            BatchID = batchId, BatchCreatedAt = now, ProjectUUID = projectUuid, ProjectName = projectName,
            Nodes = nodes, Connections = connections, ImportedNodeCount = nodes.Count, ImportedConnectionCount = connections.Count,
            SkippedNodes = skippedNodes, SkippedConnections = skippedConnections, Warnings = warnings,
            MultiResultNodeCount = multiResult, StaleNodeCount = stale, ReusedFailedNodeCount = reusedFailed,
            PlaceholderNodeCount = placeholders, ConvertedSpecialCount = converted,
        };
    }

    internal static TapNowImportResultDto AdaptTapNow(JsonElement detail, string shareId)
    {
        if (!IsTapNowShareId(shareId)) throw AppError.BadAuthRequest("TapNow 分享 ID 格式无效");
        JsonElement rawNodes = Property(detail, "nodes");
        if (rawNodes.GetArrayLengthSafe() > TapNowNodeLimit)
        {
            throw new InvalidOperationException($"TapNow 画布节点过多（最多支持 {TapNowNodeLimit} 个）");
        }
        DateTime now = DateTime.UtcNow;
        string batchId = NewBatchId("tapnow-");
        string projectName = FirstNonEmpty(Text(detail, "name").Trim(), "TapNow 画布");
        List<TapNowCanvasNodeDto> nodes = [];
        List<TapNowCanvasConnectionDto> connections = [];
        List<CanvasImportIssueDto> skippedNodes = [];
        List<CanvasImportIssueDto> skippedConnections = [];
        List<CanvasImportWarningDto> warnings = [];
        Dictionary<string, string> mapping = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        int multiResult = 0, reusedFailed = 0, placeholders = 0;

        foreach (JsonElement raw in Array(rawNodes))
        {
            if (Text(raw, "deleted_at").Trim().Length > 0) continue;
            string nodeId = Text(raw, "id").Trim();
            long shortId = Int64(raw, "short_id");
            if (nodeId.Length == 0 && shortId != 0) nodeId = shortId.ToString(CultureInfo.InvariantCulture);
            if (nodeId.Length == 0) { skippedNodes.Add(new() { Reason = "节点缺少 ID" }); continue; }
            if (Encoding.UTF8.GetByteCount(nodeId) > 128)
            {
                skippedNodes.Add(new() { ID = TruncateUtf8(nodeId, 128), Reason = "节点 ID 过长" });
                continue;
            }
            if (!seen.Add(nodeId)) { skippedNodes.Add(new() { ID = nodeId, Reason = "节点 ID 重复" }); continue; }
            JsonElement data;
            if (!TryNodeData(raw, out data)) { skippedNodes.Add(new() { ID = nodeId, Reason = "节点数据格式无效" }); continue; }
            string kind = FirstNonEmpty(Text(raw, "type"), Text(data, "type")).Trim().ToLowerInvariant();
            string dataTitle = Text(data, "title").Trim();
            if (kind is not ("image" or "video" or "audio" or "text"))
            {
                skippedNodes.Add(new() { ID = nodeId, Name = EmptyToNull(dataTitle), Reason = "暂不支持的节点类型" });
                continue;
            }
            JsonElement position = Property(raw, "position");
            if (!TryDouble(position, "x", out double x) || !TryDouble(position, "y", out double y))
            {
                skippedNodes.Add(new() { ID = nodeId, Name = EmptyToNull(dataTitle), Reason = "节点坐标无效" });
                continue;
            }
            (string content, int mediaIndex) = FirstURLFromTapNow(data);
            if (kind == "text") { content = FirstNonEmpty(Text(data, "text"), Text(data, "prompt")); mediaIndex = -1; }
            JsonElement measured = Property(raw, "measured");
            bool validWidth = TryDouble(measured, "width", out double width) && width > 0;
            bool validHeight = TryDouble(measured, "height", out double height) && height > 0;
            if (!validWidth) width = 480;
            if (!validHeight) height = 300;
            JsonElement parameters = Property(data, "params");
            JsonElement metadata = Property(data, "__metadata");
            string title = dataTitle.Length == 0 ? kind + " 节点" : dataTitle;
            TapNowCanvasNodeDto node = new()
            {
                ID = "tapnow-" + batchId + "-" + nodeId, Type = kind, Title = title, X = x, Y = y,
                Width = width, Height = height, Content = content,
                Prompt = EmptyToNull(FirstNonEmpty(Text(data, "prompt"), Param(parameters, "prompt"))),
                Model = EmptyToNull(Param(parameters, "model")), Size = EmptyToNull(FirstParam(parameters, "aspectRatio", "size")),
                Quality = EmptyToNull(ImageQuality(parameters)), Seconds = EmptyToNull(FirstParam(parameters, "duration", "seconds")),
                VQuality = EmptyToNull(FirstParam(parameters, "resolution", "vquality")), GenerateAudio = EmptyToNull(Param(parameters, "generateAudio")),
                NaturalWidth = PositiveInt(metadata, "width"), NaturalHeight = PositiveInt(metadata, "height"),
                DurationMs = Milliseconds(PositiveDouble(metadata, "duration")), MimeType = InferTapNowMime(kind, content, Text(data, "prompt")),
                Metadata = new TapNowImportMetadataDto { ShareID = shareId, NodeID = nodeId, BatchID = batchId, SourceType = EmptyToNull(Text(data, "type").Trim()) },
            };
            string status = Param(Property(data, "taskInfo"), "status").ToLowerInvariant();
            if (content.Length > 0)
            {
                node = WithTapNowStatus(node, "success", null);
                if (IsFailed(status)) reusedFailed++;
            }
            else if (IsFailed(status))
            {
                string error = FirstNonEmpty(Text(data, "error"), Text(data, "failedReason"), Param(Property(data, "taskInfo"), "error"), Param(Property(data, "taskInfo"), "failedReason"), Param(Property(data, "taskInfo"), "message"));
                node = WithTapNowStatus(node, "error", FirstNonEmpty(error, "TapNow 生成任务失败"));
                placeholders++;
            }
            else { node = WithTapNowStatus(node, "idle", null); placeholders++; }
            mapping[nodeId] = node.ID;
            if (shortId != 0) mapping[shortId.ToString(CultureInfo.InvariantCulture)] = node.ID;
            nodes.Add(node);
            if (!validWidth || !validHeight) warnings.Add(new() { ID = nodeId, Message = $"节点“{title}”尺寸无效，已使用默认尺寸。" });
            if (mediaIndex >= 0 && Property(data, "options").GetArrayLengthSafe() > 1)
            {
                multiResult++;
                warnings.Add(new() { ID = nodeId, Message = $"节点“{title}”包含多个结果，已导入首个结果。" });
            }
        }

        JsonElement rawConnections = Property(detail, "connections");
        if (rawConnections.GetArrayLengthSafe() > TapNowConnectionLimit)
        {
            warnings.Add(new() { Message = $"连接超过 {TapNowConnectionLimit} 条，仅处理前 {TapNowConnectionLimit} 条。" });
        }
        HashSet<string> seenConnections = new(StringComparer.Ordinal);
        int connectionIndex = 0;
        foreach (JsonElement raw in Array(rawConnections))
        {
            if (connectionIndex++ >= TapNowConnectionLimit) break;
            if (Text(raw, "deleted_at").Trim().Length > 0) continue;
            string connectionId = Text(raw, "id").Trim();
            if (connectionId.Length == 0) { skippedConnections.Add(new() { Reason = "连接缺少 ID" }); continue; }
            if (!seenConnections.Add(connectionId)) { skippedConnections.Add(new() { ID = connectionId, Reason = "连接 ID 重复" }); continue; }
            if (!mapping.TryGetValue(Text(raw, "source").Trim(), out string? from) || !mapping.TryGetValue(Text(raw, "target").Trim(), out string? to))
            {
                skippedConnections.Add(new() { ID = connectionId, Reason = "连接端点节点未导入" });
                continue;
            }
            string fromHandle = Text(raw, "sourceHandle").Trim();
            string toHandle = Text(raw, "targetHandle").Trim();
            if (toHandle.Length == 0) toHandle = Param(Property(raw, "data"), "valueKey");
            connections.Add(new() { ID = "tapnow-" + batchId + "-" + connectionId, FromNodeID = from, ToNodeID = to, FromHandleID = EmptyToNull(fromHandle), ToHandleID = EmptyToNull(toHandle) });
        }
        if (nodes.Count == 0) throw new InvalidOperationException("TapNow 画布没有可导入的有效节点");
        return new TapNowImportResultDto
        {
            BatchID = batchId, BatchCreatedAt = now, ShareID = shareId, ProjectName = projectName,
            Nodes = nodes, Connections = connections, ImportedNodeCount = nodes.Count, ImportedConnectionCount = connections.Count,
            SkippedNodes = skippedNodes, SkippedConnections = skippedConnections, Warnings = warnings,
            MultiResultNodeCount = multiResult, ReusedFailedNodeCount = reusedFailed, PlaceholderNodeCount = placeholders,
        };
    }

    private sealed class LibTVEnvelope
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("msg")] public string Msg { get; set; } = "";
        [JsonPropertyName("data")] public JsonElement Data { get; set; }
    }

    private sealed class TapNowEnvelope
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("msg")] public string Msg { get; set; } = "";
        [JsonPropertyName("message")] public string Message { get; set; } = "";
        [JsonPropertyName("data")] public JsonElement Data { get; set; }
    }

    private static LibTVCanvasNodeDto WithLibTVMedia(LibTVCanvasNodeDto node, int? width, int? height, double duration, string mime) => new()
    {
        ID = node.ID, Type = node.Type, Title = node.Title, X = node.X, Y = node.Y, Width = node.Width, Height = node.Height,
        Content = node.Content, Prompt = node.Prompt, Model = node.Model, NaturalWidth = width, NaturalHeight = height,
        DurationMs = Milliseconds(duration), MimeType = mime, Metadata = node.Metadata,
    };

    private static LibTVCanvasNodeDto WithLibTVStatus(LibTVCanvasNodeDto node, string status, string? error) => new()
    {
        ID = node.ID, Type = node.Type, Title = node.Title, X = node.X, Y = node.Y, Width = node.Width, Height = node.Height,
        Content = node.Content, Prompt = node.Prompt, Model = node.Model, NaturalWidth = node.NaturalWidth, NaturalHeight = node.NaturalHeight,
        DurationMs = node.DurationMs, MimeType = node.MimeType, Status = status, ErrorDetails = EmptyToNull(error), Metadata = node.Metadata,
    };

    private static TapNowCanvasNodeDto WithTapNowStatus(TapNowCanvasNodeDto node, string status, string? error) => new()
    {
        ID = node.ID, Type = node.Type, Title = node.Title, X = node.X, Y = node.Y, Width = node.Width, Height = node.Height, Content = node.Content,
        Prompt = node.Prompt, Model = node.Model, Size = node.Size, Quality = node.Quality, Seconds = node.Seconds, VQuality = node.VQuality,
        GenerateAudio = node.GenerateAudio, NaturalWidth = node.NaturalWidth, NaturalHeight = node.NaturalHeight, DurationMs = node.DurationMs,
        MimeType = node.MimeType, Status = status, ErrorDetails = EmptyToNull(error), Metadata = node.Metadata,
    };

    private static (string URL, int Index) FirstURL(JsonElement values, string? requiredHost)
    {
        int index = 0;
        foreach (JsonElement item in Array(values))
        {
            string raw = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
            if (Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 &&
                (requiredHost is null || string.Equals(uri.DnsSafeHost.TrimEnd('.'), requiredHost, StringComparison.OrdinalIgnoreCase)))
            {
                return (uri.AbsoluteUri, index);
            }
            index++;
        }
        return ("", -1);
    }

    private static (string URL, int Index) FirstURLFromTapNow(JsonElement data)
    {
        List<string> values = [Text(data, "src")];
        values.AddRange(Array(Property(data, "options")).Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : ""));
        values.Add(Text(Property(data, "__metadata"), "url"));
        for (int index = 0; index < values.Count; index++)
        {
            JsonElement value = JsonSerializer.SerializeToElement(new[] { values[index] });
            (string URL, _) = FirstURL(value, "files.tapnow.media");
            if (URL.Length > 0) return (URL, index);
        }
        return ("", -1);
    }

    private static string InferMime(string kind, string url)
    {
        string lower = url.ToLowerInvariant();
        if (lower.Contains(".png")) return "image/png";
        if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return "image/jpeg";
        if (lower.Contains(".webp")) return "image/webp";
        return kind == "video" ? "video/mp4" : "image/*";
    }

    private static string InferTapNowMime(string kind, string url, string hint)
    {
        string lower = (url + " " + hint).ToLowerInvariant();
        return kind switch
        {
            "image" when lower.Contains(".png") => "image/png",
            "image" when lower.Contains(".jpg") || lower.Contains(".jpeg") => "image/jpeg",
            "image" when lower.Contains(".webp") => "image/webp",
            "image" when lower.Contains(".gif") => "image/gif",
            "image" => "image/*",
            "video" when lower.Contains(".webm") => "video/webm",
            "video" when lower.Contains(".mov") => "video/quicktime",
            "video" => "video/mp4",
            "audio" when lower.Contains(".wav") => "audio/wav",
            "audio" when lower.Contains(".flac") => "audio/flac",
            "audio" when lower.Contains(".ogg") || lower.Contains(".oga") => "audio/ogg",
            "audio" when lower.Contains(".m4a") => "audio/mp4",
            "audio" => "audio/mpeg",
            "text" => "text/plain",
            _ => "application/octet-stream",
        };
    }

    private static string ImageQuality(JsonElement values)
    {
        string value = FirstParam(values, "quality", "imageSize").ToLowerInvariant();
        return value is "1k" or "2k" or "4k" or "auto" or "low" or "medium" or "high" ? value : "";
    }

    private static string FirstParam(JsonElement values, params string[] keys)
    {
        foreach (string key in keys) { string value = Param(values, key); if (value.Length > 0) return value; }
        return "";
    }

    private static string Param(JsonElement values, string key)
    {
        JsonElement value = Property(values, key);
        return value.ValueKind switch
        {
            JsonValueKind.String => (value.GetString() ?? "").Trim(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    private static bool TryNodeData(JsonElement raw, out JsonElement data)
    {
        data = Property(raw, "data");
        if (data.ValueKind == JsonValueKind.String) return TryParseObject(data.GetString() ?? "", out data);
        return data.ValueKind == JsonValueKind.Object;
    }

    private static bool TryParseObject(string raw, out JsonElement result)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            result = document.RootElement.Clone();
            return result.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { result = default; return false; }
    }

    private static bool TryStringDouble(JsonElement element, string key, out double value) => double.TryParse(Text(element, key).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private static bool TryDouble(JsonElement element, string key, out double value) => Property(element, key).TryGetDoubleSafe(out value);
    private static int PositiveInt(JsonElement element, string key) => Number(Property(element, key)) > 0 ? Number(Property(element, key)) : 0;
    private static double PositiveDouble(JsonElement element, string key) => TryDoubleValue(Property(element, key), out double value) && value > 0 ? value : 0;
    private static int Number(JsonElement element) => element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int value) ? value : 0;
    private static long Int64(JsonElement element, string key) => Property(element, key).ValueKind == JsonValueKind.Number && Property(element, key).TryGetInt64(out long value) ? value : 0;
    private static bool Bool(JsonElement element, string key) => Property(element, key).ValueKind == JsonValueKind.True;
    private static bool TryDoubleValue(JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value) && double.IsFinite(value);
    }
    private static long? Milliseconds(double value) => value > 0 && double.IsFinite(value) ? (long)Math.Round(value * 1000, MidpointRounding.AwayFromZero) : null;

    private static JsonElement Property(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out JsonElement value) ? value : default;
    private static string Text(JsonElement element, string key) => Property(element, key).ValueKind == JsonValueKind.String ? Property(element, key).GetString() ?? "" : "";
    private static IEnumerable<JsonElement> Array(JsonElement element) => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;
    private static bool IsFailed(string value) => value is "failed" or "error" or "failure";
    private static bool IsLibTVUUID(string value) => value.Length == 32 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    private static bool IsTapNowShareId(string value) => value.Length is > 0 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private static string NewBatchId(string prefix) => prefix + ToBase36(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    private static string ToBase36(long value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char> buffer = stackalloc char[16]; int index = buffer.Length;
        while (value > 0) { buffer[--index] = digits[(int)(value % 36)]; value /= 36; }
        return new string(buffer[index..]);
    }
    private static string TruncateUtf8(string value, int maxBytes)
    {
        int bytes = 0, chars = 0;
        foreach (Rune rune in value.EnumerateRunes()) { if (bytes + rune.Utf8SequenceLength > maxBytes) break; bytes += rune.Utf8SequenceLength; chars += rune.Utf16SequenceLength; }
        return value[..chars];
    }
}

internal static class CanvasImportJsonElementExtensions
{
    public static int GetArrayLengthSafe(this JsonElement element) => element.ValueKind == JsonValueKind.Array ? element.GetArrayLength() : 0;
    public static bool TryGetDoubleSafe(this JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value) && double.IsFinite(value);
    }
}
