using System.Collections.Immutable;

namespace PuddingGoalPolicy.Contracts;

// PA-04 独立纯策略（BCL-only）：只做 Continue/Wait/Complete/Stop 建议，不写库、不调用模型/工具，
// 不复制 Goal 状态机；建议必须再经既有 calculator/state machine 校验后提交。

public enum InteractionMode { Chat, Task, Goal }

public enum GoalPhaseFact { Active, Paused, Blocked, BudgetExhausted, Completed, Cancelled, Failed }

public enum ReviewFact { NotRequired, Missing, Pending, Unknown, Satisfied, NeedsWork, NeedsUser, Stale }

public enum GoalPolicyDecisionKind { Continue, Wait, Complete, Stop }

public sealed record ContinuationPolicySnapshot(InteractionMode Mode, string PolicyVersion, bool ReviewRequired,
    int RepairLimit, int NoProgressLimit, string RootBudgetId, ImmutableArray<string> AllowedCapabilities,
    string ResumePolicy)
{
    public bool IsValid => Valid(PolicyVersion) && RepairLimit >= 0 && NoProgressLimit >= 1 && Valid(RootBudgetId)
        && !AllowedCapabilities.IsDefault && !string.IsNullOrWhiteSpace(ResumePolicy) && ResumePolicy.Length <= 128;

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

/// <summary>Frozen facts read from authoritative services; the policy never queries them itself.</summary>
public sealed record GoalPolicyFacts(string GoalRunId, long ActivationEpoch, int IterationNo, GoalPhaseFact Phase,
    bool Cancelled, bool Paused, bool AuthorizationRevoked, bool ObjectiveVersionChanged, bool ReviewStale,
    ReviewFact Review, bool BudgetExhausted, bool MaxRoundsReached, bool HasUnmetRequiredCriteria,
    bool EvidenceSufficient, bool NoUnresolvedExecutionFacts, bool RepairAllowed, int RepairUsed,
    bool HasExecutableNextAction, int ConsecutiveNoProgress, string? NextActionId, string? BlockerReason)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(GoalRunId) && GoalRunId.Length <= 128 && ActivationEpoch >= 0
        && IterationNo >= 0 && RepairUsed >= 0 && ConsecutiveNoProgress >= 0;
}

public sealed record GoalPolicyDecision(GoalPolicyDecisionKind Kind, string ReasonCode, bool CreateNextRound,
    string? NextActionId, bool RequiresUser, bool IsTerminal, bool RetryOnRestart)
{
    public static GoalPolicyDecision Continue(string reason, string? nextActionId) =>
        new(GoalPolicyDecisionKind.Continue, reason, CreateNextRound: true, nextActionId, false, false, true);
    public static GoalPolicyDecision Wait(string reason, bool requiresUser = false) =>
        new(GoalPolicyDecisionKind.Wait, reason, false, null, requiresUser, false, false);
    public static GoalPolicyDecision Complete(string reason) =>
        new(GoalPolicyDecisionKind.Complete, reason, false, null, false, true, false);
    public static GoalPolicyDecision Stop(string reason) =>
        new(GoalPolicyDecisionKind.Stop, reason, false, null, false, false, false);
}

public interface IGoalContinuationPolicy
{
    GoalPolicyDecision Decide(ContinuationPolicySnapshot policy, GoalPolicyFacts facts);
    /// <summary>Stable next-round key: GoalRunId + ActivationEpoch + IterationNo + policy version (no polling counter).</summary>
    string NextRoundKey(ContinuationPolicySnapshot policy, GoalPolicyFacts facts);
}

public static class GoalPolicyErrorCodes
{
    public const string RequestInvalid = "goal.policy_request_invalid";
    public const string Cancelled = "goal.cancelled";
    public const string Paused = "goal.paused";
    public const string AuthorizationRevoked = "goal.authorization_revoked";
    public const string ChatNoContinuation = "goal.chat_single_turn";
    public const string ReviewStale = "goal.review_stale";
    public const string ObjectiveVersionChanged = "goal.objective_version_changed";
    public const string ReviewMissing = "goal.review_missing";
    public const string ReviewPending = "goal.review_pending";
    public const string ReviewUnknown = "goal.review_unknown";
    public const string ReviewNeedsUser = "goal.review_needs_user";
    public const string ReviewNeedsWork = "goal.review_needs_work";
    public const string BudgetExhausted = "goal.budget_exhausted";
    public const string RoundsExhausted = "goal.rounds_exhausted";
    public const string RepairExhausted = "goal.repair_exhausted";
    public const string RepairNotAllowed = "goal.repair_not_allowed";
    public const string NoExecutableAction = "goal.no_executable_action";
    public const string NoProgress = "goal.no_progress";
    public const string AwaitingEvidence = "goal.awaiting_evidence";
    public const string Completed = "goal.completed";
    public const string ContinueRepair = "goal.continue_repair";
    public const string ContinueEvidence = "goal.continue_evidence";
}

public sealed class GoalPolicyException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
