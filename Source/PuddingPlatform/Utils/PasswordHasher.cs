using System.Security.Cryptography;

namespace PuddingPlatform.Utils;

/// <summary>
/// PBKDF2-SHA256 密码哈希工具。
/// 存储格式：base64(salt):base64(hash)
/// 使用 FixedTimeEquals 防止时序攻击。
/// </summary>
public static class PasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
    }

    public static bool Verify(string password, string storedHash)
    {
        var parts = storedHash.Split(':');
        if (parts.Length != 2) return false;
        try
        {
            byte[] salt = Convert.FromBase64String(parts[0]);
            byte[] expected = Convert.FromBase64String(parts[1]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
