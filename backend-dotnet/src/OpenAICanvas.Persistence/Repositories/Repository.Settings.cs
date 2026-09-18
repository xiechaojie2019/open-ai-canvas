using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 系统设置、第三方身份与用户更新的仓储方法。
/// 对应 Go 的 <c>repository.go</c> 中 SystemSetting / UserIdentity / Save 相关方法。
/// </summary>
public sealed partial class Repository
{
    // ---------------------------------------------------------------- 用户更新

    /// <summary>
    /// 按主键全量更新用户。对应 Go: <c>r.db.Save(user)</c>。
    /// </summary>
    public async Task SaveUserAsync(User user, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Update(typeof(User)),
            SqlBuilder.Parameters(user),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>按主键更新任意实体。对应 Go: <c>r.db.Save(value)</c>。</summary>
    public async Task SaveAsync<T>(T value, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Update(typeof(T)),
            SqlBuilder.Parameters(value!),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 第三方身份

    /// <summary>
    /// 按用户与提供方查第三方身份；不存在返回 null。
    /// 对应 Go: <c>r.UserIdentityForUser</c>（Go 返回 ErrRecordNotFound）。
    /// </summary>
    public async Task<UserIdentity?> UserIdentityForUserAsync(
        string userId,
        string provider,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<UserIdentity>(
            connection,
            SqlBuilder.Select<UserIdentity>(
                "user_id = @userId AND provider = @provider",
                limitOffset: " LIMIT 1"),
            new { userId, provider },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 系统设置

    /// <summary>
    /// 按键查系统设置；不存在返回 null。
    /// 对应 Go: <c>r.SystemSetting</c>（Go 返回 ErrRecordNotFound）。
    /// </summary>
    /// <remarks>
    /// 注意列名是 <c>key</c>，在两个数据库里都是保留字，必须加引号。
    /// </remarks>
    public async Task<SystemSetting?> SystemSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<SystemSetting>(
            connection,
            SqlBuilder.Select<SystemSetting>("\"key\" = @key", limitOffset: " LIMIT 1"),
            new { key },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 保存系统设置（存在则更新，不存在则插入）。对应 Go: <c>r.SaveSystemSetting</c> 的 upsert 语义。
    /// </summary>
    public async Task SaveSystemSettingAsync(SystemSetting setting, CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            int updated = await ExecuteAsync(
                connection,
                "UPDATE \"system_settings\" SET value_json = @ValueJSON, updated_by = @UpdatedBy, created_at = @CreatedAt, updated_at = @UpdatedAt WHERE \"key\" = @Key",
                new
                {
                    setting.Key,
                    setting.ValueJSON,
                    setting.UpdatedBy,
                    setting.CreatedAt,
                    setting.UpdatedAt,
                },
                transaction,
                cancellationToken).ConfigureAwait(false);

            if (updated > 0)
            {
                return;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                SqlBuilder.Insert(typeof(SystemSetting)),
                setting,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
