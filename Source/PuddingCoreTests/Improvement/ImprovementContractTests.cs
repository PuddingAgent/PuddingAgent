using PuddingCode.Improvement;
using static PuddingCoreTests.Improvement.GuardAssertions;

namespace PuddingCoreTests.Improvement;

/// <summary>
/// L3-a 切片（改进落点层最小契约）测试：**纯类型 + 守卫，零行为**。
/// </summary>
/// <remarks>
/// 本切片刻意不落任何落点动作（不写技能、不写记忆、不调服务）：
/// 它只锁定两件事 —— ① 枚举数值的序列化兼容；② 每条硬约束在**构造期**就拦得住。
/// 落点适配（L3-b）与整理作业（G6）在这之后才有可能"不改判据地"接上来。
/// </remarks>
[TestClass]
public sealed class ImprovementEnumsContractTests
{
    [TestMethod]
    public void ArtifactKind_Unknown_IsZero_And_ExistingValues_AreFrozen()
    {
        Assert.AreEqual(0, (int)ArtifactKind.Unknown);
        Assert.AreEqual(1, (int)ArtifactKind.Skill);
        Assert.AreEqual(2, (int)ArtifactKind.MemoryChapter);
        Assert.AreEqual(3, (int)ArtifactKind.Rule);
        Assert.AreEqual(4, (int)ArtifactKind.Doc);
        Assert.AreEqual(5, (int)ArtifactKind.CodeFile);
        Assert.AreEqual(ArtifactKind.Unknown, default(ArtifactKind));
    }

    [TestMethod]
    public void ImprovementOperation_Unknown_IsZero_And_ExistingValues_AreFrozen()
    {
        Assert.AreEqual(0, (int)ImprovementOperation.Unknown);
        Assert.AreEqual(1, (int)ImprovementOperation.Create);
        Assert.AreEqual(2, (int)ImprovementOperation.Update);
        Assert.AreEqual(3, (int)ImprovementOperation.Merge);
        Assert.AreEqual(4, (int)ImprovementOperation.Replace);
        Assert.AreEqual(5, (int)ImprovementOperation.Retire);
        Assert.AreEqual(ImprovementOperation.Unknown, default(ImprovementOperation));
    }

    [TestMethod]
    public void ChangeDecision_Unknown_IsZero_And_ExistingValues_AreFrozen()
    {
        Assert.AreEqual(0, (int)ChangeDecision.Unknown);
        Assert.AreEqual(1, (int)ChangeDecision.ApplyShadow);
        Assert.AreEqual(2, (int)ChangeDecision.Apply);
        Assert.AreEqual(3, (int)ChangeDecision.Reject);
        Assert.AreEqual(4, (int)ChangeDecision.Defer);
        Assert.AreEqual(ChangeDecision.Unknown, default(ChangeDecision));
    }

    /// <summary>「默认 shadow」必须是可表达的：不携带回滚句柄也应能构造出影子裁决。</summary>
    [TestMethod]
    public void ChangeDecision_ApplyShadow_IsExpressible_WithoutRollbackHandle()
    {
        var verdict = ChangeVerdict.Create(
            ChangeDecision.ApplyShadow, "improvement.portfolio", 1, "dry_run", rollbackHandle: null);

        Assert.AreEqual(ChangeDecision.ApplyShadow, verdict.Decision);
        Assert.IsNull(verdict.RollbackHandle);
    }
}

/// <summary><see cref="ArtifactRef"/> 的守卫与结构化相等。 </summary>
[TestClass]
public sealed class ArtifactRefContractTests
{
    [TestMethod]
    public void Create_Valid_ExposesKindIdVersionAndHandle()
    {
        var reference = ArtifactRef.Create(ArtifactKind.Skill, "self-evolution", "1.0.3");

        Assert.AreEqual(ArtifactKind.Skill, reference.Kind);
        Assert.AreEqual("self-evolution", reference.Id);
        Assert.AreEqual("1.0.3", reference.Version);
        Assert.AreEqual("Skill:self-evolution@1.0.3", reference.Handle);
    }

    [TestMethod]
    public void Create_UnknownKind_IsRejected_BeforeItBecomesUnrouteableData()
    {
        AssertRejected(() => ArtifactRef.Create(ArtifactKind.Unknown, "x"));
    }

