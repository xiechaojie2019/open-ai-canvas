#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 插件平台状态与用户安装状态的仓储方法。
/// 对应 Go: <c>repository/plugin_states.go</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>按插件 ID 查平台状态；不存在返回 null。对应 Go: <c>PluginPlatformState</c>。</summary>
    public async Task<PluginPlatformState?> PluginPlatformStateAsync(
        string pluginID, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<PluginPlatformState>(
            connection,
            SqlBuilder.Select<PluginPlatformState>("plugin_id = @pluginID", limitOffset: " LIMIT 1"),
            new { pluginID },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>列出全部平台状态（plugin_id 升序）。对应 Go: <c>PluginPlatformStates</c>。</summary>
    public async Task<IReadOnlyList<PluginPlatformState>> PluginPlatformStatesAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<PluginPlatformState>(
            connection,
            SqlBuilder.Select<PluginPlatformState>(orderBy: "plugin_id ASC"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 保存平台状态（存在则更新，不存在则插入）。对应 Go: <c>r.Save(&state)</c> 的 upsert 语义。
    /// </summary>
    public async Task SavePluginPlatformStateAsync(
        PluginPlatformState state, CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            int updated = await ExecuteAsync(
                connection,
                "UPDATE plugin_platform_states SET available = @Available, updated_by = @UpdatedBy, created_at = @CreatedAt, updated_at = @UpdatedAt WHERE plugin_id = @PluginID",
                new
                {
                    state.PluginID,
                    state.Available,
                    state.UpdatedBy,
                    state.CreatedAt,
                    state.UpdatedAt,
                },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (updated > 0)
            {
                return;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                SqlBuilder.Insert(typeof(PluginPlatformState)),
                state,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按用户与插件 ID 查安装状态；不存在返回 null。对应 Go: <c>UserPluginState</c>。</summary>
    public async Task<UserPluginState?> UserPluginStateAsync(
        string userID, string pluginID, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<UserPluginState>(
            connection,
            SqlBuilder.Select<UserPluginState>(
                "user_id = @userID AND plugin_id = @pluginID",
                limitOffset: " LIMIT 1"),
            new { userID, pluginID },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>列出用户全部安装状态（plugin_id 升序）。对应 Go: <c>UserPluginStates</c>。</summary>
    public async Task<IReadOnlyList<UserPluginState>> UserPluginStatesAsync(
        string userID, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<UserPluginState>(
            connection,
            SqlBuilder.Select<UserPluginState>(
                "user_id = @userID",
                orderBy: "plugin_id ASC"),
            new { userID },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保存用户安装状态（按 user_id + plugin_id upsert）。对应 Go: <c>r.Save(&state)</c>。</summary>
    public async Task SaveUserPluginStateAsync(
        UserPluginState state, CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            int updated = await ExecuteAsync(
                connection,
                "UPDATE user_plugin_states SET enabled = @Enabled, updated_at = @UpdatedAt WHERE user_id = @UserID AND plugin_id = @PluginID",
                new
                {
                    state.UserID,
                    state.PluginID,
                    state.Enabled,
                    state.UpdatedAt,
                },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (updated > 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(state.ID))
            {
                state.ID = IdGenerator.NewId();
            }

            await connection.ExecuteAsync(new CommandDefinition(
                SqlBuilder.Insert(typeof(UserPluginState)),
                state,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>各插件的用户启用计数（仅 enabled 行）。对应 Go: <c>EnabledPluginUserCounts</c>。</summary>
    public async Task<Dictionary<string, long>> EnabledPluginUserCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<(string PluginID, long EnabledCount)> rows = await connection.QueryAsync<(string, long)>(
            new CommandDefinition(
                // enabled 在 PG 是 boolean、SQLite 是 numeric，参数化布尔两端同源。
                "SELECT plugin_id AS PluginID, COUNT(*) AS EnabledCount FROM user_plugin_states WHERE enabled = @enabled GROUP BY plugin_id",
                new { enabled = true },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToDictionary(row => row.PluginID, row => row.EnabledCount, StringComparer.Ordinal);
    }

    /// <summary>删除插件全部用户状态（卸载时清理）。对应 Go: <c>DeleteUserPluginStates</c>。</summary>
    public async Task DeleteUserPluginStatesAsync(
        string pluginID, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_plugin_states WHERE plugin_id = @pluginID",
            new { pluginID },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>删除插件平台状态行（卸载时清理）。返回是否删除了行。</summary>
    public async Task<bool> DeletePluginPlatformStateAsync(
        string pluginID, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM plugin_platform_states WHERE plugin_id = @pluginID",
            new { pluginID },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }
}
