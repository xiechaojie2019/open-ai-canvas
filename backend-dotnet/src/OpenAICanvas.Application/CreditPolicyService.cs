using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>
/// 积分策略。对应 Go: <c>app.CreditPolicy</c>。
/// </summary>
public sealed class CreditPolicy
{
    [JsonPropertyName("signupBonusMicrocredits")]
    public long SignupBonusMicrocredits { get; set; }

    [JsonPropertyName("checkinBonusMicrocredits")]
    public long CheckinBonusMicrocredits { get; set; }

    [JsonPropertyName("defaultMultiplierBasisPoints")]
    public long DefaultMultiplierBPS { get; set; }

    [JsonPropertyName("modelMultiplierBasisPoints")]
    public Dictionary<string, long> ModelMultiplierBPS { get; set; } = [];
}

/// <summary>
/// 公开积分策略。对应 Go: <c>app.PublicCreditPolicy</c>（struct，字段顺序即输出顺序）。
/// </summary>
public sealed class PublicCreditPolicy
{
    [JsonPropertyName("signupBonusMicrocredits")]
    public long SignupBonusMicrocredits { get; init; }

    [JsonPropertyName("checkinBonusMicrocredits")]
    public long CheckinBonusMicrocredits { get; init; }

    [JsonPropertyName("checkedInToday")]
    public bool CheckedInToday { get; init; }
}

/// <summary>
/// 积分策略的读取与公开投影。对应 Go: <c>internal/app/credit_policy.go</c> 的读取部分。
/// </summary>
public sealed class CreditPolicyService
{
    /// <summary>积分换算基数。对应 Go: <c>app.CreditScale</c>。</summary>
    public const long CreditScale = 1_000_000;

    private const string SettingKey = "credit_policy";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Repository _repository;

    public CreditPolicyService(Repository repository) => _repository = repository;

    /// <summary>默认策略。对应 Go: <c>defaultCreditPolicy</c>。</summary>
    public static CreditPolicy Default() => new()
    {
        SignupBonusMicrocredits = 100 * CreditScale,
        CheckinBonusMicrocredits = 10 * CreditScale,
        DefaultMultiplierBPS = 10_000,
        ModelMultiplierBPS = [],
    };

    /// <summary>读取当前策略。没有设置记录时返回默认值。</summary>
    public async Task<CreditPolicy> GetAsync(CancellationToken cancellationToken = default)
    {
        SystemSetting? setting = await _repository.SystemSettingAsync(SettingKey, cancellationToken)
            .ConfigureAwait(false);

        if (setting is null)
        {
            return Default();
        }

        try
        {
            return JsonSerializer.Deserialize<CreditPolicy>(setting.ValueJSON, JsonOptions) ?? Default();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("积分策略配置格式无效");
        }
    }

    /// <summary>管理端读取积分策略。对应 Go: <c>AdminCreditPolicy</c>。</summary>
    public async Task<CreditPolicy> AdminAsync(User actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        return await GetAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>管理端更新积分策略（含审计）。对应 Go: <c>UpdateCreditPolicy</c>。</summary>
    public async Task<CreditPolicy> UpdateAsync(
        User actor, CreditPolicy policy, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        policy.ModelMultiplierBPS ??= [];
        Validate(policy);

        string encoded = JsonSerializer.Serialize(policy);
        SystemSetting? current = await _repository.SystemSettingAsync(SettingKey, cancellationToken)
            .ConfigureAwait(false);
        var setting = new SystemSetting
        {
            Key = SettingKey,
            ValueJSON = encoded,
            UpdatedBy = actor.ID,
        };
        if (current is not null)
        {
            setting.CreatedAt = current.CreatedAt;
        }
        await _repository.SaveSystemSettingAsync(setting, cancellationToken).ConfigureAwait(false);
        await _repository.AppendAdminAuditAsync(new AdminAuditEvent
        {
            ID = IdGenerator.NewId(),
            ActorUserID = actor.ID,
            Action = "credit_policy.update",
            TargetType = "system_setting",
            TargetID = SettingKey,
            Summary = "更新积分策略",
            MetadataJSON = encoded,
            CreatedAt = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
        return policy;
    }

    /// <summary>对应 Go: <c>validateCreditPolicy</c>。</summary>
    private static void Validate(CreditPolicy policy)
    {
        if (policy.SignupBonusMicrocredits < 0 || policy.CheckinBonusMicrocredits < 0)
        {
            throw AppError.BadAuthRequest("注册和签到奖励不能小于 0");
        }
        if (policy.SignupBonusMicrocredits > 1_000_000 * CreditScale ||
            policy.CheckinBonusMicrocredits > 100_000 * CreditScale)
        {
            throw AppError.BadAuthRequest("积分奖励超出允许范围");
        }
        if (policy.DefaultMultiplierBPS <= 0 || policy.DefaultMultiplierBPS > 1_000_000)
        {
            throw AppError.BadAuthRequest("默认模型倍率必须在 0.0001-100 之间");
        }
        foreach ((string modelKey, long multiplier) in policy.ModelMultiplierBPS)
        {
            if (modelKey.Trim().Length == 0 || multiplier <= 0 || multiplier > 1_000_000)
            {
                throw AppError.BadAuthRequest("模型倍率配置无效");
            }
        }
    }

    /// <summary>
    /// 公开积分策略。对应 Go: <c>publicCreditPolicy</c>。
    /// </summary>
    /// <remarks>
    /// <c>checkedInToday</c> 通过账本引用键 <c>checkin:&lt;userID&gt;:&lt;UTC 日期&gt;</c> 是否存在来判断。
    /// </remarks>
    public async Task<PublicCreditPolicy> PublicAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        CreditPolicy policy = await GetAsync(cancellationToken).ConfigureAwait(false);

        string reference = $"checkin:{userId}:{DateTime.UtcNow:yyyy-MM-dd}";
        bool checkedIn = await _repository.CreditLedgerReferenceExistsAsync(reference, cancellationToken)
            .ConfigureAwait(false);

        return new PublicCreditPolicy
        {
            SignupBonusMicrocredits = policy.SignupBonusMicrocredits,
            CheckinBonusMicrocredits = policy.CheckinBonusMicrocredits,
            CheckedInToday = checkedIn,
        };
    }
}