    [TestMethod]
    public void Create_BlankId_IsRejected_BecauseHandleCannotBeAddressedOrRolledBack()
    {
        AssertRejected(() => ArtifactRef.Create(ArtifactKind.Skill, "   "));
    }

    /// <summary>空白版本必须规范化为 null，否则会产出 `kind:id@` 这种"看起来有版本"的假句柄。</summary>
    [TestMethod]
    public void Create_BlankVersion_IsNormalizedToNull_AndHandleDropsTheAtSign()
    {
        var reference = ArtifactRef.Create(ArtifactKind.MemoryChapter, "chapter-1", "  ");

        Assert.IsNull(reference.Version);
        Assert.AreEqual("MemoryChapter:chapter-1", reference.Handle);
    }

    /// <summary>
    /// 显式结构化相等：record 自动相等对集合成员用**引用相等**，
    /// 两份内容相同的证据列表原本会被判为不等 —— 那会让去重与幂等键静默失效。
    /// </summary>
    [TestMethod]
    public void Equality_ComparesEvidenceRefsByContent_NotByListReference()
    {
        var first = ArtifactRef.Create(ArtifactKind.Skill, "s", "1.0.0", ["ev-a", "ev-b"]);
        var second = ArtifactRef.Create(ArtifactKind.Skill, "s", "1.0.0", ["ev-a", "ev-b"]);
        var different = ArtifactRef.Create(ArtifactKind.Skill, "s", "1.0.0", ["ev-a"]);

        Assert.AreNotSame(first.EvidenceRefs, second.EvidenceRefs);
        Assert.AreEqual(first, second);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
        Assert.AreNotEqual(first, different);
    }
}

/// <summary><see cref="ImprovementProposal"/> 的守卫：把「add 不是默认」做成字段级强制。</summary>
[TestClass]
public sealed class ImprovementProposalContractTests
{
    /// <summary>本切片最核心的一条：Create 必须自证"既有资产为什么不能更新或合并"。</summary>
    [TestMethod]
    public void Create_WithoutWhyNotUpdateOrMerge_IsRejected()
    {
        AssertRejected(() => ImprovementProposal.Create(
            ImprovementOperation.Create,
            proposedBy: "agent:test",
            idempotencyKey: "k1",
            evidenceRefs: ["ev-1"],
            payload: "new skill body",
            whyNotUpdateOrMerge: null));
    }

    [TestMethod]
    public void Create_WithWhyTextButNoTarget_IsAccepted()
    {
        var proposal = ImprovementProposal.Create(
            ImprovementOperation.Create,
            proposedBy: "agent:test",
            idempotencyKey: "k1",
            evidenceRefs: ["ev-1"],
            payload: "new skill body",
            whyNotUpdateOrMerge: "家族内既有技能关键词冲突，无法在原技能上表达该程序");

        Assert.AreEqual(ImprovementOperation.Create, proposal.Operation);
        Assert.IsNull(proposal.Target);
    }

    [TestMethod]
    public void Create_WithTarget_IsRejected_BecauseTheTwoSemanticsContradict()
    {
        var target = ArtifactRef.Create(ArtifactKind.Skill, "existing");

        AssertRejected(() => ImprovementProposal.Create(
            ImprovementOperation.Create,
            proposedBy: "agent:test",
            idempotencyKey: "k1",
            evidenceRefs: ["ev-1"],
            target: target,
            payload: "body",
            whyNotUpdateOrMerge: "reason"));
    }

    [TestMethod]
    public void Create_WithoutPayload_IsRejected()
    {
        AssertRejected(() => ImprovementProposal.Create(
            ImprovementOperation.Create,
            proposedBy: "agent:test",
            idempotencyKey: "k1",
            evidenceRefs: ["ev-1"],
            payload: null,
            whyNotUpdateOrMerge: "reason"));
    }

    /// <summary>合并路径必须可表达 —— 否则"整合而非新增"就只是口号。</summary>
    [TestMethod]
    public void Merge_WithTargetAndPayload_IsAccepted()
    {
        var target = ArtifactRef.Create(ArtifactKind.Skill, "voice-family", "1.0.0");

        var proposal = ImprovementProposal.Create(
            ImprovementOperation.Merge,
            proposedBy: "subconscious:skill.curate",
            idempotencyKey: "curate:2026-09-21",
            evidenceRefs: ["turn-1", "turn-2", "turn-3"],
            target: target,
            payload: "提炼后的通用程序：适用条件 / 步骤 / 陷阱");

        Assert.AreEqual(ImprovementOperation.Merge, proposal.Operation);
        Assert.AreEqual(target, proposal.Target);
    }

