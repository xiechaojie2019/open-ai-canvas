#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>风格档案仓储。对应 Go: <c>repository/style_profile.go</c>。</summary>
public sealed partial class Repository
{
    /// <summary>用户风格列表（收藏优先、更新时间倒序）。对应 Go: <c>StyleProfiles</c>。</summary>
    public async Task<IReadOnlyList<StyleProfile>> StyleProfilesAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<StyleProfile>(
            connection,
            SqlBuilder.Select<StyleProfile>("user_id = @userId", "favorite DESC, updated_at DESC"),
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>用户风格数量（上限 200）。对应 Go: <c>StyleProfileCount</c>。</summary>
    public async Task<long> StyleProfileCountAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"style_profiles\" WHERE \"user_id\" = @userId",
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按用户 + ID 查风格。对应 Go: <c>StyleProfileForUser</c>。</summary>
    public async Task<StyleProfile?> StyleProfileForUserAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<StyleProfile>(
            connection,
            SqlBuilder.Select<StyleProfile>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建风格。对应 Go: <c>CreateStyleProfile</c>。</summary>
    public async Task CreateStyleProfileAsync(
        StyleProfile profile, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(StyleProfile)), profile,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>更新风格内容。对应 Go: <c>UpdateStyleProfile</c>。</summary>
    public async Task UpdateStyleProfileAsync(
        StyleProfile profile, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE "style_profiles" SET "name" = @Name, "description" = @Description,
              "cover_url" = @CoverURL, "tags_json" = @TagsJSON, "profile_json" = @ProfileJSON,
              "revision" = @Revision, "updated_at" = @UpdatedAt
            WHERE "id" = @ID AND "user_id" = @UserID
            """,
            profile,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>设置收藏（命中 0 行视为不存在）。对应 Go: <c>SetStyleProfileFavorite</c>。</summary>
    public async Task<bool> SetStyleProfileFavoriteAsync(
        string userId, string id, bool favorite, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            "UPDATE \"style_profiles\" SET \"favorite\" = @favorite WHERE \"id\" = @id AND \"user_id\" = @userId",
            new { favorite, id, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated > 0;
    }

    /// <summary>记录最近使用（命中 0 行视为不存在）。对应 Go: <c>TouchStyleProfile</c>。</summary>
    public async Task<bool> TouchStyleProfileAsync(
        string userId, string id, DateTime usedAt, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            "UPDATE \"style_profiles\" SET \"last_used_at\" = @usedAt WHERE \"id\" = @id AND \"user_id\" = @userId",
            new { usedAt, id, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated > 0;
    }

    /// <summary>删除风格（命中 0 行视为不存在）。对应 Go: <c>DeleteStyleProfile</c>。</summary>
    public async Task<bool> DeleteStyleProfileAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int deleted = await ExecuteAsync(
            connection,
            "DELETE FROM \"style_profiles\" WHERE \"id\" = @id AND \"user_id\" = @userId",
            new { id, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return deleted > 0;
    }

    /// <summary>用户声音档案列表。对应 Go: <c>VoiceProfiles</c>。</summary>
    public async Task<IReadOnlyList<VoiceProfile>> VoiceProfilesAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<VoiceProfile>(
            connection,
            SqlBuilder.Select<VoiceProfile>("user_id = @userId", "created_at ASC"),
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}