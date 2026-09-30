using System.Data.Common;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 画布工具库仓储。对应 Go: <c>repository/tools.go</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>
    /// 内置工具按 id 幂等写入；只允许覆盖 source='builtin' 的行，created_at 保留首次写入值。
    /// 对应 Go: <c>UpsertBuiltinTools</c>。
    /// </summary>
    public Task UpsertBuiltinToolsAsync(
        IReadOnlyList<Tool> tools, CancellationToken cancellationToken = default) =>
        InTransactionAsync(async (connection, transaction) =>
        {
            if (tools.Count == 0)
            {
                return true;
            }
            long collisions = await ScalarAsync<long>(
                connection,
                $"SELECT COUNT(*) FROM {Quote("tools")} WHERE {Quote("id")} IN @ids AND {Quote("source")} <> 'builtin'",
                new { ids = tools.Select(tool => tool.ID).ToArray() },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (collisions > 0)
            {
                throw new InvalidOperationException("内置工具 ID 与用户工具冲突，拒绝覆盖");
            }

            foreach (Tool tool in tools)
            {
                await ExecuteAsync(
                    connection,
                    UpsertToolSql,
                    SqlBuilder.Parameters(tool),
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }

            // 内置工具显式写主键，PostgreSQL 序列需要前移到当前最大值。
            if (Dialect.IsPostgres)
            {
                await ExecuteAsync(
                    connection,
                    "SELECT setval(pg_get_serial_sequence('tools', 'id'), (SELECT COALESCE(MAX(id), 1) FROM tools), true)",
                    transaction: transaction,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            return true;
        }, cancellationToken);

    /// <summary>工具 upsert 语句：冲突时仅覆盖内置工具行，保留 created_at。</summary>
    private string UpsertToolSql => string.Join('\n',
        $"INSERT INTO {Quote("tools")} ({Quote("id")}, {Quote("type")}, {Quote("labelEn")}, {Quote("label")}, {Quote("desc")}, {Quote("tag")}, {Quote("cover")}, {Quote("extraInfoJson")}, {Quote("prompt")}, {Quote("ratio")}, {Quote("mediaUrl")}, {Quote("ownerId")}, {Quote("source")}, {Quote("enabled")}, {Quote("visibility")}, {Quote("sortWeight")}, {Quote("createdAt")}, {Quote("updatedAt")})",
        $"VALUES (@ID, @Type, @LabelEn, @Label, @Desc, @Tag, @Cover, @ExtraInfoJSON, @Prompt, @Ratio, @MediaURL, @OwnerID, @Source, @Enabled, @Visibility, @SortWeight, @CreatedAt, @UpdatedAt)",
        $"ON CONFLICT ({Quote("id")}) DO UPDATE SET",
        $"{Quote("type")} = excluded.{Quote("type")}, {Quote("labelEn")} = excluded.{Quote("labelEn")}, {Quote("label")} = excluded.{Quote("label")}, {Quote("desc")} = excluded.{Quote("desc")}, {Quote("tag")} = excluded.{Quote("tag")}, {Quote("cover")} = excluded.{Quote("cover")}, {Quote("extraInfoJson")} = excluded.{Quote("extraInfoJson")}, {Quote("prompt")} = excluded.{Quote("prompt")}, {Quote("ratio")} = excluded.{Quote("ratio")}, {Quote("mediaUrl")} = excluded.{Quote("mediaUrl")}, {Quote("ownerId")} = excluded.{Quote("ownerId")}, {Quote("source")} = excluded.{Quote("source")}, {Quote("enabled")} = excluded.{Quote("enabled")}, {Quote("visibility")} = excluded.{Quote("visibility")}, {Quote("sortWeight")} = excluded.{Quote("sortWeight")}, {Quote("updatedAt")} = excluded.{Quote("updatedAt")}",
        $"WHERE {Quote("tools")}.{Quote("source")} = 'builtin'");

    /// <summary>
    /// 按范围分页查询工具列表并携带收藏状态。对应 Go: <c>ListTools</c>。
    /// 列表不取 extra_info_json / prompt 两个大字段。
    /// </summary>
    public async Task<(List<ToolListRow> Items, long Total)> ListToolsAsync(
        string userID, string scope, string type, string tag, string search,
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        string where =
            $"{Quote("tools")}.{Quote("enabled")} = @enabled AND ({Quote("tools")}.{Quote("visibility")} = 'public' OR {Quote("tools")}.{Quote("ownerId")} = @userID)";
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
        {
            ["enabled"] = Dialect.Boolean(true),
            ["userID"] = userID,
        };
        string join;
        switch (scope)
        {
            case "favorites":
                join = $"JOIN {Quote("toolFavorites")} tf ON tf.{Quote("toolId")} = {Quote("tools")}.{Quote("id")} AND tf.{Quote("userId")} = @userID";
                where += $" AND tf.{Quote("userId")} = @userID";
                break;
            case "custom":
                join = $"LEFT JOIN {Quote("toolFavorites")} tf ON tf.{Quote("toolId")} = {Quote("tools")}.{Quote("id")} AND tf.{Quote("userId")} = @userID";
                where += $" AND {Quote("tools")}.{Quote("ownerId")} = @userID AND {Quote("tools")}.{Quote("source")} = 'user'";
                break;
            default: // public
                join = $"LEFT JOIN {Quote("toolFavorites")} tf ON tf.{Quote("toolId")} = {Quote("tools")}.{Quote("id")} AND tf.{Quote("userId")} = @userID";
                where += $" AND {Quote("tools")}.{Quote("visibility")} = 'public'";
                break;
        }
        if (type.Length > 0)
        {
            where += $" AND {Quote("tools")}.{Quote("type")} = @type";
            parameters["type"] = type;
        }
        if (tag.Length > 0)
        {
            where += $" AND {Quote("tools")}.{Quote("tag")} = @tag";
            parameters["tag"] = tag;
        }
        if (search.Length > 0)
        {
            where += $" AND (LOWER({Quote("tools")}.{Quote("label")}) LIKE @keyword OR LOWER({Quote("tools")}.{Quote("labelEn")}) LIKE @keyword OR LOWER({Quote("tools")}.{Quote("desc")}) LIKE @keyword)";
            parameters["keyword"] = "%" + search.ToLowerInvariant() + "%";
        }

        long? total = await ScalarAsync<long?>(
            connection,
            $"SELECT COUNT(*) FROM {Quote("tools")} {join} WHERE {where}",
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string orderBy = scope == "favorites"
            ? $"tf.{Quote("createdAt")} DESC, {Quote("tools")}.{Quote("id")} DESC"
            : $"{Quote("tools")}.{Quote("sortWeight")} ASC, {Quote("tools")}.{Quote("id")} ASC";
        parameters["limit"] = pageSize;
        parameters["offset"] = (page - 1) * pageSize;

        // 列摘要显式别名，避免 JOIN 后列名歧义（Dapper 不做表名推断）。
        string columns = string.Join(", ",
            $"{Quote("tools")}.{Quote("id")} AS {Quote("ID")}",
            $"{Quote("tools")}.{Quote("type")} AS {Quote("Type")}",
            $"{Quote("tools")}.{Quote("labelEn")} AS {Quote("LabelEn")}",
            $"{Quote("tools")}.{Quote("label")} AS {Quote("Label")}",
            $"{Quote("tools")}.{Quote("desc")} AS {Quote("Desc")}",
            $"{Quote("tools")}.{Quote("tag")} AS {Quote("Tag")}",
            $"{Quote("tools")}.{Quote("cover")} AS {Quote("Cover")}",
            $"{Quote("tools")}.{Quote("ratio")} AS {Quote("Ratio")}",
            $"{Quote("tools")}.{Quote("mediaUrl")} AS {Quote("MediaURL")}",
            $"{Quote("tools")}.{Quote("ownerId")} AS {Quote("OwnerID")}",
            $"{Quote("tools")}.{Quote("source")} AS {Quote("Source")}",
            $"{Quote("tools")}.{Quote("enabled")} AS {Quote("Enabled")}",
            $"{Quote("tools")}.{Quote("visibility")} AS {Quote("Visibility")}",
            $"{Quote("tools")}.{Quote("sortWeight")} AS {Quote("SortWeight")}",
            $"{Quote("tools")}.{Quote("createdAt")} AS {Quote("CreatedAt")}",
            $"{Quote("tools")}.{Quote("updatedAt")} AS {Quote("UpdatedAt")}",
            $"tf.{Quote("id")} AS {Quote("FavoriteRowID")}",
            $"tf.{Quote("createdAt")} AS {Quote("FavoritedAt")}");
        List<ToolListRow> items = (await QueryAsync<ToolListRow>(
            connection,
            $"SELECT {columns} FROM {Quote("tools")} {join} WHERE {where} ORDER BY {orderBy} LIMIT @limit OFFSET @offset",
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        return (items, total ?? 0);
    }

    /// <summary>校验可见性后返回工具。对应 Go: <c>ToolForUser</c>。</summary>
    public async Task<Tool?> ToolForUserAsync(
        string userID, long toolID, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        Tool? tool = await FirstOrDefaultAsync<Tool>(
            connection,
            SqlBuilder.Select<Tool>(
                $"{Quote("id")} = @toolID AND {Quote("enabled")} = @enabled AND ({Quote("visibility")} = 'public' OR {Quote("ownerId")} = @userID)",
                limitOffset: " LIMIT 1"),
            new { toolID, enabled = Dialect.Boolean(true), userID },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (tool is null)
        {
            throw AppError.New(404, "工具不存在或不可见");
        }
        return tool;
    }

    /// <summary>返回工具与收藏时间（未收藏为 null）。对应 Go: <c>ToolFavorited</c>。</summary>
    public async Task<(Tool Tool, DateTime? FavoritedAt)> ToolFavoritedAsync(
        string userID, long toolID, CancellationToken cancellationToken = default)
    {
        Tool tool = (await ToolForUserAsync(userID, toolID, cancellationToken).ConfigureAwait(false))!;
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ToolFavorite? favorite = await FirstOrDefaultAsync<ToolFavorite>(
            connection,
            SqlBuilder.Select<ToolFavorite>(
                $"{Quote("userId")} = @userID AND {Quote("toolId")} = @toolID", limitOffset: " LIMIT 1"),
            new { userID, toolID },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (tool, favorite?.CreatedAt);
    }

    /// <summary>幂等添加收藏。对应 Go: <c>AddToolFavorite</c>。</summary>
    public async Task AddToolFavoriteAsync(
        string userID, long toolID, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        string conflict = Dialect.IsPostgres
            ? "ON CONFLICT (\"userId\", \"toolId\") DO NOTHING"
            : "ON CONFLICT (\"userId\", \"toolId\") DO NOTHING";
        await ExecuteAsync(
            connection,
            $"INSERT INTO {Quote("toolFavorites")} ({Quote("userId")}, {Quote("toolId")}, {Quote("createdAt")}) VALUES (@userID, @toolID, @now) {conflict}",
            new { userID, toolID, now = DateTime.UtcNow },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取消收藏（幂等）。对应 Go: <c>RemoveToolFavorite</c>。</summary>
    public async Task RemoveToolFavoriteAsync(
        string userID, long toolID, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            $"DELETE FROM {Quote("toolFavorites")} WHERE {Quote("userId")} = @userID AND {Quote("toolId")} = @toolID",
            new { userID, toolID },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 创建用户自定义工具；自增主键回填到实体（对应 GORM Create 的回填行为）。
    /// 对应 Go: <c>CreateTool</c>。
    /// </summary>
    public async Task<Tool> CreateToolAsync(
        Tool tool, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        string columns = string.Join(", ",
            Quote("type"), Quote("labelEn"), Quote("label"), Quote("desc"), Quote("tag"), Quote("cover"),
            Quote("extraInfoJson"), Quote("prompt"), Quote("ratio"), Quote("mediaUrl"), Quote("ownerId"),
            Quote("source"), Quote("enabled"), Quote("visibility"), Quote("sortWeight"),
            Quote("createdAt"), Quote("updatedAt"));
        string values = "@Type, @LabelEn, @Label, @Desc, @Tag, @Cover, @ExtraInfoJSON, @Prompt, @Ratio, " +
                        "@MediaURL, @OwnerID, @Source, @Enabled, @Visibility, @SortWeight, @CreatedAt, @UpdatedAt";
        if (Dialect.IsPostgres)
        {
            long? id = await ScalarAsync<long?>(
                connection,
                $"INSERT INTO {Quote("tools")} ({columns}) VALUES ({values}) RETURNING {Quote("id")}",
                SqlBuilder.Parameters(tool),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            tool.ID = id ?? 0;
        }
        else
        {
            await ExecuteAsync(
                connection,
                $"INSERT INTO {Quote("tools")} ({columns}) VALUES ({values})",
                SqlBuilder.Parameters(tool),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            tool.ID = await ScalarAsync<long>(
                connection,
                "SELECT last_insert_rowid()",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return tool;
    }

    /// <summary>删除自己的自定义工具并清理收藏。对应 Go: <c>DeleteUserTool</c>（事务）。</summary>
    public Task DeleteUserToolAsync(
        string userID, long toolID, CancellationToken cancellationToken = default) =>
        InTransactionAsync(async (connection, transaction) =>
        {
            Tool? tool = await FirstOrDefaultAsync<Tool>(
                connection,
                SqlBuilder.Select<Tool>(
                    $"{Quote("id")} = @toolID AND {Quote("ownerId")} = @userID AND {Quote("source")} = 'user'",
                    limitOffset: " LIMIT 1"),
                new { toolID, userID },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (tool is null)
            {
                throw AppError.New(404, "工具不存在或不支持删除");
            }
            await ExecuteAsync(
                connection,
                $"DELETE FROM {Quote("toolFavorites")} WHERE {Quote("toolId")} = @toolID",
                new { toolID },
                transaction,
                cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                $"DELETE FROM {Quote("tools")} WHERE {Quote("id")} = @toolID",
                new { toolID },
                transaction,
                cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
}

/// <summary>工具列表行：摘要字段 + 收藏状态。对应 Go: <c>tools.ToolWithFavorite</c> 的查询投影。</summary>
public sealed class ToolListRow
{
    public long ID { get; set; }

    public string Type { get; set; } = "";

    public string LabelEn { get; set; } = "";

    public string Label { get; set; } = "";

    public string Desc { get; set; } = "";

    public string Tag { get; set; } = "";

    public string Cover { get; set; } = "";

    public string Ratio { get; set; } = "";

    public string MediaURL { get; set; } = "";

    public string OwnerID { get; set; } = "";

    public string Source { get; set; } = "";

    public bool Enabled { get; set; }

    public string Visibility { get; set; } = "";

    public long SortWeight { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public long? FavoriteRowID { get; set; }

    public DateTime? FavoritedAt { get; set; }
}
