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
            "SELECT \"unitId\" AS KeyValue, COUNT(DISTINCT \"canvasId\") AS CountValue FROM \"canvasUnitLinks\" WHERE \"projectId\" = @projectId GROUP BY \"unitId\"",
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
                (SELECT COUNT(*) FROM "projectUnits" WHERE "projectId" = @projectId) AS UnitCount,
                (SELECT COUNT(*) FROM "projectUnits" WHERE "projectId" = @projectId AND status = 'completed') AS CompletedUnitCount,
                (SELECT COALESCE(SUM("wordCount"), 0) FROM "projectUnits" WHERE "projectId" = @projectId) AS TotalWordCount,
                (SELECT COUNT(*) FROM "projectUnits" WHERE "projectId" = @projectId AND "wordCount" = 0) AS UnitsWithoutText,
                (SELECT COUNT(*) FROM "projectUnits" pu WHERE pu."projectId" = @projectId AND pu.status <> 'draft' AND NOT EXISTS (SELECT 1 FROM shots s WHERE s."projectId" = pu."projectId" AND s."unitId" = pu.id)) AS UnitsWithoutShots,
                (SELECT COUNT(*) FROM "canvasProjects" WHERE "projectId" = @projectId) AS CanvasCount,
                (SELECT COUNT(*) FROM "projectAssetLinks" WHERE "projectId" = @projectId) AS AssetCount,
                (SELECT COUNT(*) FROM shots WHERE "projectId" = @projectId) AS ShotCount,
                (SELECT COUNT(*) FROM "projectAssetCandidates" WHERE "projectId" = @projectId AND status = 'pending_confirmation') AS PendingCandidateCount,
                (SELECT COUNT(DISTINCT "shotId") FROM "shotArtifacts" WHERE "projectId" = @projectId AND type = 'storyboard' AND selected = @selectedTrue AND status = 'ready') AS ReadyStoryboardCount,
                (SELECT COUNT(DISTINCT "shotId") FROM "shotArtifacts" WHERE "projectId" = @projectId AND type = 'action_board' AND selected = @selectedTrue AND status = 'ready') AS ReadyPrevizCount,
                (SELECT COUNT(DISTINCT "shotId") FROM "shotArtifacts" WHERE "projectId" = @projectId AND type = 'video' AND selected = @selectedTrue AND status = 'ready') AS ReadyVideoCount,
                (SELECT COUNT(*) FROM tasks WHERE "projectId" = @projectId AND type = 'timeline_render' AND status = 'succeeded') AS TimelineRenderSucceededCount,
                (SELECT COUNT(*) FROM "shotArtifacts" WHERE "projectId" = @projectId AND status = 'stale') AS StaleArtifactCount
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
            SELECT pu.id, pu."projectId", pu."parentId", pu.kind, pu.title, pu."wordCount", pu.status, pu.position,
                pu."createdAt", pu."updatedAt",
                (SELECT COUNT(*) FROM shots s WHERE s."projectId" = pu."projectId" AND s."unitId" = pu.id) AS ShotCount,
                (SELECT COUNT(*) FROM "projectAssetCandidates" pac WHERE pac."projectId" = pu."projectId" AND pac."unitId" = pu.id) AS CandidateCount,
                (SELECT COUNT(DISTINCT cul."canvasId") FROM "canvasUnitLinks" cul WHERE cul."projectId" = pu."projectId" AND cul."unitId" = pu.id) AS CanvasCount
            FROM "projectUnits" pu
            WHERE pu."projectId" = @projectId
            ORDER BY pu.position ASC, pu."createdAt" ASC
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
