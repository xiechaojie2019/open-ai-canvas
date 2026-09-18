#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 项目角色资产（角色卡 / 形象表现 / 声音绑定）。对应 Go: <c>repository/project_character.go</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>
    /// 项目内的角色资产（JOIN 链接表并限定 character 分类）。对应 Go: <c>ProjectCharacterAsset</c>。
    /// </summary>
    /// <remarks>Go 用 <c>First</c>：未命中返回 not-found 错误；这里返回 null，由 service 决定投影。</remarks>
    public async Task<Asset?> ProjectCharacterAssetAsync(
        string userId, string projectId, string assetId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<Asset>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<Asset>("assets")}
            FROM assets
            JOIN project_asset_links ON project_asset_links.asset_id = assets.id
            WHERE assets.id = @assetId AND assets.user_id = @userId
              AND assets.category = @category AND project_asset_links.project_id = @projectId
            LIMIT 1
            """,
            new { assetId, userId, category = AssetCategory.AssetCategoryCharacter, projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 查资产版本。对应 Go: <c>AssetVersion</c>。</summary>
    public async Task<AssetVersion?> AssetVersionAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<AssetVersion>(
            connection,
            SqlBuilder.Select<AssetVersion>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>版本的抽象表现（视角、创建时间升序）。对应 Go: <c>AssetRepresentations</c>。</summary>
    public async Task<IReadOnlyList<AssetRepresentation>> AssetRepresentationsAsync(
        string assetVersionId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<AssetRepresentation>(
            connection,
            SqlBuilder.Select<AssetRepresentation>(
                "asset_version_id = @assetVersionId", "role ASC, created_at ASC"),
            new { assetVersionId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>版本绑定的声音。对应 Go: <c>CharacterVoiceBinding</c>。</summary>
    public async Task<CharacterVoiceBinding?> CharacterVoiceBindingAsync(
        string assetVersionId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<CharacterVoiceBinding>(
            connection,
            SqlBuilder.Select<CharacterVoiceBinding>(
                "asset_version_id = @assetVersionId", limitOffset: " LIMIT 1"),
            new { assetVersionId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按用户 + ID 查声音档案。对应 Go: <c>VoiceProfileForUser</c>。</summary>
    public async Task<VoiceProfile?> VoiceProfileForUserAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<VoiceProfile>(
            connection,
            SqlBuilder.Select<VoiceProfile>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按样本资源查声音档案。对应 Go: <c>VoiceProfileBySampleResource</c>。</summary>
    public async Task<VoiceProfile?> VoiceProfileBySampleResourceAsync(
        string userId, string sampleResourceId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<VoiceProfile>(
            connection,
            SqlBuilder.Select<VoiceProfile>(
                "user_id = @userId AND sample_resource_id = @sampleResourceId AND status = 'active'",
                limitOffset: " LIMIT 1"),
            new { userId, sampleResourceId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建声音档案。对应 Go: <c>CreateVoiceProfile</c>。</summary>
    public async Task CreateVoiceProfileAsync(
        VoiceProfile profile, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(VoiceProfile)), profile,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量播种内置声音档案，唯一键冲突时跳过。对应 Go: <c>EnsureVoiceProfiles</c>。
    /// </summary>
    public async Task EnsureVoiceProfilesAsync(
        IReadOnlyList<VoiceProfile> profiles, CancellationToken cancellationToken = default)
    {
        if (profiles.Count == 0)
        {
            return;
        }

        string statement = SqlBuilder.Insert(typeof(VoiceProfile))
            + Dialect.OnConflictDoNothing("\"user_id\", \"provider\", \"voice_key\"");
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (VoiceProfile profile in profiles)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                statement, profile, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 角色创建事务：资产、首版本与项目链接同事务落库，并递增项目版本号
    /// （updated_at 取资产时间）。对应 Go: <c>CreateProjectCharacter</c>。
    /// </summary>
    public async Task CreateProjectCharacterAsync(
        string projectId,
        Asset asset,
        AssetVersion version,
        ProjectAssetLink link,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection, SqlBuilder.Insert<Asset>(), asset, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection, SqlBuilder.Insert<AssetVersion>(), version, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection, SqlBuilder.Insert<ProjectAssetLink>(), link, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE projects SET revision = revision + 1, updated_at = @updatedAt
            WHERE id = @projectId
            """,
            new { updatedAt = asset.UpdatedAt, projectId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 角色版本替换事务：新版本、表现、声音同事务写入并更新资产域字段（含标题与载荷），
    /// 命中 0 行视为资产不存在。对应 Go: <c>SaveCharacterVersion</c> / <c>saveCharacterVersion</c>。
    /// </summary>
    public async Task SaveCharacterVersionAsync(
        string projectId,
        Asset asset,
        AssetVersion version,
        IReadOnlyList<AssetRepresentation> representations,
        CharacterVoiceBinding? voice,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection, SqlBuilder.Insert<AssetVersion>(), version, transaction, cancellationToken).ConfigureAwait(false);
        foreach (AssetRepresentation representation in representations)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<AssetRepresentation>(), representation, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        if (voice is not null)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<CharacterVoiceBinding>(), voice, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        int assetAffected = await ExecuteAsync(
            connection,
            """
            UPDATE assets SET
                kind = @Kind, category = @Category, status = @Status,
                primary_version_id = @PrimaryVersionID, title = @Title,
                payload_json = @PayloadJSON, updated_at = @UpdatedAt
            WHERE id = @ID AND user_id = @UserID
            """,
            asset,
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (assetAffected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("record not found");
        }

        await BumpProjectRevisionAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
