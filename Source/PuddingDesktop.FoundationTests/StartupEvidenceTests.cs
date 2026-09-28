using System.Text.Json;
using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// D1 startup evidence: the artifact must be ordered, honest about failure and cancellation, and
/// bounded. These tests run without Core, Desktop or any background service.
/// </summary>
public sealed class StartupEvidenceTests
{
    [Fact]
    public void PhasesAreRecordedInCompletionOrderWithDurations()
    {
        var time = new FakeTime();
        var attempt = StartupAttempts.Begin(@"C:\data", Origin(), time);
        var builder = attempt.Phase("host.builder");
        time.Advance(TimeSpan.FromMilliseconds(40));
        builder.Complete("builder");
        var build = attempt.Phase("host.build");
        time.Advance(TimeSpan.FromMilliseconds(60));
        build.Complete();

        var evidence = attempt.Complete();

        Assert.Equal(StartupAttemptOutcome.Succeeded, evidence.Outcome);
        Assert.Null(evidence.FailureType);
        Assert.Equal(["host.builder", "host.build"], evidence.Phases.Select(phase => phase.Name));
        Assert.Equal([1, 2], evidence.Phases.Select(phase => phase.Sequence));
        Assert.All(evidence.Phases, phase => Assert.Equal(StartupPhaseOutcome.Completed, phase.Outcome));
        Assert.Equal(40, evidence.Phases[0].DurationMs, 3);
        Assert.Equal(60, evidence.Phases[1].DurationMs, 3);
        Assert.Equal(0, evidence.Phases[0].AtMs, 3);
        Assert.Equal(40, evidence.Phases[1].AtMs, 3);
        Assert.Equal("builder", evidence.Phases[0].Detail);
        Assert.Equal(100, evidence.TotalMs, 3);
    }

    [Fact]
    public void NestedPhasesKeepTheirOwnBoundaries()
    {
        var time = new FakeTime();
        var attempt = StartupAttempts.Begin("root", Origin(), time);
        using (attempt.Phase("host.initialize"))
        {
            time.Advance(TimeSpan.FromMilliseconds(10));
            attempt.Phase("host.initialize.platform-schema.app-user").Complete();
            time.Advance(TimeSpan.FromMilliseconds(5));
            attempt.Phase("host.initialize.memory-db").Complete();
        }

        var evidence = attempt.Complete();

        // The outer phase settles on dispose, so it is recorded last with the full span.
        Assert.Equal(["host.initialize.platform-schema.app-user", "host.initialize.memory-db", "host.initialize"],
            evidence.Phases.Select(phase => phase.Name));
        var outer = evidence.Phases[2];
        Assert.Equal(StartupPhaseOutcome.Aborted, outer.Outcome);
        Assert.Equal(15, outer.DurationMs, 3);
    }

    [Fact]
    public void PhaseSettledOnceThenDisposedKeepsTheReportedOutcome()
    {
        var attempt = StartupAttempts.Begin("root", Origin(), new FakeTime());
        using (var phase = attempt.Phase("desktop.directory.read"))
        {
            phase.Complete("3 个角色");
            // A second settlement is misuse, not a new fact.
            phase.Skip("ignored");
        }

        var evidence = attempt.Complete();

        var record = Assert.Single(evidence.Phases);
        Assert.Equal(StartupPhaseOutcome.Completed, record.Outcome);
        Assert.Equal("3 个角色", record.Detail);
        Assert.Contains(evidence.Violations, violation => violation.Contains("settled twice", StringComparison.Ordinal));
    }

    [Fact]
    public void SkippedPhaseIsRecordedAsSkippedNotCompleted()
    {
        var attempt = StartupAttempts.Begin("root", Origin(), new FakeTime());
        var phase = attempt.Phase(StartupPhases.HostJiebaBackfill);
        phase.Skip("跳过：SqliteException");

        var evidence = attempt.Complete();

        var record = Assert.Single(evidence.Phases);
        Assert.Equal(StartupPhaseOutcome.Skipped, record.Outcome);
        Assert.Equal("跳过：SqliteException", record.Detail);
    }

