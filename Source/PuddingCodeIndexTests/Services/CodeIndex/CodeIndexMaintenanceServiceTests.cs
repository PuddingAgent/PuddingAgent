using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;
using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// U3-B1, part 2: the change-capture pipeline must actually drive indexing, batch payload must stay
/// visible, and shutdown must be explicit and bounded.
/// </summary>
[TestClass]
public sealed class CodeIndexMaintenanceServiceTests
{
    /// <summary>I2 — consuming a batch must invoke the indexer, not idle.</summary>
    [TestMethod]
    public async Task Batch_From_The_Change_Pipeline_Triggers_A_Real_Indexing_Run()
    {
        using var harness = new Harness();
        await harness.StartWithActiveScopeAsync();

        Assert.IsTrue(harness.PublishChange("a.cs", IndexChangeKind.Created));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync(), "the debounce window has not elapsed yet");

        harness.AdvancePastDebounce();

        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(1, harness.Indexer.CallCount, "consuming a batch must invoke the indexer");
        Assert.AreEqual(1, harness.Service.BatchesProcessed);
        Assert.IsTrue(
            string.Equals(
                Path.GetFullPath(harness.Root).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(harness.Indexer.CalledProjectPaths[0]).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase),
            "the indexer must be called for the scope root");

        var status = harness.Status();
        Assert.AreEqual(1, status.ObservedVersion);
        Assert.AreEqual(1, status.DesiredVersion);
        Assert.AreEqual(1, status.CommittedVersion);
        Assert.IsFalse(status.IndexPending);
        Assert.IsFalse(status.IndexInFlight);
        Assert.IsTrue(status.WatcherAttached);
        Assert.IsTrue(status.RootPath.Length > 0);
    }

    /// <summary>I3 — a reconcile request must become visible state and be counted, never swallowed.</summary>
    [TestMethod]
    public async Task Reconcile_Request_Is_Exposed_And_Triggers_A_Scope_Re_Index()
    {
        using var harness = new Harness(queueCapacity: 1);
        await harness.StartWithActiveScopeAsync();

        // The U3-A watcher records an overflow on the scope state instead of dropping the observation.
        Assert.IsTrue(harness.PublishChange("seed.cs", IndexChangeKind.Created));
        Assert.IsFalse(harness.PublishChange("overflow.cs", IndexChangeKind.Created), "the queue is full");

        var state = harness.Watcher.State;
        Assert.AreEqual(1, state.RecordOverflow());
        state.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.QueueOverflow);

        harness.AdvancePastDebounce();

        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(1, harness.Service.ReconcileRequests);
        Assert.AreEqual(1, harness.Service.PendingReconcileScopeCount);

        var status = harness.Status();
        Assert.IsTrue(status.NeedsReconcile, "the reconcile request must stay visible");
        Assert.AreEqual(CodeIndexScopeState.ReconcileReasons.QueueOverflow, status.ReconcileReason);
        Assert.AreEqual(1, status.ReconcileRequestCount);
        Assert.AreEqual(1, harness.Indexer.CallCount, "a scope reconcile must trigger a real re-index");
    }

    /// <summary>I4 — removal paths must be observable, not silently ignored.</summary>
    [TestMethod]
    public async Task Removal_Paths_Are_Counted_And_Exposed()
    {
        using var harness = new Harness();
        await harness.StartWithActiveScopeAsync();

        Assert.IsTrue(harness.PublishChange("gone.cs", IndexChangeKind.Deleted));
        harness.AdvancePastDebounce();

        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());

        var status = harness.Status();
        Assert.AreEqual(1, status.RemovalObservationCount);
        Assert.AreEqual(1, status.LastRemovalPaths.Count);
        Assert.AreEqual(MaintenanceTestData.Combine(harness.Root, "gone.cs"), status.LastRemovalPaths[0]);
        Assert.AreEqual(1, harness.Service.RemovalObservations);
        Assert.AreEqual(1, harness.Indexer.CallCount, "the scope is re-indexed at scope granularity");
    }

    /// <summary>I1 at the driver level: a change that lands during a re-index is not lost.</summary>
    [TestMethod]
    public async Task Change_Arriving_During_A_Re_Index_Is_Picked_Up_By_The_Next_Batch()
    {
        using var harness = new Harness();
        await harness.StartWithActiveScopeAsync();

        harness.Indexer.OnIndexAsync = (_, _) =>
        {
            if (harness.Indexer.CallCount == 1)
                harness.PublishChange("later.cs", IndexChangeKind.Changed);

            return Task.CompletedTask;
        };

        Assert.IsTrue(harness.PublishChange("first.cs", IndexChangeKind.Created));
        harness.AdvancePastDebounce();

        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(1, harness.Indexer.CallCount);

        harness.AdvancePastDebounce();

        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync(),
            "the change that arrived during indexing must produce another batch");
        Assert.AreEqual(2, harness.Indexer.CallCount);
        Assert.AreEqual(2, harness.Service.BatchesProcessed);
    }

    /// <summary>I5 — an in-flight run is cancelled explicitly, the job stays queued, and stop is bounded.</summary>
    [TestMethod]
    public async Task Stop_Cancels_An_In_Flight_Run_Without_Losing_The_Job()
    {
        using var harness = new Harness(pollInterval: TimeSpan.FromMilliseconds(50));
        await harness.StartWithActiveScopeAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Indexer.OnIndexAsync = (_, token) => gate.Task.WaitAsync(token);

        Assert.IsTrue(harness.PublishChange("a.cs", IndexChangeKind.Created));
        harness.AdvancePastDebounce();

        await harness.Indexer.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var stopwatch = Stopwatch.StartNew();
        await harness.Service.StopAsync();
        stopwatch.Stop();

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"stop took {stopwatch.Elapsed}");
        Assert.IsFalse(harness.Service.IsRunning);
        Assert.AreEqual(0, harness.Service.AbandonedBatches,
            "the run observed cancellation, so the bounded wait was not needed");
        Assert.IsTrue(harness.Watcher.Stopped, "the change source must be detached on stop");
        Assert.IsTrue(harness.Watcher.Disposed);

        var progress = harness.Scheduler.GetProgress(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
        Assert.IsTrue(progress.Pending, "the cancelled job must stay queued instead of vanishing");
        Assert.IsFalse(progress.InFlight);

        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync(), "a stopped driver processes nothing");
        Assert.IsFalse(harness.Service.EnsureScope("workspace-2", "scope-2", harness.Root));
        Assert.AreEqual(1, harness.Service.RejectedScopeRequests);
    }

    /// <summary>I5 — stop returns within its timeout even when a run refuses to observe cancellation.</summary>
    [TestMethod]
    public async Task Stop_Returns_Within_The_Timeout_Even_If_A_Run_Ignores_Cancellation()
    {
        using var harness = new Harness(
            pollInterval: TimeSpan.FromMilliseconds(50),
            stopTimeout: TimeSpan.FromMilliseconds(200));
        await harness.StartWithActiveScopeAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Indexer.OnIndexAsync = (_, _) => gate.Task; // deliberately ignores the token

        Assert.IsTrue(harness.PublishChange("a.cs", IndexChangeKind.Created));
        harness.AdvancePastDebounce();

        await harness.Indexer.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var stopwatch = Stopwatch.StartNew();
        await harness.Service.StopAsync();
        stopwatch.Stop();

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"stop must be bounded, took {stopwatch.Elapsed}");
        Assert.AreEqual(1, harness.Service.AbandonedBatches,
            "a run that ignores cancellation must be reported, not hidden");
        Assert.IsFalse(harness.Service.IsRunning);

        gate.SetResult();
        await Task.Delay(250); // let the abandoned run unwind before the fixture is torn down
    }

    /// <summary>I5 — a stopped driver neither attaches scopes nor processes observations.</summary>
    [TestMethod]
    public async Task Stopped_Driver_Accepts_No_New_Scope_And_Processes_Nothing()
    {
        using var harness = new Harness();
        await harness.StartWithActiveScopeAsync();

        await harness.Service.StopAsync();

        Assert.IsFalse(harness.Service.IsRunning);
        Assert.IsFalse(harness.Service.EnsureScope("workspace-2", "scope-2", harness.Root));
        Assert.AreEqual(1, harness.Service.RejectedScopeRequests);
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
    }

    /// <summary>Scopes can only be attached while the driver runs; attaching twice is a no-op.</summary>
    [TestMethod]
    public async Task Scope_Can_Only_Be_Attached_While_The_Driver_Is_Running()
    {
        using var harness = new Harness();

        Assert.IsFalse(harness.Service.EnsureScope(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, harness.Root));
        Assert.AreEqual(1, harness.Service.RejectedScopeRequests);
        Assert.IsNull(harness.Service.GetScopeStatus(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId));

        await harness.Service.StartAsync();
        await harness.Service.StartAsync(); // idempotent

        Assert.IsTrue(harness.Service.IsRunning);
        Assert.IsTrue(harness.Service.EnsureScope(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, harness.Root));
        Assert.IsFalse(harness.Service.EnsureScope(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, harness.Root),
            "attaching the same scope twice is a no-op");
        Assert.AreEqual(1, harness.Service.RejectedScopeRequests, "a duplicate is not a rejection");
        Assert.AreEqual(1, harness.Service.GetScopeStatuses().Count);
    }

    /// <summary>Wires a real scheduler, the real store and a fake change source together.</summary>
    private sealed class Harness : IDisposable
    {
        public Harness(
            TimeSpan? pollInterval = null,
            TimeSpan? stopTimeout = null,
            int queueCapacity = CodeIndexChangeQueue.DefaultCapacity)
        {
            Fixture = CodeIndexFixture.Create();
            Clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));
            Indexer = new RecordingCodeIndexer();
            Scheduler = new CodeIndexScheduler(
                Indexer,
                new DefaultCodeWorkspaceResolver(Fixture.Store),
                Fixture.Store,
                NullLogger<CodeIndexScheduler>.Instance);
            WatcherFactory = new FakeCodeIndexWatcherFactory();
            Service = new CodeIndexMaintenanceService(
                Scheduler,
                WatcherFactory,
                NullLogger<CodeIndexMaintenanceService>.Instance,
                Clock,
                queueCapacity: queueCapacity,
                pollInterval: pollInterval ?? TimeSpan.FromHours(1),
                stopTimeout: stopTimeout ?? CodeIndexMaintenanceService.DefaultStopTimeout);
        }

        public CodeIndexFixture Fixture { get; }

        public MutableTimeProvider Clock { get; }

        public RecordingCodeIndexer Indexer { get; }

        public CodeIndexScheduler Scheduler { get; }

        public FakeCodeIndexWatcherFactory WatcherFactory { get; }

        public CodeIndexMaintenanceService Service { get; }

        public string Root => Fixture.Root;

        public FakeCodeIndexChangeWatcher Watcher => WatcherFactory.Watcher;

        public async Task StartWithActiveScopeAsync()
        {
            await Fixture.Store.UpsertProjectAsync(
                new CodeProjectRecord(
                    MaintenanceTestData.WorkspaceId,
                    MaintenanceTestData.ScopeId,
                    Root,
                    CodeProjectStatus.Active));

            await Service.StartAsync();
            Assert.IsTrue(Service.EnsureScope(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, Root));
            Assert.IsTrue(Watcher.Started);
        }

        /// <summary>Publishes an observation the way the real watcher callback does.</summary>
        public bool PublishChange(string relativePath, IndexChangeKind kind, string? oldRelativePath = null)
        {
            var observedAt = Clock.GetUtcNow();
            var sequence = Watcher.State.NextSequence();
            Watcher.State.MarkObserved(observedAt);

            return Watcher.Queue.TryPublish(
                MaintenanceTestData.Change(Root, relativePath, kind, sequence, observedAt, oldRelativePath));
        }

        public void AdvancePastDebounce() => Clock.Advance(TimeSpan.FromSeconds(3));

        public CodeIndexMaintenanceScopeStatus Status() =>
            Service.GetScopeStatus(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId)
            ?? throw new InvalidOperationException("the scope is not attached");

        public void Dispose()
        {
            Service.Dispose();
            Scheduler.Dispose();
            Fixture.Dispose();
        }
    }
}
