#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 项目素材关联。对应 Go: <c>repository/repository.go</c> 的素材部分。
/// </summary>
public sealed partial class Repository
{
    // ---------------------------------------------------------------- 查询

    /// <summary>
    /// 项目下的素材（按更新时间倒序）。对应 Go: <c>ProjectAssets</c>。
    /// </summary>
    /// <remarks>
    /// 用 JOIN 而不是 IN 子查询：素材可能被多个项目引用，链接表是唯一的归属依据。
    /// </remarks>
    public async Task<IReadOnlyList<Asset>> ProjectAssetsAsync(
        string userId, string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Asset>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<Asset>("assets")}
            FROM assets
            JOIN "projectAssetLinks" ON "projectAssetLinks"."assetId" = assets.id
            WHERE assets."userId" = @userId AND "projectAssetLinks"."projectId" = @projectId
            ORDER BY assets."updatedAt" DESC
            """,
            new { userId, projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>素材的版本列表（版本号倒序）。对应 Go: <c>AssetVersions</c>。</summary>
    public async Task<IReadOnlyList<AssetVersion>> AssetVersionsAsync(
        string assetId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<AssetVersion>(
            connection,
            SqlBuilder.Select<AssetVersion>("\"assetId\" = @assetId", orderBy: "version DESC"),
            new { assetId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>素材与项目的链接。对应 Go: <c>ProjectAssetLink</c>。</summary>
    public async Task<ProjectAssetLink?> ProjectAssetLinkAsync(
        string projectId, string assetId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<ProjectAssetLink>(
            connection,
            SqlBuilder.Select<ProjectAssetLink>(
                "\"projectId\" = @projectId AND \"assetId\" = @assetId", limitOffset: " LIMIT 1"),
            new { projectId, assetId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>素材是否已加入该项目。对应 Go: <c>ProjectAssetLinked</c>。</summary>
    public async Task<bool> ProjectAssetLinkedAsync(
        string projectId, string assetId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long count = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"projectAssetLinks\" WHERE \"projectId\" = @projectId AND \"assetId\" = @assetId",
            new { projectId, assetId },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return count > 0;
    }

    /// <summary>
    /// 素材在项目内的用途角色（镜头引用 + 表现层引用，去重排序）。
    /// 对应 Go: <c>ProjectAssetUsageRoles</c>。
    /// </summary>
    public async Task<IReadOnlyList<string>> ProjectAssetUsageRolesAsync(
        string projectId, string assetId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> shotRoles = await QueryAsync<string>(
            connection,
            """
            SELECT DISTINCT "shotAssetReferences".role
            FROM "shotAssetReferences"
            JOIN shots ON shots.id = "shotAssetReferences"."shotId"
            JOIN "assetVersions" ON "assetVersions".id = "shotAssetReferences"."assetVersionId"
            WHERE shots."projectId" = @projectId AND "assetVersions"."assetId" = @assetId
            ORDER BY "shotAssetReferences".role ASC
            """,
            new { projectId, assetId },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> representationRoles = await QueryAsync<string>(
            connection,
            """
            SELECT DISTINCT "assetRepresentations".role
            FROM "assetRepresentations"
            JOIN "assetVersions" ON "assetVersions".id = "assetRepresentations"."assetVersionId"
            JOIN "projectAssetLinks" ON "projectAssetLinks"."assetId" = "assetVersions"."assetId"
            WHERE "projectAssetLinks"."projectId" = @projectId AND "assetVersions"."assetId" = @assetId
            """,
            new { projectId, assetId },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return shotRoles.Concat(representationRoles)
            .Where(role => !string.IsNullOrEmpty(role))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(role => role, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 项目内引用该素材的镜头数。对应 Go: <c>ProjectAssetShotReferenceCount</c>。
    /// </summary>
    public async Task<long> ProjectAssetShotReferenceCountAsync(
        string projectId, string assetId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long>(
            connection,
            """
            SELECT COUNT(*)
            FROM "shotAssetReferences"
            JOIN shots ON shots.id = "shotAssetReferences"."shotId"
            JOIN "assetVersions" ON "assetVersions".id = "shotAssetReferences"."assetVersionId"
            WHERE shots."projectId" = @projectId AND "assetVersions"."assetId" = @assetId
            """,
            new { projectId, assetId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 文件夹内下一个可用排序位（从 0 起）。
    /// 对应 Go: <c>NextProjectAssetPosition</c>。
    /// </summary>
    /// <remarks>
    /// 空文件夹时 <c>MAX(position)</c> 为 NULL，COALESCE 到 -1 再 +1 得到 0。
    /// </remarks>
    public async Task<long> NextProjectAssetPositionAsync(
        string projectId, string folderId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long maximum = await ScalarAsync<long>(
            connection,
            """
            SELECT COALESCE(MAX(position), -1) FROM "projectAssetLinks"
            WHERE "projectId" = @projectId AND "folderId" = @folderId
            """,
            new { projectId, folderId },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return maximum + 1;
    }

    /// <summary>
    /// 素材分页（带分类/媒体类型/状态/文件夹/标题过滤）。
    /// 对应 Go: <c>ProjectAssetsPage</c>。
    /// </summary>
    public async Task<(IReadOnlyList<Asset> Assets, long Total)> ProjectAssetsPageAsync(
        string userId, string projectId, int page, int pageSize,
        string category, string mediaType, string status, string? folderId, string queryText,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        List<string> conditions = ["assets.\"userId\" = @userId", "\"projectAssetLinks\".\"projectId\" = @projectId"];
        DynamicParameters parameters = new();
        parameters.Add("userId", userId);
        parameters.Add("projectId", projectId);

        if (category.Trim().Length > 0)
        {
            conditions.Add("assets.category = @category");
            parameters.Add("category", category.Trim());
        }
        if (mediaType.Trim().Length > 0)
        {
            // 媒体类型落在 assets.kind 上。
            conditions.Add("assets.kind = @mediaType");
            parameters.Add("mediaType", mediaType.Trim());
        }
        if (status.Trim().Length > 0)
        {
            conditions.Add("assets.status = @status");
            parameters.Add("status", status.Trim());
        }
        if (folderId is not null)
        {
            conditions.Add("\"projectAssetLinks\".\"folderId\" = @folderId");
            parameters.Add("folderId", folderId.Trim());
        }
        if (queryText.Trim().Length > 0)
        {
            conditions.Add("LOWER(assets.title) LIKE @pattern");
            parameters.Add("pattern", "%" + queryText.Trim().ToLowerInvariant() + "%");
        }

        string where = " WHERE " + string.Join(" AND ", conditions);

        long total = await ScalarAsync<long>(
            connection,
            $"SELECT COUNT(*) FROM assets JOIN \"projectAssetLinks\" ON \"projectAssetLinks\".\"assetId\" = assets.id{where}",
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Asset> assets = await QueryAsync<Asset>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<Asset>("assets")}
            FROM assets
            JOIN "projectAssetLinks" ON "projectAssetLinks"."assetId" = assets.id
            {where}
            ORDER BY assets."updatedAt" DESC
            {Dialect.LimitOffset(pageSize, (page - 1) * pageSize)}
            """,
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (assets, total);
    }

    /// <summary>
    /// 素材分面计数（按分类、按文件夹）。
    /// 对应 Go: <c>ProjectAssetFacets</c>。
    /// </summary>
    /// <remarks>
    /// 文件夹计数的 key 可能是空串（根目录素材），调用方需要保留这个键。
    /// </remarks>
    public async Task<(Dictionary<string, long> CategoryCounts, Dictionary<string, long> FolderCounts)>
        ProjectAssetFacetsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CountRow> categoryRows = await QueryAsync<CountRow>(
            connection,
            """
            SELECT assets.category AS "Key", COUNT(*) AS "Count"
            FROM assets
            JOIN "projectAssetLinks" pal ON pal."assetId" = assets.id
            WHERE pal."projectId" = @projectId
            GROUP BY assets.category
            """,
            new { projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CountRow> folderRows = await QueryAsync<CountRow>(
            connection,
            """
            SELECT "folderId" AS "Key", COUNT(*) AS "Count"
            FROM "projectAssetLinks"
            WHERE "projectId" = @projectId
            GROUP BY "folderId"
            """,
            new { projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (
            categoryRows.ToDictionary(row => row.Key, row => row.Count, StringComparer.Ordinal),
            folderRows.ToDictionary(row => row.Key, row => row.Count, StringComparer.Ordinal));
    }

    /// <summary>分面计数的行。对应 Go: <c>ProjectAssetCountRow</c>。</summary>
    public sealed class CountRow
    {
        public string Key { get; set; } = "";
        public long Count { get; set; }
    }

    // ---------------------------------------------------------------- 写操作

    /// <summary>
    /// 链接素材到项目（含首次落库与初始版本）。
    /// 对应 Go: <c>LinkProjectAsset</c>。
    /// </summary>
    /// <returns>是否真的新建了链接；false 表示并发下已被别的请求抢先链接。</returns>
    /// <remarks>
    /// 一次事务完成四件事：资产 upsert（可能尚未落库）、链接 upsert、
    /// 初始版本插入、资产域字段与项目版本号更新。
    /// 链接冲突时<b>提前返回</b>，不再动资产与版本——保证幂等重试不产生副作用。
    /// </remarks>
    public async Task<bool> LinkProjectAssetAsync(
        Asset asset, AssetVersion? version, ProjectAssetLink link, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // 资产可能已存在（重试/并发）：冲突时保留库内记录，不覆盖。
        await ExecuteAsync(
            connection,
            SqlBuilder.Insert<Asset>() + Dialect.OnConflictDoNothing("\"id\""),
            asset,
            transaction,
            cancellationToken).ConfigureAwait(false);

        int linkCreated = await ExecuteAsync(
            connection,
            SqlBuilder.Insert<ProjectAssetLink>() + Dialect.OnConflictDoNothing("\"projectId\", \"assetId\""),
            link,
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (linkCreated == 0)
        {
            // 已被链接：幂等返回，不改动资产与版本。
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (version is not null)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<AssetVersion>(), version, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        int assetAffected = await ExecuteAsync(
            connection,
            """
            UPDATE assets SET
                category = @Category, status = @Status,
                "primaryVersionId" = @PrimaryVersionID, "updatedAt" = @UpdatedAt
            WHERE id = @ID AND "userId" = @UserID
            """,
            asset,
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (assetAffected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw AppError.NotFound("素材不存在");
        }

        await BumpProjectRevisionAsync(connection, transaction, link.ProjectID, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 把素材移动到另一个文件夹并重新排序（同时递增项目版本号）。
    /// 对应 Go: <c>MoveProjectAsset</c>。
    /// </summary>
    public async Task MoveProjectAssetAsync(
        string projectId, string assetId, string folderId, long position,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int affected = await ExecuteAsync(
            connection,
            """
            UPDATE "projectAssetLinks" SET "folderId" = @folderId, position = @position
            WHERE "projectId" = @projectId AND "assetId" = @assetId
            """,
            new { folderId, position, projectId, assetId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw AppError.NotFound("素材尚未加入当前项目");
        }

        await BumpProjectRevisionAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 更新资产的域字段（分类/状态/主版本）。
    /// 对应 Go: <c>UpdateAssetDomain</c>。
    /// </summary>
    public async Task UpdateAssetDomainAsync(Asset asset, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            UPDATE assets SET
                category = @Category, status = @Status,
                "primaryVersionId" = @PrimaryVersionID, "updatedAt" = @UpdatedAt
            WHERE id = @ID AND "userId" = @UserID
            """,
            asset,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建资产版本。对应 Go: <c>CreateAssetVersion</c>。</summary>
    public async Task CreateAssetVersionAsync(AssetVersion version, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlBuilder.Insert<AssetVersion>(), version, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>在已有事务里递增项目版本号。供内部复用。</summary>
    private async Task BumpProjectRevisionAsync(
        DbConnection connection, DbTransaction transaction, string projectId, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            """
            UPDATE projects SET revision = revision + 1, "updatedAt" = @now
            WHERE id = @projectId
            """,
            new { now = DateTime.UtcNow, projectId },
            transaction,
            cancellationToken).ConfigureAwait(false);
    }
}
