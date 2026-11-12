using PuddingGoalPolicy;
using PuddingGoalPolicy.Contracts;

namespace PuddingGoalPolicyTests;

[TestClass]
public sealed class GoalPolicyTests
{
    private static readonly GoalContinuationPolicy Policy = new();

    [TestMethod]
    public void CancelledPausedOrRevoked_WinsOverEverything_AndCreatesNoRound()
    {
        foreach (var (facts, code) in new[]
                 {
                     (GoalFixtures.Facts(cancelled: true), GoalPolicyErrorCodes.Cancelled),
                     (GoalFixtures.Facts(paused: true), GoalPolicyErrorCodes.Paused),
                     (GoalFixtures.Facts(revoked: true), GoalPolicyErrorCodes.AuthorizationRevoked),
                 })
        {
            var decision = Policy.Decide(GoalFixtures.Policy(), facts);
            Assert.AreEqual(GoalPolicyDecisionKind.Stop, decision.Kind);
            Assert.AreEqual(code, decision.ReasonCode);
            Assert.IsFalse(decision.CreateNextRound);
            Assert.IsFalse(decision.IsTerminal);
        }
    }

    [TestMethod]
    public void Chat_EndsAfterOneTurn_WithoutAReviewOrSecondRound()
    {
        var decision = Policy.Decide(GoalFixtures.Policy(mode: InteractionMode.Chat), GoalFixtures.Facts());
        Assert.AreEqual(GoalPolicyDecisionKind.Stop, decision.Kind);
        Assert.AreEqual(GoalPolicyErrorCodes.ChatNoContinuation, decision.ReasonCode);
        Assert.IsFalse(decision.CreateNextRound);
    }

    [TestMethod]
    public void StaleVersionOrChangedObjective_WaitsForCurrentEvidence_AndNeverReusesTheOldPass()
    {
        var stale = Policy.Decide(GoalFixtures.Policy(), GoalFixtures.Facts(stale: true));
        Assert.AreEqual(GoalPolicyDecisionKind.Wait, stale.Kind);
        Assert.AreEqual(GoalPolicyErrorCodes.ReviewStale, stale.ReasonCode);
        Assert.IsFalse(stale.CreateNextRound);

        var changed = Policy.Decide(GoalFixtures.Policy(), GoalFixtures.Facts(versionChanged: true));
        Assert.AreEqual(GoalPolicyDecisionKind.Wait, changed.Kind);
        Assert.AreEqual(GoalPolicyErrorCodes.ObjectiveVersionChanged, changed.ReasonCode);
    }

    [TestMethod]
    public void RequiredReviewMissingPendingOrUnknown_WaitsInsteadOfDefaulting()
    {
        foreach (var (review, code) in new[]
                 {
                     (ReviewFact.Missing, GoalPolicyErrorCodes.ReviewMissing),
                     (ReviewFact.Pending, GoalPolicyErrorCodes.ReviewPending),
                     (ReviewFact.Unknown, GoalPolicyErrorCodes.ReviewUnknown),
                 })
        {
            var decision = Policy.Decide(GoalFixtures.Policy(), GoalFixtures.Facts(review: review));
            Assert.AreEqual(GoalPolicyDecisionKind.Wait, decision.Kind, review.ToString());
            Assert.AreEqual(code, decision.ReasonCode);
            Assert.IsFalse(decision.CreateNextRound);
        }
    }

    [TestMethod]
    public void ReviewNotRequired_UnknownReview_CanStillComplete_WhenEvidenceIsSufficient()
    {
        var decision = Policy.Decide(GoalFixtures.Policy(reviewRequired: false),
            GoalFixtures.Facts(review: ReviewFact.Unknown));
        Assert.AreEqual(GoalPolicyDecisionKind.Complete, decision.Kind);
        Assert.AreEqual(GoalPolicyErrorCodes.Completed, decision.ReasonCode);
        Assert.IsTrue(decision.IsTerminal);
    }

    [TestMethod]
    public void BudgetOrRoundExhaustion_Stops_WithoutGrantingMore()
    {
        var budget = Policy.Decide(GoalFixtures.Policy(), GoalFixtures.Facts(budgetExhausted: true));
        Assert.AreEqual(GoalPolicyDecisionKind.Stop, budget.Kind);
        Assert.AreEqual(GoalPolicyErrorCodes.BudgetExhausted, budget.ReasonCode);
        Assert.IsFalse(budget.CreateNextRound);

        var rounds = Policy.Decide(GoalFixtures.Policy(), GoalFixtures.Facts(maxRounds: true));
        Assert.AreEqual(GoalPolicyErrorCodes.RoundsExhausted, rounds.ReasonCode);
    }

    [TestMethod]
    public void NeedsUser_MapsToWaitWithUserAction()
    {
        var decision = Policy.Decide(GoalFixtures.Policy(), GoalFixtures.Facts(review: ReviewFact.NeedsUser));
        Assert.AreEqual(GoalPolicyDecisionKind.Wait, decision.Kind);
        Assert.IsTrue(decision.RequiresUser);
        Assert.AreEqual(GoalPolicyErrorCodes.ReviewNeedsUser, decision.ReasonCode);
    }

