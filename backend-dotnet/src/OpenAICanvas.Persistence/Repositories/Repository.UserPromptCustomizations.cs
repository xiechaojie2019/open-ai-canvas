#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 用户提示词定制仓储。对应 Go: <c>repository</c> 的 user_prompt_customizations 部分。
/// </summary>
public sealed partial class Repository
{
    /// <summary>用户全部定制。对应 Go: <c>UserPromptCustomizations</c>。</summary>
    public async Task<IReadOnlyList<UserPromptCustomization>> UserPromptCustomizationsAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<UserPromptCustomization>(
            connection,
            SqlBuilder.Select<UserPromptCustomization>("user_id = @userId", "operation ASC"),
            new { userId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按用户 + 操作查定制。对应 Go: <c>UserPromptCustomization</c>。</summary>
    public async Task<UserPromptCustomization?> UserPromptCustomizationAsync(
        string userId, string operation, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<UserPromptCustomization>(
            connection,
            SqlBuilder.Select<UserPromptCustomization>(
                "user_id = @userId AND operation = @operation", limitOffset: " LIMIT 1"),
            new { userId, operation },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保存定制（GORM Save 的 update-or-insert 语义）。对应 Go: <c>SaveUserPromptCustomization</c>。</summary>
    public async Task SaveUserPromptCustomizationAsync(
        UserPromptCustomization customization, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE user_prompt_customizations SET mode = @Mode, content = @Content,
              base_template_id = @BaseTemplateID, updated_at = @UpdatedAt
            WHERE id = @ID
            """,
            customization,
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (updated == 0)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<UserPromptCustomization>(), customization, transaction, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除定制。对应 Go: <c>DeleteUserPromptCustomization</c>。</summary>
    public async Task DeleteUserPromptCustomizationAsync(
        string userId, string operation, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM user_prompt_customizations WHERE user_id = @userId AND operation = @operation",
            new { userId, operation },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
