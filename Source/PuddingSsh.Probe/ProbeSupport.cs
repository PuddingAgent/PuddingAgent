using System.Security.Cryptography;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;

namespace PuddingSsh.Probe;

/// <summary>
/// 探针主机密钥验证器：<b>探针专用</b>，不是交付路径。
/// <list type="bullet">
///   <item><c>observe</c>：记录出示的指纹后**一律拒绝**，用于与 <c>ssh-keyscan</c> 交叉核对；</item>
///   <item>其余：只接受显式期望指纹，不匹配即 <c>ssh.host_key_mismatch</c>。</item>
/// </list>
/// 交付路径的 known_hosts / pin 验证器属于 A2；探针**不做**首次信任，也不写 known_hosts。
/// </summary>
internal sealed class ProbeHostKeyVerifier(string? expectedFingerprint, bool alwaysReject = false) : ISshHostKeyVerifier
{
    private readonly List<string> _observed = [];

    public IReadOnlyList<string> Observed => _observed;

    public SshHostKeyDecision Verify(SshHostKeyEvidence evidence)
    {
        var blobHash = Convert.ToHexString(SHA256.HashData(evidence.HostKeyBlob.Span));
        _observed.Add(
            $"alg={evidence.HostKeyAlgorithm} sha256={evidence.Sha256Fingerprint} blob_len={evidence.HostKeyBlob.Length} blob_sha256={blobHash}");

        if (alwaysReject)
        {
            return SshHostKeyDecision.Reject(SshErrorCodes.HostKeyUntrusted, "探针观测模式：一律拒绝");
        }

        if (string.IsNullOrEmpty(expectedFingerprint))
        {
            return SshHostKeyDecision.Reject(SshErrorCodes.HostKeyUntrusted, "探针未配置期望指纹");
        }

        return FingerprintEquals(expectedFingerprint, evidence.Sha256Fingerprint)
            ? SshHostKeyDecision.Trust()
            : SshHostKeyDecision.Reject(
                SshErrorCodes.HostKeyMismatch,
                $"期望 {Normalize(expectedFingerprint)}，实际 {Normalize(evidence.Sha256Fingerprint)}");
    }

    /// <summary>OpenSSH 与库都以「去掉 SHA256: 前缀与尾部 = 填充」的形式给出指纹。</summary>
    public static bool FingerprintEquals(string expected, string actual) =>
        string.Equals(Normalize(expected), Normalize(actual), StringComparison.Ordinal);

    public static string Normalize(string fingerprint)
    {
        var value = fingerprint.Trim();
        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["SHA256:".Length..];
        }

        return value.TrimEnd('=');
    }

    /// <summary>把期望指纹的中间一个字符改掉，得到一个必然不匹配的诱饵（用于 hostkey/reject 断言）。</summary>
    public static string Mutable(string fingerprint)
    {
        var normalized = Normalize(fingerprint);
        var index = normalized.Length / 2;
        var chars = normalized.ToCharArray();
        chars[index] = chars[index] == 'A' ? 'B' : 'A';
        return "SHA256:" + new string(chars);
    }
}

/// <summary>
/// 探针身份提供者：直接从显式路径读字节。交付路径的目录解析与 ACL/owner 校验属于 A2/Runtime；
/// 探针不搜寻其他身份、不跨作用域回退。
/// </summary>
internal sealed class ProbeIdentityProvider(string path) : ISshIdentityProvider
{
    public ValueTask<SshIdentityMaterial> GetIdentityAsync(
        SshIdentityRequest request,
        CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(path);
        var material = new SshIdentityMaterial(
            Path.GetDirectoryName(full) ?? string.Empty,
            Path.GetFileName(full),
            File.ReadAllBytes(full));
        return ValueTask.FromResult(material);
    }
}
