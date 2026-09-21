using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Skills.Curation;

namespace PuddingCoreTests;

/// <summary>
/// RSI-G7 交付物 1–2 的契约层用例：技能整理策略 + 提炼产物契约（反笔记不变式）。
/// <para>
/// 纪律：**每条判据都要有"能变红"的用例**，且负例必须断言**具体违规码** ——
/// 只断言"没崩 / 返回了非空"不算（那是无牙断言）。
/// </para>
/// </summary>
[TestClass]
public sealed class SkillDistillationContractTests
{
    private const string ApplicabilityHeading = "何时适用";
    private const string PitfallHeading = "陷阱与反例";

    [TestMethod]
    public void ValidProduct_ShouldPass_WithNoViolations()
    {
        var violations = SkillDistillationContract.Validate(
            ValidProduct(),
            forbiddenLiterals: ["turn-1", "session-1"],
            isToolLikeKeyword: _ => false,
            policy: Policy());

        Assert.AreEqual(0, violations.Count, "合规产物必须零违规：" + string.Join(",", violations));
    }

    [TestMethod]
    public void MissingApplicabilitySection_ShouldViolate()
    {
        var product = ValidProduct() with { Markdown = MarkdownWithoutApplicability() };

        var violations = SkillDistillationContract.Validate(
            product, forbiddenLiterals: [], isToolLikeKeyword: _ => false, policy: Policy());

        CollectionAssert.Contains(violations.ToList(), SkillDistillationContract.MissingApplicabilitySection);
    }

    [TestMethod]
    public void MissingPitfallSection_ShouldViolate()
    {
        var product = ValidProduct() with { Markdown = MarkdownWithoutPitfall() };

        var violations = SkillDistillationContract.Validate(
            product, forbiddenLiterals: [], isToolLikeKeyword: _ => false, policy: Policy());

        CollectionAssert.Contains(violations.ToList(), SkillDistillationContract.MissingPitfallSection);
    }

    [TestMethod]
    public void HeadingMatch_ShouldIgnoreHashPrefixAndWhitespace()
    {
        // 容忍度是**被冻结的语义**：去掉前导 # 与空白后按前缀匹配（大小写不敏感）。
        var product = ValidProduct() with
        {
            Markdown = "#   何时适用（本岗位）\n\n正文内容足够长，足以通过长度下限检查。\n\n### 陷阱与反例\n\n踩过的坑，记下来。",
        };

        var violations = SkillDistillationContract.Validate(
            product, forbiddenLiterals: [], isToolLikeKeyword: _ => false, policy: Policy());

        Assert.AreEqual(0, violations.Count, "标题匹配必须容忍 # 前缀与空白：" + string.Join(",", violations));
    }

    [TestMethod]
    public void IdentityLiteralInContent_ShouldViolate()
    {
        var product = ValidProduct() with
        {
            Markdown = FullMarkdown() + "\n来源：turn-1",
        };

        var violations = SkillDistillationContract.Validate(
            product, forbiddenLiterals: ["turn-1"], isToolLikeKeyword: _ => false, policy: Policy());

        CollectionAssert.Contains(violations.ToList(), SkillDistillationContract.IdentityLiteralInContent);
    }

    [TestMethod]
    public void IdentityLiteralInKeyword_ShouldViolate()
    {
        var product = ValidProduct() with { Keywords = ["登录流程", "session-1"] };

        var violations = SkillDistillationContract.Validate(
            product, forbiddenLiterals: ["session-1"], isToolLikeKeyword: _ => false, policy: Policy());

        CollectionAssert.Contains(violations.ToList(), SkillDistillationContract.IdentityLiteralInKeyword);
    }

    [TestMethod]
    public void ToolNameInKeyword_ShouldViolate()
    {
        var violations = SkillDistillationContract.Validate(
            ValidProduct() with { Keywords = ["经验提炼", "git_commit"] },
            forbiddenLiterals: [],
            isToolLikeKeyword: keyword => keyword == "git_commit",
            policy: Policy());

        CollectionAssert.Contains(violations.ToList(), SkillDistillationContract.ToolNameInKeyword);
    }

