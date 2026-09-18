#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Auth;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>分享状态。对应 Go: <c>canvas.CanvasShareStatus</c>（字段顺序即输出顺序）。</summary>
public sealed class CanvasShareStatusDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Token { get; init; } = "";

    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ExpiresAt { get; init; }

    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedAt { get; init; }
}

/// <summary>公开分享投影。对应 Go: <c>canvas.PublicCanvasShare</c>。</summary>
public sealed class PublicCanvasShareDto
{
    [JsonPropertyName("project")]
    public JsonElement Project { get; init; }

    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>
/// 画布分享域服务：令牌签发/轮换、公开投影脱敏、公开资源放行。
/// 对应 Go: <c>internal/canvas/canvas_share.go</c>。
/// </summary>
/// <remarks>
/// 令牌 32 字节 base64url；仅存 SHA-256 哈希，明文经设置加密通道（当前占位）回显给创建者。
/// 公开投影剥离禁用键（apiKey/storageKey/task* 等）并把 media 节点 content
/// 重写为带令牌的资源代理 URL。
/// </remarks>
public sealed class CanvasShareService
{
    /// <summary>公开 metadata 白名单。对应 Go: <c>publicCanvasMetadataKeys</c>。</summary>
    private static readonly HashSet<string> PublicMetadataKeys = new(StringComparer.Ordinal)
    {
        "content", "composerContent", "prompt", "status", "fontSize",
        "generationMode", "generationType", "model", "size", "quality", "transparentBackground",
        "count", "seconds", "vquality", "generateAudio", "watermark",
        "audioVoice", "audioFormat", "audioSpeed", "audioInstructions",
        "naturalWidth", "naturalHeight", "freeResize", "isBatchRoot",
        "batchRootId", "batchChildIds", "batchUsesReferenceImages", "primaryImageId",
        "imageBatchExpanded", "mimeType", "bytes", "durationMs", "hasAudio", "assetTags",
        "workflowKind", "workflowTitle", "workflowDescription", "shotIndex",
        "sceneId", "characterIds", "referenceSetId", "referenceAssetNodeIds", "assetBindings",
        "characterName", "characterPrompt", "characterAliases", "characterView", "characterViewNodeIds",
        "videoEditOperation", "videoCameraMoveId", "videoCameraMovePrompt",
        "videoStartFrameNodeId", "videoEndFrameNodeId", "versionOfNodeId",
        "versionLabel", "versionPrimary", "directorSceneId", "directorShotId",
        "directorPreviewNodeId", "directorDepthNodeId", "directorNormalNodeId",
        "skillId", "skillVersion", "skillSnapshot", "storyboard",
        "storyboardShotDuration", "storyboardShotCount", "storyboardComposerHeight", "frame",
    };

    /// <summary>公开投影禁用键。对应 Go: <c>publicCanvasForbiddenKeys</c>。</summary>
    private static readonly HashSet<string> PublicForbiddenKeys = new(StringComparer.Ordinal)
    {
        "apiKey", "storageKey", "taskId", "taskStatus", "taskProgress",
        "taskStage", "taskCreatedAt", "taskUpdatedAt",
        "errorDetails", "references",
    };

    private readonly Repository _repository;

    public CanvasShareService(Repository repository)
    {
        _repository = repository;
    }

    /// <summary>分享状态。对应 Go: <c>CanvasShareStatus</c>。</summary>
    public async Task<CanvasShareStatusDto> StatusAsync(
        string userId, string projectId, CancellationToken cancellationToken = default)
    {
        if (await _repository.CanvasProjectForUserAsync(userId, projectId, cancellationToken).ConfigureAwait(false)
            is null)
        {
            throw new InvalidOperationException("record not found");
        }
        CanvasShare? share = await _repository.CanvasShareForProjectAsync(userId, projectId, cancellationToken)
            .ConfigureAwait(false);
        if (share is null)
        {
            return new CanvasShareStatusDto { Enabled = false };
        }
        return ShareStatus(share);
    }

