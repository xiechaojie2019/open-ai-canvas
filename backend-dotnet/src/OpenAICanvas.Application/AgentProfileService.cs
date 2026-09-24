#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>Agent 偏好档案请求体。对应 Go: <c>app.AgentProfileRequest</c>。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AgentProfileRequest
{
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";

    [JsonPropertyName("projectId")]
    public string ProjectID { get; set; } = "";

    [JsonPropertyName("canvasId")]
    public string CanvasID { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("revision")]
    public long Revision { get; set; }
}

/// <summary>Agent 偏好档案层。对应 Go: <c>app.AgentProfileLayer</c>。</summary>
public sealed class AgentProfileLayerDto
{
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";

    [JsonPropertyName("projectId")]
    [GoOmitEmpty]
    public string ProjectID { get; set; } = "";

    [JsonPropertyName("canvasId")]
    [GoOmitEmpty]
    public string CanvasID { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";
}

/// <summary>Agent 偏好合并视图。对应 Go: <c>app.AgentProfileView</c>。</summary>
public sealed class AgentProfileViewDto
{
    [JsonPropertyName("revision")]
    public string Revision { get; set; } = "";

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("layers")]
    public IReadOnlyList<AgentProfileLayerDto> Layers { get; set; } = [];
}

/// <summary>
/// 云 Agent 偏好档案服务（阶段 11.9 首批）。
/// 对应 Go: <c>app/cloud_agent_profile.go</c>。运行时执行引擎（11.1-11.8）尚未移植，
/// 契约由 AgentCapabilitiesEndpoints 显式声明 501。
/// </summary>
public sealed class AgentProfileService
{
    /// <summary>对应 Go: <c>cloudAgentProfileMaxRunes</c>。</summary>
    private const int MaxRunes = 12000;

    private readonly Repository _repository;

    public AgentProfileService(Repository repository) => _repository = repository;

