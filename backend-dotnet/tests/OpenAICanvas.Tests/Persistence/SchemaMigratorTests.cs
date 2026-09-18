using Dapper;
using Microsoft.Data.Sqlite;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Persistence;

/// <summary>
/// 迁移运行器的行为验证：全新库、幂等重跑、版本校验与失败路径。
/// </summary>
public class SchemaMigratorTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;

    public SchemaMigratorTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-migrate-{Guid.NewGuid():N}.db");
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
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

    [Fact]
    public async Task 全新库迁移后写入全部_15_条迁移记录()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();

        await using SqliteConnection connection = (SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();

        List<(long Version, string Name, string Checksum)> records = (await connection.QueryAsync<(long, string, string)>(
                "SELECT version, name, checksum FROM schema_migrations ORDER BY version"))
            .ToList();

        Assert.Equal(15, records.Count);
        Assert.Equal(Enumerable.Range(1, 15).Select(v => (long)v), records.Select(r => r.Version));

        // 名称与校验和必须与 Go 完全一致，否则无法接管 Go 版已迁移的数据库。
        Assert.Equal("baseline_gorm_schema", records[0].Name);
        Assert.Equal("sha256:open-ai-canvas-schema-v1-20260830", records[0].Checksum);
        Assert.Equal("agent_profiles", records[14].Name);
        Assert.Equal("sha256:agent-profiles-v15-20260914", records[14].Checksum);
    }

    [Fact]
    public async Task 迁移后结构状态为就绪()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();

        SchemaStatus status = await migrator.ReadStatusAsync();

        Assert.Equal(15, status.Current);
        Assert.Equal(15, status.Expected);
        Assert.True(status.Ready);
    }

    [Fact]
    public async Task 重复迁移是幂等的()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        await using SqliteConnection connection = (SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();
        long count = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations");

        Assert.Equal(15, count);
    }

    [Fact]
    public async Task 空库状态为未就绪()
    {
        SchemaMigrator migrator = new(_database);
        SchemaStatus status = await migrator.ReadStatusAsync();

        Assert.Equal(0, status.Current);
        Assert.Equal(15, status.Expected);
        Assert.False(status.Ready);
    }

    [Fact]
    public async Task 空库直接校验版本会报结构过旧()
    {
        SchemaMigrator migrator = new(_database);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => migrator.RequireSchemaVersionAsync());

        Assert.Contains("数据库结构版本过旧", error.Message, StringComparison.Ordinal);
        Assert.Contains("请先执行 migrate-schema up", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 校验和不匹配时拒绝启动()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();

        // 篡改一条已发布迁移的校验和，模拟"改动了已发布迁移实现"。
        await using (SqliteConnection connection = (SqliteConnection)_database.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "UPDATE schema_migrations SET checksum = 'sha256:tampered' WHERE version = 8");
        }

        SchemaMigrator fresh = new(_database);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fresh.RequireSchemaVersionAsync());

        Assert.Contains("校验和不一致", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 迁移名称不匹配时拒绝启动()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();

        await using (SqliteConnection connection = (SqliteConnection)_database.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "UPDATE schema_migrations SET name = 'wrong_name' WHERE version = 3");
        }

        SchemaMigrator fresh = new(_database);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fresh.RequireSchemaVersionAsync());

        Assert.Contains("名称不一致", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 数据库版本高于程序时拒绝连接()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();

        await using (SqliteConnection connection = (SqliteConnection)_database.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "INSERT INTO schema_migrations (version, name, checksum, applied_at) VALUES (99, 'future', 'sha256:future', CURRENT_TIMESTAMP)");
        }

        SchemaMigrator fresh = new(_database);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fresh.RequireSchemaVersionAsync());

        Assert.Contains("高于程序支持的", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 不支持的驱动直接报错()
    {
        CanvasDatabaseOptions options = new() { Driver = "mysql" };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.FromResult(options.ResolveProvider()));

        Assert.Contains("不支持的数据库驱动", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSQL_模式缺少_DATABASE_URL_时报错()
    {
        CanvasDatabaseOptions options = new() { Driver = "postgres", Dsn = null };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => options.ResolveConnectionString());

        Assert.Contains("PostgreSQL 模式必须配置 DATABASE_URL", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 连接池参数与_Go_一致()
    {
        Assert.Equal((8, 4), new CanvasDatabaseOptions { Driver = "sqlite" }.PoolSize());
        Assert.Equal((30, 10), new CanvasDatabaseOptions { Driver = "postgres", Dsn = "Host=x" }.PoolSize());
    }
}

/// <summary>软删除过滤的语义验证。</summary>
public class SoftDeleteTests
{
    [Fact]
    public void 只对_Go_侧的_3_张表生效()
    {
        Assert.True(SoftDelete.Applies("model_channels"));
        Assert.True(SoftDelete.Applies("channel_models"));
        Assert.True(SoftDelete.Applies("channel_model_price_tiers"));
        Assert.False(SoftDelete.Applies("users"));
        Assert.False(SoftDelete.Applies("tasks"));
    }

    [Fact]
    public void 默认注入过滤()
    {
        Assert.Equal("deleted_at IS NULL", SoftDelete.Apply("model_channels"));
        Assert.Equal("mc.deleted_at IS NULL", SoftDelete.Apply("model_channels", alias: "mc"));
    }

    [Fact]
    public void 与已有条件用_AND_组合()
    {
        Assert.Equal(
            "deleted_at IS NULL AND (user_id = @userId)",
            SoftDelete.Apply("channel_models", "user_id = @userId"));
    }

    [Fact]
    public void 非软删除表不加过滤()
    {
        Assert.Equal("id = @id", SoftDelete.Apply("tasks", "id = @id"));
        Assert.Equal(string.Empty, SoftDelete.WhereClause("tasks"));
    }

    [Fact]
    public void 显式声明包含已删除记录时不过滤()
    {
        Assert.Equal(
            "id = @id",
            SoftDelete.Apply("model_channels", "id = @id", includeDeleted: true));
    }

    [Fact]
    public void 软删除写入的是_UPDATE_而非_DELETE()
    {
        string sql = SoftDelete.SoftDeleteStatement("model_channels", "id");
        Assert.StartsWith("UPDATE", sql, StringComparison.Ordinal);
        Assert.Contains("deleted_at = @deletedAt", sql, StringComparison.Ordinal);
    }
}
