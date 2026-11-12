using System.Collections.Immutable;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pudding.Api.Contracts;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;
using ReviewJobView = PuddingReviews.Contracts.ReviewJobView;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-08 A goal-level lookup: the latest review job by accepted sequence is returned for a Goal, another Goal
/// returns null, the query service builds the same explicit snapshot, and the endpoint returns 200/404 without
/// fabricating a view.
/// </summary>
[TestClass]
public sealed class ExecutionReviewGoalLookupTests
{
    [TestMethod]
    public async Task Store_ReturnsTheLatestJobByAcceptedSequence()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-old", "review-old", artifactHash: "artifact-old", sequence: 3), "op-old");
        await host.Store.CreateOrGetAsync(Record("job-new", "review-new", artifactHash: "artifact-new", sequence: 9), "op-new");
        await host.Store.CreateOrGetAsync(Record("job-other", "review-other", goalRunId: "goal-2", artifactHash: "artifact-other", sequence: 99), "op-other");

        var latest = await host.Store.GetLatestByGoalAsync("goal-1");
        Assert.AreEqual("job-new", latest!.JobId);
        Assert.IsNull(await host.Store.GetLatestByGoalAsync("goal-missing"));
        Assert.IsNull(await host.Store.GetLatestByGoalAsync(" "));
    }

    [TestMethod]
    public async Task QueryService_LatestForGoal_UsesTheByGoalReader()
    {
        var service = new ExecutionReviewQueryService(new FakeJobs(Job()), new FakeBudgets(),
            new FakeProjection(), new FakeConversations(), new FakeByGoal(Job()));
        var result = await service.GetLatestForGoalAsync("goal-1");
        Assert.IsNull(result.MissingReason);
        Assert.AreEqual("review-1", result.Snapshot!.ReviewId);

        var missing = new ExecutionReviewQueryService(new FakeJobs(Job()), new FakeBudgets(), new FakeProjection(),
            new FakeConversations(), new FakeByGoal(null));
        Assert.AreEqual(ReviewErrorCodes.NotFound, (await missing.GetLatestForGoalAsync("goal-1")).MissingReason);
        Assert.AreEqual(ReviewErrorCodes.RequestInvalid, (await missing.GetLatestForGoalAsync(" ")).MissingReason);
    }

    [TestMethod]
    public async Task Controller_GoalEndpoint_Returns200Or404()
    {
        var okController = new ExecutionReviewController(new ExecutionReviewQueryService(new FakeJobs(Job()),
            new FakeBudgets(), new FakeProjection(), new FakeConversations(), new FakeByGoal(Job())));
        var ok = await okController.GetLatestForGoal("goal-1", stale: false, default);
        Assert.IsInstanceOfType<OkObjectResult>(ok);

        var missingController = new ExecutionReviewController(new ExecutionReviewQueryService(new FakeJobs(Job()),
            new FakeBudgets(), new FakeProjection(), new FakeConversations(), new FakeByGoal(null)));
        var missing = await missingController.GetLatestForGoal("goal-1", stale: false, default) as ObjectResult;
        Assert.IsNotNull(missing);
        Assert.AreEqual(404, missing!.StatusCode);
    }

    private static ReviewJobView Job() => new("job-1", "review-1",
        new("ws-1", "work-1", "goal-1", 1, 2, "objective-v1", "acceptance-v1", "artifact-hash", "policy-v1"),
        Required: true, "budget-1", DateTimeOffset.UtcNow.AddMinutes(10), ReviewJobState.Completed, 2, 1, 7, null,
        null, DateTimeOffset.UtcNow);

    private static ReviewJobRecord Record(string jobId, string reviewId, string goalRunId = "goal-1", long sequence = 0, string artifactHash = "artifact-hash")
        => new(jobId, reviewId, new("ws-1", "work-1", goalRunId, 1, 2, "objective-v1", "acceptance-v1",
            artifactHash, "policy-v1"), Required: true, "budget-1", DateTimeOffset.UtcNow.AddMinutes(10),
            DateTimeOffset.UtcNow.AddMinutes(-1), sequence);

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

    private sealed class FakeByGoal(ReviewJobView? job) : IReviewJobByGoalReader
    {
        public Task<ReviewJobView?> GetLatestByGoalAsync(string goalRunId, CancellationToken ct = default)
            => Task.FromResult(job);
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

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, ReviewJobStore store) : IAsyncDisposable
    {
        public ReviewJobStore Store { get; } = store;

        public static async Task<TestHost> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new TestDbContextFactory(options);
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await ReviewJobSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            return new(connection, new ReviewJobStore(factory));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
