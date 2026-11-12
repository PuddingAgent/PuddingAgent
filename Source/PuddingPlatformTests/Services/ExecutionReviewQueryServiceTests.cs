using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pudding.Api.Contracts;
using PuddingExecutionResources.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.ExecutionBudgets;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;
using ReviewJobView = PuddingReviews.Contracts.ReviewJobView;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-08 A query service: missing job/budget are explicit missing reasons; a completed job with verified review and
/// budget maps to a satisfied snapshot; stale data is marked stale. The budget adapter preserves unknown amounts.
/// </summary>
[TestClass]
public sealed class ExecutionReviewQueryServiceTests
{
    [TestMethod]
    public async Task MissingOrInvalidInputs_AreExplicitMissingReasons()
    {
        var service = new ExecutionReviewQueryService(new FakeJobs(), new FakeBudgets(), new FakeProjection(),
            new FakeConversations());
        Assert.AreEqual(ReviewErrorCodes.RequestInvalid, (await service.GetAsync(" ")).MissingReason);
        Assert.AreEqual(ReviewErrorCodes.NotFound, (await service.GetAsync("review-missing")).MissingReason);

        var budgetMissing = new ExecutionReviewQueryService(new FakeJobs { Job = Job() }, new FakeBudgets(),
            new FakeProjection(), new FakeConversations());
        Assert.AreEqual(BudgetErrorCodes.AccountMissing, (await budgetMissing.GetAsync("review-1")).MissingReason);
    }

    [TestMethod]
    public async Task CompletedVerifiedReviewWithBudget_MapsToSatisfiedSnapshot()
    {
        var service = new ExecutionReviewQueryService(new FakeJobs { Job = Job() },
            new FakeBudgets { Snapshot = Budget() }, new FakeProjection { Latest = Review("Satisfied", "CheckVerified") },
            new FakeConversations { ConversationId = "conv-1" });
        var result = await service.GetAsync("review-1");
        Assert.IsNull(result.MissingReason);
        Assert.AreEqual("work-1", result.Snapshot!.WorkId);
        Assert.AreEqual("goal-1", result.Snapshot.GoalId);
        Assert.AreEqual("satisfied", result.Snapshot.ReviewState);
        Assert.AreEqual("review-1", result.Snapshot.ReviewId);
        Assert.IsTrue(result.Snapshot.Budget.Actual.Known);
        Assert.AreEqual(0.25m, result.Snapshot.Budget.Actual.Amount);
    }

    [TestMethod]
    public async Task StaleFlagMarksTheSnapshotStale()
    {
        var service = new ExecutionReviewQueryService(new FakeJobs { Job = Job() },
            new FakeBudgets { Snapshot = Budget() }, new FakeProjection { Latest = Review("Satisfied", "CheckVerified") },
            new FakeConversations { ConversationId = "conv-1" });
        var result = await service.GetAsync("review-1", stale: true);
        Assert.AreEqual("stale", result.Snapshot!.ReviewState);
        Assert.IsTrue(result.Snapshot.Freshness.Stale);
    }

    private static ReviewJobView Job() => new("job-1", "review-1",
        new("ws-1", "work-1", "goal-1", 1, 2, "objective-v1", "acceptance-v1", "artifact-hash", "policy-v1"),
        Required: true, "budget-1", DateTimeOffset.UtcNow.AddMinutes(10), ReviewJobState.Completed, 2, 1, 7, null,
        null, DateTimeOffset.UtcNow);

    private static ExecutionBudgetSnapshot Budget() => new("budget-1", 9, new(20_000, 2m, 10, 2, null),
        new(100, 200, 5, 0.25m, "usd"), new(50, 0.05m), new(0, 0m), 1, Cancelled: false,
        CancellationRevision: 0, ReasonCode: null);

    private static ReviewOutcomeProjection Review(string finding, string level) => new("review-1", "job-1", finding,
        level, 2, "artifact-hash", "acceptance-v1", 42);

    private sealed class FakeJobs : IReviewJobStore
    {
        public ReviewJobView? Job;
        public ValueTask<ReviewCreateResult> CreateOrGetAsync(ReviewJobRecord record, string operationId,
            CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<ReviewJobView?> GetAsync(string jobId, CancellationToken ct = default)
            => ValueTask.FromResult(Job);
        public ValueTask<ReviewJobView?> GetByReviewAsync(string reviewId, CancellationToken ct = default)
            => ValueTask.FromResult(Job?.ReviewId == reviewId ? Job : null);
        public ValueTask<ReviewJobView> ClaimAsync(string jobId, long ownerEpoch, DateTimeOffset leaseUntil,
            long expectedRevision, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<ReviewJobView> CompleteAsync(string jobId, long ownerEpoch, long expectedRevision,
            ReviewOutcome outcome, ReviewCompletedEvent completed, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<ReviewJobView> FailAsync(string jobId, long ownerEpoch, long expectedRevision,
            ReviewJobState state, string reasonCode, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<ReviewJobView> CancelAsync(string jobId, long expectedRevision, string reasonCode,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeBudgets : IExecutionBudgetApi
    {
        public ExecutionBudgetSnapshot? Snapshot;
        public Task<ExecutionBudgetSnapshot?> GetAsync(string accountId, CancellationToken ct = default)
            => Task.FromResult(Snapshot);
    }

    private sealed class FakeProjection : IReviewCompletionReader
    {
        public ReviewOutcomeProjection? Latest;
        public Task<ReviewCompletionProjectionResult> GetLatestAsync(string conversationId,
            CancellationToken ct = default) => Task.FromResult(new ReviewCompletionProjectionResult(Latest, 1, 0));
    }

    private sealed class FakeConversations : IReviewGoalConversationResolver
    {
        public string? ConversationId;
        public Task<string?> ResolveAsync(string goalRunId, CancellationToken ct = default)
            => Task.FromResult(ConversationId);
    }
}
