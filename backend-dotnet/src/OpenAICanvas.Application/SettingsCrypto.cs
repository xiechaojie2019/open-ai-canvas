#nullable enable
using System.IO;
using System.Security.Cryptography;

namespace OpenAICanvas.Application;

/// <summary>
/// 设置密钥加密（AES-GCM）。与 Go 逐字节兼容：密钥是 <c>&lt;dataDir&gt;/.settings-key</c>
/// 的 32 字节原始内容（缺失时随机生成，0600 权限语义在 Windows 上由文件系统决定）；
/// 密文 = "enc:v1:" + Base64(RawStd)(nonce(12) + seal(nonce, plaintext))。
/// 对应 Go: <c>encryptSettingSecret / decryptSettingSecret / settingsEncryptionKey</c>。
/// </summary>
public static class SettingsCrypto
{
    public const string EncryptedPrefix = "enc:v1:";
    private const int NonceSize = 12;

    public static string EncryptSecret(string value, string dataDir)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        byte[] key = ObtainKey(dataDir);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] plaintext = System.Text.Encoding.UTF8.GetBytes(value);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using AesGcm gcm = new(key, tagSizeInBytes: 16);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag);
        byte[] payload = new byte[NonceSize + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, payload, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, NonceSize + ciphertext.Length, tag.Length);
        return EncryptedPrefix + Convert.ToBase64String(payload).TrimEnd('=');
    }

    public static string DecryptSecret(string value, string dataDir)
    {
        if (!value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return value;
        }
        string encoded = value[EncryptedPrefix.Length..];
        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(PadBase64(encoded));
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("OSS 密钥密文格式无效");
        }
        if (payload.Length < NonceSize + 16)
        {
            throw new InvalidOperationException("OSS 密钥密文长度无效");
        }
        byte[] key = ObtainKey(dataDir);
        byte[] nonce = payload[..NonceSize];
        byte[] ciphertext = payload[NonceSize..^16];
        byte[] tag = payload[^16..];
        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using AesGcm gcm = new(key, tagSizeInBytes: 16);
            gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("OSS 密钥解密失败，请检查存储加密密钥");
        }
        return System.Text.Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>读取或创建 32 字节密钥文件。对应 Go: <c>settingsEncryptionKey</c>。</summary>
    private static byte[] ObtainKey(string dataDir)
    {
        string path = Path.Combine(dataDir, ".settings-key");
        if (File.Exists(path))
        {
            byte[] existing = File.ReadAllBytes(path);
            if (existing.Length == 32)
            {
                return existing;
            }
            throw new InvalidOperationException("存储加密密钥长度无效");
        }
        Directory.CreateDirectory(dataDir);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(key);
        }
        catch (IOException)
        {
            // 并发创建时回落读取（对应 Go 的 O_EXCL 分支）。
            byte[] existing = File.ReadAllBytes(path);
            if (existing.Length != 32)
            {
                throw new InvalidOperationException("存储加密密钥长度无效");
            }
            return existing;
        }
        return key;
    }

    private static string PadBase64(string value) => (value.Length % 4) switch
    {
        2 => value + "==",
        3 => value + "=",
        _ => value,
    };
}
