#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

public sealed partial class Repository
{
    // ------------------------------------------------------------
    // OAuth 状态
    // ------------------------------------------------------------

    public async Task CreateOAuthStateAsync(OAuthState state, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            INSERT INTO oauth_states (id, provider, state_hash, code_verifier, next_path, expires_at, used_at, created_at)
            VALUES (@ID, @Provider, @StateHash, @CodeVerifier, @NextPath, @ExpiresAt, @UsedAt, @CreatedAt)
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql, state, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// 消费（查找并标记已用）一个 OAuth state。对应 Go: <c>Repository.ConsumeOAuthState</c>。
    /// 使用乐观并发：UPDATE … WHERE used_at IS NULL，然后检查 affected rows。
    /// </summary>
    public async Task<OAuthState?> ConsumeOAuthStateAsync(
        string provider,
        string stateHash,
        CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            const string selectSql = """
                SELECT id, provider, state_hash, code_verifier, next_path, expires_at, used_at, created_at
                FROM oauth_states
                WHERE provider = @Provider AND state_hash = @StateHash
                  AND used_at IS NULL AND expires_at > @Now
                LIMIT 1
                """;

            OAuthState? state = await connection.QueryFirstOrDefaultAsync<OAuthState>(
                new CommandDefinition(selectSql, new { Provider = provider, StateHash = stateHash, Now = DateTime.UtcNow },
                    transaction: transaction, cancellationToken: cancellationToken));

            if (state is null)
            {
                return null;
            }

            DateTime now = DateTime.UtcNow;
            const string updateSql = """
                UPDATE oauth_states SET used_at = @Now
                WHERE id = @ID AND used_at IS NULL
                """;

            int affected = await connection.ExecuteAsync(
                new CommandDefinition(updateSql, new { Now = now, state.ID },
                    transaction: transaction, cancellationToken: cancellationToken));

            if (affected != 1)
            {
                return null;
            }

            state.UsedAt = now;
            return state;
        }, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------
    // 用户身份
    // ------------------------------------------------------------

    public async Task<UserIdentity?> UserIdentityAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT id, user_id, provider, subject, provider_username, avatar_url, created_at, updated_at
            FROM user_identities
            WHERE provider = @Provider AND subject = @Subject
            LIMIT 1
            """;
        return await connection.QueryFirstOrDefaultAsync<UserIdentity>(
            new CommandDefinition(sql, new { Provider = provider, Subject = subject },
                cancellationToken: cancellationToken));
    }

    /// <summary>
    /// 在事务中创建 OAuth 用户：user → identity → credit_account。
    /// 对应 Go: <c>Repository.CreateOAuthUser</c>。
    /// </summary>
    public async Task CreateOAuthUserAsync(
        User user,
        UserIdentity identity,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(
                new CommandDefinition(SqlBuilder.Insert(typeof(User)), user, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(
                new CommandDefinition(SqlBuilder.Insert(typeof(UserIdentity)), identity, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(
                new CommandDefinition(SqlBuilder.Insert(typeof(CreditAccount)), new CreditAccount
                {
                    UserID = user.ID,
                    AvailableMicrocredits = 0,
                    ReservedMicrocredits = 0,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }, transaction, cancellationToken: cancellationToken));
        }, cancellationToken).ConfigureAwait(false);
    }
}
