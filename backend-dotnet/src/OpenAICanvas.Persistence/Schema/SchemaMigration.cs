namespace OpenAICanvas.Persistence.Schema;

/// <summary>数据库结构版本状态。对应 Go: <c>database.SchemaStatus</c>。</summary>
public sealed class SchemaStatus
{
    /// <summary>数据库当前版本（<c>schema_migrations</c> 的 MAX(version)）。</summary>
    public long Current { get; init; }

    /// <summary>程序期望版本。</summary>
    public long Expected { get; init; }

    /// <summary>版本一致且全部迁移记录的名称与校验和都对得上。</summary>
    public bool Ready { get; init; }
}

/// <summary>一条已应用的迁移记录。对应 Go: <c>database.schemaMigration</c>。</summary>
public sealed class SchemaMigrationRecord
{
    public long Version { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Checksum { get; init; } = string.Empty;

    public DateTime AppliedAt { get; init; }
}

/// <summary>迁移定义。对应 Go: <c>database.migration</c>。</summary>
public sealed class SchemaMigration
{
    public required long Version { get; init; }

    public required string Name { get; init; }

    public required string Checksum { get; init; }

    /// <summary>应用该版本的增量变更。全新数据库走全量建表路径，不会调用它。</summary>
    public required Func<SchemaMigrationContext, Task> Apply { get; init; }
}
