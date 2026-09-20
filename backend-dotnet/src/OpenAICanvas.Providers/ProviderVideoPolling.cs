#nullable enable
using System.Text.Json;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Providers;

/// <summary>视频轮询事件。对应 Go: <c>videoPollEvent</c>。</summary>
public enum VideoPollEvent
{
    /// <summary>上游查询暂时异常，将继续轮询原任务。</summary>
    Retrying,

    /// <summary>上游查询已恢复。</summary>
    Recovered,
}

/// <summary>轮询结果。对应 Go: <c>videoPollOutcome</c>。</summary>
/// <param name="Done">是否已得到终态结果。</param>
/// <param name="Result">终态结果载荷（<see cref="Done"/> 为 <c>true</c> 时有效）。</param>
public sealed record VideoPollOutcome(bool Done, Dictionary<string, object?>? Result);

/// <summary>
/// 视频轮询与下载策略。对应 Go: <c>videoPollPolicy</c>。
/// </summary>
/// <remarks>
/// <see cref="Sleep"/> 与 <see cref="Notify"/> 可注入，便于测试不真的等待 30 秒。
/// </remarks>
public sealed class VideoPollPolicy
{
    /// <summary>默认轮询间隔。对应 Go: <c>defaultVideoPollInterval</c>。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    /// <summary>轮询总超时。对应 Go: <c>videoPollTimeout</c>。</summary>
    public static readonly TimeSpan PollTimeout = TimeSpan.FromHours(1);

    public TimeSpan InitialDelay { get; set; } = DefaultInterval;

    public TimeSpan Interval { get; set; } = DefaultInterval;

    public int MaxNotFoundMisses { get; set; } = 3;

    public int MaxMalformedResponses { get; set; } = 3;

    public int MaxDownloadTries { get; set; } = 3;

    public bool RetryTransient { get; set; } = true;

    /// <summary>
    /// 轮询总时限覆盖值；<c>null</c> 时用 <see cref="VideoPollPolicy.PollTimeout"/>。
    /// </summary>
    /// <remarks>
    /// 仅供测试注入短时限 —— 生产路径必须保留 1 小时的默认预算。
    /// </remarks>
    public TimeSpan? TotalTimeout { get; set; }

    /// <summary>等待实现。默认 <see cref="ProviderVideoPolling.SleepAsync"/>。</summary>
    public Func<TimeSpan, CancellationToken, Task> Sleep { get; set; } =
        (duration, token) => ProviderVideoPolling.SleepAsync(duration, token);

    /// <summary>事件通知（记任务日志用）。</summary>
    public Func<string, VideoPollEvent, Exception?, Task>? Notify { get; set; }
}

/// <summary>
/// 视频结果下载失败。对应 Go: <c>videoDownloadError</c>。
/// </summary>
/// <remarks>
/// 携带该异常意味着<b>不再重试</b>
/// （见 <see cref="ProviderVideoPolling.RetryablePollError"/>）。
/// </remarks>
public sealed class VideoDownloadException : Exception
{
    public VideoDownloadException(string taskId, Exception cause)
        : base(BuildMessage(taskId, cause), cause) => TaskID = taskId;

    public string TaskID { get; }

    private static string BuildMessage(string taskId, Exception cause) =>
        string.IsNullOrWhiteSpace(taskId)
            ? $"视频结果下载失败：{cause.Message}"
            : $"视频结果下载失败（任务 {taskId}）：{cause.Message}";
}

/// <summary>
/// 视频任务的轮询循环与结果下载。
/// 对应 Go: <c>internal/app/provider_video_polling.go</c> 的
/// <c>runVideoPollLoop</c> / <c>runVideoDownload</c> / <c>retryableVideoPollError</c> /
/// <c>isTransientResponseDecodeError</c> / <c>isProviderTaskNotReadyError</c> /
/// <c>providerRetryAfter</c> / <c>providerPollingDeadline</c> / <c>normalizeVideoPollPolicy</c>。
/// </summary>
public static class ProviderVideoPolling
{
    /// <summary>
    /// 把策略补齐为合法值（零值回落默认）。对应 Go: <c>normalizeVideoPollPolicy</c>。
    /// </summary>
    public static VideoPollPolicy Normalize(VideoPollPolicy? policy)
    {
        policy ??= new VideoPollPolicy();
        if (policy.Interval <= TimeSpan.Zero)
        {
            policy.Interval = VideoPollPolicy.DefaultInterval;
        }
        if (policy.MaxNotFoundMisses <= 0)
        {
            policy.MaxNotFoundMisses = 3;
        }
        if (policy.MaxMalformedResponses <= 0)
        {
            policy.MaxMalformedResponses = 3;
        }
        if (policy.MaxDownloadTries <= 0)
        {
            policy.MaxDownloadTries = 3;
        }
        policy.Sleep ??= (duration, token) => SleepAsync(duration, token);
        return policy;
    }

