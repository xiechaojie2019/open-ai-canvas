#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>管理端存储资源筛选条件。对应 Go: <c>repository.AdminResourceFilter</c>。</summary>
public sealed record AdminResourceFilter(
    string Keyword,
    string Kind,
    string Status,
    string Provider,
    string UserID,
    long Limit,
    long Offset);

/// <summary>
/// 管理端存储管理仓储（资源分页 / 按 ID 查询 / 引用检查 / 事务删除）。
/// 对应 Go: <c>repository/admin_storage.go</c> 与 <c>repository/admin_storage_delete.go</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>资源已被并发修改。对应 Go: <c>ErrAdminResourceDeleteChanged</c>。</summary>
    public const string AdminResourceDeleteChanged = "admin_resource_delete_changed";

    /// <summary>资源仍被业务数据引用。对应 Go: <c>ErrAdminResourceStillReferenced</c>。</summary>
    public const string AdminResourceStillReferenced = "admin_resource_still_referenced";

    /// <summary>
    /// 管理端资源分页（按创建时间倒序，ID 次序兜底）。
    /// 对应 Go: <c>Repository.AdminResources</c>。
    /// </summary>
    public async Task<(IReadOnlyList<Resource> Resources, long Total)> AdminResourcesAsync(
        AdminResourceFilter filter, CancellationToken cancellationToken = default)
    {
        List<string> conditions = [];
        DynamicParameters parameters = new();

        if (filter.Kind.Length > 0)
        {
            conditions.Add("\"kind\" = @kind");
            parameters.Add("kind", filter.Kind);
        }
        if (filter.Status.Length > 0)
        {
            conditions.Add("\"status\" = @status");
            parameters.Add("status", filter.Status);
        }
        if (filter.Provider.Length > 0)
        {
            // 与 Go 的 resourceProviderExpression 一致：空值等同 local。
            conditions.Add(
                "(CASE WHEN \"provider\" IS NULL OR \"provider\" = '' THEN 'local' ELSE lower(\"provider\") END) = @provider");
            parameters.Add("provider", filter.Provider);
        }
        if (filter.UserID.Length > 0)
        {
            conditions.Add("\"user_id\" = @userId");
            parameters.Add("userId", filter.UserID);
        }
        if (filter.Keyword.Length > 0)
        {
            conditions.Add("(lower(\"id\") LIKE @pattern OR lower(\"object_key\") LIKE @pattern)");
            parameters.Add("pattern", "%" + filter.Keyword.ToLowerInvariant() + "%");
        }
        string where = conditions.Count > 0 ? string.Join(" AND ", conditions) : "1 = 1";

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long total = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"resources\" WHERE " + where,
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        DynamicParameters pageParameters = new(parameters);
        pageParameters.Add("limit", filter.Limit);
        pageParameters.Add("offset", filter.Offset);
        // 必须走 SqlBuilder 的列名映射（Dapper 默认不会把 user_id 映射到 UserID）。
        IReadOnlyList<Resource> resources = await QueryAsync<Resource>(
            connection,
            SqlBuilder.Select<Resource>(where, "created_at DESC, id DESC", Dialect.LimitOffset(filter.Limit, filter.Offset)),
            pageParameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (resources, total);
    }

    /// <summary>按 ID 批量取资源（不区分用户）。对应 Go: <c>Repository.AdminResourcesByIDs</c>。</summary>
    public async Task<IReadOnlyList<Resource>> AdminResourcesByIDsAsync(
        IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Resource>(
            connection,
            SqlBuilder.Select<Resource>("id IN @ids"),
            new { ids },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 公告对资源的直接引用（不含用户维度）。
    /// 对应 Go: <c>Repository.AnnouncementResourceReferences</c>。
    /// </summary>
    public async Task<IReadOnlyList<ResourceDirectReference>> AnnouncementResourceReferencesAsync(
        IReadOnlyList<string> resourceIds, CancellationToken cancellationToken = default)
    {
        if (resourceIds.Count == 0)
        {
            return [];
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Announcement> announcements = await QueryAsync<Announcement>(
            connection,
            SqlBuilder.Select<Announcement>("image_resource_id IN @resourceIds"),
            new { resourceIds },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return [.. announcements.Select(announcement => new ResourceDirectReference(
            "公告", announcement.ID, announcement.Title, announcement.ImageResourceID))];
    }

    /// <summary>
    /// 管理端批量删除资源：锁读校验 → 引用检查 → 清绑定 → 写删除任务 → 删资源 → 写审计，同事务。
    /// 对应 Go: <c>Repository.DeleteAdminResources</c>。
    /// </summary>
    /// <remarks>引用/数量不一致时抛 <see cref="AdminResourceDeleteChanged"/> /
    /// <see cref="AdminResourceStillReferenced"/>，由上层映射为 400。</remarks>
    public async Task DeleteAdminResourcesAsync(
        IReadOnlyList<Resource> resources,
        IReadOnlyList<ResourceDeletionJob> deletionJobs,
        IReadOnlyList<AdminAuditEvent> audits,
        CancellationToken cancellationToken = default)
    {
        if (resources.Count == 0)
        {
            return;
        }
        if (audits.Count != resources.Count)
        {
            throw new InvalidOperationException("admin_resource_audit_mismatch");
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
                throw new InvalidOperationException(AdminResourceDeleteChanged);
            }

            foreach ((string table, string column) in new[]
                     {
                         ("announcements", "image_resource_id"),
                         ("asset_representations", "resource_id"),
                         ("voice_profiles", "sample_resource_id"),
                         ("shot_artifacts", "resource_id"),
                     })
            {
                long count = await ScalarAsync<long>(
                    connection,
                    $"SELECT COUNT(*) FROM \"{table}\" WHERE \"{column}\" IN @resourceIds",
                    new { resourceIds },
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                if (count > 0)
                {
                    throw new InvalidOperationException(AdminResourceStillReferenced);
                }
            }

            await ExecuteAsync(connection,
                "DELETE FROM \"ark_private_asset_bindings\" WHERE \"resource_id\" IN @resourceIds",
                new { resourceIds }, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "DELETE FROM \"announcement_image_drafts\" WHERE \"resource_id\" IN @resourceIds",
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
                throw new InvalidOperationException(AdminResourceDeleteChanged);
            }

            foreach (AdminAuditEvent audit in audits)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert(typeof(AdminAuditEvent)),
                    audit,
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }
}
