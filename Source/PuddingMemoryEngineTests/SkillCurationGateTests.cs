using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Improvement;
using PuddingCode.Skills.Curation;
using PuddingCode.Skills.Portfolio;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// G7 整理门禁（C1–C5）契约用例。
/// <para>
/// 纪律：五条门禁**各有一个"必须拒绝"的负例**，且每条负例断言**具体理由码 / 明细**；
/// 冷启动必须证明"记录但不阻断"；<c>Unavailable</c> 必须证明没被当 0。
/// 每个用例都能在对应实现被改坏时**变红**（RSI-G7 任务书 §5 的 I1–I8）。
/// </para>
/// </summary>
[TestClass]
public sealed class SkillCurationGateTests
{
    private const string ApplicabilityHeading = "何时适用";
    private const string PitfallHeading = "陷阱与反例";

    // ───────────────────────── 基线：五条门禁全过 ⇒ shadow ─────────────────────────

    [TestMethod]
    public void CleanProduct_ShouldProduceShadowVerdict_AndEveryCriterionPassed()
    {
        var outcome = Gate().Evaluate(Request());

        Assert.AreEqual(ChangeDecision.ApplyShadow, outcome.Verdict.Decision);
        Assert.AreEqual(SkillCurationGate.ReasonNoBlockingFindings, outcome.Verdict.ReasonCode);
        Assert.IsFalse(outcome.HasBlockingFindings);
        Assert.AreEqual(0, outcome.ContractViolations.Count);
        Assert.AreEqual(0, outcome.KeywordCollisions.Count);
        Assert.AreEqual(0, outcome.MissingSourceTurns.Count);
        Assert.AreEqual(7, outcome.Criteria.Count, "逐条判据留痕：contract / C1-C5 / operation");
        Assert.IsTrue(outcome.Criteria.All(criterion => criterion.Passed), "全通过时每条判据都必须留痕为 Passed");
    }

    // ───────────────────────── I1 C1 证据不丢 ─────────────────────────

    [TestMethod]
    public void C1_ShouldReject_WhenAReplacedTurnIsMissingFromEvidence()
    {
        // 被取代者真实 provenance 里有过 turn-2；产物只留下 turn-1 ⇒ 证据丢失 ⇒ 拒绝。
        var replaced = new[]
        {
            Snapshot("skill-a", Observed(0.5), turns: ["turn-1"], sessions: ["session-1"]),
            Snapshot("skill-b", Observed(0.5), turns: ["turn-2"], sessions: ["session-1"]),
        };
        var product = Product(turns: ["turn-1"], sessions: ["session-1"]);

        var outcome = Gate().Evaluate(Request(product: product, replaced: replaced));

        Assert.AreEqual(ChangeDecision.Reject, outcome.Verdict.Decision);
        StringAssert.Contains(outcome.Verdict.ReasonCode, SkillCurationGate.ReasonEvidenceLost);
        StringAssert.Contains(outcome.Verdict.ReasonCode, "turn-2", "理由码必须**指向**缺失的 turn");
        CollectionAssert.Contains(outcome.MissingSourceTurns.ToList(), "turn-2");
    }

    // ───────────────────────── I2 C2 一般性不降 + 冷启动 ─────────────────────────

    [TestMethod]
    public void C2_ShouldReject_WhenCoveringSessionsDrop()
    {
        // 被取代者覆盖数 = **每个技能各自的覆盖数取最大**（不是并集）：skill-a 覆盖 2 个，产物只覆盖 1 个 ⇒ 一般性下降 ⇒ 拒绝。
        var replaced = new[]
        {
            Snapshot("skill-a", Observed(0.4), turns: ["turn-1"], sessions: ["session-1", "session-2"]),
            Snapshot("skill-b", Observed(0.4), turns: ["turn-2"], sessions: ["session-1"]),
        };
        var product = Product(turns: ["turn-1", "turn-2"], sessions: ["session-1"]);

        var outcome = Gate().Evaluate(Request(product: product, replaced: replaced));

        Assert.AreEqual(ChangeDecision.Reject, outcome.Verdict.Decision);
        StringAssert.Contains(outcome.Verdict.ReasonCode, SkillCurationGate.ReasonGeneralityReduced);
        StringAssert.Contains(outcome.Verdict.ReasonCode, "1<2", "理由码必须带实测计数，便于回归");
    }

