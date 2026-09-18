namespace OpenAICanvas.Auth;

/// <summary>
/// 邮件发送。对应 Go: <c>auth.Host</c> 注入的 <c>mailSender</c> 与 <c>sendSMTPMail</c>。
/// </summary>
/// <remarks>
/// SMTP 实现属于后续模块；当前先固定接口，让认证链路的行为（含"发送失败回删验证码"）
/// 可以完整实现与验证。
/// </remarks>
public interface IMailSender
{
    Task SendAsync(
        EmailSettingValue setting,
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 未配置邮件发送器。被调用时明确失败，避免"以为发出去了"的静默成功。
/// </summary>
/// <remarks>
/// 默认配置下不会走到这里——<c>IssueRegistrationEmailCodeAsync</c> 会先因
/// <c>setting.Enabled == false</c> 返回 403。
/// </remarks>
public sealed class UnconfiguredMailSender : IMailSender
{
    public Task SendAsync(
        EmailSettingValue setting,
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("邮件发送器尚未接线（SMTP 模块未实现）");
}
