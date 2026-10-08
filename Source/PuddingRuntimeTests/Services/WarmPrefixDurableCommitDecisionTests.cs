using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// F2 落库决策的**纯判定**单测（脱离执行宿主）：什么时候允许尝试持久化、不能时如何如实降级。
/// 硬约束：跨度里有任何「没有持久行」的消息就不许写覆盖标注（错标 = 静默隐藏真实消息）。
/// </summary>
[TestClass]
public sealed class WarmPrefixDurableCommitDecisionTests
{
    private static DurableSpanResolution Resolved(params string[] ids) => new(true, ids, 0);

    private static DurableSpanResolution PartiallyResolved(int unresolved, params string[] ids)
        => new(false, ids, unresolved);

    [TestMethod]
    public void ShouldAttempt_OnlyWhenServiceAvailableAndSpanFullyResolved()
    {
        Assert.IsTrue(WarmPrefixDurableCommitDecision.ShouldAttempt(true, Resolved("m1", "m2")));

        Assert.IsFalse(
            WarmPrefixDurableCommitDecision.ShouldAttempt(false, Resolved("m1")),
            "没有可用的压缩服务时只能内存-only");
        Assert.IsFalse(
            WarmPrefixDurableCommitDecision.ShouldAttempt(true, PartiallyResolved(1, "m1")),
            "跨度未全部解析时绝不允许写覆盖标注（不得部分标注）");
        Assert.IsFalse(
            WarmPrefixDurableCommitDecision.ShouldAttempt(true, Resolved()),
            "没有可覆盖的行时不做无意义的空提交");
    }

    [TestMethod]
    public void ResolveSkipReason_IsExplicitAndCarriesUnresolvedCount()
    {
        Assert.AreEqual(
            WarmPrefixDurableCommitDecision.ServiceUnavailable,
            WarmPrefixDurableCommitDecision.ResolveSkipReason(false, PartiallyResolved(2, "m1")));
        Assert.AreEqual(
            "unresolved_durable_identities:3",
            WarmPrefixDurableCommitDecision.ResolveSkipReason(true, PartiallyResolved(3, "m1")));
        Assert.AreEqual(
            "unresolved_durable_identities:0",
            WarmPrefixDurableCommitDecision.ResolveSkipReason(true, Resolved()),
            "全解析但无可覆盖行时仍需明确原因码，不得静默");
        Assert.IsNull(WarmPrefixDurableCommitDecision.ResolveSkipReason(true, Resolved("m1")));
    }

    [TestMethod]
    public void Interpret_ReportsCommittedAsDurable_AndEveryFailureAsDegraded()
    {
        var committed = WarmPrefixDurableCommitDecision.Interpret(
            new WarmPrefixCheckpointCommitResult(WarmPrefixCheckpointCommitStatus.Committed, "summary-1"));
        Assert.IsTrue(committed.DurableCheckpoint);
        Assert.IsNull(committed.SkipReason);

        var mismatch = WarmPrefixDurableCommitDecision.Interpret(
            new WarmPrefixCheckpointCommitResult(WarmPrefixCheckpointCommitStatus.GenerationMismatch));
        Assert.IsFalse(mismatch.DurableCheckpoint);
        Assert.AreEqual("durable_commit_generationmismatch", mismatch.SkipReason);

        var unsupported = WarmPrefixDurableCommitDecision.Interpret(
            new WarmPrefixCheckpointCommitResult(WarmPrefixCheckpointCommitStatus.Unsupported));
        Assert.IsFalse(unsupported.DurableCheckpoint);
        Assert.AreEqual("durable_commit_unsupported", unsupported.SkipReason);

        var missing = WarmPrefixDurableCommitDecision.Interpret(
            new WarmPrefixCheckpointCommitResult(
                WarmPrefixCheckpointCommitStatus.CoveredMessagesNotFound,
                ProblemMessageIds: ["gone"]));
        Assert.IsFalse(missing.DurableCheckpoint);
        Assert.AreEqual("durable_commit_coveredmessagesnotfound", missing.SkipReason);
    }
}
