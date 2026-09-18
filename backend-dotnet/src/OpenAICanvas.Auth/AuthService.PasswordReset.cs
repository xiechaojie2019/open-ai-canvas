using System.Security.Cryptography;
using System.Text;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Auth;

/// <summary>口令重置请求。对应 Go: <c>auth.PasswordResetRequest</c>。</summary>
public sealed class PasswordResetRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("emailCode")]
    public string EmailCode { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>认证服务的口令重置部分。对应 Go: <c>internal/auth/password_reset.go</c>。</summary>
public sealed partial class AuthService
{
    /// <summary>口令重置验证码有效期 10 分钟。对应 Go: <c>passwordResetCodeTTL</c>。</summary>
    public static readonly TimeSpan PasswordResetCodeTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 发送口令重置验证码。
    /// </summary>
    /// <remarks>
    /// 与注册验证码的关键差异：<b>无论邮箱是否存在都返回成功</b>，
    /// 避免通过该接口探测账号是否注册。对应 Go: <c>SendPasswordResetEmailCode</c>。
    /// </remarks>
    public async Task SendPasswordResetEmailCodeAsync(
        string rawEmail,
        CancellationToken cancellationToken = default)
    {
        string email = NormalizeEmail(rawEmail);
        ValidateEmail(email);

        (_, EmailSettingValue setting) = await ReadEmailSettingAsync(cancellationToken).ConfigureAwait(false);
        if (!setting.Enabled || setting.Host.Length == 0 || setting.Port < 1 || setting.FromEmail.Length == 0)
        {
            throw AppError.Forbidden("管理员尚未启用密码找回，请联系管理员");
        }

        // 账号不存在、已禁用或没有口令时静默返回，不暴露账号状态。
        User? user = await _repository.UserByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (user is null || user.Status != UserStatus.UserStatusActive || user.PasswordHash.Trim().Length == 0)
        {
            return;
        }

        // 同一邮箱 1 分钟内不重复发送（静默跳过，不报冷却错误）。
        EmailVerificationCode? latest = await _repository.LatestEmailVerificationCodeAsync(
            email, PasswordResetEmailPurpose, cancellationToken).ConfigureAwait(false);

        if (latest is not null && DateTime.UtcNow - latest.CreatedAt < EmailCodeCooldown)
        {
            return;
        }

        string code = IdGenerator.RandomNumericCode(6);
        DateTime now = DateTime.UtcNow;
        string recordId = IdGenerator.NewId();

        await _repository.CreateAsync(new EmailVerificationCode
        {
            ID = recordId,
            Email = email,
            CodeHash = EmailVerificationCodeHash(PasswordResetEmailPurpose, email, code),
            Purpose = PasswordResetEmailPurpose,
            ExpiresAt = now.Add(PasswordResetCodeTtl),
            CreatedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        EmailSettingValue sender = ResolveEmailSender(setting);
        try
        {
            await _mailSender.SendAsync(
                sender, email, sender.FromName + "密码重置验证码",
                PasswordResetEmailBody(code), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 发送失败回删记录；对外仍返回成功，避免通过错误差异探测账号存在性。
            await _repository.DeleteEmailVerificationCodeAsync(recordId, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await _repository.DeleteExpiredEmailVerificationCodesAsync(now.AddHours(-24), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 与 Go 一致：清理失败不影响本次结果。
        }
    }

    /// <summary>
    /// 重置口令。对应 Go: <c>ResetPassword</c>。
    /// </summary>
    /// <remarks>
    /// 所有校验失败都返回同一文案"验证码无效或已过期"，不区分"账号不存在"、
    /// "验证码过期"与"验证码错误"，避免信息泄漏。
    /// </remarks>
    public async Task ResetPasswordAsync(
        PasswordResetRequest request,
        CancellationToken cancellationToken = default)
    {
        string email = NormalizeEmail(request.Email);
        ValidateEmail(email);
        ValidatePassword(request.Password);

        string code = request.EmailCode.Trim();
        if (code.Length != 6)
        {
            throw InvalidPasswordResetCode();
        }

        User? user = await _repository.UserByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (user is null || user.Status != UserStatus.UserStatusActive || user.PasswordHash.Trim().Length == 0)
        {
            throw InvalidPasswordResetCode();
        }

        EmailVerificationCode? record = await _repository.LatestEmailVerificationCodeAsync(
            email, PasswordResetEmailPurpose, cancellationToken).ConfigureAwait(false);

        if (record is null || DateTime.UtcNow > record.ExpiresAt)
        {
            throw InvalidPasswordResetCode();
        }

        string expectedHash = EmailVerificationCodeHash(PasswordResetEmailPurpose, email, code);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedHash), Encoding.UTF8.GetBytes(record.CodeHash)))
        {
            throw InvalidPasswordResetCode();
        }

        string passwordHash = HashPassword(request.Password);

        try
        {
            await _repository.ResetUserPasswordWithEmailVerificationAsync(
                user.ID, email, PasswordResetEmailPurpose, record.ID, passwordHash, DateTime.UtcNow,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AppError)
        {
            // 仓储层在验证码被并发消费时抛错，对外统一为"验证码无效"。
            throw InvalidPasswordResetCode();
        }
    }

    /// <summary>口令重置邮件正文。对应 Go: <c>passwordResetEmailBody</c>。</summary>
    public string PasswordResetEmailBody(string code) =>
        "你正在重置" + _host.BrandName() + "账号密码。\n\n验证码：" + code
        + "\n\n验证码 10 分钟内有效。若非本人操作，请忽略本邮件，并确保邮箱账号安全。";

    /// <summary>掩码邮箱，用于日志。对应 Go: <c>maskedEmail</c>。</summary>
    public static string MaskedEmail(string email)
    {
        int at = email.IndexOf('@');
        if (at <= 0)
        {
            return "***";
        }

        string local = email[..at];
        Rune first = local.EnumerateRunes().First();
        return first + "***" + email[at..];
    }

    private static AppError InvalidPasswordResetCode() => AppError.BadAuthRequest("验证码无效或已过期");
}
