using System.Security.Cryptography;
using System.Text;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;

namespace PuddingSsh.Validation;

/// <summary>信任来源；两种来源**互斥**，不存在「known_hosts 读不到就退回 pin」的自动降级（设计 §6.3）。</summary>
public enum SshTrustSource
{
    KnownHosts,
    Pin,
}

/// <summary>
/// 冻结的主机信任快照（设计 §5/§6.3）。引用方在**连接前**把它解析并冻结，之后不可变；
/// <see cref="CreateVerifier"/> 绑定具体目标后才交给传输层，因此验证器只需回答「出示的密钥是否被信任」。
/// <para>
/// 不提供：首次信任、自动写入 known_hosts、通配/否定模式、主机证书/CA。
/// </para>
/// </summary>
public sealed class SshTrustSnapshot
{
    private readonly IReadOnlyList<SshKnownHostsEntry> _entries;
    private readonly string? _pinAlgorithm;
    private readonly string? _pinSha256;

    private SshTrustSnapshot(
        SshTrustSource source,
        IReadOnlyList<SshKnownHostsEntry> entries,
        string? pinAlgorithm,
        string? pinSha256)
    {
        Source = source;
        _entries = entries;
        _pinAlgorithm = pinAlgorithm;
        _pinSha256 = pinSha256;
    }

    public SshTrustSource Source { get; }

    /// <summary>有效信任集合里的记录条数（哈希/明文都算；撤销记录也算「已知」）。</summary>
    public int EntryCount => Source == SshTrustSource.KnownHosts ? _entries.Count : 1;

    /// <summary>
    /// 从 known_hosts **内容**构建。<paramref name="content"/> 由引用方按冻结的绝对路径读取，
    /// 组件不搜索其他身份目录。文件里出现无法安全解释的记录 ⇒ <c>ssh.known_hosts_unsupported</c>。
    /// </summary>
    public static SshTrustSnapshot FromKnownHosts(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var parsed = SshKnownHostsParser.Parse(content);
        if (!parsed.IsUsable)
        {
            throw new SshComponentException(
                SshErrorCodes.KnownHostsUnsupported,
                "known_hosts 含无法安全解释的记录：" + parsed.UnsupportedReason);
        }

        return new SshTrustSnapshot(SshTrustSource.KnownHosts, parsed.Entries, null, null);
    }

    /// <summary>显式 pin：算法 + 用户事先核对的 <c>SHA256:</c> 指纹。</summary>
    public static SshTrustSnapshot FromPin(string keyAlgorithm, string sha256Fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyAlgorithm);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256Fingerprint);

        var normalized = NormalizeSha256(sha256Fingerprint);
        if (normalized.Length == 0)
        {
            throw new SshComponentException(
                SshErrorCodes.ConfigInvalid,
                "pin 指纹为空：应为 SHA256:<base64>。");
        }

        return new SshTrustSnapshot(SshTrustSource.Pin, [], keyAlgorithm, normalized);
    }

    /// <summary>绑定目标（主机 + 端口）后生成验证器；host token 语义见 <see cref="SshHostTokens"/>。</summary>
    public ISshHostKeyVerifier CreateVerifier(string host, int port)
    {
        var hostToken = SshHostTokens.ForTarget(host, port);
        return Source switch
        {
            SshTrustSource.KnownHosts => new KnownHostsHostKeyVerifier(_entries, hostToken),
            _ => new PinHostKeyVerifier(_pinAlgorithm!, _pinSha256!, hostToken),
        };
    }

    /// <summary>OpenSSH 同级指纹：<c>SHA256:base64(sha256(blob))</c>，去掉尾部填充。</summary>
    public static string ComputeSha256Fingerprint(ReadOnlySpan<byte> keyBlob)
    {
        var digest = SHA256.HashData(keyBlob);
        return "SHA256:" + Convert.ToBase64String(digest).TrimEnd('=');
    }

    /// <summary>去掉 <c>SHA256:</c> 前缀与尾部 <c>=</c> 填充，便于比较库/OpenSSH/配置三种写法。</summary>
    public static string NormalizeSha256(string fingerprint)
    {
        var value = fingerprint.Trim();
        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["SHA256:".Length..];
        }

        return value.TrimEnd('=');
    }

    /// <summary>脱敏描述，用于诊断；不含路径、密钥内容或哈希明细。</summary>
    public string Describe() => Source == SshTrustSource.KnownHosts
        ? $"known_hosts(records={_entries.Count})"
        : $"pin(algorithm={_pinAlgorithm})";
}

