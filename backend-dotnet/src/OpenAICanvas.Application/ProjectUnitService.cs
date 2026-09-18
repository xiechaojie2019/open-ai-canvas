#nullable enable
using System.Text.Json;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>单元创建请求。对应 Go: <c>app.CreateProjectUnitRequest</c>。</summary>
public sealed class CreateProjectUnitRequest
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("sourceText")]
    public string SourceText { get; set; } = "";

    [JsonPropertyName("position")]
    public long Position { get; set; }
}

/// <summary>单元更新请求。对应 Go: <c>app.UpdateProjectUnitRequest</c>。</summary>
public sealed class UpdateProjectUnitRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("sourceText")]
    public string SourceText { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}

/// <summary>批量导入请求。对应 Go: <c>app.ImportProjectUnitsRequest</c>。</summary>
public sealed class ImportProjectUnitsRequest
{
    [JsonPropertyName("units")]
    public List<CreateProjectUnitRequest>? Units { get; set; }
}

/// <summary>重排请求。对应 Go: <c>app.ReorderProjectUnitsRequest</c>。</summary>
public sealed class ReorderProjectUnitsRequest
{
    [JsonPropertyName("unitIds")]
    public List<string>? UnitIDs { get; set; }
}

/// <summary>画布单元链接请求。对应 Go: <c>app.LinkCanvasUnitRequest</c>。</summary>
public sealed class LinkCanvasUnitRequest
{
    [JsonPropertyName("canvasId")]
    public string CanvasID { get; set; } = "";

    [JsonPropertyName("unitId")]
    public string UnitID { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";
}

/// <summary>单元摘要集合。对应 Go: <c>app.ProjectUnitSummaries</c>。</summary>
public sealed class ProjectUnitSummariesDto
{
    [JsonPropertyName("units")]
    public IReadOnlyList<ProjectUnit> Units { get; init; } = [];

    [JsonPropertyName("canvasCounts")]
    public IReadOnlyDictionary<string, long> CanvasCounts { get; init; } = new Dictionary<string, long>();
}

/// <summary>
/// 项目单元与画布链接服务。对应 Go: <c>app/project.go</c> 的单元/链接部分。
/// </summary>
public sealed class ProjectUnitService
{
    private static readonly Regex HtmlTagPattern = new("<[^>]*>", RegexOptions.Compiled);

    private readonly Repository _repository;

    public ProjectUnitService(Repository repository)
    {
        _repository = repository;
    }

    /// <summary>创建单元。对应 Go: <c>CreateProjectUnit</c>。</summary>
    public async Task<ProjectUnit> CreateProjectUnitAsync(
        string userId, string projectId, CreateProjectUnitRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        ProjectUnit unit = NewProjectUnit(projectId, request, (int)request.Position);
        await _repository.CreateProjectUnitAsync(unit, cancellationToken).ConfigureAwait(false);
        await _repository.BumpProjectRevisionAsync(projectId, cancellationToken).ConfigureAwait(false);
        return unit;
    }

