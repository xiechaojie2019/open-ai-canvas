#nullable enable
using System.Data.Common;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>方舟可信素材绑定仓储。对应 Go: <c>repository.ArkPrivateAssetBinding</c> 系列。</summary>
public sealed partial class Repository
{
    /// <summary>按资源与项目查询绑定。对应 Go: <c>ArkPrivateAssetBinding</c>。</summary>
    public async Task<ArkPrivateAssetBinding?> ArkPrivateAssetBindingAsync(
        string resourceId, string projectName, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<ArkPrivateAssetBinding>(
            connection,
            SqlBuilder.Select<ArkPrivateAssetBinding>(
                "\"resourceId\" = @resourceId AND \"projectName\" = @projectName",
                limitOffset: " LIMIT 1"),
            new { resourceId, projectName },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 插入绑定。并发任务同时为同一资源建绑时只有一个能插入，返回是否由本次调用插入。
    /// 对应 Go: <c>CreateArkPrivateAssetBinding</c>（依赖唯一索引，冲突时返回 false）。
    /// </summary>
    public async Task<bool> CreateArkPrivateAssetBindingAsync(
        ArkPrivateAssetBinding binding, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await ExecuteAsync(
            connection,
            SqlBuilder.Insert<ArkPrivateAssetBinding>(onConflictDoNothing: true),
            binding,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }

    /// <summary>保存绑定状态与方舟素材 ID。对应 Go: <c>SaveArkPrivateAssetBinding</c>。</summary>
    public async Task SaveArkPrivateAssetBindingAsync(
        ArkPrivateAssetBinding binding, CancellationToken cancellationToken = default)
    {
        binding.UpdatedAt = DateTime.UtcNow;
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            SqlBuilder.Update(
                typeof(ArkPrivateAssetBinding),
                ["UserID", "ResourceID", "ProjectName", "AssetGroupID", "ArkAssetID", "Status", "Error", "CreatedAt", "UpdatedAt"]),
            binding,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
