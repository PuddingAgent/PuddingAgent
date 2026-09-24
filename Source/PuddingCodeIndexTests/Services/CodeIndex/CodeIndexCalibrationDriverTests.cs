using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;
using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// U3-C — calibration (mark-and-sweep) driven by the maintenance step: indexed paths that no longer exist on
/// disk are really removed, a successful sweep puts <c>NeedsReconcile</c> back in place, and a sweep that cannot
/// be trusted is refused instead of emptying the index.
/// <para>
/// A1 is the hard criterion of this slice. Its baseline was measured <b>before</b> the change (raw output kept in
/// <c>temp/u3c-A1-baseline-prefix-red.txt</c>): the step removed nothing, so a vanished file kept its symbol and
/// file rows and the index kept answering with a symbol of a file that is gone — <c>Assert.IsEmpty 失败。实际： 1</c>.
/// </para>
/// </summary>
[TestClass]
public sealed class CodeIndexCalibrationDriverTests
{
    /// <summary>
    /// A1: a file that was indexed and then disappeared <b>without any change event</b> (historical leftover,
    /// a whole directory deleted or renamed while the watcher was not observing, a lost watcher event) must
    /// stop being returned by the index once the flagged scope has been calibrated.
    /// </summary>
    [TestMethod]
    public async Task A1_A_Flagged_Scope_Is_Calibrated_And_The_Vanished_File_Is_Really_Removed()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        var gonePath = harness.Combine("legacy/gone.cs");
        await harness.SeedIndexedFileAsync("legacy/gone.cs", "LegacyClass");

        // Positive control: the seeded file *is* indexed and *is* returned, so a later empty result cannot be
        // explained by a dead query or a broken seed. Nothing is ever created on disk for it.
        Assert.IsFalse(File.Exists(gonePath), "the scenario starts from a file that is not on disk");
        Assert.HasCount(1, await harness.SearchSymbolsAsync("LegacyClass"));

