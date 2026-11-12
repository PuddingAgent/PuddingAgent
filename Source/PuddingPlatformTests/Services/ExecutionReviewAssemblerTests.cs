using System.Collections.Immutable;
using Pudding.Api.Contracts;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;
using ReviewJob = PuddingReviews.Contracts.ReviewJobView;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-08 A backend projection: verified review + known budget maps to a satisfied panel snapshot; advice-only
/// Satisfied becomes needs-user; missing review stays explicit; stale data is marked stale; an unknown cost bucket is
/// never rendered as a known zero; cancellation is surfaced without clearing occupancy.
/// </summary>
[TestClass]
public sealed class ExecutionReviewAssemblerTests
{
    [TestMethod]
    public void VerifiedReviewAndKnownBudget_MapToSatisfiedSnapshot()
    {
        var snapshot = ExecutionReviewAssembler.Assemble(Input(
            Review("Satisfied", "CheckVerified", revision: 3), Budget()));
        Assert.AreEqual("completed", snapshot.RunState);
        Assert.AreEqual("satisfied", snapshot.ReviewState);
        Assert.AreEqual("review-1", snapshot.ReviewId);
        Assert.AreEqual("model-a", snapshot.ModelPolicy.ModelId);
        Assert.IsTrue(snapshot.Budget.Actual.Known);
        Assert.AreEqual(0.25m, snapshot.Budget.Actual.Amount);
        Assert.IsTrue(snapshot.Budget.Unknown.Known, "A zero unknown bucket is known zero.");
        Assert.AreEqual(0m, snapshot.Budget.Unknown.Amount);
        Assert.IsFalse(snapshot.Freshness.Stale);
    }

    [TestMethod]
    public void ModelAdviceOnlySatisfied_IsNeverRenderedAsVerifiedSatisfaction()
    {
        var snapshot = ExecutionReviewAssembler.Assemble(Input(Review("Satisfied", "Observed"), Budget()));
        Assert.AreEqual("needs-user", snapshot.ReviewState);
        Assert.AreEqual("Satisfied", snapshot.ModelSuggestionFinding);
        Assert.AreEqual("Observed", snapshot.ModelSuggestionLevel);
    }

    [TestMethod]
    public void MissingReviewAndJob_StayExplicitAsUnknown()
    {
        var snapshot = ExecutionReviewAssembler.Assemble(Input(null, Budget(), withJob: false));
        Assert.AreEqual("unknown", snapshot.ReviewState);
        Assert.IsNull(snapshot.ReviewId);
        Assert.IsNull(snapshot.ModelSuggestionFinding);
    }

    [TestMethod]
    public void StaleDataIsMarkedAndNeverLooksLikeLiveSuccess()
    {
        var snapshot = ExecutionReviewAssembler.Assemble(Input(Review("Satisfied", "CheckVerified"), Budget(),
            stale: true, gapDetected: true));
        Assert.AreEqual("stale", snapshot.ReviewState);
        Assert.IsTrue(snapshot.Freshness.Stale);
        Assert.IsTrue(snapshot.Freshness.GapDetected);
    }

    [TestMethod]
    public void UnknownCostBucketIsNotAZeroAmount()
    {
        var budget = Budget() with
        {
            Unknown = new BudgetUnknownView(0, 1.5m),
        };
        var snapshot = ExecutionReviewAssembler.Assemble(Input(Review("NeedsWork", "Observed"), budget));
        Assert.IsFalse(snapshot.Budget.Unknown.Known);
        Assert.IsNull(snapshot.Budget.Unknown.Amount);
        Assert.AreEqual("needs-work", snapshot.ReviewState);
    }

    [TestMethod]
    public void CancelledJobIsSurfacedWithoutClearingOccupancy()
    {
        var job = Job() with { State = ReviewJobState.Cancelled, ReasonCode = "user.cancelled" };
        var snapshot = ExecutionReviewAssembler.Assemble(Input(null, Budget(), job: job));
        Assert.AreEqual("cancelled", snapshot.RunState);
        Assert.IsTrue(snapshot.Cancel.Requested);
        Assert.AreEqual("user.cancelled", snapshot.Cancel.ReasonCode);
        Assert.AreEqual(0.05m, snapshot.Budget.Reserved.Amount, "Occupancy is not cleared by a cancellation request.");
    }

    private static ExecutionReviewAssemblyInput Input(ReviewOutcomeProjection? review, ExecutionBudgetSnapshot budget,
        ReviewJob? job = null, bool stale = false, bool gapDetected = false, bool withJob = true) => new("work-1", "goal-1", "turn-1",
        budget, withJob ? job ?? Job() : null, review, stale, gapDetected, "2026-11-05T12:00:00+00:00", "model-a", "rev-1", "policy-v1",
        "prompt-v3", [], []);

    private static ReviewOutcomeProjection Review(string finding, string level, long revision = 2) => new("review-1",
        "job-1", finding, level, revision, "artifact-hash", "acceptance-v1", 42);

    private static ReviewJob Job() => new("job-1", "review-1",
        new("ws-1", "work-1", "goal-1", 1, 2, "objective-v1", "acceptance-v1", "artifact-hash", "policy-v1"),
        Required: true, "budget-1", DateTimeOffset.UtcNow.AddMinutes(10), ReviewJobState.Completed, 2, 1, 7, null,
        null, DateTimeOffset.UtcNow);

    private static ExecutionBudgetSnapshot Budget() => new("budget-1", 9, new(20_000, 2m, 10, 2, null),
        new(100, 200, 5, 0.25m, "usd"), new(50, 0.05m), new(0, 0m), 1, Cancelled: false,
        CancellationRevision: 0, ReasonCode: null);
}