    [TestMethod]
    public void Merge_WithoutTarget_IsRejected()
    {
        AssertRejected(() => ImprovementProposal.Create(
            ImprovementOperation.Merge,
            proposedBy: "agent:test",
            idempotencyKey: "k1",
            evidenceRefs: ["ev-1"],
            target: null,
            payload: "body"));
    }

    /// <summary>有 target 时不存在"该不该新增"的疑问，两处同时表达会让事后解释产生歧义。</summary>
    [TestMethod]
    public void NonCreate_WithWhyNotUpdateOrMerge_IsRejected()
    {
        var target = ArtifactRef.Create(ArtifactKind.Skill, "existing");

        AssertRejected(() => ImprovementProposal.Create(
            ImprovementOperation.Update,
            proposedBy: "agent:test",
            idempotencyKey: "k1",
            evidenceRefs: ["ev-1"],
            target: target,
            payload: "new body",
            whyNotUpdateOrMerge: "should not be here"));
    }

    [TestMethod]
    public void Update_WithoutPayload_IsRejected_BecauseEmptyChangeIsNotAChange()
    {
        var target = ArtifactRef.Create(ArtifactKind.Skill, "existing");

        AssertRejected(() => ImprovementProposal.Create(
            ImprovementOperation.Update,
            proposedBy: "agent:test",
            idempotencyKey: "k1",
            evidenceRefs: ["ev-1"],
            target: target,
            payload: "   "));
    }

    [TestMethod]
    public void Retire_WithTargetAndNoPayload_IsAccepted()
    {
        var proposal = ImprovementProposal.Create(
            ImprovementOperation.Retire,
            proposedBy: "subconscious:skill.curate",
            idempotencyKey: "retire:voice-panel",
            evidenceRefs: ["metric:zero-hits-90d"],
            target: ArtifactRef.Create(ArtifactKind.Skill, "voice-panel"));

        Assert.AreEqual(ImprovementOperation.Retire, proposal.Operation);
        Assert.IsNull(proposal.Payload);
    }

    [TestMethod]
    public void Retire_WithPayload_IsRejected_BecauseRetireHasOnlyOneMeaning()
    {
        AssertRejected(() => ImprovementProposal.Create(
            ImprovementOperation.Retire,
            proposedBy: "agent:test",
            idempotencyKey: "k1",
            evidenceRefs: ["ev-1"],
            target: ArtifactRef.Create(ArtifactKind.Skill, "existing"),
            payload: "extra content"));
    }

    [TestMethod]
    public void UnknownOperation_IsRejected()
    {
        AssertRejected(() => ImprovementProposal.Create(
            ImprovementOperation.Unknown,
            proposedBy: "agent:test",
            idempotencyKey: "k1",
            evidenceRefs: ["ev-1"],
            target: ArtifactRef.Create(ArtifactKind.Skill, "existing"),
            payload: "body"));
    }

    /// <summary>无证据的变更不进入裁决：否则提案者的自述就是全部理由。</summary>
    [TestMethod]
    public void EmptyEvidence_IsRejected_ForEveryOperation()
    {
        AssertRejected(() => ImprovementProposal.Create(
            ImprovementOperation.Create,
            proposedBy: "agent:test",
            idempotencyKey: "k1",
            evidenceRefs: [],
            payload: "body",
            whyNotUpdateOrMerge: "reason"));

        AssertRejected(() => ImprovementProposal.Create(
            ImprovementOperation.Retire,
            proposedBy: "agent:test",
            idempotencyKey: "k2",
            evidenceRefs: [],
            target: ArtifactRef.Create(ArtifactKind.Skill, "existing")));
    }

    [TestMethod]
    public void BlankProposedBy_OrBlankIdempotencyKey_IsRejected()
    {
        AssertRejected(() => ImprovementProposal.Create(
            ImprovementOperation.Retire,
            proposedBy: " ",
            idempotencyKey: "k1",
            evidenceRefs: ["ev-1"],
            target: ArtifactRef.Create(ArtifactKind.Skill, "existing")));

        AssertRejected(() => ImprovementProposal.Create(
            ImprovementOperation.Retire,
            proposedBy: "agent:test",
            idempotencyKey: "  ",
            evidenceRefs: ["ev-1"],
            target: ArtifactRef.Create(ArtifactKind.Skill, "existing")));
    }

