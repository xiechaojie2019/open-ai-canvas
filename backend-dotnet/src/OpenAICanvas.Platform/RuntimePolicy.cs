using System.Text.Json.Serialization;

namespace OpenAICanvas.Platform;

/// <summary>
/// 资源配额策略。对应 Go: <c>platform.RuntimeResourcePolicy</c>。
/// </summary>
/// <remarks>默认值与 Go 的 <c>DefaultRuntimePolicy()</c> 逐字一致。</remarks>
public sealed class RuntimeResourcePolicy
{
    [JsonPropertyName("resourceUploadMB")]
    public long ResourceUploadMB { get; init; } = 50;

    [JsonPropertyName("generatedFileMB")]
    public long GeneratedFileMB { get; init; } = 64;

    [JsonPropertyName("dailyUploadMB")]
    public long DailyUploadMB { get; init; } = 2048;

    [JsonPropertyName("storedFileGB")]
    public long StoredFileGB { get; init; } = 20;

    [JsonPropertyName("structuredDataMB")]
    public long StructuredDataMB { get; init; } = 256;

    [JsonPropertyName("taskDataGB")]
    public long TaskDataGB { get; init; } = 1;

    [JsonPropertyName("assetCount")]
    public long AssetCount { get; init; } = 2_000;

    [JsonPropertyName("canvasCount")]
    public long CanvasCount { get; init; } = 1_000;

    [JsonPropertyName("taskCount")]
    public long TaskCount { get; init; } = 20_000;

    [JsonPropertyName("apiCallLogCount")]
    public long ApiCallLogCount { get; init; } = 100_000;

    [JsonPropertyName("recycleBinRetentionDays")]
    public int RecycleBinRetentionDays { get; init; } = 30;
}

/// <summary>
/// 任务运行策略。对应 Go: <c>platform.RuntimeTaskPolicy</c>。
/// </summary>
public sealed class RuntimeTaskPolicy
{
    [JsonPropertyName("workerConcurrency")]
    public int WorkerConcurrency { get; init; } = 3;

    [JsonPropertyName("channelConcurrency")]
    public int ChannelConcurrency { get; init; } = 3;

    [JsonPropertyName("activeTaskLimit")]
    public int ActiveTaskLimit { get; init; } = 5;

    [JsonPropertyName("imageTimeoutMinutes")]
    public int ImageTimeoutMinutes { get; init; } = 8;

    [JsonPropertyName("textTimeoutMinutes")]
    public int TextTimeoutMinutes { get; init; } = 8;

    [JsonPropertyName("audioTimeoutMinutes")]
    public int AudioTimeoutMinutes { get; init; } = 8;

    [JsonPropertyName("videoTimeoutMinutes")]
    public int VideoTimeoutMinutes { get; init; } = 60;

    [JsonPropertyName("storyboardTimeoutMinutes")]
    public int StoryboardTimeoutMinutes { get; init; } = 20;

    [JsonPropertyName("defaultTimeoutMinutes")]
    public int DefaultTimeoutMinutes { get; init; } = 10;
}

/// <summary>
/// 请求频控策略。对应 Go: <c>platform.RuntimeRequestPolicy</c>。
/// </summary>
/// <remarks>默认值与 Go 的 <c>DefaultRuntimePolicy()</c> 逐字一致。</remarks>
public sealed class RuntimeRequestPolicy
{
    [JsonPropertyName("taskCreatePerMinute")]
    public int TaskCreatePerMinute { get; init; } = 30;

    [JsonPropertyName("resourceUploadPerMinute")]
    public int ResourceUploadPerMinute { get; init; } = 30;

    [JsonPropertyName("resourceImportPerMinute")]
    public int ResourceImportPerMinute { get; init; } = 30;

    [JsonPropertyName("assetWritePerMinute")]
    public int AssetWritePerMinute { get; init; } = 120;

    [JsonPropertyName("canvasWritePerMinute")]
    public int CanvasWritePerMinute { get; init; } = 120;

    [JsonPropertyName("registerPerHour")]
    public int RegisterPerHour { get; init; } = 30;

