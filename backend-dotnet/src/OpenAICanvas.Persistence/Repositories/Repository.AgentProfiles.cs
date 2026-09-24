#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// Agent 偏好档案仓储方法。对应 Go: <c>repository/agent_profile.go</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>按用户 + 作用域查档案；不存在返回 null。对应 Go: <c>AgentProfile</c>。</summary>
    public async Task<AgentProfile?> AgentProfileForScopeAsync(
        string userId,
        string scope,
        string projectId,
        string canvasId,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<AgentProfile>(
            connection,
            SqlBuilder.Select<AgentProfile>(
                "user_id = @userId AND scope = @scope AND project_id = @projectId AND canvas_id = @canvasId",
                limitOffset: " LIMIT 1"),
            new { userId, scope, projectId, canvasId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 乐观锁保存档案：不存在时期望 revision 必须为 0；存在时必须匹配，
    /// 成功后 revision 加一。冲突返回 null。对应 Go: <c>SaveAgentProfile</c>。
    /// </summary>
    public async Task<AgentProfile?> SaveAgentProfileAsync(
        AgentProfile profile,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            AgentProfile? current = await QuerySingleOrDefaultAsync<AgentProfile>(
                connection,
                SqlBuilder.Select<AgentProfile>(
                    "user_id = @userId AND scope = @scope AND project_id = @projectId AND canvas_id = @canvasId",
                    limitOffset: " LIMIT 1"),
                new { userId = profile.UserID, scope = profile.Scope, projectId = profile.ProjectID, canvasId = profile.CanvasID },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                if (expectedRevision != 0)
                {
                    return null;
                }
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert(typeof(AgentProfile)),
                    profile,
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                return profile;
            }
            if (expectedRevision != current.Revision)
            {
                return null;
            }
            profile.ID = current.ID;
            profile.Revision = current.Revision + 1;
            profile.CreatedAt = current.CreatedAt;
            profile.UpdatedAt = DateTime.UtcNow;
            int updated = await ExecuteAsync(
                connection,
                "UPDATE \"agent_profiles\" SET \"content\" = @Content, \"revision\" = @Revision, \"hash\" = @Hash, \"updated_at\" = @UpdatedAt WHERE \"id\" = @ID AND \"revision\" = @ExpectedRevision",
                new { profile.Content, profile.Revision, profile.Hash, profile.UpdatedAt, profile.ID, ExpectedRevision = current.Revision },
                transaction,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return updated == 1 ? profile : null;
        }, cancellationToken).ConfigureAwait(false);
    }
}
