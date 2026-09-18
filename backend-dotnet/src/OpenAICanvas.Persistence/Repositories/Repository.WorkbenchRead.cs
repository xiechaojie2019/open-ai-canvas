#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>项目总览指标行。对应 Go: <c>repository.ProjectOverviewMetrics</c>。</summary>
public sealed class ProjectOverviewMetricsRow
{
    public long UnitCount { get; set; }
    public long CompletedUnitCount { get; set; }
    public long TotalWordCount { get; set; }
    public long UnitsWithoutText { get; set; }
    public long UnitsWithoutShots { get; set; }
    public long CanvasCount { get; set; }
    public long AssetCount { get; set; }
    public long ShotCount { get; set; }
    public long PendingCandidateCount { get; set; }
    public long ReadyStoryboardCount { get; set; }
    public long ReadyPrevizCount { get; set; }
    public long ReadyVideoCount { get; set; }
    public long TimelineRenderSucceededCount { get; set; }
    public long StaleArtifactCount { get; set; }
}

/// <summary>项目总览单元行（单元字段 + 关联计数）。对应 Go: <c>repository.ProjectOverviewUnitRow</c>。</summary>
public sealed class ProjectOverviewUnitRow
{
    public string ID { get; set; } = "";
    public string ProjectID { get; set; } = "";
    public string ParentID { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public long WordCount { get; set; }
    public string Status { get; set; } = "";
    public long Position { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long ShotCount { get; set; }
    public long CandidateCount { get; set; }
    public long CanvasCount { get; set; }
}

/// <summary>
/// 工作台读视图查询。对应 Go: <c>repository/project_workbench_read.go</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>章节画布计数（按章节去重画布）。对应 Go: <c>ProjectUnitCanvasCounts</c>。</summary>
    public async Task<IReadOnlyDictionary<string, long>> ProjectUnitCanvasCountsAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectKeyValueRow> rows = await QueryAsync<ProjectKeyValueRow>(
            connection,
            "SELECT unit_id AS KeyValue, COUNT(DISTINCT canvas_id) AS CountValue FROM canvas_unit_links WHERE project_id = @projectId GROUP BY unit_id",
            new { projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        foreach (ProjectKeyValueRow row in rows)
        {
            counts[row.KeyValue] = row.CountValue;
        }
        return counts;
    }

    /// <summary>项目总览指标（14 项子查询聚合）。对应 Go: <c>ProjectOverviewMetrics</c>。</summary>
    public async Task<ProjectOverviewMetricsRow> ProjectOverviewMetricsAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<ProjectOverviewMetricsRow>(
            connection,
            $"""
            SELECT
                (SELECT COUNT(*) FROM project_units WHERE project_id = @projectId) AS UnitCount,
                (SELECT COUNT(*) FROM project_units WHERE project_id = @projectId AND status = 'completed') AS CompletedUnitCount,
                (SELECT COALESCE(SUM(word_count), 0) FROM project_units WHERE project_id = @projectId) AS TotalWordCount,
                (SELECT COUNT(*) FROM project_units WHERE project_id = @projectId AND word_count = 0) AS UnitsWithoutText,
                (SELECT COUNT(*) FROM project_units pu WHERE pu.project_id = @projectId AND pu.status <> 'draft' AND NOT EXISTS (SELECT 1 FROM shots s WHERE s.project_id = pu.project_id AND s.unit_id = pu.id)) AS UnitsWithoutShots,
                (SELECT COUNT(*) FROM canvas_projects WHERE project_id = @projectId) AS CanvasCount,
                (SELECT COUNT(*) FROM project_asset_links WHERE project_id = @projectId) AS AssetCount,
                (SELECT COUNT(*) FROM shots WHERE project_id = @projectId) AS ShotCount,
                (SELECT COUNT(*) FROM project_asset_candidates WHERE project_id = @projectId AND status = 'pending_confirmation') AS PendingCandidateCount,
                (SELECT COUNT(DISTINCT shot_id) FROM shot_artifacts WHERE project_id = @projectId AND type = 'storyboard' AND selected = @selectedTrue AND status = 'ready') AS ReadyStoryboardCount,
                (SELECT COUNT(DISTINCT shot_id) FROM shot_artifacts WHERE project_id = @projectId AND type = 'action_board' AND selected = @selectedTrue AND status = 'ready') AS ReadyPrevizCount,
                (SELECT COUNT(DISTINCT shot_id) FROM shot_artifacts WHERE project_id = @projectId AND type = 'video' AND selected = @selectedTrue AND status = 'ready') AS ReadyVideoCount,
                (SELECT COUNT(*) FROM tasks WHERE project_id = @projectId AND type = 'timeline_render' AND status = 'succeeded') AS TimelineRenderSucceededCount,
                (SELECT COUNT(*) FROM shot_artifacts WHERE project_id = @projectId AND status = 'stale') AS StaleArtifactCount
            """,
            new { projectId, selectedTrue = Dialect.Boolean(true) },
            cancellationToken: cancellationToken).ConfigureAwait(false) ?? new ProjectOverviewMetricsRow();
    }

    /// <summary>项目总览单元行（按顺序取前 limit 个）。对应 Go: <c>ProjectOverviewUnits</c>。</summary>
    public async Task<IReadOnlyList<ProjectOverviewUnitRow>> ProjectOverviewUnitsAsync(
        string projectId, int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > 20)
        {
            limit = 8;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ProjectOverviewUnitRow>(
            connection,
            """
            SELECT pu.id, pu.project_id, pu.parent_id, pu.kind, pu.title, pu.word_count, pu.status, pu.position,
                pu.created_at, pu.updated_at,
                (SELECT COUNT(*) FROM shots s WHERE s.project_id = pu.project_id AND s.unit_id = pu.id) AS ShotCount,
                (SELECT COUNT(*) FROM project_asset_candidates pac WHERE pac.project_id = pu.project_id AND pac.unit_id = pu.id) AS CandidateCount,
                (SELECT COUNT(DISTINCT cul.canvas_id) FROM canvas_unit_links cul WHERE cul.project_id = pu.project_id AND cul.unit_id = pu.id) AS CanvasCount
            FROM project_units pu
            WHERE pu.project_id = @projectId
            ORDER BY pu.position ASC, pu.created_at ASC
            LIMIT @limit
            """,
            new { projectId, limit },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>键值计数行（列别名避免与 Dapper 保留映射冲突）。</summary>
public sealed class ProjectKeyValueRow
{
    public string KeyValue { get; set; } = "";
    public long CountValue { get; set; }
}
