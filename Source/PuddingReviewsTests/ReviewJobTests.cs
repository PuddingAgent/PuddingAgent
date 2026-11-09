using System.Collections.Immutable;
using PuddingReviews;
using PuddingReviews.Contracts;

namespace PuddingReviewsTests;

[TestClass]
public sealed class ReviewJobTests
{
    [TestMethod]
    public async Task Submit_IsIdempotentByLogicalKey_AndOperationReuseConflicts()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var first = await coordinator.SubmitAsync(ReviewFixtures.Record(), "op-1");
        var replaySameOperation = await coordinator.SubmitAsync(ReviewFixtures.Record(), "op-1");
        var replayNewOperation = await coordinator.SubmitAsync(ReviewFixtures.Record(), "op-2");
        Assert.IsTrue(first.Created);
        Assert.IsFalse(replaySameOperation.Created);
        Assert.IsFalse(replayNewOperation.Created, "The same logical key is accepted once even under a new operation id.");
        Assert.AreEqual(first.Job.JobId, replayNewOperation.Job.JobId);

        var conflicting = await ReviewFixtures.FailsAsync(() =>
            coordinator.SubmitAsync(ReviewFixtures.Record(ReviewFixtures.Key(artifactHash: "artifact-hash-2")), "op-1"));
        Assert.AreEqual(ReviewErrorCodes.Conflict, conflicting.Code);
    }

    [TestMethod]
    public async Task Submit_DeadlineOutOfBounds_IsRejected()
    {
        var (_, coordinator) = ReviewFixtures.New();
        var past = ReviewFixtures.Record() with { Deadline = DateTimeOffset.UtcNow.AddMinutes(-1), AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(-2) };
        var failure = await ReviewFixtures.FailsAsync(() => coordinator.SubmitAsync(past, "op-1"));
        Assert.AreEqual(ReviewErrorCodes.RequestInvalid, failure.Code);
    }

    [TestMethod]
    public async Task Run_CompletesReview_AndPublishesExactlyOneEvent()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        host.EvidenceFactory = view => new ReviewEvidenceReadResult(ReviewFixtures.Snapshot(view), null);
        host.Model = snapshot => ReviewFixtures.Result(snapshot);

        var run = await coordinator.RunOnceAsync(job.JobId, ownerEpoch: 1, TimeSpan.FromMinutes(1));
        Assert.IsTrue(run.Executed);
        Assert.AreEqual(ReviewJobState.Completed, run.Job.State);
        Assert.AreEqual(ReviewFindingValue.Satisfied, run.Job.Outcome!.Finding);
        Assert.AreEqual(ReviewEvidenceLevel.Observed, run.Job.Outcome.Level);
        var published = host.Events.Single();
        Assert.AreEqual(run.Job.ReviewId, published.ReviewId);
        Assert.AreEqual(ReviewFindingValue.Satisfied, published.Finding);
        Assert.AreEqual(run.Job.Revision, published.Revision);
        Assert.AreEqual(1, host.Executions);
    }

    [TestMethod]
    public async Task Run_TerminalJob_IsNeverExecutedTwice()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        host.EvidenceFactory = view => new ReviewEvidenceReadResult(ReviewFixtures.Snapshot(view), null);
        host.Model = snapshot => ReviewFixtures.Result(snapshot);
        await coordinator.RunOnceAsync(job.JobId, 1, TimeSpan.FromMinutes(1));

        var replay = await coordinator.RunOnceAsync(job.JobId, 2, TimeSpan.FromMinutes(1));
        Assert.IsFalse(replay.Executed, "A terminal review must not re-run the model.");
        Assert.AreEqual(1, host.Executions);
        Assert.AreEqual(1, host.Events.Count);
    }

    [TestMethod]
    public async Task Run_StaleOwner_IsRejected()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        var current = (await host.GetAsync(job.JobId))!;
        await host.ClaimAsync(job.JobId, ownerEpoch: 2, DateTimeOffset.UtcNow.AddMinutes(1), current.Revision);
        var stale = await ReviewFixtures.FailsAsync(() =>
            coordinator.RunOnceAsync(job.JobId, ownerEpoch: 1, TimeSpan.FromMinutes(1)));
        Assert.AreEqual(ReviewErrorCodes.OwnerStale, stale.Code);
        Assert.AreEqual(0, host.Executions);
    }

    [TestMethod]
    public async Task Run_ExecutorFailure_MarksUnknown_WithoutPublishingCompletion()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        host.EvidenceFactory = view => new ReviewEvidenceReadResult(ReviewFixtures.Snapshot(view), null);
        host.ExecutionFailure = new InvalidOperationException("provider exploded");

        var run = await coordinator.RunOnceAsync(job.JobId, 1, TimeSpan.FromMinutes(1));
        Assert.AreEqual(ReviewJobState.Unknown, run.Job.State);
        Assert.AreEqual(ReviewErrorCodes.ExecutorUnknown, run.Job.ReasonCode);
        Assert.AreEqual(0, host.Events.Count, "A failed attempt must not publish a completion fact.");
    }

    [TestMethod]
    public async Task Run_ConcurrentClaims_ExecuteExactlyOnce()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        host.EvidenceFactory = view => new ReviewEvidenceReadResult(ReviewFixtures.Snapshot(view), null);
        host.Model = snapshot => ReviewFixtures.Result(snapshot);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            try { return (await coordinator.RunOnceAsync(job.JobId, 1, TimeSpan.FromMinutes(1))).Executed; }
            catch (ReviewException ex)
            {
                if (ex.Code is not (ReviewErrorCodes.RevisionConflict or ReviewErrorCodes.StateInvalid)) throw;
                return false;
            }
        })));
        Assert.AreEqual(1, outcomes.Count(executed => executed));
        Assert.AreEqual(1, host.Executions, "Only the winning claim may call the model.");
    }

    [TestMethod]
    public async Task Cancel_LateCompletion_IsRejected_AndStateStaysCancelled()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        host.EvidenceFactory = view => new ReviewEvidenceReadResult(ReviewFixtures.Snapshot(view), null);
        host.DuringExecution = () =>
        {
            var running = host.GetAsync(job.JobId).GetAwaiter().GetResult()!;
            host.CancelAsync(job.JobId, running.Revision, "user.cancelled").GetAwaiter().GetResult();
        };
        host.Model = snapshot => ReviewFixtures.Result(snapshot);

        var failure = await ReviewFixtures.FailsAsync(() =>
            coordinator.RunOnceAsync(job.JobId, 1, TimeSpan.FromMinutes(1)));
        Assert.AreEqual(ReviewErrorCodes.RevisionConflict, failure.Code);
        Assert.AreEqual(ReviewJobState.Cancelled, (await host.GetAsync(job.JobId))!.State);
        Assert.AreEqual(0, host.Events.Count, "A cancelled review must not publish a completion.");
    }

    [TestMethod]
    public async Task Get_MissingReview_IsNull_AndCancelNeedsTheCurrentRevision()
    {
        var (host, coordinator) = ReviewFixtures.New();
        Assert.IsNull(await coordinator.GetAsync("missing"));
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        var stale = await ReviewFixtures.FailsAsync(() => coordinator.CancelAsync(job.JobId, 9, "user.cancelled"));
        Assert.AreEqual(ReviewErrorCodes.RevisionConflict, stale.Code);
        var cancelled = await coordinator.CancelAsync(job.JobId, 0, "user.cancelled");
        Assert.AreEqual(ReviewJobState.Cancelled, cancelled.State);
    }
}