    [Fact]
    public void OpenPhaseEndsWhenTheAttemptFails()
    {
        var attempt = StartupAttempts.Begin("root", Origin(), new FakeTime());
        attempt.Phase("host.builder").Complete();
        attempt.Phase("host.build");

        var evidence = attempt.Fail("InvalidOperationException");

        Assert.Equal(StartupAttemptOutcome.Failed, evidence.Outcome);
        Assert.Equal("InvalidOperationException", evidence.FailureType);
        Assert.Equal([StartupPhaseOutcome.Completed, StartupPhaseOutcome.Aborted], evidence.Phases.Select(phase => phase.Outcome));
        Assert.Contains("完成前结束", evidence.Phases[1].Detail);
    }

    [Fact]
    public void CancellationIsDistinctFromFailure()
    {
        var attempt = StartupAttempts.Begin("root", Origin(), new FakeTime());
        attempt.Phase("kernel.core-start");

        var evidence = attempt.Cancel();

        Assert.Equal(StartupAttemptOutcome.Cancelled, evidence.Outcome);
        Assert.Null(evidence.FailureType);
        Assert.Equal(StartupPhaseOutcome.Aborted, Assert.Single(evidence.Phases).Outcome);
    }

    [Fact]
    public void FirstFinalOutcomeStandsAndLaterAttemptsToChangeItAreReported()
    {
        var attempt = StartupAttempts.Begin("root", Origin(), new FakeTime());
        var first = attempt.Complete();
        var second = attempt.Fail("IOException");

        Assert.Same(first, second);
        Assert.Equal(StartupAttemptOutcome.Succeeded, second.Outcome);
        Assert.Contains(second.Violations, violation => violation.Contains("finalized as", StringComparison.Ordinal));
    }

    [Fact]
    public void PhasesAndMilestonesAfterFinalizeCannotMutateTheArtifact()
    {
        var attempt = StartupAttempts.Begin("root", Origin(), new FakeTime());
        attempt.Phase("host.start").Complete();
        var evidence = attempt.Complete();

        attempt.Phase("late.phase");
        attempt.Milestone(StartupMilestone.ConversationReadable);
        attempt.Metric("late.metric", 1);

        Assert.Same(evidence, attempt.Evidence);
        Assert.Single(evidence.Phases);
        Assert.Empty(evidence.Milestones);
        Assert.Empty(evidence.Metrics);
        Assert.Equal(3, evidence.Violations.Count);
    }

    [Fact]
    public void MilestonesAreRecordedOnceAndDuplicatesAreReported()
    {
        var attempt = StartupAttempts.Begin("root", Origin(), new FakeTime());
        attempt.Milestone(StartupMilestone.ShellVisible, "窗口已激活");
        attempt.Milestone(StartupMilestone.ShellVisible, "第二次");
        attempt.Milestone(StartupMilestone.ExecutionReady);
        attempt.Metric(StartupPhases.MetricWorkspaceCount, 7);

        var evidence = attempt.Complete();

        Assert.Equal([StartupMilestone.ShellVisible, StartupMilestone.ExecutionReady], evidence.Milestones.Select(item => item.Milestone));
        Assert.Equal("窗口已激活", evidence.Milestones[0].Detail);
        Assert.Contains(evidence.Violations, violation => violation.Contains("more than once", StringComparison.Ordinal));
        Assert.Equal(7, Assert.Single(evidence.Metrics).Value);
    }

    [Fact]
    public void MilestoneCanBePlacedBeforeTheAttemptStarted()
    {
        var time = new FakeTime();
        var attempt = StartupAttempts.Begin("root", Origin(), time);

        // The shell became visible 300 ms before this attempt object existed.
        attempt.Milestone(StartupMilestone.ShellVisible, "窗口已激活", time.GetUtcNow() - TimeSpan.FromMilliseconds(300));
        time.Advance(TimeSpan.FromMilliseconds(500));

        var evidence = attempt.Complete();

        Assert.Equal(-300, Assert.Single(evidence.Milestones).AtMs, 3);
        Assert.Equal(1000, evidence.SinceProcessStartMs, 3);
        Assert.Equal(500, evidence.TotalMs, 3);
    }

    [Fact]
    public void UnknownMilestoneAndBlankNamesAreRejected()
    {
        var attempt = StartupAttempts.Begin("root", Origin(), new FakeTime());

        Assert.Throws<ArgumentOutOfRangeException>(() => attempt.Milestone((StartupMilestone)99));
        Assert.Throws<ArgumentException>(() => attempt.Phase(" "));
        Assert.Throws<ArgumentException>(() => attempt.Metric("", 1));
        Assert.Throws<ArgumentException>(() => StartupAttempts.Begin(""));
    }

