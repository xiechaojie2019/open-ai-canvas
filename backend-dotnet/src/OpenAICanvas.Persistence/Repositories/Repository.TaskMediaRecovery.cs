using System.Data.Common;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 任务媒体恢复仓储：检查点保存与手动恢复重排队。
/// 对应 Go: <c>repository/task_media_recovery.go</c>。
/// </summary>
/// <remarks>
/// 检查点写入沿用任务租约做栅栏，取消后不再写；恢复重排队复用同一任务与
/// 计费订单，绝不产生第二次生成。
/// </remarks>
public sealed partial class Repository
{
    /// <summary>
    /// 保存媒体恢复检查点（租约栅栏 + 运行态条件）。对应 Go: <c>SaveTaskMediaCheckpoint</c>。
    /// </summary>
    public async Task SaveTaskMediaCheckpointAsync(
        TaskEntity task, string encrypted, string stage, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await ExecuteAsync(
            connection,
            """
            UPDATE "tasks" SET "media_recovery_json" = @encrypted, "media_stage" = @stage,
                "stage" = '作品已生成，正在保存', "updated_at" = @now
            WHERE "id" = @id AND "user_id" = @userID AND "status" = @running
                AND "route_run" = @routeRun AND "lease_owner" = @leaseOwner
            """,
            new
            {
                encrypted,
                stage,
                now = DateTime.UtcNow,
                id = task.ID,
                userID = task.UserID,
                running = TaskStatus.TaskStatusRunning,
                routeRun = task.RouteRun,
                leaseOwner = task.LeaseOwner,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new TaskStateConflictException();
        }
        task.MediaRecoveryJSON = encrypted;
        task.MediaStage = stage;
    }

    /// <summary>
    /// 手动恢复重排队：复用同一任务与订单，乐观条件防止并发/过期恢复。
    /// 对应 Go: <c>RequeueTaskMediaRecovery</c>。
    /// </summary>
    public async Task RequeueTaskMediaRecoveryAsync(
        TaskEntity task, string encrypted, CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await ExecuteAsync(
            connection,
            """
            UPDATE "tasks" SET "status" = @queued, "stage" = '等待恢复作品保存', "error" = '',
                "completed_at" = NULL, "next_poll_at" = NULL,
                "lease_owner" = '', "lease_expires_at" = NULL,
                "media_recovery_json" = @encrypted, "updated_at" = @now
            WHERE "id" = @id AND "user_id" = @userID AND "status" = @failed
                AND "media_recovery_json" = @previous
                AND ("lease_expires_at" IS NULL OR "lease_expires_at" <= @now)
            """,
            new
            {
                queued = TaskStatus.TaskStatusQueued,
                failed = TaskStatus.TaskStatusFailed,
                now,
                id = task.ID,
                userID = task.UserID,
                previous = task.MediaRecoveryJSON,
                encrypted,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new TaskStateConflictException();
        }
    }
}
