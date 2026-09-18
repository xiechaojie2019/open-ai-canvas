#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>项目摘要。对应 Go: <c>app.ProjectSummary</c>（Project 实体在前，字段顺序即输出顺序）。</summary>
public sealed class ProjectSummaryDto
{
    [JsonPropertyName("project")]
    public Project Project { get; init; } = new();

    [JsonPropertyName("canvasCount")]
    public int CanvasCount { get; init; }

    [JsonPropertyName("assetCount")]
    public long AssetCount { get; init; }

    [JsonPropertyName("unitCount")]
    public int UnitCount { get; init; }

    [JsonPropertyName("completedUnitCount")]
    public int CompletedUnitCount { get; init; }
}

/// <summary>项目分页。对应 Go: <c>app.ProjectListPage</c>。</summary>
public sealed class ProjectListPageDto
{
    [JsonPropertyName("projects")]
    public IReadOnlyList<ProjectSummaryDto> Projects { get; init; } = [];

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}

/// <summary>项目创建请求。对应 Go: <c>app.CreateProjectRequest</c>。</summary>
public sealed class CreateProjectRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("aspectRatio")]
    public string AspectRatio { get; set; } = "";

    [JsonPropertyName("sourceType")]
    public string SourceType { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("stylePresetId")]
    public string StylePresetID { get; set; } = "";

    [JsonPropertyName("styleProfileJson")]
    public string StyleProfileJSON { get; set; } = "";

    [JsonPropertyName("defaultImageModel")]
    public string DefaultImageModel { get; set; } = "";

    [JsonPropertyName("defaultVideoModel")]
    public string DefaultVideoModel { get; set; } = "";
}

/// <summary>项目更新请求。对应 Go: <c>app.UpdateProjectRequest</c>（指针字段区分未提交）。</summary>
public sealed class UpdateProjectRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("aspectRatio")]
    public string AspectRatio { get; set; } = "";

    [JsonPropertyName("sourceType")]
    public string SourceType { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("coverResourceId")]
    public string? CoverResourceID { get; set; }

    [JsonPropertyName("stylePresetId")]
    public string? StylePresetID { get; set; }

    [JsonPropertyName("styleProfileJson")]
    public string? StyleProfileJSON { get; set; }

    [JsonPropertyName("defaultImageModel")]
    public string? DefaultImageModel { get; set; }

    [JsonPropertyName("defaultVideoModel")]
    public string? DefaultVideoModel { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}

/// <summary>
/// 项目域服务：CRUD 与摘要聚合。
/// 对应 Go: <c>app/project.go</c> 的 ListProjects / CreateProject / UpdateProject / DeleteProject。
/// </summary>
/// <remarks>
/// Go 的 ProjectDetail（工作台全量聚合）、内置工作流模板初始化
/// （EnsureBuiltinProjectWorkflowTemplate / createProjectWorkflow）与 reconcile 修复链路
/// 属阶段 6 后续节点；创建项目暂不生成默认工作流（差异记入待确认清单 #27）。
/// </remarks>
public sealed class ProjectService
{
    private readonly Repository _repository;

    public ProjectService(Repository repository)
    {
        _repository = repository;
    }

