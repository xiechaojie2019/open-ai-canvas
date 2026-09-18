#nullable enable
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 素材库（assets / asset_folders）仓储方法。
/// 对应 Go: <c>repository/asset_library.go</c> 与 <c>repository/repository.go</c> 的素材部分。
/// </summary>
public sealed partial class Repository
{
    /// <summary>素材分页过滤条件。对应 Go: <c>repository.UserAssetPageFilter</c>。</summary>
    public sealed record UserAssetPageFilter(
        string Kind,
        string Category,
        string? FolderID,
        bool Uncategorized,
        string Status,
        string Query);

    /// <summary>分面统计行。对应 Go: <c>repository.UserAssetFacetRow</c>。</summary>
    public sealed record UserAssetFacetRow(string Key, long Count);

    /// <summary>素材分页（含标题与 payload 搜索）。对应 Go: <c>UserAssetsPage</c>。</summary>
    public async Task<(IReadOnlyList<Asset> Assets, long Total)> UserAssetsPageAsync(
        string userId,
        int page,
        int pageSize,
        UserAssetPageFilter filter,
        CancellationToken cancellationToken = default)
    {
        (string where, DynamicParameters parameters) = BuildAssetFilter(userId, filter, includeSearch: true);

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long total = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"assets\" WHERE " + where,
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        parameters.Add("limit", pageSize);
        parameters.Add("offset", (page - 1) * pageSize);
        IReadOnlyList<Asset> assets = await QueryAsync<Asset>(
            connection,
            SqlBuilder.Select<Asset>(
                where, "updated_at DESC, id DESC", Dialect.LimitOffset(pageSize, (page - 1) * pageSize)),
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (assets, total);
    }

    /// <summary>kind/category/folder 三组分面统计。对应 Go: <c>UserAssetFacets</c>。</summary>
    public async Task<(IReadOnlyList<UserAssetFacetRow> Kind, IReadOnlyList<UserAssetFacetRow> Category, IReadOnlyList<UserAssetFacetRow> Folder)>
        UserAssetFacetsAsync(string userId, string status, CancellationToken cancellationToken = default)
    {
        (string where, DynamicParameters parameters) = BuildAssetFilter(
            userId, new UserAssetPageFilter("", "", null, false, status, ""), includeSearch: false);

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        async Task<List<UserAssetFacetRow>> FacetAsync(string column)
        {
            List<UserAssetFacetRow> rows = (await QueryAsync<UserAssetFacetRow>(
                connection,
                $"SELECT {column} AS \"Key\", COUNT(*) AS \"Count\" FROM \"assets\" WHERE {where} GROUP BY {column}",
                parameters,
                cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
            return rows;
        }

        List<UserAssetFacetRow> kind = await FacetAsync("\"kind\"").ConfigureAwait(false);
        List<UserAssetFacetRow> category = await FacetAsync("\"category\"").ConfigureAwait(false);
        List<UserAssetFacetRow> folder = await FacetAsync("\"folder_id\"").ConfigureAwait(false);
        return (kind, category, folder);
    }

    private (string Where, DynamicParameters Parameters) BuildAssetFilter(
        string userId, UserAssetPageFilter filter, bool includeSearch)
    {
        List<string> conditions = ["user_id = @userId"];
        DynamicParameters parameters = new();
        parameters.Add("userId", userId);

        if (!string.IsNullOrWhiteSpace(filter.Kind))
        {
            conditions.Add("kind = @kind");
            parameters.Add("kind", filter.Kind.Trim());
        }
        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            conditions.Add("category = @category");
            parameters.Add("category", filter.Category.Trim());
        }
        if (filter.Uncategorized)
        {
            conditions.Add("folder_id = ''");
        }
        else if (filter.FolderID is not null)
        {
            conditions.Add("folder_id = @folderId");
            parameters.Add("folderId", filter.FolderID.Trim());
        }
        switch (filter.Status?.Trim())
        {
            case "active":
                conditions.Add("status <> @statusArchived");
                parameters.Add("statusArchived", "archived");
                break;
            case "archived":
                conditions.Add("status = @statusArchived");
                parameters.Add("statusArchived", "archived");
                break;
            case "":
            case null:
                break;
            default:
                conditions.Add("status = @status");
                parameters.Add("status", filter.Status.Trim());
                break;
        }
        if (includeSearch)
        {
            string query = filter.Query.Trim().ToLowerInvariant();
            if (query.Length > 0)
            {
                conditions.Add("(LOWER(title) LIKE @pattern OR LOWER(payload_json) LIKE @pattern)");
                parameters.Add("pattern", "%" + query + "%");
            }
        }
        return (string.Join(" AND ", conditions), parameters);
    }

    /// <summary>用户全量素材。对应 Go: <c>Assets</c>。</summary>
    public async Task<IReadOnlyList<Asset>> AssetsAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Asset>(
            connection,
            SqlBuilder.Select<Asset>("user_id = @userId", "updated_at DESC"),
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>素材摘要（不含 payload）。对应 Go: <c>AssetSummaries</c>。</summary>
    public async Task<IReadOnlyList<Asset>> AssetSummariesAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Asset>(
            connection,
            SqlBuilder.SelectColumns<Asset>(
                ["ID", "FolderID", "Kind", "Category", "Status", "PrimaryVersionID", "Title", "CreatedAt", "UpdatedAt"],
                "user_id = @userId",
                "updated_at DESC"),
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按用户 + ID 查素材。对应 Go: <c>AssetForUser</c>。</summary>
    public async Task<Asset?> AssetForUserAsync(string userId, string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<Asset>(
            connection,
            SqlBuilder.Select<Asset>("id = @id AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { id, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>素材 upsert：已有行更新，未命中插入。对应 Go: <c>UpsertAsset</c>。</summary>
    public async Task UpsertAssetAsync(Asset asset, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            "UPDATE \"assets\" SET \"folder_id\" = @FolderID, \"kind\" = @Kind, \"category\" = @Category, \"status\" = @Status, \"primary_version_id\" = @PrimaryVersionID, \"title\" = @Title, \"payload_json\" = @PayloadJSON, \"updated_at\" = @UpdatedAt WHERE \"id\" = @ID AND \"user_id\" = @UserID",
            asset,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (updated > 0)
        {
            return;
        }
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(Asset)),
            asset,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 素材分类

    /// <summary>素材分类列表。对应 Go: <c>AssetFolders</c>。</summary>
    public async Task<IReadOnlyList<AssetFolder>> AssetFoldersAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<AssetFolder>(
            connection,
            SqlBuilder.Select<AssetFolder>("user_id = @userId", "position ASC, created_at ASC"),
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按用户 + ID 查分类。对应 Go: <c>AssetFolderForUser</c>。</summary>
    public async Task<AssetFolder?> AssetFolderForUserAsync(
        string userId, string folderId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<AssetFolder>(
            connection,
            SqlBuilder.Select<AssetFolder>("id = @folderId AND user_id = @userId", limitOffset: " LIMIT 1"),
            new { folderId, userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>同名分类（可排除自身）。对应 Go: <c>AssetFolderNameExists</c>。</summary>
    public async Task<bool> AssetFolderNameExistsAsync(
        string userId, string nameKey, string excludeId, CancellationToken cancellationToken = default)
    {
        string condition = "user_id = @userId AND name_key = @nameKey";
        if (excludeId.Length > 0)
        {
            condition += " AND id <> @excludeId";
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long count = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"asset_folders\" WHERE " + condition,
            new { userId, nameKey, excludeId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return count > 0;
    }

    /// <summary>下一个分类排序位。对应 Go: <c>NextAssetFolderPosition</c>。</summary>
    public async Task<long> NextAssetFolderPositionAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long? maximum = await ScalarAsync<long?>(
            connection,
            "SELECT COALESCE(MAX(\"position\"), -1) FROM \"asset_folders\" WHERE \"user_id\" = @userId",
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (maximum ?? -1) + 1;
    }

    /// <summary>创建分类。对应 Go: <c>CreateAssetFolder</c>。</summary>
    public async Task CreateAssetFolderAsync(
        AssetFolder folder, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(AssetFolder)),
            folder,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>更新分类名。对应 Go: <c>UpdateAssetFolder</c>（命中 0 行视为不存在）。</summary>
    public async Task UpdateAssetFolderAsync(
        AssetFolder folder, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            "UPDATE \"asset_folders\" SET \"name\" = @Name, \"name_key\" = @NameKey, \"updated_at\" = @UpdatedAt WHERE \"id\" = @ID AND \"user_id\" = @UserID",
            folder,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new InvalidOperationException("record not found");
        }
    }

    /// <summary>
    /// 删除分类：其下素材先移出（回收 payload 内 folderId），再删分类。
    /// 对应 Go: <c>DeleteAssetFolder</c>。
    /// </summary>
    public async Task DeleteAssetFolderAsync(
        string userId, string folderId, DateTime now, CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            AssetFolder? folder = await FirstOrDefaultAsync<AssetFolder>(
                connection,
                SqlBuilder.Select<AssetFolder>("id = @folderId AND user_id = @userId", limitOffset: " LIMIT 1"),
                new { folderId, userId },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (folder is null)
            {
                throw new InvalidOperationException("record not found");
            }

            IReadOnlyList<Asset> assets = await QueryAsync<Asset>(
                connection,
                SqlBuilder.Select<Asset>("user_id = @userId AND folder_id = @folderId"),
                new { userId, folderId },
                transaction,
                cancellationToken).ConfigureAwait(false);
            string[] ids = assets.Select(asset => asset.ID).ToArray();
            await MoveUserAssetsToFolderAsync(
                connection, transaction, userId, ids, "", now, cancellationToken).ConfigureAwait(false);

            int deleted = await ExecuteAsync(
                connection,
                "DELETE FROM \"asset_folders\" WHERE \"id\" = @folderId AND \"user_id\" = @userId",
                new { folderId, userId },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (deleted != 1)
            {
                throw new InvalidOperationException("record not found");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 移动素材到分类：更新 folder_id、回写 payload.folderId/updatedAt。
    /// 对应 Go: <c>moveUserAssetsToFolder</c>（命中数不符视为不存在）。
    /// </summary>
    public async Task MoveUserAssetsToFolderAsync(
        string userId, IReadOnlyList<string> assetIds, string folderId, DateTime now,
        CancellationToken cancellationToken = default)
    {
        if (assetIds.Count == 0)
        {
            return;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await InTransactionAsync(async (connection, transaction) =>
        {
            await MoveUserAssetsToFolderAsync(
                connection, transaction, userId, assetIds, folderId, now, cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task MoveUserAssetsToFolderAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string userId,
        IReadOnlyList<string> assetIds,
        string folderId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (assetIds.Count == 0)
        {
            return;
        }
        List<Asset> assets = (await QueryAsync<Asset>(
            connection,
            SqlBuilder.Select<Asset>("user_id = @userId AND id IN @ids"),
            new { userId, ids = assetIds },
            transaction,
            cancellationToken).ConfigureAwait(false)).ToList();
        if (assets.Count != assetIds.Count)
        {
            throw new InvalidOperationException("record not found");
        }
        foreach (Asset asset in assets)
        {
            string payloadJSON = AssetPayloadWithFolder(asset.PayloadJSON, folderId, now);
            await ExecuteAsync(
                connection,
                "UPDATE \"assets\" SET \"folder_id\" = @folderId, \"payload_json\" = @payloadJSON, \"updated_at\" = @now WHERE \"id\" = @id AND \"user_id\" = @userId",
                new { folderId, payloadJSON, now, id = asset.ID, userId },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>对应 Go: <c>assetPayloadWithFolder</c>（folderId 空则删除键，updatedAt 用 RFC3339Nano）。</summary>
    private static string AssetPayloadWithFolder(string payloadJSON, string folderId, DateTime updatedAt)
    {
        JsonElement element;
        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(payloadJSON);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("asset payload is not valid JSON");
        }
        Dictionary<string, JsonElement> payload = element.ValueKind == JsonValueKind.Object
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : [];
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name == "folderId" || property.Name == "updatedAt")
                {
                    continue;
                }
                payload[property.Name] = property.Value.Clone();
            }
        }
        if (folderId.Length > 0)
        {
            payload["folderId"] = JsonSerializer.SerializeToElement(folderId);
        }
        payload["updatedAt"] = JsonSerializer.SerializeToElement(
            updatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
        return JsonSerializer.Serialize(
            new Dictionary<string, JsonElement>(payload.OrderBy(pair => pair.Key, StringComparer.Ordinal), StringComparer.Ordinal));
    }
}