    public async Task<AgentProfileViewDto> ProfileForScopeAsync(
        string userID,
        string projectID,
        string canvasID,
        CancellationToken cancellationToken)
    {
        if (userID.Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        string effectiveProjectID = projectID;
        if (projectID.Length > 0)
        {
            if (await _repository.ProjectForUserAsync(userID, projectID, cancellationToken).ConfigureAwait(false) is null)
            {
                throw AppError.NotFound("项目不存在");
            }
        }
        if (canvasID.Length > 0)
        {
            CanvasProject? canvas = await _repository.CanvasProjectForUserAsync(userID, canvasID, cancellationToken).ConfigureAwait(false);
            if (canvas is null)
            {
                throw AppError.NotFound("画布不存在");
            }
            if (projectID.Length > 0 && canvas.ProjectID != projectID)
            {
                throw AppError.New(400, "画布不属于指定项目");
            }
            effectiveProjectID = canvas.ProjectID;
        }

        List<AgentProfileLayerDto> layers = [];
        AgentProfile? userLayer = await _repository.AgentProfileForScopeAsync(
            userID, "user", "", "", cancellationToken).ConfigureAwait(false);
        if (userLayer is not null)
        {
            layers.Add(ToLayer(userLayer, "user"));
        }
        if (effectiveProjectID.Length > 0)
        {
            AgentProfile? projectLayer = await _repository.AgentProfileForScopeAsync(
                userID, "project", effectiveProjectID, "", cancellationToken).ConfigureAwait(false);
            if (projectLayer is not null)
            {
                layers.Add(ToLayer(projectLayer, "project"));
            }
        }
        if (canvasID.Length > 0)
        {
            AgentProfile? canvasLayer = await _repository.AgentProfileForScopeAsync(
                userID, "canvas", effectiveProjectID, canvasID, cancellationToken).ConfigureAwait(false);
            if (canvasLayer is not null)
            {
                layers.Add(ToLayer(canvasLayer, "canvas"));
            }
        }
        return new AgentProfileViewDto
        {
            Revision = ProfileRevision(layers),
            Hash = ProfileHash(ProfileText(layers)),
            Layers = layers,
        };
    }

    public async Task<AgentProfileViewDto> UpdateAsync(
        string userID,
        AgentProfileRequest request,
        CancellationToken cancellationToken)
    {
        if (userID.Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        ValidateScope(request);
        string projectID = request.ProjectID;
        string canvasID = request.CanvasID;
        if (request.Scope == "project")
        {
            if (await _repository.ProjectForUserAsync(userID, projectID, cancellationToken).ConfigureAwait(false) is null)
            {
                throw AppError.NotFound("项目不存在");
            }
        }
        if (request.Scope == "canvas")
        {
            CanvasProject? canvas = await _repository.CanvasProjectForUserAsync(userID, canvasID, cancellationToken).ConfigureAwait(false);
            if (canvas is null)
            {
                throw AppError.NotFound("画布不存在");
            }
            if (projectID.Length > 0 && canvas.ProjectID != projectID)
            {
                throw AppError.New(400, "画布不属于指定项目");
            }
            projectID = canvas.ProjectID;
        }
        if (request.Scope != "canvas")
        {
            canvasID = "";
        }
        string content = request.Content.Trim();
        AgentProfile profile = new()
        {
            ID = IdGenerator.NewId(),
            UserID = userID,
            Scope = request.Scope,
            ProjectID = projectID,
            CanvasID = canvasID,
            Content = content,
            Revision = 1,
            Hash = ProfileHash(content),
            UpdatedAt = DateTime.UtcNow,
        };
        AgentProfile? saved = await _repository.SaveAgentProfileAsync(profile, request.Revision, cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            throw AppError.New(409, "Agent 偏好已变化，请重新读取后保存");
        }
        return await ProfileForScopeAsync(userID, projectID, canvasID, cancellationToken).ConfigureAwait(false);
    }

    private static AgentProfileLayerDto ToLayer(AgentProfile profile, string scope) => new()
    {
        Scope = scope,
        ProjectID = profile.ProjectID,
        CanvasID = profile.CanvasID,
        Content = profile.Content,
        Revision = profile.Revision,
        Hash = profile.Hash,
    };

    /// <summary>合并文档。对应 Go: <c>agentProfileText</c>。</summary>
    private static string ProfileText(IReadOnlyList<AgentProfileLayerDto> layers)
    {
        StringBuilder text = new();
        foreach (AgentProfileLayerDto layer in layers)
        {
            if (string.IsNullOrWhiteSpace(layer.Content))
            {
                continue;
            }
            text.Append("\n## ").Append(layer.Scope).Append(" profile\n").Append(layer.Content).Append('\n');
        }
        return text.ToString().Trim();
    }

    private static string ProfileHash(string content)
    {
        byte[] sum = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(sum).ToLowerInvariant();
    }

    /// <summary>层级指纹。对应 Go: <c>agentProfileRevision</c>。</summary>
    private static string ProfileRevision(IReadOnlyList<AgentProfileLayerDto> layers)
    {
        StringBuilder text = new();
        foreach (AgentProfileLayerDto layer in layers)
        {
            text.Append(layer.Scope).Append('\0').Append(layer.ProjectID).Append('\0').Append(layer.CanvasID).Append('\0').Append(layer.Hash).Append('\0');
        }
        return ProfileHash(text.ToString());
    }

    /// <summary>作用域校验。对应 Go: <c>validateAgentProfileScope</c> 与内容校验。</summary>
    private static void ValidateScope(AgentProfileRequest request)
    {
        if (request.Scope is not ("user" or "project" or "canvas"))
        {
            throw AppError.BadAuthRequest("无效的 Agent 偏好作用域");
        }
        if (request.Scope == "user" && (request.ProjectID.Length > 0 || request.CanvasID.Length > 0))
        {
            throw AppError.BadAuthRequest("用户偏好不能带项目或画布 ID");
        }
        if (request.Scope == "project" && (request.ProjectID.Length == 0 || request.CanvasID.Length > 0))
        {
            throw AppError.BadAuthRequest("项目偏好需要 projectId，且不能带 canvasId");
        }
        if (request.Scope == "canvas" && request.CanvasID.Length == 0)
        {
            throw AppError.BadAuthRequest("画布偏好缺少 canvasId");
        }
        if (request.Revision < 0)
        {
            throw AppError.BadAuthRequest("偏好 revision 无效");
        }
        if (request.Content.Length > MaxRunes)
        {
            throw AppError.BadAuthRequest("Agent 偏好文档必须是有效 UTF-8，且不超过 12000 个字符");
        }
        if (request.Content.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            throw AppError.BadAuthRequest("Agent 偏好文档不能包含控制字符");
        }
        ValidateCloudAgentID(request.ProjectID, "项目 ID");
        ValidateCloudAgentID(request.CanvasID, "画布 ID");
    }

    /// <summary>对象 ID 校验。对应 Go: <c>validateCloudAgentID</c>。</summary>
    private static void ValidateCloudAgentID(string value, string label)
    {
        if (value.Length == 0)
        {
            return;
        }
        if (value.Trim() != value || value.Any(char.IsControl) || value.Length > 80)
        {
            throw AppError.BadAuthRequest(label + "不能包含首尾空白、控制字符，且不超过 80 个字符");
        }
    }
}
