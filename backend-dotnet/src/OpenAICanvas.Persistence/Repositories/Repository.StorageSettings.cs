using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>对象存储设置及其测试记录的持久化操作。</summary>
public sealed partial class Repository
{
    public async Task<UserOSSSetting?> LatestUserOSSSettingAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<UserOSSSetting>(connection,
            SqlBuilder.Select<UserOSSSetting>("user_id = @userId", "updated_at DESC, id DESC", " LIMIT 1"),
            new { userId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateUserOSSSettingAsync(
        UserOSSSetting setting, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(SqlBuilder.Insert(typeof(UserOSSSetting)), setting,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<StorageLocation?> StorageLocationByDigestAsync(
        string scope, string ownerId, string provider, string digest,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<StorageLocation>(connection,
            SqlBuilder.Select<StorageLocation>(
                "scope = @scope AND owner_id = @ownerId AND provider = @provider AND location_digest = @digest",
                limitOffset: " LIMIT 1"),
            new { scope, ownerId, provider, digest }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateStorageLocationAsync(
        StorageLocation location, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(SqlBuilder.Insert(typeof(StorageLocation)), location,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task SaveStorageLocationAsync(
        StorageLocation location, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(SqlBuilder.Update(typeof(StorageLocation)),
            SqlBuilder.Parameters(location), cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task ActivateStorageLocationAsync(
        string scope, string ownerId, string id, bool active, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection,
            "UPDATE \"storage_locations\" SET active = FALSE, updated_at = @now WHERE scope = @scope AND owner_id = @ownerId",
            new { scope, ownerId, now = DateTime.UtcNow }, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection,
            "UPDATE \"storage_locations\" SET active = @active, updated_at = @now WHERE id = @id AND scope = @scope AND owner_id = @ownerId",
            new { id, scope, ownerId, active, now = DateTime.UtcNow }, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> StorageLocationHistoryCountAsync(
        string scope, string ownerId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM \"storage_locations\" WHERE scope = @scope AND owner_id = @ownerId",
            new { scope, ownerId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> StorageLocationResourceCountAsync(
        string storageLocationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storageLocationId))
        {
            return 0;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM \"resources\" WHERE storage_setting_id = @storageLocationId",
            new { storageLocationId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