    [TestMethod]
    public void C2_ColdStart_ShouldRecordButNotBlock_WhenNoReplacedSkillHasSessionTags()
    {
        // 全部被取代者都没有 source-session: ⇒ 「算不出来」≠「低价值」⇒ 只记录、不阻断。
        var replaced = new[]
        {
            Snapshot("skill-a", Observed(0.4), turns: ["turn-1"]),
            Snapshot("skill-b", Observed(0.4), turns: ["turn-2"]),
        };
        var product = Product(turns: ["turn-1", "turn-2"]);

        var outcome = Gate().Evaluate(Request(product: product, replaced: replaced));

        Assert.AreEqual(ChangeDecision.ApplyShadow, outcome.Verdict.Decision, "冷启动不得阻断");
        CollectionAssert.Contains(
            outcome.AvailabilityNotes.ToList(),
            SkillCurationGate.AvailabilityCoveringSessionsUnavailable);
    }

    // ───────────────────────── I3 C3 关键词唯一（含 §2.6 裁决 1 双向） ─────────────────────────

    [TestMethod]
    public void C3_ShouldReject_WhenCollisionLandsOnThirdPartyEnabledSkill()
    {
        var outcome = Gate().Evaluate(Request(
            retained: [Snapshot("skill-z", Observed(0.5), keywords: ["登录流程"])],
            productKeywords: ["登录流程", "会话治理"]));

        Assert.AreEqual(ChangeDecision.Reject, outcome.Verdict.Decision);
        StringAssert.Contains(outcome.Verdict.ReasonCode, SkillCurationGate.ReasonKeywordCollision);
        CollectionAssert.Contains(outcome.KeywordCollisions.ToList(), "登录流程");
    }

    [TestMethod]
    public void C3_ShouldPass_WhenCollisionOnlyLandsOnReplacedSkill()
    {
        // §2.6 裁决 1：被取代者即被停用，与它们的交集不构成唯一性问题 ⇒ 必须放行（否则静默过度阻断）。
        var outcome = Gate().Evaluate(Request(
            retained: [Snapshot("skill-a", Observed(0.5), keywords: ["登录流程"])],
            productKeywords: ["登录流程", "会话治理"]));

        Assert.AreEqual(ChangeDecision.ApplyShadow, outcome.Verdict.Decision);
        Assert.AreEqual(0, outcome.KeywordCollisions.Count);
    }

    [TestMethod]
    public void C3_ShouldReject_CaseInsensitively()
    {
        var outcome = Gate().Evaluate(Request(
            retained: [Snapshot("skill-z", Observed(0.5), keywords: ["LoginFlow"])],
            productKeywords: ["loginflow"]));

        Assert.AreEqual(ChangeDecision.Reject, outcome.Verdict.Decision);
        StringAssert.Contains(outcome.Verdict.ReasonCode, SkillCurationGate.ReasonKeywordCollision);
    }

    // ───────────────────────── I4 C4 价值不降 + 未把无数据当 0 ─────────────────────────

    [TestMethod]
    public void C4_ShouldReject_WhenProductValueFallsBelowRatioTimesSum()
    {
        // ratio 0.5；被取代者 0.75 + 0.25 = 1.00 ⇒ 下限 0.50；产物 0.40 < 0.50 ⇒ 拒绝。
        var replaced = new[]
        {
            Snapshot("skill-a", Observed(0.75), turns: ["turn-1"], sessions: ["session-1"]),
            Snapshot("skill-b", Observed(0.25), turns: ["turn-2"], sessions: ["session-1"]),
        };

        var outcome = Gate().Evaluate(Request(
            replaced: replaced,
            product: Product(turns: ["turn-1", "turn-2"], sessions: ["session-1"]),
            productScore: Observed(0.40)));

        Assert.AreEqual(ChangeDecision.Reject, outcome.Verdict.Decision);
        StringAssert.Contains(outcome.Verdict.ReasonCode, SkillCurationGate.ReasonValueReduced);
    }

