using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace OpenAICanvas.Auth;

/// <summary>
/// SMTP 邮件发送实现。对应 Go: <c>auth.sendSMTPMail</c>。
/// </summary>
/// <remarks>
/// 加密语义与 Go 一致："tls" 为 465 式隐式 TLS；"starttls" 必须升级加密，
/// 服务器不支持 STARTTLS 时报错（Go 的 <c>client.StartTLS</c> 同样失败）；其余按明文。
/// 相比 Go 的手写报文，这里补了 RFC 5322 的 Date / Message-ID 头，降低被判垃圾邮件的概率。
/// </remarks>
public sealed class SmtpMailSender : IMailSender
{
    // Go 的拨号超时是 12s，这里作用于连接建立阶段。
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);

    public async Task SendAsync(
        EmailSettingValue setting,
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        SecureSocketOptions options = setting.Encryption switch
        {
            "tls" => SecureSocketOptions.SslOnConnect,
            "starttls" => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.None,
        };

        using SmtpClient client = new();
        using CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(ConnectTimeout);

        await client.ConnectAsync(setting.Host, setting.Port, options, connectTimeout.Token).ConfigureAwait(false);
        try
        {
            // 与 Go 一致：留空用户名表示不认证（常见于内网中继）。
            if (setting.Username.Length > 0)
            {
                await client.AuthenticateAsync(setting.Username, setting.Password, cancellationToken)
                    .ConfigureAwait(false);
            }

            MimeMessage message = new()
            {
                Date = DateTimeOffset.UtcNow,
                Subject = subject,
                Body = new TextPart("plain") { Text = body },
            };
            message.From.Add(new MailboxAddress(setting.FromName, setting.FromEmail));
            message.To.Add(MailboxAddress.Parse(recipient));
            string domain = SenderDomain(setting.FromEmail);
            if (domain.Length > 0)
            {
                message.MessageId = MimeUtils.GenerateMessageId(domain);
            }

            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // QUIT 失败不影响发送结果；令牌不复用外层的取消状态。
            try
            {
                await client.DisconnectAsync(quit: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 连接已经被服务器关闭等情况直接忽略。
            }
        }
    }

    private static string SenderDomain(string fromEmail)
    {
        int at = fromEmail.LastIndexOf('@');
        return at >= 0 ? fromEmail[(at + 1)..] : string.Empty;
    }
}
