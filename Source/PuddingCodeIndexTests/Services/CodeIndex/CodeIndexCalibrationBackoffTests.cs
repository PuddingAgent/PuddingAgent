using System.Reflection;
using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// U3-E — the calibration <b>retry backoff</b>.
/// <para>
/// ADR-089 asks for "监听不可用时 60s 轮询 <b>+ 退避</b>"; only the first half was ever built, so a scope whose
/// root disappeared was re-probed — and re-logged — once a minute for ever (1440 probes/day per broken scope), and
/// U3-D made that reachable for any scope whose root goes away, not just a flagged one. These cases lock the
/// ladder that closes the gap (60 s, 2, 4, 8, 16 min, capped at 30 min), lock the reset that keeps a recovered
/// root from being punished for the outage before it, and lock what must <b>not</b> change: the routine 15 minute
/// clock, and the "first attempt on the very next step" rule of a freshly flagged scope.
/// </para>
/// <para>
/// No case waits for real time: the driver reads the clock through the harness' <c>TimeProvider</c>, so a rung of
/// the ladder is climbed by moving that clock — the driver's poll cadence is never involved.
/// </para>
/// </summary>
[TestClass]
public sealed class CodeIndexCalibrationBackoffTests
{
    /// <summary>The documented ladder, as gaps between two consecutive attempts.</summary>
    private static readonly TimeSpan[] Ladder =
    [
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4),
        TimeSpan.FromMinutes(8),
        TimeSpan.FromMinutes(16)
    ];

    /// <summary>
    /// A1: repeated refusals widen the gap <b>exponentially</b>. After the k-th consecutive refusal the next
    /// attempt may not happen before <c>60s × 2^(k-1)</c>, and it does happen then — asserted from both sides,
    /// because "not yet" alone would also pass for a ladder that never retries at all.
    /// </summary>
    [TestMethod]
    public async Task A1_Repeated_Failures_Widen_The_Retry_Gap_Exponentially()
    {
        var logger = new RecordingLogger<CodeIndexMaintenanceService>();
        using var harness = new MaintenanceHarness(logger: logger);
        var offlineRoot = OfflineRoot(harness);
        await StartWithOfflineRootAsync(harness, offlineRoot);

        // U3-C, unchanged: a flagged scope is calibrated on the very next step, whatever the ladder says.
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(1, harness.Status().CalibrationRunCount, "flagging a scope means 'calibrate it now'");

        for (var rung = 0; rung < Ladder.Length; rung++)
        {
            var attempts = rung + 1;
            var gap = Ladder[rung];

            // One second short of the rung: not a single attempt — the ladder is a lower bound, not a hint, and
            // the driver's cadence must not erode it.
            harness.Clock.Advance(gap - TimeSpan.FromSeconds(1));
            Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
            Assert.AreEqual(
                attempts,
                harness.Status().CalibrationRunCount,
                $"attempt {attempts + 1} must not happen before {gap} have elapsed since attempt {attempts}");

            // On the rung: exactly the next attempt, and it is refused again.
            harness.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

            var status = harness.Status();
            Assert.AreEqual(
                attempts + 1,
                status.CalibrationRunCount,
                $"attempt {attempts + 1} must happen once {gap} have elapsed since attempt {attempts}");
            Assert.AreEqual(attempts + 1, status.RejectedCalibrationRunCount, "every attempt was refused");
            Assert.IsTrue(status.NeedsReconcile, "a refused scope stays flagged");
        }

        // Six attempts, five widening gaps: 60 s + 2 + 4 + 8 + 16 minutes of clock, not 5 × 60 s.
        Assert.AreEqual(6, harness.Status().CalibrationRunCount);

        // R4: the ladder is visible from the log — including when the next attempt is allowed.
        Assert.IsTrue(
            logger.Contains(LogLevel.Error, "next attempt no earlier than"),
            "a refusal must say when the next attempt is allowed, or nobody can tell a backoff from a stall");
        Assert.IsTrue(
            logger.Contains(LogLevel.Error, "Consecutive unusable-root outcome 5"),
            "the rung the scope landed on must be in the log");

        // Every refusal is a refusal: nothing was removed, and the index still holds its row.
        Assert.HasCount(1, await harness.SearchSymbolsAsync("AClass"));
    }

    /// <summary>
    /// A2: the ladder is <b>capped</b>. The rung after 16 minutes would be 32; the cap is 30, and the interval
    /// stays there for ever — a root that comes back is therefore always noticed within the cap, and a root that
    /// never comes back is never probed more slowly than that.
    /// </summary>
    [TestMethod]
    public async Task A2_The_Ladder_Is_Capped_And_Stops_Growing()
    {
        using var harness = new MaintenanceHarness();
        var offlineRoot = OfflineRoot(harness);
        await StartWithOfflineRootAsync(harness, offlineRoot);

        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(1, harness.Status().CalibrationRunCount);

        // Climb the whole ladder: five gaps (60 s, 2, 4, 8, 16 min) end on the sixth attempt.
        foreach (var gap in Ladder)
        {
            harness.Clock.Advance(gap);
            Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        }

        Assert.AreEqual(6, harness.Status().CalibrationRunCount, "five gaps climbed, six attempts made");

        // From here the cap rules: three more rungs, each exactly the cap apart. A ladder that kept doubling
        // would need 32, 64 and 128 minutes for these three attempts and would fail the very next assertion.
        for (var rung = 0; rung < 3; rung++)
        {
            var attempts = 6 + rung;

            harness.Clock.Advance(CodeIndexMaintenanceService.DefaultCalibrationBackoffMax - TimeSpan.FromMinutes(1));
            Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
            Assert.AreEqual(
                attempts,
                harness.Status().CalibrationRunCount,
                $"one minute short of the cap: no attempt beyond the {attempts} already made");

            harness.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
            Assert.AreEqual(
                attempts + 1,
                harness.Status().CalibrationRunCount,
                "on the minute, exactly one attempt: that is the cap, not a 32 minute rung");
        }
    }

    /// <summary>
    /// A2 (pure): the ladder itself, as values. Locks the sequence and the cap without any clock, so the numbers
    /// the report claims are asserted where they are defined.
    /// </summary>
    [TestMethod]
    public void A2_The_Ladder_Values_Are_The_Documented_Sequence()
    {
        Assert.AreEqual(
            CodeIndexMaintenanceService.DefaultCalibrationInterval,
            CodeIndexMaintenanceService.CalibrationBackoffInterval(0),
            "a scope that never failed waits the U3-C bound");
        Assert.AreEqual(
            CodeIndexMaintenanceService.DefaultCalibrationInterval,
            CodeIndexMaintenanceService.CalibrationBackoffInterval(1),
            "one failure must not widen anything — backing off from the first failure would be a behaviour change");

        var rungs = new[]
        {
            CodeIndexMaintenanceService.CalibrationBackoffInterval(2),
            CodeIndexMaintenanceService.CalibrationBackoffInterval(3),
            CodeIndexMaintenanceService.CalibrationBackoffInterval(4),
            CodeIndexMaintenanceService.CalibrationBackoffInterval(5),
            CodeIndexMaintenanceService.CalibrationBackoffInterval(6),
            CodeIndexMaintenanceService.CalibrationBackoffInterval(7),
            CodeIndexMaintenanceService.CalibrationBackoffInterval(64),
            CodeIndexMaintenanceService.CalibrationBackoffInterval(long.MaxValue)
        };

        CollectionAssert.AreEqual(
            new[]
            {
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(4),
                TimeSpan.FromMinutes(8),
                TimeSpan.FromMinutes(16),
                CodeIndexMaintenanceService.DefaultCalibrationBackoffMax,
                CodeIndexMaintenanceService.DefaultCalibrationBackoffMax,
                CodeIndexMaintenanceService.DefaultCalibrationBackoffMax,
                CodeIndexMaintenanceService.DefaultCalibrationBackoffMax
            },
            rungs,
            "60 s doubled per failure (starting at the second), capped at the component constant");

        Assert.IsLessThanOrEqualTo(
            TimeSpan.FromMinutes(30),
            CodeIndexMaintenanceService.DefaultCalibrationBackoffMax,
            "the cap must stay within the half hour the slice was specified with");

        Assert.IsTrue(
            rungs.Zip(rungs.Skip(1)).All(pair => pair.Second >= pair.First),
            "the ladder is monotonic: a later failure never waits less than an earlier one");
    }

    /// <summary>
    /// A3: a sweep that <b>got through</b> resets the ladder. Whatever the outage before it cost, the next failure
    /// starts from 60 s again — otherwise one bad morning would keep a healthy root throttled all day.
    /// </summary>
    [TestMethod]
    public async Task A3_A_Successful_Sweep_Resets_The_Ladder_To_The_First_Rung()
    {
        var logger = new RecordingLogger<CodeIndexMaintenanceService>();
        using var harness = new MaintenanceHarness(logger: logger);
        var offlineRoot = OfflineRoot(harness);
        await StartWithOfflineRootAsync(harness, offlineRoot);

        // Three refusals: the ladder now sits on the 4 minute rung.
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        Assert.AreEqual(3, harness.Status().CalibrationRunCount);
        Assert.AreEqual(3, harness.Status().RejectedCalibrationRunCount);

        // The root comes back. The ladder is not reset by hope: the pending rung still has to elapse.
        Directory.CreateDirectory(offlineRoot);
        harness.Clock.Advance(TimeSpan.FromMinutes(4) - TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(3, harness.Status().CalibrationRunCount, "the 4 minute rung still applies to the pending attempt");

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var recovered = harness.Status();
        Assert.AreEqual(4, recovered.CalibrationRunCount);
        Assert.AreEqual(3, recovered.RejectedCalibrationRunCount, "the fourth attempt got through");
        Assert.IsFalse(recovered.NeedsReconcile, "a successful sweep clears the flag");
        Assert.IsEmpty(await harness.SearchSymbolsAsync("AClass"), "the sweep really removed the stale row");

        // R4: the reset is a visible ladder change.
        Assert.IsTrue(
            logger.Contains(LogLevel.Information, "the retry backoff is reset"),
            "coming off the ladder must be logged too, otherwise the logs cannot tell backoff from recovery");

        // ...and the ladder is back on the first rung: the next failure waits 60 s, not the 8 minutes the
        // counter would be on had it resumed where the outage left it.
        Directory.Delete(offlineRoot, recursive: true);
        harness.Watcher.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError);

        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var afterReset = harness.Status();
        Assert.AreEqual(5, afterReset.CalibrationRunCount, "after a success the first retry is 60 s again");
        Assert.AreEqual(4, afterReset.RejectedCalibrationRunCount);

        // ...and only from that failure does the ladder start widening again: the second rung is 2 minutes, which
        // is the signature of a counter that restarted at 1 — resuming from 4 would need 8 minutes here.
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(6, harness.Status().CalibrationRunCount, "the second failure widens the gap to 2 minutes");

        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(6, harness.Status().CalibrationRunCount, "one minute is not enough for the 2 minute rung");

        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(7, harness.Status().CalibrationRunCount, "the second minute is");
    }

    /// <summary>
    /// A4: no step may calibrate before the rung has elapsed. Fifty steps at <b>one single instant</b> attempt
    /// nothing, and fifty steps spread over less than the rung attempt nothing either — the driver's cadence is
    /// not a licence to probe.
    /// </summary>
    [TestMethod]
    public async Task A4_No_Step_Before_The_Rung_Elapses_Calibrates()
    {
        using var harness = new MaintenanceHarness();
        var offlineRoot = OfflineRoot(harness);
        await StartWithOfflineRootAsync(harness, offlineRoot);

        // Two refusals put the scope on the 2 minute rung.
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(2, harness.Status().CalibrationRunCount);

        for (var step = 0; step < 50; step++)
            Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        Assert.AreEqual(2, harness.Status().CalibrationRunCount, "50 steps at one instant must attempt nothing");

        // 50 seconds of driving, still one full minute short of the rung.
        for (var step = 0; step < 50; step++)
        {
            harness.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        }

        var status = harness.Status();
        Assert.AreEqual(2, status.CalibrationRunCount, "no attempt inside the rung, however many steps run");
        Assert.AreEqual(2, status.RejectedCalibrationRunCount);
        Assert.IsTrue(status.NeedsReconcile, "the scope is still flagged throughout");
    }

    /// <summary>
    /// A5: the <b>normal path</b> is untouched. The routine clock is still exactly the U3-D one (15 minutes, from
    /// the completion of the previous run, from the attach instant while none ran), it does not stack with the
    /// ladder, and a freshly flagged scope is still calibrated on the very next step.
    /// </summary>
    [TestMethod]
    public async Task A5_The_Routine_Clock_Is_Unchanged_And_Does_Not_Stack_With_The_Ladder()
    {
        using var harness = new MaintenanceHarness();
        await harness.StartWithActiveScopeAsync();

        await harness.SeedIndexedFileAsync("legacy/gone.cs", "LegacyClass");

        // (a) The boundary is the U3-D one to the second.
        harness.Clock.Advance(CodeIndexMaintenanceService.DefaultCalibrationPeriod - TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(0, harness.Status().CalibrationRunCount, "a second short of the period is not due");

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());

        var first = harness.Status();
        Assert.AreEqual(1, first.CalibrationRunCount, "the period is the only thing that made it due");
        Assert.AreEqual(1, first.SweptFileCount);
        Assert.IsFalse(first.NeedsReconcile, "a routine sweep is not a reconcile");
        Assert.AreEqual(0, first.RejectedCalibrationRunCount);

        // (b) The next routine run is a full period from that completion — the ladder adds nothing to it, even
        //     though the scope has now been calibrated twice.
        var completion = harness.Clock.GetUtcNow();
        harness.Clock.Advance(CodeIndexMaintenanceService.DefaultCalibrationPeriod - TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(1, harness.Status().CalibrationRunCount, "the routine clock still runs from the completion");

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(2, harness.Status().CalibrationRunCount);
        Assert.IsTrue(
            completion + CodeIndexMaintenanceService.DefaultCalibrationPeriod == harness.Clock.GetUtcNow(),
            "two routine runs are exactly one period apart — the ladder contributed no extra wait");

        // (c) U3-C is unchanged: a fresh flag is calibrated on the very next step — here the flag carries no
        //     failure history, so the ladder's first rung (60 s) applies, exactly as before U3-E.
        harness.Watcher.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError);

        harness.Clock.Advance(CodeIndexMaintenanceService.DefaultCalibrationInterval - TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(2, harness.Status().CalibrationRunCount, "the 60 s bound of U3-C is kept to the second");

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, await harness.Service.ProcessDueBatchesAsync());
        Assert.AreEqual(3, harness.Status().CalibrationRunCount, "and 60 s later the flagged path retries, as before");
        Assert.IsFalse(harness.Status().NeedsReconcile);
    }

    /// <summary>
    /// A6: the ladder is a <b>component constant</b>, not a configuration layer, and the component's construction
    /// surface did not grow a knob for it — that is what keeps this slice out of the Host and out of DI. A6 also
    /// carries the suite-size regress check as a comment: the runner summary is the evidence (baseline 107).
    /// </summary>
    [TestMethod]
    public void A6_The_Ladder_Is_A_Component_Constant_Not_A_Configuration_Knob()
    {
        var type = typeof(CodeIndexMaintenanceService);

        foreach (var name in new[] { nameof(CodeIndexMaintenanceService.DefaultCalibrationInterval), nameof(CodeIndexMaintenanceService.DefaultCalibrationBackoffMax) })
        {
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(field, $"{name} must be a component constant on the service itself");
            Assert.IsTrue(field!.IsInitOnly, $"{name} must be readonly");
            Assert.AreEqual(typeof(TimeSpan), field.FieldType, $"{name} is a TimeSpan knob");
            Assert.IsTrue(field.IsLiteral == false, "a TimeSpan cannot be const; a readonly field is the component constant");
        }

        var constructor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        Assert.IsEmpty(
            constructor.GetParameters()
                .Where(parameter => parameter.Name?.Contains("backoff", StringComparison.OrdinalIgnoreCase) == true
                    || parameter.Name?.Contains("ladder", StringComparison.OrdinalIgnoreCase) == true)
                .Select(parameter => parameter.Name!)
                .ToArray(),
            "the U3-E ladder must not become a constructor knob: it changes nothing on the host side");

        CollectionAssert.AreEqual(
            new[] { "calibration" },
            constructor.GetParameters()
                .Select(parameter => parameter.Name!)
                .Where(name => name.Contains("calibration", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            "the only calibration-related argument stays the pre-existing calibration service, so the ladder added no host-side knob");
    }

    /// <summary>
    /// A root that is deliberately never created: "the drive is not mounted / the share is gone / the directory was
    /// renamed away". It is the one scenario every refusal in this file starts from, so it is built here once.
    /// </summary>
    private static string OfflineRoot(MaintenanceHarness harness) => Path.Combine(harness.Root, "offline-scope");

    /// <summary>Attaches a scope at a root that does not exist, seeds one indexed row in it, and flags the scope.</summary>
    private static async Task StartWithOfflineRootAsync(MaintenanceHarness harness, string offlineRoot)
    {
        await harness.StartWithActiveScopeAsync(offlineRoot);
        await harness.SeedIndexedAbsoluteFileAsync(Path.Combine(offlineRoot, "a.cs"), "AClass");

        Assert.HasCount(1, await harness.SearchSymbolsAsync("AClass"), "positive control: the row is there before the run");
        Assert.IsFalse(Directory.Exists(offlineRoot), "the scenario starts from a root that is not there");

        harness.Watcher.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError);
    }
}