    [TestMethod]
    public void C4_ShouldPass_AtExactlyTheRatioBoundary()
    {
        // 边界：product == ratio × Σ ⇒ 不低于 ⇒ 放行（与 G3 同款浮点纪律，用 0.75/0.25 避免 0.55-0.50 型假红）。
        var replaced = new[]
        {
            Snapshot("skill-a", Observed(0.75), turns: ["turn-1"], sessions: ["session-1"]),
            Snapshot("skill-b", Observed(0.25), turns: ["turn-2"], sessions: ["session-1"]),
        };

        var outcome = Gate().Evaluate(Request(
            replaced: replaced,
            product: Product(turns: ["turn-1", "turn-2"], sessions: ["session-1"]),
            productScore: Observed(0.50)));

        Assert.AreEqual(ChangeDecision.ApplyShadow, outcome.Verdict.Decision);
    }

    [TestMethod]
    public void C4_UnavailableOnAnySide_ShouldRecordButNotBlock_NotTreatedAsZero()
    {
        // 被取代者无分 ⇒ 若把 Unavailable 当 0，则「0 × ratio = 0」会恒通过/或反被误判；正确行为是"记录不阻断"。
        var replaced = new[]
        {
            Snapshot("skill-a", new SkillScoreSnapshot.Unavailable("telemetry_inactive"), turns: ["turn-1"], sessions: ["session-1"]),
        };

        var outcome = Gate().Evaluate(Request(
            replaced: replaced,
            product: Product(turns: ["turn-1"], sessions: ["session-1"]),
            productScore: Observed(0.01)));

        Assert.AreEqual(ChangeDecision.ApplyShadow, outcome.Verdict.Decision, "任一侧不可比 ⇒ 记录不阻断");
        CollectionAssert.Contains(
            outcome.AvailabilityNotes.ToList(),
            SkillCurationGate.AvailabilityValueScoreUnavailable);
    }

    [TestMethod]
    public void C4_ScoreScaleMismatch_ShouldRecordButNotBlock()
    {
        var replaced = new[]
        {
            Snapshot("skill-a", new SkillScoreSnapshot.Observed("hits/v1", 0.9), turns: ["turn-1"], sessions: ["session-1"]),
        };

        var outcome = Gate().Evaluate(Request(
            replaced: replaced,
            product: Product(turns: ["turn-1"], sessions: ["session-1"]),
            productScore: new SkillScoreSnapshot.Observed("quality/v2", 0.01)));

        Assert.AreEqual(ChangeDecision.ApplyShadow, outcome.Verdict.Decision);
        CollectionAssert.Contains(
            outcome.AvailabilityNotes.ToList(),
            SkillCurationGate.AvailabilityScoreScaleMismatch);
    }

    // ───────────────────────── I5 C5 可回滚（构造期强制在本路径被走到） ─────────────────────────

    [TestMethod]
    public void C5_ShouldReject_WhenApplyRequestedWithoutRollbackHandle()
    {
        var outcome = Gate().Evaluate(Request(
            decision: ChangeDecision.Apply,
            rollbackHandle: null));

        Assert.AreEqual(ChangeDecision.Reject, outcome.Verdict.Decision, "缺句柄 ⇒ 构造期拒绝 ⇒ 门禁转 Reject");
        Assert.AreEqual(SkillCurationGate.ReasonRollbackUnavailable, outcome.Verdict.ReasonCode);
        StringAssert.Contains(outcome.Verdict.Note ?? string.Empty, "rollbackHandle");
    }

    [TestMethod]
    public void C5_ShouldNeverEmitApply_EvenWhenHandleIsProvided()
    {
        var outcome = Gate().Evaluate(Request(
            decision: ChangeDecision.Apply,
            rollbackHandle: "rollback:skill-a+skill-b"));

        Assert.AreNotEqual(ChangeDecision.Apply, outcome.Verdict.Decision, "本片无写盘职权 ⇒ 不得出现 Apply");
        Assert.AreEqual(ChangeDecision.ApplyShadow, outcome.Verdict.Decision);
        Assert.AreEqual(SkillCurationGate.ReasonShadowOnly, outcome.Verdict.ReasonCode);
    }

