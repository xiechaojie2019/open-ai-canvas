#nullable enable
using System.Text.Json;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>
/// 卡死兜底：长时间没有任何进展（既没有新事件、也没有在跑的任务）的运行必须收尾。
///
/// 没有这层兜底时，调度器每 2 秒会对它做一次注定失败的推进，而失败的典型形态是
/// 「画布已变化，本次未写入」这类 CAS 冲突（<c>AppError.Status == 409</c>）——
/// 调度器把所有 409 当作并发推进静默跳过，连一行日志都不留。
/// 结果：运行永久停在 <c>running</c>，用户只看到"突然停住"，既没有失败原因也没有恢复路径。
///
/// 线上实例（2026-10-09，run <c>agec93ff351ad71aab8a61b69bdd4e00b3</c>）：画布操作写失败后
/// 画布被写脏（见 <see cref="CloudAgentMutations.PrepareCanvasMutationAsync"/> 的调用方），
/// 模型仍拿着旧快照哈希重试 → 每次规划审批都 409 → 运行从 04:06 一直卡在 running。
///
/// 对应 Go: <c>app/cloud_agent_stuck.go</c>。
/// </summary>
public sealed partial class CloudAgentRuntimeService
{
    /// <summary>判定卡死的静止时长。对应 Go: <c>cloudAgentStuckAfter</c>。</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(5);

    /// <summary>连续 CAS 冲突达到该次数时打一条诊断日志。对应 Go: <c>cloudAgentConflictLogThreshold</c>。</summary>
    public const int ConflictLogThreshold = 15;

    /// <summary>
    /// 判定卡死并收尾。返回 <c>null</c> 表示"还有进展或任务仍在跑"，调用方应继续正常推进；
    /// 返回静止分钟数表示已判定卡死 —— 无论 CAS 是否命中都当作已处置，调用方本轮应跳过它。
    /// 对应 Go: <c>terminateStuckCloudAgent</c>。
    /// </summary>
    public async Task<double?> TryTerminateStuckCloudAgentAsync(
        CloudAgentExecution run, CancellationToken cancellationToken = default)
    {
        if (run.CleanupPending || run.Status is not ("running" or "queued"))
        {
            return null;
        }
        CloudAgentRuntimeDto state;
        try
        {
            state = Decode(run);
        }
        catch (Exception cause) when (cause is AppError or InvalidOperationException or JsonException)
        {
            // 状态解不开时不敢下判断：交给正常推进路径（它会按自己的规则终态化）。
            return null;
        }
        if (await CloudAgentHasLiveWorkAsync(run, state, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        DateTime last = CloudAgentLastEventAt(state);
        if (last == default || DateTime.UtcNow - last < StuckAfter)
        {
            return null;
        }
        double idle = (DateTime.UtcNow - last).TotalMinutes;
        // Go 把 CAS 未命中（别的写者正在动这一行）也当作"已处置"：返回 nil + true，本轮跳过它。
        await _repository.MarkCloudAgentFailedAsync(
            run.UserID, run.ID, run.Revision,
            $"运行已 {idle:0} 分钟没有任何进展（既没有新事件、也没有在跑的任务），已判定为卡住并停止；可以重新发起")
            .ConfigureAwait(false);
        return idle;
    }

    /// <summary>是否有仍在排队/运行的任务。对应 Go: <c>cloudAgentHasLiveWork</c>。</summary>
    private async Task<bool> CloudAgentHasLiveWorkAsync(
        CloudAgentExecution run, CloudAgentRuntimeDto state, CancellationToken cancellationToken)
    {
        // Go 还会检查 storyboardTaskId；两边的分镜都是同步写、该字段从未被赋值，故不移植。
        foreach (string taskID in new[] { state.ActiveTaskID, state.MediaTaskID })
        {
            if (taskID.Length == 0)
            {
                continue;
            }
            TaskEntity? task = await _repository.TaskForUserAsync(run.UserID, taskID, cancellationToken)
                .ConfigureAwait(false);
            if (task is null)
            {
                // 指向的任务查不到：宁可不动，也不误杀。
                return true;
            }
            if (task.Status is TaskStatus.TaskStatusQueued or TaskStatus.TaskStatusRunning)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 最后一条事件的时间。对应 Go: <c>cloudAgentLastEventAt</c>。
    /// <c>state.Events</c> 由 <c>CloudAgentContracts.Decode</c> 从 journal 行重建（检查点里的
    /// events 是空数组，见 <c>CloudAgentContracts.Save</c>），时间戳经 <c>GoTimeConverter</c>
    /// 解析后 Kind 恒为 Utc，可直接与 <see cref="DateTime.UtcNow"/> 相减。
    /// </summary>
    private static DateTime CloudAgentLastEventAt(CloudAgentRuntimeDto state) =>
        state.Events.Count == 0 ? default : state.Events[^1].CreatedAt;
}
