using Dapper;
using Microsoft.Data.Sqlite;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Persistence;

/// <summary>
/// v35 <c>camel_case_identifiers</c> 的升级路径验证：已有数据的 v34 库升到 v35 后
/// 数据必须原样保留，标识符收敛为 camelCase，且迁移账本不被改名波及。
/// </summary>
/// <remarks>
/// <see cref="SchemaMigratorTests"/> 只覆盖全新库；生产上是"已经跑过 v1–v34、里面有数据"
/// 的库去补 v35，那才是真正的风险面，因此这里单独模拟：先把 v35 库逆向改回 v34 形态并写入
/// 数据，再让迁移器补 v35。
/// </remarks>
public class SchemaUpgradeTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;

    public SchemaUpgradeTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-upgrade-{Guid.NewGuid():N}.db");
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

    /// <summary>
    /// 把 v35 的改名整体逆向执行，得到一个"结构停在 v34、但已经装了数据"的库。
    /// 顺序与正向迁移相反：正向是先列后表，逆向就先表后列。
    /// </summary>
    private static async Task RevertToV34Async(SqliteConnection connection)
    {
        foreach ((string oldName, string newName) in CamelCaseRenamePlan.Tables)
        {
            await connection.ExecuteAsync($"ALTER TABLE \"{newName}\" RENAME TO \"{oldName}\"");
        }

        foreach ((string table, string oldName, string newName) in CamelCaseRenamePlan.Columns)
        {
            await connection.ExecuteAsync($"ALTER TABLE \"{table}\" RENAME COLUMN \"{newName}\" TO \"{oldName}\"");
        }

        // 抹掉 v35 账本行，让迁移器认为这是 v34 库。
        await connection.ExecuteAsync("DELETE FROM schema_migrations WHERE version = 35");
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name)
    {
        long count = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name", new { name });
        return count > 0;
    }

    [Fact]
    public async Task 从_v34_升级到_v35_数据保留且标识符收敛为_camelCase()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();

        // 选一张会被改名的表（announcement_image_drafts → announcementImageDrafts），
        // 三列全部可空，适合直接插一条哨兵数据。
        const string Sentinels = "RES_UPGRADE_PROBE";
        await using (SqliteConnection write = (SqliteConnection)_database.CreateConnection())
        {
            await write.OpenAsync();
            await write.ExecuteAsync(
                "INSERT INTO \"announcementImageDrafts\" (\"resourceId\", \"userId\", \"createdAt\") VALUES (@id, @userId, @createdAt)",
                new { id = Sentinels, userId = "USR_UPGRADE_PROBE", createdAt = "2026-09-28 00:00:00" });
        }

        // ---- 逆向改回 v34 形态，模拟"线上已经跑过 v1–v34 的库" ----
        await using (SqliteConnection revert = (SqliteConnection)_database.CreateConnection())
        {
            await revert.OpenAsync();
            await RevertToV34Async(revert);

            Assert.True(await TableExistsAsync(revert, "announcement_image_drafts"));
            Assert.False(await TableExistsAsync(revert, "announcementImageDrafts"));

            // 改名后数据仍在，且能用 snake_case 读出来。
            string? value = await revert.ExecuteScalarAsync<string>(
                "SELECT resource_id FROM announcement_image_drafts WHERE resource_id = @id", new { id = Sentinels });
            Assert.Equal(Sentinels, value);
        }

        // 账本此刻应为 v34。
        SchemaStatus before = await migrator.ReadStatusAsync();
        Assert.Equal(34, before.Current);
        Assert.Equal(35, before.Expected);
        Assert.False(before.Ready);

        // ---- 补 v35 ----
        SchemaMigrator upgrade = new(_database);
        await upgrade.MigrateAsync();

        await using (SqliteConnection verify = (SqliteConnection)_database.CreateConnection())
        {
            await verify.OpenAsync();

            Assert.True(await TableExistsAsync(verify, "announcementImageDrafts"));
            Assert.False(await TableExistsAsync(verify, "announcement_image_drafts"));

            // 数据必须跨过改名活下来。
            string? value = await verify.ExecuteScalarAsync<string>(
                "SELECT \"resourceId\" FROM \"announcementImageDrafts\" WHERE \"resourceId\" = @id", new { id = Sentinels });
            Assert.Equal(Sentinels, value);

            string? userId = await verify.ExecuteScalarAsync<string>(
                "SELECT \"userId\" FROM \"announcementImageDrafts\" WHERE \"resourceId\" = @id", new { id = Sentinels });
            Assert.Equal("USR_UPGRADE_PROBE", userId);

            // 账本表本身没被改名波及：v1–v34 的记录仍在，且 applied_at 列还在。
            long versions = await verify.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations");
            Assert.Equal(35, versions);
            long appliedAt = await verify.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM pragma_table_info('schema_migrations') WHERE name = 'applied_at'");
            Assert.Equal(1, appliedAt);
        }

        SchemaStatus after = await upgrade.ReadStatusAsync();
        Assert.Equal(35, after.Current);
        Assert.True(after.Ready);
    }

    [Fact]
    public async Task 逆向改名后再补_v35_是幂等且可重复的()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();

        // 连续两次"补 v35"不应产生任何结构或账本变化。
        SchemaMigrator second = new(_database);
        await second.MigrateAsync();

        await using SqliteConnection connection = (SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();

        long versions = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations");
        Assert.Equal(35, versions);
        Assert.True(await TableExistsAsync(connection, "announcementImageDrafts"));
    }
}