    [TestMethod]
    public void MissingProvenance_ShouldViolate()
    {
        var product = ValidProduct() with { EvidenceTags = ["auto-generated"] };

        var violations = SkillDistillationContract.Validate(
            product, forbiddenLiterals: [], isToolLikeKeyword: _ => false, policy: Policy());

        CollectionAssert.Contains(violations.ToList(), SkillDistillationContract.MissingProvenance);
    }

    [TestMethod]
    public void MarkdownTooShort_ShouldViolate()
    {
        var product = ValidProduct() with { Markdown = "何时适用\n陷阱与反例" };

        var violations = SkillDistillationContract.Validate(
            product, forbiddenLiterals: [], isToolLikeKeyword: _ => false, policy: Policy());

        CollectionAssert.Contains(violations.ToList(), SkillDistillationContract.MarkdownTooShort);
    }

    [TestMethod]
    public void EmptyKeywords_ShouldViolate()
    {
        var product = ValidProduct() with { Keywords = ["  ", ""] };

        var violations = SkillDistillationContract.Validate(
            product, forbiddenLiterals: [], isToolLikeKeyword: _ => false, policy: Policy());

        CollectionAssert.Contains(violations.ToList(), SkillDistillationContract.KeywordsEmpty);
    }

    [TestMethod]
    public void BlankForbiddenLiteral_ShouldNotRejectEverything()
    {
        // 显式边界：空字面量不是"匹配一切"，而是被忽略（否则调用方一个空串就阻断全部产物）。
        var violations = SkillDistillationContract.Validate(
            ValidProduct(), forbiddenLiterals: ["", "   "], isToolLikeKeyword: _ => false, policy: Policy());

        Assert.AreEqual(0, violations.Count, "空字面量必须被忽略：" + string.Join(",", violations));
    }

