using PuddingSsh.Validation;

namespace PuddingSshTests;

/// <summary>
/// 真实 OpenSSH fixture 的读取与解释（由 <c>TestScripts/ssh-probe/make-known-hosts-fixtures.sh</c> 生成）。
/// <para>
/// 关键点：断言用的**期望值来自 OpenSSH 自己**——指纹取自 <c>ssh-keygen -lf -E sha256</c>（写进
/// <c>fingerprints.txt</c>），哈希主机记录由 <c>ssh-keygen -H</c> 产出，且每个 token 都由
/// <c>ssh-keygen -F</c> 确认「OpenSSH 认为该主机在文件里」。这样测试不是拿我们自己的输出自证。
/// </para>
/// </summary>
internal static class SshFixtures
{
    /// <summary>fixture 覆盖的四个主机 token（含 22 与非 22 端口、IPv4 与 IPv6）。</summary>
    public static readonly (string Host, int Port, string Token)[] Tokens =
    [
        ("127.0.0.1", 22, "127.0.0.1"),
        ("127.0.0.1", 22122, "[127.0.0.1]:22122"),
        ("::1", 22, "[::1]:22"),
        ("::1", 22122, "[::1]:22122"),
    ];

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string PathOf(string name) => Path.Combine(Directory, name);

    public static string Read(string name)
    {
        var path = PathOf(name);
        Assert.IsTrue(File.Exists(path), $"fixture missing: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>取自 fixture 的 ed25519 主机公钥（真实 keyscan 记录里的 blob）。</summary>
    public static (string Algorithm, byte[] Blob) Ed25519Key(string knownHostsFile = "known_hosts.plain")
    {
        var parsed = SshKnownHostsParser.Parse(Read(knownHostsFile));
        var entry = parsed.Entries.First(e => string.Equals(e.KeyAlgorithm, "ssh-ed25519", StringComparison.Ordinal));
        return (entry.KeyAlgorithm, entry.KeyBlob);
    }

    /// <summary>取自 fixture 的 RSA 主机公钥（用于「同主机、不同密钥/算法」的取红断言）。</summary>
    public static (string Algorithm, byte[] Blob) RsaKey(string knownHostsFile = "known_hosts.plain")
    {
        var parsed = SshKnownHostsParser.Parse(Read(knownHostsFile));
        var entry = parsed.Entries.First(e => string.Equals(e.KeyAlgorithm, "ssh-rsa", StringComparison.Ordinal));
        return (entry.KeyAlgorithm, entry.KeyBlob);
    }

    /// <summary>OpenSSH 算出的指纹（<c>ssh-keygen -lf</c> 的原样值，含 <c>SHA256:</c> 前缀）。</summary>
    public static string OpenSshFingerprint(string keyName)
    {
        var line = Read("fingerprints.txt")
            .Split('\n')
            .First(l => l.Contains($"host_key_{keyName}", StringComparison.Ordinal));

        // 形如: # host_key_ed25519 256 SHA256:xxx comment (ED25519)
        var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .First(t => t.StartsWith("SHA256:", StringComparison.Ordinal));
        return token;
    }

    /// <summary>指纹文件里的 <c>ssh-keygen -F</c> 交叉核对结论（evidence），例如 <c>match [::1]:22 -&gt; present</c>。</summary>
    public static string FingerprintEvidence(string key)
    {
        var line = Read("fingerprints.txt")
            .Split('\n')
            .FirstOrDefault(l => l.Contains(key + " ->", StringComparison.Ordinal));
        Assert.IsNotNull(line, $"fingerprints.txt 缺少 {key} 的交叉核对行");
        return line;
    }
}
