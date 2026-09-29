using System.Data.Common;
using System.Text;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>画布历史引用行。对应 Go: <c>repository.ResourceDirectReference</c> 投影。</summary>
public sealed class CanvasHistoryReferenceRow
{
    public string Kind { get; set; } = "";

    public string ID { get; set; } = "";

    public string Title { get; set; } = "";

    public string ResourceID { get; set; } = "";
}

/// <summary>
/// 画布版本历史仓储：快照查询、随内容事务保存与资源引用保护。
/// 对应 Go: <c>repository/canvas_history.go</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>历史引用保护失败：引用的素材缺失。对应 Go: <c>ErrCanvasHistoryResourceMissing</c>。</summary>
    public const string CanvasHistoryResourceMissingError = "canvas history resource missing";

    /// <summary>历史引用保护失败：素材仍被历史版本引用。对应 Go: <c>ErrCanvasHistoryResourceReferenced</c>。</summary>
    public const string CanvasHistoryResourceReferencedError = "resource referenced by canvas history";

    /// <summary>画布元数据（不含 payload）。对应 Go: <c>CanvasProjectMetadata</c>。</summary>
    public async Task<CanvasProject?> CanvasProjectMetadataAsync(
        string userID, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<CanvasProject>(
            connection,
            ProjectionWithoutPayload<CanvasProject>(null) + " FROM \"canvas_projects\" WHERE \"id\" = @id AND \"user_id\" = @userID LIMIT 1",
            new { id, userID },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>快照摘要列表（不含 payload）。对应 Go: <c>CanvasSnapshots</c>。</summary>
    public async Task<List<CanvasSnapshot>> CanvasSnapshotsAsync(
        string userID, string canvasID, int limit, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<CanvasSnapshot> items = (await QueryAsync<CanvasSnapshot>(
            connection,
            ProjectionWithoutPayload<CanvasSnapshot>(null) + " FROM \"canvas_snapshots\" " +
            "WHERE \"user_id\" = @userID AND \"canvas_id\" = @canvasID ORDER BY \"revision\" DESC LIMIT @limit",
            new { userID, canvasID, limit },
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        return items;
    }

    /// <summary>单个快照（含 payload）。对应 Go: <c>CanvasSnapshot</c>。</summary>
    public async Task<CanvasSnapshot?> CanvasSnapshotAsync(
        string userID, string canvasID, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<CanvasSnapshot>(
            connection,
            SqlBuilder.Select<CanvasSnapshot>(
                "\"id\" = @id AND \"user_id\" = @userID AND \"canvas_id\" = @canvasID", limitOffset: " LIMIT 1"),
            new { id, userID, canvasID },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// CAS 保存画布并在同一事务内决定是否捕获快照：引用保护与保留策略随内容一起提交。
    /// 对应 Go: <c>SaveCanvasWithSnapshot</c>。成功后 <paramref name="project"/> 的 Revision 为落库值。
    /// </summary>
    public Task SaveCanvasWithSnapshotAsync(
        CanvasProject project, CanvasSnapshot? snapshot, IReadOnlyList<string> resourceIDs,
        IReadOnlyList<string> restoredResourceIDs, DateTime cutoff, int limit, bool force,
        CancellationToken cancellationToken = default) =>
        InTransactionAsync(async (connection, transaction) =>
        {
            long originalRevision = project.Revision;
            try
            {
                await UpsertCanvasProjectCoreAsync(connection, transaction, project, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CanvasRevisionConflictException)
            {
                project.Revision = originalRevision;
                throw;
            }
            if (snapshot is null)
            {
                return true;
            }

            CanvasSnapshot? last = await FirstOrDefaultAsync<CanvasSnapshot>(
                connection,
                "SELECT \"id\" AS \"ID\", \"revision\" AS \"Revision\", \"created_at\" AS \"CreatedAt\" " +
                "FROM \"canvas_snapshots\" WHERE \"canvas_id\" = @canvasID ORDER BY \"revision\" DESC LIMIT 1",
                new { canvasID = project.ID },
                transaction,
                cancellationToken).ConfigureAwait(false);
            bool capture = last is null
                || (last.Revision != snapshot.Revision && (force || !(last.CreatedAt > cutoff)));

            List<string> requiredIDs = [.. restoredResourceIDs];
            if (capture)
            {
                requiredIDs.AddRange(resourceIDs);
            }
            requiredIDs = UniqueSortedStrings(requiredIDs);
            if (requiredIDs.Count > 0)
            {
                string resourcesSql = "SELECT \"id\" FROM \"resources\" " +
                    "WHERE \"id\" IN @ids AND \"status\" = 'ready' ORDER BY \"id\"" + Dialect.ForUpdate();
                List<string> found = (await QueryAsync<string>(
                    connection, resourcesSql, new { ids = requiredIDs }, transaction, cancellationToken)
                    .ConfigureAwait(false)).ToList();
                if (found.Count != requiredIDs.Count)
                {
                    throw new InvalidOperationException(CanvasHistoryResourceMissingError);
                }
            }
            if (!capture)
            {
                return true;
            }

            await ExecuteAsync(
                connection,
                SqlBuilder.Insert(typeof(CanvasSnapshot)),
                SqlBuilder.Parameters(snapshot),
                transaction,
                cancellationToken).ConfigureAwait(false);
            foreach (string resourceID in resourceIDs)
            {
                await ExecuteAsync(
                    connection,
                    "INSERT INTO \"canvas_snapshot_resources\" (\"snapshot_id\", \"resource_id\") VALUES (@snapshotID, @resourceID) " +
                    "ON CONFLICT (\"snapshot_id\", \"resource_id\") DO NOTHING",
                    new { snapshotID = snapshot.ID, resourceID },
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }

            // 保留最近 limit 个版本：超出部分连引用一起删除。
            // SQLite 的 OFFSET 必须带 LIMIT 子句（-1 表示不限制）；PostgreSQL 单独使用 OFFSET。
            string retentionSql = Dialect.IsPostgres
                ? "SELECT \"id\" FROM \"canvas_snapshots\" WHERE \"canvas_id\" = @canvasID ORDER BY \"revision\" DESC OFFSET @limit"
                : "SELECT \"id\" FROM \"canvas_snapshots\" WHERE \"canvas_id\" = @canvasID ORDER BY \"revision\" DESC LIMIT -1 OFFSET @limit";
            List<string> expired = (await QueryAsync<string>(
                connection,
                retentionSql,
                new { canvasID = project.ID, limit },
                transaction,
                cancellationToken).ConfigureAwait(false)).ToList();
            if (expired.Count > 0)
            {
                await ExecuteAsync(
                    connection,
                    "DELETE FROM \"canvas_snapshot_resources\" WHERE \"snapshot_id\" IN @ids",
                    new { ids = expired },
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(
                    connection,
                    "DELETE FROM \"canvas_snapshots\" WHERE \"id\" IN @ids",
                    new { ids = expired },
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }
            return true;
        }, cancellationToken);

    /// <summary>
    /// 物理对象是否仍被画布历史引用（含同 endpoint/bucket/object_key 的别名资源）。
    /// 对应 Go: <c>CanvasHistoryReferencesObject</c>。
    /// </summary>
    public async Task<bool> CanvasHistoryReferencesObjectAsync(
        Resource resource, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long? count = await ScalarAsync<long?>(
            connection,
            """
            SELECT COUNT(*) FROM "canvas_snapshot_resources"
            WHERE "resource_id" = @id
               OR "resource_id" IN (
                    SELECT "id" FROM "resources"
                    WHERE "endpoint" = @endpoint AND "bucket" = @bucket AND "object_key" = @objectKey)
            """,
            new { id = resource.ID, endpoint = resource.Endpoint, bucket = resource.Bucket, objectKey = resource.ObjectKey },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (count ?? 0) > 0;
    }

    /// <summary>历史引用明细。对应 Go: <c>CanvasHistoryResourceReferences</c>。</summary>
    public async Task<List<CanvasHistoryReferenceRow>> CanvasHistoryResourceReferencesAsync(
        IReadOnlyList<string> resourceIDs, CancellationToken cancellationToken = default)
    {
        if (resourceIDs.Count == 0)
        {
            return [];
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<CanvasHistoryReferenceRow> refs = (await QueryAsync<CanvasHistoryReferenceRow>(
            connection,
            """
            SELECT DISTINCT '画布历史版本' AS "Kind", snapshots."canvas_id" AS "ID", snapshots."title" AS "Title", refs."resource_id" AS "ResourceID"
            FROM "canvas_snapshot_resources" AS refs
            JOIN "canvas_snapshots" AS snapshots ON snapshots."id" = refs."snapshot_id"
            WHERE refs."resource_id" IN @resourceIDs
            """,
            new { resourceIDs },
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        return refs;
    }

    /// <summary>任一素材仍被历史引用时报错。对应 Go: <c>RequireNoCanvasHistoryReferences</c>。</summary>
    public async Task RequireNoCanvasHistoryReferencesAsync(
        IReadOnlyList<string> resourceIDs, CancellationToken cancellationToken = default)
    {
        if (resourceIDs.Count == 0)
        {
            return;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<string> found = (await QueryAsync<string>(
            connection,
            "SELECT \"id\" FROM \"resources\" WHERE \"id\" IN @ids ORDER BY \"id\"" + Dialect.ForUpdate(),
            new { ids = resourceIDs },
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        List<CanvasHistoryReferenceRow> refs = await CanvasHistoryResourceReferencesAsync(
            resourceIDs, cancellationToken).ConfigureAwait(false);
        if (refs.Count > 0)
        {
            throw new InvalidOperationException(CanvasHistoryResourceReferencedError);
        }
    }
    /// <summary>测试辅助：按自然主键写入最小快照行（模拟历史存量）。</summary>
    public async Task CanvasSnapshotSeedForTestAsync(
        string userID, string canvasID, long revision, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO "canvas_snapshots"
                ("id", "canvas_id", "user_id", "revision", "title", "node_count", "connection_count",
                 "payload_json", "payload_bytes", "reason", "content_updated_at", "created_at")
            VALUES (@id, @canvasID, @userID, @revision, 'seed', 0, 0, '{}', 2, 'automatic', @now, @now)
            ON CONFLICT ("id") DO NOTHING
            """,
            new { id = $"snap-{canvasID}-{revision}", canvasID, userID, revision, now = DateTime.UtcNow },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>元数据投影：全部列除 payload_json（Go Omit payload_json 的等价实现）。</summary>
    private static string ProjectionWithoutPayload<T>(string? alias)
    {
        EntityMap map = EntityMetadata.For(typeof(T));
        string prefix = string.IsNullOrEmpty(alias) ? "" : "\"" + alias + "\".";
        StringBuilder builder = new();
        foreach (ColumnMap column in map.Columns)
        {
            if (column.Column == "payload_json")
            {
                continue;
            }
            if (builder.Length > 0)
            {
                builder.Append(", ");
            }
            builder.Append(prefix).Append('"').Append(column.Column).Append("\" AS \"").Append(column.Property).Append('"');
        }
        return "SELECT " + builder;
    }
}