        // The scope's fine-grained capture can no longer be trusted (watcher error / queue overflow / lost
        // events). No change batch is involved in this scenario at all.
        harness.Watcher.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError);
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        // The hard criterion.
        Assert.IsEmpty(
            await harness.SearchSymbolsAsync("LegacyClass"),
            "a file that no longer exists must not be returned by the index any more");
        Assert.IsEmpty(
            await harness.Store.GetSymbolsByFileAsync(
                MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, gonePath),
            "the vanished file must not keep symbol rows");
        Assert.IsFalse(
            (await harness.Store.ListFilesAsync(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId))
                .Any(file => string.Equals(file.FilePath, gonePath, StringComparison.OrdinalIgnoreCase)),
            "the vanished file must not keep a file record");

        Assert.IsFalse(harness.Status().NeedsReconcile, "a successful calibration clears the reconcile flag");
    }

    /// <summary>
    /// A2: the sweep removes <b>index rows only</b>. The files and directories of the scope must be exactly as the
    /// test left them, including the directory that held the vanished file and the files that are still there.
    /// </summary>
    [TestMethod]
    public async Task A2_A_Sweep_Never_Touches_The_File_System()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        var keptPath = harness.Combine("keep.cs");
        var subDirectory = harness.Combine("sub");
        var innerPath = harness.Combine("sub/inner.cs");
        Directory.CreateDirectory(subDirectory);
        await File.WriteAllTextAsync(keptPath, "class Keep { }");
        await File.WriteAllTextAsync(innerPath, "class Inner { }");

        await harness.SeedIndexedFileAsync("keep.cs", "KeepClass");
        await harness.SeedIndexedFileAsync("sub/inner.cs", "InnerClass");
        await harness.SeedIndexedFileAsync("sub/gone.cs", "GoneClass"); // never created on disk

        harness.Watcher.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError);
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var status = harness.Status();
        Assert.AreEqual(1, status.SweptFileCount, "exactly the vanished file is swept");
        Assert.IsFalse(status.NeedsReconcile);

        // The file system is untouched: nothing deleted, nothing created, nothing moved.
        Assert.IsTrue(File.Exists(keptPath), "a file that is still on disk must stay there");
        Assert.IsTrue(File.Exists(innerPath), "a file that is still on disk must stay there");
        Assert.IsTrue(Directory.Exists(subDirectory), "the directory that held the vanished file must stay there");
        Assert.IsFalse(File.Exists(harness.Combine("sub/gone.cs")), "the sweep must not create the missing file either");
        Assert.HasCount(1, Directory.GetFiles(subDirectory), "the directory must contain exactly the file it had");
        Assert.AreEqual(innerPath, Directory.GetFiles(subDirectory)[0]);

        // ...and the rows of the files that are still there are untouched too.
        Assert.HasCount(1, await harness.SearchSymbolsAsync("KeepClass"));
        Assert.HasCount(1, await harness.SearchSymbolsAsync("InnerClass"));
    }

    /// <summary>
    /// A3 (the dangerous case, and the one mutation A targets): when the scope root cannot be probed,
    /// "this path is not on disk" is true for every path, so the sweep must be refused outright — nothing removed,
    /// an error logged, the scope stays flagged, and the index keeps every row it had.
    /// </summary>
    [TestMethod]
    public async Task A3_An_Unusable_Root_Refuses_The_Sweep_And_Keeps_Every_Row()
    {
        var logger = new RecordingLogger<CodeIndexMaintenanceService>();
        using var harness = new MaintenanceHarness(logger: logger);

        // A root that is not there any more: the drive is not mounted / the network share is gone / the directory
        // was renamed away. It is deliberately never created.
        var offlineRoot = Path.Combine(harness.Root, "offline-scope");
        await harness.StartWithActiveScopeAsync(offlineRoot);

        var indexedPath = Path.Combine(offlineRoot, "a.cs");
        await harness.SeedIndexedAbsoluteFileAsync(indexedPath, "AClass");
        Assert.HasCount(1, await harness.SearchSymbolsAsync("AClass"), "positive control: the row is there before the run");

        harness.Watcher.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError);
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var status = harness.Status();
        Assert.AreEqual(0, status.SweptFileCount, "a refused sweep removes nothing");
        Assert.AreEqual(1, status.CalibrationRunCount, "the attempt is counted");
        Assert.AreEqual(1, status.RejectedCalibrationRunCount);
        Assert.AreEqual(CodeIndexScopeState.ReconcileReasons.CalibrationRootUnavailable, status.ReconcileReason);
        Assert.IsTrue(status.NeedsReconcile, "the scope must stay flagged while the root is unusable");
        Assert.AreEqual(1, harness.Service.PendingReconcileScopeCount);

        // The index was NOT emptied — which is exactly what a naive sweep would have done here.
        Assert.HasCount(1, await harness.SearchSymbolsAsync("AClass"));
        Assert.HasCount(1, await harness.Store.GetSymbolsByFileAsync(
            MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, indexedPath));
        Assert.HasCount(1, await harness.Store.ListFilesAsync(
            MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId));

        // ...and it said so: the refusal is an error, not silence.
        Assert.IsTrue(
            logger.Contains(LogLevel.Error, "Refusing to sweep"),
            "a refused sweep must be logged as an error");
        Assert.IsTrue(
            logger.Contains(LogLevel.Error, "calibration was refused"),
            "the driver must record the consequence it owns (the scope stays flagged)");
    }

    /// <summary>
    /// A4: the run is observable on the scope status — swept count, run count, refusals and the timestamp — and a
    /// refused run is counted too, so the numbers never hide an attempt. A later run accumulates.
    /// </summary>
    [TestMethod]
    public async Task A4_Calibration_Counters_Are_Exposed_And_Accumulate()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        var keptPath = harness.Combine("keep.cs");
        await File.WriteAllTextAsync(keptPath, "class Keep { }");
        await harness.SeedIndexedFileAsync("keep.cs", "KeepClass");
        await harness.SeedIndexedFileAsync("stale-one.cs", "StaleOne");

        Assert.IsNull(harness.Status().LastCalibrationAtUtc, "nothing ran yet");

        harness.Watcher.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.QueueOverflow);
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var first = harness.Status();
        Assert.AreEqual(1, first.SweptFileCount);
        Assert.AreEqual(1, first.CalibrationRunCount);
        Assert.AreEqual(0, first.RejectedCalibrationRunCount);
        Assert.AreEqual(harness.Clock.GetUtcNow(), first.LastCalibrationAtUtc, "the run stamps the clock it ran on");
        Assert.IsFalse(first.NeedsReconcile);

        // A second, later round: a fresh flag is calibrated again and the counters accumulate.
        await harness.SeedIndexedFileAsync("stale-two.cs", "StaleTwo");
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        harness.Watcher.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.PathLimitExceeded);
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var second = harness.Status();
        Assert.AreEqual(2, second.SweptFileCount);
        Assert.AreEqual(2, second.CalibrationRunCount);
        Assert.IsTrue(second.LastCalibrationAtUtc > first.LastCalibrationAtUtc, "the timestamp moves forward");
        Assert.HasCount(1, await harness.SearchSymbolsAsync("KeepClass"), "the file on disk keeps its rows");
    }

    /// <summary>
    /// A5: a scope whose change source could not be created has untrusted capture, which is exactly what
    /// <c>NeedsReconcile</c> means — so it is flagged at attach time and resolved by the calibration on the next
    /// step, instead of leaving historical rows in the index forever. This is the "watcher never attached" case
    /// named by U3-C (and the only path that exercises the driver's internally built calibration).
    /// </summary>
    [TestMethod]
    public async Task A5_A_Scope_Whose_Change_Source_Could_Not_Be_Attached_Is_Flagged_And_Calibrated()
    {
        using var harness = new MaintenanceHarness();
        var store = harness.Store;

        await harness.SeedIndexedFileAsync("legacy/gone.cs", "LegacyClass");
        Assert.HasCount(1, await harness.SearchSymbolsAsync("LegacyClass"), "positive control");

        // A second driver over the same store whose change source cannot be created at all.
        var indexer = new RecordingCodeIndexer();
        using var scheduler = new CodeIndexScheduler(
            indexer,
            new DefaultCodeWorkspaceResolver(store),
            store,
            NullLogger<CodeIndexScheduler>.Instance);
        using var service = new CodeIndexMaintenanceService(
            scheduler,
            new ThrowingWatcherFactory(),
            store,
            indexer,
            new DefaultCodeWorkspaceResolver(store),
            NullLogger<CodeIndexMaintenanceService>.Instance,
            harness.Clock,
            pollInterval: TimeSpan.FromHours(1));

        await service.StartAsync();
        Assert.IsTrue(service.EnsureScope(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, harness.Root));

        var attached = service.GetScopeStatus(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
        Assert.IsNotNull(attached);
        Assert.IsFalse(attached.WatcherAttached, "the change source could not be created");
        Assert.IsTrue(attached.NeedsReconcile, "unobserved capture must be flagged, not pretended");

        Assert.AreEqual(0, await service.ProcessDueBatchesAsync());

        var calibrated = service.GetScopeStatus(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
        Assert.IsNotNull(calibrated);
        Assert.IsFalse(calibrated.NeedsReconcile, "the calibration resolves the flag it can resolve");
        Assert.AreEqual(1, calibrated.SweptFileCount);
        Assert.IsEmpty(await harness.SearchSymbolsAsync("LegacyClass"));
    }

    /// <summary>
    /// U3-C red line 3 (grace window): a path the change pipeline observed recently is never swept, even while it
    /// is absent on disk — an atomic replace, an in-flight rename or a build rewriting its own output must not be
    /// mistaken for a historical leftover. Once the window has passed, a later run does sweep it (the exempt path
    /// is deferred, never dropped).
    /// </summary>
    [TestMethod]
    public async Task Grace_Window_Leaves_A_Recently_Observed_Path_Alone_And_Sweeps_It_Later()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        var flipPath = harness.Combine("flip.cs");
        await File.WriteAllTextAsync(flipPath, "class Flip { }");
        await harness.SeedIndexedFileAsync("flip.cs", "FlipClass");

        // Round 1: the pipeline observes the change; the batch re-indexes the file.
        Assert.IsTrue(harness.PublishChange("flip.cs", IndexChangeKind.Changed));
        harness.AdvancePastDebounce();
        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(1, harness.Status().IncrementallyIndexedFileCount);

        // The file disappears with no further event and the scope turns out to need reconciliation.
        File.Delete(flipPath);
        harness.Watcher.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError);
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        Assert.AreEqual(0, harness.Status().SweptFileCount, "a path observed seconds ago is inside the grace window");
        Assert.HasCount(1, await harness.SearchSymbolsAsync("FlipClass"), "the pipeline owns that path for now");

        // Once the window has passed, a later run sweeps it — the deferral is not a leak.
        harness.Clock.Advance(TimeSpan.FromMinutes(3)); // > the 2 minute grace window, > the retry interval
        harness.Watcher.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError);
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        Assert.AreEqual(1, harness.Status().SweptFileCount);
        Assert.IsEmpty(await harness.SearchSymbolsAsync("FlipClass"));
    }

    /// <summary>
    /// The grace-window observation map is bounded by construction, and it can be observed: only the paths inside
    /// the window are kept, so a scope that is never flagged cannot accumulate one entry per path it ever saw.
    /// </summary>
    [TestMethod]
    public async Task Grace_Window_Observation_Map_Is_Bounded_And_Ages_Out()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        Assert.IsTrue(harness.PublishChange("round-one.cs", IndexChangeKind.Created));
        harness.AdvancePastDebounce();
        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(1, harness.Status().RecentObservationCount);

        Assert.IsTrue(harness.PublishChange("round-two.cs", IndexChangeKind.Created));
        harness.AdvancePastDebounce();
        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(2, harness.Status().RecentObservationCount, "both observations are inside the window");

        // Past the window the aged-out entries are dropped (the scope is never flagged, so nothing else would
        // ever prune them).
        harness.Clock.Advance(CodeIndexCalibrationService.DefaultGraceWindow + TimeSpan.FromSeconds(1));
        Assert.IsTrue(harness.PublishChange("round-three.cs", IndexChangeKind.Created));
        harness.AdvancePastDebounce();
        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync());

        Assert.AreEqual(1, harness.Status().RecentObservationCount, "only the fresh observation is kept");
    }

    /// <summary>
    /// Bounded per run: a sweep that stops at the per-run ceiling keeps the scope flagged, and a later run finishes
    /// the job — a large backlog is swept across rounds instead of inside one unbounded series of transactions.
    /// </summary>
    [TestMethod]
    public async Task A_Per_Run_Ceiling_Keeps_The_Scope_Flagged_And_A_Later_Run_Finishes_The_Sweep()
    {
        using var harness = new MaintenanceHarness(maxRemovalsPerRun: 1);
        await harness.StartWithActiveScopeAsync();

        await harness.SeedIndexedFileAsync("gone-one.cs", "GoneOne");
        await harness.SeedIndexedFileAsync("gone-two.cs", "GoneTwo");

        harness.Watcher.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError);
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var first = harness.Status();
        Assert.AreEqual(1, first.SweptFileCount, "the run stops at the ceiling");
        Assert.IsTrue(first.NeedsReconcile, "a truncated sweep is not a finished one");
        Assert.HasCount(1, await harness.SearchSymbolsAsync("GoneTwo"), "the rest is left for the next run");

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var second = harness.Status();
        Assert.AreEqual(2, second.SweptFileCount);
        Assert.IsFalse(second.NeedsReconcile);
        Assert.IsEmpty(await harness.SearchSymbolsAsync("GoneOne"));
        Assert.IsEmpty(await harness.SearchSymbolsAsync("GoneTwo"));
    }
}
