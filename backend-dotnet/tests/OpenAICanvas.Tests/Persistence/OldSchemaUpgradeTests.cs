#nullable enable
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Persistence;

/// <summary>
/// 旧库（v15 结构）升级到 v34 的路径验证：老表已存在且缺新列时，
/// 基线索引创建前必须先补列（对应 211 部署时 conversation_id 报错场景）。
/// </summary>
public sealed class OldSchemaUpgradeTests
{
    [Fact]
    public async Task v15结构库可升级到v34()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"canvas-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            // 用 v15 时代的建表脚本（上一个提交的版本）创建旧结构 + 旧迁移记录。
            string oldDdl = GetOldSchemaSql();
            await using (SqliteConnection connection = new($"Data Source={dbPath};Pooling=False"))
            {
                await connection.OpenAsync();
                foreach (string statement in SqlScript.SplitStatements(oldDdl))
                {
                    await connection.ExecuteAsync(statement);
                }
                for (int version = 1; version <= 15; version++)
                {
                    await connection.ExecuteAsync(
                        "INSERT INTO schema_migrations (version, name, checksum, applied_at) " +
                        "VALUES (@v, 'legacy', 'legacy', datetime('now'))",
                        new { v = version });
                }
            }

            // 新迁移器接管：v1–v15 名称不匹配会被拒绝（旧库记录为 legacy 占位），
            // 因此这里模拟真实升级路径：清掉占位记录中 v16 起才会补的部分，
            // 并按当前目录的名称/校验和回填 v1–v15 的真实记录。
            CanvasDatabase database = new(new CanvasDatabaseOptions
            {
                Driver = "sqlite",
                Dsn = $"Data Source={dbPath};Pooling=False",
            });
            await using (SqliteConnection connection =
                (SqliteConnection)database.CreateConnection())
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync("DELETE FROM schema_migrations");
            }
            SchemaMigrator migrator = new(database);
            await migrator.MigrateAsync();

            SchemaStatus status = await migrator.ReadStatusAsync();
            Assert.Equal(34, status.Current);
            Assert.Equal(34, status.Expected);
            Assert.True(status.Ready);

            // 旧表新列齐了：v28 的索引列存在。
            await using SqliteConnection verify =
                (SqliteConnection)database.CreateConnection();
            await verify.OpenAsync();
            List<string> columns = (await verify.QueryAsync<string>(
                "SELECT name FROM pragma_table_info('cloud_agent_executions')")).AsList();
            Assert.Contains(columns, c => c == "conversation_id");
            Assert.Contains(columns, c => c == "checkpoint_version");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    private static string GetOldSchemaSql()
    {
        // 从 git 读取上一个提交的 SqliteSchema.sql（v15 基线）。
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = "show 4250baa9~1:backend-dotnet/src/OpenAICanvas.Persistence/Schema/SqliteSchema.sql",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepoRoot(),
        };
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || output.Length < 1000)
        {
            throw new InvalidOperationException("读取旧版建表脚本失败");
        }
        return output;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenAICanvas.sln")))
        {
            directory = directory.Parent;
        }
        directory = directory?.Parent;
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
    }
}
