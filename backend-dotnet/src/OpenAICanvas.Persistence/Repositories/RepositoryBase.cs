using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence;

/// <summary>
/// 仓储基类：提供 Dapper 查询入口、事务与方言。
/// </summary>
/// <remarks>
/// 对应 Go 的 <c>repository.Repository</c>。每个方法自行取连接（驱动内部有连接池），
/// 与 Go 侧每次 <c>r.db</c> 操作由 GORM 内部借还连接的语义等价。
/// <para>
/// <b>软删除</b>：查询 <see cref="SoftDelete.Tables"/> 里的表必须经 <see cref="WhereAsync"/>
/// 或 <see cref="SoftDelete.Apply"/> 注入 <c>deleted_at IS NULL</c>，除非显式声明要包含已删除记录。
/// </para>
/// </remarks>
public abstract class RepositoryBase
{
    protected RepositoryBase(CanvasDatabase database) => Database = database;

    protected CanvasDatabase Database { get; }

    protected SqlDialect Dialect => Database.Dialect;

    protected string Quote(string identifier) => Dialect.Quote(identifier);

    /// <summary>打开连接。调用方负责释放。</summary>
    protected Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default) =>
        Database.OpenAsync(cancellationToken);

    /// <summary>在事务中执行。对应 Go 的 <c>db.Transaction(func(tx) error)</c>。</summary>
    protected Task InTransactionAsync(
        Func<DbConnection, DbTransaction, Task> action,
        CancellationToken cancellationToken = default) =>
        Database.InTransactionAsync(action, cancellationToken);

    protected Task<T> InTransactionAsync<T>(
        Func<DbConnection, DbTransaction, Task<T>> action,
        CancellationToken cancellationToken = default) =>
        Database.InTransactionAsync(action, cancellationToken);

    /// <summary>
    /// 构造带软删除过滤的 WHERE 子句（含 <c>WHERE</c> 关键字，无条件时为空串）。
    /// </summary>
    protected static string Where(string table, string? condition = null, string? alias = null, bool includeDeleted = false) =>
        SoftDelete.WhereClause(table, condition, alias, includeDeleted);

    /// <summary>把条件与参数合并进 DynamicParameters，返回含软删除过滤的 WHERE 子句。</summary>
    protected static string Where(
        string table,
        DynamicParameters parameters,
        string? condition = null,
        string? alias = null,
        bool includeDeleted = false)
    {
        _ = parameters;
        return SoftDelete.WhereClause(table, condition, alias, includeDeleted);
    }

    /// <summary>按主键查询单行，找不到返回 null。对应 GORM 的 <c>First</c> + <c>ErrRecordNotFound</c>。</summary>
    protected async Task<T?> QuerySingleOrDefaultAsync<T>(
        DbConnection connection,
        string sql,
        object? parameters = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(
                sql, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // 多行结果：与 Go 的 First 语义不同，这里视为编程错误，向上抛出以便尽早发现。
            throw;
        }
    }

    protected async Task<T?> FirstOrDefaultAsync<T>(
        DbConnection connection,
        string sql,
        object? parameters = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await connection.QueryFirstOrDefaultAsync<T>(new CommandDefinition(
            sql, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

    protected async Task<IReadOnlyList<T>> QueryAsync<T>(
        DbConnection connection,
        string sql,
        object? parameters = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<T> rows = await connection.QueryAsync<T>(new CommandDefinition(
            sql, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    protected async Task<int> ExecuteAsync(
        DbConnection connection,
        string sql,
        object? parameters = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await connection.ExecuteAsync(new CommandDefinition(
            sql, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

    protected async Task<T?> ScalarAsync<T>(
        DbConnection connection,
        string sql,
        object? parameters = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await connection.ExecuteScalarAsync<T?>(new CommandDefinition(
            sql, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// 条件更新，返回是否恰好命中一行。对应 Go 里 <c>RowsAffected != 1</c> 的冲突检测。
    /// </summary>
    protected static void EnsureSingleRow(int affected, AppError conflict)
    {
        if (affected != 1)
        {
            throw conflict;
        }
    }

    /// <summary>
    /// 生成 <c>IN</c> 子句参数占位符。Dapper 原生支持列表参数，
    /// 这里只用于需要显式展开列名的场景。
    /// </summary>
    protected static string Placeholders(int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(index => "@p" + index));
}
