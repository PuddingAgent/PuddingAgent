using PuddingGoalPolicy.Contracts;

namespace PuddingGoalPolicyTests;

internal static class GoalFixtures
{
    public static ContinuationPolicySnapshot Policy(InteractionMode mode = InteractionMode.Goal,
        bool reviewRequired = true, int repairLimit = 2, int noProgressLimit = 2)
        => new(mode, "policy-v1", reviewRequired, repairLimit, noProgressLimit, "budget-root",
            ["read", "write"], "restart-resume");

    public static GoalPolicyFacts Facts(ReviewFact review = ReviewFact.Satisfied, bool unmet = false,
        bool evidenceSufficient = true, bool noUnresolved = true, bool repairAllowed = true, int repairUsed = 0,
        bool hasExecutableAction = true, int noProgress = 0, bool cancelled = false, bool paused = false,
        bool revoked = false, bool versionChanged = false, bool stale = false, bool budgetExhausted = false,
        bool maxRounds = false)
        => new("goal-run-1", 3, 2, GoalPhaseFact.Active, cancelled, paused, revoked, versionChanged, stale, review,
            budgetExhausted, maxRounds, unmet, evidenceSufficient, noUnresolved, repairAllowed, repairUsed,
            hasExecutableAction, noProgress, "next-action-1", null);
}
