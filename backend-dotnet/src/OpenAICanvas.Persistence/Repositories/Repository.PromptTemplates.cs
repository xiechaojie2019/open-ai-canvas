#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 提示词模板版本。对应 Go: <c>repository/repository.go</c> 的模板部分。
/// </summary>
/// <remarks>
/// <b>每个操作最多一个启用版本</b>——由 <see cref="SavePromptTemplateAsync"/> 保证：
/// 保存启用版本时先把同操作的其他版本全部停用，与 Go 的写法一致。
/// </remarks>
public sealed partial class Repository
{
    /// <summary>全部模板（按操作升序、版本降序）。对应 Go: <c>PromptTemplates</c>。</summary>
    public async Task<IReadOnlyList<PromptTemplate>> PromptTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<PromptTemplate>(
            connection,
            SqlBuilder.Select<PromptTemplate>(orderBy: "operation ASC, version DESC"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 查模板。对应 Go: <c>PromptTemplate</c>。</summary>
    public async Task<PromptTemplate?> PromptTemplateAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<PromptTemplate>(
            connection,
            SqlBuilder.Select<PromptTemplate>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>某操作的启用版本（版本号最大）。对应 Go: <c>ActivePromptTemplate</c>。</summary>
    public async Task<PromptTemplate?> ActivePromptTemplateAsync(
        string operation, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<PromptTemplate>(
            connection,
            SqlBuilder.Select<PromptTemplate>(
                "operation = @operation AND enabled = @enabled", orderBy: "version DESC", limitOffset: " LIMIT 1"),
            new { operation, enabled = true },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>某操作的模板数量。对应 Go: <c>PromptTemplateCount</c>。</summary>
    public async Task<long> PromptTemplateCountAsync(
        string operation, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM prompt_templates WHERE operation = @operation",
            new { operation },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>某操作的下一个版本号。对应 Go: <c>NextPromptTemplateVersion</c>。</summary>
    public async Task<long> NextPromptTemplateVersionAsync(
        string operation, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long max = await ScalarAsync<long>(
            connection,
            "SELECT COALESCE(MAX(version), 0) FROM prompt_templates WHERE operation = @operation",
            new { operation },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return max + 1;
    }

    /// <summary>
    /// 保存模板。启用时先停用同操作的其他版本。
    /// 对应 Go: <c>SavePromptTemplate</c>。
    /// </summary>
    public async Task SavePromptTemplateAsync(PromptTemplate template, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (template.Enabled)
        {
            // 「每个操作最多一个启用版本」的不变量在这里维护。
            await ExecuteAsync(
                connection,
                """
                UPDATE prompt_templates SET enabled = @disabled, updated_at = @now
                WHERE operation = @operation AND id <> @id
                """,
                new { disabled = false, now = DateTime.UtcNow, operation = template.Operation, id = template.ID },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        // Go 用 tx.Save（有主键则 UPDATE，无则 INSERT），所以这里也必须是 upsert。
        await ExecuteAsync(
            connection,
            SqlBuilder.Insert<PromptTemplate>() + Dialect.OnConflictDoUpdate(
                "\"id\"",
                EntityMetadata.For<PromptTemplate>().Columns
                    .Select(column => column.Column)
                    .Where(column => column != "id")),
            template,
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除模板。对应 Go: <c>DeletePromptTemplate</c>。</summary>
    public async Task DeletePromptTemplateAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM prompt_templates WHERE id = @id",
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
