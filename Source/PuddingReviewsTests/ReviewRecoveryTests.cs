using PuddingReviews;
using PuddingReviews.Contracts;

namespace PuddingReviewsTests;

[TestClass]
public sealed class ReviewRecoveryTests
{
    [TestMethod]
    public async Task SnapshotRequired_LeavesTheJobWaiting_WithoutCallingTheModel()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        host.Evidence = new ReviewEvidenceReadResult(null, ReviewErrorCodes.SnapshotRequired);

        var run = await coordinator.RunOnceAsync(job.JobId, 1, TimeSpan.FromMinutes(1));
        Assert.AreEqual(ReviewJobState.Waiting, run.Job.State);
        Assert.AreEqual(ReviewErrorCodes.SnapshotRequired, run.Job.ReasonCode);
        Assert.AreEqual(0, host.Executions);
        Assert.AreEqual(0, host.Events.Count);
    }

    [TestMethod]
    public async Task EvidenceReaderThrowingSnapshotRequired_IsASnapshotSignal()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        host.EvidenceFailure = new ReviewException(ReviewErrorCodes.SnapshotRequired, "retention window passed.");
        var run = await coordinator.RunOnceAsync(job.JobId, 1, TimeSpan.FromMinutes(1));
        Assert.AreEqual(ReviewJobState.Waiting, run.Job.State);
        Assert.AreEqual(ReviewErrorCodes.SnapshotRequired, run.Job.ReasonCode);
        Assert.AreEqual(0, host.Executions);
    }

    [TestMethod]
    public async Task MissingEvidence_StaysWaitingRatherThanPassing()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        var run = await coordinator.RunOnceAsync(job.JobId, 1, TimeSpan.FromMinutes(1));
        Assert.AreEqual(ReviewJobState.Waiting, run.Job.State);
        Assert.AreEqual(ReviewErrorCodes.EvidenceUnavailable, run.Job.ReasonCode);
        Assert.AreEqual(0, host.Events.Count, "A required review that cannot read evidence must not complete.");
    }

    [TestMethod]
    public async Task EvidenceFailure_MarksUnknown_AndDoesNotPublish()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        host.EvidenceFailure = new InvalidOperationException("store unavailable");
        var run = await coordinator.RunOnceAsync(job.JobId, 1, TimeSpan.FromMinutes(1));
        Assert.AreEqual(ReviewJobState.Unknown, run.Job.State);
        Assert.AreEqual(ReviewErrorCodes.ExecutorUnknown, run.Job.ReasonCode);
        Assert.AreEqual(0, host.Executions);
    }

    [TestMethod]
    public async Task WaitingJob_CanBeReclaimedByAnExplicitNewOwner_AndThenCompletes()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        var waiting = await coordinator.RunOnceAsync(job.JobId, 1, TimeSpan.FromMinutes(1));
        Assert.AreEqual(ReviewJobState.Waiting, waiting.Job.State);

        host.EvidenceFactory = view => new ReviewEvidenceReadResult(ReviewFixtures.Snapshot(view), null);
        host.Model = snapshot => ReviewFixtures.Result(snapshot);
        var retried = await coordinator.RunOnceAsync(job.JobId, ownerEpoch: 2, TimeSpan.FromMinutes(1));
        Assert.AreEqual(ReviewJobState.Completed, retried.Job.State);
        Assert.AreEqual(1, host.Events.Count);
    }

    [TestMethod]
    public async Task CompletedEvent_CarriesFindingLevelAndRevision()
    {
        var (host, coordinator) = ReviewFixtures.New();
        var job = await ReviewFixtures.SubmitAsync(host, coordinator);
        host.EvidenceFactory = view => new ReviewEvidenceReadResult(ReviewFixtures.Snapshot(view), null);
        host.Model = snapshot => ReviewFixtures.Result(snapshot);
        var run = await coordinator.RunOnceAsync(job.JobId, 1, TimeSpan.FromMinutes(1));
        var published = host.Events.Single();
        Assert.AreEqual(run.Job.Revision, published.Revision);
        Assert.AreEqual(run.Job.ReviewId, published.ReviewId);
        Assert.AreEqual(ReviewFindingValue.Satisfied, published.Finding);
        Assert.AreEqual(ReviewEvidenceLevel.Observed, published.Level);
        Assert.IsTrue(published.EventId.Length > 0);
    }
}
