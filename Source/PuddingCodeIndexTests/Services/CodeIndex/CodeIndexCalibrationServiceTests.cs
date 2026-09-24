using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// U3-C — the calibration itself, driven directly (no maintenance step in the way): the refusal guards, the grace
/// window, idempotency, the per-run bounds, cancellation, and the fact that it only ever asks the store to remove
/// index rows.
/// </summary>
[TestClass]
public sealed class CodeIndexCalibrationServiceTests
{
    private const string WorkspaceId = MaintenanceTestData.WorkspaceId;
    private const string ScopeId = MaintenanceTestData.ScopeId;

    /// <summary>
    /// A6: running the calibration twice removes nothing the second time — the sweep is idempotent, and the second
    /// run does not even ask the store to remove anything.
    /// </summary>
    [TestMethod]
    public async Task A6_A_Second_Calibration_Removes_Nothing()
    {
        using var harness = new MaintenanceHarness();
        var store = new RecordingCodeIndexStore(harness.Store);
        var calibrator = new CodeIndexCalibrationService(store, harness.Clock);

        await harness.SeedIndexedFileAsync("stale.cs", "StaleClass");
        Assert.HasCount(1, await harness.SearchSymbolsAsync("StaleClass"), "positive control");

        var request = new CodeIndexCalibrationRequest(WorkspaceId, ScopeId, harness.Root);

        var first = await calibrator.CalibrateAsync(request);
        Assert.IsTrue(first.RootUsable);
        Assert.AreEqual(1, first.ScannedFileCount);
        Assert.AreEqual(1, first.AbsentFileCount);
        Assert.AreEqual(1, first.SweptFileCount);
        Assert.IsFalse(first.Truncated);

        var callsAfterFirstRun = store.Calls.Count;

        var second = await calibrator.CalibrateAsync(request);
        Assert.IsTrue(second.RootUsable);
        Assert.AreEqual(0, second.ScannedFileCount, "the scope has no indexed file left to scan");
        Assert.AreEqual(0, second.SweptFileCount, "a second run removes nothing");
        Assert.IsFalse(second.Truncated);
        Assert.HasCount(1, store.Calls.Skip(callsAfterFirstRun), "the second run lists the scope and stops there");
        Assert.AreEqual(nameof(ICodeIndexStore.ListFilesAsync), store.Calls[^1]);

        Assert.IsEmpty(await harness.SearchSymbolsAsync("StaleClass"));
    }

    /// <summary>
    /// The refusal guard: a root that is not there (unmounted drive, gone network share) or no root at all means
    /// the "not on disk" verdict is meaningless, so the run is refused before the store is even listed.
    /// </summary>
    [TestMethod]
    public async Task A_Missing_Root_Refuses_The_Sweep_Before_Anything_Is_Read()
    {
        using var harness = new MaintenanceHarness();
        var store = new RecordingCodeIndexStore(harness.Store);
        var logger = new RecordingLogger<CodeIndexCalibrationService>();
        var calibrator = new CodeIndexCalibrationService(store, harness.Clock, logger: logger);

        var missingRoot = Path.Combine(harness.Root, "not-mounted");
        await harness.SeedIndexedAbsoluteFileAsync(Path.Combine(missingRoot, "a.cs"), "AClass");

        var result = await calibrator.CalibrateAsync(new CodeIndexCalibrationRequest(WorkspaceId, ScopeId, missingRoot));

        Assert.IsFalse(result.RootUsable);
        Assert.AreEqual(CodeIndexCalibrationRejections.RootMissing, result.RejectionReason);
        Assert.AreEqual(0, result.ScannedFileCount, "a refused run does not even list the scope");
        Assert.AreEqual(0, result.SweptFileCount);
        Assert.IsEmpty(store.Calls, "the store must not be touched at all");
        Assert.IsTrue(logger.Contains(LogLevel.Error, "Refusing to sweep"));
        Assert.HasCount(1, await harness.SearchSymbolsAsync("AClass"), "every row survives a refusal");

        var noRoot = await calibrator.CalibrateAsync(new CodeIndexCalibrationRequest(WorkspaceId, ScopeId, "   "));
        Assert.IsFalse(noRoot.RootUsable);
        Assert.AreEqual(CodeIndexCalibrationRejections.RootPathMissing, noRoot.RejectionReason);
        Assert.IsEmpty(store.Calls);
    }

