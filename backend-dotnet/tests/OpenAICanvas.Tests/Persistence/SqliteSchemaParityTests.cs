using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Persistence;

/// <summary>
/// 把生成的建表脚本落到真实 SQLite 库上，再读回物理结构与 GORM 的权威导出逐表逐列比对。
/// </summary>
/// <remarks>
/// 这替代了原先基于 EF Core 模型的比对：改用 Dapper 后表结构由静态 DDL 决定，
/// 因此必须验证"DDL 真正执行出来的结构"与 Go 侧一致，而不是验证模型元数据。
/// </remarks>
public class SqliteSchemaParityTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;

    public SqliteSchemaParityTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-parity-{Guid.NewGuid():N}.db");
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

    private sealed record GoColumn(string Name, bool IsColumn, string SqliteType, bool PrimaryKey, bool NotNull);

    private sealed record GoIndex(string Name, List<string> Columns, bool Unique);

    private sealed record GoTable(string GoName, string Table, List<GoColumn> Columns, List<GoIndex> Indexes);

    private static List<GoTable> LoadBaseline()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "schema-dump.json");
        Assert.True(File.Exists(path), $"找不到基线文件：{path}");

        using FileStream stream = File.OpenRead(path);
        List<GoTable>? tables = JsonSerializer.Deserialize<List<GoTable>>(
            stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(tables);
        return tables!;
    }

    private async Task ApplySchemaAsync()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();
    }

    private sealed record ColumnRow(string Name, string Type, long NotNull, long Pk);

    private async Task<Dictionary<string, List<ColumnRow>>> ReadColumnsAsync()
    {
        await using var connection = (SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();

        IEnumerable<string> tables = await connection.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'");

        Dictionary<string, List<ColumnRow>> result = new(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            IEnumerable<ColumnRow> columns = await connection.QueryAsync<ColumnRow>(
                $"SELECT name, type, \"notnull\" AS \"NotNull\", pk FROM pragma_table_info('{table}')");
            result[table] = columns.AsList();
        }

        return result;
    }

    [Fact]
    public async Task 迁移后表数量与_Go_一致()
    {
        await ApplySchemaAsync();
        List<GoTable> baseline = LoadBaseline();
        Dictionary<string, List<ColumnRow>> actual = await ReadColumnsAsync();

        string[] expected = baseline.Select(t => t.Table).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        string[] observed = actual.Keys.OrderBy(t => t, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, observed);
    }

    [Fact]
    public async Task 每张表的列名与列序一致()
    {
        await ApplySchemaAsync();
        List<GoTable> baseline = LoadBaseline();
        Dictionary<string, List<ColumnRow>> actual = await ReadColumnsAsync();

        List<string> problems = [];
        foreach (GoTable table in baseline)
        {
            string[] expected = table.Columns.Where(c => c.IsColumn).Select(c => c.Name).ToArray();
            if (!actual.TryGetValue(table.Table, out List<ColumnRow>? columns))
            {
                problems.Add($"{table.Table}: 缺少该表");
                continue;
            }

            string[] observed = columns.Select(c => c.Name).ToArray();
            if (!expected.SequenceEqual(observed, StringComparer.Ordinal))
            {
                problems.Add($"{table.Table}: 期望 [{string.Join(",", expected)}] 实际 [{string.Join(",", observed)}]");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task 每张表的索引全部创建成功()
    {
        await ApplySchemaAsync();
        List<GoTable> baseline = LoadBaseline();

        await using var connection = (SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();
        HashSet<string> observed = (await connection.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_autoindex_%'"))
            .ToHashSet(StringComparer.Ordinal);

        // 基线里的 421 个模型索引 + 4 个原始 SQL 索引。
        List<string> missing = baseline
            .SelectMany(t => t.Indexes.Select(i => i.Name))
            .Where(name => !observed.Contains(name))
            .ToList();

        Assert.True(missing.Count == 0, "缺少索引：" + string.Join(",", missing));
        Assert.True(observed.Count >= 425, $"索引数量不足：{observed.Count}");
    }

    [Fact]
    public async Task 原始_SQL_索引也已创建()
    {
        await ApplySchemaAsync();

        await using var connection = (SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();
        HashSet<string> observed = (await connection.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type = 'index'"))
            .ToHashSet(StringComparer.Ordinal);

        foreach (string name in new[]
        {
            "idx_schema_migrations_applied_at",
            "idx_project_asset_candidates_pending_identity",
            "idx_resources_user_upload_key",
            "idx_logical_model_source_active",
            "idx_users_email_nonempty",
        })
        {
            Assert.Contains(name, observed);
        }
    }

    [Fact]
    public async Task 软删除表带_deleted_at_列()
    {
        await ApplySchemaAsync();
        Dictionary<string, List<ColumnRow>> actual = await ReadColumnsAsync();

        foreach (string table in SoftDelete.Tables)
        {
            Assert.True(actual.ContainsKey(table), $"缺少软删除表 {table}");
            Assert.Contains(actual[table], c => c.Name == "deleted_at");
        }
    }
}

/// <summary>
/// 校验生成的 PostgreSQL 建表脚本：表数量、列类型与 GORM 的预期一致。
/// </summary>
public class PostgresSchemaScriptTests
{
    private sealed record GoColumn(string Name, bool IsColumn, string PostgresType);

    private sealed record GoTable(string Table, List<GoColumn> Columns);

    private static List<GoTable> LoadBaseline()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "schema-dump.json");
        using FileStream stream = File.OpenRead(path);
        List<GoTable>? tables = JsonSerializer.Deserialize<List<GoTable>>(
            stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(tables);
        return tables!;
    }

    private static string Canonical(string rawType)
    {
        string value = rawType.Trim().ToLowerInvariant();
        value = Regex.Replace(value, @"\s+generated\s+.*$", string.Empty);
        value = Regex.Replace(value, @"\s+default\s+.*$", string.Empty);
        value = Regex.Replace(value, @"\s+not null$", string.Empty);
        value = Regex.Replace(value, @"\s+null$", string.Empty);

        Match varchar = Regex.Match(value, @"^varchar\((\d+)\)$");
        if (varchar.Success)
        {
            return $"character varying({varchar.Groups[1].Value})";
        }

        return value switch
        {
            "timestamptz" => "timestamp with time zone",
            "bigserial" => "bigint",
            _ => value,
        };
    }

    [Fact]
    public void PostgreSQL_建表脚本每列类型与_Go_一致()
    {
        List<GoTable> baseline = LoadBaseline();
        string script = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "OpenAICanvas.Persistence", "Schema", "PostgresSchema.sql"));

        Dictionary<string, Dictionary<string, string>> parsed = ParseCreateTables(script);

        Assert.Equal(baseline.Count, parsed.Count);

        List<string> problems = [];
        int compared = 0;
        foreach (GoTable table in baseline)
        {
            if (!parsed.TryGetValue(table.Table, out Dictionary<string, string>? columns))
            {
                problems.Add($"{table.Table}: 脚本未生成该表");
                continue;
            }

            foreach (GoColumn column in table.Columns.Where(c => c.IsColumn))
            {
                if (!columns.TryGetValue(column.Name, out string? actual))
                {
                    problems.Add($"{table.Table}.{column.Name}: 脚本未生成该列");
                    continue;
                }

                string expected = Canonical(column.PostgresType);
                string observed = Canonical(actual);
                compared++;
                if (expected != observed)
                {
                    problems.Add($"{table.Table}.{column.Name}: GORM={column.PostgresType}，脚本={actual}");
                }
            }
        }

        Assert.True(compared > 900, $"比对列数过少（{compared}）");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static Dictionary<string, Dictionary<string, string>> ParseCreateTables(string script)
    {
        Dictionary<string, Dictionary<string, string>> result = new(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;

        foreach (string rawLine in script.Split('\n'))
        {
            string line = rawLine.Trim();
            Match tableMatch = Regex.Match(line, @"^CREATE TABLE IF NOT EXISTS ""(?<name>[^""]+)""");
            if (tableMatch.Success)
            {
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                result[tableMatch.Groups["name"].Value] = current;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line.StartsWith(");", StringComparison.Ordinal))
            {
                current = null;
                continue;
            }

            if (line.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Match columnMatch = Regex.Match(line, @"^""(?<name>[^""]+)""\s+(?<type>.+?)(?<tail>,\s*)?$");
            if (columnMatch.Success)
            {
                current[columnMatch.Groups["name"].Value] = columnMatch.Groups["type"].Value.Trim().TrimEnd(',');
            }
        }

        return result;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenAICanvas.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
    }
}
