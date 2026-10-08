namespace PuddingSsh.Validation;

/// <summary>
/// known_hosts 的**主机 token** 语义（设计 §6.3）：22 端口以裸主机名匹配，非默认端口用
/// <c>[host]:port</c>；IPv6 因为含冒号**始终**保留方括号（OpenSSH 的写法就是 <c>[::1]:22</c>）。
/// <para>
/// 只做字符串规范化：**不做** DNS 解析或地址/主机名互换，避免用某条 IP 记录扩大另一台主机的信任。
/// </para>
/// </summary>
public static class SshHostTokens
{
    public static string ForTarget(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        var normalized = host.Trim();
        if (normalized.Length > 1 && normalized[0] == '[' && normalized[^1] == ']')
        {
            normalized = normalized[1..^1];
        }

        var isIpv6Literal = normalized.Contains(':', StringComparison.Ordinal);
        return port == 22 && !isIpv6Literal ? normalized : $"[{normalized}]:{port}";
    }
}