    /// <summary>
    /// 轮询直到取得终态结果。
    /// 对应 Go: <c>runVideoPollLoop</c>。
    /// </summary>
    /// <param name="query">单次查询；抛异常表示失败，返回 <c>Done=false</c> 表示继续轮询。</param>
    /// <remarks>
    /// <b>"连续未找到"与"连续畸形响应"是两个独立计数器</b>，任一超限即放弃：
    /// 合并成一个计数器会让"上游交替返回 404 和坏 JSON"永不触发上限。
    /// 计数器在成功时同时归零。
    /// </remarks>
    public static async Task<Dictionary<string, object?>> RunPollLoopAsync(
        string taskId,
        VideoPollPolicy policy,
        Func<CancellationToken, Task<VideoPollOutcome>> query,
        CancellationToken cancellationToken = default)
    {
        policy = Normalize(policy);
        DateTimeOffset deadline = PollingDeadline(cancellationToken, policy.TotalTimeout);
        using CancellationTokenSource pollSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan budget = deadline - DateTimeOffset.UtcNow;
        pollSource.CancelAfter(budget > TimeSpan.Zero ? budget : TimeSpan.Zero);
        CancellationToken token = pollSource.Token;

        TimeSpan nextDelay = policy.InitialDelay;
        int notFoundMisses = 0;
        int malformedResponses = 0;
        bool retrying = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (nextDelay > TimeSpan.Zero)
            {
                await policy.Sleep(nextDelay, token).ConfigureAwait(false);
            }

            VideoPollOutcome outcome;
            try
            {
                outcome = await query(token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (!policy.RetryTransient)
                {
                    throw;
                }
                (bool retry, bool notFound) = RetryablePollError(error, token);
                if (!retry)
                {
                    throw;
                }
                if (IsTransientResponseDecodeError(error))
                {
                    malformedResponses++;
                    notFoundMisses = 0;
                    if (malformedResponses >= policy.MaxMalformedResponses)
                    {
                        throw;
                    }
                }
                else if (notFound)
                {
                    malformedResponses = 0;
                    notFoundMisses++;
                    if (notFoundMisses >= policy.MaxNotFoundMisses)
                    {
                        throw;
                    }
                }
                else
                {
                    malformedResponses = 0;
                    notFoundMisses = 0;
                }
                if (!retrying)
                {
                    retrying = true;
                    await NotifyAsync(policy, taskId, VideoPollEvent.Retrying, error).ConfigureAwait(false);
                }
                nextDelay = Max(policy.Interval, ProviderRetryAfter(error));
                continue;
            }

            notFoundMisses = 0;
            malformedResponses = 0;
            nextDelay = policy.Interval;
            if (retrying)
            {
                retrying = false;
                await NotifyAsync(policy, taskId, VideoPollEvent.Recovered, null).ConfigureAwait(false);
            }
            if (outcome.Done)
            {
                return outcome.Result
                    ?? throw new InvalidOperationException("视频查询已完成但没有结果载荷");
            }
        }

        // 与 Go 一致：循环自然结束或子上下文超时都归为轮询超时。
        if (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        throw new TimeoutException("视频任务轮询超时");
    }

    /// <summary>
    /// 下载视频结果并重试瞬时故障。
    /// 对应 Go: <c>runVideoDownload</c>。
    /// </summary>
    public static async Task<(byte[] Data, string MIMEType)> RunDownloadAsync(
        string taskId,
        VideoPollPolicy policy,
        Func<CancellationToken, Task<(byte[] Data, string MIMEType)>> download,
        CancellationToken cancellationToken = default)
    {
        policy = Normalize(policy);
        Exception? lastError = null;
        for (int attempt = 1; attempt <= policy.MaxDownloadTries; attempt++)
        {
            try
            {
                return await download(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lastError = error;
                (bool retry, _) = RetryablePollError(error, cancellationToken);
                if (!retry || attempt == policy.MaxDownloadTries)
                {
                    throw new VideoDownloadException(taskId, error);
                }
                TimeSpan delay = Max(policy.Interval, ProviderRetryAfter(error));
                await policy.Sleep(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new VideoDownloadException(taskId, lastError ?? new InvalidOperationException("下载失败"));
    }

    /// <summary>
    /// 判定错误是否可重试，以及是否属于"任务尚未就绪"。
    /// 对应 Go: <c>retryableVideoPollError</c>。
    /// </summary>
    /// <remarks>
    /// <b>顺序敏感</b>：<see cref="VideoDownloadException"/> 与已取消的上下文必须先短路为不可重试，
    /// 否则下载失败会无限重试下去。
    /// </remarks>
    public static (bool Retry, bool NotFound) RetryablePollError(Exception? error, CancellationToken cancellationToken)
    {
        if (error is null || cancellationToken.IsCancellationRequested || error is OperationCanceledException)
        {
            return (false, false);
        }
        if (error is VideoDownloadException)
        {
            return (false, false);
        }
        // 渠道并发获取失败（占槽超时/取消/不可用）属于可重试的瞬时故障。
        if (error is ProviderChannelSlotException)
        {
            return (true, false);
        }
        if (error is ProviderCircuitOpenException)
        {
            return (true, false);
        }
        if (IsTransientResponseDecodeError(error))
        {
            return (true, false);
        }
        if (error is IOException or System.Net.Sockets.SocketException or TimeoutException)
        {
            return (true, false);
        }
        if (error is ProviderHttpException httpError)
        {
            if (IsProviderTaskNotReadyError(httpError))
            {
                return (true, true);
            }
            if (httpError.StatusCode == 404)
            {
                return (true, true);
            }
            return httpError.StatusCode switch
            {
                408 or 409 or 425 or 429 => (true, false),
                _ => (httpError.StatusCode >= 500, false),
            };
        }
        return (false, false);
    }

    /// <summary>对应 Go: <c>isTransientResponseDecodeError</c>（解码异常或 JSON 语法错误）。</summary>
    public static bool IsTransientResponseDecodeError(Exception? error) =>
        error is ProviderResponseDecodeException || error is JsonException;

    /// <summary>
    /// 上游是否以"任务不存在"表达了"还没准备好"。
    /// 对应 Go: <c>isProviderTaskNotReadyError</c>。
    /// </summary>
    /// <remarks>
    /// 只认 400/404，且只匹配 <c>code</c>/<c>message</c> 等于固定的几个短语（大小写不敏感）——
    /// <b>不做自然语言扫描</b>，否则任意含 "not found" 的错误都会被当成未就绪而反复轮询。
    /// </remarks>
    public static bool IsProviderTaskNotReadyError(ProviderHttpException error)
    {
        if (error.StatusCode is not (400 or 404))
        {
            return false;
        }
        Dictionary<string, object?>? payload = ProviderTransport.ParseObject(
            System.Text.Encoding.UTF8.GetBytes(error.Body));
        if (payload is null)
        {
            return false;
        }
        (string code, string message) = ProviderError.FailureDetails(payload);
        foreach (string value in new[] { code, message })
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "task_not_exist":
                case "task_not_found":
                case "task not exist":
                case "task not found":
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 从 HTTP 异常取出 <c>Retry-After</c>；无则返回零。
    /// 对应 Go: <c>providerRetryAfter</c>。
    /// </summary>
    public static TimeSpan ProviderRetryAfter(Exception? error) =>
        error is ProviderHttpException httpError ? httpError.RetryAfter : TimeSpan.Zero;

    /// <summary>
    /// 轮询截止时间：优先用调用方上下文，否则为"当前时间 + 有效视频轮询超时"。
    /// 对应 Go: <c>providerPollingDeadline</c>。
    /// </summary>
    public static DateTimeOffset PollingDeadline(CancellationToken cancellationToken, TimeSpan? totalTimeout = null) =>
        cancellationToken.CanBeCanceled && cancellationToken.IsCancellationRequested
            ? DateTimeOffset.UtcNow
            : DateTimeOffset.UtcNow.Add(totalTimeout ?? VideoPollPolicy.PollTimeout);

    /// <summary>对应 Go: <c>sleepContext</c>（可取消的等待）。</summary>
    public static async Task SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }
        await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>对应 Go 的 <c>max(a, b)</c>。</summary>
    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static async Task NotifyAsync(
        VideoPollPolicy policy, string taskId, VideoPollEvent pollEvent, Exception? error)
    {
        if (policy.Notify is null)
        {
            return;
        }
        try
        {
            await policy.Notify(taskId, pollEvent, error).ConfigureAwait(false);
        }
        catch
        {
            // 与 Go 一致：事件通知是旁路，失败不能影响轮询。
        }
    }
}
