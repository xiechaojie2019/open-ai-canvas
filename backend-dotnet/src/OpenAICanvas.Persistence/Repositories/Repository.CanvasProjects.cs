#nullable enable
using System.Data.Common;
using System.Text.Json;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 画布版本 CAS 未命中。对应 Go: <c>repository.ErrCanvasRevisionConflict</c>。
/// 服务层将其映射为 HTTP 409，提示保留本地草稿并加载云端最新版本。
/// </summary>
public sealed class CanvasRevisionConflictException : Exception
{
    public CanvasRevisionConflictException()
        : base("canvas revision conflict")
    {
    }
}

/// <summary>
/// 画布工程（canvas_projects）仓储方法。
/// 对应 Go: <c>repository/repository.go</c> 与 <c>repository/project_workbench_read.go</c> 的画布部分。
/// </summary>
public sealed partial class Repository
{
    /// <summary>用户全部画布（payload 含全量 JSON）。对应 Go: <c>CanvasProjects</c>。</summary>
    public async Task<IReadOnlyList<CanvasProject>> CanvasProjectsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<CanvasProject>(
            connection,
            SqlBuilder.Select<CanvasProject>("user_id = @userId", "updated_at DESC"),
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>用户画布摘要（id/title/时间）。对应 Go: <c>CanvasProjectSummaries</c>。</summary>
    public async Task<IReadOnlyList<CanvasProject>> CanvasProjectSummariesAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<CanvasProject>(
            connection,
            SqlBuilder.SelectColumns<CanvasProject>(
                ["ID", "Title", "CreatedAt", "UpdatedAt", "Revision"], "user_id = @userId", "updated_at DESC"),
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按用户 + ID 查画布。对应 Go: <c>CanvasProjectForUser</c>。</summary>
    public async Task<CanvasProject?> CanvasProjectForUserAsync(
        string userId,
        string id,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<CanvasProject>(
            connection,
            SqlBuilder.Select<CanvasProject>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 画布 upsert：按版本号 CAS 递增，未命中（新建）仅在 revision=0 时插入。
    /// 对应 Go: <c>UpsertCanvasProject</c>——版本谓词与递增必须在同一条 SQL 内完成，
    /// 行缺失是冲突，绝不重建已删除画布。
    /// </summary>
    public async Task UpsertCanvasProjectAsync(
        CanvasProject project,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await UpsertCanvasProjectCoreAsync(connection, transaction: null, project, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>事务内变体：供快照保存与画布保存共用同一连接。</summary>
    public async Task UpsertCanvasProjectCoreAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CanvasProject project,
        CancellationToken cancellationToken = default)
    {
        long expected = project.Revision;
        if (expected < 0)
        {
            throw new CanvasRevisionConflictException();
        }
        if (expected == 0)
        {
            CanvasProject created = new()
            {
                ID = project.ID,
                UserID = project.UserID,
                ProjectID = project.ProjectID,
                Title = project.Title,
                PayloadJSON = project.PayloadJSON,
                Revision = 1,
                CreatedAt = project.CreatedAt,
                UpdatedAt = project.UpdatedAt,
            };
            int inserted = await ExecuteAsync(
                connection,
                SqlBuilder.Insert(typeof(CanvasProject), onConflictDoNothing: true),
                created,
                transaction,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (inserted != 1)
            {
                throw new CanvasRevisionConflictException();
            }
            project.Revision = 1;
            return;
        }
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE "canvas_projects" SET "project_id" = @ProjectID, "title" = @Title, "payload_json" = @PayloadJSON, "updated_at" = @UpdatedAt, "revision" = @NextRevision
            WHERE "id" = @ID AND "user_id" = @UserID AND "revision" = @ExpectedRevision
            """,
            new { project.ProjectID, project.Title, project.PayloadJSON, project.UpdatedAt, NextRevision = expected + 1, project.ID, project.UserID, ExpectedRevision = expected },
            transaction,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new CanvasRevisionConflictException();
        }
        project.Revision = expected + 1;
    }

    /// <summary>
    /// 删除画布：连带清理分享、项目单元链接，并把任务与画布的归属解绑。
    /// 对应 Go: <c>DeleteCanvasProject</c>。
    /// </summary>
    public async Task DeleteCanvasProjectAsync(
        string userId,
        string id,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(
                connection,
                "DELETE FROM \"canvas_shares\" WHERE \"user_id\" = @userId AND \"project_id\" = @id",
                new { userId, id },
                transaction,
                cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                "DELETE FROM \"canvas_unit_links\" WHERE \"canvas_id\" = @id",
                new { id },
                transaction,
                cancellationToken).ConfigureAwait(false);

            // 任务是审计记录，不随独立画布实体保留归属 ID，避免删除后继续挂住画布上下文。
            await ExecuteAsync(
                connection,
                "UPDATE \"tasks\" SET \"project_id\" = '' WHERE \"user_id\" = @userId AND \"project_id\" = @id",
                new { userId, id },
                transaction,
                cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                "DELETE FROM \"canvas_projects\" WHERE \"id\" = @id AND \"user_id\" = @userId",
                new { id, userId },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 管理端/创作端画布分页（projectID 过滤、标题搜索、三种排序）。
    /// 对应 Go: <c>UserCanvasProjectsPage</c>。
    /// </summary>
    public async Task<(IReadOnlyList<CanvasProject> Projects, long Total)> UserCanvasProjectsPageAsync(
        string userId,
        int page,
        int pageSize,
        string projectId,
        string search,
        string sort,
        CancellationToken cancellationToken = default)
    {
        List<string> conditions = ["user_id = @userId"];
        DynamicParameters parameters = new();
        parameters.Add("userId", userId);

        if (projectId == "independent")
        {
            conditions.Add("(project_id = '' OR project_id IS NULL)");
        }
        else if (projectId.Length > 0 && projectId != "all")
        {
            conditions.Add("project_id = @projectId");
            parameters.Add("projectId", projectId);
        }
        string trimmedSearch = search.Trim();
        if (trimmedSearch.Length > 0)
        {
            conditions.Add("LOWER(title) LIKE @pattern");
            parameters.Add("pattern", "%" + trimmedSearch.ToLowerInvariant() + "%");
        }
        string where = string.Join(" AND ", conditions);

        // sort=nodes 按 nodes 数组长度排序：SQLite json_array_length / PG jsonb_array_length。
        string order = "updated_at DESC, id ASC";
        if (sort == "name")
        {
            order = "title ASC, id ASC";
        }
        else if (sort == "nodes")
        {
            order = Dialect.IsPostgres
                ? "COALESCE(jsonb_array_length(payload_json::jsonb->'nodes'), 0) DESC, id ASC"
                : "COALESCE(json_array_length(payload_json, '$.nodes'), 0) DESC, id ASC";
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long total = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"canvas_projects\" WHERE " + where,
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        parameters.Add("limit", pageSize);
        parameters.Add("offset", (page - 1) * pageSize);
        IReadOnlyList<CanvasProject> projects = await QueryAsync<CanvasProject>(
            connection,
            SqlBuilder.Select<CanvasProject>(where, order, Dialect.LimitOffset(pageSize, (page - 1) * pageSize)),
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (projects, total);
    }

    /// <summary>按用户批量取素材。对应 Go: <c>AssetsForUserIDs</c>。</summary>
    public async Task<IReadOnlyList<Asset>> AssetsForUserIDsAsync(
        string userId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Asset>(
            connection,
            SqlBuilder.Select<Asset>("user_id = @userId AND id IN @ids"),
            new { userId, ids },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按用户批量取资源。对应 Go: <c>ResourcesForUserIDs</c>。</summary>
    public async Task<IReadOnlyList<Resource>> ResourcesForUserIDsAsync(
        string userId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Resource>(
            connection,
            SqlBuilder.Select<Resource>("user_id = @userId AND id IN @ids"),
            new { userId, ids },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
