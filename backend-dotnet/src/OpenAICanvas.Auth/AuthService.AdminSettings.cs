using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Auth;

/// <summary>注册开关请求。对应 Go: <c>auth.RegistrationSettingRequest</c>。</summary>
public sealed class RegistrationSettingRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

/// <summary>公开注册设置。对应 Go: <c>auth.PublicRegistrationSetting</c>（字段顺序即输出顺序）。</summary>
public sealed class PublicRegistrationSetting
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("updatedBy")]
    public string UpdatedBy { get; init; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }
}

/// <summary>邮件配置请求。对应 Go: <c>auth.EmailSettingRequest</c>。</summary>
public sealed class EmailSettingRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("encryption")]
    public string Encryption { get; set; } = "";

    [JsonPropertyName("fromEmail")]
    public string FromEmail { get; set; } = "";

    [JsonPropertyName("fromName")]
    public string FromName { get; set; } = "";

    [JsonPropertyName("registrationAllowedDomains")]
    public List<string>? RegistrationAllowedDomains { get; set; }
}

/// <summary>公开邮件设置。对应 Go: <c>auth.PublicEmailSetting</c>（字段顺序即输出顺序）。</summary>
public sealed class PublicEmailSetting
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("host")]
    public string Host { get; init; } = "";

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("username")]
    public string Username { get; init; } = "";

    [JsonPropertyName("encryption")]
    public string Encryption { get; init; } = "";

    [JsonPropertyName("fromEmail")]
    public string FromEmail { get; init; } = "";

    [JsonPropertyName("fromName")]
    public string FromName { get; init; } = "";

    [JsonPropertyName("fromNameInherited")]
    public bool FromNameInherited { get; init; }

    [JsonPropertyName("hasPassword")]
    public bool HasPassword { get; init; }

    [JsonPropertyName("registrationAllowedDomains")]
    public List<string> RegistrationAllowedDomains { get; init; } = [];

    [JsonPropertyName("updatedBy")]
    public string UpdatedBy { get; init; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// 认证设置的读写（管理端）。对应 Go: <c>auth/registration.go</c> 与 <c>auth/email.go</c> 的 admin 部分。
/// </summary>
public sealed partial class AuthService
{
    /// <summary>默认注册邮箱域名。对应 Go: <c>defaultRegistrationEmailDomains</c>。</summary>
    private static readonly string[] DefaultRegistrationEmailDomains =
        ["gmail.com", "163.com", "126.com", "qq.com", "outlook.com", "hotmail.com", "icloud.com", "yahoo.com", "foxmail.com"];

    // ------------------------------------------------------------ 注册开关（管理端）

    /// <summary>对应 Go: <c>AdminRegistrationSetting</c>。</summary>
    public async Task<PublicRegistrationSetting> AdminRegistrationSettingAsync(
        User actor, CancellationToken cancellationToken = default)
    {
        _host.RequireAdmin(actor);
        (SystemSetting? setting, RegistrationSettingValue value) =
            await ReadRegistrationSettingAsync(cancellationToken).ConfigureAwait(false);
        return PublicRegistrationSetting(setting, value);
    }

    /// <summary>对应 Go: <c>UpdateRegistrationSetting</c>。</summary>
    public async Task<PublicRegistrationSetting> UpdateRegistrationSettingAsync(
        User actor, RegistrationSettingRequest request, CancellationToken cancellationToken = default)
    {
        _host.RequireAdmin(actor);
        (SystemSetting? current, _) = await ReadRegistrationSettingAsync(cancellationToken).ConfigureAwait(false);

        string encoded = JsonSerializer.Serialize(new RegistrationSettingValue { Enabled = request.Enabled });
        var setting = new SystemSetting
        {
            Key = RegistrationSettingKey,
            ValueJSON = encoded,
            UpdatedBy = actor.ID,
        };
        if (current is not null)
        {
            setting.CreatedAt = current.CreatedAt;
        }
        await _repository.SaveSystemSettingAsync(setting, cancellationToken).ConfigureAwait(false);
        return PublicRegistrationSetting(setting, new RegistrationSettingValue { Enabled = request.Enabled });
    }

    /// <summary>对应 Go: <c>publicRegistrationSetting</c>。</summary>
    private static PublicRegistrationSetting PublicRegistrationSetting(
        SystemSetting? setting, RegistrationSettingValue value) => new()
    {
        Enabled = value.Enabled,
        UpdatedBy = setting?.UpdatedBy ?? "",
        CreatedAt = setting?.CreatedAt ?? default,
        UpdatedAt = setting?.UpdatedAt ?? default,
    };

    // ------------------------------------------------------------ 邮件配置（管理端）

    /// <summary>对应 Go: <c>AdminEmailSetting</c>。</summary>
    public async Task<PublicEmailSetting> AdminEmailSettingAsync(
        User actor, CancellationToken cancellationToken = default)
    {
        _host.RequireAdmin(actor);
        (SystemSetting? setting, EmailSettingValue value) =
            await ReadEmailSettingAsync(cancellationToken).ConfigureAwait(false);
        return PublicEmailSetting(setting, value);
    }

    /// <summary>对应 Go: <c>UpdateEmailSetting</c>。</summary>
    public async Task<PublicEmailSetting> UpdateEmailSettingAsync(
        User actor, EmailSettingRequest request, CancellationToken cancellationToken = default)
    {
        _host.RequireAdmin(actor);
        (SystemSetting? currentSetting, EmailSettingValue current) =
            await ReadEmailSettingAsync(cancellationToken).ConfigureAwait(false);

        List<string> allowedDomains = request.RegistrationAllowedDomains ?? current.RegistrationAllowedDomains;
        EmailSettingValue next = NormalizeEmailSetting(new EmailSettingValue
        {
            Enabled = request.Enabled,
            Host = request.Host,
            Port = request.Port,
            Username = request.Username,
            Password = request.Password,
            Encryption = request.Encryption,
            FromEmail = request.FromEmail,
            FromName = request.FromName,
            RegistrationAllowedDomains = allowedDomains,
        });
        if (next.Password.Length == 0)
        {
            next.Password = current.Password;
        }
        ValidateEmailSetting(next);

        EmailSettingValue stored = new()
        {
            Enabled = next.Enabled,
            Host = next.Host,
            Port = next.Port,
            Username = next.Username,
            Password = EncryptSecret(next.Password),
            Encryption = next.Encryption,
            FromEmail = next.FromEmail,
            FromName = next.FromName,
            RegistrationAllowedDomains = next.RegistrationAllowedDomains,
        };
        string encoded = JsonSerializer.Serialize(stored);
        var setting = new SystemSetting
        {
            Key = EmailSettingKey,
            ValueJSON = encoded,
            UpdatedBy = actor.ID,
        };
        if (currentSetting is not null)
        {
            setting.CreatedAt = currentSetting.CreatedAt;
        }
        await _repository.SaveSystemSettingAsync(setting, cancellationToken).ConfigureAwait(false);
        return PublicEmailSetting(setting, next);
    }

    /// <summary>对应 Go: <c>publicEmailSetting</c>。</summary>
    private PublicEmailSetting PublicEmailSetting(SystemSetting? setting, EmailSettingValue value)
    {
        bool inherited = value.FromName.Length == 0 || value.FromName == NullAuthHost.DefaultBrandName;
        EmailSettingValue resolved = ResolveEmailSender(value);
        return new PublicEmailSetting
        {
            Enabled = resolved.Enabled,
            Host = resolved.Host,
            Port = resolved.Port,
            Username = resolved.Username,
            Encryption = resolved.Encryption,
            FromEmail = resolved.FromEmail,
            FromName = resolved.FromName,
            FromNameInherited = inherited,
            HasPassword = resolved.Password.Length > 0,
            RegistrationAllowedDomains = resolved.RegistrationAllowedDomains,
            UpdatedBy = setting?.UpdatedBy ?? "",
            CreatedAt = setting?.CreatedAt ?? default,
            UpdatedAt = setting?.UpdatedAt ?? default,
        };
    }

    /// <summary>对应 Go: <c>normalizeEmailSetting</c>。</summary>
    private static EmailSettingValue NormalizeEmailSetting(EmailSettingValue value)
    {
        value.Host = value.Host.Trim();
        value.Username = value.Username.Trim();
        value.Password = value.Password.Trim();
        value.FromEmail = NormalizeEmail(value.FromEmail);
        value.FromName = value.FromName.Trim();
        if (value.RegistrationAllowedDomains.Count == 0)
        {
            value.RegistrationAllowedDomains = [.. DefaultRegistrationEmailDomains];
        }
        value.RegistrationAllowedDomains = NormalizeEmailDomains(value.RegistrationAllowedDomains);
        if (value.Port == 0)
        {
            value.Port = 587;
        }
        value.Encryption = value.Encryption switch
        {
            "tls" or "none" => value.Encryption,
            _ => "starttls",
        };
        return value;
    }

    /// <summary>对应 Go: <c>validateEmailSetting</c>。</summary>
    private static void ValidateEmailSetting(EmailSettingValue value)
    {
        ValidateRegistrationEmailDomains(value.RegistrationAllowedDomains);
        if (!value.Enabled)
        {
            return;
        }
        if (value.Host.Length == 0 || value.Port is < 1 or > 65535 || value.FromEmail.Length == 0)
        {
            throw AppError.BadAuthRequest("启用邮件前请完整填写 SMTP 主机、端口和发件邮箱");
        }
        if (!IsValidEmail(value.FromEmail))
        {
            throw AppError.BadAuthRequest("发件邮箱格式不正确");
        }
        if (value.Username.Length > 0 && value.Password.Length == 0)
        {
            throw AppError.BadAuthRequest("SMTP 用户名已填写，请同时填写密码");
        }
        if (value.FromName.Contains('\r') || value.FromName.Contains('\n'))
        {
            throw AppError.BadAuthRequest("发件人名称不能包含换行");
        }
    }

    /// <summary>对应 Go: <c>validateRegistrationEmailDomains</c>。</summary>
    private static void ValidateRegistrationEmailDomains(List<string> domains)
    {
        if (domains.Count > 200)
        {
            throw AppError.BadAuthRequest("注册邮箱域名最多配置 200 个");
        }
        foreach (string domain in domains)
        {
            if (domain.Length > 120)
            {
                throw AppError.BadAuthRequest("注册邮箱域名过长");
            }
        }
    }

    /// <summary>对应 Go: <c>normalizeEmailDomains</c>。</summary>
    private static List<string> NormalizeEmailDomains(IEnumerable<string> values)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string raw in values)
        {
            string value = raw.Trim();
            if (value.StartsWith('@'))
            {
                value = value[1..];
            }
            value = value.Trim().ToLowerInvariant().TrimEnd('.');
            if (value.Length == 0 || !seen.Add(value))
            {
                continue;
            }
            result.Add(value);
        }
        return result;
    }

    /// <summary>邮箱格式校验。对应 Go: <c>auth.ValidateEmail</c>。</summary>
    private static bool IsValidEmail(string email)
    {
        int at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@') || at == email.Length - 1)
        {
            return false;
        }
        string domain = email[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.') &&
               !email.Contains(' ') && !email.Contains('\t');
    }
}