namespace OpenAICanvas.Persistence;

/// <summary>
/// 软删除过滤。对应 GORM 的 <c>gorm.DeletedAt</c> 自动行为。
/// </summary>
/// <remarks>
/// Go 侧只有 3 张表使用 <c>gorm.DeletedAt</c>，GORM 会在<b>每条</b>查询上自动追加
/// <c>deleted_at IS NULL</c>。改用 Dapper 后这个行为会消失，而这 3 张表在代码里被引用
/// 76 处——漏写一处就会把已删除的渠道/逻辑模型泄漏到前台。
/// <para>
/// 因此这里把过滤做成<b>默认行为</b>：<see cref="Apply"/> 自动追加过滤，
/// 需要包含已删除记录时必须显式调用 <see cref="IncludeDeleted"/>（对应 GORM 的 <c>Unscoped()</c>）。
/// </para>
/// </remarks>
public static class SoftDelete
{
    /// <summary>使用 <c>gorm.DeletedAt</c> 的表。来源：internal/model 的 3 个 gorm.DeletedAt 字段。</summary>
    public static readonly IReadOnlySet<string> Tables = new HashSet<string>(StringComparer.Ordinal)
    {
        "channel_model_price_tiers",
        "channel_models",
        "model_channels",
    };

    /// <summary>软删除列名。GORM 固定为 <c>deleted_at</c>。</summary>
    public const string Column = "deleted_at";

    /// <summary>该表是否启用软删除。</summary>
    public static bool Applies(string table) => Tables.Contains(table);

    /// <summary>
    /// 返回软删除过滤片段，例如 <c>mc.deleted_at IS NULL</c>；非软删除表返回 null。
    /// </summary>
    /// <param name="table">表名（不带引号）。</param>
    /// <param name="alias">可选表别名。</param>
    public static string? Filter(string table, string? alias = null)
    {
        if (!Applies(table))
        {
            return null;
        }

        string prefix = string.IsNullOrEmpty(alias) ? string.Empty : alias + ".";
        return $"{prefix}{Column} IS NULL";
    }

    /// <summary>
    /// 把软删除过滤并入 WHERE 子句。非软删除表或显式包含已删除记录时原样返回。
    /// </summary>
    /// <param name="table">表名。</param>
    /// <param name="where">已有的 WHERE 条件，不含 <c>WHERE</c> 关键字；可为空。</param>
    /// <param name="alias">可选表别名。</param>
    /// <param name="includeDeleted">显式声明包含已删除记录（对应 GORM <c>Unscoped()</c>）。</param>
    public static string Apply(
        string table,
        string? where = null,
        string? alias = null,
        bool includeDeleted = false)
    {
        if (includeDeleted)
        {
            return where ?? string.Empty;
        }

        string? filter = Filter(table, alias);
        if (filter is null)
        {
            return where ?? string.Empty;
        }

        return string.IsNullOrWhiteSpace(where) ? filter : $"{filter} AND ({where})";
    }

    /// <summary>
    /// 生成带软删除过滤的 WHERE 子句，含 <c>WHERE</c> 关键字；无任何条件时返回空串。
    /// </summary>
    public static string WhereClause(
        string table,
        string? where = null,
        string? alias = null,
        bool includeDeleted = false)
    {
        string combined = Apply(table, where, alias, includeDeleted);
        return combined.Length == 0 ? string.Empty : " WHERE " + combined;
    }

    /// <summary>
    /// 软删除：把 <c>deleted_at</c> 置为当前时间。
    /// 对应 GORM 的 <c>Delete()</c>——它执行 UPDATE 而不是 DELETE。
    /// </summary>
    public static string SoftDeleteStatement(string table, string keyColumn) =>
        $"UPDATE {table} SET {Column} = @deletedAt WHERE {keyColumn} = @id";

    /// <summary>物理删除。对应 GORM 的 <c>Unscoped().Delete()</c>。</summary>
    public static string HardDeleteStatement(string table, string keyColumn) =>
        $"DELETE FROM {table} WHERE {keyColumn} = @id";

    /// <summary>恢复软删除记录。对应 GORM 的 <c>Unscoped().Update("deleted_at", nil)</c>。</summary>
    public static string RestoreStatement(string table, string keyColumn) =>
        $"UPDATE {table} SET {Column} = NULL WHERE {keyColumn} = @id";
}
