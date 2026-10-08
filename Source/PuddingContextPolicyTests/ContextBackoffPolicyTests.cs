using PuddingContextPolicy;

namespace PuddingContextPolicyTests;

/// <summary>
/// 退避/去重判定。硬不变量：**退避绝不能屏蔽硬预算保护**。
/// </summary>
[TestClass]
public sealed class ContextBackoffPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private static CandidateMaintenanceRecord Record(
        string outcome,
        string fingerprint = "fp-1",
        long generation = 7,
        string policy = ContextPolicyVersion.Current,
        int ageMinutes = 1) =>
        new(fingerprint, generation, policy, outcome, Now.AddMinutes(-ageMinutes));

    /// <summary>硬保护优先于任何退避：即使刚刚有过同候选的无收益记录，也必须尝试。</summary>
    [TestMethod]
    public void Decide_HardProtectionOverridesBackoff()
    {
        var decision = ContextBackoffPolicy.Decide(
            Record(CandidateMaintenanceOutcomes.NoGain, ageMinutes: 0),
            fingerprint: "fp-1",
            generation: 7,
            policyVersion: ContextPolicyVersion.Current,
            now: Now,
            minRetryInterval: Interval,
            hardProtectionRequired: true);

        Assert.IsTrue(decision.ShouldAttempt);
        Assert.AreEqual("hard_protection_overrides_backoff", decision.Reason);
    }

    [TestMethod]
    public void Decide_NoPreviousRecordAttempts()
    {
        var decision = ContextBackoffPolicy.Decide(
            lastRecord: null, "fp-1", 7, ContextPolicyVersion.Current, Now, Interval,
            hardProtectionRequired: false);

        Assert.IsTrue(decision.ShouldAttempt);
        Assert.AreEqual("no_previous_record", decision.Reason);
    }

    [TestMethod]
    [DataRow(CandidateMaintenanceOutcomes.NoGain)]
    [DataRow(CandidateMaintenanceOutcomes.Failed)]
    public void Decide_SameCandidateWithinIntervalIsSuppressed(string outcome)
    {
        var decision = ContextBackoffPolicy.Decide(
            Record(outcome, ageMinutes: 1), "fp-1", 7, ContextPolicyVersion.Current, Now, Interval,
            hardProtectionRequired: false);

        Assert.IsFalse(decision.ShouldAttempt);
        Assert.AreEqual("same_candidate_backoff", decision.Reason);
    }

    [TestMethod]
    public void Decide_IntervalExpiredAttemptsAgain()
    {
        var decision = ContextBackoffPolicy.Decide(
            Record(CandidateMaintenanceOutcomes.NoGain, ageMinutes: 30), "fp-1", 7,
            ContextPolicyVersion.Current, Now, Interval, hardProtectionRequired: false);

        Assert.IsTrue(decision.ShouldAttempt);
        Assert.AreEqual("backoff_expired", decision.Reason);
    }

    [TestMethod]
    public void Decide_NewCandidateFingerprintAttemptsAgain()
    {
        var decision = ContextBackoffPolicy.Decide(
            Record(CandidateMaintenanceOutcomes.NoGain, fingerprint: "fp-old"),
            fingerprint: "fp-new",
            generation: 7,
            policyVersion: ContextPolicyVersion.Current,
            now: Now,
            minRetryInterval: Interval,
            hardProtectionRequired: false);

        Assert.IsTrue(decision.ShouldAttempt);
        Assert.AreEqual("candidate_changed", decision.Reason);
    }

    [TestMethod]
    public void Decide_GenerationChangeAttemptsAgain()
    {
        var decision = ContextBackoffPolicy.Decide(
            Record(CandidateMaintenanceOutcomes.NoGain, generation: 6),
            fingerprint: "fp-1",
            generation: 7,
            policyVersion: ContextPolicyVersion.Current,
            now: Now,
            minRetryInterval: Interval,
            hardProtectionRequired: false);

        Assert.IsTrue(decision.ShouldAttempt);
        Assert.AreEqual("candidate_changed", decision.Reason);
    }

    /// <summary>
    /// 同候选、同代次上的 applied 记录说明 generation 没被推进（成功应用应推进代次）
    /// ⇒ 该记录不可信，不得据此静默跳过。
    /// </summary>
    [TestMethod]
    public void Decide_AppliedRecordWithoutGenerationAdvanceDoesNotSuppress()
    {
        var decision = ContextBackoffPolicy.Decide(
            Record(CandidateMaintenanceOutcomes.Applied, ageMinutes: 0), "fp-1", 7,
            ContextPolicyVersion.Current, Now, Interval, hardProtectionRequired: false);

        Assert.IsTrue(decision.ShouldAttempt);
        Assert.AreEqual("applied_record_without_generation_advance", decision.Reason);
    }

    /// <summary>策略版本变化让旧退避记录失效（策略语义已变）。</summary>
    [TestMethod]
    public void Decide_PolicyVersionChangeAttemptsAgain()
    {
        var decision = ContextBackoffPolicy.Decide(
            Record(CandidateMaintenanceOutcomes.NoGain, policy: "context-policy/0"),
            fingerprint: "fp-1",
            generation: 7,
            policyVersion: ContextPolicyVersion.Current,
            now: Now,
            minRetryInterval: Interval,
            hardProtectionRequired: false);

        Assert.IsTrue(decision.ShouldAttempt);
        Assert.AreEqual("candidate_changed", decision.Reason);
    }
}
