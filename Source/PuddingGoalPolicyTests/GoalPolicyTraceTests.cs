using PuddingGoalPolicy;
using PuddingGoalPolicy.Contracts;

namespace PuddingGoalPolicyTests;

/// <summary>PA-04 minimal-loop traces: the policy must never turn "step done", a model Accept or a stream end
/// into Goal completion; repeated settlement must reuse one stable next-round key.</summary>
[TestClass]
public sealed class GoalPolicyTraceTests
{
    private static readonly GoalContinuationPolicy Policy = new();

    [TestMethod]
    public void Trace_Chat_FirstRoundEnds_WithoutReviewOrSecondRound()
    {
        var decision = Policy.Decide(GoalFixtures.Policy(mode: InteractionMode.Chat),
            GoalFixtures.Facts(review: ReviewFact.NotRequired, unmet: true, evidenceSufficient: false));
        Assert.AreEqual(GoalPolicyDecisionKind.Stop, decision.Kind);
        Assert.IsFalse(decision.CreateNextRound);
        Assert.AreEqual(GoalPolicyErrorCodes.ChatNoContinuation, decision.ReasonCode);
    }

    [TestMethod]
    public void Trace_Goal_MissingDeliverable_ContinuesOnce_ThenCompletes()
    {
        var policy = GoalFixtures.Policy();
        var roundOne = Policy.Decide(policy, GoalFixtures.Facts(review: ReviewFact.NeedsWork, unmet: true)
            with { IterationNo = 2 });
        Assert.AreEqual(GoalPolicyDecisionKind.Continue, roundOne.Kind, "A missing deliverable must produce exactly one repair round.");
        Assert.IsTrue(roundOne.CreateNextRound);

        var roundTwo = Policy.Decide(policy, GoalFixtures.Facts(review: ReviewFact.Satisfied)
            with { IterationNo = 3 });
        Assert.AreEqual(GoalPolicyDecisionKind.Complete, roundTwo.Kind);
        Assert.IsTrue(roundTwo.IsTerminal);
    }

    [TestMethod]
    public void Trace_SatisfiedReviewFromAnOldVersion_CannotCompleteTheNewGoal()
    {
        var decision = Policy.Decide(GoalFixtures.Policy(),
            GoalFixtures.Facts(review: ReviewFact.Satisfied, versionChanged: true));
        Assert.AreEqual(GoalPolicyDecisionKind.Wait, decision.Kind);
        Assert.AreNotEqual(GoalPolicyDecisionKind.Complete, decision.Kind);
        Assert.AreEqual(GoalPolicyErrorCodes.ObjectiveVersionChanged, decision.ReasonCode);
    }

    [TestMethod]
    public void Trace_RepeatedSettlement_ProducesOneStableNextRoundKey()
    {
        var policy = GoalFixtures.Policy();
        var facts = GoalFixtures.Facts(review: ReviewFact.NeedsWork, unmet: true, repairUsed: 1);
        var first = Policy.Decide(policy, facts);
        var second = Policy.Decide(policy, facts);
        Assert.AreEqual(first, second, "The pure policy is idempotent for the same facts.");
        Assert.AreEqual(Policy.NextRoundKey(policy, facts), Policy.NextRoundKey(policy, facts),
            "The next-round key must not depend on polling counters.");
    }

    [TestMethod]
    public void Trace_UnknownReview_IsNeitherAcceptNorReject()
    {
        var decision = Policy.Decide(GoalFixtures.Policy(), GoalFixtures.Facts(review: ReviewFact.Unknown));
        Assert.AreEqual(GoalPolicyDecisionKind.Wait, decision.Kind);
        Assert.AreNotEqual(GoalPolicyDecisionKind.Complete, decision.Kind);
        Assert.AreNotEqual(GoalPolicyDecisionKind.Stop, decision.Kind);
        Assert.AreEqual(GoalPolicyErrorCodes.ReviewUnknown, decision.ReasonCode);
    }

    [TestMethod]
    public void Trace_BudgetExhausted_StopsWithoutInfiniteRetry()
    {
        var policy = GoalFixtures.Policy(repairLimit: 10);
        var facts = GoalFixtures.Facts(budgetExhausted: true, review: ReviewFact.NeedsWork, unmet: true);
        var first = Policy.Decide(policy, facts);
        var second = Policy.Decide(policy, facts);
        Assert.AreEqual(GoalPolicyDecisionKind.Stop, first.Kind);
        Assert.AreEqual(GoalPolicyErrorCodes.BudgetExhausted, first.ReasonCode);
        Assert.AreEqual(first, second);
        Assert.IsFalse(first.CreateNextRound);
    }

    [TestMethod]
    public void Trace_TruncatedOrInsufficientEvidence_NeverPasses()
    {
        var decision = Policy.Decide(GoalFixtures.Policy(),
            GoalFixtures.Facts(review: ReviewFact.Unknown, evidenceSufficient: false));
        Assert.AreNotEqual(GoalPolicyDecisionKind.Complete, decision.Kind);
        Assert.AreEqual(GoalPolicyErrorCodes.ReviewUnknown, decision.ReasonCode);
    }
}
