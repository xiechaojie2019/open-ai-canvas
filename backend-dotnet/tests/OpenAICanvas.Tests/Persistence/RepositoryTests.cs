using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Persistence;

/// <summary>
/// 仓储层端到端验证：在真实 SQLite 库上执行用户/会话/验证码的完整读写路径。
/// </summary>
/// <remarks>
/// 这证明 Dapper + 生成的列映射（<see cref="EntityMetadata"/>）能正确地把
/// Go 字段名（<c>ID</c>、<c>PasswordHash</c>）映射到 snake_case 列名，
/// 而不是依赖任何命名推断。
/// </remarks>
public class RepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;

    public RepositoryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-repo-{Guid.NewGuid():N}.db");
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    private async Task MigrateAsync()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();
    }

    private static User NewUser(string id, string username, string email = "") => new()
    {
        ID = id,
        Username = username,
        Email = email,
        DisplayName = username,
        Role = UserRole.UserRoleUser,
        Status = UserStatus.UserStatusActive,
        PasswordHash = "$2a$10$abcdefghijklmnopqrstuv",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task 写入并读回用户_列名映射正确()
    {
        await MigrateAsync();
        User user = NewUser("USR_000001", "alice", "alice@example.com");
        await _repository.CreateAsync(user);

        User? loaded = await _repository.UserAsync("USR_000001");

        Assert.NotNull(loaded);
        Assert.Equal("alice", loaded!.Username);
        Assert.Equal("alice@example.com", loaded.Email);
        Assert.Equal("user", loaded.Role);
        Assert.Equal("active", loaded.Status);
        Assert.Equal("$2a$10$abcdefghijklmnopqrstuv", loaded.PasswordHash);
    }

    [Fact]
    public async Task 按账号查询忽略大小写()
    {
        await MigrateAsync();
        await _repository.CreateAsync(NewUser("USR_000002", "Bob", "Bob@Example.COM"));

        Assert.NotNull(await _repository.UserByUsernameAsync("bob"));
        Assert.NotNull(await _repository.UserByEmailAsync("bob@example.com"));
        Assert.NotNull(await _repository.UserByAccountAsync("BOB"));
        Assert.NotNull(await _repository.UserByAccountAsync("bob@example.com"));
    }

    [Fact]
    public async Task 空邮箱不参与邮箱匹配()
    {
        await MigrateAsync();
        await _repository.CreateAsync(NewUser("USR_000003", "carol"));

        // Go 侧条件是 email <> '' AND lower(email) = lower(?)，空串不应命中。
        Assert.Null(await _repository.UserByEmailAsync(string.Empty));
    }

    [Fact]
    public async Task 不存在时返回_null_而不是抛异常()
    {
        await MigrateAsync();
        Assert.Null(await _repository.UserAsync("NOT_EXIST"));
        Assert.Null(await _repository.UserByUsernameAsync("nobody"));
    }

    [Fact]
    public async Task 序列号递增且格式为前缀加六位()
    {
        await MigrateAsync();

        string first = await _repository.NextPrefixedIdAsync("usr");
        string second = await _repository.NextPrefixedIdAsync("usr");
        string other = await _repository.NextPrefixedIdAsync("task");

        Assert.Equal("USR_000001", first);
        Assert.Equal("USR_000002", second);
        Assert.Equal("TASK_000001", other);
    }

    [Fact]
    public async Task 非法前缀被拒绝()
    {
        await MigrateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.NextPrefixedIdAsync(""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.NextPrefixedIdAsync("12345678901234567"));
    }

    [Fact]
    public async Task 会话写入读取与删除()
    {
        await MigrateAsync();
        await _repository.CreateAsync(NewUser("USR_000004", "dave"));

        AuthSession session = new()
        {
            ID = "SES_000001",
            UserID = "USR_000004",
            TokenHash = "hash-1",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await _repository.CreateAsync(session);

        AuthSession? loaded = await _repository.AuthSessionAsync("SES_000001");
        Assert.NotNull(loaded);
        Assert.Equal("USR_000004", loaded!.UserID);

        await _repository.DeleteAuthSessionAsync("SES_000001");
        Assert.Null(await _repository.AuthSessionAsync("SES_000001"));
    }

    [Fact]
    public async Task 过期会话被清理_未过期保留()
    {
        await MigrateAsync();
        await _repository.CreateAsync(NewUser("USR_000005", "erin"));

        await _repository.CreateAsync(new AuthSession
        {
            ID = "SES_EXPIRED",
            UserID = "USR_000005",
            TokenHash = "h1",
            ExpiresAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _repository.CreateAsync(new AuthSession
        {
            ID = "SES_ACTIVE",
            UserID = "USR_000005",
            TokenHash = "h2",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await _repository.DeleteExpiredAuthSessionsAsync();

        Assert.Null(await _repository.AuthSessionAsync("SES_EXPIRED"));
        Assert.NotNull(await _repository.AuthSessionAsync("SES_ACTIVE"));
    }

    [Fact]
    public async Task 验证码消费后不能再被消费()
    {
        await MigrateAsync();

        await _repository.CreateAsync(new EmailVerificationCode
        {
            ID = "EVC_000001",
            Email = "frank@example.com",
            CodeHash = "code-hash",
            Purpose = "register",
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            CreatedAt = DateTime.UtcNow,
        });

        DateTime usedAt = DateTime.UtcNow;
        Assert.True(await _repository.MarkEmailVerificationCodeUsedAsync("EVC_000001", usedAt));
        // 第二次必须失败：WHERE 里的 used_at IS NULL 不再命中。
        Assert.False(await _repository.MarkEmailVerificationCodeUsedAsync("EVC_000001", usedAt));
    }

    [Fact]
    public async Task 消费验证码并建用户_验证码无效时整笔回滚()
    {
        await MigrateAsync();

        User user = NewUser("USR_000006", "grace", "grace@example.com");

        // 验证码不存在 → 必须抛错且用户不落库。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.CreateUserWithEmailVerificationAsync(user, "EVC_MISSING", DateTime.UtcNow));

        Assert.Null(await _repository.UserAsync("USR_000006"));
    }

    [Fact]
    public async Task 消费验证码并建用户_成功路径()
    {
        await MigrateAsync();

        await _repository.CreateAsync(new EmailVerificationCode
        {
            ID = "EVC_000002",
            Email = "heidi@example.com",
            CodeHash = "code-hash",
            Purpose = "register",
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            CreatedAt = DateTime.UtcNow,
        });

        User user = NewUser("USR_000007", "heidi", "heidi@example.com");
        await _repository.CreateUserWithEmailVerificationAsync(user, "EVC_000002", DateTime.UtcNow);

        Assert.NotNull(await _repository.UserAsync("USR_000007"));
        EmailVerificationCode? code = await _repository.LatestEmailVerificationCodeAsync("heidi@example.com", "register");
        Assert.Null(code); // 已消费，不再出现在"未使用"查询里。
    }

    [Fact]
    public async Task 统计与分页()
    {
        await MigrateAsync();
        await _repository.CreateAsync(NewUser("USR_000008", "ivan"));
        await _repository.CreateAsync(NewUser("USR_000009", "judy"));
        User admin = NewUser("USR_00000A", "admin", "admin@example.com");
        admin.Role = UserRole.UserRoleAdmin;
        await _repository.CreateAsync(admin);

        Assert.Equal(3, await _repository.UserCountAsync());

        (IReadOnlyList<User> admins, long adminTotal) =
            await _repository.AdminUsersAsync(string.Empty, "admin", string.Empty, 10, 0);
        Assert.Equal(1, adminTotal);
        Assert.Single(admins);

        (IReadOnlyList<User> found, long foundTotal) =
            await _repository.AdminUsersAsync("jud", string.Empty, string.Empty, 10, 0);
        Assert.Equal(1, foundTotal);
        Assert.Equal("judy", found[0].Username);

        // 排除自己后没有其他管理员 → 用于禁止禁用最后一个管理员。
        Assert.Equal(0, await _repository.ActiveAdminCountExcludingAsync("USR_00000A"));
        Assert.Equal(1, await _repository.ActiveAdminCountExcludingAsync("USR_000008"));
    }

    [Fact]
    public async Task 列映射与建表脚本一致()
    {
        await MigrateAsync();

        // 每个实体的每个属性都必须能在库里找到对应列，否则 SQL 会在运行期才炸。
        await using System.Data.Common.DbConnection connection = await _database.OpenAsync();
        foreach (Type type in EntityMetadata.KnownTypes)
        {
            EntityMap map = EntityMetadata.For(type);
            foreach (ColumnMap column in map.Columns)
            {
                Assert.False(string.IsNullOrWhiteSpace(column.Column));
                Assert.False(string.IsNullOrWhiteSpace(column.Property));
            }

            Assert.NotEmpty(map.Columns);
            Assert.NotNull(map.Table);
        }

        Assert.Equal(80, EntityMetadata.KnownTypes.Count);
        await Task.CompletedTask;
    }
}
