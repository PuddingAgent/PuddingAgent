using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03 S5: durable review job store on real SQLite — logical-key/operation idempotency, owner-epoch lease
/// claims with expected revision, owner/revision fencing on completion/failure, cancellation of non-terminal
/// jobs, and persistence across a new DbContext.
/// </summary>
[TestClass]
public sealed class ReviewJobStoreTests
{
    [TestMethod]
    public async Task Create_IsIdempotentByLogicalKeyAndOperation()
    {
        await using var host = await TestHost.CreateAsync();
        var first = await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");
        Assert.IsTrue(first.Created);
        Assert.AreEqual(0L, first.Job.Revision);
        Assert.AreEqual(ReviewJobState.Pending, first.Job.State);

        var replay = await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");
        Assert.IsFalse(replay.Created);
        Assert.AreEqual("job-1", replay.Job.JobId);

        var sameKey = await host.Store.CreateOrGetAsync(Record("job-2", "review-2"), "op-2");
        Assert.IsFalse(sameKey.Created, "One logical review key maps to one job.");
        Assert.AreEqual("job-1", sameKey.Job.JobId);
    }

    [TestMethod]
    public async Task ActorIdentity_IsPersistedForBinderRouting()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1") with { ActorId = "actor-1" }, "op-1");
        Assert.AreEqual("actor-1", (await host.Store.GetAsync("job-1"))!.ActorId);
    }
    [TestMethod]
    public async Task ReusedOperation_WithDifferentContent_Conflicts()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");
        var failure = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Store.CreateOrGetAsync(Record("job-9", "review-9") with
            {
                Key = Key(artifactSetHash: "different-artifacts"),
            }, "op-1"));
        Assert.AreEqual(ReviewErrorCodes.Conflict, failure.Code);
    }

    [TestMethod]
    public async Task ExactObjectiveLookup_IgnoresNewerOtherVersion()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-v1", "review-v1") with
        {
            Key = Key(objectiveVersion: "objective-v1"), AcceptedSequence = 0,
        }, "op-v1");
        await host.Store.CreateOrGetAsync(Record("job-v2", "review-v2") with
        {
            Key = Key(objectiveVersion: "objective-v2"), AcceptedSequence = 0,
        }, "op-v2");
        Assert.AreEqual("job-v2", (await host.Store.GetLatestByGoalAsync("goal-1"))!.JobId);
        Assert.AreEqual("job-v1", (await host.Store.GetLatestByGoalObjectiveAsync("goal-1", "objective-v1"))!.JobId);
        Assert.AreEqual("job-v2", (await host.Store.GetLatestByGoalObjectiveAsync("goal-1", "objective-v2"))!.JobId);
        Assert.IsNull(await host.Store.GetLatestByGoalObjectiveAsync("goal-1", "objective-v3"));
    }
    [TestMethod]
    public async Task AutoAcceptedSequence_KeepsLatestOrdering()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1") with { AcceptedSequence = 0 }, "op-1");
        await host.Store.CreateOrGetAsync(Record("job-2", "review-2") with
        {
            Key = Key(artifactSetHash: "artifact-2"),
            AcceptedSequence = 0,
        }, "op-2");
        var first = await host.Store.GetAsync("job-1");
        var second = await host.Store.GetAsync("job-2");
        Assert.AreEqual(1L, first!.AcceptedSequence);
        Assert.AreEqual(2L, second!.AcceptedSequence);
        Assert.AreEqual("job-2", (await host.Store.GetLatestByGoalAsync("goal-1"))!.JobId,
            "The latest query must follow the allocated accepted sequence, not the raw JobId hash.");
    }

    [TestMethod]
    public async Task ReusedOperation_WithDifferentActorOrBudget_Conflicts()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1") with { ActorId = "actor-1" }, "op-1");
        var actor = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Store.CreateOrGetAsync(Record("job-1", "review-1") with { ActorId = "actor-2" }, "op-1"));
        Assert.AreEqual(ReviewErrorCodes.Conflict, actor.Code, "The creation fingerprint must include the trusted actor.");
        var budget = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Store.CreateOrGetAsync(Record("job-1", "review-1") with
            {
                ActorId = "actor-1",
                BudgetAccountId = "budget-2",
            }, "op-1"));
        Assert.AreEqual(ReviewErrorCodes.Conflict, budget.Code, "The creation fingerprint must include the budget account.");
    }
    [TestMethod]
    public async Task Get_ReturnsView_AndMissingIsNull()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");
        var byJob = await host.Store.GetAsync("job-1");
        Assert.AreEqual("review-1", byJob!.ReviewId);
        Assert.AreEqual("goal-1", byJob.Key.GoalRunId);
        Assert.AreEqual("review-1", (await host.Store.GetByReviewAsync("review-1"))!.ReviewId);
        Assert.IsNull(await host.Store.GetAsync("missing"));
        Assert.IsNull(await host.Store.GetByReviewAsync("missing"));
    }

    [TestMethod]
    public async Task Claim_RequiresRevisionAndOwnerEpoch_AndRejectsAStaleWorker()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");

        var claim = await host.Store.ClaimAsync("job-1", ownerEpoch: 1, DateTimeOffset.UtcNow.AddMilliseconds(60), 0);
        Assert.AreEqual(ReviewJobState.Running, claim.State);
        Assert.AreEqual(1L, claim.OwnerEpoch);
        Assert.AreEqual(1L, claim.Revision);

        var staleRevision = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Store.ClaimAsync("job-1", 2, DateTimeOffset.UtcNow.AddMinutes(1), 0));
        Assert.AreEqual(ReviewErrorCodes.RevisionConflict, staleRevision.Code);

        var running = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Store.ClaimAsync("job-1", 2, DateTimeOffset.UtcNow.AddMinutes(1), 1));
        Assert.AreEqual(ReviewErrorCodes.StateInvalid, running.Code);

        await Task.Delay(120);
        var reclaimed = await host.Store.ClaimAsync("job-1", 2, DateTimeOffset.UtcNow.AddMilliseconds(60), 1);
        Assert.AreEqual(2L, reclaimed.OwnerEpoch);
        await Task.Delay(120);
        var staleOwner = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Store.ClaimAsync("job-1", 1, DateTimeOffset.UtcNow.AddMinutes(1), reclaimed.Revision));
        Assert.AreEqual(ReviewErrorCodes.OwnerStale, staleOwner.Code);
    }

    [TestMethod]
    public async Task ExpiredLease_RequiresAStrictlyNewerOwnerEpoch()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");
        await host.Store.ClaimAsync("job-1", 5, DateTimeOffset.UtcNow.AddMilliseconds(40), 0);
        await Task.Delay(100);

        var sameEpoch = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Store.ClaimAsync("job-1", 5, DateTimeOffset.UtcNow.AddMinutes(1), 1));
        Assert.AreEqual(ReviewErrorCodes.OwnerStale, sameEpoch.Code, "A reclaim must allocate a strictly newer epoch.");

        var newer = await host.Store.ClaimAsync("job-1", 6, DateTimeOffset.UtcNow.AddMinutes(1), 1);
        Assert.AreEqual(6L, newer.OwnerEpoch);
    }
    [TestMethod]
    public async Task Complete_IsOwnerAndRevisionFenced_AndPersistsTheOutcome()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");
        var claim = await host.Store.ClaimAsync("job-1", 1, DateTimeOffset.UtcNow.AddMinutes(1), 0);
        var outcome = new ReviewOutcome(ReviewFindingValue.NeedsWork, ReviewEvidenceLevel.Observed,
            [new("criterion-1", CriterionOutcomeValue.NotSupported, ["evidence-1"])], "missing-b");

        var staleOwner = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Store.CompleteAsync("job-1", 2, claim.Revision, outcome, Completed("job-1", claim.Revision + 1)));
        Assert.AreEqual(ReviewErrorCodes.OwnerStale, staleOwner.Code);
        var staleRevision = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Store.CompleteAsync("job-1", 1, 0, outcome, Completed("job-1", 1)));
        Assert.AreEqual(ReviewErrorCodes.RevisionConflict, staleRevision.Code);

        var completed = await host.Store.CompleteAsync("job-1", 1, claim.Revision, outcome,
            Completed("job-1", claim.Revision + 1));
        Assert.AreEqual(ReviewJobState.Completed, completed.State);
        Assert.AreEqual(ReviewFindingValue.NeedsWork, completed.Outcome!.Finding);
        Assert.AreEqual(ReviewEvidenceLevel.Observed, completed.Outcome.Level);
        Assert.AreEqual("missing-b", completed.Outcome.ReasonCode);
        Assert.AreEqual(1, completed.Outcome.Criteria.Length);
        Assert.AreEqual(CriterionOutcomeValue.NotSupported, completed.Outcome.Criteria[0].Outcome);
        Assert.AreEqual("evidence-1", completed.Outcome.Criteria[0].EvidenceIds[0]);

        Assert.AreEqual(1, await host.EventCountAsync(), "Completion writes exactly one outbox fact.");
        var replay = await host.Store.CompleteAsync("job-1", 1, claim.Revision, outcome,
            Completed("job-1", claim.Revision + 1));
        Assert.AreEqual(completed.Revision, replay.Revision, "A completion replay is idempotent.");
        Assert.AreEqual(1, await host.EventCountAsync(), "A replay never creates a second event.");
        var different = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Store.CompleteAsync("job-1", 1, claim.Revision, outcome,
                Completed("job-1", claim.Revision + 1) with { EventId = "different-event" }));
        Assert.AreEqual(ReviewErrorCodes.StateInvalid, different.Code);
    }

    [TestMethod]
    public async Task Completion_WithAnEventIdOwnedByAnotherJob_RollsBackTheTerminalState()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");
        var claim1 = await host.Store.ClaimAsync("job-1", 1, DateTimeOffset.UtcNow.AddMinutes(1), 0);
        var outcome = new ReviewOutcome(ReviewFindingValue.Satisfied, ReviewEvidenceLevel.CheckVerified,
            [new("criterion-1", CriterionOutcomeValue.Supported, ["evidence-1"])], null);
        await host.Store.CompleteAsync("job-1", 1, claim1.Revision, outcome, Completed("job-1", claim1.Revision + 1));

        await host.Store.CreateOrGetAsync(Record("job-2", "review-2") with
        {
            Key = Key(artifactSetHash: "other-hash"),
        }, "op-2");
        var claim2 = await host.Store.ClaimAsync("job-2", 1, DateTimeOffset.UtcNow.AddMinutes(1), 0);
        var conflict = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Store.CompleteAsync("job-2", 1, claim2.Revision, outcome,
                Completed("job-1", claim1.Revision + 1) with { JobId = "job-2", ReviewId = "review-2" }));
        Assert.AreEqual(ReviewErrorCodes.Conflict, conflict.Code);
        Assert.AreEqual(ReviewJobState.Running, (await host.Store.GetAsync("job-2"))!.State,
            "A rejected completion must not leave a terminal job without its event.");
        Assert.AreEqual(1, await host.EventCountAsync());
    }
    [TestMethod]
    public async Task Fail_RequiresRunningStateAndAValidTarget()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");
        var claim = await host.Store.ClaimAsync("job-1", 1, DateTimeOffset.UtcNow.AddMinutes(1), 0);

        var invalidState = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Store.FailAsync("job-1", 1, claim.Revision, ReviewJobState.Completed, "reason"));
        Assert.AreEqual(ReviewErrorCodes.RequestInvalid, invalidState.Code);

        var failed = await host.Store.FailAsync("job-1", 1, claim.Revision, ReviewJobState.Unknown, "provider-timeout");
        Assert.AreEqual(ReviewJobState.Unknown, failed.State);
        Assert.AreEqual("provider-timeout", failed.ReasonCode);
    }

    [TestMethod]
    public async Task Cancel_OnlyNonTerminalJobs_WithRevisionFence()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");
        var cancelled = await host.Store.CancelAsync("job-1", 0, "user-cancelled");
        Assert.AreEqual(ReviewJobState.Cancelled, cancelled.State);

        var revisionConflict = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Store.CancelAsync("job-1", 0, "again"));
        Assert.AreEqual(ReviewErrorCodes.RevisionConflict, revisionConflict.Code);
        var terminal = await Assert.ThrowsExactlyAsync<ReviewException>(async () =>
            await host.Store.CancelAsync("job-1", cancelled.Revision, "again"));
        Assert.AreEqual(ReviewErrorCodes.StateInvalid, terminal.Code);
    }

    [TestMethod]
    public async Task State_SurvivesANewDbContext()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.CreateOrGetAsync(Record("job-1", "review-1"), "op-1");
        var claim = await host.Store.ClaimAsync("job-1", 3, DateTimeOffset.UtcNow.AddMinutes(1), 0);

        var second = new ReviewJobStore(host.Factory);
        var view = await second.GetAsync("job-1");
        Assert.AreEqual(ReviewJobState.Running, view!.State);
        Assert.AreEqual(3L, view.OwnerEpoch);
        Assert.AreEqual(claim.Revision, view.Revision);
    }

    private static ReviewCompletedEvent Completed(string jobId, long revision) => new("evt-" + jobId + "-" + revision,
        "review-1", jobId, Key(), ReviewFindingValue.NeedsWork, ReviewEvidenceLevel.Observed, revision,
        DateTimeOffset.UtcNow);

    private static ReviewLogicalKey Key(string artifactSetHash = "artifact-hash",
        string objectiveVersion = "objective-v1") => new("ws-1", null, "goal-1", 1, 2,
        objectiveVersion, "acceptance-v1", artifactSetHash, "policy-v1");

    private static ReviewJobRecord Record(string jobId, string reviewId) => new(jobId, reviewId, Key(), Required: true,
        "budget-1", DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow.AddMinutes(-1), AcceptedSequence: 7);

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, ReviewJobStore store,
        IDbContextFactory<PlatformDbContext> factory) : IAsyncDisposable
    {
        public ReviewJobStore Store { get; } = store;
        public IDbContextFactory<PlatformDbContext> Factory { get; } = factory;

        public async Task<int> EventCountAsync()
        {
            await using var db = Factory.CreateDbContext();
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
            return new(connection, new ReviewJobStore(factory), factory);
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