    /// <summary>
    /// The grace window itself, at the level of one request: a path observed inside the window is exempt, and once
    /// the window has passed the same request sweeps it. Exemption is a deferral, never a leak.
    /// </summary>
    [TestMethod]
    public async Task A_Recently_Observed_Path_Is_Exempt_Inside_The_Grace_Window_And_Swept_Afterwards()
    {
        using var harness = new MaintenanceHarness();
        var store = new RecordingCodeIndexStore(harness.Store);
        var calibrator = new CodeIndexCalibrationService(store, harness.Clock);

        await harness.SeedIndexedFileAsync("flip.cs", "FlipClass");
        var flipPath = harness.Combine("flip.cs");

        var observations = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
        {
            [flipPath] = harness.Clock.GetUtcNow(),
        };
        var request = new CodeIndexCalibrationRequest(WorkspaceId, ScopeId, harness.Root, observations);

        var protectedRun = await calibrator.CalibrateAsync(request);
        Assert.AreEqual(1, protectedRun.AbsentFileCount, "the path really is not on disk");
        Assert.AreEqual(1, protectedRun.ProtectedFileCount);
        Assert.AreEqual(0, protectedRun.SweptFileCount, "the pipeline owns that path for now");
        Assert.IsEmpty(store.RemovalBatchSizes, "a protected path is not even handed to the store");
        Assert.HasCount(1, await harness.SearchSymbolsAsync("FlipClass"));

        harness.Clock.Advance(CodeIndexCalibrationService.DefaultGraceWindow + TimeSpan.FromSeconds(1));

        var sweptRun = await calibrator.CalibrateAsync(request);
        Assert.AreEqual(0, sweptRun.ProtectedFileCount, "the observation has aged out");
        Assert.AreEqual(1, sweptRun.SweptFileCount);
        Assert.IsEmpty(await harness.SearchSymbolsAsync("FlipClass"));
    }

    /// <summary>
    /// The per-run bounds: removals are handed to the store in transactions of at most
    /// <c>sweepBatchSize</c> paths, and at most <c>maxRemovalsPerRun</c> paths in one run (the run then reports
    /// itself truncated so the caller can keep the scope flagged).
    /// </summary>
    [TestMethod]
    public async Task Removals_Are_Batched_By_Transaction_And_Bounded_Per_Run()
    {
        using var harness = new MaintenanceHarness();
        var store = new RecordingCodeIndexStore(harness.Store);
        var calibrator = new CodeIndexCalibrationService(store, harness.Clock, sweepBatchSize: 2, maxRemovalsPerRun: 4);

        for (var i = 1; i <= 5; i++)
            await harness.SeedIndexedFileAsync($"gone-{i}.cs", $"Gone{i}");

        var first = await calibrator.CalibrateAsync(new CodeIndexCalibrationRequest(WorkspaceId, ScopeId, harness.Root));

        Assert.IsTrue(first.Truncated, "the per-run ceiling is reached");
        Assert.AreEqual(4, first.SweptFileCount);
        Assert.AreEqual(5, first.ScannedFileCount);
        Assert.AreEqual(5, first.AbsentFileCount);
        Assert.HasCount(2, store.RemovalBatchSizes);
        Assert.AreEqual(2, store.RemovalBatchSizes[0], "one transaction never carries more than sweepBatchSize paths");
        Assert.AreEqual(2, store.RemovalBatchSizes[1]);
        Assert.HasCount(1, await harness.SearchSymbolsAsync("Gone5"), "the path beyond the ceiling is left for the next run");

        var second = await calibrator.CalibrateAsync(new CodeIndexCalibrationRequest(WorkspaceId, ScopeId, harness.Root));

        Assert.IsFalse(second.Truncated);
        Assert.AreEqual(1, second.ScannedFileCount);
        Assert.AreEqual(1, second.SweptFileCount);
        Assert.IsEmpty(await harness.SearchSymbolsAsync("Gone5"));
    }