    /// <summary>创建/轮换分享。对应 Go: <c>CreateCanvasShare</c>。</summary>
    public async Task<CanvasShareStatusDto> CreateAsync(
        string userId,
        string projectId,
        int expiresDays,
        bool rotate,
        CancellationToken cancellationToken = default)
    {
        if (expiresDays < 0 || expiresDays > 365)
        {
            throw AppError.BadAuthRequest("分享有效期必须在 0 到 365 天之间");
        }
        if (await _repository.CanvasProjectForUserAsync(userId, projectId, cancellationToken).ConfigureAwait(false)
            is null)
        {
            throw new InvalidOperationException("record not found");
        }

        CanvasShare? share = await _repository.CanvasShareForProjectAsync(userId, projectId, cancellationToken)
            .ConfigureAwait(false);
        if (share is null)
        {
            share = new CanvasShare { ID = IdGenerator.NewId(), UserID = userId, ProjectID = projectId };
        }
        if (rotate || share.TokenCipher.Trim().Length == 0)
        {
            string token = NewShareToken();
            share.TokenHash = TokenHash(token);
            share.TokenCipher = AuthService.EncryptSecret(token);
        }
        share.Enabled = true;
        share.ExpiresAt = null;
        if (expiresDays > 0)
        {
            share.ExpiresAt = DateTime.UtcNow.AddDays(expiresDays);
        }
        await _repository.SaveCanvasShareAsync(share, cancellationToken).ConfigureAwait(false);
        return ShareStatus(share);
    }

    /// <summary>撤销分享。对应 Go: <c>DeleteCanvasShare</c>。</summary>
    public Task DeleteAsync(
        string userId, string projectId, CancellationToken cancellationToken = default)
    {
        if (_repository.CanvasProjectForUserAsync(userId, projectId, cancellationToken).GetAwaiter().GetResult()
            is null)
        {
            throw new InvalidOperationException("record not found");
        }
        return _repository.DeleteCanvasShareByProjectAsync(userId, projectId, cancellationToken);
    }

    /// <summary>公开分享投影。对应 Go: <c>PublicCanvasShare</c>。</summary>
    public async Task<PublicCanvasShareDto> PublicAsync(
        string token, CancellationToken cancellationToken = default)
    {
        (CanvasShare share, CanvasProject project) = await SharedProjectAsync(token, cancellationToken)
            .ConfigureAwait(false);
        JsonElement publicProject = PublicProjectPayload(
            JsonSerializer.Deserialize<JsonElement>(project.PayloadJSON), token, out _);
        return new PublicCanvasShareDto { Project = publicProject, ExpiresAt = share.ExpiresAt };
    }

