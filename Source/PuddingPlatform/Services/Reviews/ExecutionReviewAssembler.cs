using System.Collections.Immutable;
using Pudding.Api.Contracts;
using PuddingReviews.Contracts;

namespace PuddingPlatform.Services.Reviews;

public sealed record ExecutionReviewAssemblyInput(string WorkId, string GoalId, string TurnId,
    ExecutionBudgetSnapshot Budget, PuddingReviews.Contracts.ReviewJobView? Job, ReviewOutcomeProjection? LatestReview, bool Stale,
    bool GapDetected, string DataAsOf, string ModelId, string ModelRevision, string PolicyVersion,
    string PromptVersion, ImmutableArray<ExecutionEvidenceRef> Evidence, ImmutableArray<ExecutionFailedCheck> FailedChecks);

/// <summary>
/// PA-08 A backend projection: maps authoritative budget/review/cancel state to the frozen panel snapshot. It never
/// derives a control conclusion: unknown amounts stay unknown, a model-advice-only Satisfied is not a verified
/// success, a missing review stays explicit, and stale data is marked stale rather than shown as live success.
/// </summary>
public static class ExecutionReviewAssembler
{
    public static ExecutionReviewSnapshot Assemble(ExecutionReviewAssemblyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var budget = input.Budget;
        var review = input.LatestReview;
        var job = input.Job;
        var runState = job?.State switch
        {
            ReviewJobState.Cancelled => "cancelled",
            _ when input.Stale => "unknown",
            _ => "completed",
        };
        var reviewState = job is null && review is null
            ? "unknown"
            : review is null
                ? "pending"
                : input.Stale
                    ? "stale"
                    : review.Finding switch
                    {
                        "Satisfied" when string.Equals(review.Level, ReviewEvidenceLevel.CheckVerified.ToString(),
                            StringComparison.Ordinal) => "satisfied",
                        "Satisfied" => "needs-user",
                        "NeedsWork" => "needs-work",
                        "NeedsUser" => "needs-user",
                        "Inconclusive" => "unknown",
                        _ => "unknown",
                    };
        // Actual/reserved are concrete amounts; the Unknown bucket is only "known" when it is exactly zero.
        var actual = new ExecutionBudgetAmount(budget.Actual.Cost, budget.Actual.CostUnit, Known: true);
        var reserved = new ExecutionBudgetAmount(budget.Reserved.Cost, budget.Actual.CostUnit, Known: true);
        var unknown = new ExecutionBudgetAmount(budget.Unknown.Cost == 0 ? 0 : null, budget.Actual.CostUnit,
            Known: budget.Unknown.Cost == 0);
        return new(input.WorkId, input.GoalId, input.TurnId, job?.ReviewId ?? review?.ReviewId, runState, reviewState,
            job is { State: ReviewJobState.Cancelled } ? job.ReasonCode : null,
            job is { State: ReviewJobState.Cancelled } ? "用户取消" : null,
            ContinueReasonCode: null, ContinueReasonSummary: null, ImmutableArray<ExecutionUnmetCriterion>.Empty,
            new ExecutionReviewBudgetView(budget.Limits.MaxOutputTokens, budget.Limits.MaxCost, actual, reserved, unknown),
            new ExecutionReviewPolicyView(input.ModelId, input.ModelRevision, input.PolicyVersion, input.PromptVersion,
                InheritedFromPrimary: false),
            new ExecutionReviewCancelView(budget.Cancelled || job is { State: ReviewJobState.Cancelled }, job?.UpdatedAt.ToString("O"), Draining: false,
                PendingCalls: 0, job?.ReasonCode),
            new ExecutionReviewFreshnessView(input.Stale, input.DataAsOf, budget.Revision, budget.Revision,
                input.GapDetected),
            review?.Finding,
            review?.Level,
            input.FailedChecks,
            budget.Revision, budget.Revision);
    }
}
