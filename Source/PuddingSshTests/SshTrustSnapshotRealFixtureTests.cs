using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Validation;

namespace PuddingSshTests;

/// <summary>
/// known_hosts 与 pin 验证：**真实 OpenSSH 生成的 fixture**（A2 门禁「以真实记录验证，不靠自己生成样例自证」）。
/// </summary>
[TestClass]
public sealed class SshTrustSnapshotRealFixtureTests
{
    [TestMethod]
    public void PlainFixture_AllFourTokens_AreTrusted()
    {
        var snapshot = SshTrustSnapshot.FromKnownHosts(SshFixtures.Read("known_hosts.plain"));
        var (algorithm, blob) = SshFixtures.Ed25519Key();

        foreach (var (host, port, token) in SshFixtures.Tokens)
        {
            var decision = snapshot.CreateVerifier(host, port)
                .Verify(new SshHostKeyEvidence(algorithm, SshTrustSnapshot.ComputeSha256Fingerprint(blob), blob));

            Assert.IsTrue(decision.Trusted, $"{token} 应当被信任，实际 {decision.ErrorCode}: {decision.Diagnostic}");
        }
    }

    [TestMethod]
    public void HashedFixture_AllFourTokens_AreTrusted()
    {
        // 哈希记录由 `ssh-keygen -H` 产出：这条断言等价于「我们的 HMAC-SHA1 host token 与 OpenSSH 一致」。
        var snapshot = SshTrustSnapshot.FromKnownHosts(SshFixtures.Read("known_hosts.hashed"));
        var (algorithm, blob) = SshFixtures.Ed25519Key();

        foreach (var (host, port, token) in SshFixtures.Tokens)
        {
            var decision = snapshot.CreateVerifier(host, port)
                .Verify(new SshHostKeyEvidence(algorithm, SshTrustSnapshot.ComputeSha256Fingerprint(blob), blob));

            Assert.IsTrue(decision.Trusted, $"{token}（哈希记录）应当被信任，实际 {decision.ErrorCode}");
        }
    }

    [TestMethod]
    public void RsaRecord_ForSameHost_IsAlsoTrusted()
    {
        var snapshot = SshTrustSnapshot.FromKnownHosts(SshFixtures.Read("known_hosts.plain"));
        var (algorithm, blob) = SshFixtures.RsaKey();

        var decision = snapshot.CreateVerifier("127.0.0.1", 22)
            .Verify(new SshHostKeyEvidence(algorithm, SshTrustSnapshot.ComputeSha256Fingerprint(blob), blob));

        Assert.IsTrue(decision.Trusted, $"同一主机的另一把已登记密钥也应被信任：{decision.ErrorCode}");
    }

    [TestMethod]
    public void OurSha256Fingerprint_EqualsOpenSshOwn()
    {
        // 不拿自己的输出自证：期望值来自 `ssh-keygen -lf -E sha256`（夹具生成时记录）。
        var (_, ed25519) = SshFixtures.Ed25519Key();
        var (_, rsa) = SshFixtures.RsaKey();

        Assert.AreEqual(
            SshTrustSnapshot.NormalizeSha256(SshFixtures.OpenSshFingerprint("ed25519")),
            SshTrustSnapshot.NormalizeSha256(SshTrustSnapshot.ComputeSha256Fingerprint(ed25519)),
            "ed25519 主机密钥指纹必须与 OpenSSH 一致");

        Assert.AreEqual(
            SshTrustSnapshot.NormalizeSha256(SshFixtures.OpenSshFingerprint("rsa")),
            SshTrustSnapshot.NormalizeSha256(SshTrustSnapshot.ComputeSha256Fingerprint(rsa)),
            "rsa 主机密钥指纹必须与 OpenSSH 一致");
    }

    [TestMethod]
    public void OpenSshItselfFindsEveryFixtureToken()
    {
        // 夹具证据的自检：如果这份 fixture 被改坏，指纹文件里的 ssh-keygen -F 结论就不再成立。
        foreach (var (_, _, token) in SshFixtures.Tokens)
        {
            StringAssert.Contains(SshFixtures.FingerprintEvidence(token), "present");
            StringAssert.Contains(SshFixtures.FingerprintEvidence("match-hashed " + token), "present");
        }
    }

