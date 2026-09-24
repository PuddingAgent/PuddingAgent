using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// U3-D — the <b>routine</b> (periodic) calibration of every attached scope.
/// <para>
/// U3-C made calibration reachable, but only through <c>NeedsReconcile</c>: a scope was swept in the moment a
/// change source failed, and never again. A scope whose capture goes quiet for good therefore kept its stale rows
/// for ever — nothing else in the pipeline can clear a row that no change event mentions. These cases lock the
/// routine cadence that closes that gap, and lock what it must <b>not</b> change: the flagged path, the retry
/// throttle, the refusal/truncation rules, and the fact that the driver's poll cadence never becomes a disk scan.
/// </para>
/// <para>
/// No case waits for real time: the driver reads the clock through the <c>TimeProvider</c> the harness injects, so
/// a period is advanced by moving that clock.
/// </para>
/// </summary>
[TestClass]
public sealed class CodeIndexRoutineCalibrationTests
{
    private const string SecondWorkspaceId = "workspace-u3d-second";

    private const string SecondScopeId = "scope-u3d-second";

    /// <summary>
    /// A1: a scope whose change source is <b>silent</b> — no batch, no <c>NeedsReconcile</c>, no event of any kind
    /// — has its stale row swept once the routine period elapses. This is the gap U3-D closes: before it, the
    /// step did nothing about such a scope, ever.
    /// </summary>
    [TestMethod]
    public async Task A1_A_Silent_Scope_Is_Calibrated_Once_Its_Routine_Period_Elapses()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        var gonePath = harness.Combine("legacy/gone.cs");
        await harness.SeedIndexedFileAsync("legacy/gone.cs", "LegacyClass");

        // Positive control, and the scenario itself: the row is indexed, nothing is on disk, nothing is flagged.
        Assert.IsFalse(File.Exists(gonePath), "the scenario starts from a file that is not on disk");
        Assert.IsFalse(harness.Status().NeedsReconcile, "the scope is not flagged — that is the whole point");
        Assert.HasCount(1, await harness.SearchSymbolsAsync("LegacyClass"));

        // Not due yet: the period has not elapsed, so no step may calibrate (the other half of A2).
        harness.Clock.Advance(CodeIndexMaintenanceService.DefaultCalibrationPeriod - TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var beforeDue = harness.Status();
        Assert.AreEqual(0, beforeDue.CalibrationRunCount, "未到期：一次校准都不许发起");
        Assert.IsNull(beforeDue.LastCalibrationAtUtc, "no calibration has ever run");
        Assert.HasCount(1, await harness.SearchSymbolsAsync("LegacyClass"), "the stale row is still there (control)");

        // Due: exactly one calibration, and the stale row is really gone.
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var afterDue = harness.Status();
        Assert.AreEqual(1, afterDue.CalibrationRunCount, "到期必校准，且只校准一次");
        Assert.AreEqual(1, afterDue.SweptFileCount);
        Assert.AreEqual(0, afterDue.RejectedCalibrationRunCount);
        Assert.IsNotNull(afterDue.LastCalibrationAtUtc, "the run stamps the clock it ran on");
        Assert.IsFalse(afterDue.NeedsReconcile, "a routine sweep is not a reconcile and must not flag the scope");

        Assert.IsEmpty(
            await harness.SearchSymbolsAsync("LegacyClass"),
            "a file that no longer exists must not be returned by the index any more");
        Assert.IsEmpty(
            await harness.Store.GetSymbolsByFileAsync(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, gonePath),
            "the vanished file must not keep symbol rows");
        Assert.IsFalse(
            await IsFileIndexedAsync(harness, MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, gonePath),
            "the vanished file must not keep a file record");

        // One expiry, one sweep: the next step must not calibrate again (the anchor moved to the completion).
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(1, harness.Status().CalibrationRunCount, "一次到期只校准一次");
    }

    /// <summary>
    /// A2: the throttle. Steps that run before the period expires calibrate <b>nothing</b> — the driver's poll
    /// cadence (200 ms in production) must never turn into a per-step disk scan.
    /// </summary>
    [TestMethod]
    public async Task A2_No_Step_Before_The_Period_Expires_Calibrates()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        await harness.SeedIndexedFileAsync("legacy/gone.cs", "LegacyClass");
        var attachInstant = harness.Clock.GetUtcNow();

