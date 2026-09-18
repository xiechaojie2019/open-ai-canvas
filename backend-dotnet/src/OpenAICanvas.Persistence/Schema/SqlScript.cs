using System.Reflection;

namespace OpenAICanvas.Persistence.Schema;

/// <summary>SQL 脚本工具：语句拆分与内嵌建表脚本读取。</summary>
public static class SqlScript
{
    /// <summary>
    /// 按分号拆分多条语句，跳过 <c>--</c> 注释行。
    /// </summary>
    /// <remarks>
    /// 生成器产出的 DDL 不含嵌套分号（字符串默认值里没有分号），因此按行尾分号拆分是安全的。
    /// </remarks>
    public static IEnumerable<string> SplitStatements(string sql)
    {
        System.Text.StringBuilder current = new();
        foreach (string rawLine in sql.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.TrimStart().StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            current.Append(line).Append('\n');
            if (!line.TrimEnd().EndsWith(';'))
            {
                continue;
            }

            string statement = current.ToString().Trim().TrimEnd(';').Trim();
            current.Clear();
            if (statement.Length > 0)
            {
                yield return statement;
            }
        }

        string tail = current.ToString().Trim().TrimEnd(';').Trim();
        if (tail.Length > 0)
        {
            yield return tail;
        }
    }
}

/// <summary>内嵌的建表脚本。对应 Go 侧的 <c>AutoMigrate(Models()...)</c> 产物。</summary>
public sealed class SchemaScripts
{
    private const string SqliteResource = "OpenAICanvas.Persistence.Schema.SqliteSchema.sql";
    private const string PostgresResource = "OpenAICanvas.Persistence.Schema.PostgresSchema.sql";

    private SchemaScripts(string sqlite, string postgres)
    {
        Sqlite = sqlite;
        Postgres = postgres;
    }

    public string Sqlite { get; }

    public string Postgres { get; }

    public string For(SqlDialect dialect) => dialect.IsPostgres ? Postgres : Sqlite;

    public static SchemaScripts Load()
    {
        Assembly assembly = typeof(SchemaScripts).Assembly;
        return new SchemaScripts(
            ReadResource(assembly, SqliteResource),
            ReadResource(assembly, PostgresResource));
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using Stream? stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            throw new InvalidOperationException($"找不到内嵌建表脚本：{name}");
        }

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
