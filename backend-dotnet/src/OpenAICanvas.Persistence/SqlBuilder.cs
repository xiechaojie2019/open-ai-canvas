using System.Reflection;
using Dapper;
using System.Text;

namespace OpenAICanvas.Persistence;

/// <summary>
/// 基于 <see cref="EntityMetadata"/> 的 SQL 构造器。
/// </summary>
/// <remarks>
/// Dapper 不做命名推断，而实体属性名保留了 Go 字段名（<c>ID</c>、<c>MetadataJSON</c>），
/// 与列名不是简单的大小写关系。这里统一由生成的元数据推导列名，
/// 保证 SQL 与建表脚本永远一致，也避免每个仓储方法手写列名。
/// </remarks>
public static class SqlBuilder
{
    /// <summary>构造 INSERT 语句。<c>@属性名</c> 由 Dapper 绑定到实体属性。</summary>
    public static string Insert<T>(bool onConflictDoNothing = false) =>
        Insert(typeof(T), onConflictDoNothing);

    public static string Insert(Type entityType, bool onConflictDoNothing = false)
    {
        EntityMap map = EntityMetadata.For(entityType);
        StringBuilder columns = new();
        StringBuilder values = new();

        foreach (ColumnMap column in map.Columns)
        {
            if (columns.Length > 0)
            {
                columns.Append(", ");
                values.Append(", ");
            }

            columns.Append(Quote(column.Column));
            values.Append('@').Append(column.Property);
        }

        string sql = $"INSERT INTO {Quote(map.Table)} ({columns}) VALUES ({values})";
        return onConflictDoNothing ? sql + " ON CONFLICT DO NOTHING" : sql;
    }

    /// <summary>
    /// 构造按主键更新的 UPDATE 语句。只更新显式列出的属性。
    /// </summary>
    /// <param name="entityType">实体类型。</param>
    /// <param name="properties">要更新的属性名；为空表示除主键外全部。</param>
    /// <param name="where">额外 WHERE 条件（不含 <c>WHERE</c> 关键字）。</param>
    public static string Update(Type entityType, IEnumerable<string>? properties = null, string? where = null)
    {
        EntityMap map = EntityMetadata.For(entityType);
        string key = map.KeyProperty
            ?? throw new InvalidOperationException($"{entityType.Name} 没有单列主键，无法构造按主键更新");

        HashSet<string>? selected = properties is null
            ? null
            : new HashSet<string>(properties, StringComparer.Ordinal);

        List<string> assignments = [];
        foreach (ColumnMap column in map.Columns)
        {
            if (column.Property == key)
            {
                continue;
            }

            if (selected is not null && !selected.Contains(column.Property))
            {
                continue;
            }

            assignments.Add($"{Quote(column.Column)} = @{column.Property}");
        }

        string keyColumn = map.ColumnOf(key)
            ?? throw new InvalidOperationException($"{entityType.Name} 的主键列缺失");

        StringBuilder sql = new();
        sql.Append("UPDATE ").Append(Quote(map.Table))
            .Append(" SET ").Append(string.Join(", ", assignments))
            .Append(" WHERE ").Append(Quote(keyColumn)).Append(" = @").Append(key);

        if (!string.IsNullOrWhiteSpace(where))
        {
            sql.Append(" AND (").Append(where).Append(')');
        }

        return sql.ToString();
    }

    /// <summary>构造按主键删除的 DELETE 语句。</summary>
    public static string Delete(Type entityType, string? where = null)
    {
        EntityMap map = EntityMetadata.For(entityType);
        string key = map.KeyProperty
            ?? throw new InvalidOperationException($"{entityType.Name} 没有单列主键");

        StringBuilder sql = new();
        sql.Append("DELETE FROM ").Append(Quote(map.Table));
        if (!string.IsNullOrWhiteSpace(where))
        {
            sql.Append(" WHERE ").Append(where);
        }

        return sql.ToString();
    }

    /// <summary>
    /// 构造按主键的 SELECT。列名显式列出，避免 <c>SELECT *</c> 与实体属性顺序耦合。
    /// </summary>
    public static string Select(Type entityType, string? where = null, string? orderBy = null, string? limitOffset = null)
    {
        EntityMap map = EntityMetadata.For(entityType);

        StringBuilder sql = new();
        sql.Append("SELECT ");
        sql.Append(string.Join(", ", map.Columns.Select(c => $"{Quote(c.Column)} AS {Quote(c.Property)}")));
        sql.Append(" FROM ").Append(Quote(map.Table));

        if (!string.IsNullOrWhiteSpace(where))
        {
            sql.Append(" WHERE ").Append(where);
        }

        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            sql.Append(" ORDER BY ").Append(orderBy);
        }

        if (!string.IsNullOrWhiteSpace(limitOffset))
        {
            sql.Append(limitOffset);
        }

