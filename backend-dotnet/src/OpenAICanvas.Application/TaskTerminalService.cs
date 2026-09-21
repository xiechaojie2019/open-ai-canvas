#nullable enable
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Providers;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;
using BillingStatusValues = OpenAICanvas.Domain.Entities.BillingStatus;

namespace OpenAICanvas.Application;

/// <summary>
/// 任务终态协调：失败、取消与成功的收尾策略。
/// 对应 Go: <c>app/task_terminal.go</c> 的 <c>taskTerminalCoordinator</c>。
/// </summary>
/// <remarks>
/// 任务执行由 worker 编排，但进入终态后的回放归并、计费收尾与日志必须保持一致，
/// 不能散落在执行分支里。
/// </remarks>
public sealed class TaskTerminalService
{
    private readonly Repository _repository;
    private readonly TaskBillingLifecycle _billing;
    private readonly TaskService _tasks;

    public TaskTerminalService(Repository repository, TaskService tasks)
    {
        _repository = repository;
        _tasks = tasks;
        _billing = new TaskBillingLifecycle(repository);
    }

    /// <summary>计费收尾动作收敛。对应 Go: <c>taskBillingCoordinator</c> 的生命周期方法。</summary>
    private sealed class TaskBillingLifecycle
    {
        private readonly Repository _repository;

        public TaskBillingLifecycle(Repository repository) => _repository = repository;

        public async Task MarkBillingUncertainAsync(string orderID, string errorText, CancellationToken ct)
        {
            if (orderID.Length > 0)
            {
                await _repository.MarkBillingUncertainAsync(orderID, TruncateRunes(errorText, 1000), ct).ConfigureAwait(false);
            }
        }

        public async Task RefundBillingAsync(string orderID, string errorText, CancellationToken ct)
        {
            if (orderID.Length > 0)
            {
                await _repository.RefundBillingOrderAsync(orderID, TruncateRunes(errorText, 1000), ct).ConfigureAwait(false);
            }
        }

        public async Task SettleBillingAsync(string orderID, string providerRequestID, CancellationToken ct)
        {
            if (orderID.Length > 0)
            {
                await _repository.SettleBillingOrderAsync(orderID, providerRequestID, ct).ConfigureAwait(false);
            }
        }

        /// <summary>对应 Go: <c>taskBillingCoordinator.BillingFailureRequiresReview</c>。</summary>
        public async Task<bool> BillingFailureRequiresReviewAsync(string orderID, string taskID, Exception? error, CancellationToken ct)
        {
            if (orderID.Length == 0)
            {
                return false;
            }
            if (BillingFailureUncertain(error))
            {
                return true;
            }
            BillingOrder? order = await _repository.BillingOrderAsync(orderID, ct).ConfigureAwait(false);
            if (order is null || order.Status == BillingStatusValues.BillingStatusUncertain)
            {
                return true;
            }
            return await _repository.TaskHasSuccessfulBillableCallAsync(taskID, ct).ConfigureAwait(false);
        }