        // 50 steps, ten seconds apart: 500 s of driving, still inside the 15 minute period.
        for (var step = 0; step < 50; step++)
        {
            harness.Clock.Advance(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        }

        Assert.IsTrue(
            harness.Clock.GetUtcNow() - attachInstant < CodeIndexMaintenanceService.DefaultCalibrationPeriod,
            "the loop above must stay inside the period it is testing");

        var status = harness.Status();
        Assert.AreEqual(0, status.CalibrationRunCount, "节流生效：未到期不得校准");
        Assert.AreEqual(0, status.SweptFileCount, "nothing was swept either");
        Assert.IsNull(status.LastCalibrationAtUtc, "a calibration that never ran leaves no stamp");
        Assert.HasCount(1, await harness.SearchSymbolsAsync("LegacyClass"), "the stale row is untouched");
    }

    /// <summary>
    /// A3: the routine clock is <b>per scope</b>. One scope being calibrated (here through the reconcile path)
    /// must not push another scope's clock forward, and one scope coming due must not calibrate the other.
    /// </summary>
    [TestMethod]
    public async Task A3_Each_Scope_Keeps_Its_Own_Routine_Clock()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        // A second scope on the same driver, with its own root and its own stale row.
        var secondRoot = Path.Combine(harness.Root, "second-scope");
        Directory.CreateDirectory(secondRoot);
        await harness.Store.UpsertProjectAsync(
            new CodeProjectRecord(SecondWorkspaceId, SecondScopeId, secondRoot, CodeProjectStatus.Active));
        Assert.IsTrue(harness.Service.EnsureScope(SecondWorkspaceId, SecondScopeId, secondRoot));

        var firstGone = harness.Combine("first-gone.cs");
        var secondGone = Path.Combine(secondRoot, "second-gone.cs");
        await harness.SeedIndexedFileAsync("first-gone.cs", "FirstClass");
        await SeedFileRowAsync(harness, SecondWorkspaceId, SecondScopeId, secondGone);

        // t0 + 14 min: neither scope is due (both were attached at t0), so nothing is calibrated.
        harness.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(0, harness.Status().CalibrationRunCount);
        Assert.AreEqual(0, ScopeStatus(harness, SecondWorkspaceId, SecondScopeId).CalibrationRunCount);