    [TestMethod]
    public void Validate_ShouldThrow_WhenArgumentsNull()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => SkillDistillationContract.Validate(
            null!, [], _ => false, Policy()));

        Assert.ThrowsExactly<ArgumentNullException>(() => SkillDistillationContract.Validate(
            ValidProduct(), [], null!, Policy()));

        Assert.ThrowsExactly<ArgumentNullException>(() => SkillDistillationContract.Validate(
            ValidProduct(), [], _ => false, null!));
    }

    [TestMethod]
    public void Validate_ShouldThrow_WhenPolicyInvalid()
    {
        var invalid = new SkillCurationPolicy
        {
            PolicyId = "skill-curation/test",
            Version = 1,
            MinRetainedValueRatio = 0.5,
            ApplicabilityHeadings = [], // 空标题集合 ⇒ 契约会退化成"永远违规"
            PitfallHeadings = [PitfallHeading],
            MinMarkdownLength = 80,
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => SkillDistillationContract.Validate(
            ValidProduct(), [], _ => false, invalid));
    }

    [TestMethod]
    [DataRow(double.NaN, 80, 1, "ApplicabilityHeadings 为空")]
    public void Policy_ShouldRejectInvalidConfigurations(double ratio, int minLength, int version, string reason)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => SkillCurationPolicy.Create(
            "skill-curation/test",
            version,
            ratio,
            [],
            [PitfallHeading],
            minLength), reason);
    }

    [TestMethod]
    public void Policy_ShouldRejectNegativeRatio()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => SkillCurationPolicy.Create(
            "skill-curation/test", 1, -0.1, [ApplicabilityHeading], [PitfallHeading], 80));
    }

    [TestMethod]
    public void Policy_ShouldRejectBlankPolicyId()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => SkillCurationPolicy.Create(
            "   ", 1, 0.5, [ApplicabilityHeading], [PitfallHeading], 80));
    }

    [TestMethod]
    public void Policy_ShouldRejectNonPositiveMarkdownLength()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => SkillCurationPolicy.Create(
            "skill-curation/test", 1, 0.5, [ApplicabilityHeading], [PitfallHeading], 0));
    }

    [TestMethod]
    public void Policy_Create_ShouldRequireAllKnobsExplicitly()
    {
        var policy = Policy();

        // 可版本化事实：id / 版本 / 比例 / 标题集合 / 长度下限全部随策略对象携带。
        Assert.AreEqual("skill-curation/test", policy.PolicyId);
        Assert.AreEqual(3, policy.Version);
        Assert.AreEqual(0.5, policy.MinRetainedValueRatio, 0.000001);
        Assert.AreEqual(40, policy.MinMarkdownLength);
        Assert.IsTrue(policy.IsValid);
    }

    [TestMethod]
    public void Policy_ToApplied_ShouldMirrorEveryConsumedField()
    {
        var applied = Policy().ToApplied();

        Assert.AreEqual("skill-curation/test", applied.PolicyId);
        Assert.AreEqual(3, applied.Version);
        Assert.AreEqual(0.5, applied.MinRetainedValueRatio, 0.000001);
        Assert.AreEqual(40, applied.MinMarkdownLength);
        CollectionAssert.AreEqual(new[] { ApplicabilityHeading }, applied.ApplicabilityHeadings.ToArray());
        CollectionAssert.AreEqual(new[] { PitfallHeading }, applied.PitfallHeadings.ToArray());
    }

    [TestMethod]
    public void EvidenceTagExtraction_ShouldTrimDedupeAndIgnoreOtherTags()
    {
        string[] tags =
        [
            "auto-generated",
            " source-turn:turn-1 ",
            "source-turn:TURN-1",
            "source-turn:",
            "source-session:session-1",
            "source-session:session-1",
        ];

        CollectionAssert.AreEqual(
            new[] { "turn-1" },
            SkillDistillationContract.SourceTurnsOf(tags).ToArray());

        CollectionAssert.AreEqual(
            new[] { "session-1" },
            SkillDistillationContract.SourceSessionsOf(tags).ToArray());
    }

    [TestMethod]
    public void HeadingMatch_ShouldBeCaseInsensitive()
    {
        var policy = Policy() with { ApplicabilityHeadings = new[] { "when to apply" } };
        var product = ValidProduct() with
        {
            Markdown = "### WHEN TO APPLY（本岗位）\n\n正文内容足够长，足以通过长度下限检查。\n\n# 陷阱与反例\n\n踩过的坑，记下来。",
        };

        var violations = SkillDistillationContract.Validate(
            product, forbiddenLiterals: [], isToolLikeKeyword: _ => false, policy: policy);

        Assert.AreEqual(0, violations.Count, "标题匹配必须大小写不敏感：" + string.Join(",", violations));
    }

    private static SkillCurationPolicy Policy() => SkillCurationPolicy.Create(
        policyId: "skill-curation/test",
        version: 3,
        minRetainedValueRatio: 0.5,
        applicabilityHeadings: [ApplicabilityHeading],
        pitfallHeadings: [PitfallHeading],
        minMarkdownLength: 40);

    private static string FullMarkdown()
        => "# " + ApplicabilityHeading + "\n\n"
            + "当需要把多条同族经验收敛为一条可迁移程序时使用；单条具体操作不要用。\n\n"
            + "## " + PitfallHeading + "\n\n"
            + "把会话 id 或工具名写进正文会让产物退化成笔记。";

    private static string MarkdownWithoutApplicability()
        => "# " + PitfallHeading + "\n\n只有陷阱段，没有适用条件段，正文长度足够长以通过长度下限检查。";

    private static string MarkdownWithoutPitfall()
        => "# " + ApplicabilityHeading + "\n\n只有适用条件段，没有陷阱段，正文长度足够长以通过长度下限检查。";

    private static DistilledSkillProduct ValidProduct() => new()
    {
        Name = "把同族经验收敛为可迁移程序",
        Description = "提炼产物",
        Markdown = FullMarkdown(),
        Keywords = ["经验提炼", "家族收敛"],
        EvidenceTags = ["auto-generated", "source-turn:turn-9", "source-session:session-9"],
        ReplacedSkillIds = ["skill-a", "skill-b"],
    };
}
