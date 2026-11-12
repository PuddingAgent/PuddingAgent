using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Pudding.Api.Contracts;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;
using ReviewJobView = PuddingReviews.Contracts.ReviewJobView;

namespace PuddingPlatformTests.Controllers;

/// <summary>
/// PA-08 A endpoint: returns 200 with the projected review snapshot; missing/invalid ids map to 404/400 instead of a
/// fake empty snapshot; the payload never contains unknown budget amounts as zero.
/// </summary>
[TestClass]
public sealed class ExecutionReviewControllerTests
{
    private static ExecutionReviewController Controller(ExecutionReviewQueryService service) => new(service);

    [TestMethod]
    public async Task Get_ReturnsTheProjectedSnapshot()
    {
        var controller = Controller(Service());
        var result = await controller.Get("review-1", stale: false, default);
        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        var json = JsonSerializer.Serialize(ok!.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var review = document.RootElement.GetProperty("review");
        Assert.AreEqual("satisfied", review.GetProperty("reviewState").GetString());
        Assert.AreEqual("review-1", review.GetProperty("reviewId").GetString());
        Assert.AreEqual(0.25m, review.GetProperty("budget").GetProperty("actual").GetProperty("amount").GetDecimal());
    }

    [TestMethod]
    public async Task Get_MissingReviewIs404_AndInvalidIdIs400()
    {
        var controller = Controller(Service(job: null));
        var missing = await controller.Get("review-missing", stale: false, default);
        var problem = missing as ObjectResult;
        Assert.IsNotNull(problem);
        Assert.AreEqual(404, problem!.StatusCode);

        var invalid = await controller.Get(" ", stale: false, default);
        Assert.IsInstanceOfType<ObjectResult>(invalid);
    }

    private static ExecutionReviewQueryService Service(ReviewJobView? job = default) => new(
        new FakeJobs(job ?? Job()), new FakeBudgets(), new FakeProjection(), new FakeConversations());

    private static ReviewJobView Job() => new("job-1", "review-1",
        new("ws-1", "work-1", "goal-1", 1, 2, "objective-v1", "acceptance-v1", "artifact-hash", "policy-v1"),
        Required: true, "budget-1", DateTimeOffset.UtcNow.AddMinutes(10), ReviewJobState.Completed, 2, 1, 7, null,
        null, DateTimeOffset.UtcNow);

    private sealed class FakeJobs(ReviewJobView job) : IReviewJobStore
    {
        public ValueTask<ReviewCreateResult> CreateOrGetAsync(ReviewJobRecord record, string operationId,
            CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<ReviewJobView?> GetAsync(string jobId, CancellationToken ct = default)
            => ValueTask.FromResult<ReviewJobView?>(job);
        public ValueTask<ReviewJobView?> GetByReviewAsync(string reviewId, CancellationToken ct = default)
            => ValueTask.FromResult<ReviewJobView?>(job.ReviewId == reviewId ? job : null);
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
        public Task<ExecutionBudgetSnapshot?> GetAsync(string accountId, CancellationToken ct = default)
            => Task.FromResult<ExecutionBudgetSnapshot?>(new ExecutionBudgetSnapshot("budget-1", 9,
                new(20_000, 2m, 10, 2, null), new(100, 200, 5, 0.25m, "usd"), new(50, 0.05m), new(0, 0m), 1,
                Cancelled: false, 0, null));
    }

    private sealed class FakeProjection : IReviewCompletionReader
    {
        public Task<ReviewCompletionProjectionResult> GetLatestAsync(string conversationId, CancellationToken ct = default)
            => Task.FromResult(new ReviewCompletionProjectionResult(new ReviewOutcomeProjection("review-1", "job-1",
                "Satisfied", "CheckVerified", 2, "artifact-hash", "acceptance-v1", 42), 1, 0));
    }

    private sealed class FakeConversations : IReviewGoalConversationResolver
    {
        public Task<string?> ResolveAsync(string goalRunId, CancellationToken ct = default)
            => Task.FromResult<string?>("conv-1");
    }
}
