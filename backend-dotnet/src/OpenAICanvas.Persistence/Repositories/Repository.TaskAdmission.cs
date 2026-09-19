#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>任务准入辅助仓储：路由快照校验与任务日志写入。</summary>
public sealed partial class Repository
{
    /// <summary>按 ID 查逻辑模型路由。对应 Go: <c>LogicalModelRoute</c>。</summary>
    public async Task<LogicalModelRoute?> LogicalModelRouteAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<LogicalModelRoute>(
            connection,
            SqlBuilder.Select<LogicalModelRoute>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 查系统渠道（不限启用）。对应 Go: <c>SystemChannel</c>。</summary>
    public async Task<ModelChannel?> SystemChannelByIDAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<ModelChannel>(
            connection,
            SqlBuilder.Select<ModelChannel>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>写任务日志。对应 Go: <c>Service.log</c>（task_logs 追加）。</summary>
    public async Task CreateTaskLogAsync(
        string userId,
        string taskId,
        string level,
        string message,
        string detail,
        CancellationToken cancellationToken = default)
    {
        TaskLog log = new()
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            TaskID = taskId,
            Level = level,
            Message = message,
            Payload = detail,
            CreatedAt = DateTime.UtcNow,
        };
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(TaskLog)), log, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
