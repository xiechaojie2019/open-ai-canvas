namespace OpenAICanvas.Persistence;

/// <summary>
/// SQLite 与 PostgreSQL 的方言差异。
/// </summary>
/// <remarks>
/// 差异点全部来自 Go 侧 GORM 驱动与 clause 构建器的实际行为，不是猜测：
/// <list type="bullet">
/// <item>行锁：<c>gorm.io/driver/sqlite</c> 在 "FOR" 子句构建器里直接 return，
/// 注释为 "SQLite3 does not support row-level locking"。所以 SQLite 下必须省略
/// <c>FOR UPDATE</c> / <c>FOR SHARE</c>，否则语法错误。</item>
/// <item>upsert：两边都支持 <c>ON CONFLICT (...) DO NOTHING / DO UPDATE</c>。</item>
/// <item>标识符引用：两边都用双引号（SQLite 也接受）。</item>
/// </list>
/// </remarks>
public sealed class SqlDialect
{
    public static readonly SqlDialect Sqlite = new(DatabaseProvider.Sqlite);
    public static readonly SqlDialect Postgres = new(DatabaseProvider.Postgres);

    private SqlDialect(DatabaseProvider provider) => Provider = provider;

    public DatabaseProvider Provider { get; }

    public bool IsPostgres => Provider == DatabaseProvider.Postgres;

    /// <summary>引用标识符。两个 provider 都用双引号。</summary>
    public string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// 行锁子句。SQLite 返回空串——对应 GORM sqlite 驱动忽略 <c>clause.Locking</c> 的行为。
    /// </summary>
    public string ForUpdate() => IsPostgres ? " FOR UPDATE" : string.Empty;

    /// <summary>共享行锁。同样在 SQLite 下为空。</summary>
    public string ForShare() => IsPostgres ? " FOR SHARE" : string.Empty;

    /// <summary>分页子句。两边语法相同。</summary>
    public string LimitOffset(long? limit, long? offset)
    {
        System.Text.StringBuilder builder = new();
        if (limit is not null)
        {
            builder.Append(" LIMIT ").Append(limit.Value);
        }

        if (offset is not null && offset.Value > 0)
        {
            builder.Append(" OFFSET ").Append(offset.Value);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 插入并忽略冲突。对应 Go 的 <c>clause.OnConflict{DoNothing: true}</c>（27 处）。
    /// </summary>
    public string OnConflictDoNothing(string conflictTarget) =>
        string.IsNullOrEmpty(conflictTarget)
            ? " ON CONFLICT DO NOTHING"
            : $" ON CONFLICT ({conflictTarget}) DO NOTHING";

    /// <summary>
    /// 插入并更新冲突行。对应 Go 的 <c>clause.OnConflict{DoUpdates: Assignments(...)}</c>，
    /// 以及 GORM <c>tx.Save()</c> 的 upsert 语义（有主键则更新）。
    /// </summary>
    /// <param name="conflictTarget">冲突列，形如 <c>"id"</c> 或 <c>"user_id", "skill_id"</c>。</param>
    /// <param name="columns">需要覆盖的列（不含冲突列本身）。</param>
    public string OnConflictDoUpdate(string conflictTarget, IEnumerable<string> columns) =>
        $" ON CONFLICT ({conflictTarget}) DO UPDATE SET "
        + string.Join(", ", columns.Select(column => $"{Quote(column)} = excluded.{Quote(column)}"));

    /// <summary>
    /// 迁移期间拿排他锁。对应 Go 的 <c>pg_advisory_xact_lock</c>；SQLite 无此需要（库级写锁）。
    /// </summary>
    public string? AcquireMigrationLock(long lockId) =>
        IsPostgres ? $"SELECT pg_advisory_xact_lock({lockId})" : null;

    /// <summary>当前时间。两边都支持 CURRENT_TIMESTAMP。</summary>
    public string CurrentTimestamp() => "CURRENT_TIMESTAMP";

    /// <summary>把 bool 参数转成驱动可接受的值（SQLite 存 0/1）。</summary>
    public object Boolean(bool value) => IsPostgres ? value : (value ? 1 : 0);
}
