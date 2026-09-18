using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Auth;

/// <summary>邮件发送配置。对应 Go: <c>auth.EmailSettingValue</c>。</summary>
public sealed class EmailSettingValue
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("encryption")]
    public string Encryption { get; set; } = string.Empty;

    [JsonPropertyName("fromEmail")]
    public string FromEmail { get; set; } = string.Empty;

    [JsonPropertyName("fromName")]
    public string FromName { get; set; } = string.Empty;

    [JsonPropertyName("registrationAllowedDomains")]
    public List<string> RegistrationAllowedDomains { get; set; } = [];
}

/// <summary>注册开关配置值。对应 Go: <c>auth.registrationSettingValue</c>。</summary>
public sealed class RegistrationSettingValue
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

/// <summary>认证服务的设置读取与邮箱验证码部分。</summary>
public sealed partial class AuthService
{
    // ------------------------------------------------------------ 注册开关

    /// <summary>对应 Go: <c>Service.RegistrationEnabled</c>。</summary>
    public async Task<bool> RegistrationEnabledAsync(CancellationToken cancellationToken = default)
    {
        (_, RegistrationSettingValue value) = await ReadRegistrationSettingAsync(cancellationToken).ConfigureAwait(false);
        return value.Enabled;
    }

    /// <summary>
    /// 读取注册开关。没有设置记录时回落到环境变量，与 Go 一致。
    /// </summary>
    private async Task<(SystemSetting? Setting, RegistrationSettingValue Value)> ReadRegistrationSettingAsync(
        CancellationToken cancellationToken)
    {
        SystemSetting? setting = await _repository.SystemSettingAsync(
            RegistrationSettingKey, cancellationToken).ConfigureAwait(false);

        if (setting is null)
        {
            return (null, new RegistrationSettingValue { Enabled = RegistrationEnabledFromEnvironment() });
        }

        if (string.IsNullOrWhiteSpace(setting.ValueJSON))
        {
            throw new InvalidOperationException("用户注册配置格式无效");
        }

        try
        {
            RegistrationSettingValue? value = JsonSerializer.Deserialize<RegistrationSettingValue>(
                setting.ValueJSON, SettingsJsonOptions);
            if (value is null)
            {
                throw new InvalidOperationException("用户注册配置格式无效");
            }

            return (setting, value);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("用户注册配置格式无效");
        }
    }

    /// <summary>对应 Go: <c>registrationEnabledFromEnvironment</c>（读 CANVAS_REGISTRATION_ENABLED）。</summary>
    private static bool RegistrationEnabledFromEnvironment()
    {
        string value = (Environment.GetEnvironmentVariable("CANVAS_REGISTRATION_ENABLED") ?? string.Empty)
            .Trim().ToLowerInvariant();
        return value is "1" or "true" or "yes";
    }

    // ------------------------------------------------------------ 邮件配置

    /// <summary>
    /// 邮件功能是否可用：启用且主机、端口、发件人都已配置。
    /// 对应 Go: <c>Service.EmailEnabled</c>。
    /// </summary>
    public async Task<bool> EmailEnabledAsync(CancellationToken cancellationToken = default)
    {
        (_, EmailSettingValue value) = await ReadEmailSettingAsync(cancellationToken).ConfigureAwait(false);
        return value.Enabled && value.Host.Length > 0 && value.Port > 0 && value.FromEmail.Length > 0;
    }

    /// <summary>读取邮件配置。没有记录时返回归一化后的默认值。</summary>
    private async Task<(SystemSetting? Setting, EmailSettingValue Value)> ReadEmailSettingAsync(
        CancellationToken cancellationToken)
    {
        SystemSetting? setting = await _repository.SystemSettingAsync(
            EmailSettingKey, cancellationToken).ConfigureAwait(false);

        if (setting is null)
        {
            // 与 Go 一致：无记录时回落归一化默认值（含默认注册域名白名单）。
            return (null, NormalizeEmailSetting(new EmailSettingValue()));
        }

        try
        {
            EmailSettingValue? value = JsonSerializer.Deserialize<EmailSettingValue>(
                setting.ValueJSON, SettingsJsonOptions);
            if (value is null)
            {
                throw new InvalidOperationException("邮件配置格式无效");
            }

            return (setting, value);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("邮件配置格式无效");
        }
    }

    // ------------------------------------------------------------ 邮箱验证码