    [TestMethod]
    public void NonFiniteExpectedGain_IsRejected_InsteadOfSilentlyFailingLater()
    {
        AssertRejected(() => ImprovementProposal.Create(
            ImprovementOperation.Retire,
            proposedBy: "agent:test",
            idempotencyKey: "k1",
            evidenceRefs: ["ev-1"],
            target: ArtifactRef.Create(ArtifactKind.Skill, "existing"),
            expectedGain: double.NaN));
    }

    [TestMethod]
    public void Equality_ComparesEvidenceRefsByContent()
    {
        var target = ArtifactRef.Create(ArtifactKind.Skill, "existing");
        var first = ImprovementProposal.Create(
            ImprovementOperation.Retire, "agent:test", "k1", ["ev-1", "ev-2"], target);
        var second = ImprovementProposal.Create(
            ImprovementOperation.Retire, "agent:test", "k1", ["ev-1", "ev-2"], target);

        Assert.AreEqual(first, second);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
    }
}

/// <summary><see cref="ChangeVerdict"/> 的守卫：Apply 必须可回滚，Unknown 不得当成放行。</summary>
[TestClass]
public sealed class ChangeVerdictContractTests
{
    [TestMethod]
    public void Apply_WithRollbackHandle_IsAccepted()
    {
        var verdict = ChangeVerdict.Create(
            ChangeDecision.Apply, "improvement.portfolio", 3, "within_budget", "rollback:set-enabled:true");

        Assert.AreEqual(ChangeDecision.Apply, verdict.Decision);
        Assert.AreEqual(3, verdict.PolicyVersion);
        Assert.AreEqual("rollback:set-enabled:true", verdict.RollbackHandle);
    }

    /// <summary>不可回滚的自动变更不允许存在：缺句柄即拒绝应用。</summary>
    [TestMethod]
    public void Apply_WithoutRollbackHandle_IsRejected()
    {
        AssertRejected(() => ChangeVerdict.Create(
            ChangeDecision.Apply, "improvement.portfolio", 1, "within_budget", rollbackHandle: null));
    }

    [TestMethod]
    public void NonApplyDecisions_WithRollbackHandle_AreRejected()
    {
        AssertRejected(() => ChangeVerdict.Create(
            ChangeDecision.ApplyShadow, "p", 1, "dry_run", rollbackHandle: "rollback:x"));
        AssertRejected(() => ChangeVerdict.Create(
            ChangeDecision.Reject, "p", 1, "unexpected_failures", rollbackHandle: "rollback:x"));
        AssertRejected(() => ChangeVerdict.Create(
            ChangeDecision.Defer, "p", 1, "budget_exhausted", rollbackHandle: "rollback:x"));
    }

    [TestMethod]
    public void UnknownDecision_IsRejected_BecauseItMustNotBeReadAsPermission()
    {
        AssertRejected(() => ChangeVerdict.Create(
            ChangeDecision.Unknown, "p", 1, "whatever", rollbackHandle: null));
    }

    [TestMethod]
    public void BlankPolicyId_BlankReasonCode_OrNonPositiveVersion_IsRejected()
    {
        AssertRejected(() => ChangeVerdict.Create(
            ChangeDecision.Reject, "  ", 1, "reason", rollbackHandle: null));
        AssertRejected(() => ChangeVerdict.Create(
            ChangeDecision.Reject, "p", 1, "  ", rollbackHandle: null));
        AssertRejected(() => ChangeVerdict.Create(
            ChangeDecision.Reject, "p", 0, "reason", rollbackHandle: null));
    }
}

/// <summary>
/// 守卫断言助手：不依赖 MSTest 的异常断言 API 版本，直接检查"构造期是否抛错"。
/// </summary>
internal static class GuardAssertions
{
    /// <summary>
    /// 断言该动作被**构造期**拒绝。
    /// <para>
    /// 接受 <see cref="ArgumentException"/> 及其派生（<see cref="ArgumentOutOfRangeException"/>）：
    /// 本切片的纪律是「非法配置不静默」，具体异常类型不是契约的一部分。
    /// </para>
    /// </summary>
    public static void AssertRejected(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        Assert.Fail("非法输入应当在构造期被拒绝（ArgumentException），但实际上没有抛异常 —— 它被静默接受了。");
    }
}