    [TestMethod]
    public void C5_UnknownDecision_ShouldReject_NeverTreatedAsApproval()
    {
        var outcome = Gate().Evaluate(Request(decision: ChangeDecision.Unknown));

        Assert.AreEqual(ChangeDecision.Reject, outcome.Verdict.Decision);
        Assert.AreEqual(SkillCurationGate.ReasonInvalidRequestedDecision, outcome.Verdict.ReasonCode);
    }

    // ───────────────────────── operation：Add 不是默认 ─────────────────────────

    [TestMethod]
    [DataRow(ImprovementOperation.Create, false)]
    [DataRow(ImprovementOperation.Unknown, false)]
    [DataRow(ImprovementOperation.Merge, true)]
    [DataRow(ImprovementOperation.Replace, true)]
    [DataRow(ImprovementOperation.Retire, true)]
    public void Operation_CreateIsNotDefault(ImprovementOperation operation, bool shouldPass)
    {
        var outcome = Gate().Evaluate(Request(operation: operation));

        if (shouldPass)
        {
            Assert.AreEqual(ChangeDecision.ApplyShadow, outcome.Verdict.Decision, operation.ToString());
            return;
        }

        Assert.AreEqual(ChangeDecision.Reject, outcome.Verdict.Decision, operation.ToString());
        StringAssert.Contains(outcome.Verdict.ReasonCode, SkillCurationGate.ReasonAddNotDefault);
    }

    // ───────────────────────── I6 反笔记不变式（P1/P3/P4 经门禁复核） ─────────────────────────

    [TestMethod]
    public void Contract_ShouldReject_WhenMarkdownLacksApplicabilitySection()
    {
        var product = Product() with
        {
            Markdown = "# " + PitfallHeading + "\n\n只有陷阱段，正文长度足够长以通过长度下限检查，且不含任何身份字面量。",
        };

        var outcome = Gate().Evaluate(Request(product: product));

        Assert.AreEqual(ChangeDecision.Reject, outcome.Verdict.Decision);
        StringAssert.Contains(outcome.Verdict.ReasonCode, SkillCurationGate.ReasonContractViolated);
        StringAssert.Contains(outcome.Verdict.ReasonCode, SkillDistillationContract.MissingApplicabilitySection);
    }

    [TestMethod]
    public void Contract_ShouldReject_WhenKeywordCarriesReplacedSessionId()
    {
        // 禁字面量来自**真实 provenance**（被取代者的 session tag），不是正则猜出来的。
        var replaced = new[]
        {
            Snapshot("skill-a", Observed(0.5), turns: ["turn-1"], sessions: ["session-42"]),
        };

        var outcome = Gate().Evaluate(Request(
            replaced: replaced,
            product: Product(turns: ["turn-1"], sessions: ["session-42"], keywords: ["session-42", "会话治理"])));

        Assert.AreEqual(ChangeDecision.Reject, outcome.Verdict.Decision);
        StringAssert.Contains(outcome.Verdict.ReasonCode, SkillDistillationContract.IdentityLiteralInKeyword);
    }

    [TestMethod]
    public void Contract_ShouldReject_WhenKeywordIsToolLike()
    {
        var outcome = Gate().Evaluate(Request(
            productKeywords: ["git_commit", "会话治理"],
            isToolLike: keyword => keyword == "git_commit"));

        Assert.AreEqual(ChangeDecision.Reject, outcome.Verdict.Decision);
        StringAssert.Contains(outcome.Verdict.ReasonCode, SkillDistillationContract.ToolNameInKeyword);
    }

    // ───────────────────────── 变异取红：阈值必须来自策略对象（门禁内不得有裸阈值） ─────────────────────────

