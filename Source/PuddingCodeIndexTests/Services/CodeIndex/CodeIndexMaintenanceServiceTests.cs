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
    /// <summary>
    /// I2 (U3-B3 form) — consuming a batch must index the changed file itself: a one-file change is applied
    /// per file and must not force a full scope re-index.
    /// </summary>
    [TestMethod]
    public async Task Batch_From_The_Change_Pipeline_Indexes_The_Changed_File_Without_A_Full_Scope_Run()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        var changedFile = harness.Combine("a.cs");
        await File.WriteAllTextAsync(changedFile, "class A { }");

        Assert.IsTrue(harness.PublishChange("a.cs", IndexChangeKind.Created));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync(), "the debounce window has not elapsed yet");

        harness.AdvancePastDebounce();

        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(1, harness.Service.BatchesProcessed);
        Assert.HasCount(1, harness.Indexer.IndexedFiles, "consuming a batch must invoke the indexer");
        Assert.AreEqual(changedFile, harness.Indexer.IndexedFiles[0], "the changed file itself must be indexed");
        Assert.AreEqual(0, harness.Indexer.CallCount, "a single-file change must not trigger a full scope re-index");

        var status = harness.Status();
        Assert.AreEqual(1, status.ObservedVersion);
        Assert.AreEqual(0, status.DesiredVersion, "no scope-level job was requested for a per-file change");
        Assert.AreEqual(1, status.IncrementallyIndexedFileCount);
        Assert.AreEqual(0, status.ScopeEscalationCount);
        Assert.IsFalse(status.IndexPending);
        Assert.IsFalse(status.IndexInFlight);
        Assert.IsTrue(status.WatcherAttached);
        Assert.IsTrue(status.RootPath.Length > 0);
    }

    /// <summary>
    /// I3 — a reconcile request must become visible state, be counted, and force a scope-level re-index, never be
    /// swallowed. Since U3-C it is also <b>resolved</b>, not only exposed: the calibration at the end of the same
    /// step sweeps the scope and clears the flag. The "refused ⇒ stays flagged" half is locked by
    /// <c>CodeIndexCalibrationDriverTests</c>.
    /// </summary>
    [TestMethod]
    public async Task Reconcile_Request_Is_Counted_Triggers_A_Scope_Re_Index_And_Is_Resolved_By_Calibration()
    {
        using var harness = new MaintenanceHarness(queueCapacity: 1);
        await harness.StartWithActiveScopeAsync();

        // The U3-A watcher records an overflow on the scope state instead of dropping the observation.
        Assert.IsTrue(harness.PublishChange("seed.cs", IndexChangeKind.Created));
        Assert.IsFalse(harness.PublishChange("overflow.cs", IndexChangeKind.Created), "the queue is full");

        var state = harness.Watcher.State;
        Assert.AreEqual(1, state.RecordOverflow());
        state.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.QueueOverflow);
        Assert.IsTrue(state.NeedsReconcile, "the flag is set before the step runs");

        harness.AdvancePastDebounce();

        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(1, harness.Service.ReconcileRequests, "the request must be counted, never swallowed");

        var status = harness.Status();
        Assert.AreEqual(1, status.ReconcileRequestCount);
        Assert.AreEqual(1, harness.Indexer.CallCount, "a scope reconcile must trigger a real re-index");
        Assert.AreEqual(1, status.CalibrationRunCount, "U3-C: the flagged scope is calibrated in the same step");
        Assert.AreEqual(0, status.SweptFileCount, "nothing was stale in this scenario");
        Assert.IsFalse(status.NeedsReconcile, "U3-C: a successful calibration resolves the request");
        Assert.AreEqual(0, harness.Service.PendingReconcileScopeCount);
    }

    /// <summary>
    /// I4 (U3-B3 form) — a removal path is applied to the store, not only counted, and a deletion alone must
    /// not trigger a full scope re-index. Removing a path that was never indexed stays a safe no-op.
    /// </summary>
    [TestMethod]
    public async Task Removal_Paths_Are_Applied_To_The_Store_And_Exposed()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        Assert.IsTrue(harness.PublishChange("gone.cs", IndexChangeKind.Deleted));
        harness.AdvancePastDebounce();

        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());

        var status = harness.Status();
        Assert.AreEqual(1, status.RemovalObservationCount);
        Assert.AreEqual(1, status.LastRemovalPaths.Count);
        Assert.AreEqual(MaintenanceTestData.Combine(harness.Root, "gone.cs"), status.LastRemovalPaths[0]);
        Assert.AreEqual(1, harness.Service.RemovalObservations);
        Assert.AreEqual(0, status.RemovedFileCount, "a path that was never indexed is a safe no-op");
        Assert.AreEqual(0, harness.Indexer.CallCount, "a deletion must not trigger a full scope re-index");
        Assert.AreEqual(0, status.ScopeEscalationCount);
    }

    /// <summary>I1 at the driver level: a change that lands during a per-file index is not lost.</summary>
    [TestMethod]
    public async Task Change_Arriving_During_A_Per_File_Index_Is_Picked_Up_By_The_Next_Batch()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        await File.WriteAllTextAsync(harness.Combine("first.cs"), "class First { }");
        await File.WriteAllTextAsync(harness.Combine("later.cs"), "class Later { }");

        harness.Indexer.OnIndexFileAsync = (_, _, _) =>
        {
            if (harness.Indexer.IndexedFiles.Count == 1)
                harness.PublishChange("later.cs", IndexChangeKind.Changed);

            return Task.CompletedTask;
        };

        Assert.IsTrue(harness.PublishChange("first.cs", IndexChangeKind.Created));
        harness.AdvancePastDebounce();

        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());
        Assert.HasCount(1, harness.Indexer.IndexedFiles);

        harness.AdvancePastDebounce();

        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync(),
            "the change that arrived during indexing must produce another batch");
        Assert.HasCount(2, harness.Indexer.IndexedFiles);
        Assert.AreEqual(harness.Combine("later.cs"), harness.Indexer.IndexedFiles[1]);
        Assert.AreEqual(2, harness.Service.BatchesProcessed);
    }

    /// <summary>I5 — an in-flight run is cancelled explicitly, the job stays queued, and stop is bounded.</summary>
    [TestMethod]
    public async Task Stop_Cancels_An_In_Flight_Run_Without_Losing_The_Job()
    {
        using var harness = new MaintenanceHarness(pollInterval: TimeSpan.FromMilliseconds(50));
        await harness.StartWithActiveScopeAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Indexer.OnIndexAsync = (_, token) => gate.Task.WaitAsync(token);

        // Enqueued the way code_index_register_project does it: a scope-level run the driver has to pump.
        harness.Scheduler.Enqueue(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);

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
        using var harness = new MaintenanceHarness(
            pollInterval: TimeSpan.FromMilliseconds(50),
            stopTimeout: TimeSpan.FromMilliseconds(200));
        await harness.StartWithActiveScopeAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Indexer.OnIndexAsync = (_, _) => gate.Task; // deliberately ignores the token

        // Enqueued the way code_index_register_project does it: a scope-level run the driver has to pump.
        harness.Scheduler.Enqueue(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);

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
        using var harness = new MaintenanceHarness();
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
        using var harness = new MaintenanceHarness();

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

}
