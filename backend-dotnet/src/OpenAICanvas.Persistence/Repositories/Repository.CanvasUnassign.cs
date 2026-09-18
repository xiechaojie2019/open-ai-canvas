#nullable enable
using System.Data.Common;
using Dapper;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 画布与项目解绑。对应 Go: <c>repository.UnassignCanvasFromProject</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>
    /// 关系列、同步快照与项目版本号原子更新；命中 0 行视为画布不在该项目下。
    /// </summary>
    public async Task UnassignCanvasFromProjectAsync(
        string userId,
        string projectId,
        string canvasId,
        string payloadJson,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            "DELETE FROM canvas_unit_links WHERE project_id = @projectId AND canvas_id = @canvasId",
            new { projectId, canvasId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE canvas_projects SET project_id = '', payload_json = @payloadJson, updated_at = @updatedAt
            WHERE id = @canvasId AND user_id = @userId AND project_id = @projectId
            """,
            new { canvasId, userId, projectId, payloadJson, updatedAt },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("record not found");
        }

        await ExecuteAsync(
            connection,
            "UPDATE projects SET revision = revision + 1, updated_at = @updatedAt WHERE id = @projectId",
            new { updatedAt, projectId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
