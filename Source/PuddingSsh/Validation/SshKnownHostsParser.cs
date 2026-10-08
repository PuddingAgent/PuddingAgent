using System.Security.Cryptography;
using System.Text;
using PuddingSsh.Diagnostics;

namespace PuddingSsh.Validation;

/// <summary>known_hosts 的一条有效记录（设计 §6.3）。公钥以**原始 blob 字节**保存，按字节比较。</summary>
public sealed record SshKnownHostsEntry
{
    public required string RawLine { get; init; }
    public required string KeyAlgorithm { get; init; }
    public required byte[] KeyBlob { get; init; }

    /// <summary>明文主机名列表（逗号分隔记录会展开）；哈希记录为空。</summary>
    public required IReadOnlyList<string> HostPatterns { get; init; }

    /// <summary>哈希记录：<c>|1|salt|hash</c> 的 salt（base64）。</summary>
    public string? HashedHostSalt { get; init; }

    /// <summary>哈希记录：<c>|1|salt|hash</c> 的 hash（base64）。</summary>
    public string? HashedHostDigest { get; init; }

    public bool IsHashedHost => HashedHostSalt is not null;

    public bool IsRevoked { get; init; }
}

/// <summary>
/// 解析结果。<see cref="UnsupportedReason"/> 非空表示文件里有**无法安全解释**的记录：
/// 此时 <see cref="IsUsable"/> 为 false，调用方必须 fail-closed（设计 §6.3 的
/// <c>ssh.known_hosts_unsupported</c>），不得「忽略看不懂的行然后照常信任」——
/// 忽略 <c>!</c> 否定模式会**扩大**信任，忽略通配/CA 行会误判主机是否已知。
/// </summary>
public sealed record SshKnownHostsParseResult(
    IReadOnlyList<SshKnownHostsEntry> Entries,
    string? UnsupportedReason,
    int ContentLineCount)
{
    public bool IsUsable => UnsupportedReason is null;
}

/// <summary>
/// known_hosts 解析器：支持明文精确名称、逗号分隔名称、<c>|1|salt|hash</c> 哈希主机，
/// 以及 <c>@revoked</c> 标记。通配/否定模式、<c>@cert-authority</c>、未知标记、非 v1 哈希与
/// 语法/公钥非法的行一律判为**不支持**（首期边界见设计 §6.3）。
/// </summary>
public static class SshKnownHostsParser
{
    public static SshKnownHostsParseResult Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var entries = new List<SshKnownHostsEntry>();
        string? unsupported = null;
        var lines = content.Split('\n');
        var contentLines = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            contentLines++;
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            var isRevoked = false;
            var index = 0;

            if (tokens[0].StartsWith('@'))
            {
                var marker = tokens[0][1..];
                if (marker == "revoked")
                {
                    isRevoked = true;
                }
                else
                {
                    // 未知标记与 @cert-authority 一律**整行丢弃**并判为不支持：
                    // 放行一条我们解释不了语义的信任记录，比拒绝更危险。
                    unsupported ??= marker == "cert-authority"
                        ? $"记录使用了 @{marker}（主机证书/CA 不在首期范围内）：{line}"
                        : $"记录使用了未知标记 @{marker}：{line}";
                    continue;
                }

                index = 1;
            }

            if (tokens.Length - index < 3)
            {
                unsupported ??= $"known_hosts 行格式非法（应为 <hosts> <keytype> <base64>）：{line}";
                continue;
            }

            var hostField = tokens[index];
            var keyAlgorithm = tokens[index + 1];
            var keyBase64 = tokens[index + 2];

            if (keyAlgorithm.StartsWith('@') || keyBase64.StartsWith('@'))
            {
                unsupported ??= $"known_hosts 行格式非法：{line}";
                continue;
            }

            byte[] keyBlob;
            try
            {
                keyBlob = Convert.FromBase64String(keyBase64);
            }
            catch (FormatException)
            {
                unsupported ??= $"known_hosts 公钥不是合法 base64：{line}";
                continue;
            }

            if (keyBlob.Length == 0)
            {
                unsupported ??= $"known_hosts 公钥为空：{line}";
                continue;
            }

            List<string> patterns = [];
            string? salt = null;
            string? digest = null;

            if (hostField.StartsWith('|'))
            {
                var parts = hostField.Split('|');
                // 期望 ["", "1", salt, hash]
                if (parts.Length != 4 || parts[1] != "1" || parts[2].Length == 0 || parts[3].Length == 0)
                {
                    unsupported ??= $"known_hosts 哈希主机记录不支持（只支持 |1|salt|hash）：{line}";
                    continue;
                }

                salt = parts[2];
                digest = parts[3];
            }
            else
            {
                var unsupportedPattern = false;
                foreach (var pattern in hostField.Split(','))
                {
                    if (pattern.Length == 0)
                    {
                        unsupported ??= $"known_hosts 主机字段含空项：{line}";
                        unsupportedPattern = true;
                        break;
                    }

                    if (pattern.StartsWith('!'))
                    {
                        unsupported ??= $"known_hosts 使用否定模式（无法安全解释）：{line}";
                        unsupportedPattern = true;
                        break;
                    }

                    if (pattern.Contains('*') || pattern.Contains('?'))
                    {
                        unsupported ??= $"known_hosts 使用通配模式（无法安全解释）：{line}";
                        unsupportedPattern = true;
                        break;
                    }

                    patterns.Add(pattern);
                }

                if (unsupportedPattern)
                {
                    continue;
                }
            }

            entries.Add(new SshKnownHostsEntry
            {
                RawLine = line,
                KeyAlgorithm = keyAlgorithm,
                KeyBlob = keyBlob,
                HostPatterns = patterns,
                HashedHostSalt = salt,
                HashedHostDigest = digest,
                IsRevoked = isRevoked,
            });
        }

        return new SshKnownHostsParseResult(entries, unsupported, contentLines);
    }

    /// <summary>OpenSSH 的 hashed-host token：<c>base64(HMAC-SHA1(key=salt, data=hostToken))</c>。</summary>
    public static string ComputeHashedHostToken(string hostToken, string base64Salt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Salt);

        var salt = Convert.FromBase64String(base64Salt);
        using var hmac = new HMACSHA1(salt);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(hostToken));
        return Convert.ToBase64String(digest);
    }
}