    /// <summary>
    /// 公开分享资源投递：校验令牌与节点放行清单，返回资源与本地文件流。
    /// 对应 Go: <c>PrepareSharedCanvasResourceDelivery</c>（本地 provider；
    /// 云 provider 重定向/流式转发属云存储节点，见待确认 #25）。
    /// </summary>
    public async Task<(Resource Resource, string LocalPath)> PrepareSharedResourceDeliveryAsync(
        string token, string resourceId, string dataDir, CancellationToken cancellationToken = default)
    {
        (CanvasShare share, CanvasProject project) = await SharedProjectAsync(token, cancellationToken)
            .ConfigureAwait(false);
        JsonElement payloadElement = project.PayloadJSON.Length == 0
            ? throw AppError.New(500, "分享画布数据格式无效")
            : JsonSerializer.Deserialize<JsonElement>(project.PayloadJSON);
        PublicProjectPayload(payloadElement, token, out HashSet<string> allowed);

        if (!allowed.Contains(resourceId))
        {
            throw new InvalidOperationException("record not found");
        }
        Resource? resource = await _repository.ResourceForUserAsync(share.UserID, resourceId, cancellationToken)
            .ConfigureAwait(false);
        if (resource is null)
        {
            throw new InvalidOperationException("record not found");
        }
        if (resource.Provider.Trim() is not ("" or "local"))
        {
            // 云 provider 资源需签名重定向；当前仅支持本地投递。
            throw new InvalidOperationException("cloud delivery unsupported");
        }
        string objectKey = resource.ObjectKey.TrimStart('/', '\\');
        string root = Path.GetFullPath(Path.Combine(dataDir, "resources"));
        string path = Path.GetFullPath(Path.Combine(root, objectKey));
        if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path))
        {
            throw new InvalidOperationException("record not found");
        }
        return (resource, path);
    }

    // ------------------------------------------------------------ 内部

    private async Task<(CanvasShare Share, CanvasProject Project)> SharedProjectAsync(
        string token, CancellationToken cancellationToken)
    {
        token = token.Trim();
        if (token.Length < 32 || token.Length > 128)
        {
            throw new InvalidOperationException("record not found");
        }
        CanvasShare? share = await _repository.CanvasShareByTokenHashAsync(TokenHash(token), cancellationToken)
            .ConfigureAwait(false);
        if (share is null || (share.ExpiresAt is not null && share.ExpiresAt <= DateTime.UtcNow))
        {
            throw new InvalidOperationException("record not found");
        }
        CanvasProject? project = await _repository.CanvasProjectForUserAsync(
            share.UserID, share.ProjectID, cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            throw new InvalidOperationException("record not found");
        }
        return (share, project);
    }

    private static CanvasShareStatusDto ShareStatus(CanvasShare share)
    {
        if (!share.Enabled || (share.ExpiresAt is not null && share.ExpiresAt <= DateTime.UtcNow))
        {
            return new CanvasShareStatusDto { Enabled = false };
        }
        return new CanvasShareStatusDto
        {
            Enabled = true,
            Token = AuthService.DecryptSecret(share.TokenCipher),
            ExpiresAt = share.ExpiresAt,
            CreatedAt = share.CreatedAt,
        };
    }

    private static string NewShareToken()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>对应 Go: <c>canvasShareTokenHash</c>（SHA-256 hex）。</summary>
    private static string TokenHash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()))).ToLowerInvariant();

    /// <summary>对应 Go: <c>publicCanvasProject</c>。返回脱敏投影与放行资源清单。</summary>
    private static JsonElement PublicProjectPayload(
        JsonElement source, string token, out HashSet<string> allowedResources)
    {
        allowedResources = new HashSet<string>(StringComparer.Ordinal);
        if (source.ValueKind != JsonValueKind.Object)
        {
            throw AppError.New(500, "分享画布数据格式无效");
        }

        Dictionary<string, object?> result = new(StringComparer.Ordinal)
        {
            ["id"] = SourceValue(source, "id"),
            ["title"] = DefaultString(SourceValue(source, "title"), ""),
            ["createdAt"] = SourceValue(source, "createdAt"),
            ["updatedAt"] = SourceValue(source, "updatedAt"),
            ["backgroundMode"] = SourceValue(source, "backgroundMode"),
            ["showImageInfo"] = SourceValue(source, "showImageInfo"),
            ["viewport"] = Scrub(SourceValue(source, "viewport")),
            ["connections"] = PublicConnections(SourceValue(source, "connections")),
            ["chatSessions"] = Array.Empty<object>(),
            ["activeChatId"] = null,
            ["directorScenes"] = Array.Empty<object>(),
        };

        List<object?> nodes = [];
        if (SourceValue(source, "nodes") is JsonElement nodesElement && nodesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement node in nodesElement.EnumerateArray())
            {
                Dictionary<string, object?>? publicNode = PublicNode(node, token, allowedResources);
                if (publicNode is not null)
                {
                    nodes.Add(publicNode);
                }
            }
        }
        result["nodes"] = nodes;
        return JsonSerializer.SerializeToElement(result);
    }

    private static JsonElement? SourceValue(JsonElement source, string key) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(key, out JsonElement value)
            ? value
            : null;

    private static object? DefaultString(JsonElement? value, string fallback)
    {
        string text = value?.ValueKind == JsonValueKind.String ? value.Value.GetString() ?? "" : "";
        return text.Length > 0 ? text : fallback;
    }

    private static List<object?> PublicConnections(JsonElement? value)
    {
        List<object?> result = [];
        if (value is null || value.Value.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        foreach (JsonElement raw in value.Value.EnumerateArray())
        {
            if (raw.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            Dictionary<string, object?> connection = new(StringComparer.Ordinal)
            {
                ["id"] = SourceValue(raw, "id"),
                ["fromNodeId"] = SourceValue(raw, "fromNodeId"),
                ["toNodeId"] = SourceValue(raw, "toNodeId"),
                ["fromHandleId"] = SourceValue(raw, "fromHandleId"),
                ["toHandleId"] = SourceValue(raw, "toHandleId"),
            };
            result.Add(connection);
        }
        return result;
    }

    /// <summary>对应 Go: <c>publicCanvasNode</c>。白名单 metadata + 媒体 content 重写为代理 URL。</summary>
    private static Dictionary<string, object?>? PublicNode(
        JsonElement node, string token, HashSet<string> allowedResources)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        string id = NodeText(node, "id");
        string type = NodeText(node, "type");
        if (id.Length == 0 || type.Length == 0)
        {
            return null;
        }

        Dictionary<string, object?> result = new(StringComparer.Ordinal)
        {
            ["id"] = SourceValue(node, "id"),
            ["type"] = SourceValue(node, "type"),
            ["title"] = SourceValue(node, "title"),
            ["position"] = Scrub(SourceValue(node, "position")),
            ["width"] = SourceValue(node, "width"),
            ["height"] = SourceValue(node, "height"),
            ["parentId"] = SourceValue(node, "parentId"),
        };

        Dictionary<string, object?> publicMetadata = new(StringComparer.Ordinal);
        JsonElement? metadata = SourceValue(node, "metadata");
        if (metadata is { ValueKind: JsonValueKind.Object })
        {
            foreach (JsonProperty property in metadata.Value.EnumerateObject())
            {
                if (PublicMetadataKeys.Contains(property.Name))
                {
                    publicMetadata[property.Name] = Scrub(property.Value);
                }
            }
            string storageKey = NodeText(metadata.Value, "storageKey");
            string content = NodeText(metadata.Value, "content");
            string resourceID = ResourceIDOf(storageKey);
            if (resourceID.Length == 0)
            {
                resourceID = ResourceIDOf(content);
            }
            if (resourceID.Length > 0)
            {
                allowedResources.Add(resourceID);
                publicMetadata["content"] = SharedResourceURL(token, resourceID);
            }
            else if (type is "image" or "video" or "audio")
            {
                publicMetadata.Remove("content");
            }
        }
        publicMetadata.Remove("storageKey");
        result["metadata"] = publicMetadata;
        return result;
    }

    private static string NodeText(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static string ResourceIDOf(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith("resource:", StringComparison.Ordinal))
        {
            return ValidID(trimmed["resource:".Length..]);
        }
        return ValidID(IDFromFileURL(trimmed));
    }

    private static string ValidID(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 80)
        {
            return "";
        }
        foreach (char ch in trimmed)
        {
            if (!(ch >= 'a' && ch <= 'z') && !(ch >= 'A' && ch <= 'Z') &&
                !(ch >= '0' && ch <= '9') && ch != '-' && ch != '_')
            {
                return "";
            }
        }
        return trimmed;
    }

    private static string IDFromFileURL(string value)
    {
        const string prefix = "/api/resources/";
        string trimmed = value.Trim();
        int index = trimmed.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0)
        {
            return "";
        }
        string remainder = trimmed[(index + prefix.Length)..];
        if (remainder.Length == 0)
        {
            return "";
        }
        int end = remainder.IndexOfAny(['/', '?', '#']);
        if (end >= 0)
        {
            remainder = remainder[..end];
        }
        return remainder;
    }

    /// <summary>对应 Go: <c>scrubPublicCanvasValue</c>。递归剥离禁用键。</summary>
    private static object? Scrub(JsonElement? valueElement)
    {
        if (valueElement is null)
        {
            return null;
        }
        JsonElement value = valueElement.Value;
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                Dictionary<string, object?> result = new(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (PublicForbiddenKeys.Contains(property.Name))
                    {
                        continue;
                    }
                    result[property.Name] = Scrub(property.Value);
                }
                return result;
            }
            case JsonValueKind.Array:
            {
                List<object?> result = [];
                foreach (JsonElement child in value.EnumerateArray())
                {
                    result.Add(Scrub(child));
                }
                return result;
            }
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Number:
                return value.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    /// <summary>对应 Go: <c>sharedCanvasResourceURL</c>。</summary>
    private static string SharedResourceURL(string token, string resourceID) =>
        "/api/public/canvas-shares/" + token + "/resources/" + resourceID + "/file";
}
