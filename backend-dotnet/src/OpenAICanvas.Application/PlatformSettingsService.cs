#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>公开运行时策略。对应 Go: <c>platform.PublicRuntimePolicySetting</c>（字段展开平铺）。</summary>
public sealed class PublicRuntimePolicySettingDto
{
    [JsonPropertyName("resource")]
    public Platform.RuntimeResourcePolicy Resource { get; init; } = new();

    [JsonPropertyName("task")]
    public Platform.RuntimeTaskPolicy Task { get; init; } = new();

    [JsonPropertyName("request")]
    public Platform.RuntimeRequestPolicy Request { get; init; } = new();

    [JsonPropertyName("configured")]
    public bool Configured { get; init; }

    [JsonPropertyName("updatedBy")]
    public string UpdatedBy { get; init; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }
}

/// <summary>响应拦截规则。对应 Go: <c>app.ResponseInterceptionRule</c>。</summary>
public sealed class ResponseInterceptionRuleDto
{
    [JsonPropertyName("contains")]
    public string Contains { get; set; } = "";

    [JsonPropertyName("replace")]
    public string Replace { get; set; } = "";
}

/// <summary>响应拦截设置。对应 Go: <c>app.ResponseInterceptionSetting</c>。</summary>
public sealed class ResponseInterceptionSettingDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("rules")]
    public List<ResponseInterceptionRuleDto> Rules { get; set; } = [];
}

/// <summary>方舟素材库设置请求。对应 Go: <c>app.ArkPrivateAssetSettingRequest</c>。</summary>
public sealed class ArkPrivateAssetSettingRequestDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("region")]
    public string Region { get; set; } = "";

    [JsonPropertyName("projectName")]
    public string ProjectName { get; set; } = "";

    [JsonPropertyName("accessKeyId")]
    public string AccessKeyID { get; set; } = "";

    [JsonPropertyName("accessKeySecret")]
    public string AccessKeySecret { get; set; } = "";
}

