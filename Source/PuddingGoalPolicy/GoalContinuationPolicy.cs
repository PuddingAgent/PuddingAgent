using PuddingGoalPolicy.Contracts;

namespace PuddingGoalPolicy;

/// <summary>
/// PA-04 pure continuation policy. It only selects an advice from frozen facts: cancel/pause/authorization
/// first, stale versions never reuse an old pass, required reviews that are missing/pending/unknown wait instead
/// of defaulting, budget/round limits stop without granting more, completion needs sufficient evidence for every
/// required criterion and no unresolved execution fact, repair is bound by explicit limits and no-progress ends
/// in Blocked instead of an endless "think again". It never writes state, calls a model or runs tools.
/// </summary>
public sealed class GoalContinuationPolicy : IGoalContinuationPolicy
{
    public GoalPolicyDecision Decide(ContinuationPolicySnapshot policy, GoalPolicyFacts facts)
    {
        Ensure(policy, facts);
        if (facts.Cancelled) return GoalPolicyDecision.Stop(GoalPolicyErrorCodes.Cancelled);
        if (facts.Paused) return GoalPolicyDecision.Stop(GoalPolicyErrorCodes.Paused);
        if (facts.AuthorizationRevoked) return GoalPolicyDecision.Stop(GoalPolicyErrorCodes.AuthorizationRevoked);
        if (policy.Mode == InteractionMode.Chat) return GoalPolicyDecision.Stop(GoalPolicyErrorCodes.ChatNoContinuation);
        if (facts.ReviewStale) return GoalPolicyDecision.Wait(GoalPolicyErrorCodes.ReviewStale);
        if (facts.ObjectiveVersionChanged) return GoalPolicyDecision.Wait(GoalPolicyErrorCodes.ObjectiveVersionChanged);
        if (facts.BudgetExhausted) return GoalPolicyDecision.Stop(GoalPolicyErrorCodes.BudgetExhausted);
        if (facts.MaxRoundsReached) return GoalPolicyDecision.Stop(GoalPolicyErrorCodes.RoundsExhausted);

        if (policy.ReviewRequired)
        {
            switch (facts.Review)
            {
                case ReviewFact.Missing: return GoalPolicyDecision.Wait(GoalPolicyErrorCodes.ReviewMissing);
                case ReviewFact.Pending: return GoalPolicyDecision.Wait(GoalPolicyErrorCodes.ReviewPending);
                case ReviewFact.Unknown: return GoalPolicyDecision.Wait(GoalPolicyErrorCodes.ReviewUnknown);
            }
        }
        if (facts.Review == ReviewFact.NeedsUser)
            return GoalPolicyDecision.Wait(GoalPolicyErrorCodes.ReviewNeedsUser, requiresUser: true);

        if (IsComplete(policy, facts)) return GoalPolicyDecision.Complete(GoalPolicyErrorCodes.Completed);

        var needsWork = facts.Review == ReviewFact.NeedsWork || facts.HasUnmetRequiredCriteria;
        if (needsWork)
        {
            if (!facts.RepairAllowed || policy.RepairLimit <= 0)
                return GoalPolicyDecision.Wait(GoalPolicyErrorCodes.RepairNotAllowed);
            if (facts.RepairUsed >= policy.RepairLimit)
                return GoalPolicyDecision.Stop(GoalPolicyErrorCodes.RepairExhausted);
            if (facts.ConsecutiveNoProgress >= policy.NoProgressLimit)
                return GoalPolicyDecision.Stop(GoalPolicyErrorCodes.NoProgress);
            return facts.HasExecutableNextAction
                ? GoalPolicyDecision.Continue(GoalPolicyErrorCodes.ContinueRepair, facts.NextActionId)
                : GoalPolicyDecision.Stop(GoalPolicyErrorCodes.NoExecutableAction);
        }

        if (facts.ConsecutiveNoProgress >= policy.NoProgressLimit)
            return GoalPolicyDecision.Stop(GoalPolicyErrorCodes.NoProgress);
        if (!facts.NoUnresolvedExecutionFacts)
            return GoalPolicyDecision.Wait(GoalPolicyErrorCodes.AwaitingEvidence);
        return facts.HasExecutableNextAction
            ? GoalPolicyDecision.Continue(GoalPolicyErrorCodes.ContinueEvidence, facts.NextActionId)
            : GoalPolicyDecision.Stop(GoalPolicyErrorCodes.NoExecutableAction);
    }

    public string NextRoundKey(ContinuationPolicySnapshot policy, GoalPolicyFacts facts)
    {
        Ensure(policy, facts);
        return $"{facts.GoalRunId}|{policy.PolicyVersion}|{facts.ActivationEpoch}|{facts.IterationNo}";
    }

    private static bool IsComplete(ContinuationPolicySnapshot policy, GoalPolicyFacts facts)
    {
        if (facts.HasUnmetRequiredCriteria || !facts.EvidenceSufficient || !facts.NoUnresolvedExecutionFacts)
            return false;
        // When no review is required, an attempted-but-unknown review is irrelevant; completion rests on evidence.
        return !policy.ReviewRequired || facts.Review == ReviewFact.Satisfied;
    }

    private static void Ensure(ContinuationPolicySnapshot policy, GoalPolicyFacts facts)
    {
        if (policy is null || !policy.IsValid)
            throw new GoalPolicyException(GoalPolicyErrorCodes.RequestInvalid, "A valid frozen policy snapshot is required.");
        if (facts is null || !facts.IsValid)
            throw new GoalPolicyException(GoalPolicyErrorCodes.RequestInvalid, "Valid frozen goal facts are required.");
    }
}
