using System.Text.RegularExpressions;

namespace OpenAICanvas.Persistence.Schema;

/// <summary>
/// 从内嵌建表脚本里读取表/列定义，供增量迁移构造 <c>ALTER TABLE ADD COLUMN</c>。
/// </summary>
/// <remarks>
/// 增量升级必须给出与全量建表完全一致的列定义，因此这里直接解析生成的脚本，
/// 而不是在代码里重复写一份类型（重复写迟早会与基线漂移）。
/// </remarks>
public sealed partial class SchemaScriptReader
{
    private readonly Dictionary<string, string> _columnDefinitions = new(StringComparer.Ordinal);

    public SchemaScriptReader(string script)
    {
        string? currentTable = null;
        foreach (string rawLine in script.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            Match tableMatch = CreateTablePattern().Match(line);
            if (tableMatch.Success)
            {
                currentTable = tableMatch.Groups["name"].Value;
                continue;
            }

            if (line.StartsWith(");", StringComparison.Ordinal))
            {
                currentTable = null;
                continue;
            }

            if (currentTable is null || line.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Match columnMatch = ColumnPattern().Match(line);
            if (!columnMatch.Success)
            {
                continue;
            }

            string name = columnMatch.Groups["name"].Value;
            string definition = columnMatch.Groups["definition"].Value.Trim().TrimEnd(',').Trim();
            _columnDefinitions[$"{currentTable}.{name}"] = definition;
        }
    }

    /// <summary>返回列定义（类型 + 约束），例如 <c>varchar(80) NOT NULL DEFAULT ''</c>。</summary>
    public string? ColumnDefinition(string table, string column) =>
        _columnDefinitions.TryGetValue($"{table}.{column}", out string? value) ? value : null;

    [GeneratedRegex(@"^CREATE TABLE IF NOT EXISTS ""(?<name>[^""]+)""")]
    private static partial Regex CreateTablePattern();

    [GeneratedRegex(@"^""(?<name>[^""]+)""\s+(?<definition>.+)$")]
    private static partial Regex ColumnPattern();
}
