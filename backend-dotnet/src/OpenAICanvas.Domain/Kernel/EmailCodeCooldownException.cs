namespace OpenAICanvas.Domain.Kernel;

/// <summary>
/// 邮箱验证码冷却中。Handler 层据此返回 429 + <c>Retry-After</c> 头 + <c>code=42901</c>。
/// </summary>
/// <remarks>对应 Go: internal/auth/email_cooldown.go</remarks>
public sealed class EmailCodeCooldownException : Exception
{
    /// <summary>还需要等待的秒数，直接写入 Retry-After 响应头。</summary>
    public int Seconds { get; }

    public EmailCodeCooldownException(int seconds)
        : base($"验证码已发送，请查看邮箱；{seconds} 秒后可以重新获取")
    {
        Seconds = seconds;
    }
}
