using PuddingContextPolicy;

namespace PuddingContextPolicyTests;

/// <summary>
/// 收益准入：**必须用真实摘要长度重算 after**，计划阶段不含摘要的估算不得充当提交后的值。
/// </summary>
[TestClass]
public sealed class ContextNetGainAdmissionTests
{
    private const int EffectiveLimit = 605_760;

    [TestMethod]
    public void Evaluate_UsesRealSummaryTokens_ForProjectedAfter()
    {
        // 事故第一条的可比口径：压缩前 583,691、被替换跨度 257,299（= 583,691 − 计划保留 326,392）、
        // Provider 报的摘要输出 4,343 tokens。
        var decision = ContextNetGainAdmission.Evaluate(new NetGainInputs(
            InitialUsedTokens: 583_691,
            RemovedTokens: 257_299,
            SummaryTokens: 4_343,
            EffectiveInputTokens: EffectiveLimit));

        Assert.AreEqual(NetGainVerdict.Accepted, decision.Verdict);
        Assert.IsTrue(decision.CanCommit);
        // after 必须由真实摘要重算：583,691 − 257,299 + 4,343。
        Assert.AreEqual(330_735, decision.ProjectedAfterUsedTokens);
        Assert.AreEqual(252_956, decision.ReclaimTokens);

        // 如实记录未达目标：工具定义（67,533）与受保护尾部不可回收，0.5 × 605,760 = 302,880
        // 达不到 ⇒ 事件与日志不得谎报「达标」。
        Assert.IsFalse(decision.MeetsTarget);
        Assert.AreEqual("accepted_below_target", decision.Reason);
    }

    /// <summary>
    /// 计划估算（不含摘要）比真实 after 小：把计划值当 after 会低报占用。
    /// 本用例固定两者的差额来源，防止回退。
    /// </summary>
    [TestMethod]
    public void Evaluate_ProjectedAfterIsLargerThanPlanEstimateExcludingSummary()
    {
        const int initial = 583_691;
        const int removed = 100_000;
        const int summary = 30_000;

        var decision = ContextNetGainAdmission.Evaluate(new NetGainInputs(
            initial, removed, summary, EffectiveLimit));

        var planEstimateExcludingSummary = initial - removed;
        Assert.AreEqual(planEstimateExcludingSummary + summary, decision.ProjectedAfterUsedTokens);
        Assert.IsGreaterThan(planEstimateExcludingSummary, decision.ProjectedAfterUsedTokens);
    }

    [TestMethod]
    public void Evaluate_EmptyOrNonShrinkingSummaryIsNoGain()
    {
        var empty = ContextNetGainAdmission.Evaluate(new NetGainInputs(583_691, 100_000, 0, EffectiveLimit));
        Assert.AreEqual(NetGainVerdict.NoGain, empty.Verdict);
        Assert.AreEqual("summary_empty", empty.Reason);
        Assert.IsFalse(empty.CanCommit);

        var notSmaller = ContextNetGainAdmission.Evaluate(new NetGainInputs(583_691, 100_000, 100_000, EffectiveLimit));
        Assert.AreEqual(NetGainVerdict.NoGain, notSmaller.Verdict);
        Assert.AreEqual("summary_not_smaller_than_span", notSmaller.Reason);

        var bigger = ContextNetGainAdmission.Evaluate(new NetGainInputs(583_691, 100_000, 120_000, EffectiveLimit));
        Assert.AreEqual(NetGainVerdict.NoGain, bigger.Verdict);
    }

    /// <summary>加入摘要后仍越界 ⇒ 不得提交（提交只会把越界写进历史）。</summary>
    [TestMethod]
    public void Evaluate_SmallReclaimThatStillExceedsHardLimitIsRejected()
    {
        var decision = ContextNetGainAdmission.Evaluate(new NetGainInputs(
            InitialUsedTokens: 625_824,
            RemovedTokens: 1_000,
            SummaryTokens: 900,
            EffectiveInputTokens: EffectiveLimit));

        Assert.AreEqual(NetGainVerdict.ExceedsHardLimit, decision.Verdict);
        Assert.IsFalse(decision.CanCommit);
        Assert.AreEqual("after_summary_exceeds_hard_limit", decision.Reason);
    }

    /// <summary>有净收益但未达压实目标：允许提交，但必须如实记录（不得谎报达标）。</summary>
    [TestMethod]
    public void Evaluate_AcceptedButBelowTargetIsRecordedHonestly()
    {
        var decision = ContextNetGainAdmission.Evaluate(new NetGainInputs(
            InitialUsedTokens: 600_000,
            RemovedTokens: 10_000,
            SummaryTokens: 1_000,
            EffectiveInputTokens: EffectiveLimit,
            TargetRatio: 0.50));

        Assert.AreEqual(NetGainVerdict.Accepted, decision.Verdict);
        Assert.IsTrue(decision.CanCommit);
        Assert.IsFalse(decision.MeetsTarget);
        Assert.AreEqual("accepted_below_target", decision.Reason);
    }
}
