#nullable enable
using System.Text.Json;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 视频轮询与下载的契约测试。
/// 对应 Go: <c>internal/app/provider_video_polling.go</c>。
/// </summary>
public sealed class ProviderVideoPollingTests
{
    /// <summary>
    /// 不真的等待，只记录延迟。同时把轮询总时限压到 1 秒 ——
    /// 否则"持续返回可重试错误"的用例会一直循环到默认的 1 小时预算。
    /// </summary>
    private static VideoPollPolicy NoSleepPolicy(List<TimeSpan>? delays = null) => new()
    {
        InitialDelay = TimeSpan.FromMilliseconds(1),
        Interval = TimeSpan.FromMilliseconds(1),
        TotalTimeout = TimeSpan.FromSeconds(1),
        Sleep = (duration, _) =>
        {
            delays?.Add(duration);
            return Task.CompletedTask;
        },
    };

    private static Dictionary<string, object?> Result(string text) =>
        new(StringComparer.Ordinal) { ["text"] = text };

    // ------------------------------------------------------------ Normalize

    [Fact]
    public void 归一_零值回落默认()
    {
        VideoPollPolicy policy = ProviderVideoPolling.Normalize(new VideoPollPolicy
        {
            InitialDelay = TimeSpan.Zero,
            Interval = TimeSpan.Zero,
            MaxNotFoundMisses = 0,
            MaxMalformedResponses = -1,
            MaxDownloadTries = 0,
        });

        Assert.Equal(VideoPollPolicy.DefaultInterval, policy.Interval);
        Assert.Equal(3, policy.MaxNotFoundMisses);
        Assert.Equal(3, policy.MaxMalformedResponses);
        Assert.Equal(3, policy.MaxDownloadTries);
        Assert.NotNull(policy.Sleep);
    }

    [Fact]
    public void 归一_null时全默认()
    {
        VideoPollPolicy policy = ProviderVideoPolling.Normalize(null);

        Assert.Equal(VideoPollPolicy.DefaultInterval, policy.Interval);
        Assert.Equal(3, policy.MaxNotFoundMisses);
    }