    /// <summary>单元摘要与画布计数。对应 Go: <c>ProjectUnitSummaries</c>。</summary>
    public async Task<ProjectUnitSummariesDto> ProjectUnitSummariesAsync(
        string userId, string projectId, CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectUnit> units = await _repository.ProjectUnitSummariesAsync(
            projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CanvasUnitLink> links = await _repository.ProjectCanvasUnitLinksAsync(
            projectId, cancellationToken).ConfigureAwait(false);
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        foreach (CanvasUnitLink link in links)
        {
            counts[link.UnitID] = counts.TryGetValue(link.UnitID, out long value) ? value + 1 : 1;
        }
        return new ProjectUnitSummariesDto { Units = units, CanvasCounts = counts };
    }

    /// <summary>单个单元（含正文）。对应 Go: <c>GetProjectUnit</c>。</summary>
    public async Task<ProjectUnit> GetProjectUnitAsync(
        string userId, string projectId, string unitId, CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        ProjectUnit? unit = await _repository.ProjectUnitAsync(
            projectId, unitId.Trim(), cancellationToken).ConfigureAwait(false);
        if (unit is null)
        {
            throw new InvalidOperationException("record not found");
        }
        return unit;
    }

    /// <summary>批量导入单元（1–2500）。对应 Go: <c>ImportProjectUnits</c>。</summary>
    public async Task<IReadOnlyList<ProjectUnit>> ImportProjectUnitsAsync(
        string userId, string projectId, ImportProjectUnitsRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        List<CreateProjectUnitRequest> inputs = request.Units ?? [];
        if (inputs.Count == 0 || inputs.Count > 2500)
        {
            throw AppError.BadAuthRequest("一次导入的章节数量必须在 1 到 2500 之间");
        }
        IReadOnlyList<ProjectUnit> existing = await _repository.ProjectUnitsAsync(
            projectId, cancellationToken).ConfigureAwait(false);
        List<ProjectUnit> units = [];
        for (int index = 0; index < inputs.Count; index++)
        {
            units.Add(NewProjectUnit(projectId, inputs[index], existing.Count + index));
        }
        await _repository.ImportProjectUnitsAsync(units, cancellationToken).ConfigureAwait(false);
        return units;
    }

    /// <summary>重排单元（全量校验）。对应 Go: <c>ReorderProjectUnits</c>。</summary>
    public async Task ReorderProjectUnitsAsync(
        string userId, string projectId, ReorderProjectUnitsRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        List<string> unitIds = request.UnitIDs ?? [];
        IReadOnlyList<ProjectUnit> units = await _repository.ProjectUnitsAsync(
            projectId, cancellationToken).ConfigureAwait(false);
        if (unitIds.Count != units.Count)
        {
            throw AppError.BadAuthRequest("章节排序列表不完整");
        }
        HashSet<string> existing = units.Select(unit => unit.ID).ToHashSet(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> normalized = [];
        foreach (string rawId in unitIds)
        {
            string id = rawId.Trim();
            if (!existing.Contains(id))
            {
                throw AppError.BadAuthRequest("章节排序包含无效章节");
            }
            if (!seen.Add(id))
            {
                throw AppError.BadAuthRequest("章节排序包含重复章节");
            }
            normalized.Add(id);
        }
        await _repository.ReorderProjectUnitsAsync(projectId, normalized, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        await _repository.BumpProjectRevisionAsync(projectId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>更新单元。对应 Go: <c>UpdateProjectUnit</c>。</summary>
    public async Task<ProjectUnit> UpdateProjectUnitAsync(
        string userId, string projectId, string unitId, UpdateProjectUnitRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        ProjectUnit? unit = await _repository.ProjectUnitAsync(projectId, unitId, cancellationToken)
            .ConfigureAwait(false);
        if (unit is null)
        {
            throw new InvalidOperationException("record not found");
        }
        bool sourceChanged = unit.SourceText != request.SourceText;
        string title = request.Title.Trim();
        if (title.Length > 0)
        {
            unit.Title = title;
        }
        unit.SourceText = request.SourceText;
        unit.WordCount = ProjectUnitWordCount(request.SourceText);
        string status = request.Status.Trim();
        if (status.Length > 0)
        {
            if (status is not ("draft" or "ready" or "completed"))
            {
                throw AppError.BadAuthRequest("不支持的章节状态");
            }
            unit.Status = status;
        }
        unit.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateProjectUnitAsync(unit, sourceChanged, cancellationToken).ConfigureAwait(false);
        await _repository.BumpProjectRevisionAsync(projectId, cancellationToken).ConfigureAwait(false);
        return unit;
    }

    /// <summary>删除单元。对应 Go: <c>DeleteProjectUnit</c>。</summary>
    public async Task DeleteProjectUnitAsync(
        string userId, string projectId, string unitId, CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        if (await _repository.ProjectUnitAsync(projectId, unitId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }
        await _repository.DeleteProjectUnitAsync(projectId, unitId, cancellationToken).ConfigureAwait(false);
        await _repository.BumpProjectRevisionAsync(projectId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>画布归属项目并建单元链接。对应 Go: <c>LinkCanvasUnit</c>。</summary>
    public async Task<CanvasUnitLink> LinkCanvasUnitAsync(
        string userId, string projectId, LinkCanvasUnitRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        string canvasId = request.CanvasID.Trim();
        string unitId = request.UnitID.Trim();
        if (canvasId.Length == 0 || unitId.Length == 0)
        {
            throw AppError.BadAuthRequest("画布和章节不能为空");
        }
        if (await _repository.CanvasProjectForUserAsync(userId, canvasId, cancellationToken).ConfigureAwait(false)
            is null)
        {
            throw new InvalidOperationException("record not found");
        }
        if (await _repository.ProjectUnitAsync(projectId, unitId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }
        await _repository.AssignCanvasToProjectAsync(userId, canvasId, projectId, cancellationToken)
            .ConfigureAwait(false);
        string role = request.Role.Trim();
        CanvasUnitLink link = new()
        {
            ID = IdGenerator.NewId(),
            ProjectID = projectId,
            CanvasID = canvasId,
            UnitID = unitId,
            Role = role,
            CreatedAt = DateTime.UtcNow,
        };
        await _repository.CreateCanvasUnitLinkAsync(link, cancellationToken).ConfigureAwait(false);
        return link;
    }

    /// <summary>删除画布单元链接。对应 Go: <c>UnlinkCanvasUnit</c>。</summary>
    public async Task UnlinkCanvasUnitAsync(
        string userId, string projectId, string canvasId, string unitId,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        await _repository.DeleteCanvasUnitLinkAsync(projectId, canvasId, unitId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 从项目移除画布（清空归属、删除单元链接、剥离载荷 projectId）。
    /// 对应 Go: <c>UnlinkCanvasProject</c>。
    /// </summary>
    public async Task UnlinkCanvasProjectAsync(
        string userId, string projectId, string canvasId, CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        Domain.Entities.CanvasProject? canvas = await _repository
            .CanvasProjectForUserAsync(userId, canvasId.Trim(), cancellationToken).ConfigureAwait(false);
        if (canvas is null)
        {
            throw new InvalidOperationException("record not found");
        }
        if (canvas.ProjectID != projectId)
        {
            throw AppError.BadAuthRequest("画布不属于当前项目");
        }
        DateTime now = DateTime.UtcNow;
        string payloadJson = CanvasPayloadWithoutProject(canvas.PayloadJSON, now);
        // 关系列、同步快照和更新时间必须原子更新，否则浏览器会用旧 projectId 把关系重新写回。
        await _repository.UnassignCanvasFromProjectAsync(
            userId, projectId, canvas.ID, payloadJson, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>对应 Go: <c>canvasPayloadWithoutProject</c>。剥离 projectId 并刷新 updatedAt（键序 Ordinal）。</summary>
    private static string CanvasPayloadWithoutProject(string payloadJson, DateTime updatedAt)
    {
        JsonElement payload;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            payload = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("画布数据格式错误，无法解除项目关系");
        }
        if (payload.ValueKind == JsonValueKind.Null)
        {
            // Go 的 json.Unmarshal(null, &map) 得到 nil map，不报错。
            return "{\"updatedAt\":\"" + ProjectService.FormatRfc3339Nano(updatedAt) + "\"}";
        }
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw AppError.BadAuthRequest("画布数据格式错误，无法解除项目关系");
        }
        Dictionary<string, JsonElement> map = new(StringComparer.Ordinal);
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            if (property.Name == "projectId")
            {
                continue;
            }
            map[property.Name] = property.Value.Clone();
        }
        map["updatedAt"] = JsonSerializer.SerializeToElement(
            ProjectService.FormatRfc3339Nano(updatedAt));
        return JsonSerializer.Serialize(
            ProjectCharacterService.SortedElement(JsonSerializer.SerializeToElement(map)),
            ProjectCharacterService.GoPayloadOptions);
    }

    // ------------------------------------------------------------ 内部

    private async Task RequireProjectAsync(string userId, string projectId, CancellationToken cancellationToken)
    {
        if (await _repository.ProjectForUserAsync(userId, projectId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }
    }

    /// <summary>对应 Go: <c>newProjectUnit</c>。</summary>
    private static ProjectUnit NewProjectUnit(string projectId, CreateProjectUnitRequest request, int position)
    {
        string kind = request.Kind.Trim();
        if (kind.Length == 0)
        {
            kind = "chapter";
        }
        if (kind is not ("chapter" or "episode"))
        {
            throw AppError.BadAuthRequest("不支持的项目单元类型");
        }
        string title = request.Title.Trim();
        if (title.Length == 0)
        {
            throw AppError.BadAuthRequest("章节标题不能为空");
        }
        if (position < 0)
        {
            position = 0;
        }
        DateTime now = DateTime.UtcNow;
        return new ProjectUnit
        {
            ID = IdGenerator.NewId(),
            ProjectID = projectId,
            Kind = kind,
            Title = title,
            SourceText = request.SourceText,
            WordCount = ProjectUnitWordCount(request.SourceText),
            Status = "draft",
            Position = position,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>对应 Go: <c>model.ProjectUnitWordCount</c>（去 HTML 标签 + 反转义 + 字符数）。</summary>
    internal static long ProjectUnitWordCount(string sourceText)
    {
        string plain = HtmlTagPattern.Replace(sourceText, "");
        return WebUtility.HtmlDecode(plain).Trim().Length;
    }
}