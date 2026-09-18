using System.Security.Cryptography;

namespace OpenAICanvas.Domain.Kernel;

/// <summary>
/// ID 与随机令牌生成。对应 Go: <c>internal/kernel/util.go</c> 与 <c>internal/auth/auth.go</c>。
/// </summary>
public static class IdGenerator
{
    /// <summary>
    /// 16 字节随机数的十六进制（32 个字符）。对应 Go: <c>kernel.NewID()</c>。
    /// </summary>
    /// <remarks>与 Go 一致：随机源失败时回落到时间戳字符串，保证不返回空 ID。</remarks>
    public static string NewId()
    {
        Span<byte> buffer = stackalloc byte[16];
        try
        {
            RandomNumberGenerator.Fill(buffer);
        }
        catch (CryptographicException)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        }

        return Convert.ToHexString(buffer).ToLowerInvariant();
    }

    /// <summary>
    /// 32 字节随机令牌的十六进制（64 个字符）。对应 Go: <c>auth.RandomToken()</c>。
    /// </summary>
    public static string RandomToken()
    {
        Span<byte> buffer = stackalloc byte[32];
        try
        {
            RandomNumberGenerator.Fill(buffer);
            return Convert.ToHexString(buffer).ToLowerInvariant();
        }
        catch (CryptographicException)
        {
            return NewId() + NewId();
        }
    }

    /// <summary>
    /// 指定长度的纯数字验证码。对应 Go: <c>auth.randomNumericCode</c>。
    /// </summary>
    public static string RandomNumericCode(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        System.Text.StringBuilder builder = new(length);
        for (int index = 0; index < length; index++)
        {
            builder.Append((char)('0' + RandomNumberGenerator.GetInt32(10)));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 令牌哈希：SHA-256 十六进制。对应 Go: <c>auth.HashToken</c>。
    /// </summary>
    public static string HashToken(string token)
    {
        byte[] digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
