using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Validation;

namespace PuddingSshTests;

/// <summary>host token 语义（设计 §6.3）：22 端口裸主机名，非默认端口 <c>[host]:port</c>，IPv6 始终带方括号。</summary>
[TestClass]
public sealed class SshHostTokensTests
{
    [TestMethod]
    [DataRow("127.0.0.1", 22, "127.0.0.1")]
    [DataRow("127.0.0.1", 22122, "[127.0.0.1]:22122")]
    [DataRow("db.internal", 22, "db.internal")]
    [DataRow("db.internal", 2222, "[db.internal]:2222")]
    [DataRow("::1", 22, "[::1]:22")]
    [DataRow("::1", 22122, "[::1]:22122")]
    [DataRow("2001:db8::1", 22, "[2001:db8::1]:22")]
    [DataRow("[::1]", 22, "[::1]:22")]
    public void ForTarget_UsesOpenSshTokenSyntax(string host, int port, string expected)
    {
        Assert.AreEqual(expected, SshHostTokens.ForTarget(host, port));
    }

    [TestMethod]
    public void ForTarget_RejectsEmptyHostAndBadPort()
    {
        Assert.ThrowsExactly<ArgumentException>(() => SshHostTokens.ForTarget("  ", 22));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SshHostTokens.ForTarget("h", 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SshHostTokens.ForTarget("h", 65536));
    }
}

/// <summary>known_hosts 解析的**形状**契约；OpenSSH 语义由真 fixture 测试覆盖。</summary>
[TestClass]
public sealed class SshKnownHostsParserTests
{
    /// <summary>形状测试用的合法 base64 公钥 blob（51 字节 = ed25519 线格式长度）。</summary>
    private static readonly string Key = Convert.ToBase64String(new byte[51]);

    [TestMethod]
    public void ParsesPlainCommaSeparatedAndHashedRecords()
    {
        var parsed = SshKnownHostsParser.Parse(
            "# comment\n" +
            "\n" +
            $"a.example.com,b.example.com ssh-ed25519 {Key}\n" +
            $"|1|c2FsdA==|aGFzaA== ssh-ed25519 {Key}\n" +
            $"c.example.com ssh-ed25519 {Key} trailing comment\n");

        Assert.IsTrue(parsed.IsUsable, parsed.UnsupportedReason);
        Assert.AreEqual(3, parsed.ContentLineCount);
        Assert.HasCount(3, parsed.Entries);

        CollectionAssert.AreEquivalent(
            new[] { "a.example.com", "b.example.com" },
            parsed.Entries[0].HostPatterns.ToArray());
        Assert.IsFalse(parsed.Entries[0].IsHashedHost);

        Assert.IsTrue(parsed.Entries[1].IsHashedHost);
        Assert.AreEqual("c2FsdA==", parsed.Entries[1].HashedHostSalt);
        Assert.AreEqual("aGFzaA==", parsed.Entries[1].HashedHostDigest);

        Assert.AreEqual("ssh-ed25519", parsed.Entries[2].KeyAlgorithm);
        Assert.IsNotEmpty(parsed.Entries[2].KeyBlob);
    }

    [TestMethod]
    public void ParsesRevokedMarker()
    {
        var parsed = SshKnownHostsParser.Parse($"@revoked r.example.com ssh-ed25519 {Key}\n");

        Assert.IsTrue(parsed.IsUsable, parsed.UnsupportedReason);
        Assert.HasCount(1, parsed.Entries);
        Assert.IsTrue(parsed.Entries[0].IsRevoked);
    }

    [TestMethod]
    [DataRow("*.example.com ssh-ed25519 {0}", "通配")]
    [DataRow("!bad.example.com ssh-ed25519 {0}", "否定")]
    [DataRow("@cert-authority ca.example.com ssh-ed25519 {0}", "cert-authority")]
    [DataRow("@unknown-marker h ssh-ed25519 {0}", "未知标记")]
    [DataRow("|2|salt|hash ssh-ed25519 {0}", "只支持 |1|salt|hash")]
    [DataRow("too-few-fields", "格式非法")]
    [DataRow("h ssh-ed25519 not-base64!!!", "base64")]
    public void UnsupportedOrMalformedContent_FailsClosed(string template, string expectedFragment)
    {
        var parsed = SshKnownHostsParser.Parse(template.Replace("{0}", Key, StringComparison.Ordinal) + "\n");

        Assert.IsFalse(parsed.IsUsable, "含无法安全解释的记录时 IsUsable 必须为 false");
        Assert.IsNotNull(parsed.UnsupportedReason);
        StringAssert.Contains(parsed.UnsupportedReason, expectedFragment);
        Assert.IsEmpty(parsed.Entries, "不支持的记录不得被当作有效记录放行");
    }

