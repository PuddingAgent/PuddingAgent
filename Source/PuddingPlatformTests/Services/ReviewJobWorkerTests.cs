using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Reviews;
using PuddingReviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03 S5 worker + outbox on real SQLite: a claimable job runs through the independent coordinator (evidence →
/// executor → validate → complete → outbox), a failing executor leaves the job Unknown without a completion
/// event, terminal jobs are not re-claimed, and an expired lease can be reclaimed. The outbox is idempotent.
/// </summary>
[TestClass]
public sealed class ReviewJobWorkerTests
{
    [TestMethod]
    public async Task RunsClaimableJob_CompletesAndWritesOutbox()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");

        var result = await host.Worker.RunBatchAsync(10, TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, result.Executed);
        var job = await host.Store.GetAsync("job-1");
        Assert.AreEqual(ReviewJobState.Completed, job!.State);
        Assert.AreEqual(ReviewFindingValue.Satisfied, job.Outcome!.Finding);
        Assert.AreEqual(ReviewEvidenceLevel.CheckVerified, job.Outcome.Level);

        Assert.AreEqual(1, await host.OutboxCountAsync());
        Assert.AreEqual(0, (await host.Worker.RunBatchAsync(10, TimeSpan.FromMinutes(1))).Claimed,
            "A completed job is not claimable again.");
    }

    [TestMethod]
    public async Task ExecutorFailure_LeavesUnknown_WithoutCompletionEvent()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");
        host.Executor.Throw = true;

        var result = await host.Worker.RunBatchAsync(10, TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, result.Executed);
        Assert.AreEqual(ReviewJobState.Unknown, (await host.Store.GetAsync("job-1"))!.State);
        Assert.AreEqual(0, await host.OutboxCountAsync(), "An unknown review must not publish completion.");
    }

    [TestMethod]
    public async Task TerminalJobsAreNotClaimed_AndCancelledJobIsSkipped()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");
        await host.Store.CancelAsync("job-1", 0, "user-cancelled");

        var result = await host.Worker.RunBatchAsync(10, TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, result.Claimed);
        Assert.AreEqual(ReviewJobState.Cancelled, (await host.Store.GetAsync("job-1"))!.State);
    }

    [TestMethod]
    public async Task ExpiredLease_IsReclaimedAndRuns()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");
        await host.Store.ClaimAsync("job-1", 1, DateTimeOffset.UtcNow.AddMilliseconds(50), 0);
        await Task.Delay(120);

        var result = await host.Worker.RunBatchAsync(10, TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, result.Executed);
        var job = await host.Store.GetAsync("job-1");
        Assert.AreEqual(ReviewJobState.Completed, job!.State);
        Assert.IsTrue(job.OwnerEpoch > 1, "The reclaiming worker owns the job.");
    }

    [TestMethod]
    public async Task MissingEvidence_Waits_ThenReclaimableWhenEvidenceArrives()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");
        host.Reader.Missing = true;

        var first = await host.Worker.RunBatchAsync(10, TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, first.Executed);
        Assert.AreEqual(ReviewJobState.Waiting, (await host.Store.GetAsync("job-1"))!.State,
            "Missing evidence must persist Waiting instead of a terminal failure.");
        Assert.AreEqual(0, await host.OutboxCountAsync());

        host.Reader.Missing = false;
        var second = await host.Worker.RunBatchAsync(10, TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, second.Claimed, "A Waiting job is claimable when new evidence arrives.");
        Assert.AreEqual(ReviewJobState.Completed, (await host.Store.GetAsync("job-1"))!.State);
        Assert.AreEqual(1, await host.OutboxCountAsync());
    }

    [TestMethod]
    public async Task Outbox_IsIdempotentByEventId()
    {
        await using var host = await TestHost.CreateAsync();
        var key = Key();
        var completed = new ReviewCompletedEvent("event-1", "review-1", "job-1", key, ReviewFindingValue.Satisfied,
            ReviewEvidenceLevel.CheckVerified, 2, DateTimeOffset.UtcNow);
        await host.Outbox.PublishCompletedAsync(completed);
        await host.Outbox.PublishCompletedAsync(completed);
        Assert.AreEqual(1, await host.OutboxCountAsync());
    }

    internal static ReviewLogicalKey Key() => new("ws-1", null, "goal-1", 1, 2, "objective-v1", "acceptance-v1",
        "artifact-hash", "policy-v1");

    internal static ReviewJobRecord Record(string jobId, string reviewId) => new(jobId, reviewId, Key(), Required: true,
        "budget-1", DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow.AddMinutes(-1), AcceptedSequence: 7);

    internal sealed class FakeReader : IReviewEvidenceReader
    {
        public bool Missing { get; set; }

        public ValueTask<ReviewEvidenceReadResult> ReadAsync(ReviewJobView job, CancellationToken ct = default)
        {
            if (Missing)
                return ValueTask.FromResult(new ReviewEvidenceReadResult(null, ReviewErrorCodes.SnapshotRequired));
            var snapshot = new ReviewInputSnapshot(job.ReviewId, job.Key, "Build both files.", [new("criterion-1", "Both files exist.")],
                [new("evidence-1", EvidenceKind.CheckReport, "check://build", "check://build/1", "sha-check",
                    DateTimeOffset.UtcNow, "controlled-check", Truncated: false, CheckExitCode: "0")],
                EvidenceComplete: true, [], "prompt-v1", "policy-v1", job.BudgetAccountId, job.Deadline.AddMinutes(-1));
            return ValueTask.FromResult(new ReviewEvidenceReadResult(snapshot, null));
        }
    }

    internal sealed class FakeExecutor : IReviewExecutor
    {
        public bool Throw { get; set; }

        public ValueTask<ReviewModelResult> ReviewAsync(ReviewInputSnapshot snapshot, string attemptId,
            CancellationToken ct = default)
        {
            if (Throw) throw new ReviewException(ReviewErrorCodes.ExecutorUnknown, "provider failed");
            var result = new ReviewModelResult(ReviewFindingValue.Satisfied,
                [new("criterion-1", CriterionOutcomeValue.Supported, ["evidence-1"])], 0.9, "both files present",
                null, snapshot.Key.ObjectiveVersion, snapshot.Key.AcceptanceContractVersion, snapshot.Key.ArtifactSetHash,
                snapshot.PromptVersion, "model-a", "rev-1", "fingerprint");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, ReviewJobStore store, ReviewEventOutbox outbox,
        FakeExecutor executor, FakeReader reader, ReviewJobWorker worker) : IAsyncDisposable
    {
        public ReviewJobStore Store { get; } = store;
        public ReviewEventOutbox Outbox { get; } = outbox;
        public FakeExecutor Executor { get; } = executor;
        public FakeReader Reader { get; } = reader;
        public ReviewJobWorker Worker { get; } = worker;

        public async Task<int> OutboxCountAsync()
        {
            await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection).Options);
            return await db.ReviewEvents.CountAsync();
        }

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
            var store = new ReviewJobStore(factory);
            var outbox = new ReviewEventOutbox(factory);
            var executor = new FakeExecutor();
            var reader = new FakeReader();
            var coordinator = new ReviewJobCoordinator(store, reader, executor);
            return new(connection, store, outbox, executor, reader, new ReviewJobWorker(store, coordinator));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