    /// <summary>
    /// 校验注册验证码。对应 Go: <c>Service.VerifyRegistrationEmailCode</c>。
    /// 返回命中的记录，供注册时在同一事务内消费。
    /// </summary>
    public async Task<EmailVerificationCode> VerifyRegistrationEmailCodeAsync(
        string email,
        string rawCode,
        CancellationToken cancellationToken = default)
    {
        if (!await EmailEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            throw AppError.Forbidden("平台尚未启用注册邮件，请联系管理员");
        }

        string code = rawCode.Trim();
        if (code.Length != 6)
        {
            throw AppError.BadAuthRequest("请输入 6 位邮箱验证码");
        }

        EmailVerificationCode? record = await _repository.LatestEmailVerificationCodeAsync(
            email, RegistrationEmailPurpose, cancellationToken).ConfigureAwait(false);

        if (record is null)
        {
            throw AppError.BadAuthRequest("请先获取邮箱验证码");
        }

        if (DateTime.UtcNow > record.ExpiresAt)
        {
            throw AppError.BadAuthRequest("邮箱验证码已过期，请重新获取");
        }

        string expected = EmailVerificationCodeHash(RegistrationEmailPurpose, email, code);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(record.CodeHash)))
        {
            throw AppError.BadAuthRequest("邮箱验证码不正确");
        }

        return record;
    }

    /// <summary>
    /// 验证码 HMAC-SHA256。对应 Go: <c>Service.emailVerificationCodeHash</c>。
    /// 密钥来自宿主设置；未配置时用空密钥（与 Go 的 nopHost 返回 nil 等价）。
    /// </summary>
    public string EmailVerificationCodeHash(string purpose, string email, string code)
    {
        byte[] key = _host.SettingsEncryptionKey() ?? [];
        string payload = purpose.Trim() + ":" + NormalizeEmail(email) + ":" + code.Trim();

        using HMACSHA256 mac = new(key);
        byte[] digest = mac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>生成并落库一条注册验证码，返回明文验证码供发送。对应 Go 的发送流程。</summary>
    public async Task<string> IssueRegistrationEmailCodeAsync(
        string rawEmail,
        CancellationToken cancellationToken = default)
    {
        string email = NormalizeEmail(rawEmail);
        ValidateEmail(email);

        long count = await _repository.UserCountAsync(cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
            throw AppError.BadAuthRequest("首个管理员账号不需要邮箱验证码");
        }

        if (!await RegistrationEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            throw AppError.Forbidden("管理员未开放新用户注册");
        }

        if (await _repository.UserByEmailAsync(email, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw AppError.BadAuthRequest("邮箱已被注册");
        }

        (_, EmailSettingValue setting) = await ReadEmailSettingAsync(cancellationToken).ConfigureAwait(false);
        ValidateRegistrationEmailDomain(email, setting.RegistrationAllowedDomains);

        // 顺序与 Go 一致：先判邮件是否启用，再做冷却检查，最后才落库并发送。
        if (!setting.Enabled)
        {
            throw AppError.Forbidden("平台尚未启用注册邮件，请联系管理员");
        }

        // 同一邮箱 1 分钟内不允许重复发送；抛出带 Retry-After 的冷却错误。
        EmailVerificationCode? latest = await _repository.LatestEmailVerificationCodeAsync(
            email, RegistrationEmailPurpose, cancellationToken).ConfigureAwait(false);

        if (latest is not null)
        {
            TimeSpan elapsed = DateTime.UtcNow - latest.CreatedAt;
            if (elapsed < EmailCodeCooldown)
            {
                int seconds = Math.Max(1, (int)Math.Ceiling((EmailCodeCooldown - elapsed).TotalSeconds));
                throw new EmailCodeCooldownException(seconds);
            }
        }

        string code = IdGenerator.RandomNumericCode(6);
        DateTime now = DateTime.UtcNow;
        string recordId = IdGenerator.NewId();

        await _repository.CreateAsync(new EmailVerificationCode
        {
            ID = recordId,
            Email = email,
            CodeHash = EmailVerificationCodeHash(RegistrationEmailPurpose, email, code),
            Purpose = RegistrationEmailPurpose,
            ExpiresAt = now.Add(RegistrationCodeTtl),
            CreatedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        // 先落库再发送：发送失败必须回删记录，避免留下永远收不到的验证码。
        EmailSettingValue sender = ResolveEmailSender(setting);
        try
        {
            await _mailSender.SendAsync(
                sender, email, sender.FromName + "注册验证码", RegistrationEmailBody(code), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await _repository.DeleteEmailVerificationCodeAsync(recordId, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"发送注册邮件失败：{error.Message}", error);
        }

        // 清理 24 小时前的过期记录；失败只记日志，不影响本次发送结果。
        try
        {
            await _repository.DeleteExpiredEmailVerificationCodesAsync(now.AddHours(-24), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 与 Go 一致：清理失败仅记录，不改变返回值。
        }

        return code;
    }

    /// <summary>发件人显示名回落。对应 Go: <c>resolveEmailSender</c>。</summary>
    private EmailSettingValue ResolveEmailSender(EmailSettingValue value)
    {
        if (value.FromName.Length == 0 || value.FromName == NullAuthHost.DefaultBrandName)
        {
            value.FromName = _host.BrandName();
        }

        return value;
    }

    /// <summary>注册邮件正文。对应 Go: <c>registrationEmailBody</c>。</summary>
    public string RegistrationEmailBody(string code) =>
        "你正在注册" + _host.BrandName() + "。\n\n验证码：" + code + "\n\n验证码 10 分钟内有效。若非本人操作，请忽略本邮件。";

    // ------------------------------------------------------------ 域名白名单

    /// <summary>校验邮箱域名是否在管理员设置的白名单内。对应 Go: <c>validateRegistrationEmailDomain</c>。</summary>
    public static void ValidateRegistrationEmailDomain(string email, IReadOnlyList<string> allowedDomains)
    {
        string[] parts = email.Split('@');
        if (parts.Length != 2)
        {
            throw AppError.BadAuthRequest("邮箱格式不正确");
        }

        if (allowedDomains.Count == 0)
        {
            return;
        }

        string domain = parts[1].ToLowerInvariant();
        foreach (string allowed in allowedDomains)
        {
            if (string.Equals(domain, allowed, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw AppError.BadAuthRequest("该邮箱域名不在管理员设置的白名单内");
    }

    private async Task ValidateRegistrationEmailDomainAsync(string email, CancellationToken cancellationToken)
    {
        (_, EmailSettingValue setting) = await ReadEmailSettingAsync(cancellationToken).ConfigureAwait(false);
        ValidateRegistrationEmailDomain(email, setting.RegistrationAllowedDomains);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