    [Fact]
    public void EvidenceStaysBoundedUnderRunawayPhaseReporting()
    {
        var attempt = StartupAttempts.Begin("root", Origin(), new FakeTime());
        for (var index = 0; index < 600; index++) attempt.Phase($"step.{index}").Complete();

        var evidence = attempt.Complete();

        Assert.Equal(512, evidence.Phases.Count);
        Assert.Contains(evidence.Violations, violation => violation.Contains("phase limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentPhasesAreAllRecordedWithUniqueSequenceNumbers()
    {
        var attempt = StartupAttempts.Begin("root", Origin(), new FakeTime());
        await Task.WhenAll(Enumerable.Range(0, 200).Select(index => Task.Run(() =>
        {
            using var phase = attempt.Phase($"concurrent.{index}");
            phase.Complete();
        })));

        var evidence = attempt.Complete();

        Assert.Equal(200, evidence.Phases.Count);
        Assert.Equal(200, evidence.Phases.Select(phase => phase.Sequence).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 200), evidence.Phases.Select(phase => phase.Sequence).Order());
    }

    [Fact]
    public void PhaseRecordedEventShowsTheSettledStage()
    {
        var attempt = StartupAttempts.Begin("root", Origin(), new FakeTime());
        var seen = new List<string>();
        attempt.PhaseRecorded += (_, record) => seen.Add($"{record.Name}:{record.Outcome}");
        using (var phase = attempt.Phase("desktop.directory.read")) phase.Complete();

        Assert.Equal(["desktop.directory.read:Completed"], seen);
    }

    [Fact]
    public void EvidenceRoundTripsThroughTheJsonlSink()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Pudding-startup-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sink = new StartupEvidenceFileSink(directory);
            var attempt = StartupAttempts.Begin(@"D:\data", Origin(), new FakeTime());
            attempt.Phase("host.build").Complete();
            attempt.Milestone(StartupMilestone.DirectoryReadable, "2 个角色");
            var evidence = attempt.Complete();

            Assert.True(sink.TryWrite(evidence, out var error), error);

            var lines = File.ReadAllLines(sink.FilePath);
            var restored = JsonSerializer.Deserialize<StartupEvidence>(Assert.Single(lines), StartupEvidenceJson.Options);
            Assert.NotNull(restored);
            Assert.Equal(evidence.AttemptId, restored.AttemptId);
            Assert.Equal(@"D:\data", restored.DataRoot);
            Assert.Equal("host.build", Assert.Single(restored.Phases).Name);
            Assert.Equal(StartupMilestone.DirectoryReadable, Assert.Single(restored.Milestones).Milestone);
            Assert.Equal(StartupAttemptOutcome.Succeeded, restored.Outcome);

            // A second attempt appends rather than overwriting: repeated runs stay comparable.
            Assert.True(sink.TryWrite(evidence, out _));
            Assert.Equal(2, File.ReadAllLines(sink.FilePath).Length);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void FileSinkReportsFailureInsteadOfThrowing()
    {
        var file = Path.Combine(Path.GetTempPath(), "Pudding-startup-sink-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "not a directory");
        try
        {
            var sink = new StartupEvidenceFileSink(file);
            var evidence = StartupAttempts.Begin("root", Origin(), new FakeTime()).Complete();

            Assert.False(sink.TryWrite(evidence, out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void OriginDescribesTheRunningProcessWithoutInventingAVersion()
    {
        var processStartedAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(3);
        var origin = StartupAttemptOrigin.Capture(processStartedAt);

        Assert.Equal(32, origin.AttemptId.Length);
        Assert.Equal(Environment.ProcessId, origin.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(origin.BuildVersion));
        Assert.Equal(processStartedAt, origin.ProcessStartedAtUtc);
        Assert.NotEqual(origin.AttemptId, StartupAttemptOrigin.Capture(processStartedAt).AttemptId);
    }

    private static StartupAttemptOrigin Origin()
        => new("attempt-1", 4242, "1.2.3-test", new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Deterministic clock: the artifact must not depend on real elapsed time to be checkable.</summary>
    private sealed class FakeTime : TimeProvider
    {
        private long _ticks;
        private DateTimeOffset _now = new(2026, 9, 28, 0, 0, 1, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta)
        {
            _ticks += delta.Ticks;
            _now += delta;
        }
    }
}
