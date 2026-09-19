#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>技能版本与文件清单仓储。对应 Go: <c>repository</c> 的 skill_versions/skill_files。</summary>
public sealed partial class Repository
{
    /// <summary>按 ID 查技能版本。对应 Go: <c>SkillVersion</c>。</summary>
    public async Task<SkillVersion?> SkillVersionAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<SkillVersion>(
            connection,
            SqlBuilder.Select<SkillVersion>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>版本的文件清单（路径升序）。对应 Go: <c>SkillFiles</c>。</summary>
    public async Task<IReadOnlyList<SkillFile>> SkillFilesAsync(
        string versionId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<SkillFile>(
            connection,
            SqlBuilder.Select<SkillFile>("skill_version_id = @versionId", "path ASC"),
            new { versionId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