/// <summary>known_hosts 语义的验证器（单目标冻结）。</summary>
internal sealed class KnownHostsHostKeyVerifier(
    IReadOnlyList<SshKnownHostsEntry> entries,
    string hostToken) : ISshHostKeyVerifier
{
    public SshHostKeyDecision Verify(SshHostKeyEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var hostMatches = entries.Where(e => MatchesHost(e, hostToken)).ToArray();
        if (hostMatches.Length == 0)
        {
            return SshHostKeyDecision.Reject(
                SshErrorCodes.HostKeyUntrusted,
                $"known_hosts 中没有 {hostToken} 的记录；请由用户核对指纹后显式登记（组件不自动信任新主机）。");
        }

        // 撤销按**密钥**生效（与 OpenSSH 一致）：出示的密钥命中 @revoked 记录即拒绝，
        // 撤销记录本身也是「该主机已知」的证据。
        if (hostMatches.Any(e => e.IsRevoked && KeyMatches(e, evidence)))
        {
            return SshHostKeyDecision.Reject(
                SshErrorCodes.HostKeyRevoked,
                $"{hostToken} 的该公钥已被 known_hosts 标记为 @revoked。");
        }

        if (hostMatches.Any(e => !e.IsRevoked && KeyMatches(e, evidence)))
        {
            return SshHostKeyDecision.Trust();
        }

        return SshHostKeyDecision.Reject(
            SshErrorCodes.HostKeyMismatch,
            $"{hostToken} 出示的公钥与 known_hosts 记录不匹配（可能主机密钥已更换）；请用户核对后更新记录。");
    }

    private static bool MatchesHost(SshKnownHostsEntry entry, string hostToken)
    {
        if (!entry.IsHashedHost)
        {
            return entry.HostPatterns.Contains(hostToken, StringComparer.Ordinal);
        }

        var computed = SshKnownHostsParser.ComputeHashedHostToken(hostToken, entry.HashedHostSalt!);
        return string.Equals(
            computed.TrimEnd('='),
            entry.HashedHostDigest!.TrimEnd('='),
            StringComparison.Ordinal);
    }

    private static bool KeyMatches(SshKnownHostsEntry entry, SshHostKeyEvidence evidence) =>
        string.Equals(entry.KeyAlgorithm, evidence.HostKeyAlgorithm, StringComparison.Ordinal)
        && entry.KeyBlob.AsSpan().SequenceEqual(evidence.HostKeyBlob.Span);
}

/// <summary>显式 pin 语义的验证器（单目标冻结）：指纹由**出示的 blob** 现算，不看调用方给的字符串。</summary>
internal sealed class PinHostKeyVerifier(
    string expectedAlgorithm,
    string expectedSha256,
    string hostToken) : ISshHostKeyVerifier
{
    public SshHostKeyDecision Verify(SshHostKeyEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (!string.Equals(evidence.HostKeyAlgorithm, expectedAlgorithm, StringComparison.Ordinal))
        {
            return SshHostKeyDecision.Reject(
                SshErrorCodes.HostKeyMismatch,
                $"{hostToken} 出示的算法 {evidence.HostKeyAlgorithm} 与 pin 的 {expectedAlgorithm} 不一致。");
        }

        var actual = SshTrustSnapshot.NormalizeSha256(SshTrustSnapshot.ComputeSha256Fingerprint(evidence.HostKeyBlob.Span));
        if (!string.Equals(actual, SshTrustSnapshot.NormalizeSha256(expectedSha256), StringComparison.Ordinal))
        {
            return SshHostKeyDecision.Reject(
                SshErrorCodes.HostKeyMismatch,
                $"{hostToken} 出示的密钥指纹与 pin 不一致（实际 SHA256:{actual}）。");
        }

        return SshHostKeyDecision.Trust();
    }
}
