using System.Security.Cryptography;
using System.Text;

namespace Pudding.Contracts.Desktop;

/// <summary>
/// 稳定短哈希：跨进程、跨重启、跨版本都得到同一个值。
///
/// **不能用 <c>string.GetHashCode()</c>** —— 它按进程加盐，同一个输入在两个进程里结果不同，
/// 于是"内容有没有变"的判断会在重启后随机地变成"变了"。
/// </summary>
public static class StableHash
{
    /// <summary>SHA-256 截断成 <paramref name="length"/> 位小写十六进制。</summary>
    public static string Short(string value, int length = 8)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 64);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest)[..length].ToLowerInvariant();
    }
}