    [TestMethod]
    public void RevokedSameKey_IsRejectedAsRevoked()
    {
        var snapshot = SshTrustSnapshot.FromKnownHosts(SshFixtures.Read("known_hosts.revoked_same_key"));
        var (algorithm, blob) = SshFixtures.Ed25519Key();

        var decision = snapshot.CreateVerifier("127.0.0.1", 22)
            .Verify(new SshHostKeyEvidence(algorithm, SshTrustSnapshot.ComputeSha256Fingerprint(blob), blob));

        Assert.IsFalse(decision.Trusted);
        Assert.AreEqual(SshErrorCodes.HostKeyRevoked, decision.ErrorCode);
    }

    [TestMethod]
    public void RevokedOtherKey_StillTrustsTheRegisteredKey()
    {
        // 撤销按**密钥**生效（与 OpenSSH 一致）：被撤销的是另一把密钥时，已登记密钥仍可用，
        // 否则主机密钥轮换（旧 key 标 revoked + 新 key 登记）会被整台主机误杀。
        var snapshot = SshTrustSnapshot.FromKnownHosts(SshFixtures.Read("known_hosts.revoked_other_key"));
        var (algorithm, blob) = SshFixtures.Ed25519Key();

        var decision = snapshot.CreateVerifier("127.0.0.1", 22)
            .Verify(new SshHostKeyEvidence(algorithm, SshTrustSnapshot.ComputeSha256Fingerprint(blob), blob));

        Assert.IsTrue(decision.Trusted, decision.ErrorCode ?? "expected trust");
    }

    [TestMethod]
    public void RevokedHashedRecord_IsRejectedAsRevoked()
    {
        // 哈希文件 + 同一条哈希记录的 @revoked 变体：撤销必须压过哈希许可。
        var combined = SshFixtures.Read("known_hosts.hashed") + SshFixtures.Read("known_hosts.revoked_hashed");
        var snapshot = SshTrustSnapshot.FromKnownHosts(combined);
        var (algorithm, blob) = SshFixtures.Ed25519Key();

        var decision = snapshot.CreateVerifier("127.0.0.1", 22)
            .Verify(new SshHostKeyEvidence(algorithm, SshTrustSnapshot.ComputeSha256Fingerprint(blob), blob));

        Assert.IsFalse(decision.Trusted);
        Assert.AreEqual(SshErrorCodes.HostKeyRevoked, decision.ErrorCode);
    }

    [TestMethod]
    public void UnknownHost_IsUntrusted_AndKnownHostWithOtherKey_IsMismatch()
    {
        var snapshot = SshTrustSnapshot.FromKnownHosts(SshFixtures.Read("known_hosts.plain"));
        var (_, ed25519) = SshFixtures.Ed25519Key();

        var unknown = snapshot.CreateVerifier("192.0.2.9", 22)
            .Verify(new SshHostKeyEvidence("ssh-ed25519", SshTrustSnapshot.ComputeSha256Fingerprint(ed25519), ed25519));
        Assert.AreEqual(SshErrorCodes.HostKeyUntrusted, unknown.ErrorCode);

        // 已登记主机 + 算法相同但**另一把** ed25519 公钥 ⇒ mismatch（不是 untrusted）。
        var otherKey = new byte[51];
        var mismatch = snapshot.CreateVerifier("::1", 22122)
            .Verify(new SshHostKeyEvidence("ssh-ed25519", SshTrustSnapshot.ComputeSha256Fingerprint(otherKey), otherKey));
        Assert.AreEqual(SshErrorCodes.HostKeyMismatch, mismatch.ErrorCode);

        // 反向控制组：同一目标出示已登记的 ed25519 必须通过，证明上面的 mismatch 不是目标写错。
        var control = snapshot.CreateVerifier("::1", 22122)
            .Verify(new SshHostKeyEvidence("ssh-ed25519", SshTrustSnapshot.ComputeSha256Fingerprint(ed25519), ed25519));
        Assert.IsTrue(control.Trusted);
    }

    [TestMethod]
    public void UnsupportedFixture_FailsClosed()
    {
        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshTrustSnapshot.FromKnownHosts(SshFixtures.Read("known_hosts.unsupported")));
        Assert.AreEqual(SshErrorCodes.KnownHostsUnsupported, error.ErrorCode);
    }
}
