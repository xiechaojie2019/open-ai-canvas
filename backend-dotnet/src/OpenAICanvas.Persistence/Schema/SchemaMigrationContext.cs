using System.Data;
using System.Data.Common;
using Dapper;

namespace OpenAICanvas.Persistence.Schema;

/// <summary>
/// 迁移执行上下文：提供事务内连接、方言与建表脚本，供各版本迁移步骤使用。
/// </summary>
public sealed class SchemaMigrationContext
{
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;

    internal SchemaMigrationContext(
        DbConnection connection,
        DbTransaction transaction,
        SqlDialect dialect,
        SchemaScripts scripts)
    {
        _connection = connection;
        _transaction = transaction;
        Dialect = dialect;
        Scripts = scripts;
    }

    public SqlDialect Dialect { get; }

    internal SchemaScripts Scripts { get; }

    private SchemaScriptReader? _reader;

    /// <summary>
    /// 读取某列在完整建表脚本里的定义（类型 + 约束），用于构造 ALTER TABLE ADD COLUMN。
    /// </summary>
    public string ColumnDefinition(string table, string column)
    {
        _reader ??= new SchemaScriptReader(Scripts.For(Dialect));
        return _reader.ColumnDefinition(table, column)
            ?? throw new InvalidOperationException($"建表脚本里找不到列定义：{table}.{column}");
    }

    /// <summary>执行一条 SQL（可为多条语句，用分号分隔）。</summary>
    public async Task ExecuteAsync(string sql, object? parameters = null)
    {
        foreach (string statement in SqlScript.SplitStatements(sql))
        {
            await _connection.ExecuteAsync(new CommandDefinition(
                statement,
                parameters,
                _transaction)).ConfigureAwait(false);
        }
    }

    /// <summary>执行查询并返回首行首列。</summary>
    public async Task<T?> ScalarAsync<T>(string sql, object? parameters = null) =>
        await _connection.ExecuteScalarAsync<T?>(new CommandDefinition(
            sql,
            parameters,
            _transaction)).ConfigureAwait(false);

    /// <summary>执行查询并映射结果。</summary>
    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null)
    {
        IEnumerable<T> rows = await _connection.QueryAsync<T>(new CommandDefinition(
            sql,
            parameters,
            _transaction)).ConfigureAwait(false);
        return rows.AsList();
    }

    /// <summary>表是否存在。</summary>
    public async Task<bool> TableExistsAsync(string table)
    {
        string sql = Dialect.IsPostgres
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @table"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table";
        long count = await ScalarAsync<long>(sql, new { table }).ConfigureAwait(false);
        return count > 0;
    }

    /// <summary>列是否存在。对应 Go 的 <c>Migrator().HasColumn()</c>。</summary>
    public async Task<bool> ColumnExistsAsync(string table, string column)
    {
        if (Dialect.IsPostgres)
        {
            long count = await ScalarAsync<long>(
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @table AND column_name = @column",
                new { table, column }).ConfigureAwait(false);
            return count > 0;
        }

        // SQLite 没有 information_schema，用 PRAGMA 读取列定义。
        IReadOnlyList<string> columns = await QueryAsync<string>(
            $"SELECT name FROM pragma_table_info('{table}')").ConfigureAwait(false);
        return columns.Contains(column, StringComparer.Ordinal);
    }

    /// <summary>索引是否存在。</summary>
    public async Task<bool> IndexExistsAsync(string indexName)
    {
        string sql = Dialect.IsPostgres
            ? "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = current_schema() AND indexname = @name"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @name";
        long count = await ScalarAsync<long>(sql, new { name = indexName }).ConfigureAwait(false);
        return count > 0;
    }

    /// <summary>增加列（不存在时）。对应 Go 的 <c>Migrator().AddColumn()</c>。</summary>
    public async Task AddColumnIfMissingAsync(string table, string column, string definition)
    {
        if (await ColumnExistsAsync(table, column).ConfigureAwait(false))
        {
            return;
        }

        await ExecuteAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}").ConfigureAwait(false);
    }
}
