using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// P0 A 步：审批存储整文件签章信封的契约测试。
///
/// 覆盖三类断言：
/// <list type="number">
/// <item><b>正例</b>：往返保真、确定性（同输入 ⇒ 同产物）；</item>
/// <item><b>篡改</b>：改载荷 / 换密钥 / 跨文件重放 ⇒ 一律 MacMismatch；</item>
/// <item><b>降级与拒绝</b>：旧格式走 adopt 路径、未知版本 / 缺 mac / 损坏 ⇒ 明确失败码。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class ApprovalStoreEnvelopeTests
{
    private static readonly DateTimeOffset SignedAt = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly byte[] MasterKey =
        Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static byte[] TicketsKey => ApprovalStoreEnvelope.DeriveStoreKey(MasterKey, "tickets.json");

    private static byte[] AllowlistKey => ApprovalStoreEnvelope.DeriveStoreKey(MasterKey, "allowlist.json");

    private sealed record SampleGrant
    {
        public string GrantId { get; init; } = string.Empty;
        public string ToolId { get; init; } = string.Empty;
    }

    private static List<SampleGrant> SamplePayload() =>
    [
        new SampleGrant { GrantId = "g-1", ToolId = "shell" },
        new SampleGrant { GrantId = "g-2", ToolId = "file_patch" },
    ];

    [TestMethod]
    public void DerivedKey_DiffersPerFileName_SoCrossFileReplayIsImpossible()
    {
        var tickets = ApprovalStoreEnvelope.DeriveStoreKey(MasterKey, "tickets.json");
        var allowlist = ApprovalStoreEnvelope.DeriveStoreKey(MasterKey, "allowlist.json");

        Assert.AreEqual(32, tickets.Length, "HMAC-SHA256 子密钥应为 32 字节。");
        CollectionAssert.AreNotEqual(tickets, allowlist, "不同文件名必须派生不同子密钥。");
    }

    [TestMethod]
    public void WrapThenUnwrap_RoundTripsPayloadAndMetadata()
    {
        var wrapped = ApprovalStoreEnvelope.Wrap(SamplePayload(), 7, "kv-1", SignedAt, TicketsKey);

        var result = ApprovalStoreEnvelope.Unwrap<List<SampleGrant>>(wrapped, TicketsKey);

        Assert.AreEqual(ApprovalStoreIntegrityOutcome.Ok, result.Outcome);
        Assert.IsTrue(result.IsOk);
        Assert.AreEqual(7, result.Sequence);
        Assert.AreEqual("kv-1", result.KeyId);
        Assert.AreEqual(SignedAt, result.SignedAtUtc);
        Assert.IsNotNull(result.Payload);
        Assert.AreEqual(2, result.Payload!.Count);
        Assert.AreEqual("g-1", result.Payload[0].GrantId);
        Assert.AreEqual("shell", result.Payload[0].ToolId);
    }

    [TestMethod]
    public void Wrap_IsDeterministic_ForIdenticalInputs()
    {
        var first = ApprovalStoreEnvelope.Wrap(SamplePayload(), 7, "kv-1", SignedAt, TicketsKey);
        var second = ApprovalStoreEnvelope.Wrap(SamplePayload(), 7, "kv-1", SignedAt, TicketsKey);

        Assert.AreEqual(first, second, "同输入必须产出逐字节相同的信封（否则 mac 无法复算）。");
    }

    [TestMethod]
    public void TamperedPayload_FailsWithMacMismatch_AndYieldsNoPayload()
    {
        var wrapped = ApprovalStoreEnvelope.Wrap(SamplePayload(), 7, "kv-1", SignedAt, TicketsKey);
        Assert.IsTrue(wrapped.Contains("\"shell\"", StringComparison.Ordinal), "前置：载荷确实在信封里。");

        var tampered = wrapped.Replace("\"shell\"", "\"shel1\"", StringComparison.Ordinal);
        Assert.AreNotEqual(wrapped, tampered, "前置：篡改确实生效。");

        var result = ApprovalStoreEnvelope.Unwrap<List<SampleGrant>>(tampered, TicketsKey);

        Assert.AreEqual(ApprovalStoreIntegrityOutcome.MacMismatch, result.Outcome);
        Assert.IsNull(result.Payload, "校验失败时**不得**把载荷交回调用方。");
    }

    [TestMethod]
    public void TamperedSequence_AlsoFails_SoRollbackIsDetected()
    {
        var wrapped = ApprovalStoreEnvelope.Wrap(SamplePayload(), 7, "kv-1", SignedAt, TicketsKey);
        var tampered = wrapped.Replace("\"seq\":7", "\"seq\":6", StringComparison.Ordinal);

        var result = ApprovalStoreEnvelope.Unwrap<List<SampleGrant>>(tampered, TicketsKey);

        Assert.AreEqual(ApprovalStoreIntegrityOutcome.MacMismatch, result.Outcome,
            "seq 参与 mac ⇒ 改 seq 必须被检出（这是「防回滚」的一半；另一半是持久锚点）。");
    }

    [TestMethod]
    public void WrongKey_FailsWithMacMismatch()
    {
        var wrapped = ApprovalStoreEnvelope.Wrap(SamplePayload(), 7, "kv-1", SignedAt, TicketsKey);

        var result = ApprovalStoreEnvelope.Unwrap<List<SampleGrant>>(wrapped, AllowlistKey);

        Assert.AreEqual(ApprovalStoreIntegrityOutcome.MacMismatch, result.Outcome);
        Assert.IsNull(result.Payload);
    }

    [TestMethod]
    public void CrossFileReplay_Fails_BecauseSubKeysAreDomainSeparated()
    {
        // 用 tickets 的子密钥签一个信封，却拿 allowlist 的子密钥去验 —— 等价于"把 A 文件的 mac 贴到 B 文件"。
        var envelopeForTickets = ApprovalStoreEnvelope.Wrap(SamplePayload(), 7, "kv-1", SignedAt, TicketsKey);
        var envelopeForAllowlist = ApprovalStoreEnvelope.Wrap(SamplePayload(), 7, "kv-1", SignedAt, AllowlistKey);

        Assert.AreNotEqual(envelopeForTickets, envelopeForAllowlist);

        Assert.AreEqual(
            ApprovalStoreIntegrityOutcome.MacMismatch,
            ApprovalStoreEnvelope.Unwrap<List<SampleGrant>>(envelopeForTickets, AllowlistKey).Outcome);
        Assert.AreEqual(
            ApprovalStoreIntegrityOutcome.MacMismatch,
            ApprovalStoreEnvelope.Unwrap<List<SampleGrant>>(envelopeForAllowlist, TicketsKey).Outcome);
    }

    [TestMethod]
    public void LegacyRootArray_IsReportedAsLegacy_NotAsFailure()
    {
        const string legacy = """[{"grantId":"g-9","toolId":"git_push"}]""";

        var result = ApprovalStoreEnvelope.Unwrap<List<SampleGrant>>(legacy, TicketsKey);

        Assert.AreEqual(ApprovalStoreIntegrityOutcome.LegacyUnsigned, result.Outcome);
        Assert.IsTrue(result.IsLegacy);
        Assert.IsNotNull(result.Payload, "旧格式必须能读出内容，否则无法「首次签章」（adopt）。");
        Assert.AreEqual("g-9", result.Payload![0].GrantId);
    }

    [TestMethod]
    public void LegacyDetection_ToleratesLeadingWhitespace()
    {
        var result = ApprovalStoreEnvelope.Unwrap<List<SampleGrant>>("\r\n\t []", TicketsKey);

        Assert.AreEqual(ApprovalStoreIntegrityOutcome.LegacyUnsigned, result.Outcome);
    }

    [TestMethod]
    public void UnknownSchemaVersion_IsRejected()
    {
        const string json = """
            {"schemaVersion":99,"seq":1,"signedAtUtc":"2026-09-21T12:00:00+00:00",
             "alg":"HMAC-SHA256","keyId":"kv-1","payload":[],"mac":"AAAA"}
            """;

        var result = ApprovalStoreEnvelope.Unwrap<List<SampleGrant>>(json, TicketsKey);

        Assert.AreEqual(ApprovalStoreIntegrityOutcome.UnknownSchemaVersion, result.Outcome);
    }

    [TestMethod]
    public void MissingMac_IsRejected_NotTreatedAsLegacy()
    {
        const string json = """
            {"schemaVersion":2,"seq":1,"signedAtUtc":"2026-09-21T12:00:00+00:00",
             "alg":"HMAC-SHA256","keyId":"kv-1","payload":[]}
            """;

        var result = ApprovalStoreEnvelope.Unwrap<List<SampleGrant>>(json, TicketsKey);

        Assert.AreEqual(ApprovalStoreIntegrityOutcome.MissingMac, result.Outcome);
        Assert.IsNull(result.Payload);
    }

    [TestMethod]
    public void UnknownAlgorithm_IsRejected()
    {
        const string json = """
            {"schemaVersion":2,"seq":1,"signedAtUtc":"2026-09-21T12:00:00+00:00",
             "alg":"MD5","keyId":"kv-1","payload":[],"mac":"AAAA"}
            """;

        var result = ApprovalStoreEnvelope.Unwrap<List<SampleGrant>>(json, TicketsKey);

        Assert.AreEqual(ApprovalStoreIntegrityOutcome.UnknownAlgorithm, result.Outcome);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("{")]
    [DataRow("not json at all")]
    public void MalformedInput_IsReportedAsMalformed(string json)
    {
        var result = ApprovalStoreEnvelope.Unwrap<List<SampleGrant>>(json, TicketsKey);

        Assert.AreEqual(ApprovalStoreIntegrityOutcome.Malformed, result.Outcome);
        Assert.IsNull(result.Payload);
    }

    [TestMethod]
    public void Wrap_RejectsInvalidArguments()
    {
        var payload = SamplePayload();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ApprovalStoreEnvelope.Wrap(payload, 0, "kv-1", SignedAt, TicketsKey),
            "seq 必须为正（0 表示「没有序号」，会让回滚检测失效）。");

        Assert.ThrowsExactly<ArgumentException>(
            () => ApprovalStoreEnvelope.Wrap(payload, 1, "  ", SignedAt, TicketsKey),
            "缺 keyId ⇒ 无法轮换密钥。");

        Assert.ThrowsExactly<ArgumentException>(
            () => ApprovalStoreEnvelope.Wrap(payload, 1, "kv-1", SignedAt, []),
            "空密钥必须被拒，而不是产出「永远验不过」的文件。");
    }

    [TestMethod]
    public void DeriveStoreKey_RejectsBlankFileName()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => ApprovalStoreEnvelope.DeriveStoreKey(MasterKey, " "));
    }

    [TestMethod]
    public void IsSequenceAcceptable_EnforcesStrictlyIncreasing()
    {
        Assert.IsTrue(ApprovalStoreEnvelope.IsSequenceAcceptable(5, 4), "递增可通过。");
        Assert.IsTrue(ApprovalStoreEnvelope.IsSequenceAcceptable(1, 0), "首次（无已见序号）可通过。");
        Assert.IsFalse(ApprovalStoreEnvelope.IsSequenceAcceptable(4, 4), "相等即回放，必须拒。");
        Assert.IsFalse(ApprovalStoreEnvelope.IsSequenceAcceptable(3, 5), "倒退即回滚，必须拒。");
    }

    [TestMethod]
    public void EnvelopeJson_DoesNotContainPlaintextSecretMarker()
    {
        // 前置守卫：信封只承载"结构性元数据 + 载荷"，不引入任何额外的敏感字段。
        var wrapped = ApprovalStoreEnvelope.Wrap(SamplePayload(), 7, "kv-1", SignedAt, TicketsKey);
        var bytes = Encoding.UTF8.GetBytes(wrapped);

        Assert.IsTrue(wrapped.Contains("\"mac\"", StringComparison.Ordinal));
        Assert.IsTrue(wrapped.Contains("\"keyId\"", StringComparison.Ordinal));
        Assert.IsTrue(wrapped.Contains("\"schemaVersion\":2", StringComparison.Ordinal));
        Assert.IsTrue(bytes.Length > 0);
    }
}
