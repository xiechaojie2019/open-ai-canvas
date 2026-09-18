#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>项目素材文件夹仓储。对应 Go: <c>repository.ProjectAssetFolder*</c>。</summary>
public sealed partial class Repository
{
    /// <summary>项目素材文件夹全量。对应 Go: <c>ProjectAssetFolders</c>。</summary>
    public async Task<IReadOnlyList<ProjectAssetFolder>> ProjectAssetFoldersAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ProjectAssetFolder>(
            connection,
            SqlBuilder.Select<ProjectAssetFolder>(
                "project_id = @projectId", "parent_id ASC, position ASC, created_at ASC"),
            new { projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按项目 + ID 查文件夹。对应 Go: <c>ProjectAssetFolder</c>。</summary>
    public async Task<ProjectAssetFolder?> ProjectAssetFolderAsync(
        string projectId, string folderId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<ProjectAssetFolder>(
            connection,
            SqlBuilder.Select<ProjectAssetFolder>(
                "id = @folderId AND project_id = @projectId", limitOffset: " LIMIT 1"),
            new { folderId, projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建文件夹并递增项目版本。对应 Go: <c>CreateProjectAssetFolder</c>。</summary>
    public async Task CreateProjectAssetFolderAsync(
        ProjectAssetFolder folder, CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                SqlBuilder.Insert(typeof(ProjectAssetFolder)), folder, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                "UPDATE \"projects\" SET \"revision\" = \"revision\" + 1, \"updated_at\" = @now WHERE \"id\" = @projectId",
                new { now = folder.UpdatedAt, projectId = folder.ProjectID },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>更新文件夹并递增项目版本。对应 Go: <c>UpdateProjectAssetFolder</c>。</summary>
    public async Task<bool> UpdateProjectAssetFolderAsync(
        ProjectAssetFolder folder, CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            int updated = await ExecuteAsync(
                connection,
                """
                UPDATE "project_asset_folders" SET "parent_id" = @ParentID, "name" = @Name,
                  "name_key" = @NameKey, "style" = @Style, "theme" = @Theme,
                  "position" = @Position, "updated_at" = @UpdatedAt
                WHERE "id" = @ID AND "project_id" = @ProjectID
                """,
                folder, transaction, cancellationToken).ConfigureAwait(false);
            if (updated != 1)
            {
                throw new InvalidOperationException("record not found");
            }
            await ExecuteAsync(
                connection,
                "UPDATE \"projects\" SET \"revision\" = \"revision\" + 1, \"updated_at\" = @now WHERE \"id\" = @projectId",
                new { now = folder.UpdatedAt, projectId = folder.ProjectID },
                transaction,
                cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除文件夹的结果。对应 Go 的 <c>ErrProjectAssetFolderNotEmpty</c>。</summary>
    public enum DeleteFolderOutcome
    {
        Ok,
        NotFound,
        NotEmpty,
    }

    /// <summary>删除空文件夹并递增项目版本。对应 Go: <c>DeleteProjectAssetFolder</c>。</summary>
    public async Task<DeleteFolderOutcome> DeleteProjectAssetFolderAsync(
        string projectId, string folderId, DateTime now, CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            long childCount = await ScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM \"project_asset_folders\" WHERE \"project_id\" = @projectId AND \"parent_id\" = @folderId",
                new { projectId, folderId }, transaction, cancellationToken).ConfigureAwait(false);
            long assetCount = await ScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM \"project_asset_links\" WHERE \"project_id\" = @projectId AND \"folder_id\" = @folderId",
                new { projectId, folderId }, transaction, cancellationToken).ConfigureAwait(false);
            if (childCount > 0 || assetCount > 0)
            {
                return DeleteFolderOutcome.NotEmpty;
            }
            int deleted = await ExecuteAsync(
                connection,
                "DELETE FROM \"project_asset_folders\" WHERE \"id\" = @folderId AND \"project_id\" = @projectId",
                new { folderId, projectId }, transaction, cancellationToken).ConfigureAwait(false);
            if (deleted != 1)
            {
                return DeleteFolderOutcome.NotFound;
            }
            await ExecuteAsync(
                connection,
                "UPDATE \"projects\" SET \"revision\" = \"revision\" + 1, \"updated_at\" = @now WHERE \"id\" = @projectId",
                new { now, projectId }, transaction, cancellationToken).ConfigureAwait(false);
            return DeleteFolderOutcome.Ok;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>项目素材链接（含素材本体）。对应 Go: <c>ProjectAssets</c> 的读取部分。</summary>
    public async Task<IReadOnlyList<ProjectAssetLink>> ProjectAssetLinksAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ProjectAssetLink>(
            connection,
            SqlBuilder.Select<ProjectAssetLink>("project_id = @projectId", "created_at ASC"),
            new { projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 批量取素材（不限用户，供项目素材视图）。对应 Go 的素材批量读取。</summary>
    public async Task<IReadOnlyList<Asset>> AssetsByIDsAsync(
        IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Asset>(
            connection,
            SqlBuilder.Select<Asset>("id IN @ids"),
            new { ids },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>更新项目素材链接（分类/排序）。对应 Go: <c>UpdateProjectAsset</c> 的链接写入。</summary>
    public async Task UpdateProjectAssetLinkAsync(
        ProjectAssetLink link, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE \"project_asset_links\" SET \"folder_id\" = @FolderID, \"position\" = @Position WHERE \"id\" = @ID AND \"project_id\" = @ProjectID",
            link,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除项目素材链接。对应 Go: <c>UnlinkProjectAsset</c>。</summary>
    public async Task DeleteProjectAssetLinkAsync(
        string projectId, string assetId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM \"project_asset_links\" WHERE \"project_id\" = @projectId AND \"asset_id\" = @assetId",
            new { projectId, assetId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}