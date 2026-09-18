using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Schema;

namespace OpenAICanvas.Tools.Commands;

/// <summary>
/// 输出建表脚本，用于与 Go/GORM 的物理结构做人工比对。
/// 脚本本身就是运行时建库所用的那一份（内嵌资源），因此输出与实建结构必然一致。
/// </summary>
/// <remarks>等价于 Go <c>cmd/migrate-schema</c> 的一部分能力。</remarks>
internal static class SchemaDdlCommand
{
    public static int Run(string[] args)
    {
        string provider = args.Length > 1 ? args[1].ToLowerInvariant() : "sqlite";
        string? outputPath = args.Length > 2 ? args[2] : null;

        SqlDialect dialect = provider switch
        {
            "sqlite" => SqlDialect.Sqlite,
            "postgres" or "postgresql" => SqlDialect.Postgres,
            _ => throw new InvalidOperationException($"不支持的数据库：{provider}"),
        };

        SchemaScripts scripts = SchemaScripts.Load();
        string script = scripts.For(dialect);

        if (outputPath is null)
        {
            Console.WriteLine(script);
            return 0;
        }

        File.WriteAllText(outputPath, script, new System.Text.UTF8Encoding(false));
        Console.WriteLine($"已写入 {outputPath}（{script.Length} 字符）");
        return 0;
    }
}