        return sql.ToString();
    }

    public static string Select<T>(string? where = null, string? orderBy = null, string? limitOffset = null) =>
        Select(typeof(T), where, orderBy, limitOffset);

    /// <summary>
    /// 构造只取部分列的 SELECT。
    /// </summary>
    /// <remarks>
    /// <b>必须用别名</b>：Dapper 按属性名匹配结果集列名，而 <c>user_id</c> 与 <c>UserID</c>
    /// 只做大小写不敏感比较、不做下划线转换，所以裸 <c>SELECT *</c> 会让
    /// <c>UserID</c>、<c>PasswordHash</c> 这类属性读回空值。
    /// </remarks>
    public static string SelectColumns<T>(IReadOnlyList<string> properties, string? where = null, string? orderBy = null, string? limitOffset = null)
    {
        EntityMap map = EntityMetadata.For(typeof(T));
        List<string> projected = [];
        foreach (string property in properties)
        {
            string column = map.ColumnOf(property)
                ?? throw new InvalidOperationException($"{typeof(T).Name} 没有属性 {property}");
            projected.Add($"{Quote(column)} AS {Quote(property)}");
        }

        StringBuilder sql = new();
        sql.Append("SELECT ").Append(string.Join(", ", projected))
            .Append(" FROM ").Append(Quote(map.Table));
        if (!string.IsNullOrWhiteSpace(where))
        {
            sql.Append(" WHERE ").Append(where);
        }

        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            sql.Append(" ORDER BY ").Append(orderBy);
        }

        if (!string.IsNullOrWhiteSpace(limitOffset))
        {
            sql.Append(limitOffset);
        }

        return sql.ToString();
    }

    /// <summary>
    /// 生成带表别名的列投影，例如 <c>"rc"."batch_id" AS "BatchID"</c>。
    /// </summary>
    /// <remarks>
    /// 供带 JOIN / 子查询的手写 SQL 复用。<b>Dapper 只做大小写不敏感匹配，不做下划线转换</b>，
    /// 手写 <c>SELECT t.*</c> 会让 <c>batch_id</c> 静默读成空值；用本方法可避免漏别名。
    /// </remarks>
    public static string Projection<T>(string? alias = null)
    {
        EntityMap map = EntityMetadata.For(typeof(T));
        string prefix = string.IsNullOrEmpty(alias) ? "" : Quote(alias) + ".";
        List<string> parts = [];
        foreach (ColumnMap column in map.Columns)
        {
            parts.Add($"{prefix}{Quote(column.Column)} AS {Quote(column.Property)}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>只取单个列（如 <c>SELECT id FROM ...</c>），供 Pluck 类查询使用。</summary>
    public static string SelectSingleColumn<T>(string property, string? where = null, string? orderBy = null, string? limitOffset = null)
    {
        EntityMap map = EntityMetadata.For(typeof(T));
        string column = map.ColumnOf(property)
            ?? throw new InvalidOperationException($"{typeof(T).Name} 没有属性 {property}");

        StringBuilder sql = new();
        sql.Append("SELECT ").Append(Quote(column)).Append(" FROM ").Append(Quote(map.Table));
        if (!string.IsNullOrWhiteSpace(where))
        {
            sql.Append(" WHERE ").Append(where);
        }

        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            sql.Append(" ORDER BY ").Append(orderBy);
        }

        if (!string.IsNullOrWhiteSpace(limitOffset))
        {
            sql.Append(limitOffset);
        }

        return sql.ToString();
    }

    /// <summary>主键列名。</summary>
    public static string KeyColumn(Type entityType)
    {
        EntityMap map = EntityMetadata.For(entityType);
        string key = map.KeyProperty
            ?? throw new InvalidOperationException($"{entityType.Name} 没有单列主键");
        return map.ColumnOf(key)
            ?? throw new InvalidOperationException($"{entityType.Name} 的主键列缺失");
    }

    /// <summary>取实体的主键值，用于按主键更新/删除的参数绑定。</summary>
    public static object? KeyValue(object entity)
    {
        EntityMap map = EntityMetadata.For(entity.GetType());
        string key = map.KeyProperty
            ?? throw new InvalidOperationException($"{entity.GetType().Name} 没有单列主键");

        PropertyInfo? property = entity.GetType().GetProperty(key);
        return property?.GetValue(entity);
    }

    /// <summary>把实体转成 Dapper 参数包（属性名 → 值）。</summary>
    public static DynamicParameters Parameters(object entity)
    {
        EntityMap map = EntityMetadata.For(entity.GetType());
        DynamicParameters parameters = new();

        foreach (ColumnMap column in map.Columns)
        {
            PropertyInfo? property = entity.GetType().GetProperty(column.Property);
            parameters.Add(column.Property, property?.GetValue(entity));
        }

        return parameters;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
