#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>画布分享仓储方法。对应 Go: <c>repository.CanvasShare*</c>。</summary>
public sealed partial class Repository
{
    /// <summary>按项目查分享。对应 Go: <c>CanvasShareForProject</c>。</summary>
    public async Task<CanvasShare?> CanvasShareForProjectAsync(
        string userId, string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<CanvasShare>(
            connection,
            SqlBuilder.Select<CanvasShare>("user_id = @userId AND project_id = @projectId", limitOffset: " LIMIT 1"),
            new { userId, projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按令牌哈希查启用的分享。对应 Go: <c>CanvasShareByTokenHash</c>。</summary>
    public async Task<CanvasShare?> CanvasShareByTokenHashAsync(
        string tokenHash, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<CanvasShare>(
            connection,
            SqlBuilder.Select<CanvasShare>(
                SoftDelete.Apply("canvas_shares", "token_hash = @tokenHash AND enabled = @enabled"),
                limitOffset: " LIMIT 1"),
            new { tokenHash, enabled = Dialect.Boolean(true) },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除项目分享。对应 Go: <c>DeleteCanvasShare</c>。</summary>
    public async Task DeleteCanvasShareByProjectAsync(
        string userId, string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM \"canvas_shares\" WHERE \"user_id\" = @userId AND \"project_id\" = @projectId",
            new { userId, projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按主键 upsert 分享。对应 Go: <c>repo.Save(share)</c>（UpdateOrCreate）。</summary>
    public async Task SaveCanvasShareAsync(
        CanvasShare share, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            "UPDATE \"canvas_shares\" SET \"project_id\" = @ProjectID, \"token_hash\" = @TokenHash, \"token_cipher\" = @TokenCipher, \"enabled\" = @Enabled, \"expires_at\" = @ExpiresAt, \"created_at\" = @CreatedAt WHERE \"id\" = @ID",
            share,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (updated > 0)
        {
            return;
        }
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(CanvasShare)),
            share,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>按用户 + ID 查资源。对应 Go: <c>ResourceForUser</c>。</summary>
    public async Task<Resource?> ResourceForUserAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<Resource>(
            connection,
            SqlBuilder.Select<Resource>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
