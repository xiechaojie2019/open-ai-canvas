#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>公告仓储方法。对应 Go: <c>repository/announcement.go</c> 与 <c>announcement_image.go</c>。</summary>
public sealed partial class Repository
{
    /// <summary>管理端公告分页。对应 Go: <c>AdminAnnouncements</c>。</summary>
    public async Task<(IReadOnlyList<Announcement> Announcements, long Total)> AdminAnnouncementsAsync(
        string keyword, string status, long limit, long offset, CancellationToken cancellationToken = default)
    {
        List<string> conditions = [];
        DynamicParameters parameters = new();
        string trimmed = keyword.Trim();
        if (trimmed.Length > 0)
        {
            conditions.Add("(lower(\"title\") LIKE @pattern OR lower(\"content\") LIKE @pattern)");
            parameters.Add("pattern", "%" + trimmed.ToLowerInvariant() + "%");
        }
        if (status is "active" or "closed")
        {
            conditions.Add("\"status\" = @status");
            parameters.Add("status", status);
        }
        string where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long total = await ScalarAsync<long>(
            connection, "SELECT COUNT(*) FROM \"announcements\"" + where, parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        parameters.Add("limit", limit);
        parameters.Add("offset", offset);
        IReadOnlyList<Announcement> announcements = await QueryAsync<Announcement>(
            connection,
            SqlBuilder.Select<Announcement>(null, "pinned DESC, \"publishedAt\" DESC", Dialect.LimitOffset(limit, offset))
                .Replace(" FROM \"announcements\"", " FROM \"announcements\"" + where),
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (announcements, total);
    }

    /// <summary>用户公告 feed（含未读数）。对应 Go: <c>AnnouncementFeed</c>。</summary>
    public async Task<(IReadOnlyList<Announcement> Announcements, long UnreadCount)> AnnouncementFeedAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Announcement> announcements = await QueryAsync<Announcement>(
            connection,
            SqlBuilder.Select<Announcement>("status = @status", "pinned DESC, \"publishedAt\" DESC"),
            new { status = "active" },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        long unread = await ScalarAsync<long>(
            connection,
            """
            SELECT COUNT(*) FROM "announcements"
            LEFT JOIN "userAnnouncementReads"
              ON "userAnnouncementReads"."announcementId" = "announcements"."id"
             AND "userAnnouncementReads"."userId" = @userId
            WHERE "announcements"."status" = @status AND "userAnnouncementReads"."id" IS NULL
            """,
            new { userId, status = "active" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (announcements, unread);
    }

    /// <summary>按 ID 查公告。对应 Go: <c>Announcement</c>。</summary>
    public async Task<Announcement?> AnnouncementAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<Announcement>(
            connection,
            SqlBuilder.Select<Announcement>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>关闭公告（仅 active 命中）。对应 Go: <c>CloseAnnouncement</c>。</summary>
    public async Task<bool> CloseAnnouncementAsync(
        string id, DateTime closedAt, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            "UPDATE \"announcements\" SET \"status\" = @closed, \"closedAt\" = @closedAt, \"updatedAt\" = @closedAt WHERE \"id\" = @id AND \"status\" = @active",
            new { closed = "closed", closedAt, id, active = "active" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>标记公告已读（幂等）。对应 Go: <c>MarkAnnouncementsRead</c>。</summary>
    public async Task MarkAnnouncementsReadAsync(
        string userId, IReadOnlyList<string> announcementIds, DateTime readAt,
        CancellationToken cancellationToken = default)
    {
        if (announcementIds.Count == 0)
        {
            return;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<string> activeIds = (await QueryAsync<string>(
            connection,
            "SELECT \"id\" FROM \"announcements\" WHERE \"id\" IN @ids AND \"status\" = @active",
            new { ids = announcementIds, active = "active" },
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        if (activeIds.Count == 0)
        {
            return;
        }
        foreach (string id in activeIds)
        {
            await ExecuteAsync(
                connection,
                "INSERT INTO \"userAnnouncementReads\" (\"id\", \"userId\", \"announcementId\", \"readAt\") VALUES (@id, @userId, @announcementId, @readAt)" +
                Dialect.OnConflictDoNothing("\"userId\", \"announcementId\""),
                new { id = IdGenerator.NewId(), userId, announcementId = id, readAt },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------ 配图草稿

    /// <summary>创建配图草稿。对应 Go: <c>CreateAnnouncementImageDraft</c>。</summary>
    public async Task CreateAnnouncementImageDraftAsync(
        AnnouncementImageDraft draft, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(AnnouncementImageDraft)), draft,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>按用户 + 资源查草稿。对应 Go: <c>AnnouncementImageDraftForUser</c>。</summary>
    public async Task<AnnouncementImageDraft?> AnnouncementImageDraftForUserAsync(
        string userId, string resourceId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<AnnouncementImageDraft>(
            connection,
            SqlBuilder.Select<AnnouncementImageDraft>(
                "\"resourceId\" = @resourceId AND \"userId\" = @userId", limitOffset: " LIMIT 1"),
            new { resourceId, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除草稿记录。对应 Go: <c>DeleteAnnouncementImageDraft</c>。</summary>
    public async Task DeleteAnnouncementImageDraftAsync(
        string userId, string resourceId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM \"announcementImageDrafts\" WHERE \"resourceId\" = @resourceId AND \"userId\" = @userId",
            new { resourceId, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>过期草稿。对应 Go: <c>StaleAnnouncementImageDrafts</c>。</summary>
    public async Task<IReadOnlyList<AnnouncementImageDraft>> StaleAnnouncementImageDraftsAsync(
        DateTime before, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is <= 0 or > 200)
        {
            limit = 50;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<AnnouncementImageDraft>(
            connection,
            SqlBuilder.Select<AnnouncementImageDraft>(
                "\"createdAt\" < @before", "\"createdAt\" ASC", Dialect.LimitOffset(limit, null)),
            new { before },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建公告并消费配图草稿（同事务）。对应 Go: <c>CreateAnnouncementWithImage</c>。</summary>
    public async Task CreateAnnouncementWithImageAsync(
        Announcement announcement, CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            await ConsumeAnnouncementImageDraftAsync(
                connection, transaction, announcement.CreatedBy, announcement.ImageResourceID, cancellationToken)
                .ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                SqlBuilder.Insert(typeof(Announcement)), announcement, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>更新公告并处理旧配图（同事务）。对应 Go: <c>UpdateAnnouncementWithImage</c>。</summary>
    public async Task<bool> UpdateAnnouncementWithImageAsync(
        Announcement announcement,
        string draftUserId,
        string newDraftResourceId,
        Resource? oldResource,
        ResourceDeletionJob? deletionJob,
        CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            await ConsumeAnnouncementImageDraftAsync(
                connection, transaction, draftUserId, newDraftResourceId, cancellationToken).ConfigureAwait(false);

            int updated = await ExecuteAsync(
                connection,
                """
                UPDATE "announcements" SET "title" = @Title, "content" = @Content,
                  "imageResourceId" = @ImageResourceID, "level" = @Level, "pinned" = @Pinned,
                  "status" = @Status, "publishedAt" = @PublishedAt, "closedAt" = @ClosedAt,
                  "updatedAt" = @UpdatedAt
                WHERE "id" = @ID
                """,
                announcement, transaction, cancellationToken).ConfigureAwait(false);
            if (updated != 1)
            {
                throw new InvalidOperationException("record not found");
            }

            await ExecuteAsync(connection,
                "DELETE FROM \"userAnnouncementReads\" WHERE \"announcementId\" = @id",
                new { id = announcement.ID }, transaction, cancellationToken).ConfigureAwait(false);

            if (oldResource is null)
            {
                return true;
            }
            long referenceCount = await ScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM \"announcements\" WHERE \"imageResourceId\" = @resourceId",
                new { resourceId = oldResource.ID }, transaction, cancellationToken).ConfigureAwait(false);
            if (referenceCount > 0)
            {
                return false;
            }
            await ExecuteAsync(connection,
                "DELETE FROM \"arkPrivateAssetBindings\" WHERE \"resourceId\" = @resourceId",
                new { resourceId = oldResource.ID }, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "DELETE FROM \"announcementImageDrafts\" WHERE \"resourceId\" = @resourceId",
                new { resourceId = oldResource.ID }, transaction, cancellationToken).ConfigureAwait(false);
            int deleted = await ExecuteAsync(connection,
                "DELETE FROM \"resources\" WHERE \"id\" = @resourceId AND \"userId\" = @userId",
                new { resourceId = oldResource.ID, userId = oldResource.UserID },
                transaction, cancellationToken).ConfigureAwait(false);
            if (deleted != 1)
            {
                throw new InvalidOperationException("record not found");
            }
            if (deletionJob is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert(typeof(ResourceDeletionJob)), deletionJob, transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>丢弃配图草稿（含资源与删除任务）。对应 Go: <c>DiscardAnnouncementImageDraft</c>。</summary>
    public async Task<bool> DiscardAnnouncementImageDraftAsync(
        string userId, Resource resource, ResourceDeletionJob? deletionJob,
        CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            AnnouncementImageDraft? draft = await FirstOrDefaultAsync<AnnouncementImageDraft>(
                connection,
                SqlBuilder.Select<AnnouncementImageDraft>(
                    "\"resourceId\" = @resourceId AND \"userId\" = @userId", limitOffset: " LIMIT 1"),
                new { resourceId = resource.ID, userId }, transaction, cancellationToken).ConfigureAwait(false);
            if (draft is null)
            {
                throw new InvalidOperationException("record not found");
            }
            long referenceCount = await ScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM \"announcements\" WHERE \"imageResourceId\" = @resourceId",
                new { resourceId = resource.ID }, transaction, cancellationToken).ConfigureAwait(false);
            if (referenceCount > 0)
            {
                return false;
            }
            await ExecuteAsync(connection,
                "DELETE FROM \"arkPrivateAssetBindings\" WHERE \"resourceId\" = @resourceId",
                new { resourceId = resource.ID }, transaction, cancellationToken).ConfigureAwait(false);
            int deleted = await ExecuteAsync(connection,
                "DELETE FROM \"resources\" WHERE \"id\" = @resourceId AND \"userId\" = @userId",
                new { resourceId = resource.ID, userId }, transaction, cancellationToken).ConfigureAwait(false);
            if (deleted != 1)
            {
                throw new InvalidOperationException("record not found");
            }
            await ExecuteAsync(connection,
                "DELETE FROM \"announcementImageDrafts\" WHERE \"resourceId\" = @resourceId AND \"userId\" = @userId",
                new { resourceId = resource.ID, userId }, transaction, cancellationToken).ConfigureAwait(false);
            if (deletionJob is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert(typeof(ResourceDeletionJob)), deletionJob, transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>消费草稿（空 ID 跳过）。对应 Go: <c>consumeAnnouncementImageDraft</c>。</summary>
    private static async Task ConsumeAnnouncementImageDraftAsync(
        DbConnection connection, DbTransaction transaction, string userId, string resourceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return;
        }
        int deleted = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM \"announcementImageDrafts\" WHERE \"resourceId\" = @resourceId AND \"userId\" = @userId",
            new { resourceId, userId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (deleted != 1)
        {
            throw new InvalidOperationException("announcement image draft unavailable");
        }
    }

    /// <summary>按 ID 查资源（不限用户）。对应 Go: <c>Resource</c>。</summary>
    public async Task<Resource?> ResourceAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<Resource>(
            connection,
            SqlBuilder.Select<Resource>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除资源记录。对应 Go: <c>DeleteResource</c>。</summary>
    public async Task DeleteResourceAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM \"resources\" WHERE \"id\" = @id AND \"userId\" = @userId",
            new { id, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}