    [JsonPropertyName("emailCodePerHour")]
    public int EmailCodePerHour { get; init; } = 60;

    [JsonPropertyName("loginIPPerTenMinutes")]
    public int LoginIpPerTenMinutes { get; init; } = 50;

    [JsonPropertyName("loginAccountPerTenMinutes")]
    public int LoginAccountPerTenMinutes { get; init; } = 10;

    [JsonPropertyName("systemRelayPerMinute")]
    public int SystemRelayPerMinute { get; init; } = 120;

    [JsonPropertyName("customRelayPerMinute")]
    public int CustomRelayPerMinute { get; init; } = 120;

    [JsonPropertyName("customRelayConcurrency")]
    public int CustomRelayConcurrency { get; init; } = 4;

    [JsonPropertyName("customRelayRequestMB")]
    public long CustomRelayRequestMB { get; init; } = 32;

    [JsonPropertyName("customRelayResponseMB")]
    public long CustomRelayResponseMB { get; init; } = 32;

    [JsonPropertyName("customRelayTimeoutMinutes")]
    public int CustomRelayTimeoutMinutes { get; init; } = 10;

    [JsonPropertyName("systemRelayRequestMB")]
    public long SystemRelayRequestMB { get; init; } = 64;

    [JsonPropertyName("systemRelayResponseMB")]
    public long SystemRelayResponseMB { get; init; } = 128;

    [JsonPropertyName("channelCircuitFailureCount")]
    public int ChannelCircuitFailureCount { get; init; } = 5;

    [JsonPropertyName("channelCircuitOpenSeconds")]
    public int ChannelCircuitOpenSeconds { get; init; } = 60;

    /// <summary>频控上限。对应 Go: <c>maxRuntimeRate</c>（Go 侧为 999999）。</summary>
    public const int MaxRuntimeRate = 999_999;
}

/// <summary>
/// 完整运行时策略。对应 Go: <c>platform.RuntimePolicySetting</c>。
/// </summary>
public sealed class RuntimePolicySetting
{
    [JsonPropertyName("resource")]
    public RuntimeResourcePolicy Resource { get; init; } = new();

    [JsonPropertyName("task")]
    public RuntimeTaskPolicy Task { get; init; } = new();

    [JsonPropertyName("request")]
    public RuntimeRequestPolicy Request { get; init; } = new();

    /// <summary>默认策略。对应 Go: <c>DefaultRuntimePolicy()</c>。</summary>
    public static RuntimePolicySetting Default { get; } = new();
}

/// <summary>
/// 公开运行时上限。对应 Go: <c>platform.PublicRuntimeLimits</c>（struct，字段顺序即输出顺序）。
/// </summary>
public sealed class PublicRuntimeLimits
{
    [JsonPropertyName("activeTaskLimit")]
    public int ActiveTaskLimit { get; init; }

    [JsonPropertyName("resourceUploadMB")]
    public long ResourceUploadMB { get; init; }

    [JsonPropertyName("recycleBinRetentionDays")]
    public int RecycleBinRetentionDays { get; init; }
}

/// <summary>
/// 运行时策略来源。对应 Go: <c>Service.RuntimePolicy()</c>。
/// </summary>
/// <remarks>
/// 当前只返回内置默认值。管理员可覆盖的持久化配置（<c>runtime_policy</c> 设置键）
/// 与 env 覆盖属于后续的 platform 模块。
/// </remarks>
public interface IRuntimePolicyProvider
{
    RuntimePolicySetting Current();

    /// <summary>公开运行时上限。对应 Go: <c>PublicRuntimeLimits</c>。</summary>
    PublicRuntimeLimits PublicLimits();
}

/// <summary>返回内置默认值的策略提供者。</summary>
public sealed class DefaultRuntimePolicyProvider : IRuntimePolicyProvider
{
    private readonly RuntimePolicySetting _policy;