    [TestMethod]
    public void EmptyOrCommentOnlyFile_IsUsableButTrustsNothing()
    {
        var parsed = SshKnownHostsParser.Parse("# only comments\n\n");

        Assert.IsTrue(parsed.IsUsable, parsed.UnsupportedReason);
        Assert.IsEmpty(parsed.Entries);

        var snapshot = SshTrustSnapshot.FromKnownHosts("# only comments\n");
        var decision = snapshot.CreateVerifier("h", 22)
            .Verify(new SshHostKeyEvidence("ssh-ed25519", "SHA256:x", new byte[] { 1, 2, 3 }));
        Assert.AreEqual(SshErrorCodes.HostKeyUntrusted, decision.ErrorCode);
    }

    [TestMethod]
    public void CrlfAndTrailingWhitespaceAreTolerated()
    {
        var parsed = SshKnownHostsParser.Parse($"h.example.com ssh-ed25519 {Key}   \r\n");

        Assert.IsTrue(parsed.IsUsable, parsed.UnsupportedReason);
        Assert.HasCount(1, parsed.Entries);
        Assert.AreEqual("h.example.com", parsed.Entries[0].HostPatterns[0]);
    }
}

/// <summary>pin 语义：指纹由**出示的 blob** 现算，不看调用方给的字符串。</summary>
[TestClass]
public sealed class SshPinTrustTests
{
    [TestMethod]
    public void Pin_TrustsMatchingAlgorithmAndFingerprint()
    {
        var (algorithm, blob) = SshFixtures.Ed25519Key();
        var fingerprint = SshTrustSnapshot.ComputeSha256Fingerprint(blob);

        var snapshot = SshTrustSnapshot.FromPin(algorithm, fingerprint);
        var decision = snapshot.CreateVerifier("127.0.0.1", 22)
            .Verify(new SshHostKeyEvidence(algorithm, fingerprint, blob));

        Assert.IsTrue(decision.Trusted, decision.Diagnostic);
        Assert.AreEqual(SshTrustSource.Pin, snapshot.Source);
    }

    [TestMethod]
    public void Pin_AcceptsOpenSshFingerprintText()
    {
        // 用户通常直接粘贴 `ssh-keygen -lf` 的输出（带 SHA256: 与可能的 = 填充）。
        var (algorithm, blob) = SshFixtures.Ed25519Key();
        var openSsh = SshFixtures.OpenSshFingerprint("ed25519");

        var snapshot = SshTrustSnapshot.FromPin(algorithm, openSsh);
        var decision = snapshot.CreateVerifier("127.0.0.1", 22)
            .Verify(new SshHostKeyEvidence(algorithm, openSsh, blob));

        Assert.IsTrue(decision.Trusted, decision.Diagnostic);
    }

    [TestMethod]
    public void Pin_RejectsWrongFingerprintAndWrongAlgorithm()
    {
        var (algorithm, blob) = SshFixtures.Ed25519Key();
        var correct = SshTrustSnapshot.ComputeSha256Fingerprint(blob);
        var tampered = "SHA256:" + new string('A', correct.Length - "SHA256:".Length);

        var byFingerprint = SshTrustSnapshot.FromPin(algorithm, tampered)
            .CreateVerifier("127.0.0.1", 22)
            .Verify(new SshHostKeyEvidence(algorithm, correct, blob));
        Assert.IsFalse(byFingerprint.Trusted);
        Assert.AreEqual(SshErrorCodes.HostKeyMismatch, byFingerprint.ErrorCode);

        var (rsaAlgorithm, _) = SshFixtures.RsaKey();
        var byAlgorithm = SshTrustSnapshot.FromPin(rsaAlgorithm, correct)
            .CreateVerifier("127.0.0.1", 22)
            .Verify(new SshHostKeyEvidence(algorithm, correct, blob));
        Assert.IsFalse(byAlgorithm.Trusted);
        Assert.AreEqual(SshErrorCodes.HostKeyMismatch, byAlgorithm.ErrorCode);
    }

    [TestMethod]
    public void Pin_RejectsEmptyConfiguration()
    {
        Assert.ThrowsExactly<ArgumentException>(() => SshTrustSnapshot.FromPin("", "SHA256:x"));
        Assert.ThrowsExactly<ArgumentException>(() => SshTrustSnapshot.FromPin("ssh-ed25519", " "));
        var emptyFingerprint = Assert.ThrowsExactly<SshComponentException>(
            () => SshTrustSnapshot.FromPin("ssh-ed25519", "SHA256:"));
        Assert.AreEqual(SshErrorCodes.ConfigInvalid, emptyFingerprint.ErrorCode);
    }
}
