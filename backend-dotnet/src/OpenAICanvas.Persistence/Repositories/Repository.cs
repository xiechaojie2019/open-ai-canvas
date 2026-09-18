using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 仓储门面。对应 Go 的 <c>repository.Repository</c>——Go 用同包多文件扩展同一个 struct，
/// C# 用 <c>partial class</c> 保持同样的组织方式：一个文件对应一个 Go 文件。
/// </summary>
public sealed partial class Repository : RepositoryBase
{
    public Repository(CanvasDatabase database) : base(database)
    {
    }

    /// <summary>对应 Go: <c>r.Dialect()</c>。</summary>
    public string DialectName => Dialect.IsPostgres ? "postgres" : "sqlite";

    // ---------------------------------------------------------------- 通用入口

    /// <summary>低层兼容入口；业务写路径应优先使用带领域约束的显式方法。</summary>
    public async Task CreateAsync<T>(T value, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(T)), value, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>对应 Go: <c>NextPrefixedID</c>。在事务中递增序列。</summary>
    public async Task<string> NextPrefixedIdAsync(string prefix, CancellationToken cancellationToken = default)
    {
        string normalized = prefix.Trim().ToUpperInvariant();
        if (normalized.Length == 0 || normalized.Length > 16)
        {
            throw new InvalidOperationException("invalid id prefix");
        }

        string sequence = "id:" + normalized;

        return await InTransactionAsync(async (connection, transaction) =>
        {
            // 先确保序列行存在（并发下靠唯一键冲突忽略），再原子自增。
            await connection.ExecuteAsync(new CommandDefinition(
                $"INSERT INTO \"id_sequences\" (name, value, updated_at) VALUES (@name, 0, @now){Dialect.OnConflictDoNothing(string.Empty)}",
                new { name = sequence, now = DateTime.UtcNow },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE \"id_sequences\" SET value = value + 1, updated_at = @now WHERE name = @name",
                new { name = sequence, now = DateTime.UtcNow },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            long value = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT value FROM \"id_sequences\" WHERE name = @name",
                new { name = sequence },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            return $"{normalized}_{value:D6}";
        }, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 用户

    public async Task<long> UserCountAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM \"users\"", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<User?> UserAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<User>(
            connection,
            SqlBuilder.Select<User>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按用户名或邮箱（均忽略大小写）查用户。</summary>
    public async Task<User?> UserByAccountAsync(string account, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<User>(
            connection,
            SqlBuilder.Select<User>("lower(username) = lower(@account) OR lower(email) = lower(@account)", limitOffset: " LIMIT 1"),
            new { account },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<User?> UserByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<User>(
            connection,
            SqlBuilder.Select<User>("lower(username) = lower(@username)", limitOffset: " LIMIT 1"),
            new { username },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按邮箱查用户；空邮箱不参与匹配（与 Go 的 <c>email &lt;&gt; ''</c> 一致）。</summary>
    public async Task<User?> UserByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<User>(
            connection,
            SqlBuilder.Select<User>("email <> '' AND lower(email) = lower(@email)", limitOffset: " LIMIT 1"),
            new { email },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<User>> UsersAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<User>(
            connection,
            SqlBuilder.Select<User>(orderBy: "created_at DESC"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>管理后台用户列表（关键字/角色/状态筛选 + 分页）。</summary>
    public async Task<(IReadOnlyList<User> Users, long Total)> AdminUsersAsync(
        string keyword,
        string role,
        string status,
        long limit,
        long offset,
        CancellationToken cancellationToken = default)
    {
        List<string> conditions = [];
        DynamicParameters parameters = new();

        string trimmed = keyword.Trim();
        if (trimmed.Length > 0)
        {
            conditions.Add("(lower(username) LIKE @pattern OR lower(display_name) LIKE @pattern OR lower(email) LIKE @pattern)");
            parameters.Add("pattern", "%" + trimmed.ToLowerInvariant() + "%");
        }

        if (role is "admin" or "user")
        {
            conditions.Add("role = @role");
            parameters.Add("role", role);
        }

        if (status is "active" or "disabled")
        {
            conditions.Add("status = @status");
            parameters.Add("status", status);
        }

        string where = conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long total = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"users\"" + where,
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        parameters.Add("limit", limit);
        parameters.Add("offset", offset);
        IReadOnlyList<User> users = await QueryAsync<User>(
            connection,
            SqlBuilder.Select<User>(where.Length == 0 ? null : where[7..], "created_at DESC", Dialect.LimitOffset(limit, offset)),
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (users, total);
    }

    /// <summary>管理员引用下拉：只取 id / username / display_name 三列。</summary>
    public async Task<IReadOnlyList<User>> AdminUserReferencesAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<User>(
            connection,
            SqlBuilder.SelectColumns<User>(["ID", "Username", "DisplayName", "Role", "Status", "Email", "CreatedAt", "UpdatedAt"], orderBy: "created_at DESC", limitOffset: " LIMIT 100"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>统计除指定用户之外仍然有效的管理员数量，用于防止禁用最后一个管理员。</summary>
    public async Task<long> ActiveAdminCountExcludingAsync(string userId, CancellationToken cancellationToken = default)
    {
        string condition = "role = @role AND status = @status";
        DynamicParameters parameters = new();
        parameters.Add("role", "admin");
        parameters.Add("status", "active");

        if (userId.Length > 0)
        {
            condition += " AND id <> @userId";
            parameters.Add("userId", userId);
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"users\" WHERE " + condition,
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 会话

    public async Task<AuthSession?> AuthSessionAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<AuthSession>(
            connection,
            SqlBuilder.Select<AuthSession>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAuthSessionAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "DELETE FROM \"auth_sessions\" WHERE id = @id", new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteExpiredAuthSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "DELETE FROM \"auth_sessions\" WHERE expires_at <= @now",
            new { now = DateTime.UtcNow }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteUserAuthSessionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "DELETE FROM \"auth_sessions\" WHERE user_id = @userId", new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 邮箱验证码

    public async Task<EmailVerificationCode?> LatestEmailVerificationCodeAsync(
        string email,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<EmailVerificationCode>(
            connection,
            SqlBuilder.Select<EmailVerificationCode>("email = @email AND purpose = @purpose AND used_at IS NULL", "created_at DESC", " LIMIT 1"),
            new { email, purpose },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把未使用的验证码标记为已用；返回是否命中一行。</summary>
    public async Task<bool> MarkEmailVerificationCodeUsedAsync(
        string id,
        DateTime usedAt,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await ExecuteAsync(
            connection,
            "UPDATE \"email_verification_codes\" SET used_at = @usedAt WHERE id = @id AND used_at IS NULL",
            new { id, usedAt },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }

    public async Task DeleteEmailVerificationCodeAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "DELETE FROM \"email_verification_codes\" WHERE id = @id", new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteExpiredEmailVerificationCodesAsync(
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM \"email_verification_codes\" WHERE expires_at <= @now OR used_at IS NOT NULL",
            new { now },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 消费验证码并创建用户，两步必须在同一事务内，且验证码必须恰好命中一行。
    /// </summary>
    public async Task CreateUserWithEmailVerificationAsync(
        User user,
        string verificationCodeId,
        DateTime usedAt,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            int consumed = await ExecuteAsync(
                connection,
                "UPDATE \"email_verification_codes\" SET used_at = @usedAt WHERE id = @id AND used_at IS NULL AND expires_at > @usedAt",
                new { id = verificationCodeId, usedAt },
                transaction,
                cancellationToken).ConfigureAwait(false);

            if (consumed != 1)
            {
                throw new InvalidOperationException("email verification code is no longer valid");
            }

            await connection.ExecuteAsync(new CommandDefinition(
                SqlBuilder.Insert(typeof(User)), user, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 重置口令：消费验证码 → 更新口令 → 失效全部会话 → 作废该邮箱下其余未用验证码。
    /// 任一步不满足即整笔回滚，与 Go 的事务语义一致。
    /// </summary>
    public async Task ResetUserPasswordWithEmailVerificationAsync(
        string userId,
        string email,
        string purpose,
        string verificationCodeId,
        string passwordHash,
        DateTime usedAt,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            int consumed = await ExecuteAsync(
                connection,
                "UPDATE \"email_verification_codes\" SET used_at = @usedAt WHERE id = @id AND email = @email AND purpose = @purpose AND used_at IS NULL AND expires_at > @usedAt",
                new { id = verificationCodeId, email, purpose, usedAt },
                transaction,
                cancellationToken).ConfigureAwait(false);

            if (consumed != 1)
            {
                throw new AppError(400, "email verification code is no longer valid");
            }

            int updated = await ExecuteAsync(
                connection,
                "UPDATE \"users\" SET password_hash = @passwordHash, updated_at = @usedAt WHERE id = @userId AND email <> '' AND lower(email) = lower(@email) AND status = @status AND password_hash <> ''",
                new { passwordHash, usedAt, userId, email, status = "active" },
                transaction,
                cancellationToken).ConfigureAwait(false);

            if (updated != 1)
            {
                throw new AppError(400, "email verification code is no longer valid");
            }

            await ExecuteAsync(connection, "DELETE FROM \"auth_sessions\" WHERE user_id = @userId",
                new { userId }, transaction, cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                "UPDATE \"email_verification_codes\" SET used_at = @usedAt WHERE email = @email AND purpose = @purpose AND used_at IS NULL",
                new { email, purpose, usedAt },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
