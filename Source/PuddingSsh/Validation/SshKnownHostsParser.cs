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

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = lines[i].TrimEnd('\r').Trim();
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
                        ? Unsupported("cert_authority", lineNumber)
                        : Unsupported("unknown_marker", lineNumber);
                    continue;
                }

                index = 1;
            }

            if (tokens.Length - index < 3)
            {
                unsupported ??= Unsupported("malformed_fields", lineNumber);
                continue;
            }

            var hostField = tokens[index];
            var keyAlgorithm = tokens[index + 1];
            var keyBase64 = tokens[index + 2];

            if (keyAlgorithm.StartsWith('@') || keyBase64.StartsWith('@'))
            {
                unsupported ??= Unsupported("malformed_fields", lineNumber);
                continue;
            }

            byte[] keyBlob;
            try
            {
                keyBlob = Convert.FromBase64String(keyBase64);
            }
            catch (FormatException)
            {
                unsupported ??= Unsupported("bad_base64", lineNumber);
                continue;
            }

            if (keyBlob.Length == 0)
            {
                unsupported ??= Unsupported("empty_key", lineNumber);
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
                    unsupported ??= Unsupported("hash_version", lineNumber);
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
                        unsupported ??= Unsupported("empty_pattern", lineNumber);
                        unsupportedPattern = true;
                        break;
                    }

                    if (pattern.StartsWith('!'))
                    {
                        unsupported ??= Unsupported("negation_pattern", lineNumber);
                        unsupportedPattern = true;
                        break;
                    }

                    if (pattern.Contains('*') || pattern.Contains('?'))
                    {
                        unsupported ??= Unsupported("wildcard_pattern", lineNumber);
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

    /// <summary>
    /// 稳定且**不含原始载荷**的不支持原因（ADR-096 D5）：只给原因码与行号，
    /// 不把 known_hosts 整行（可能含主机名/公钥/注释）放进公开诊断。
    /// </summary>
    private static string Unsupported(string code, int lineNumber) =>
        $"unsupported_known_hosts_record code={code} line={lineNumber}";

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
