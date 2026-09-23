using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// U3-B1, part 1: the scheduler may no longer drop a request that arrives while a scope is being
/// indexed, and it must not run any background loop of its own.
/// </summary>
[TestClass]
public sealed class CodeIndexSchedulerTests
{
    /// <summary>
    /// I1 — a request that lands while the scope is being indexed must be indexed afterwards, not
    /// discarded (this is the defect that could leave an index stale forever).
    /// </summary>
    [TestMethod]
    public async Task Request_Arriving_While_A_Scope_Is_Being_Indexed_Is_Processed_Afterwards()
    {
        using var fixture = CodeIndexFixture.Create();
        await fixture.Store.UpsertProjectAsync(
            new CodeProjectRecord(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, fixture.Root, CodeProjectStatus.Active));

        var indexer = new RecordingCodeIndexer();
        using var scheduler = CreateScheduler(fixture, indexer);

        // The second request arrives in the middle of the first indexing run.
        indexer.OnIndexAsync = (_, _) =>
        {
            if (indexer.CallCount == 1)
                scheduler.Enqueue(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);

            return Task.CompletedTask;
        };

        scheduler.Enqueue(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
        var completed = await scheduler.ProcessPendingAsync();

        Assert.AreEqual(2, indexer.CallCount, "the request that arrived during indexing must be indexed as well");
        Assert.AreEqual(2, completed, "both runs completed");

        var progress = scheduler.GetProgress(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
        Assert.AreEqual(2, progress.DesiredVersion);
        Assert.AreEqual(2, progress.CommittedVersion);
        Assert.AreEqual(1, progress.MarkedWhileInFlightCount,
            "the request that arrived while indexing must be counted, not swallowed");
        Assert.IsFalse(progress.Behind, "the scope must not be left behind the desired version");
        Assert.IsFalse(progress.Pending);
        Assert.IsFalse(progress.InFlight);
        Assert.IsFalse(scheduler.IsIndexing(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId));
        Assert.AreEqual(0, scheduler.GetQueueDepth(MaintenanceTestData.WorkspaceId));
    }

    /// <summary>I1 — repeated requests during indexing collapse into exactly one extra run.</summary>
    [TestMethod]
    public async Task Repeated_Requests_During_Indexing_Collapse_Into_One_Extra_Run()
    {
        using var fixture = CodeIndexFixture.Create();
        await fixture.Store.UpsertProjectAsync(
            new CodeProjectRecord(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, fixture.Root, CodeProjectStatus.Active));

        var indexer = new RecordingCodeIndexer();
        using var scheduler = CreateScheduler(fixture, indexer);

        indexer.OnIndexAsync = (_, _) =>
        {
            if (indexer.CallCount == 1)
            {
                for (var i = 0; i < 5; i++)
                    scheduler.Enqueue(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
            }

            return Task.CompletedTask;
        };

        scheduler.Enqueue(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
        await scheduler.ProcessPendingAsync();

        Assert.AreEqual(2, indexer.CallCount, "five requests during one run must collapse into one follow-up run");

        var progress = scheduler.GetProgress(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
        Assert.AreEqual(6, progress.DesiredVersion, "every accepted request advances the desired version");
        Assert.AreEqual(6, progress.CommittedVersion);
        Assert.AreEqual(5, progress.MarkedWhileInFlightCount);
    }

    /// <summary>I1/I5 — a cancelled pump leaves the job queued instead of dropping it.</summary>
    [TestMethod]
    public async Task Cancelled_Pump_Re_Queues_The_Job_Instead_Of_Dropping_It()
    {
        using var fixture = CodeIndexFixture.Create();
        await fixture.Store.UpsertProjectAsync(
            new CodeProjectRecord(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, fixture.Root, CodeProjectStatus.Active));

        var indexer = new RecordingCodeIndexer();
        using var scheduler = CreateScheduler(fixture, indexer);

        using var cts = new CancellationTokenSource();
        indexer.OnIndexAsync = (_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        scheduler.Enqueue(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);

        var cancelled = false;
        try
        {
            await scheduler.ProcessPendingAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert.IsTrue(cancelled, "the pump must surface the cancellation");

        var progress = scheduler.GetProgress(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
        Assert.IsTrue(progress.Pending, "a cancelled run must leave the scope queued");
        Assert.AreEqual(1, scheduler.GetQueueDepth(MaintenanceTestData.WorkspaceId));
        Assert.IsFalse(progress.InFlight);

        // The queued job is still runnable once a caller pumps again.
        var completed = await scheduler.ProcessPendingAsync();
        Assert.AreEqual(1, completed);
        Assert.AreEqual(2, indexer.CallCount);
    }

    /// <summary>
    /// I6 — constructing the scheduler must not start any background loop: enqueued work stays untouched
    /// until a caller pumps it.
    /// </summary>
    [TestMethod]
    public async Task Scheduler_Does_Not_Run_Anything_Without_An_Explicit_Pump()
    {
        using var fixture = CodeIndexFixture.Create();
        await fixture.Store.UpsertProjectAsync(
            new CodeProjectRecord(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, fixture.Root, CodeProjectStatus.Active));

        var indexer = new RecordingCodeIndexer();
        using var scheduler = CreateScheduler(fixture, indexer);

        scheduler.Enqueue(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
        await Task.Delay(300);

        Assert.AreEqual(0, indexer.CallCount, "the scheduler must not own a background worker");

        var progress = scheduler.GetProgress(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
        Assert.AreEqual(1, progress.DesiredVersion);
        Assert.AreEqual(0, progress.CommittedVersion, "nothing may be committed before an explicit pump");
        Assert.IsFalse(progress.InFlight);
        Assert.IsTrue(progress.Pending);
        Assert.IsTrue(progress.Behind, "still behind until the caller pumps");
        Assert.IsFalse(scheduler.IsIndexing(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId));

        Assert.AreEqual(1, await scheduler.ProcessPendingAsync());
        Assert.AreEqual(1, indexer.CallCount);
    }

    /// <summary>Enqueueing the same scope twice while it is only queued keeps a single queue entry.</summary>
    [TestMethod]
    public async Task Duplicate_Enqueue_While_Queued_Keeps_A_Single_Queue_Entry()
    {
        using var fixture = CodeIndexFixture.Create();
        await fixture.Store.UpsertProjectAsync(
            new CodeProjectRecord(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, fixture.Root, CodeProjectStatus.Active));

        var indexer = new RecordingCodeIndexer();
        using var scheduler = CreateScheduler(fixture, indexer);

        scheduler.Enqueue(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
        scheduler.Enqueue(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
        scheduler.Enqueue(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);

        Assert.AreEqual(1, scheduler.GetQueueDepth(MaintenanceTestData.WorkspaceId));
        Assert.AreEqual(1, await scheduler.ProcessPendingAsync());
        Assert.AreEqual(1, indexer.CallCount);
    }

    /// <summary>A removed scope is skipped, and the attempt is not reported as a completed run.</summary>
    [TestMethod]
    public async Task Removed_Scope_Is_Skipped_Without_A_Completed_Run()
    {
        using var fixture = CodeIndexFixture.Create();
        await fixture.Store.UpsertProjectAsync(
            new CodeProjectRecord(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, fixture.Root, CodeProjectStatus.Removed));

        var indexer = new RecordingCodeIndexer();
        using var scheduler = CreateScheduler(fixture, indexer);

        scheduler.Enqueue(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);

        Assert.AreEqual(0, await scheduler.ProcessPendingAsync());
        Assert.AreEqual(0, indexer.CallCount);
    }

    /// <summary>Unknown scopes report zeroed water marks instead of throwing.</summary>
    [TestMethod]
    public void Progress_Of_An_Unknown_Scope_Is_Zeroed()
    {
        using var fixture = CodeIndexFixture.Create();
        using var scheduler = CreateScheduler(fixture, new RecordingCodeIndexer());

        var progress = scheduler.GetProgress("nope", "nope");

        Assert.AreEqual(0, progress.DesiredVersion);
        Assert.AreEqual(0, progress.CommittedVersion);
        Assert.IsFalse(progress.InFlight);
        Assert.IsFalse(progress.Pending);
        Assert.AreEqual(0, progress.MarkedWhileInFlightCount);
    }

    private static CodeIndexScheduler CreateScheduler(CodeIndexFixture fixture, RecordingCodeIndexer indexer) =>
        new(
            indexer,
            new DefaultCodeWorkspaceResolver(fixture.Store),
            fixture.Store,
            NullLogger<CodeIndexScheduler>.Instance);
}