        // Scope 1 is flagged and calibrated now — its own clock restarts at this instant. The watcher is looked
        // up by scope id on purpose: the single-scope helper of the harness insists on exactly one attached scope,
        // and this case drives two.
        var firstWatcher = harness.WatcherFactory.Watchers.Single(
            watcher => watcher.ScopeId == MaintenanceTestData.ScopeId);
        Assert.AreEqual(2, harness.WatcherFactory.Watchers.Count, "the driver drives both scopes");
        firstWatcher.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError);
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        Assert.AreEqual(1, harness.Status().CalibrationRunCount, "scope 1 was calibrated through the flagged path");
        Assert.AreEqual(1, harness.Status().SweptFileCount);
        Assert.IsFalse(await IsFileIndexedAsync(harness, MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, firstGone));

        // t0 + 15 min: scope 2 has now had its own full period since it was attached, so it is due; scope 1 has
        // had one minute since its own run, so it is not. Nothing here is shared between the two.
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        Assert.AreEqual(1, harness.Status().CalibrationRunCount, "scope 1 must not be calibrated again");
        Assert.AreEqual(1, harness.Status().SweptFileCount);

        var second = ScopeStatus(harness, SecondWorkspaceId, SecondScopeId);
        Assert.AreEqual(1, second.CalibrationRunCount, "scope 2 came due on its own clock");
        Assert.AreEqual(1, second.SweptFileCount);
        Assert.IsFalse(second.NeedsReconcile, "the routine sweep did not flag scope 2");
        Assert.IsFalse(
            await IsFileIndexedAsync(harness, SecondWorkspaceId, SecondScopeId, secondGone),
            "scope 2's stale row must be gone");
    }

    /// <summary>
    /// A4 (refused): a sweep that could not trust "this path is gone" must not look like "nothing was stale",
    /// and it must not be retried on the driver's cadence either — one attempt per throttle window.
    /// <para>
    /// The refusal happens on the <b>routine</b> path here (nothing flagged the scope), which is new in U3-D: the
    /// answer to "could not tell" is to set the flag, and the existing 60 s bound is what stops the probing from
    /// following the poll cadence.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task A4_A_Refused_Sweep_Keeps_The_Flag_And_Does_Not_Retry_Inside_The_Window()
    {
        var logger = new RecordingLogger<CodeIndexMaintenanceService>();
        using var harness = new MaintenanceHarness(logger: logger);

        // A root that is not there: the drive is not mounted / the share is gone / the directory was renamed away.
        var offlineRoot = Path.Combine(harness.Root, "offline-scope");
        await harness.StartWithActiveScopeAsync(offlineRoot);

        var indexedPath = Path.Combine(offlineRoot, "a.cs");
        await harness.SeedIndexedAbsoluteFileAsync(indexedPath, "AClass");
        Assert.HasCount(1, await harness.SearchSymbolsAsync("AClass"), "positive control: the row is there");

        harness.Clock.Advance(CodeIndexMaintenanceService.DefaultCalibrationPeriod);
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var refused = harness.Status();
        Assert.AreEqual(1, refused.CalibrationRunCount, "the routine attempt is counted");
        Assert.AreEqual(1, refused.RejectedCalibrationRunCount);
        Assert.AreEqual(0, refused.SweptFileCount, "a refused sweep removes nothing");
        Assert.IsTrue(refused.NeedsReconcile, "a refusal must not be mistaken for 'nothing was stale'");
        Assert.AreEqual(
            CodeIndexScopeState.ReconcileReasons.CalibrationRootUnavailable,
            refused.ReconcileReason);
        Assert.AreEqual(1, harness.Service.PendingReconcileScopeCount);
        Assert.HasCount(1, await harness.SearchSymbolsAsync("AClass"), "the index was not emptied");
        Assert.HasCount(
            1,
            await harness.Store.ListFilesAsync(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId));

        // Inside the window: however many steps run, the scope is not re-probed.
        for (var step = 0; step < 20; step++)
        {
            harness.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        }

        Assert.AreEqual(1, harness.Status().CalibrationRunCount, "窗口内不得重试（失败不风暴）");
        Assert.IsTrue(harness.Status().NeedsReconcile, "the flag is kept while the root stays unusable");

        // Past the window it retries: the 60 s lower bound of U3-C is kept, not abandoned.
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        Assert.AreEqual(2, harness.Status().CalibrationRunCount, "60 s 后仍按既有语义重试");
        Assert.AreEqual(0, harness.Status().SweptFileCount);
        Assert.HasCount(1, await harness.SearchSymbolsAsync("AClass"), "still nothing removed");

        Assert.IsTrue(logger.Contains(LogLevel.Error, "Refusing to sweep"), "the refusal is an error, not silence");
        Assert.IsTrue(logger.Contains(LogLevel.Error, "calibration was refused"), "the driver records its consequence");
    }

    /// <summary>
    /// A4 (truncated): a routine run that stops at the per-run ceiling keeps the scope flagged and hands the rest
    /// to a later run — and that later run waits for the window, so a large backlog converges at the throttle's
    /// pace instead of riding the poll cadence.
    /// </summary>
    [TestMethod]
    public async Task A4_A_Truncated_Sweep_Keeps_The_Flag_And_Resumes_Only_After_The_Window()
    {
        using var harness = new MaintenanceHarness(maxRemovalsPerRun: 1);
        await harness.StartWithActiveScopeAsync();

        await harness.SeedIndexedFileAsync("gone-one.cs", "GoneOne");
        await harness.SeedIndexedFileAsync("gone-two.cs", "GoneTwo");

        // Nothing is flagged: the period is what starts this run.
        harness.Clock.Advance(CodeIndexMaintenanceService.DefaultCalibrationPeriod);
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var first = harness.Status();
        Assert.AreEqual(1, first.SweptFileCount, "the run stops at the ceiling");
        Assert.IsTrue(first.NeedsReconcile, "a truncated sweep is not a finished one");
        Assert.HasCount(1, await harness.SearchSymbolsAsync("GoneTwo"), "the rest is left for the next run");

        for (var step = 0; step < 30; step++)
        {
            harness.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        }

        Assert.AreEqual(1, harness.Status().CalibrationRunCount, "窗口内不得重试");
        Assert.AreEqual(1, harness.Status().SweptFileCount);

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var second = harness.Status();
        Assert.AreEqual(2, second.CalibrationRunCount);
        Assert.AreEqual(2, second.SweptFileCount, "the later run finishes the job");
        Assert.IsFalse(second.NeedsReconcile);
        Assert.IsEmpty(await harness.SearchSymbolsAsync("GoneOne"));
        Assert.IsEmpty(await harness.SearchSymbolsAsync("GoneTwo"));
    }

    /// <summary>
    /// A5: the U3-C semantics are untouched. A flagged scope is calibrated on the very next step whatever the
    /// routine clock says, a successful sweep clears the flag, and the routine clock is then measured from that
    /// run's completion — so nothing is swept again until a full period has passed.
    /// </summary>
    [TestMethod]
    public async Task A5_The_Flagged_Path_And_The_Throttle_Are_Unchanged()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        await harness.SeedIndexedFileAsync("legacy/gone.cs", "LegacyClass");

        // Flagged ⇒ calibrated on the next step, long before any period has elapsed.
        harness.Watcher.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError);
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var afterReconcile = harness.Status();
        Assert.AreEqual(1, afterReconcile.CalibrationRunCount);
        Assert.AreEqual(1, afterReconcile.SweptFileCount);
        Assert.IsFalse(afterReconcile.NeedsReconcile, "a successful calibration clears the flag");
        Assert.AreEqual(0, harness.Service.PendingReconcileScopeCount);

        // The routine clock now runs from that completion: 14 more minutes sweep nothing.
        for (var minute = 0; minute < 14; minute++)
        {
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
            await harness.SeedIndexedFileAsync($"later-{minute:D2}.cs", $"Later{minute:D2}");
            Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        }

        Assert.AreEqual(1, harness.Status().CalibrationRunCount, "还不该到下一个周期");
        Assert.AreEqual(1, harness.Status().SweptFileCount);

        // ...and the 15th minute does sweep the rows that piled up in the meantime.
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var afterPeriod = harness.Status();
        Assert.AreEqual(2, afterPeriod.CalibrationRunCount);
        Assert.AreEqual(15, afterPeriod.SweptFileCount, "the 14 rows planted meanwhile are swept in one routine run");
    }

    /// <summary>
    /// A6: the suite only grows. The U3-D baseline is 98 cases in this assembly; the routine slice adds cases and
    /// removes none, which is asserted from the assembly itself so a deletion cannot slip through unnoticed.
    /// </summary>
    [TestMethod]
    public void A6_The_Assembly_Keeps_At_Least_The_Baseline_Case_Count()
    {
        var cases = typeof(CodeIndexRoutineCalibrationTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly))
            .Count(method => method.GetCustomAttribute<TestMethodAttribute>() is not null);

        Assert.IsTrue(
            cases >= 98,
            $"U3-D 基线：本组件测试工程开工前是 98 个用例，本刀只增不减（当前反射计数 {cases}，运行器总数是权威数字）");
    }

    /// <summary>
    /// The cost of the routine cadence, measured rather than asserted by hand: a scope of ≈350 indexed files (348
    /// of them really on disk) is swept once by the period, and a step that is <b>not</b> due touches nothing.
    /// The numbers are appended to a file under the temp directory so the report can quote them.
    /// </summary>
    [TestMethod]
    public async Task Cost_One_Routine_Sweep_Of_A_Three_Hundred_And_Fifty_File_Scope_Is_Measured()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        const int present = 348;
        var sourceDirectory = harness.Combine("src");
        Directory.CreateDirectory(sourceDirectory);
        for (var index = 0; index < present; index++)
        {
            var relative = $"src/file-{index:D3}.cs";
            await File.WriteAllTextAsync(harness.Combine(relative), "class C { }");
            await harness.SeedIndexedFileAsync(relative, $"C{index:D3}");
        }

        await harness.SeedIndexedFileAsync("src/gone-a.cs", "GoneA");
        await harness.SeedIndexedFileAsync("src/gone-b.cs", "GoneB");

        // A step that is not due: no calibration, so no listing of the scope's files at all.
        var notDue = Stopwatch.StartNew();
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        notDue.Stop();
        Assert.AreEqual(0, harness.Status().CalibrationRunCount, "未到期的一步不做任何扫盘");

        harness.Clock.Advance(CodeIndexMaintenanceService.DefaultCalibrationPeriod);

        var sweep = Stopwatch.StartNew();
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        sweep.Stop();

        var status = harness.Status();
        Assert.AreEqual(2, status.SweptFileCount, "only the two files that are really gone are swept");
        Assert.IsTrue(
            sweep.ElapsedMilliseconds < 15000,
            $"one routine sweep of {present + 2} indexed paths must stay well inside a period (measured {sweep.ElapsedMilliseconds} ms)");

        var probePath = Path.Combine(Path.GetTempPath(), "pudding-u3d-cost-probe.txt");
        await File.AppendAllTextAsync(
            probePath,
            $"indexed={present + 2} present={present} gone=2 swept={status.SweptFileCount} " +
            $"sweepMs={sweep.ElapsedMilliseconds} notDueStepMs={notDue.Elapsed.TotalMilliseconds:F2}{Environment.NewLine}");
    }

    /// <summary>
    /// A5 (U3-C red line 3, on the new path): the routine sweep leaves alone a path the change pipeline observed
    /// moments ago — an atomic replace or an in-flight rename must not be mistaken for a historical leftover —
    /// and it removes index rows only: nothing on the file system is created or deleted, and the rows of files
    /// that are still there are not touched.
    /// </summary>
    [TestMethod]
    public async Task A5_A_Routine_Sweep_Respects_The_Grace_Window_And_Removes_Only_Index_Rows()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        var flipPath = harness.Combine("flip.cs");
        var keptPath = harness.Combine("keep.cs");
        var stalePath = harness.Combine("legacy/stale.cs");
        await File.WriteAllTextAsync(flipPath, "class Flip { }");
        await File.WriteAllTextAsync(keptPath, "class Keep { }");
        await harness.SeedIndexedFileAsync("flip.cs", "FlipClass");
        await harness.SeedIndexedFileAsync("keep.cs", "KeepClass");
        await harness.SeedIndexedFileAsync("legacy/stale.cs", "StaleClass");

        // The pipeline observes flip.cs one minute before the period elapses, and its batch is applied...
        harness.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.IsTrue(harness.PublishChange("flip.cs", IndexChangeKind.Changed));
        harness.AdvancePastDebounce();
        Assert.AreEqual(1, await harness.Service.ProcessDueBatchesAsync(), "the batch is applied; nothing is due yet");

        // ...then that file disappears without any further event.
        File.Delete(flipPath);

        // The period elapses: the routine sweep runs, and flip.cs is inside the window the pipeline owns.
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var status = harness.Status();
        Assert.AreEqual(1, status.CalibrationRunCount, "the period is what starts this run");
        Assert.AreEqual(1, status.SweptFileCount, "only the historical leftover is swept");
        Assert.HasCount(1, await harness.SearchSymbolsAsync("FlipClass"), "a path the pipeline just touched is left alone");
        Assert.IsEmpty(await harness.SearchSymbolsAsync("StaleClass"));
        Assert.HasCount(1, await harness.SearchSymbolsAsync("KeepClass"), "a file that is still on disk keeps its rows");

        // Rows only: the sweep neither creates nor deletes anything on disk.
        Assert.IsFalse(File.Exists(flipPath), "the sweep must not create the missing file");
        Assert.IsFalse(File.Exists(stalePath), "...including the one it swept");
        Assert.IsFalse(Directory.Exists(harness.Combine("legacy")), "the sweep must not create the directory either");
        Assert.IsTrue(File.Exists(keptPath), "a file that is still on disk must stay there");

        // Once the window has passed, a later routine run does sweep it — the deferral is not a leak.
        harness.Clock.Advance(CodeIndexMaintenanceService.DefaultCalibrationPeriod);
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.IsEmpty(await harness.SearchSymbolsAsync("FlipClass"));
    }

    /// <summary>Status of a scope other than the harness's own.</summary>
    private static CodeIndexMaintenanceScopeStatus ScopeStatus(
        MaintenanceHarness harness,
        string workspaceId,
        string scopeId) =>
        harness.Service.GetScopeStatus(workspaceId, scopeId)
        ?? throw new InvalidOperationException($"scope {scopeId} is not attached");

    /// <summary>True while the store holds a file record for <paramref name="filePath"/> in that scope.</summary>
    private static async Task<bool> IsFileIndexedAsync(
        MaintenanceHarness harness,
        string workspaceId,
        string scopeId,
        string filePath)
    {
        var files = await harness.Store.ListFilesAsync(workspaceId, scopeId);
        return files.Any(file => string.Equals(file.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Seeds one indexed file row for an arbitrary scope (the harness seeds only its own scope).</summary>
    private static Task SeedFileRowAsync(
        MaintenanceHarness harness,
        string workspaceId,
        string scopeId,
        string filePath) =>
        harness.Store.UpsertFilesAsync(
            workspaceId,
            scopeId,
            [new CodeFileRecord(workspaceId, scopeId, filePath, "C#", harness.Clock.GetUtcNow())]);
}