    [TestMethod]
    public void Complete_RequiresSufficientEvidenceAndNoUnresolvedFacts()
    {
        var complete = Policy.Decide(GoalFixtures.Policy(), GoalFixtures.Facts());
        Assert.AreEqual(GoalPolicyDecisionKind.Complete, complete.Kind);

        var insufficient = Policy.Decide(GoalFixtures.Policy(), GoalFixtures.Facts(evidenceSufficient: false));
        Assert.AreNotEqual(GoalPolicyDecisionKind.Complete, insufficient.Kind);

        var unresolved = Policy.Decide(GoalFixtures.Policy(), GoalFixtures.Facts(noUnresolved: false));
        Assert.AreNotEqual(GoalPolicyDecisionKind.Complete, unresolved.Kind);

        var unmet = Policy.Decide(GoalFixtures.Policy(), GoalFixtures.Facts(unmet: true));
        Assert.AreNotEqual(GoalPolicyDecisionKind.Complete, unmet.Kind);
    }

    [TestMethod]
    public void ModelAccept_WithInsufficientEvidence_IsNotCompletion()
    {
        var decision = Policy.Decide(GoalFixtures.Policy(),
            GoalFixtures.Facts(review: ReviewFact.Satisfied, evidenceSufficient: false, hasExecutableAction: true));
        Assert.AreNotEqual(GoalPolicyDecisionKind.Complete, decision.Kind);
        Assert.IsFalse(decision.IsTerminal);
    }

    [TestMethod]
    public void RepairWithinLimit_ContinuesAtomicallyWithTheNextAction()
    {
        var decision = Policy.Decide(GoalFixtures.Policy(repairLimit: 2),
            GoalFixtures.Facts(review: ReviewFact.NeedsWork, unmet: true, repairUsed: 0));
        Assert.AreEqual(GoalPolicyDecisionKind.Continue, decision.Kind);
        Assert.IsTrue(decision.CreateNextRound);
        Assert.AreEqual("next-action-1", decision.NextActionId);
        Assert.AreEqual(GoalPolicyErrorCodes.ContinueRepair, decision.ReasonCode);
        Assert.IsTrue(decision.RetryOnRestart);
    }

    [TestMethod]
    public void RepairWithoutExplicitAllowance_OrAfterLimit_DoesNotAutoRepair()
    {
        var notAllowed = Policy.Decide(GoalFixtures.Policy(mode: InteractionMode.Task, repairLimit: 0),
            GoalFixtures.Facts(review: ReviewFact.NeedsWork, unmet: true));
        Assert.AreEqual(GoalPolicyDecisionKind.Wait, notAllowed.Kind);
        Assert.AreEqual(GoalPolicyErrorCodes.RepairNotAllowed, notAllowed.ReasonCode);

        var exhausted = Policy.Decide(GoalFixtures.Policy(repairLimit: 2),
            GoalFixtures.Facts(review: ReviewFact.NeedsWork, unmet: true, repairUsed: 2));
        Assert.AreEqual(GoalPolicyDecisionKind.Stop, exhausted.Kind);
        Assert.AreEqual(GoalPolicyErrorCodes.RepairExhausted, exhausted.ReasonCode);
        Assert.IsFalse(exhausted.CreateNextRound);
    }

    [TestMethod]
    public void PersistentNoProgress_Blocks_InsteadOfRepeating()
    {
        var decision = Policy.Decide(GoalFixtures.Policy(repairLimit: 5, noProgressLimit: 2),
            GoalFixtures.Facts(review: ReviewFact.NeedsWork, unmet: true, repairUsed: 1, noProgress: 2));
        Assert.AreEqual(GoalPolicyDecisionKind.Stop, decision.Kind);
        Assert.AreEqual(GoalPolicyErrorCodes.NoProgress, decision.ReasonCode);
        Assert.IsFalse(decision.CreateNextRound);
    }

    [TestMethod]
    public void NoExecutableAction_StopsWithTypedReason()
    {
        var decision = Policy.Decide(GoalFixtures.Policy(),
            GoalFixtures.Facts(review: ReviewFact.NeedsWork, unmet: true, hasExecutableAction: false));
        Assert.AreEqual(GoalPolicyDecisionKind.Stop, decision.Kind);
        Assert.AreEqual(GoalPolicyErrorCodes.NoExecutableAction, decision.ReasonCode);
    }

    [TestMethod]
    public void InvalidInputs_AreTyped()
    {
        Assert.AreEqual(GoalPolicyErrorCodes.RequestInvalid, Assert.ThrowsExactly<GoalPolicyException>(() =>
            Policy.Decide(null!, GoalFixtures.Facts())).Code);
        Assert.AreEqual(GoalPolicyErrorCodes.RequestInvalid, Assert.ThrowsExactly<GoalPolicyException>(() =>
            Policy.Decide(GoalFixtures.Policy(), null!)).Code);
    }

    [TestMethod]
    public void NextRoundKey_IsStable_AndDoesNotDependOnPollingCounters()
    {
        var policy = GoalFixtures.Policy();
        var facts = GoalFixtures.Facts(review: ReviewFact.NeedsWork, unmet: true);
        var first = Policy.NextRoundKey(policy, facts);
        var second = Policy.NextRoundKey(policy, facts);
        Assert.AreEqual(first, second, "Repeated decisions must reuse the same next-round key.");
        var nextIteration = Policy.NextRoundKey(policy, facts with { IterationNo = facts.IterationNo + 1 });
        Assert.AreNotEqual(first, nextIteration);
        var otherPolicy = Policy.NextRoundKey(policy with { PolicyVersion = "policy-v2" }, facts);
        Assert.AreNotEqual(first, otherPolicy);
    }
}
