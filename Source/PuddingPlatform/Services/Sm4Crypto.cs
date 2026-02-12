using System.Security.Cryptography;
using System.Text;

namespace PuddingPlatform.Services;

/// <summary>
/// AES-CBC 加密实现（开发阶段替代 SM4，生产环境应替换为 SM4 算法）。
/// 格式：IV(16字节) || Ciphertext。
/// </summary>
public static class Sm4Crypto
{
    private const int KeySizeBytes = 16;
    private const int IvSizeBytes = 16;

    /// <summary>
    /// 使用 AES-CBC 加密明文，并返回 Base64 编码结果。
    /// 格式：IV(16字节) || Ciphertext。
    /// </summary>
    public static string Encrypt(byte[] key, string plainText)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(plainText);

        if (key.Length < KeySizeBytes)
            throw new InvalidOperationException($"密钥长度不足，至少需要 {KeySizeBytes} 字节。当前长度={key.Length}。");

        var aesKey = key[..KeySizeBytes];
        var iv = RandomNumberGenerator.GetBytes(IvSizeBytes);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);

        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var payload = new byte[IvSizeBytes + cipherBytes.Length];
        Buffer.BlockCopy(iv, 0, payload, 0, IvSizeBytes);
        Buffer.BlockCopy(cipherBytes, 0, payload, IvSizeBytes, cipherBytes.Length);

        return Convert.ToBase64String(payload);
    }

    /// <summary>
    /// 使用 AES-CBC 解密 Base64 密文。
    /// </summary>
    public static string Decrypt(byte[] key, string encryptedBase64)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (string.IsNullOrWhiteSpace(encryptedBase64))
            throw new ArgumentException("密文字段不能为空。", nameof(encryptedBase64));

        if (key.Length < KeySizeBytes)
            throw new InvalidOperationException($"密钥长度不足，至少需要 {KeySizeBytes} 字节。当前长度={key.Length}。");

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(encryptedBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("密文格式非法（非 Base64）。", ex);
        }

        if (payload.Length <= IvSizeBytes)
            throw new InvalidOperationException("密文格式非法（长度不足）。");

        var iv = payload[..IvSizeBytes];
        var cipherBytes = payload[IvSizeBytes..];
        var aesKey = key[..KeySizeBytes];

        try
        {
            using var aes = Aes.Create();
            aes.Key = aesKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("密钥解密失败，请检查主密钥是否正确。", ex);
        }
    }
}