    public DefaultRuntimePolicyProvider()
    {
        // 与 Go 一致：worker / channel 并发数受环境变量覆盖。
        int worker = ReadEnvInt("CANVAS_WORKER_CONCURRENCY", 3);
        int channel = ReadEnvInt("CANVAS_CHANNEL_CONCURRENCY", 3);
        int circuitFailures = ReadEnvInt("CANVAS_CHANNEL_CIRCUIT_FAILURES", 5);
        int circuitSeconds = ReadEnvInt("CANVAS_CHANNEL_CIRCUIT_SECONDS", 60);

        RuntimePolicySetting baseline = RuntimePolicySetting.Default;

        _policy = new RuntimePolicySetting
        {
            Resource = baseline.Resource,
            Task = new RuntimeTaskPolicy
            {
                WorkerConcurrency = worker,
                ChannelConcurrency = channel,
                ActiveTaskLimit = baseline.Task.ActiveTaskLimit,
                ImageTimeoutMinutes = baseline.Task.ImageTimeoutMinutes,
                TextTimeoutMinutes = baseline.Task.TextTimeoutMinutes,
                AudioTimeoutMinutes = baseline.Task.AudioTimeoutMinutes,
                VideoTimeoutMinutes = baseline.Task.VideoTimeoutMinutes,
                StoryboardTimeoutMinutes = baseline.Task.StoryboardTimeoutMinutes,
                DefaultTimeoutMinutes = baseline.Task.DefaultTimeoutMinutes,
            },
            Request = new RuntimeRequestPolicy
            {
                TaskCreatePerMinute = baseline.Request.TaskCreatePerMinute,
                ResourceUploadPerMinute = baseline.Request.ResourceUploadPerMinute,
                ResourceImportPerMinute = baseline.Request.ResourceImportPerMinute,
                AssetWritePerMinute = baseline.Request.AssetWritePerMinute,
                CanvasWritePerMinute = baseline.Request.CanvasWritePerMinute,
                RegisterPerHour = baseline.Request.RegisterPerHour,
                EmailCodePerHour = baseline.Request.EmailCodePerHour,
                LoginIpPerTenMinutes = baseline.Request.LoginIpPerTenMinutes,
                LoginAccountPerTenMinutes = baseline.Request.LoginAccountPerTenMinutes,
                SystemRelayPerMinute = baseline.Request.SystemRelayPerMinute,
                CustomRelayPerMinute = baseline.Request.CustomRelayPerMinute,
                CustomRelayConcurrency = baseline.Request.CustomRelayConcurrency,
                CustomRelayRequestMB = baseline.Request.CustomRelayRequestMB,
                CustomRelayResponseMB = baseline.Request.CustomRelayResponseMB,
                CustomRelayTimeoutMinutes = baseline.Request.CustomRelayTimeoutMinutes,
                SystemRelayRequestMB = baseline.Request.SystemRelayRequestMB,
                SystemRelayResponseMB = baseline.Request.SystemRelayResponseMB,
                // 与 Go 一致：环境变量覆盖值会被夹到 [0, maxRuntimeConcurrency] / [0, 86400]。
                ChannelCircuitFailureCount = Math.Clamp(circuitFailures, 0, MaxRuntimeConcurrency),
                ChannelCircuitOpenSeconds = Math.Clamp(circuitSeconds, 0, 86_400),
            },
        };
    }

    /// <summary>对应 Go: <c>maxRuntimeConcurrency</c>（Go 侧为 999）。</summary>
    public const int MaxRuntimeConcurrency = 999;

    public RuntimePolicySetting Current() => _policy;

    public PublicRuntimeLimits PublicLimits() => new()
    {
        ActiveTaskLimit = _policy.Task.ActiveTaskLimit,
        ResourceUploadMB = _policy.Resource.ResourceUploadMB,
        RecycleBinRetentionDays = _policy.Resource.RecycleBinRetentionDays,
    };

    private static int ReadEnvInt(string key, int fallback)
    {
        string value = (Environment.GetEnvironmentVariable(key) ?? string.Empty).Trim();
        return int.TryParse(value, out int parsed) ? parsed : fallback;
    }
}
