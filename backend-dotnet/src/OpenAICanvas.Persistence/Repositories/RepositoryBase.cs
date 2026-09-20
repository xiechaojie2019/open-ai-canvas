using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
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
            return await connection.QuerySingleOrDefaultAsync<T>(Prepare(
                sql, parameters, transaction, cancellationToken)).ConfigureAwait(false);
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
        await connection.QueryFirstOrDefaultAsync<T>(Prepare(sql, parameters, transaction, cancellationToken)).ConfigureAwait(false);

    protected async Task<IReadOnlyList<T>> QueryAsync<T>(
        DbConnection connection,
        string sql,
        object? parameters = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<T> rows = await connection.QueryAsync<T>(Prepare(sql, parameters, transaction, cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    protected async Task<int> ExecuteAsync(
        DbConnection connection,
        string sql,
        object? parameters = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await connection.ExecuteAsync(Prepare(sql, parameters, transaction, cancellationToken)).ConfigureAwait(false);

    protected async Task<T?> ScalarAsync<T>(
        DbConnection connection,
        string sql,
        object? parameters = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await connection.ExecuteScalarAsync<T?>(Prepare(sql, parameters, transaction, cancellationToken)).ConfigureAwait(false);

    private CommandDefinition Prepare(
        string sql, object? parameters, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        (string preparedSql, object? preparedParameters) = ExpandCollectionParameters(sql, parameters);
        return new(preparedSql, preparedParameters, transaction, cancellationToken: cancellationToken);
    }

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
    /// 生成 <c>IN</c> 子句参数占位符，例如 <c>Placeholders(3)</c> → <c>@p0, @p1, @p2</c>。
    /// 用于手写展开列名的场景。
    /// </summary>
    protected static string Placeholders(int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(index => "@p" + index));

    /// <summary>
    /// 把 SQL 里的集合参数统一展开成逐元素占位符。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要这一步</b>：Dapper 2.1.35 只在"参数对象本身是
    /// IEnumerable&lt;T&gt;"时才做列表展开；包在匿名对象 /
    /// DynamicParameters 里的集合属性会被整体当作标量绑定，生产
    /// PostgreSQL 实测产生 <c>IN $1</c> 语法错误，SQLite 下则落入
    /// <c>IN ((?,?))</c> 行值误用。这里扫描参数模板属性，凡被
    /// <c>@名称</c> / <c>IN (@名称)</c> / gorm 风格 <c>IN @名称</c>
    /// 引用的集合，把占位符重写成 <c>IN (@名称0, @名称1, ...)</c>
    /// （gorm 风格没有括号，必须补上，否则生成非法 SQL），
    /// 并返回展开后的参数字典。
    /// 空集合展开为空子查询（<c>IN</c> 不命中任何行、<c>NOT IN</c> 恒真）。
    /// 未引用集合参数的查询原样返回，不产生额外分配。
    /// </remarks>

    private static (string Sql, object? Parameters) ExpandCollectionParameters(
        string sql, object? parameters)
    {
        // DynamicParameters 通道约定只传标量参数（配合 Placeholders 手写展开）；
        // Dictionary<string, object?> 直接按键读取，避免反射撞上索引器属性。
        if (parameters is null || parameters is DynamicParameters || sql.IndexOf('@') < 0)
        {
            return (sql, parameters);
        }

        // Dapper 原生列表展开通道：参数本身是实体集合（分批多行 INSERT），
        // 每个元素才是参数模板；按顶层属性反射会把实体绑定整体替换掉。
        if (parameters is System.Collections.IEnumerable && parameters is not Dictionary<string, object?>)
        {
            return (sql, parameters);
        }

        List<KeyValuePair<string, object?>> entries = new();
        if (parameters is Dictionary<string, object?> dictionary)
        {
            entries.AddRange(dictionary.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value)));
        }
        else
        {
            // Dictionary 等带索引器的类型经 GetProperties 会暴露 Item 索引器，
            // GetValue 将抛 TargetParameterCountException，必须跳过。
            entries.AddRange(
                parameters.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.GetIndexParameters().Length == 0)
                    .Select(property => new KeyValuePair<string, object?>(property.Name, property.GetValue(parameters))));
        }

        List<(string Name, List<object?> Items)> collections = new();
        foreach (KeyValuePair<string, object?> entry in entries)
        {
            if (entry.Value is System.Collections.IEnumerable elements
                && elements is not string
                && elements is not IEnumerable<byte>
                && elements is not IEnumerable<char>)
            {
                collections.Add((entry.Key, elements.Cast<object?>().ToList()));
            }
        }

        if (collections.Count == 0)
        {
            return (sql, parameters);
        }

        string preparedSql = sql;
        Dictionary<string, object?> bound = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> entry in entries)
        {
            (string name, List<object?>? items) = collections.FirstOrDefault(pair => pair.Name == entry.Key);
            if (items is null)
            {
                bound[entry.Key] = entry.Value;
                continue;
            }

            // 空集合展开为恒假空子查询：IN () 是语法错误，
            // IN (NULL) / NOT IN (NULL) 会因 UNKNOWN 过滤掉所有行。
            string expandedIn = items.Count == 0
                ? "(SELECT 1 WHERE 0)"
                : "(" + string.Join(", ", Enumerable.Range(0, items.Count).Select(index => "@" + name + index)) + ")";

            // 形态一：IN (@name)。形态二：gorm 风格 IN @name（无括号，必须补括号）。
            string inParensPattern = @"IN\s*\(\s*@" + Regex.Escape(name) + @"\s*\)";
            string inBarePattern = @"IN\s*@" + Regex.Escape(name) + @"\b";
            if (!Regex.IsMatch(preparedSql, inParensPattern, RegexOptions.IgnoreCase)
                && !Regex.IsMatch(preparedSql, inBarePattern, RegexOptions.IgnoreCase))
            {
                // SQL 未以 IN 引用该集合：直接丢弃。集合原值若继续绑定，
                // 仍会触发 Dapper 的 List(object) 标量包装错误。
                continue;
            }

            preparedSql = Regex.Replace(
                preparedSql, inParensPattern, "IN " + expandedIn.Replace("$", "$$"), RegexOptions.IgnoreCase);
            preparedSql = Regex.Replace(
                preparedSql, inBarePattern, "IN " + expandedIn.Replace("$", "$$"), RegexOptions.IgnoreCase);
            for (int index = 0; index < items.Count; index++)
            {
                bound[name + index] = items[index];
            }
        }

        return (preparedSql, bound);
    }
}

