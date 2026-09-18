#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>项目单元仓储方法。对应 Go: <c>repository</c> 的单元部分。</summary>
public sealed partial class Repository
{
    /// <summary>项目单元全量（含正文）。对应 Go: <c>ProjectUnits</c>。</summary>
    public async Task<IReadOnlyList<ProjectUnit>> ProjectUnitsAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ProjectUnit>(
            connection,
            SqlBuilder.Select<ProjectUnit>("project_id = @projectId", "position ASC, created_at ASC"),
            new { projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按项目 + ID 查单元。对应 Go: <c>ProjectUnit</c>。</summary>
    public async Task<ProjectUnit?> ProjectUnitAsync(
        string projectId, string unitId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<ProjectUnit>(
            connection,
            SqlBuilder.Select<ProjectUnit>("id = @unitId AND project_id = @projectId", limitOffset: " LIMIT 1"),
            new { unitId, projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建单元。对应 Go: <c>CreateProjectUnit</c>。</summary>
    public async Task CreateProjectUnitAsync(
        ProjectUnit unit, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(ProjectUnit)), unit,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>批量导入单元（同事务）。对应 Go: <c>ImportProjectUnits</c>。</summary>
    public async Task ImportProjectUnitsAsync(
        IReadOnlyList<ProjectUnit> units, CancellationToken cancellationToken = default)
    {
        if (units.Count == 0)
        {
            return;
        }
        await InTransactionAsync(async (connection, transaction) =>
        {
            foreach (ProjectUnit unit in units)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert(typeof(ProjectUnit)), unit, transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>更新单元（sourceChanged 时同步正文与字数）。对应 Go: <c>UpdateProjectUnit</c>。</summary>
    public async Task UpdateProjectUnitAsync(
        ProjectUnit unit, bool sourceChanged, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        string sql = sourceChanged
            ? "UPDATE \"project_units\" SET \"title\" = @Title, \"source_text\" = @SourceText, \"word_count\" = @WordCount, \"status\" = @Status, \"updated_at\" = @UpdatedAt WHERE \"id\" = @ID AND \"project_id\" = @ProjectID"
            : "UPDATE \"project_units\" SET \"title\" = @Title, \"status\" = @Status, \"updated_at\" = @UpdatedAt WHERE \"id\" = @ID AND \"project_id\" = @ProjectID";
        await ExecuteAsync(connection, sql, unit, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除单元。对应 Go: <c>DeleteProjectUnit</c>。</summary>
    public async Task DeleteProjectUnitAsync(
        string projectId, string unitId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM \"project_units\" WHERE \"id\" = @unitId AND \"project_id\" = @projectId",
            new { unitId, projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>重排单元（同事务逐条写 position）。对应 Go: <c>ReorderProjectUnits</c>。</summary>
    public async Task ReorderProjectUnitsAsync(
        string projectId, IReadOnlyList<string> unitIds, DateTime now,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            for (int index = 0; index < unitIds.Count; index++)
            {
                await ExecuteAsync(
                    connection,
                    "UPDATE \"project_units\" SET \"position\" = @position, \"updated_at\" = @now WHERE \"id\" = @id AND \"project_id\" = @projectId",
                    new { position = (long)index, now, id = unitIds[index], projectId },
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>项目版本号 +1。对应 Go: <c>BumpProjectRevision</c>。</summary>
    public async Task BumpProjectRevisionAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE \"projects\" SET \"revision\" = \"revision\" + 1, \"updated_at\" = @now WHERE \"id\" = @projectId",
            new { now = DateTime.UtcNow, projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>画布归属项目。对应 Go: <c>AssignCanvasToProject</c>。</summary>
    public async Task AssignCanvasToProjectAsync(
        string userId, string canvasId, string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE \"canvas_projects\" SET \"project_id\" = @projectId, \"updated_at\" = @now WHERE \"id\" = @canvasId AND \"user_id\" = @userId",
            new { projectId, now = DateTime.UtcNow, canvasId, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>项目画布单元链接。对应 Go: <c>ProjectCanvasUnitLinks</c>。</summary>
    public async Task<IReadOnlyList<CanvasUnitLink>> ProjectCanvasUnitLinksAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<CanvasUnitLink>(
            connection,
            SqlBuilder.Select<CanvasUnitLink>("project_id = @projectId", "created_at ASC"),
            new { projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建画布单元链接。对应 Go: <c>CreateCanvasUnitLink</c>。</summary>
    public async Task CreateCanvasUnitLinkAsync(
        CanvasUnitLink link, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(CanvasUnitLink)), link,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>删除画布单元链接。对应 Go: <c>DeleteCanvasUnitLink</c>。</summary>
    public async Task DeleteCanvasUnitLinkAsync(
        string projectId, string canvasId, string unitId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM \"canvas_unit_links\" WHERE \"project_id\" = @projectId AND \"canvas_id\" = @canvasId AND \"unit_id\" = @unitId",
            new { projectId, canvasId, unitId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}