    [TestMethod]
    public void ValueThreshold_ShouldComeFromPolicy_NotFromBareConstant()
    {
        var replaced = new[]
        {
            Snapshot("skill-a", Observed(0.75), turns: ["turn-1"], sessions: ["session-1"]),
            Snapshot("skill-b", Observed(0.25), turns: ["turn-2"], sessions: ["session-1"]),
        };
        var product = Product(turns: ["turn-1", "turn-2"], sessions: ["session-1"]);
        var request = Request(replaced: replaced, product: product, productScore: Observed(0.40));

        // 同一输入，仅把策略比例下限从 0.5 抬到 0.4 ⇒ 结论必须随之改变（0.40 < 0.40 为假 ⇒ 放行）。
        var strict = Gate().Evaluate(request);
        var lax = Gate(ratio: 0.4).Evaluate(request);

        Assert.AreEqual(ChangeDecision.Reject, strict.Verdict.Decision, "ratio=0.5 ⇒ 0.40 < 0.50 拒绝");
        Assert.AreEqual(ChangeDecision.ApplyShadow, lax.Verdict.Decision, "ratio=0.4 ⇒ 0.40 >= 0.40 放行");
    }

    [TestMethod]
    public void NullRequest_ShouldThrow()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => Gate().Evaluate(null!));
    }

    // ───────────────────────── 夹具 ─────────────────────────

    private static SkillCurationGate Gate(double ratio = 0.5) => new(SkillCurationPolicy.Create(
        policyId: "skill-curation/test",
        version: 7,
        minRetainedValueRatio: ratio,
        applicabilityHeadings: [ApplicabilityHeading],
        pitfallHeadings: [PitfallHeading],
        minMarkdownLength: 60));

    private static SkillScoreSnapshot Observed(double score) => new SkillScoreSnapshot.Observed("quality/v1", score);

    private static SkillCurationSkillSnapshot Snapshot(
        string skillId,
        SkillScoreSnapshot score,
        string[]? keywords = null,
        string[]? turns = null,
        string[]? sessions = null)
        => new()
        {
            SkillId = skillId,
            Score = score,
            Keywords = keywords ?? ["既有关键词"],
            Tags = BuildTags(turns, sessions),
        };

    private static SkillCurationRequest Request(
        DistilledSkillProduct? product = null,
        IReadOnlyList<SkillCurationSkillSnapshot>? replaced = null,
        IReadOnlyList<SkillCurationSkillSnapshot>? retained = null,
        SkillScoreSnapshot? productScore = null,
        ImprovementOperation operation = ImprovementOperation.Merge,
        ChangeDecision decision = ChangeDecision.ApplyShadow,
        string? rollbackHandle = null,
        string[]? productKeywords = null,
        Func<string, bool>? isToolLike = null)
        => new()
        {
            Product = product ?? Product(keywords: productKeywords),
            ReplacedSkills = replaced ?? [Snapshot("skill-a", Observed(0.5), turns: ["turn-1"], sessions: ["session-1"])],
            RetainedSkills = retained ?? [],
            ProductScore = productScore ?? Observed(0.5),
            RequestedOperation = operation,
            RequestedDecision = decision,
            IsToolLikeKeyword = isToolLike ?? (_ => false),
            RollbackHandle = rollbackHandle,
        };

    private static DistilledSkillProduct Product(
        string[]? keywords = null,
        string[]? turns = null,
        string[]? sessions = null)
        => new()
        {
            Name = "把同族经验收敛为可迁移程序",
            Description = "提炼产物",
            Markdown = CleanMarkdown(),
            Keywords = keywords ?? ["会话治理", "经验提炼"],
            EvidenceTags = BuildTags(turns ?? ["turn-1"], sessions ?? ["session-1"]),
            ReplacedSkillIds = ["skill-a"],
        };

    private static List<string> BuildTags(string[]? turns, string[]? sessions)
    {
        var tags = new List<string> { "auto-generated" };
        foreach (var turn in turns ?? [])
        {
            tags.Add("source-turn:" + turn);
        }

        foreach (var session in sessions ?? [])
        {
            tags.Add("source-session:" + session);
        }

        return tags;
    }

    private static string CleanMarkdown()
        => "# " + ApplicabilityHeading + "\n\n"
            + "当需要把多条同族经验收敛为一条可迁移程序时使用；单条具体操作不要使用本技能。\n\n"
            + "## " + PitfallHeading + "\n\n"
            + "把会话标识或工具名写进正文会让产物退化成笔记，因此一律只放审计 tags。";
}
