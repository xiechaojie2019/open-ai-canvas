#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>项目核心读视图。对应 Go: <c>app.ProjectCore</c>。</summary>
public sealed class ProjectCoreDto
{
    [JsonPropertyName("project")]
    public Project Project { get; init; } = new();
}

/// <summary>项目总览。对应 Go: <c>app.ProjectOverview</c>。</summary>
public sealed class ProjectOverviewDto
{
    [JsonPropertyName("metrics")]
    public ProjectOverviewMetricsDto Metrics { get; init; } = new();

    [JsonPropertyName("units")]
    public List<ProjectOverviewUnitDto> Units { get; init; } = [];
}

/// <summary>项目总览指标。对应 Go: <c>app.ProjectOverviewMetrics</c>（字段顺序即输出顺序）。</summary>
public sealed class ProjectOverviewMetricsDto
{
    [JsonPropertyName("unitCount")]
    public long UnitCount { get; init; }

    [JsonPropertyName("completedUnitCount")]
    public long CompletedUnitCount { get; init; }

    [JsonPropertyName("totalWordCount")]
    public long TotalWordCount { get; init; }

    [JsonPropertyName("unitsWithoutText")]
    public long UnitsWithoutText { get; init; }

    [JsonPropertyName("unitsWithoutShots")]
    public long UnitsWithoutShots { get; init; }

    [JsonPropertyName("canvasCount")]
    public long CanvasCount { get; init; }

    [JsonPropertyName("assetCount")]
    public long AssetCount { get; init; }

    [JsonPropertyName("shotCount")]
    public long ShotCount { get; init; }

    [JsonPropertyName("pendingCandidateCount")]
    public long PendingCandidateCount { get; init; }

    [JsonPropertyName("readyStoryboardCount")]
    public long ReadyStoryboardCount { get; init; }

    [JsonPropertyName("readyPrevizCount")]
    public long ReadyPrevizCount { get; init; }

    [JsonPropertyName("readyVideoCount")]
    public long ReadyVideoCount { get; init; }

    [JsonPropertyName("renderSucceededCount")]
    public long RenderSucceededCount { get; init; }

    [JsonPropertyName("staleArtifactCount")]
    public long StaleArtifactCount { get; init; }
}

/// <summary>项目总览单元行。对应 Go: <c>app.ProjectOverviewUnit</c>。</summary>
public sealed class ProjectOverviewUnitDto
{
    [JsonPropertyName("unit")]
    public ProjectUnit Unit { get; init; } = new();

    [JsonPropertyName("shotCount")]
    public long ShotCount { get; init; }

    [JsonPropertyName("candidateCount")]
    public long CandidateCount { get; init; }

    [JsonPropertyName("canvasCount")]
    public long CanvasCount { get; init; }
}

/// <summary>
/// 工作台读视图。对应 Go: <c>internal/app/project_workbench_read.go</c> 的 core/overview。
/// </summary>
/// <remarks>
/// 三视图任务 reconcile（ProjectCore 里的降级修复）与 GET /projects/:id 的全量聚合
/// 依赖任务域（阶段 8）与工作流 v2（6.7），随对应节点接入（待确认 #51）。
/// </remarks>
public sealed class ProjectWorkbenchService
{
    private readonly Repository _repository;

    public ProjectWorkbenchService(Repository repository)
    {
        _repository = repository;
    }

    /// <summary>项目核心。对应 Go: <c>ProjectCore</c>。</summary>
    public async Task<ProjectCoreDto> CoreAsync(
        string userId, string projectId, CancellationToken cancellationToken = default)
    {
        Project? project = await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        return new ProjectCoreDto { Project = project };
    }

    /// <summary>项目总览。对应 Go: <c>ProjectOverview</c>。</summary>
    public async Task<ProjectOverviewDto> OverviewAsync(
        string userId, string projectId, CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        ProjectOverviewMetricsRow row = await _repository
            .ProjectOverviewMetricsAsync(projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectOverviewUnitRow> rows = await _repository
            .ProjectOverviewUnitsAsync(projectId, 8, cancellationToken).ConfigureAwait(false);
        return new ProjectOverviewDto
        {
            Metrics = new ProjectOverviewMetricsDto
            {
                UnitCount = row.UnitCount,
                CompletedUnitCount = row.CompletedUnitCount,
                TotalWordCount = row.TotalWordCount,
                UnitsWithoutText = row.UnitsWithoutText,
                UnitsWithoutShots = row.UnitsWithoutShots,
                CanvasCount = row.CanvasCount,
                AssetCount = row.AssetCount,
                ShotCount = row.ShotCount,
                PendingCandidateCount = row.PendingCandidateCount,
                ReadyStoryboardCount = row.ReadyStoryboardCount,
                ReadyPrevizCount = row.ReadyPrevizCount,
                ReadyVideoCount = row.ReadyVideoCount,
                RenderSucceededCount = row.TimelineRenderSucceededCount,
                StaleArtifactCount = row.StaleArtifactCount,
            },
            Units = rows.Select(item => new ProjectOverviewUnitDto
            {
                Unit = new ProjectUnit
                {
                    ID = item.ID,
                    ProjectID = item.ProjectID,
                    ParentID = item.ParentID,
                    Kind = item.Kind,
                    Title = item.Title,
                    WordCount = item.WordCount,
                    Status = item.Status,
                    Position = item.Position,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt,
                },
                ShotCount = item.ShotCount,
                CandidateCount = item.CandidateCount,
                CanvasCount = item.CanvasCount,
            }).ToList(),
        };
    }

    private async Task<Project> RequireProjectAsync(
        string userId, string projectId, CancellationToken cancellationToken)
    {
        Project? project = await _repository
            .ProjectForUserAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            // Go 走 IsProjectNotFound → 404 record not found。
            throw new InvalidOperationException("record not found");
        }
        return project;
    }
}
