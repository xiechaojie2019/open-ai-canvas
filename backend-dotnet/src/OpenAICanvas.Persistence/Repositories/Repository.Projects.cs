#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>项目 CRUD 仓储方法。对应 Go: <c>repository/repository.go</c> 项目部分。</summary>
public sealed partial class Repository
{
    /// <summary>用户全量项目。对应 Go: <c>Projects</c>。</summary>
    public async Task<IReadOnlyList<Project>> ProjectsAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Project>(
            connection,
            SqlBuilder.Select<Project>("\"userId\" = @userId", "\"updatedAt\" DESC"),
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>项目分页。对应 Go: <c>ProjectsPage</c>。</summary>
    public async Task<(IReadOnlyList<Project> Projects, long Total)> ProjectsPageAsync(
        string userId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long total = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"projects\" WHERE \"userId\" = @userId",
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Project> projects = await QueryAsync<Project>(
            connection,
            SqlBuilder.Select<Project>(
                "\"userId\" = @userId", "\"updatedAt\" DESC", Dialect.LimitOffset(pageSize, (page - 1) * pageSize)),
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (projects, total);
    }

    /// <summary>按用户 + ID 查项目。对应 Go: <c>ProjectForUser</c>。</summary>
    public async Task<Project?> ProjectForUserAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<Project>(
            connection,
            SqlBuilder.Select<Project>("id = @id AND \"userId\" = @userId", limitOffset: " LIMIT 1"),
            new { id, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建项目。对应 Go: <c>CreateProject</c>。</summary>
    public async Task CreateProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(Project)),
            project,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>更新项目列。对应 Go: <c>UpdateProject</c>。</summary>
    public async Task UpdateProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE "projects" SET "name" = @Name, "type" = @Type, "aspectRatio" = @AspectRatio,
              "sourceType" = @SourceType, "description" = @Description, "coverResourceId" = @CoverResourceID,
              "stylePresetId" = @StylePresetID, "styleProfileJson" = @StyleProfileJSON,
              "defaultImageModel" = @DefaultImageModel, "defaultVideoModel" = @DefaultVideoModel,
              "status" = @Status, "revision" = @Revision, "updatedAt" = @UpdatedAt
            WHERE "id" = @ID AND "userId" = @UserID
            """,
            project,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除项目：解绑画布、清理单元/链接/分享，任务保留但解绑归属。
    /// 对应 Go: <c>DeleteProject</c>（canvasUpdates 传入时同步回写画布 payload.projectId）。
    /// </summary>
    public async Task DeleteProjectAsync(
        string userId,
        string id,
        IReadOnlyList<CanvasProject> canvasUpdates,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            List<string> canvasIds = (await QueryAsync<string>(
                connection,
                "SELECT \"id\" FROM \"canvasProjects\" WHERE \"userId\" = @userId AND \"projectId\" = @id",
                new { userId, id },
                transaction,
                cancellationToken).ConfigureAwait(false)).ToList();
            List<string> projectScopeIds = [id, .. canvasIds];

            long activeTaskCount = await ScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM \"tasks\" WHERE \"userId\" = @userId AND \"projectId\" IN @scopeIds AND \"status\" IN @statuses",
                new { userId, scopeIds = projectScopeIds, statuses = new[] { "queued", "running" } },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (activeTaskCount > 0)
            {
                throw AppError.BadAuthRequest("项目仍有排队中或进行中的任务，请先取消这些任务");
            }

            // 画布解绑：按版本守卫回写 payload（project_id 移除）并清空 project_id 列。
            // 对应 Go: <c>DeleteProject</c> 的 revision CAS 递增。
            foreach (CanvasProject canvas in canvasUpdates)
            {
                int updated = await ExecuteAsync(
                    connection,
                    "UPDATE \"canvasProjects\" SET \"payloadJson\" = @PayloadJSON, \"projectId\" = '', \"updatedAt\" = @now, \"revision\" = @NextRevision WHERE \"id\" = @ID AND \"userId\" = @UserID AND \"revision\" = @Revision",
                    new { canvas.PayloadJSON, now, canvas.ID, canvas.UserID, NextRevision = canvas.Revision + 1, Revision = canvas.Revision },
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                if (updated != 1)
                {
                    // 对应 Go: RowsAffected != 1 返回 gorm.ErrRecordNotFound。
                    throw new InvalidOperationException("record not found");
                }
            }

            await ExecuteAsync(connection,
                "DELETE FROM \"canvasUnitLinks\" WHERE \"canvasId\" IN @scopeIds",
                new { scopeIds = projectScopeIds }, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "DELETE FROM \"projectUnits\" WHERE \"projectId\" = @id",
                new { id }, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "DELETE FROM \"canvasShares\" WHERE \"userId\" = @userId AND \"projectId\" = @id",
                new { userId, id }, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "UPDATE \"tasks\" SET \"projectId\" = '' WHERE \"userId\" = @userId AND \"projectId\" IN @scopeIds",
                new { userId, scopeIds = projectScopeIds }, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "DELETE FROM \"projects\" WHERE \"id\" = @id AND \"userId\" = @userId",
                new { id, userId }, transaction, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>项目画布文档（payload 全量）。对应 Go: <c>ProjectCanvasDocuments</c>。</summary>
    public async Task<IReadOnlyList<CanvasProject>> ProjectCanvasDocumentsAsync(
        string userId, string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<CanvasProject>(
            connection,
            SqlBuilder.Select<CanvasProject>("\"userId\" = @userId AND \"projectId\" = @projectId", "\"updatedAt\" DESC"),
            new { userId, projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>项目单元摘要（不含长正文）。对应 Go: <c>ProjectUnitSummaries</c>。</summary>
    public async Task<IReadOnlyList<ProjectUnit>> ProjectUnitSummariesAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ProjectUnit>(
            connection,
            SqlBuilder.SelectColumns<ProjectUnit>(
                ["ID", "ProjectID", "ParentID", "Kind", "Title", "WordCount", "Status", "Position", "CreatedAt", "UpdatedAt"],
                "\"projectId\" = @projectId",
                "position ASC, \"createdAt\" ASC"),
            new { projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>项目画布摘要。对应 Go: <c>ProjectCanvasSummaries</c>。</summary>
    public async Task<IReadOnlyList<CanvasProject>> ProjectCanvasSummariesAsync(
        string userId, string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<CanvasProject>(
            connection,
            SqlBuilder.SelectColumns<CanvasProject>(
                ["ID", "ProjectID", "Title", "CreatedAt", "UpdatedAt"],
                "\"userId\" = @userId AND \"projectId\" = @projectId",
                "\"updatedAt\" DESC"),
            new { userId, projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>项目素材数。对应 Go: <c>ProjectAssetCount</c>。</summary>
    public async Task<long> ProjectAssetCountAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"projectAssetLinks\" WHERE \"projectId\" = @projectId",
            new { projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