/// <summary>公开方舟素材库设置。对应 Go: <c>app.PublicArkPrivateAssetSetting</c>。</summary>
public sealed class PublicArkPrivateAssetSettingDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("region")]
    public string Region { get; init; } = "";

    [JsonPropertyName("projectName")]
    public string ProjectName { get; init; } = "";

    [JsonPropertyName("accessKeyId")]
    public string AccessKeyID { get; init; } = "";

    [JsonPropertyName("hasAccessKeySecret")]
    public bool HasAccessKeySecret { get; init; }

    [JsonPropertyName("updatedBy")]
    public string UpdatedBy { get; init; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// 平台设置：运行时策略、模型响应拦截、方舟素材库。
/// 对应 Go: <c>internal/platform/runtime_policy.go</c> / <c>app/response_interception.go</c> /
/// <c>app/settings_ark_assets.go</c>。
/// </summary>
/// <remarks>
/// 与既有 <see cref="Platform.DefaultRuntimePolicyProvider"/> 的关系：Go 的
/// <c>RuntimePolicy()</c> 优先读取持久化设置，缺省时回落内置默认值；本服务提供
/// 同一读取语义，全局 provider 的切换随任务引擎批次接入。
/// </remarks>
public sealed class PlatformSettingsService
{
    private const string RuntimePolicySettingKey = "runtime_policy";
    private const string ResponseInterceptionSettingKey = "response_interception";
    private const string ArkPrivateAssetSettingKey = "ark_private_assets";

    private const long MaxRuntimeUploadMB = 999;
    private const long MaxRuntimeStorageGB = 999;
    private const long MaxRuntimeDataMB = 999_999;
    private const long MaxRuntimeCount = 999_999_999;
    private const int MaxRuntimeRate = 999_999;
    private const int MaxRuntimeConcurrency = 999;
    private const int MaxRuntimeTimeoutMinutes = 9_999;

    private const int MaxResponseInterceptionRules = 100;
    private const int MaxResponseInterceptionTextRunes = 200;
    private const int MaxResponseInterceptionReplaceRunes = 500;

    private readonly Repository _repository;
    private readonly string _dataDir;

    public PlatformSettingsService(Repository repository, string? dataDir = null)
    {
        _repository = repository;
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? "data" : dataDir!;
    }

    // ------------------------------------------------------------ 运行时策略

    /// <summary>生效策略（设置优先，缺省回落默认）。对应 Go: <c>RuntimePolicy</c>。</summary>
    public async Task<Platform.RuntimePolicySetting> EffectivePolicyAsync(
        CancellationToken cancellationToken = default)
    {
        (SystemSetting? _, Platform.RuntimePolicySetting value) = await ReadRuntimePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        return value;
    }

    /// <summary>管理员读取运行时策略。对应 Go: <c>AdminRuntimePolicySetting</c>。</summary>
    public async Task<PublicRuntimePolicySettingDto> AdminRuntimePolicySettingAsync(
        User actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (SystemSetting? setting, Platform.RuntimePolicySetting value) = await ReadRuntimePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        return PublicRuntimePolicy(setting, value);
    }

    /// <summary>自用部署的策略上限模板。对应 Go: <c>AdminSelfUseRuntimePolicy</c>。</summary>
    public Task<PublicRuntimePolicySettingDto> AdminSelfUseRuntimePolicyAsync(
        User actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        return Task.FromResult(PublicRuntimePolicy(null, SelfUseRuntimePolicy()));
    }

    /// <summary>更新运行时策略。对应 Go: <c>UpdateRuntimePolicySetting</c>。</summary>
    public async Task<PublicRuntimePolicySettingDto> UpdateRuntimePolicySettingAsync(
        User actor,
        Platform.RuntimePolicySetting value,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        ValidateRuntimePolicy(value);
        (SystemSetting? current, Platform.RuntimePolicySetting _) = await ReadRuntimePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        SystemSetting setting = new()
        {
            Key = RuntimePolicySettingKey,
            ValueJSON = JsonSerializer.Serialize(value),
            UpdatedBy = actor.ID,
            CreatedAt = current?.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await _repository.SaveSystemSettingAsync(setting, cancellationToken).ConfigureAwait(false);
        return PublicRuntimePolicy(setting, value);
    }

    /// <summary>重置运行时策略。对应 Go: <c>ResetRuntimePolicySetting</c>。</summary>
    public async Task<PublicRuntimePolicySettingDto> ResetRuntimePolicySettingAsync(
        User actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        await _repository.DeleteSystemSettingAsync(RuntimePolicySettingKey, cancellationToken).ConfigureAwait(false);
        return PublicRuntimePolicy(null, Platform.RuntimePolicySetting.Default);
    }

    private async Task<(SystemSetting? Setting, Platform.RuntimePolicySetting Value)> ReadRuntimePolicyAsync(
        CancellationToken cancellationToken)
    {
        SystemSetting? setting = await _repository
            .SystemSettingAsync(RuntimePolicySettingKey, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            return (null, Platform.RuntimePolicySetting.Default);
        }
        Platform.RuntimePolicySetting value;
        try
        {
            if (string.IsNullOrWhiteSpace(setting.ValueJSON))
            {
                throw new InvalidOperationException();
            }
            value = JsonSerializer.Deserialize<Platform.RuntimePolicySetting>(
                setting.ValueJSON, CanvasJsonOptions) ?? throw new InvalidOperationException();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("资源与请求策略配置格式无效");
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("资源与请求策略配置格式无效");
        }
        ValidateRuntimePolicy(value);
        return (setting, value);
    }

    private static PublicRuntimePolicySettingDto PublicRuntimePolicy(
        SystemSetting? setting, Platform.RuntimePolicySetting value) => new()
        {
            Resource = value.Resource,
            Task = value.Task,
            Request = value.Request,
            Configured = setting is not null,
            UpdatedBy = setting?.UpdatedBy ?? "",
            CreatedAt = setting?.CreatedAt ?? default,
            UpdatedAt = setting?.UpdatedAt ?? default,
        };

    /// <summary>自用部署上限模板。对应 Go: <c>selfUseRuntimePolicy</c>。</summary>
    public static Platform.RuntimePolicySetting SelfUseRuntimePolicy()
    {
        Platform.RuntimePolicySetting baseline = Platform.RuntimePolicySetting.Default;
        return new Platform.RuntimePolicySetting
        {
            Resource = new Platform.RuntimeResourcePolicy
            {
                ResourceUploadMB = MaxRuntimeUploadMB,
                GeneratedFileMB = MaxRuntimeUploadMB,
                DailyUploadMB = MaxRuntimeDataMB,
                StoredFileGB = MaxRuntimeStorageGB,
                StructuredDataMB = MaxRuntimeDataMB,
                TaskDataGB = MaxRuntimeStorageGB,
                AssetCount = MaxRuntimeCount,
                CanvasCount = MaxRuntimeCount,
                TaskCount = MaxRuntimeCount,
                ApiCallLogCount = MaxRuntimeCount,
                RecycleBinRetentionDays = 0,
            },
            Task = new Platform.RuntimeTaskPolicy
            {
                WorkerConcurrency = MaxRuntimeConcurrency,
                ChannelConcurrency = MaxRuntimeConcurrency,
                ActiveTaskLimit = MaxRuntimeConcurrency,
                ImageTimeoutMinutes = MaxRuntimeTimeoutMinutes,
                TextTimeoutMinutes = MaxRuntimeTimeoutMinutes,
                AudioTimeoutMinutes = MaxRuntimeTimeoutMinutes,
                VideoTimeoutMinutes = MaxRuntimeTimeoutMinutes,
                StoryboardTimeoutMinutes = MaxRuntimeTimeoutMinutes,
                DefaultTimeoutMinutes = MaxRuntimeTimeoutMinutes,
            },
            Request = new Platform.RuntimeRequestPolicy
            {
                TaskCreatePerMinute = MaxRuntimeRate,
                ResourceUploadPerMinute = MaxRuntimeRate,
                ResourceImportPerMinute = MaxRuntimeRate,
                AssetWritePerMinute = MaxRuntimeRate,
                CanvasWritePerMinute = MaxRuntimeRate,
                RegisterPerHour = MaxRuntimeRate,
                EmailCodePerHour = MaxRuntimeRate,
                LoginIpPerTenMinutes = MaxRuntimeRate,
                LoginAccountPerTenMinutes = MaxRuntimeRate,
                SystemRelayPerMinute = MaxRuntimeRate,
                CustomRelayPerMinute = MaxRuntimeRate,
                CustomRelayConcurrency = MaxRuntimeConcurrency,
                CustomRelayRequestMB = MaxRuntimeUploadMB,
                CustomRelayResponseMB = MaxRuntimeUploadMB,
                CustomRelayTimeoutMinutes = MaxRuntimeTimeoutMinutes,
                SystemRelayRequestMB = MaxRuntimeUploadMB,
                SystemRelayResponseMB = MaxRuntimeUploadMB,
                ChannelCircuitFailureCount = MaxRuntimeConcurrency,
                ChannelCircuitOpenSeconds = 1,
            },
        };
    }

    /// <summary>策略校验（错误文案逐字对齐 Go）。对应 Go: <c>validateRuntimePolicy</c>。</summary>
    private static void ValidateRuntimePolicy(Platform.RuntimePolicySetting value)
    {
        Platform.RuntimeResourcePolicy resource = value.Resource;
        if (resource.ResourceUploadMB < 1 || resource.ResourceUploadMB > MaxRuntimeUploadMB)
        {
            throw AppError.BadAuthRequest($"普通资源单文件必须是 1-{MaxRuntimeUploadMB} MB 的整数");
        }
        if (resource.GeneratedFileMB < 1 || resource.GeneratedFileMB > MaxRuntimeUploadMB)
        {
            throw AppError.BadAuthRequest($"单个生成资源必须是 1-{MaxRuntimeUploadMB} MB 的整数");
        }
        if (resource.DailyUploadMB < 1 || resource.DailyUploadMB > MaxRuntimeDataMB)
        {
            throw AppError.BadAuthRequest($"每日上传量必须是 1-{MaxRuntimeDataMB} MB 的整数");
        }
        if (resource.StoredFileGB < 1 || resource.StoredFileGB > MaxRuntimeStorageGB
            || resource.TaskDataGB < 1 || resource.TaskDataGB > MaxRuntimeStorageGB)
        {
            throw AppError.BadAuthRequest($"账号文件与任务数据容量必须是 1-{MaxRuntimeStorageGB} GB 的整数");
        }
        if (resource.StructuredDataMB < 1 || resource.StructuredDataMB > MaxRuntimeDataMB)
        {
            throw AppError.BadAuthRequest($"结构化数据容量必须是 1-{MaxRuntimeDataMB} MB 的整数");
        }
        long storedMB = resource.StoredFileGB * 1024;
        if (resource.ResourceUploadMB > storedMB || resource.GeneratedFileMB > storedMB)
        {
            throw AppError.BadAuthRequest("单文件上限不能大于账号文件总容量");
        }
        if (resource.AssetCount < 1 || resource.AssetCount > MaxRuntimeCount)
        {
            throw AppError.BadAuthRequest($"素材数量必须是 1-{MaxRuntimeCount} 的整数");
        }
        if (resource.CanvasCount < 1 || resource.CanvasCount > MaxRuntimeCount)
        {
            throw AppError.BadAuthRequest($"画布数量必须是 1-{MaxRuntimeCount} 的整数");
        }
        if (resource.TaskCount < 1 || resource.TaskCount > MaxRuntimeCount)
        {
            throw AppError.BadAuthRequest($"任务历史数量必须是 1-{MaxRuntimeCount} 的整数");
        }
        if (resource.ApiCallLogCount < 1 || resource.ApiCallLogCount > MaxRuntimeCount)
        {
            throw AppError.BadAuthRequest($"请求日志数量必须是 1-{MaxRuntimeCount} 的整数");
        }
        if (resource.RecycleBinRetentionDays < 0 || resource.RecycleBinRetentionDays > 365)
        {
            throw AppError.BadAuthRequest("回收站保留天数必须是 0-365 的整数 (0 表示不自动清理)");
        }

        Platform.RuntimeTaskPolicy task = value.Task;
        if (task.WorkerConcurrency < 1 || task.WorkerConcurrency > MaxRuntimeConcurrency)
        {
            throw AppError.BadAuthRequest($"Worker 并发数必须是 1-{MaxRuntimeConcurrency} 的整数");
        }
        if (task.ChannelConcurrency < 1 || task.ChannelConcurrency > MaxRuntimeConcurrency)
        {
            throw AppError.BadAuthRequest($"全局渠道并发数必须是 1-{MaxRuntimeConcurrency} 的整数");
        }
        if (task.ActiveTaskLimit < 1 || task.ActiveTaskLimit > MaxRuntimeConcurrency)
        {
            throw AppError.BadAuthRequest($"活动任务上限必须是 1-{MaxRuntimeConcurrency} 的整数");
        }
        foreach ((string label, int minutes) in new[]
        {
            ("图片任务超时", task.ImageTimeoutMinutes),
            ("文本任务超时", task.TextTimeoutMinutes),
            ("音频任务超时", task.AudioTimeoutMinutes),
            ("视频任务超时", task.VideoTimeoutMinutes),
            ("分镜任务超时", task.StoryboardTimeoutMinutes),
            ("默认任务超时", task.DefaultTimeoutMinutes),
        })
        {
            if (minutes < 1 || minutes > MaxRuntimeTimeoutMinutes)
            {
                throw AppError.BadAuthRequest($"{label}必须是 1-{MaxRuntimeTimeoutMinutes} 分钟的整数");
            }
        }

        Platform.RuntimeRequestPolicy request = value.Request;
        foreach ((string label, int rate) in new[]
        {
            ("任务创建频控", request.TaskCreatePerMinute),
            ("资源上传频控", request.ResourceUploadPerMinute),
            ("资源导入频控", request.ResourceImportPerMinute),
            ("素材写入频控", request.AssetWritePerMinute),
            ("画布写入频控", request.CanvasWritePerMinute),
            ("注册频控", request.RegisterPerHour),
            ("验证码频控", request.EmailCodePerHour),
            ("登录 IP 频控", request.LoginIpPerTenMinutes),
            ("登录账号频控", request.LoginAccountPerTenMinutes),
            ("系统渠道频控", request.SystemRelayPerMinute),
            ("自定义渠道频控", request.CustomRelayPerMinute),
        })
        {
            if (rate < 1 || rate > MaxRuntimeRate)
            {
                throw AppError.BadAuthRequest($"{label}必须是 1-{MaxRuntimeRate} 的整数");
            }
        }
        if (request.CustomRelayConcurrency < 1 || request.CustomRelayConcurrency > MaxRuntimeConcurrency)
        {
            throw AppError.BadAuthRequest($"自定义渠道并发必须是 1-{MaxRuntimeConcurrency} 的整数");
        }
        foreach ((string label, long sizeMB) in new[]
        {
            ("自定义渠道请求体", request.CustomRelayRequestMB),
            ("自定义渠道响应体", request.CustomRelayResponseMB),
            ("系统渠道请求体", request.SystemRelayRequestMB),
            ("系统渠道响应体", request.SystemRelayResponseMB),
        })
        {
            if (sizeMB < 1 || sizeMB > MaxRuntimeUploadMB)
            {
                throw AppError.BadAuthRequest($"{label}必须是 1-{MaxRuntimeUploadMB} MB 的整数");
            }
        }
        if (request.CustomRelayTimeoutMinutes < 1 || request.CustomRelayTimeoutMinutes > MaxRuntimeTimeoutMinutes)
        {
            throw AppError.BadAuthRequest($"自定义渠道超时必须是 1-{MaxRuntimeTimeoutMinutes} 分钟的整数");
        }
        if (request.ChannelCircuitFailureCount < 1 || request.ChannelCircuitFailureCount > MaxRuntimeConcurrency)
        {
            throw AppError.BadAuthRequest($"渠道熔断失败次数必须是 1-{MaxRuntimeConcurrency} 的整数");
        }
        if (request.ChannelCircuitOpenSeconds < 1 || request.ChannelCircuitOpenSeconds > 86_400)
        {
            throw AppError.BadAuthRequest("渠道熔断时长必须是 1-86400 秒的整数");
        }
    }

    // ------------------------------------------------------------ 模型响应拦截

    /// <summary>读取拦截设置。对应 Go: <c>AdminResponseInterceptionSetting</c>。</summary>
    public async Task<ResponseInterceptionSettingDto> AdminResponseInterceptionSettingAsync(
        User actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        return await ReadInterceptionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>更新拦截设置。对应 Go: <c>UpdateResponseInterceptionSetting</c>。</summary>
    public async Task<ResponseInterceptionSettingDto> UpdateResponseInterceptionSettingAsync(
        User actor,
        ResponseInterceptionSettingDto value,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        value = NormalizeInterception(value);
        ValidateInterception(value);
        SystemSetting? current = await _repository
            .SystemSettingAsync(ResponseInterceptionSettingKey, cancellationToken).ConfigureAwait(false);
        SystemSetting setting = new()
        {
            Key = ResponseInterceptionSettingKey,
            ValueJSON = JsonSerializer.Serialize(value, CanvasJsonOptions),
            UpdatedBy = actor.ID,
            CreatedAt = current?.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await _repository.SaveSystemSettingAsync(setting, cancellationToken).ConfigureAwait(false);
        return value;
    }

    /// <summary>读取持久化拦截设置（私有）。对应 Go: <c>responseInterceptionSetting</c>。</summary>
    public async Task<ResponseInterceptionSettingDto> ReadInterceptionAsync(
        CancellationToken cancellationToken = default)
    {
        SystemSetting? setting = await _repository
            .SystemSettingAsync(ResponseInterceptionSettingKey, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            return new ResponseInterceptionSettingDto();
        }
        ResponseInterceptionSettingDto value = new();
        if (string.IsNullOrWhiteSpace(setting.ValueJSON))
        {
            return value;
        }
        try
        {
            value = JsonSerializer.Deserialize<ResponseInterceptionSettingDto>(
                setting.ValueJSON, CanvasJsonOptions) ?? value;
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"模型响应拦截配置格式错误: {error.Message}");
        }
        return NormalizeInterception(value);
    }

    private static ResponseInterceptionSettingDto NormalizeInterception(ResponseInterceptionSettingDto value)
    {
        value.Rules ??= [];
        foreach (ResponseInterceptionRuleDto rule in value.Rules)
        {
            rule.Contains = rule.Contains.Trim();
            rule.Replace = rule.Replace.Trim();
        }
        return value;
    }

    private static void ValidateInterception(ResponseInterceptionSettingDto value)
    {
        if (value.Rules.Count > MaxResponseInterceptionRules)
        {
            throw AppError.BadAuthRequest($"模型响应拦截规则不能超过 {MaxResponseInterceptionRules} 条");
        }
        for (int index = 0; index < value.Rules.Count; index++)
        {
            ResponseInterceptionRuleDto rule = value.Rules[index];
            if (rule.Contains.Length == 0)
            {
                throw AppError.BadAuthRequest($"第 {index + 1} 条拦截规则的匹配文案不能为空");
            }
            if (rule.Contains.EnumerateRunes().Count() > MaxResponseInterceptionTextRunes)
            {
                throw AppError.BadAuthRequest(
                    $"第 {index + 1} 条拦截规则的匹配文案不能超过 {MaxResponseInterceptionTextRunes} 个字符");
            }
            if (rule.Replace.Length == 0)
            {
                throw AppError.BadAuthRequest($"第 {index + 1} 条拦截规则的替换文案不能为空");
            }
            if (rule.Replace.EnumerateRunes().Count() > MaxResponseInterceptionReplaceRunes)
            {
                throw AppError.BadAuthRequest(
                    $"第 {index + 1} 条拦截规则的替换文案不能超过 {MaxResponseInterceptionReplaceRunes} 个字符");
            }
        }
    }

    // ------------------------------------------------------------ 方舟素材库

    /// <summary>读取方舟素材库设置。对应 Go: <c>AdminArkPrivateAssetSetting</c>。</summary>
    public async Task<PublicArkPrivateAssetSettingDto> AdminArkPrivateAssetSettingAsync(
        User actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (SystemSetting? setting, ArkPrivateAssetValue value) = await ReadArkPrivateAssetAsync(cancellationToken)
            .ConfigureAwait(false);
        return PublicArkPrivateAsset(setting, value);
    }

    /// <summary>更新方舟素材库设置。对应 Go: <c>UpdateArkPrivateAssetSetting</c>。</summary>
    public async Task<PublicArkPrivateAssetSettingDto> UpdateArkPrivateAssetSettingAsync(
        User actor,
        ArkPrivateAssetSettingRequestDto request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (SystemSetting? currentSetting, ArkPrivateAssetValue current) = await ReadArkPrivateAssetAsync(cancellationToken)
            .ConfigureAwait(false);
        ArkPrivateAssetValue next = ArkPrivateAssetFromRequest(request, current);
        SystemSetting setting = new()
        {
            Key = ArkPrivateAssetSettingKey,
            UpdatedBy = actor.ID,
            CreatedAt = currentSetting?.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await SaveArkPrivateAssetAsync(setting, next, cancellationToken).ConfigureAwait(false);
        return PublicArkPrivateAsset(setting, next);
    }

    private async Task<(SystemSetting? Setting, ArkPrivateAssetValue Value)> ReadArkPrivateAssetAsync(
        CancellationToken cancellationToken)
    {
        SystemSetting? setting = await _repository
            .SystemSettingAsync(ArkPrivateAssetSettingKey, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            return (null, new ArkPrivateAssetValue());
        }
        ArkPrivateAssetValue value = new();
        if (setting.ValueJSON.Trim().Length > 0)
        {
            try
            {
                value = JsonSerializer.Deserialize<ArkPrivateAssetValue>(
                    setting.ValueJSON, CanvasJsonOptions) ?? value;
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("方舟素材库配置格式无效");
            }
        }
        bool needsMigration = value.AccessKeySecret.Length > 0
            && !value.AccessKeySecret.StartsWith(SettingsCrypto.EncryptedPrefix, StringComparison.Ordinal);
        value.AccessKeySecret = SettingsCrypto.DecryptSecret(value.AccessKeySecret, _dataDir);
        value = NormalizeArkPrivateAsset(value);
        if (needsMigration)
        {
            await SaveArkPrivateAssetAsync(setting, value, cancellationToken).ConfigureAwait(false);
        }
        return (setting, value);
    }

    private async Task SaveArkPrivateAssetAsync(
        SystemSetting setting, ArkPrivateAssetValue value, CancellationToken cancellationToken)
    {
        ArkPrivateAssetValue stored = NormalizeArkPrivateAsset(value);
        stored.AccessKeySecret = SettingsCrypto.EncryptSecret(stored.AccessKeySecret, _dataDir);
        setting.ValueJSON = JsonSerializer.Serialize(stored, CanvasJsonOptions);
        await _repository.SaveSystemSettingAsync(setting, cancellationToken).ConfigureAwait(false);
    }

    private static ArkPrivateAssetValue ArkPrivateAssetFromRequest(
        ArkPrivateAssetSettingRequestDto request, ArkPrivateAssetValue current)
    {
        ArkPrivateAssetValue next = NormalizeArkPrivateAsset(new ArkPrivateAssetValue
        {
            Enabled = request.Enabled,
            Region = request.Region,
            ProjectName = request.ProjectName,
            AccessKeyID = request.AccessKeyID,
            AccessKeySecret = request.AccessKeySecret,
        });
        if (next.AccessKeySecret.Length == 0 && next.AccessKeyID == current.AccessKeyID)
        {
            next.AccessKeySecret = current.AccessKeySecret;
        }
        if (!next.Enabled)
        {
            return next;
        }
        if (next.Region.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写方舟 Region");
        }
        if (next.ProjectName.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写方舟 ProjectName");
        }
        if (next.AccessKeyID.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写方舟素材库 AccessKey");
        }
        if (next.AccessKeySecret.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写方舟素材库 SecretKey");
        }
        return next;
    }

    private static ArkPrivateAssetValue NormalizeArkPrivateAsset(ArkPrivateAssetValue value) => new()
    {
        Enabled = value.Enabled,
        Region = value.Region.Trim(),
        ProjectName = value.ProjectName.Trim(),
        AccessKeyID = value.AccessKeyID.Trim(),
        AccessKeySecret = value.AccessKeySecret.Trim(),
        DefaultGroupID = value.DefaultGroupID.Trim(),
    };

    private static PublicArkPrivateAssetSettingDto PublicArkPrivateAsset(
        SystemSetting? setting, ArkPrivateAssetValue value) => new()
        {
            Enabled = value.Enabled,
            Region = value.Region,
            ProjectName = value.ProjectName,
            AccessKeyID = value.AccessKeyID,
            HasAccessKeySecret = value.AccessKeySecret.Length > 0,
            UpdatedBy = setting?.UpdatedBy ?? "",
            CreatedAt = setting?.CreatedAt ?? default,
            UpdatedAt = setting?.UpdatedAt ?? default,
        };

    /// <summary>方舟素材库持久化值。对应 Go: <c>arkPrivateAssetSettingValue</c>。</summary>
    private sealed class ArkPrivateAssetValue
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("region")]
        public string Region { get; set; } = "";

        [JsonPropertyName("projectName")]
        public string ProjectName { get; set; } = "";

        [JsonPropertyName("accessKeyId")]
        public string AccessKeyID { get; set; } = "";

        [JsonPropertyName("accessKeySecret")]
        public string AccessKeySecret { get; set; } = "";

        [JsonPropertyName("defaultGroupId")]
        public string DefaultGroupID { get; set; } = "";
    }

    /// <summary>设置读写的 JSON 配置（字段名由 JsonPropertyName 显式声明）。</summary>
    private static readonly JsonSerializerOptions CanvasJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
