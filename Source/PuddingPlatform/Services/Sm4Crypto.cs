using System.Security.Cryptography;
using System.Text;
using github.hyfree.GM.SM4;

namespace PuddingPlatform.Services;

/// <summary>
/// SM4-CBC 加密实现。
/// 使用 github.hyfree.GM 的 <see cref="SM4Utils"/> 进行 CBC 模式加解密。
/// </summary>
public static class Sm4Crypto
{
    private const int Sm4KeySizeBytes = 16;
    private const int IvSizeBytes = 16;

    /// <summary>
    /// 使用 SM4-CBC 加密明文，并返回 Base64 编码结果。
    /// 格式：IV(16字节) || Ciphertext。
    /// </summary>
    /// <param name="key">主密钥（至少16字节，实际取前16字节作为 SM4 密钥）。</param>
    /// <param name="plainText">待加密明文。</param>
    /// <returns>Base64 编码密文。</returns>
    public static string Encrypt(byte[] key, string plainText)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(plainText);

        if (key.Length < Sm4KeySizeBytes)
            throw new InvalidOperationException($"SM4 密钥长度不足，至少需要 {Sm4KeySizeBytes} 字节。当前长度={key.Length}。");

        var sm4Key = key[..Sm4KeySizeBytes];
        var iv = RandomNumberGenerator.GetBytes(IvSizeBytes);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);

        var sm4 = new SM4Utils
        {
            secretKey = sm4Key,
            iv = iv,
        };

        var cipherBytes = sm4.Encrypt_CBC(plainBytes);

        var payload = new byte[IvSizeBytes + cipherBytes.Length];
        Buffer.BlockCopy(iv, 0, payload, 0, IvSizeBytes);
        Buffer.BlockCopy(cipherBytes, 0, payload, IvSizeBytes, cipherBytes.Length);

        return Convert.ToBase64String(payload);
    }

    /// <summary>
    /// 使用 SM4-CBC 解密 Base64 密文。
    /// 输入格式要求：IV(16字节) || Ciphertext。
    /// </summary>
    /// <param name="key">主密钥（至少16字节，实际取前16字节作为 SM4 密钥）。</param>
    /// <param name="encryptedBase64">Base64 编码密文。</param>
    /// <returns>UTF-8 解码后的明文。</returns>
    public static string Decrypt(byte[] key, string encryptedBase64)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (string.IsNullOrWhiteSpace(encryptedBase64))
            throw new ArgumentException("SM4 密文字段不能为空。", nameof(encryptedBase64));

        if (key.Length < Sm4KeySizeBytes)
            throw new InvalidOperationException($"SM4 密钥长度不足，至少需要 {Sm4KeySizeBytes} 字节。当前长度={key.Length}。");

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(encryptedBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("SM4 密文格式非法（非 Base64）。", ex);
        }

        if (payload.Length <= IvSizeBytes)
            throw new InvalidOperationException("SM4 密文格式非法（长度不足）。");

        var iv = payload[..IvSizeBytes];
        var cipherBytes = payload[IvSizeBytes..];
        var sm4Key = key[..Sm4KeySizeBytes];

        try
        {
            var sm4 = new SM4Utils
            {
                secretKey = sm4Key,
                iv = iv,
            };

            var plainBytes = sm4.Decrypt_CBC(cipherBytes);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("SM4 密钥解密失败，请检查主密钥是否正确。", ex);
        }
    }
}
