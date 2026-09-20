#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 孤儿资源清理的仓储方法（候选筛选 + 事务删除）。
/// 对应 Go: <c>repository/resource_cleanup.go</c> 与 <c>Repository.ResourceCleanupCandidates</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>候选集在事务内发生变化。对应 Go: <c>ErrResourceCleanupSetChanged</c>。</summary>
    public const string ResourceCleanupSetChanged = "resource cleanup set changed";

    /// <summary>候选在事务内被重新引用。对应 Go: <c>ErrResourceCleanupStillReferenced</c>。</summary>
    public const string ResourceCleanupStillReferenced =
        "resource cleanup resource is still directly referenced";

    /// <summary>
    /// 清理候选：未完成（pending/failed）超过 incompleteBefore，或就绪超过 readyBefore。
    /// 对应 Go: <c>Repository.ResourceCleanupCandidates</c>（limit ≤0 或 &gt;500 时回落 100）。
    /// </summary>
    public async Task<IReadOnlyList<Resource>> ResourceCleanupCandidatesAsync(
        DateTime incompleteBefore,
        DateTime readyBefore,
        long limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > 500)
        {
            limit = 100;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Resource>(
            connection,
            SqlBuilder.Select<Resource>(
                "((\"status\" IN @incompleteStatuses AND \"updated_at\" <= @incompleteBefore)" +
                " OR (\"status\" = @readyStatus AND \"created_at\" <= @readyBefore))",
                "created_at ASC, id ASC",
                Dialect.LimitOffset(limit, 0)),
            new
            {
                incompleteStatuses = new[]
                {
                    ResourceStatus.ResourceStatusPending,
                    ResourceStatus.ResourceStatusFailed,
                },
                incompleteBefore,
                readyStatus = ResourceStatus.ResourceStatusReady,
                readyBefore,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 调整资源的生命周期时间点（测试构造过期候选用）。
    /// Go 侧等价能力由测试直接操作 <c>db.Model(...).Updates(...)</c> 完成。
    /// </summary>
    public async Task UpdateResourceLifetimeAsync(
        string id,
        DateTime? createdAt,
        DateTime? updatedAt,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE "resources" SET
                "created_at" = COALESCE(@createdAt, "created_at"),
                "updated_at" = COALESCE(@updatedAt, "updated_at"),
                "status" = COALESCE(@status, "status")
            WHERE "id" = @id
            """,
            new { id, createdAt, updatedAt, status },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 事务内删除孤儿资源并写入物理删除任务。
    /// 对应 Go: <c>Repository.DeleteDetachedResources</c>。
    /// </summary>
    /// <remarks>
    /// 服务层已完成精确 JSON 引用检查；这里的二次检查是保守兜底，
    /// 用于关闭「快照之后、删除之前被重新挂载」的竞态。
    /// 数量或引用不一致时抛 <see cref="ResourceCleanupSetChanged"/> /
    /// <see cref="ResourceCleanupStillReferenced"/>。
    /// </remarks>
    public async Task DeleteDetachedResourcesAsync(
        IReadOnlyList<Resource> resources,
        IReadOnlyList<ResourceDeletionJob> deletionJobs,
        CancellationToken cancellationToken = default)
    {
        if (resources.Count == 0)
        {
            return;
        }
        List<string> resourceIds = [.. resources.Select(resource => resource.ID)];

        await InTransactionAsync(async (connection, transaction) =>
        {
            IReadOnlyList<Resource> current = await QueryAsync<Resource>(
                connection,
                SqlBuilder.Select<Resource>("id IN @resourceIds"),
                new { resourceIds },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (current.Count != resources.Count)
            {
                throw new InvalidOperationException(ResourceCleanupSetChanged);
            }

            // 按用户取素材与画布文档做粗粒度包含检查（对应 Go 的 strings.Contains）。
            Dictionary<string, List<string>> documentsByUser = new(StringComparer.Ordinal);
            foreach (Resource resource in current)
            {
                if (documentsByUser.ContainsKey(resource.UserID))
                {
                    continue;
                }
                List<string> documents = [];
                documents.AddRange(await QueryAsync<string>(
                    connection,
                    "SELECT \"payload_json\" FROM \"assets\" WHERE \"user_id\" = @userId",
                    new { userId = resource.UserID },
                    transaction,
                    cancellationToken).ConfigureAwait(false));
                documents.AddRange(await QueryAsync<string>(
                    connection,
                    "SELECT \"payload_json\" FROM \"canvas_projects\" WHERE \"user_id\" = @userId",
                    new { userId = resource.UserID },
                    transaction,
                    cancellationToken).ConfigureAwait(false));
                documentsByUser[resource.UserID] = documents;
            }
            foreach (Resource resource in current)
            {
                string storageKey = "resource:" + resource.ID + "\"";
                string fileURL = "/api/resources/" + resource.ID + "/";
                foreach (string document in documentsByUser[resource.UserID])
                {
                    if (document.Contains(storageKey, StringComparison.Ordinal) ||
                        document.Contains(fileURL, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(ResourceCleanupStillReferenced);
                    }
                }
            }

            (string Table, string Column)[] checks =
            [
                ("announcements", "image_resource_id"),
                ("announcement_image_drafts", "resource_id"),
                ("asset_representations", "resource_id"),
                ("voice_profiles", "sample_resource_id"),
                ("shot_artifacts", "resource_id"),
            ];
            foreach ((string table, string column) in checks)
            {
                long count = await ScalarAsync<long>(
                    connection,
                    $"SELECT COUNT(*) FROM \"{table}\" WHERE \"{column}\" IN @resourceIds",
                    new { resourceIds },
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                if (count > 0)
                {
                    throw new InvalidOperationException(ResourceCleanupStillReferenced);
                }
            }

            await ExecuteAsync(connection,
                "DELETE FROM \"ark_private_asset_bindings\" WHERE \"resource_id\" IN @resourceIds",
                new { resourceIds }, transaction, cancellationToken).ConfigureAwait(false);

            foreach (ResourceDeletionJob job in deletionJobs)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert(typeof(ResourceDeletionJob)),
                    job,
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            int deleted = await ExecuteAsync(connection,
                "DELETE FROM \"resources\" WHERE \"id\" IN @resourceIds",
                new { resourceIds }, transaction, cancellationToken).ConfigureAwait(false);
            if (deleted != resourceIds.Count)
            {
                throw new InvalidOperationException(ResourceCleanupSetChanged);
            }
        }, cancellationToken).ConfigureAwait(false);
    }
}
