#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 资源上传写路径的仓储方法（幂等键、当日上传额度、资源持久化）。
/// 对应 Go: <c>app/resource.go</c> + <c>app/upload_quota.go</c> 依赖的
/// <c>Repository.CreateResource</c> / <c>SaveResource</c> / <c>ResourceByUploadKey</c> /
/// <c>ClaimFailedResourceUpload</c> / <c>ReserveDailyUpload</c> / <c>ReleaseDailyUpload</c>。
/// </summary>
public sealed partial class Repository
{
    public async Task<bool> ClaimPlaybackTranscodeAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await ExecuteAsync(connection,
            "UPDATE \"resources\" SET \"playback_status\" = 'processing', \"updated_at\" = @now WHERE \"id\" = @resourceId AND \"kind\" = 'video' AND \"provider\" = 'local' AND \"status\" = @ready AND (\"playback_status\" = '' OR \"playback_status\" = 'none')",
            new { resourceId, ready = ResourceStatus.ResourceStatusReady, now = DateTime.UtcNow }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }

    public async Task<IReadOnlyList<Resource>> PlaybackPendingVideosAsync(int limit, CancellationToken cancellationToken = default)
    {
        limit = limit is <= 0 or > 100 ? 20 : limit;
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Resource>(connection, $"SELECT * FROM \"resources\" WHERE \"kind\" = 'video' AND \"provider\" = 'local' AND \"status\" = @ready AND (\"playback_status\" = '' OR \"playback_status\" = 'processing') ORDER BY \"updated_at\" ASC LIMIT {limit}", new { ready = ResourceStatus.ResourceStatusReady }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Resource>> PlaybackNoneVideosAsync(int limit, CancellationToken cancellationToken = default)
    {
        limit = limit is <= 0 or > 100 ? 20 : limit;
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Resource>(connection, $"SELECT * FROM \"resources\" WHERE \"kind\" = 'video' AND \"provider\" = 'local' AND \"status\" = @ready AND \"playback_status\" = 'none' ORDER BY \"updated_at\" ASC LIMIT {limit}", new { ready = ResourceStatus.ResourceStatusReady }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetStuckPlaybackTranscodesAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "UPDATE \"resources\" SET \"playback_status\" = '', \"playback_error\" = '', \"updated_at\" = @now WHERE \"kind\" = 'video' AND \"playback_status\" = 'processing'", new { now = DateTime.UtcNow }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    /// <summary>当日上传额度不足。对应 Go: <c>repository.ErrDailyUploadLimitExceeded</c>。</summary>
    public const string DailyUploadLimitExceeded = "daily_upload_limit_exceeded";

    /// <summary>按用户 + 幂等键查资源。对应 Go: <c>Repository.ResourceByUploadKey</c>（未命中返回 null）。</summary>
    public async Task<Resource?> ResourceByUploadKeyAsync(
        string userId, string uploadKey, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<Resource>(
            connection,
            SqlBuilder.Select<Resource>("user_id = @userId AND upload_key = @uploadKey", limitOffset: " LIMIT 1"),
            new { userId, uploadKey },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 用户资源列表，按创建时间倒序。limit≤0 或 &gt;500 时回落为 200。
    /// 对应 Go: <c>Repository.Resources</c>。
    /// </summary>
    public async Task<IReadOnlyList<Resource>> ResourcesAsync(
        string userId, long limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > 500)
        {
            limit = 200;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Resource>(
            connection,
            """SELECT * FROM "resources" WHERE "user_id" = @userId ORDER BY "created_at" DESC LIMIT @limit""",
            new { userId, limit },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>新建资源记录。对应 Go: <c>Repository.CreateResource</c>。</summary>
    public async Task CreateResourceAsync(Resource resource, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(Resource)),
            resource,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>按主键整体保存资源。对应 Go: <c>Repository.SaveResource</c>（GORM Save = 全字段更新）。</summary>
    public async Task SaveResourceAsync(Resource resource, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE "resources" SET
                "user_id" = @UserID, "kind" = @Kind, "status" = @Status, "provider" = @Provider,
                "endpoint" = @Endpoint, "bucket" = @Bucket, "storage_setting_id" = @StorageSettingID,
                "object_key" = @ObjectKey, "public_url" = @PublicURL, "mime_type" = @MimeType,
                "size" = @Size, "width" = @Width, "height" = @Height, "duration_ms" = @DurationMs,
                "e_tag" = @ETag, "playback_status" = @PlaybackStatus,
                "playback_object_key" = @PlaybackObjectKey, "playback_error" = @PlaybackError,
                "upload_key" = @UploadKey, "error" = @Error, "updated_at" = @UpdatedAt
            WHERE "id" = @ID
            """,
            resource,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 抢占失败资源用于重试：仅当状态仍为 failed 时置为 pending 并清空错误。
    /// 对应 Go: <c>Repository.ClaimFailedResourceUpload</c>（返回是否命中 1 行）。
    /// </summary>
    public async Task<bool> ClaimFailedResourceUploadAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await ExecuteAsync(
            connection,
            """
            UPDATE "resources" SET "status" = @pending, "error" = '', "updated_at" = @now
            WHERE "id" = @id AND "user_id" = @userId AND "status" = @failed
            """,
            new
            {
                pending = ResourceStatus.ResourceStatusPending,
                failed = ResourceStatus.ResourceStatusFailed,
                now = DateTime.UtcNow,
                id,
                userId,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }

    /// <summary>
    /// 原子预留当日上传额度（先插占位行，再带额度下限条件累加）。
    /// 对应 Go: <c>Repository.ReserveDailyUpload</c>；额度不足时抛
    /// <see cref="DailyUploadLimitExceeded"/>。
    /// </summary>
    public async Task ReserveDailyUploadAsync(
        string userId, string day, long size, long limit, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        string id = userId + ":" + day;
        await ExecuteAsync(
            connection,
            SqlBuilder.Insert(typeof(UserDailyUploadUsage)) + Dialect.OnConflictDoNothing(string.Empty),
            new UserDailyUploadUsage
            {
                ID = id,
                UserID = userId,
                Day = day,
                Bytes = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            transaction,
            cancellationToken).ConfigureAwait(false);

        // 与 Go 一致：bytes + size < limit 才计入，命中 0 行即超限。
        int affected = await ExecuteAsync(
            connection,
            """
            UPDATE "user_daily_upload_usages" SET "bytes" = "bytes" + @size, "updated_at" = @now
            WHERE "id" = @id AND "bytes" + @size < @limit
            """,
            new { id, size, limit, now = DateTime.UtcNow },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(DailyUploadLimitExceeded);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>回退当日上传额度（下限 0）。对应 Go: <c>Repository.ReleaseDailyUpload</c>。</summary>
    public async Task ReleaseDailyUploadAsync(
        string userId, string day, long size, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE "user_daily_upload_usages" SET
                "bytes" = CASE WHEN "bytes" >= @size THEN "bytes" - @size ELSE 0 END,
                "updated_at" = @now
            WHERE "id" = @id
            """,
            new { id = userId + ":" + day, size, now = DateTime.UtcNow },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