    [Fact]
    public void 默认值_与Go一致()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), VideoPollPolicy.DefaultInterval);
        Assert.Equal(TimeSpan.FromHours(1), VideoPollPolicy.PollTimeout);
    }

    // ------------------------------------------------------------ 正常轮询

    [Fact]
    public async Task 轮询_首次即完成()
    {
        int calls = 0;
        Dictionary<string, object?> result = await ProviderVideoPolling.RunPollLoopAsync(
            "t1",
            NoSleepPolicy(),
            _ =>
            {
                calls++;
                return Task.FromResult(new VideoPollOutcome(true, Result("done")));
            });

        Assert.Equal("done", result["text"]);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 轮询_多次未完成后成功()
    {
        int calls = 0;
        Dictionary<string, object?> result = await ProviderVideoPolling.RunPollLoopAsync(
            "t1",
            NoSleepPolicy(),
            _ =>
            {
                calls++;
                return Task.FromResult(calls < 3
                    ? new VideoPollOutcome(false, null)
                    : new VideoPollOutcome(true, Result("ok")));
            });

        Assert.Equal(3, calls);
        Assert.Equal("ok", result["text"]);
    }

    [Fact]
    public async Task 轮询_完成后无结果载荷时报错()
    {
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProviderVideoPolling.RunPollLoopAsync(
                "t1",
                NoSleepPolicy(),
                _ => Task.FromResult(new VideoPollOutcome(true, null))));

        Assert.Equal("视频查询已完成但没有结果载荷", error.Message);
    }

    [Fact]
    public async Task 轮询_初始延迟与间隔都被使用()
    {
        List<TimeSpan> delays = [];
        VideoPollPolicy policy = NoSleepPolicy(delays);
        policy.InitialDelay = TimeSpan.FromMilliseconds(5);
        policy.Interval = TimeSpan.FromMilliseconds(7);

        int calls = 0;
        await ProviderVideoPolling.RunPollLoopAsync(
            "t1",
            policy,
            _ =>
            {
                calls++;
                return Task.FromResult(calls < 3
                    ? new VideoPollOutcome(false, null)
                    : new VideoPollOutcome(true, Result("x")));
            });

        // 每次循环开头都先等：3 次查询 → 3 次等待。
        Assert.Equal(3, delays.Count);
        // 首次用 InitialDelay，其后用 Interval。
        Assert.Equal(TimeSpan.FromMilliseconds(5), delays[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(7), delays[1]);
        Assert.Equal(TimeSpan.FromMilliseconds(7), delays[2]);
    }

    // ------------------------------------------------------------ 重试与计数

    [Fact]
    public async Task 重试_未找到累计到上限后放弃()
    {
        int calls = 0;
        VideoPollPolicy policy = NoSleepPolicy();
        policy.MaxNotFoundMisses = 3;

        ProviderHttpException error = await Assert.ThrowsAsync<ProviderHttpException>(
            () => ProviderVideoPolling.RunPollLoopAsync(
                "t1",
                policy,
                _ =>
                {
                    calls++;
                    throw new ProviderHttpException(404, "404 X", "", TimeSpan.Zero);
                }));

        Assert.Equal(404, error.StatusCode);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task 重试_畸形响应累计到上限后放弃()
    {
        int calls = 0;
        VideoPollPolicy policy = NoSleepPolicy();
        policy.MaxMalformedResponses = 2;

        await Assert.ThrowsAsync<ProviderResponseDecodeException>(
            () => ProviderVideoPolling.RunPollLoopAsync(
                "t1",
                policy,
                _ =>
                {
                    calls++;
                    throw new ProviderResponseDecodeException(new InvalidOperationException("bad"));
                }));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task 重试_两个计数器互不影响()
    {
        // 交替抛 404 与畸形响应：任一计数器都不该到上限（各 2 次，上限 4）。
        // 若两个计数器被合并成一个，第 4 次就会超限抛错而拿不到结果。
        int calls = 0;
        VideoPollPolicy policy = NoSleepPolicy();
        policy.MaxNotFoundMisses = 4;
        policy.MaxMalformedResponses = 4;

        Dictionary<string, object?> result = await ProviderVideoPolling.RunPollLoopAsync(
            "t1",
            policy,
            _ =>
            {
                calls++;
                if (calls <= 4)
                {
                    throw calls % 2 == 1
                        ? new ProviderHttpException(404, "404", "", TimeSpan.Zero)
                        : new ProviderResponseDecodeException(new InvalidOperationException("bad"));
                }
                return Task.FromResult(new VideoPollOutcome(true, Result("survived")));
            });

        Assert.Equal("survived", result["text"]);
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task 重试_成功后计数器归零()
    {
        VideoPollPolicy policy = NoSleepPolicy();
        policy.MaxNotFoundMisses = 3;
        int calls = 0;

        // 2 次 404（未到上限）→ 成功 → 再 2 次 404（若未归零则早已超限）。
        Dictionary<string, object?> result = await ProviderVideoPolling.RunPollLoopAsync(
            "t1",
            policy,
            _ =>
            {
                calls++;
                return calls switch
                {
                    1 or 2 => throw new ProviderHttpException(404, "404", "", TimeSpan.Zero),
                    3 => Task.FromResult(new VideoPollOutcome(false, null)),
                    4 or 5 => throw new ProviderHttpException(404, "404", "", TimeSpan.Zero),
                    _ => Task.FromResult(new VideoPollOutcome(true, Result("ok"))),
                };
            });

        Assert.Equal("ok", result["text"]);
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task 重试_不可重试时立即抛出()
    {
        int calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProviderVideoPolling.RunPollLoopAsync(
                "t1",
                NoSleepPolicy(),
                _ =>
                {
                    calls++;
                    throw new InvalidOperationException("致命错误");
                }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 重试_关闭RetryTransient时不重试()
    {
        int calls = 0;
        VideoPollPolicy policy = NoSleepPolicy();
        policy.RetryTransient = false;

        await Assert.ThrowsAsync<ProviderHttpException>(
            () => ProviderVideoPolling.RunPollLoopAsync(
                "t1",
                policy,
                _ =>
                {
                    calls++;
                    throw new ProviderHttpException(503, "503", "", TimeSpan.Zero);
                }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 重试_RetryAfter会拉长等待间隔()
    {
        List<TimeSpan> delays = [];
        VideoPollPolicy policy = NoSleepPolicy(delays);
        policy.Interval = TimeSpan.FromSeconds(1);
        int calls = 0;

        await ProviderVideoPolling.RunPollLoopAsync(
            "t1",
            policy,
            _ =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new ProviderHttpException(503, "503", "", TimeSpan.FromSeconds(10));
                }
                return Task.FromResult(new VideoPollOutcome(true, Result("ok")));
            });

        // 第二次等待取 max(Interval, RetryAfter) = 10s。
        Assert.Equal(TimeSpan.FromSeconds(10), delays[1]);
    }

    // ------------------------------------------------------------ 恢复通知

    [Fact]
    public async Task 通知_失败与恢复各一次()
    {
        List<(VideoPollEvent Event, Exception? Error)> events = [];
        VideoPollPolicy policy = NoSleepPolicy();
        policy.Notify = (_, pollEvent, error) =>
        {
            events.Add((pollEvent, error));
            return Task.CompletedTask;
        };
        int calls = 0;

        await ProviderVideoPolling.RunPollLoopAsync(
            "t1",
            policy,
            _ =>
            {
                calls++;
                return calls switch
                {
                    1 => throw new ProviderHttpException(503, "503", "", TimeSpan.Zero),
                    2 => throw new ProviderHttpException(503, "503", "", TimeSpan.Zero),
                    _ => Task.FromResult(new VideoPollOutcome(true, Result("ok"))),
                };
            });

        Assert.Equal(2, events.Count);
        Assert.Equal(VideoPollEvent.Retrying, events[0].Event);
        Assert.NotNull(events[0].Error);
        Assert.Equal(VideoPollEvent.Recovered, events[1].Event);
        Assert.Null(events[1].Error);
    }

    [Fact]
    public async Task 通知_持续失败只通知一次()
    {
        List<VideoPollEvent> events = [];
        VideoPollPolicy policy = NoSleepPolicy();
        policy.Notify = (_, pollEvent, _) =>
        {
            events.Add(pollEvent);
            return Task.CompletedTask;
        };
        // 503 可重试且无计数上限，会一直轮询到总时限耗尽。
        await Assert.ThrowsAsync<TimeoutException>(
            () => ProviderVideoPolling.RunPollLoopAsync(
                "t1",
                policy,
                _ => throw new ProviderHttpException(503, "503", "", TimeSpan.Zero)));

        // 连续失败期间不应反复播报，只播报一次进入重试态。
        Assert.Single(events);
        Assert.Equal(VideoPollEvent.Retrying, events[0]);
    }

    [Fact]
    public async Task 通知_抛错不影响轮询()
    {
        VideoPollPolicy policy = NoSleepPolicy();
        policy.Notify = (_, _, _) => throw new InvalidOperationException("通知失败");
        int calls = 0;

        Dictionary<string, object?> result = await ProviderVideoPolling.RunPollLoopAsync(
            "t1",
            policy,
            _ =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new ProviderHttpException(503, "503", "", TimeSpan.Zero);
                }
                return Task.FromResult(new VideoPollOutcome(true, Result("ok")));
            });

        Assert.Equal("ok", result["text"]);
    }

    // ------------------------------------------------------------ 取消

    [Fact]
    public async Task 取消_已取消令牌直接抛出()
    {
        using CancellationTokenSource source = new();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProviderVideoPolling.RunPollLoopAsync(
                "t1",
                NoSleepPolicy(),
                _ => Task.FromResult(new VideoPollOutcome(true, Result("x"))),
                source.Token));
    }

    // ------------------------------------------------------------ 下载

    [Fact]
    public async Task 下载_首次成功()
    {
        int calls = 0;
        (byte[] data, string mimeType) = await ProviderVideoPolling.RunDownloadAsync(
            "t1",
            NoSleepPolicy(),
            _ =>
            {
                calls++;
                return Task.FromResult((new byte[] { 1, 2, 3 }, "video/mp4"));
            });

        Assert.Equal(1, calls);
        Assert.Equal(3, data.Length);
        Assert.Equal("video/mp4", mimeType);
    }

    [Fact]
    public async Task 下载_瞬时失败后成功后()
    {
        int calls = 0;
        (byte[] data, _) = await ProviderVideoPolling.RunDownloadAsync(
            "t1",
            NoSleepPolicy(),
            _ =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new ProviderHttpException(503, "503", "", TimeSpan.Zero);
                }
                return Task.FromResult((new byte[] { 9 }, "video/mp4"));
            });

        Assert.Equal(2, calls);
        Assert.Equal(9, data[0]);
    }

    [Fact]
    public async Task 下载_重试耗尽后抛下载异常()
    {
        int calls = 0;
        VideoPollPolicy policy = NoSleepPolicy();
        policy.MaxDownloadTries = 2;

        VideoDownloadException error = await Assert.ThrowsAsync<VideoDownloadException>(
            () => ProviderVideoPolling.RunDownloadAsync(
                "task-9",
                policy,
                _ =>
                {
                    calls++;
                    throw new ProviderHttpException(503, "503", "", TimeSpan.Zero);
                }));

        Assert.Equal(2, calls);
        Assert.Equal("task-9", error.TaskID);
        Assert.Contains("视频结果下载失败（任务 task-9）", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 下载_不可重试时立即包装()
    {
        int calls = 0;
        VideoDownloadException error = await Assert.ThrowsAsync<VideoDownloadException>(
            () => ProviderVideoPolling.RunDownloadAsync(
                "t1",
                NoSleepPolicy(),
                _ =>
                {
                    calls++;
                    throw new InvalidOperationException("致命");
                }));

        Assert.Equal(1, calls);
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public void 下载异常_无任务ID时不带括号()
    {
        VideoDownloadException error = new("", new InvalidOperationException("boom"));

        Assert.Equal("视频结果下载失败：boom", error.Message);
    }

    // ------------------------------------------------------------ 可重试判定

    [Theory]
    // 404 无条件视为"未找到"；其余状态码走通用分支。
    [InlineData(404, true, true)]
    // 400 无 body 时无法判定"未就绪"（需要 body 里的固定短语），故落到通用分支 → 不可重试。
    [InlineData(400, false, false)]
    [InlineData(408, true, false)]
    [InlineData(409, true, false)]
    [InlineData(425, true, false)]
    [InlineData(429, true, false)]
    [InlineData(500, true, false)]
    [InlineData(503, true, false)]
    [InlineData(403, false, false)]
    [InlineData(401, false, false)]
    [InlineData(422, false, false)]
    public void 可重试_HTTP状态码_无body时(int statusCode, bool retry, bool notFound)
    {
        ProviderHttpException error = new(statusCode, $"{statusCode} X", "", TimeSpan.Zero);

        (bool actualRetry, bool actualNotFound) =
            ProviderVideoPolling.RetryablePollError(error, CancellationToken.None);

        Assert.Equal(retry, actualRetry);
        Assert.Equal(notFound, actualNotFound);
    }

    [Theory]
    [InlineData(500, false, false)]
    [InlineData(502, false, false)]
    public void 可重试_取消令牌时一律不可重试(int statusCode, bool retry, bool notFound)
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        ProviderHttpException error = new(statusCode, $"{statusCode} X", "", TimeSpan.Zero);

        (bool actualRetry, bool actualNotFound) =
            ProviderVideoPolling.RetryablePollError(error, source.Token);

        Assert.Equal(retry, actualRetry);
        Assert.Equal(notFound, actualNotFound);
    }

    [Fact]
    public void 可重试_空错误()
    {
        (bool retry, bool notFound) =
            ProviderVideoPolling.RetryablePollError(null, CancellationToken.None);

        Assert.False(retry);
        Assert.False(notFound);
    }

    [Fact]
    public void 可重试_下载异常不可重试()
    {
        (bool retry, _) = ProviderVideoPolling.RetryablePollError(
            new VideoDownloadException("t", new InvalidOperationException("x")), CancellationToken.None);

        Assert.False(retry);
    }

    [Fact]
    public void 可重试_渠道并发异常可重试()
    {
        (bool retry, bool notFound) = ProviderVideoPolling.RetryablePollError(
            new ProviderChannelSlotException(ProviderChannelSlotException.WaitTimeoutCode, "满载"),
            CancellationToken.None);

        Assert.True(retry);
        Assert.False(notFound);
    }

    [Fact]
    public void 可重试_熔断打开可重试()
    {
        (bool retry, _) = ProviderVideoPolling.RetryablePollError(
            new ProviderCircuitOpenException(), CancellationToken.None);

        Assert.True(retry);
    }

    [Fact]
    public void 可重试_解码错误可重试()
    {
        (bool retry, _) = ProviderVideoPolling.RetryablePollError(
            new ProviderResponseDecodeException(new InvalidOperationException("x")), CancellationToken.None);

        Assert.True(retry);
    }

    [Fact]
    public void 可重试_JSON语法错误可重试()
    {
        JsonException jsonError;
        try
        {
            using JsonDocument _ = JsonDocument.Parse("{bad");
            throw new InvalidOperationException("不应到达");
        }
        catch (JsonException error)
        {
            jsonError = error;
        }

        (bool retry, _) = ProviderVideoPolling.RetryablePollError(jsonError, CancellationToken.None);

        Assert.True(retry);
    }

    [Fact]
    public void 可重试_IO与超时可重试()
    {
        Assert.True(ProviderVideoPolling.RetryablePollError(new IOException("x"), CancellationToken.None).Retry);
        Assert.True(ProviderVideoPolling.RetryablePollError(
            new TimeoutException("x"), CancellationToken.None).Retry);
    }

    [Fact]
    public void 可重试_任务未就绪的400算未找到()
    {
        ProviderHttpException error = new(
            400, "400 X", """{"code":"task_not_exist"}""", TimeSpan.Zero);

        (bool retry, bool notFound) = ProviderVideoPolling.RetryablePollError(error, CancellationToken.None);

        Assert.True(retry);
        Assert.True(notFound);
    }

    [Fact]
    public void 可重试_非对象体不算未就绪()
    {
        ProviderHttpException error = new(400, "400 X", "not json", TimeSpan.Zero);

        (_, bool notFound) = ProviderVideoPolling.RetryablePollError(error, CancellationToken.None);

        Assert.False(notFound);
    }

    // ------------------------------------------------------------ 未就绪判定

    [Theory]
    [InlineData("task_not_exist")]
    [InlineData("task_not_found")]
    [InlineData("task not exist")]
    [InlineData("task not found")]
    [InlineData("TASK_NOT_FOUND")]
    public void 未就绪_固定短语命中(string value)
    {
        ProviderHttpException error = new(
            404, "404 X", $"{{\"code\":\"{value}\"}}", TimeSpan.Zero);

        Assert.True(ProviderVideoPolling.IsProviderTaskNotReadyError(error));
    }

    [Fact]
    public void 未就绪_消息字段也命中()
    {
        ProviderHttpException error = new(
            400, "400 X", """{"message":"task not found"}""", TimeSpan.Zero);

        Assert.True(ProviderVideoPolling.IsProviderTaskNotReadyError(error));
    }

    [Fact]
    public void 未就绪_嵌套error对象也命中()
    {
        ProviderHttpException error = new(
            404, "404 X", """{"error":{"code":"task_not_found"}}""", TimeSpan.Zero);

        Assert.True(ProviderVideoPolling.IsProviderTaskNotReadyError(error));
    }

    [Fact]
    public void 未就绪_不做自然语言扫描()
    {
        // 含 "not found" 的自然语言不该被识别，否则会反复轮询一个真正的错误。
        ProviderHttpException error = new(
            400, "400 X", """{"message":"the model was not found in this region"}""", TimeSpan.Zero);

        Assert.False(ProviderVideoPolling.IsProviderTaskNotReadyError(error));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(403)]
    public void 未就绪_仅400与404参与(int statusCode)
    {
        ProviderHttpException error = new(
            statusCode, $"{statusCode} X", """{"code":"task_not_found"}""", TimeSpan.Zero);

        Assert.False(ProviderVideoPolling.IsProviderTaskNotReadyError(error));
    }

    // ------------------------------------------------------------ 工具

    [Fact]
    public void RetryAfter_仅HTTP异常有值()
    {
        Assert.Equal(TimeSpan.FromSeconds(5),
            ProviderVideoPolling.ProviderRetryAfter(
                new ProviderHttpException(429, "429", "", TimeSpan.FromSeconds(5))));
        Assert.Equal(TimeSpan.Zero, ProviderVideoPolling.ProviderRetryAfter(new IOException("x")));
        Assert.Equal(TimeSpan.Zero, ProviderVideoPolling.ProviderRetryAfter(null));
    }

    [Fact]
    public async Task 睡眠_零或负时长立即返回()
    {
        await ProviderVideoPolling.SleepAsync(TimeSpan.Zero);
        await ProviderVideoPolling.SleepAsync(TimeSpan.FromMilliseconds(-1));
    }

    [Fact]
    public async Task 睡眠_可被取消()
    {
        using CancellationTokenSource source = new();
        source.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProviderVideoPolling.SleepAsync(TimeSpan.FromSeconds(10), source.Token));
    }

    [Fact]
    public void 轮询截止_远期()
    {
        DateTimeOffset deadline = ProviderVideoPolling.PollingDeadline(CancellationToken.None);

        Assert.True(deadline > DateTimeOffset.UtcNow.AddMinutes(50));
    }
}