        private static bool BillingFailureUncertain(Exception? error)
        {
            if (error is null)
            {
                return false;
            }
            string message = error.Message.ToLowerInvariant();
            foreach (string marker in new[]
            {
                "524", "timeout", "超时", "deadline exceeded", "context canceled",
                "connection reset", "unexpected eof", "broken pipe",
            })
            {
                if (message.Contains(marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static string TruncateRunes(string text, int limit)
        {
            return text.Length <= limit ? text : text.Substring(0, limit);
        }
    }

    /// <summary>准备阶段失败：置 failed、退款或进入待核对。对应 Go: <c>markPreparationFailure</c>。</summary>
    public async Task<Exception> MarkPreparationFailureAsync(
        TaskEntity task, string stage, Exception error, bool billingUncertain, string refundReason,
        CancellationToken cancellationToken = default)
    {
        task.Status = TaskStatus.TaskStatusFailed;
        task.Stage = stage;
        task.Error = UserFacing(error);
        Exception? terminalError = null;
        Exception? billingError = null;
        try
        {
            await MarkTerminalStateAsync(task, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            terminalError = ex;
        }
        try
        {
            if (billingUncertain)
            {
                await _billing.MarkBillingUncertainAsync(task.BillingOrderID, task.Error, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _billing.RefundBillingAsync(task.BillingOrderID, refundReason, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            billingError = ex;
        }
        if (terminalError is null && billingError is null)
        {
            return error;
        }
        List<Exception> inner = [error];
        if (terminalError is not null)
        {
            inner.Add(new Exception("写入任务终态失败", terminalError));
        }
        if (billingError is not null)
        {
            inner.Add(new Exception("任务计费收尾失败", billingError));
        }
        return inner.Count == 1 ? inner[0] : new AggregateException(inner);
    }

    /// <summary>
    /// 执行失败收尾。返回 null 仅表示取消已被正常收尾；普通失败仍返回原始错误，
    /// 让 worker 保留重试/监控所需的失败语义。对应 Go: <c>handleExecutionFailure</c>。
    /// </summary>
    public async Task<Exception?> HandleExecutionFailureAsync(
        TaskEntity task, Exception error, bool providerSucceeded, bool channelSlotFailedBeforeRequest,
        CancellationToken cancellationToken = default)
    {
        if (error is OperationCanceledException)
        {
            // 用户取消会先把数据库任务置为 cancelled，再停止 worker context。
            // 此时不再重复退款/核对，只补齐 worker 侧的回放和日志收尾。
            TaskEntity? latest = await _repository.TaskAsync(task.ID, cancellationToken).ConfigureAwait(false);
            if (latest is not null && latest.Status == TaskStatus.TaskStatusCancelled)
            {
                await HandleAlreadyCancelledAsync(latest, cancellationToken).ConfigureAwait(false);
                return null;
            }
            task.Status = TaskStatus.TaskStatusCancelled;
            task.Stage = "任务已取消";
            task.Error = "任务已取消";
            await MarkTerminalStateAsync(task, cancellationToken).ConfigureAwait(false);
            Exception? billingError = null;
            try
            {
                if (channelSlotFailedBeforeRequest)
                {
                    await _billing.RefundBillingAsync(task.BillingOrderID, "等待渠道槽位期间取消，上游请求未发出", cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _billing.MarkBillingUncertainAsync(task.BillingOrderID, "任务取消时上游费用状态不明确", cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                billingError = ex;
            }
            await FinalizeReplayAsync(task, TaskStatus.TaskStatusCancelled, "文本回放草稿归并失败", cancellationToken).ConfigureAwait(false);
            await LogAsync(task.UserID, task.ID, "warn", "任务已取消", "", cancellationToken).ConfigureAwait(false);
            return billingError is null ? null : new Exception("任务取消后的计费收尾失败", billingError);
        }

        task.Status = TaskStatus.TaskStatusFailed;
        task.Stage = "任务失败";
        task.Error = UserFacing(error);
        await MarkTerminalStateAsync(task, cancellationToken).ConfigureAwait(false);
        await FinalizeReplayAsync(task, TaskStatus.TaskStatusFailed, "文本回放草稿归并失败", cancellationToken).ConfigureAwait(false);
        bool requiresReview = await _billing.BillingFailureRequiresReviewAsync(task.BillingOrderID, task.ID, error, cancellationToken).ConfigureAwait(false);
        Exception? billingFailure = null;
        try
        {
            if (providerSucceeded || (!channelSlotFailedBeforeRequest && requiresReview))
            {
                await _billing.MarkBillingUncertainAsync(task.BillingOrderID, task.Error, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _billing.RefundBillingAsync(task.BillingOrderID, task.Error, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            billingFailure = ex;
        }
        await LogAsync(task.UserID, task.ID, "error", "任务处理失败", task.Error, cancellationToken).ConfigureAwait(false);
        return billingFailure is null ? error : new AggregateException(error, new Exception("任务计费收尾失败", billingFailure));
    }

    /// <summary>任务已取消，worker 停止执行。对应 Go: <c>handleAlreadyCancelled</c>。</summary>
    public async Task HandleAlreadyCancelledAsync(TaskEntity task, CancellationToken cancellationToken = default)
    {
        await FinalizeReplayAsync(task, TaskStatus.TaskStatusCancelled, "文本回放草稿归并失败", cancellationToken).ConfigureAwait(false);
        await LogAsync(task.UserID, task.ID, "warn", "任务已取消，worker 已停止执行", "", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>上游已返回结果但任务被取消，丢弃结果。对应 Go: <c>handleCancelledResult</c>。</summary>
    public async Task<Exception?> HandleCancelledResultAsync(TaskEntity task, CancellationToken cancellationToken = default)
    {
        await FinalizeReplayAsync(task, TaskStatus.TaskStatusCancelled, "文本回放草稿归并失败", cancellationToken).ConfigureAwait(false);
        Exception? billingError = null;
        try
        {
            await _billing.MarkBillingUncertainAsync(task.BillingOrderID, "上游已返回结果，但任务被取消", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            billingError = ex;
        }
        await LogAsync(task.UserID, task.ID, "warn", "任务已取消，丢弃生成结果", "", cancellationToken).ConfigureAwait(false);
        return billingError is null ? null : new Exception("丢弃已生成结果后的计费收尾失败", billingError);
    }

    /// <summary>
    /// 上游成功但本地结果保存失败的收尾。返回 handled=true 表示并发取消已被识别，
    /// 调用方不应再返回保存错误。对应 Go: <c>handleResultPersistenceFailure</c>。
    /// </summary>
    public async Task<(bool Handled, Exception? Error)> HandleResultPersistenceFailureAsync(
        TaskEntity task, Exception saveError, CancellationToken cancellationToken = default)
    {
        if (saveError is TaskStateConflictException)
        {
            TaskEntity? latest = await _repository.TaskAsync(task.ID, cancellationToken).ConfigureAwait(false);
            if (latest is not null && latest.Status == TaskStatus.TaskStatusCancelled)
            {
                Exception? billingError = await HandleCancelledResultAsync(latest, cancellationToken).ConfigureAwait(false);
                return billingError is null ? (true, null) : (true, billingError);
            }
        }

        task.Status = TaskStatus.TaskStatusFailed;
        task.Stage = "任务结果保存失败";
        task.Error = UserFacing(saveError);
        Exception? terminalError = null;
        Exception? billingError2 = null;
        try
        {
            await MarkTerminalStateAsync(task, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            terminalError = ex;
        }
        await FinalizeReplayAsync(task, TaskStatus.TaskStatusFailed, "文本回放草稿归并失败", cancellationToken).ConfigureAwait(false);
        try
        {
            await _billing.MarkBillingUncertainAsync(task.BillingOrderID, "上游已成功但任务结果未保存：" + task.Error, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            billingError2 = ex;
        }
        await LogAsync(task.UserID, task.ID, "error", "任务结果保存失败", task.Error, cancellationToken).ConfigureAwait(false);

        List<Exception> failures = [saveError];
        if (terminalError is not null)
        {
            failures.Add(new Exception("写入任务终态失败", terminalError));
        }
        if (billingError2 is not null)
        {
            failures.Add(new Exception("任务结果保存失败后的计费收尾失败", billingError2));
        }
        return (false, failures.Count == 1 ? failures[0] : new AggregateException(failures));
    }

    /// <summary>成功收尾：回放归并、结算。对应 Go: <c>handleSuccess</c>（产物登记未移植，见 PENDING-CONFIRMATIONS.md）。</summary>
    public async Task<Exception?> HandleSuccessAsync(TaskEntity task, CancellationToken cancellationToken = default)
    {
        await FinalizeReplayAsync(task, TaskStatus.TaskStatusSucceeded, "文本回放窗口更新失败", cancellationToken).ConfigureAwait(false);
        Exception? completionError = null;
        if (await _repository.TaskAsync(task.ID, cancellationToken).ConfigureAwait(false) is null)
        {
            await LogAsync(task.UserID, task.ID, "error", "任务成功但读取任务产物失败", "record not found", cancellationToken).ConfigureAwait(false);
        }
        try
        {
            await _billing.SettleBillingAsync(task.BillingOrderID, "", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Exception? uncertainError = null;
            try
            {
                await _billing.MarkBillingUncertainAsync(task.BillingOrderID, "生成成功但积分结算失败：" + error.Message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                uncertainError = ex;
            }
            List<Exception> settleFailures = [new Exception("积分结算失败", error)];
            if (uncertainError is not null)
            {
                settleFailures.Add(new Exception("记录计费待核对状态失败", uncertainError));
            }
            completionError = settleFailures.Count == 1 ? settleFailures[0] : new AggregateException(settleFailures);
            await LogAsync(task.UserID, task.ID, "error", "积分结算失败，已进入待核对", error.Message, cancellationToken).ConfigureAwait(false);
        }
        await LogAsync(task.UserID, task.ID, "info", "任务完成，结果已持久化", "", cancellationToken).ConfigureAwait(false);
        return completionError;
    }

    private async Task MarkTerminalStateAsync(TaskEntity task, CancellationToken cancellationToken)
    {
        DateTime completedAt = DateTime.UtcNow;
        task.CompletedAt = completedAt;
        bool updated = await _repository.UpdateTaskTerminalStateAsync(
            task.ID, task.LeaseOwner, TaskStatus.TaskStatusRunning, task.Status,
            task.Stage, task.Error == null ? "" : task.Error, completedAt, cancellationToken).ConfigureAwait(false);
        if (!updated)
        {
            throw new TaskStateConflictException();
        }
    }

    private async Task FinalizeReplayAsync(TaskEntity task, string status, string message, CancellationToken cancellationToken)
    {
        try
        {
            await _tasks.FinalizeTaskTextReplayAsync(task.ID, status, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await LogAsync(task.UserID, task.ID, "error", message, error.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LogAsync(string userId, string taskId, string level, string message, string payload, CancellationToken cancellationToken)
    {
        try
        {
            string traceId = "";
            string requestId = "";
            if (taskId.Length > 0)
            {
                TaskEntity? task = await _repository.TaskAsync(taskId, cancellationToken).ConfigureAwait(false);
                if (task is not null)
                {
                    traceId = task.TraceID;
                    requestId = task.RequestID;
                }
            }
            await _repository.CreateAsync(new TaskLog
            {
                ID = IdGenerator.NewId(),
                UserID = userId,
                TaskID = taskId,
                TraceID = traceId,
                RequestID = requestId,
                Level = level,
                Message = message,
                Payload = payload,
                CreatedAt = DateTime.UtcNow,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 日志失败不影响终态收尾（与 Go 的 _ = s.log 一致）。
        }
    }

    private static string UserFacing(Exception error) => ProviderErrorMessages.UserFacing(error);
}