    /// <summary>项目列表（摘要聚合）。对应 Go: <c>ListProjects</c>。</summary>
    public async Task<IReadOnlyList<ProjectSummaryDto>> ListProjectsAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Project> projects = await _repository.ProjectsAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        return await SummarizeProjectsAsync(userId, projects, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>项目分页。对应 Go: <c>ListProjectsPage</c>。</summary>
    public async Task<ProjectListPageDto> ListProjectsPageAsync(
        string userId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        (IReadOnlyList<Project> projects, long total) = await _repository.ProjectsPageAsync(
            userId, page, pageSize, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectSummaryDto> summaries = await SummarizeProjectsAsync(
            userId, projects, cancellationToken).ConfigureAwait(false);
        return new ProjectListPageDto
        {
            Projects = summaries,
            Page = page,
            PageSize = pageSize,
            Total = total,
            HasMore = (long)page * pageSize < total,
        };
    }

    /// <summary>创建项目。对应 Go: <c>CreateProject</c>（默认工作流初始化待接）。</summary>
    public async Task<Project> CreateProjectAsync(
        string userId, CreateProjectRequest request, CancellationToken cancellationToken = default)
    {
        string name = request.Name.Trim();
        if (name.Length == 0)
        {
            throw AppError.BadAuthRequest("项目名称不能为空");
        }
        string projectType = request.Type.Trim();
        if (projectType.Length == 0)
        {
            projectType = "short-drama";
        }
        string aspectRatio = request.AspectRatio.Trim();
        if (aspectRatio.Length == 0)
        {
            aspectRatio = "9:16";
        }
        string sourceType = request.SourceType.Trim();
        if (sourceType.Length == 0)
        {
            sourceType = "blank";
        }
        string styleProfileJSON = ValidateStyleProfileJSON(request.StyleProfileJSON);
        string stylePresetID = request.StylePresetID.Trim();
        ValidateStyleProfilePreset(stylePresetID, styleProfileJSON);
        string defaultImageModel = NormalizeDefaultModel(request.DefaultImageModel);
        string defaultVideoModel = NormalizeDefaultModel(request.DefaultVideoModel);

        DateTime now = DateTime.UtcNow;
        var project = new Project
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            Name = name,
            Type = projectType,
            AspectRatio = aspectRatio,
            SourceType = sourceType,
            Description = request.Description.Trim(),
            StylePresetID = stylePresetID,
            StyleProfileJSON = styleProfileJSON,
            DefaultImageModel = defaultImageModel,
            DefaultVideoModel = defaultVideoModel,
            Status = ProjectStatus.ProjectStatusActive,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _repository.CreateProjectAsync(project, cancellationToken).ConfigureAwait(false);
        // Go 会创建默认工作流并递增 revision；工作流引擎属阶段 6 后续节点。
        project.Revision++;
        project.UpdatedAt = DateTime.UtcNow;
        return project;
    }

    /// <summary>更新项目（局部提交语义）。对应 Go: <c>UpdateProject</c>。</summary>
    public async Task<Project> UpdateProjectAsync(
        string userId, string id, UpdateProjectRequest request, CancellationToken cancellationToken = default)
    {
        Project? project = await _repository.ProjectForUserAsync(userId, id, cancellationToken)
            .ConfigureAwait(false);
        if (project is null)
        {
            throw new InvalidOperationException("record not found");
        }

        if (request.Name.Trim().Length > 0)
        {
            project.Name = request.Name.Trim();
        }
        if (request.Type.Trim().Length > 0)
        {
            project.Type = request.Type.Trim();
        }
        if (request.AspectRatio.Trim().Length > 0)
        {
            project.AspectRatio = request.AspectRatio.Trim();
        }
        if (request.SourceType.Trim().Length > 0)
        {
            project.SourceType = request.SourceType.Trim();
        }
        if (request.Description is not null)
        {
            project.Description = request.Description.Trim();
        }
        if (request.CoverResourceID is not null)
        {
            string coverResourceID = request.CoverResourceID.Trim();
            if (coverResourceID.Length > 0)
            {
                Resource? resource = await _repository.ResourceForUserAsync(userId, coverResourceID, cancellationToken)
                    .ConfigureAwait(false);
                if (resource is null)
                {
                    throw AppError.BadAuthRequest("项目主图不存在或不属于当前用户");
                }
                if (resource.Status != ResourceStatus.ResourceStatusReady || resource.Kind != "image")
                {
                    throw AppError.BadAuthRequest("项目主图必须是已就绪的图片资源");
                }
            }
            project.CoverResourceID = coverResourceID;
        }
        if (request.StylePresetID is not null)
        {
            project.StylePresetID = request.StylePresetID.Trim();
        }
        if (request.StyleProfileJSON is not null)
        {
            project.StyleProfileJSON = ValidateStyleProfileJSON(request.StyleProfileJSON);
        }
        if (request.DefaultImageModel is not null)
        {
            project.DefaultImageModel = NormalizeDefaultModel(request.DefaultImageModel);
        }
        if (request.DefaultVideoModel is not null)
        {
            project.DefaultVideoModel = NormalizeDefaultModel(request.DefaultVideoModel);
        }
        ValidateStyleProfilePreset(project.StylePresetID, project.StyleProfileJSON);
        project.Revision++;
        project.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateProjectAsync(project, cancellationToken).ConfigureAwait(false);
        return project;
    }

    /// <summary>删除项目。对应 Go: <c>DeleteProject</c>（解绑画布、清理单元/链接/分享、任务解绑）。</summary>
    public async Task DeleteProjectAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        if (await _repository.ProjectForUserAsync(userId, id, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }
        IReadOnlyList<CanvasProject> canvases = await _repository.ProjectCanvasDocumentsAsync(
            userId, id, cancellationToken).ConfigureAwait(false);
        DateTime deleteTime = DateTime.UtcNow;
        List<CanvasProject> canvasUpdates = [];
        foreach (CanvasProject canvas in canvases)
        {
            canvasUpdates.Add(new CanvasProject
            {
                ID = canvas.ID,
                UserID = canvas.UserID,
                ProjectID = "",
                Title = canvas.Title,
                PayloadJSON = CanvasPayloadWithoutProject(canvas.PayloadJSON, deleteTime),
                CreatedAt = canvas.CreatedAt,
                UpdatedAt = deleteTime,
            });
        }
        await _repository.DeleteProjectAsync(userId, id, canvasUpdates, deleteTime, cancellationToken)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 摘要聚合

    private async Task<IReadOnlyList<ProjectSummaryDto>> SummarizeProjectsAsync(
        string userId, IReadOnlyList<Project> projects, CancellationToken cancellationToken)
    {
        List<ProjectSummaryDto> result = [];
        foreach (Project project in projects)
        {
            IReadOnlyList<ProjectUnit> units = await _repository.ProjectUnitSummariesAsync(
                project.ID, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<CanvasProject> canvases = await _repository.ProjectCanvasSummariesAsync(
                userId, project.ID, cancellationToken).ConfigureAwait(false);
            long assetCount = await _repository.ProjectAssetCountAsync(project.ID, cancellationToken)
                .ConfigureAwait(false);
            int completed = units.Count(unit => unit.Status == ProjectUnitStatus.ProjectUnitStatusCompleted);
            result.Add(new ProjectSummaryDto
            {
                Project = project,
                CanvasCount = canvases.Count,
                AssetCount = assetCount,
                UnitCount = units.Count,
                CompletedUnitCount = completed,
            });
        }
        return result;
    }

    // ------------------------------------------------------------ 校验

    /// <summary>对应 Go: <c>prompts.ValidateStyleProfileJSON</c>。返回归一化后的 JSON。</summary>
    internal static string ValidateStyleProfileJSON(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "";
        }
        if (Encoding.UTF8.GetByteCount(trimmed) > 256 * 1024)
        {
            throw AppError.BadAuthRequest("项目画风资产配置过大");
        }
        JsonElement profile;
        try
        {
            profile = JsonSerializer.Deserialize<JsonElement>(trimmed);
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("项目画风资产配置不是合法 JSON");
        }
        if (profile.ValueKind != JsonValueKind.Object)
        {
            throw AppError.BadAuthRequest("项目画风资产配置缺少必要字段");
        }
        long schemaVersion = profile.TryGetProperty("schemaVersion", out JsonElement version) &&
                             version.ValueKind == JsonValueKind.Number
            ? version.GetInt64()
            : 0;
        string presetID = ProfileString(profile, "presetId");
        string title = ProfileString(profile, "title");
        string prompt = ProfileString(profile, "prompt");
        long revision = profile.TryGetProperty("revision", out JsonElement revisionElement) &&
                        revisionElement.ValueKind == JsonValueKind.Number
            ? revisionElement.GetInt64()
            : 0;
        if (schemaVersion != 1 || presetID.Length == 0 || title.Length == 0 || prompt.Length == 0 || revision < 1)
        {
            throw AppError.BadAuthRequest("项目画风资产配置缺少必要字段");
        }
        if (!profile.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw AppError.BadAuthRequest("项目画风资产配置缺少 assets 数组");
        }
        if (assets.GetArrayLength() > 20)
        {
            throw AppError.BadAuthRequest("项目画风执行资产最多绑定 20 个");
        }
        string executionPolicy = ProfileString(profile, "executionPolicy");
        if (executionPolicy.Length > 0 && executionPolicy is not ("compatible-fallback" or "strict-assets"))
        {
            throw AppError.BadAuthRequest("项目画风执行策略不支持");
        }
        return trimmed;
    }

    /// <summary>对应 Go: <c>ValidateStyleProfilePreset</c>。</summary>
    private static void ValidateStyleProfilePreset(string presetId, string profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return;
        }
        JsonElement profile;
        try
        {
            profile = JsonSerializer.Deserialize<JsonElement>(profileJson);
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("项目画风资产配置解析失败");
        }
        string profilePresetID = ProfileString(profile, "presetId");
        if (string.IsNullOrWhiteSpace(presetId) || profilePresetID != presetId.Trim())
        {
            throw AppError.BadAuthRequest("项目画风预设与结构化快照不一致");
        }
    }

    /// <summary>对应 Go: <c>normalizeProjectDefaultModel</c>（≤500 字符）。</summary>
    private static string NormalizeDefaultModel(string value)
    {
        string modelRef = value.Trim();
        if (modelRef.Length > 500)
        {
            throw AppError.BadAuthRequest("项目默认模型标识过长");
        }
        return modelRef;
    }

    /// <summary>对应 Go: <c>canvasPayloadWithoutProject</c>（键字典序输出）。</summary>
    internal static string CanvasPayloadWithoutProject(string payloadJSON, DateTime updatedAt)
    {
        JsonElement element;
        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(payloadJSON);
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("画布数据格式错误，无法解除项目关系");
        }
        Dictionary<string, JsonElement> payload = new(StringComparer.Ordinal);
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name is "projectId" or "updatedAt")
                {
                    continue;
                }
                payload[property.Name] = property.Value.Clone();
            }
        }
        payload["updatedAt"] = JsonSerializer.SerializeToElement(FormatRfc3339Nano(updatedAt));
        return JsonSerializer.Serialize(
            new Dictionary<string, JsonElement>(payload.OrderBy(pair => pair.Key, StringComparer.Ordinal), StringComparer.Ordinal));
    }

    internal static string FormatRfc3339Nano(DateTime value)
    {
        DateTime utc = value.ToUniversalTime();
        string baseText = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        string fraction = (utc.Ticks % TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture)
            .PadLeft(7, '0')
            .TrimEnd('0');
        return fraction.Length == 0 ? baseText + "Z" : baseText + "." + fraction + "Z";
    }

    private static string ProfileString(JsonElement profile, string key) =>
        profile.ValueKind == JsonValueKind.Object &&
        profile.TryGetProperty(key, out JsonElement element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim() ?? ""
            : "";
}

/// <summary>项目状态常量别名。对应 Go: <c>model.ProjectStatus*</c>。</summary>
internal static class ProjectStatus
{
    public const string ProjectStatusActive = "active";

    public const string ProjectStatusArchived = "archived";
}

/// <summary>项目单元状态常量别名。对应 Go: <c>model.ProjectUnitStatus*</c>。</summary>
internal static class ProjectUnitStatus
{
    public const string ProjectUnitStatusCompleted = "completed";
}
