using System.Security.Cryptography;
using System.Text;
using PuddingCode.Goals;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// G92-1 S1-c 片 1+2（text-assertion 定义载体与哈希）契约锁：
/// 词表、spec 载体、ChecksJson 往返、注册表条目、「永不建命令」拒绝集与哈希方案 A。
/// <para>
/// 金样 hash（改动前注册表现状，payload = sha256("{Ref}|{Kind}|{CommandTemplate}")，2026-09-18 固化）
/// 经两条独立路径计算并交叉验证一致后固化；测试内再用 System.Security.Cryptography 独立重算
/// 作为第二把锁，防止金样来源单点失真。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalCheckTextAssertionDefinitionTests
{
    // —— 金样：改动前注册表既有三条目（2026-09-18 固化，hex 小写，与 Convert.ToHexStringLower 一致）——
    private const string GoldenBuildHash =
        "sha256:4f6c397e3026d371aebfcf8843573e7097b58ffa2eeb1398ef771e55999ffa9e";
    private const string GoldenTestHash =
        "sha256:4357c6fa09cbcbd901d90810350fdee8d14ccad5303f765d521ca1861162d782";
    private const string GoldenFileEvidenceHash =
        "sha256:28f1b16e35d7fb3c6eeb3b90d31650a57d1ae9204ea02e45bb1b6f575a167a39";

    // —— 方案 A 断言值（text-assertion 条目，CommandTemplate = 空串）——
    // null ⇒ payload = "{Ref}|{Kind}|"（无追加段，单尾管道）；
    // 非空 ⇒ 在其后再拼 "|{ExpectedText}"（追加段自带管道 ⇒ 出现连续 "||"）。
    private const string TextAssertionTemplateHash =
        "sha256:921b0855760fa1f100fec4337f53ccccd2ad6e05ccb0f7ee1ec781a1e8eab5bf"; // ExpectedText=null
    private const string TextAssertionOkHash =
        "sha256:ab11469cb64977dc536f9e7c65268e694dfe8ffb32c60e88de1e834702c68294"; // ExpectedText="OK"
    private const string TextAssertionWhitespaceHash =
        "sha256:0b8635e91a1223533d1885de9bcc2a34b757d6c6c26469f7079fe249ca05b41f"; // ExpectedText=" "

    private const string TextAssertionTemplatePayload = "checks/text-assertion.md#equals|text-assertion|";
    private const string TextAssertionWithTextPayloadPrefix = "checks/text-assertion.md#equals|text-assertion||";

    // —— 片 1：词表与 spec 载体 ——

    [TestMethod]
    public void SpecKinds_TextAssertion_WireValueIsControlledKebabCase()
    {
        Assert.AreEqual("text-assertion", GoalVerificationSpecKinds.TextAssertion);
    }

    [TestMethod]
    public void Registry_TextAssertionEntry_IsReadOnlyTemplate()
    {
        Assert.IsTrue(GoalCheckDefinitionRegistry.TryResolve(
            GoalCheckDefinitionRegistry.TextAssertionRef, out var definition));

        Assert.AreEqual(GoalVerificationSpecKinds.TextAssertion, definition.Kind);
        Assert.AreEqual(string.Empty, definition.CommandTemplate);
        Assert.IsFalse(definition.RequiresTestEvidence);
        // 注册表条目是模板态：ExpectedText 为 null（期望文本由 spec 供给，按方案 A 进 hash 载荷）。
        Assert.IsNull(definition.ExpectedText);
    }

    // —— 片 1：ChecksJson 序列化往返 ——

    [TestMethod]
    public void SerializeChecks_TextAssertionSpec_RoundTripsExpectedText()
    {
        var spec = new GoalCheckSpec
        {
            CheckId = "check-text-1",
            CriterionId = "criterion-1",
            Kind = GoalVerificationSpecKinds.TextAssertion,
            DefinitionRef = GoalCheckDefinitionRegistry.TextAssertionRef,
            ExpectedText = "OK",
        };

        var json = GoalVerificationPersistence.SerializeChecks([spec]);
        var restored = GoalVerificationPersistence.ReadChecks(json);

        Assert.AreEqual(1, restored.Count);
        Assert.AreEqual("check-text-1", restored[0].CheckId);
        Assert.AreEqual(GoalVerificationSpecKinds.TextAssertion, restored[0].Kind);
        Assert.AreEqual(GoalCheckDefinitionRegistry.TextAssertionRef, restored[0].DefinitionRef);
        Assert.AreEqual("OK", restored[0].ExpectedText);
    }

    [TestMethod]
    public void ReadChecks_LegacySpecWithoutExpectedText_YieldsNullWithoutThrowing()
    {
        // 旧格式：无 expectedText 键。反序列化不得抛异常，ExpectedText 必须为 null。
        const string legacyJson =
            """[{"checkId":"check-1","criterionId":"criterion-1","kind":"build"}]""";

        var checks = GoalVerificationPersistence.ReadChecks(legacyJson);

        Assert.AreEqual(1, checks.Count);
        Assert.IsNull(checks[0].ExpectedText);
        Assert.AreEqual(GoalVerificationSpecKinds.Build, checks[0].Kind);
    }

    // —— 片 2：金样 hash 锁（改动前三条目逐字节不变） ——

    [TestMethod]
    public void Registry_LegacyEntries_HashesAreByteIdenticalToBaseline()
    {
        Assert.IsTrue(GoalCheckDefinitionRegistry.TryGetDefinitionHash(
            "checks/build.md#dotnet-build", out var buildHash));
        Assert.AreEqual(GoldenBuildHash, buildHash);

        Assert.IsTrue(GoalCheckDefinitionRegistry.TryGetDefinitionHash(
            "checks/test.md#dotnet-test", out var testHash));
        Assert.AreEqual(GoldenTestHash, testHash);

        Assert.IsTrue(GoalCheckDefinitionRegistry.TryGetDefinitionHash(
            GoalCheckDefinitionRegistry.FileEvidenceRef, out var fileEvidenceHash));
        Assert.AreEqual(GoldenFileEvidenceHash, fileEvidenceHash);
    }

    [TestMethod]
    public void GoldenHashes_MatchIndependentLocalRecompute()
    {
        // 第二把锁：金样值与测试内独立重算一致（防金样来源单点失真）。
        Assert.AreEqual(
            GoldenBuildHash,
            Sha256Hex("checks/build.md#dotnet-build|build|dotnet build {0} --no-restore --nologo"));
        Assert.AreEqual(
            GoldenTestHash,
            Sha256Hex("checks/test.md#dotnet-test|test|dotnet test {0} --no-restore --nologo"));
        Assert.AreEqual(
            GoldenFileEvidenceHash,
            Sha256Hex("checks/file-evidence.md#file-exists-nonempty|file-evidence|只读核验：文件存在且非空（File.Exists + 长度；不启动任何进程）"));
    }

    // —— 片 2：方案 A（期望文本按 IsNullOrEmpty 条件拼接） ——

    [TestMethod]
    public void Registry_TextAssertionTemplateEntry_HashFollowsSchemeA()
    {
        // 模板条目 ExpectedText=null ⇒ 不追加段，payload 产生连续 "||"。
        Assert.IsTrue(GoalCheckDefinitionRegistry.TryGetDefinitionHash(
            GoalCheckDefinitionRegistry.TextAssertionRef, out var hash));

        Assert.AreEqual(TextAssertionTemplateHash, hash);
        Assert.AreEqual(Sha256Hex(TextAssertionTemplatePayload), TextAssertionTemplateHash);
    }

    [TestMethod]
    public void ComputeDefinitionHash_NonEmptyExpectedText_AppendsToPayload()
    {
        var definition = new GoalCheckDefinitionRegistry.GoalCheckDefinition(
            GoalCheckDefinitionRegistry.TextAssertionRef,
            GoalVerificationSpecKinds.TextAssertion,
            string.Empty,
            RequiresTestEvidence: false,
            ExpectedText: "OK");

        var hash = GoalCheckDefinitionRegistry.ComputeDefinitionHash(definition);

        Assert.AreEqual(TextAssertionOkHash, hash);
        Assert.AreEqual(Sha256Hex(TextAssertionWithTextPayloadPrefix + "OK"), TextAssertionOkHash);
    }

    [TestMethod]
    public void ComputeDefinitionHash_WhitespaceExpectedText_MustJoinPayload()
    {
        // D1：默认不 Trim，纯空白期望文本是合法载荷。
        // 精确锁 IsNullOrEmpty 语义：ExpectedText=" " 必须参与拼接（防 IsNullOrWhiteSpace 回归）。
        var template = new GoalCheckDefinitionRegistry.GoalCheckDefinition(
            GoalCheckDefinitionRegistry.TextAssertionRef,
            GoalVerificationSpecKinds.TextAssertion,
            string.Empty,
            RequiresTestEvidence: false);
        var whitespace = template with { ExpectedText = " " };

        var templateHash = GoalCheckDefinitionRegistry.ComputeDefinitionHash(template);
        var whitespaceHash = GoalCheckDefinitionRegistry.ComputeDefinitionHash(whitespace);

        Assert.AreNotEqual(templateHash, whitespaceHash);
        Assert.AreEqual(TextAssertionWhitespaceHash, whitespaceHash);
        Assert.AreEqual(Sha256Hex(TextAssertionWithTextPayloadPrefix + " "), TextAssertionWhitespaceHash);
    }

    // —— 片 2：TryBuildCommand「永不建命令」拒绝集 ——

    [TestMethod]
    public void TryBuildCommand_TextAssertionKind_IsAlwaysRejected()
    {
        var spec = new GoalCheckSpec
        {
            CheckId = "check-text-1",
            CriterionId = "criterion-1",
            Kind = GoalVerificationSpecKinds.TextAssertion,
            DefinitionRef = GoalCheckDefinitionRegistry.TextAssertionRef,
            ExpectedText = "OK",
            InputRefs = ["docs/readme.md"],
        };

        var resolved = GoalCheckDefinitionRegistry.TryBuildCommand(
            spec, out var command, out var failureCode);

        Assert.IsFalse(resolved);
        Assert.AreEqual(string.Empty, command);
        Assert.AreEqual(GoalCheckFailureCodes.UnsupportedCheckKind, failureCode);
    }

    private static string Sha256Hex(string payload)
        => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))}";
}