    /// <summary>
    /// Red line 2, machine-checked: the sweep asks the store for exactly two things — the scope's indexed file list
    /// and the removal of index rows. It cannot delete a file or a directory even in principle.
    /// </summary>
    [TestMethod]
    public async Task A_Sweep_Asks_The_Store_For_The_Listing_And_Index_Removals_And_Nothing_Else()
    {
        using var harness = new MaintenanceHarness();
        var store = new RecordingCodeIndexStore(harness.Store);
        var calibrator = new CodeIndexCalibrationService(store, harness.Clock);

        await harness.SeedIndexedFileAsync("stale.cs", "StaleClass");

        await calibrator.CalibrateAsync(new CodeIndexCalibrationRequest(WorkspaceId, ScopeId, harness.Root));

        var distinctCalls = store.Calls.Distinct().ToArray();
        Assert.HasCount(2, distinctCalls, $"the sweep called: {string.Join(", ", store.Calls)}");
        Assert.AreEqual(nameof(ICodeIndexStore.ListFilesAsync), distinctCalls[0]);
        Assert.AreEqual(nameof(ICodeIndexStore.RemoveFilesAsync), distinctCalls[1]);
    }

    /// <summary>
    /// Cancellation stops the run between batches: the batches already committed stay committed (each one is its own
    /// transaction and no file is ever half-removed), and the caller keeps the decision about the scope's flag.
    /// </summary>
    [TestMethod]
    public async Task A_Cancelled_Run_Stops_Between_Batches_And_Keeps_The_Committed_Ones()
    {
        using var harness = new MaintenanceHarness();
        var store = new RecordingCodeIndexStore(harness.Store);
        var calibrator = new CodeIndexCalibrationService(store, harness.Clock, sweepBatchSize: 2);
        using var cts = new CancellationTokenSource();

        for (var i = 1; i <= 5; i++)
            await harness.SeedIndexedFileAsync($"gone-{i}.cs", $"Gone{i}");

        store.OnFirstRemoval = () => cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            calibrator.CalibrateAsync(
                new CodeIndexCalibrationRequest(WorkspaceId, ScopeId, harness.Root),
                cts.Token));

        Assert.HasCount(1, store.RemovalBatchSizes, "the run stopped after the first batch");
        Assert.AreEqual(2, store.RemovalBatchSizes[0]);
        Assert.IsEmpty(await harness.SearchSymbolsAsync("Gone1"), "the committed batch stays committed");
        Assert.IsEmpty(await harness.SearchSymbolsAsync("Gone2"));
        Assert.HasCount(1, await harness.SearchSymbolsAsync("Gone3"), "the rest was not touched");
        Assert.HasCount(3, await harness.Store.ListFilesAsync(WorkspaceId, ScopeId));
    }

    /// <summary>A scope with nothing indexed is a no-op, not an error.</summary>
    [TestMethod]
    public async Task An_Empty_Scope_Is_A_No_Op()
    {
        using var harness = new MaintenanceHarness();
        var store = new RecordingCodeIndexStore(harness.Store);
        var calibrator = new CodeIndexCalibrationService(store, harness.Clock);

        var result = await calibrator.CalibrateAsync(new CodeIndexCalibrationRequest(WorkspaceId, ScopeId, harness.Root));

        Assert.IsTrue(result.RootUsable);
        Assert.AreEqual(0, result.ScannedFileCount);
        Assert.AreEqual(0, result.SweptFileCount);
        Assert.IsFalse(result.Truncated);
        Assert.IsEmpty(store.RemovalBatchSizes);
    }
}
