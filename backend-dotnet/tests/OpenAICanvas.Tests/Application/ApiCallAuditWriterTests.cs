#nullable enable
using System.Text;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using OpenAICanvas.Platform;
using OpenAICanvas.Providers;
using Xunit;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 任务链路调用审计写入的专项测试。
/// 对应 Go: <c>recordProviderRequest</c> + <c>LogAPICall</c> + <c>ensureFailedProviderAttemptLogged</c>
///（<c>task_api_call_log_test.go</c> / <c>api_log_billing_test.go</c> 的移植面）。
/// </summary>
public sealed class ApiCallAuditWriterTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-audit-{Guid.NewGuid():N}.db");
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;

    public ApiCallAuditWriterTests()
    {
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
        new SchemaMigrator(_database).MigrateAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
        GC.SuppressFinalize(this);
    }

    private sealed class StubPolicyProvider : IRuntimePolicyProvider
    {
        public RuntimeResourcePolicy Resource { get; set; } = new()
        {
            GeneratedFileMB = 64,
            TaskDataGB = 1,
            ApiCallLogCount = 100_000,
        };

        public RuntimePolicySetting Current() => new()
        {
            Resource = Resource,
            Task = RuntimePolicySetting.Default.Task,
            Request = RuntimePolicySetting.Default.Request,
        };

        public PublicRuntimeLimits PublicLimits() => new();
    }

    private (ApiCallAuditWriter Writer, StubPolicyProvider Policy) Writer() =>
        (new ApiCallAuditWriter(_repository, new StubPolicyProvider()), new StubPolicyProvider());

    private static ProviderCallAudit Audit(
        string taskID = "task-audit-1",
        string billingOrderID = "",
        string capability = "image",
        string model = "seedream-4") =>
        new(
            UserID: "audit-user",
            TaskID: taskID,
            TraceID: "trace-1",
            RequestID: "request-1",
            BillingOrderID: billingOrderID,
            ChannelID: "channel-1",
            Capability: capability,
            Operation: "image",
            Model: model,
            VideoSeconds: 0);

    private static ProviderCallObservation Observation(
        string method = "POST",
        string path = "/v1/images/generations",
        int statusCode = 200,
        string response = """{"data":[{"url":"https://cdn.example.com/a.png"}],"usage":{"prompt_tokens":100,"completion_tokens":50}}""",
        string request = """{"model":"seedream-4","api_key":"sk-secret","image":"data:image/png;base64,AAAA"}""",
        Exception? failure = null) =>
        new(
            Method: method,
            UpstreamURL: "https://api.example.com" + path,
            Path: path,
            APIFormat: "openai",
            RequestContentType: "application/json",
            RequestBody: Encoding.UTF8.GetBytes(request),
            StatusCode: statusCode,
            ResponseBody: Encoding.UTF8.GetBytes(response),
            Failure: failure,
            DurationMs: 120,
            StartedAt: DateTime.UtcNow.AddMilliseconds(-120));

    private async Task SeedUserAndTaskAsync(string taskID)
    {
        await _repository.CreateAsync(new User
        {
            ID = "audit-user",
            Username = "audit-user",
            Email = "audit-user@example.com",
            DisplayName = "audit-user",
            Role = UserRole.UserRoleUser,
            Status = UserStatus.UserStatusActive,
            PasswordHash = "$2a$10$abcdefghijklmnopqrstuv",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _repository.CreateAsync(new TaskEntity
        {
            ID = taskID,
            UserID = "audit-user",
            Type = "canvas_image",
            Status = TaskStatus.TaskStatusRunning,
            Stage = "调用生成模型",
            Progress = 35,
            Prompt = "测试图片",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private async Task<IReadOnlyList<ApiCallLog>> AllLogsAsync()
    {
        (IReadOnlyList<ApiCallLog> logs, long _) = await _repository.QueryApiCallLogsAsync(
            new ApiCallLogFilter(
                new AnalyticsFilter(
                    DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), "", "", "", ""),
                "all", "", "", 1, 50));
        return logs;
    }

    private async Task<long> LogCountAsync()
    {
        (_, long total) = await _repository.QueryApiCallLogsAsync(
            new ApiCallLogFilter(
                new AnalyticsFilter(
                    DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), "", "", "", ""),
                "all", "", "", 1, 50));
        return total;
    }

    [Fact]
    public async Task 图片创建成功_写入backend_task日志并提取用量媒体数与脱敏报文()
    {
        await SeedUserAndTaskAsync("task-audit-1");
        (ApiCallAuditWriter writer, _) = Writer();

        await writer.RecordAsync(Audit(), Observation());

        ApiCallLog? log = (await AllLogsAsync()).FirstOrDefault();
        Assert.NotNull(log);
        Assert.Equal("backend-task", log!.Source);
        Assert.Equal("image", log.Capability);
        Assert.Equal("create", log.RequestKind);
        Assert.Equal(ApiCallStatus.ApiCallStatusSucceeded, log.Status);
        Assert.Equal(200, log.StatusCode);
        Assert.True(log.Billable);
        Assert.True(log.UsageAvailable);
        Assert.Equal(100, log.InputTokens);
        Assert.Equal(50, log.OutputTokens);
        Assert.Equal(1, log.MediaCount);
        // 请求报文脱敏：密钥键名替换、内嵌 data URL 省略。
        Assert.DoesNotContain("sk-secret", log.RequestBody);
        Assert.Contains("[REDACTED]", log.RequestBody);
        Assert.DoesNotContain("AAAA", log.RequestBody);
        Assert.Contains("内嵌媒体", log.RequestBody);
        // 上游请求 ID 从响应 id 字段提取。
        Assert.Equal("task-audit-1", log.TaskID);
    }

    [Fact]
    public async Task 有模型报价时_按请求与token估算成本()
    {
        await SeedUserAndTaskAsync("task-audit-cost");
        await _repository.CreateAsync(new ModelPricing
        {
            ID = Guid.NewGuid().ToString(),
            ChannelID = "channel-1",
            Model = "seedream-4",
            Capability = "image",
            Currency = "CNY",
            PerRequestMicros = 1000,
            InputPerMillionMicros = 1_000_000,
            OutputPerMillionMicros = 2_000_000,
            CachedPerMillionMicros = 0,
            PerMediaMicros = 0,
            PerVideoSecondMicros = 0,
        });
        (ApiCallAuditWriter writer, _) = Writer();

        await writer.RecordAsync(Audit(taskID: "task-audit-cost"), Observation());

        ApiCallLog? log = (await AllLogsAsync()).FirstOrDefault();
        Assert.NotNull(log);
        // 1000 + 100*1000000/1e6 + 50*2000000/1e6 = 1200。
        Assert.Equal(1200, log!.EstimatedCostMicros);
        Assert.True(log.CostAvailable);
        Assert.Equal("CNY", log.Currency);
    }

    [Fact]
    public async Task 视频轮询_合并回create根行不再新插入()
    {
        await SeedUserAndTaskAsync("task-audit-video");
        (ApiCallAuditWriter writer, _) = Writer();

        await writer.RecordAsync(
            Audit(taskID: "task-audit-video", capability: "video", model: "seedance-pro"),
            Observation(
                path: "/v1/contents/generations/tasks",
                response: """{"id":"video-task-9","status":"running"}"""));
        await writer.RecordAsync(
            Audit(taskID: "task-audit-video", capability: "video", model: "seedance-pro"),
            Observation(
                method: "GET",
                path: "/v1/contents/generations/tasks/video-task-9",
                response: """{"id":"video-task-9","status":"succeeded","usage":{"completion_tokens":800}}"""));

        IReadOnlyList<ApiCallLog> logs = await AllLogsAsync();
        Assert.Equal(1, logs.Count);
        ApiCallLog root = logs.Single();
        // 轮询观测并入根行：次数、终态用量与上游请求 ID。
        Assert.Equal("create", root.RequestKind);
        Assert.Equal(1, root.PollCount);
        Assert.Equal("video-task-9", root.ProviderRequestID);
        Assert.True(root.UsageAvailable);
        Assert.Equal(800, root.OutputTokens);
        Assert.Equal(ApiCallStatus.ApiCallStatusSucceeded, root.Status);
        // 任务行回写上游请求 ID（worker 崩溃后恢复轮询的数据源）。
        TaskEntity stored = (await _repository.TaskAsync("task-audit-video"))!;
        Assert.Equal("video-task-9", stored.ProviderRequestID);
    }

    [Fact]
    public async Task 日志条数超限_拒绝写入且不抛出到调用方()
    {
        await SeedUserAndTaskAsync("task-audit-quota");
        StubPolicyProvider policy = new()
        {
            Resource = new RuntimeResourcePolicy
            {
                GeneratedFileMB = 64,
                TaskDataGB = 1,
                ApiCallLogCount = 1,
            },
        };
        ApiCallAuditWriter writer = new(_repository, policy);

        await writer.RecordAsync(Audit(taskID: "task-audit-quota"), Observation());
        await writer.RecordAsync(Audit(taskID: "task-audit-quota"), Observation());

        Assert.Equal(1, await LogCountAsync());
    }

    [Fact]
    public async Task 业务失败响应_HTTP200但业务码非0时记为失败()
    {
        await SeedUserAndTaskAsync("task-audit-biz");
        (ApiCallAuditWriter writer, _) = Writer();

        await writer.RecordAsync(
            Audit(taskID: "task-audit-biz"),
            Observation(response: """{"code":1,"msg":"余额不足"}"""));

        ApiCallLog? log = (await AllLogsAsync()).FirstOrDefault();
        Assert.NotNull(log);
        Assert.Equal(ApiCallStatus.ApiCallStatusFailed, log!.Status);
        Assert.Contains("余额不足", log.Error);
    }

    [Fact]
    public async Task 上游5xx_记录失败并归类安全错误文案()
    {
        await SeedUserAndTaskAsync("task-audit-5xx");
        (ApiCallAuditWriter writer, _) = Writer();

        await writer.RecordAsync(
            Audit(taskID: "task-audit-5xx"),
            Observation(statusCode: 500, response: "boom", failure: new ProviderHttpException(500, "500 Internal", "boom", TimeSpan.Zero)));

        ApiCallLog? log = (await AllLogsAsync()).FirstOrDefault();
        Assert.NotNull(log);
        Assert.Equal(ApiCallStatus.ApiCallStatusFailed, log!.Status);
        Assert.Equal(500, log.StatusCode);
        Assert.Contains("模型服务暂时不可用", log.Error);
        Assert.Contains("boom", log.Error);
    }

    [Fact]
    public async Task 任务失败兜底_无任何日志时补INTERNAL记录且不重复()
    {
        await SeedUserAndTaskAsync("task-audit-fallback");
        (ApiCallAuditWriter writer, _) = Writer();
        TaskEntity task = (await _repository.TaskAsync("task-audit-fallback"))!;

        await writer.EnsureFailedAttemptLoggedAsync(
            task, new ProviderHttpException(500, "500", "boom", TimeSpan.Zero));
        await writer.EnsureFailedAttemptLoggedAsync(
            task, new ProviderHttpException(500, "500", "boom", TimeSpan.Zero));

        IReadOnlyList<ApiCallLog> logs = await AllLogsAsync();
        Assert.Equal(1, logs.Count);
        ApiCallLog log = logs.Single();
        Assert.Equal("INTERNAL", log.Method);
        Assert.Equal("/task/provider-request", log.Path);
        Assert.Equal("provider_request_unlogged", log.ErrorCode);
        Assert.Equal(500, log.StatusCode);
        Assert.False(log.Billable);
        Assert.Equal("backend-task", log.Source);
    }

    [Fact]
    public async Task 任务失败兜底_已有调用日志时不补()
    {
        await SeedUserAndTaskAsync("task-audit-skip");
        (ApiCallAuditWriter writer, _) = Writer();
        TaskEntity task = (await _repository.TaskAsync("task-audit-skip"))!;

        await writer.RecordAsync(Audit(taskID: "task-audit-skip"), Observation());
        await writer.EnsureFailedAttemptLoggedAsync(
            task, new InvalidOperationException("上游断开"));

        Assert.Equal(1, await LogCountAsync());
    }

    [Theory]
    [InlineData("canvas_image", "image")]
    [InlineData("canvas_video", "video")]
    [InlineData("canvas_text", "text")]
    [InlineData("canvas_audio", "audio")]
    [InlineData("cloud_agent_step", "text")]
    [InlineData("storyboard", "text")]
    [InlineData("other", "")]
    public void 任务类型推断能力_与Go一致(string taskType, string expected) =>
        Assert.Equal(expected, ApiCallAuditWriter.CapabilityFromTaskType(taskType));
}
