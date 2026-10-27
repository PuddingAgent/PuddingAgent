using PuddingRuntime.Services;
using PuddingCode.Models;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class BackgroundCompactionCandidateTests
{
    [TestMethod]
    public async Task MultiPartSummaryKeepsMakingProgressBeyondFiveMinutes_ThenWaitsForSafeBoundary()
    {
        var clock = new ManualClock();
        var history = new List<string> { "original" };
        var calls = 0;
        await using var candidate = new BackgroundCompactionCandidate<List<string>, int>(async (preparation, ct) =>
        {
            var result = await BoundedCompactionSummary.GenerateAsync(
                [new(ChatRole.User, new string('x', 1_450_000))], (_, _) =>
                {
                    calls++;
                    clock.Advance(TimeSpan.FromSeconds(40));
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(AgentExecutionLlmInvocationResult.CreateSuccess(
                        new LlmResponse($"complete checkpoint {calls}", null) { FinishReason = "stop" }, null));
                }, ct, observe: (attempt, _, _) =>
                {
                    preparation.ReportProgress(attempt);
                    return Task.CompletedTask;
                });
            Assert.IsTrue(result.Success);
            var live = await preparation.AwaitBoundaryAsync(ct);
            // The preparation lease also cannot interrupt a released durable transaction.
            clock.Advance(TimeSpan.FromMinutes(10));
            ct.ThrowIfCancellationRequested();
            live[0] = result.Response!.Content!;
            return calls;
        }, CancellationToken.None, clock: clock);

        await candidate.WaitUntilReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(candidate.IsReady, "All segments must finish before opening the commit gate.");
        Assert.IsTrue(calls * 40 > 300, "Regression fixture must outlast the former whole-job timeout.");
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.IsFalse(candidate.Completion.IsCompleted, "A ready candidate waits for its owner without expiring.");
        CollectionAssert.AreEqual(new[] { "original" }, history);
        history.Add("new user message");
        Assert.AreEqual(calls, await candidate.CommitAtBoundaryAsync(history));
        Assert.AreEqual("new user message", history[1]);
        StringAssert.Contains(history[0], "complete checkpoint");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StalledOrRejectedSummaryExpiresWithDistinctProgressEvidence(bool rejected)
    {
        var clock = new ManualClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var candidate = new BackgroundCompactionCandidate<int, int>(async (preparation, ct) =>
        {
            entered.SetResult();
            await continuePreparation.Task;
            if (rejected) preparation.ReportProgress(new(1, "map", 1, 23, 0, 0, 100, "hash", "length",
                10, 20, 0, 20, 100, 240000, "output_truncated"));
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        }, CancellationToken.None, clock: clock);
        await entered.Task;
        clock.Advance(TimeSpan.FromMinutes(4));
        continuePreparation.SetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => candidate.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(candidate.IsReady);
        Assert.AreEqual("candidate_preparation_idle_timeout", candidate.CancellationReason);
        Assert.AreEqual("300000", candidate.GetEvidence()["compaction_no_progress_ms"]);
        Assert.AreEqual(rejected ? "output_truncated" : "none", candidate.GetEvidence()["compaction_last_decision"]);
    }

    [TestMethod]
    public async Task OwnerCancellationIsNotReportedAsPreparationExpiry()
    {
        using var owner = new CancellationTokenSource();
        await using var candidate = new BackgroundCompactionCandidate<int, int>(async (preparation, ct) =>
            await preparation.AwaitBoundaryAsync(ct), owner.Token);
        await candidate.WaitUntilReadyAsync(CancellationToken.None);
        await owner.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => candidate.Completion);
        Assert.AreEqual("candidate_turn_cancelled", candidate.CancellationReason);
        Assert.ThrowsExactly<OperationCanceledException>(() => candidate.CommitAtBoundaryAsync(1));
    }

    [TestMethod]
    public async Task PreparationCannotModifyLiveHistoryUntilForegroundReleasesBoundary()
    {
        var history = new List<string> { "old" };
        await using var candidate = new BackgroundCompactionCandidate<List<string>, int>(async (preparation, ct) =>
        {
            var live = await preparation.AwaitBoundaryAsync(ct);
            live[0] = "summary";
            return live.Count;
        }, CancellationToken.None);
        await candidate.WaitUntilReadyAsync(CancellationToken.None);
        Assert.AreEqual("old", history[0]);
        Assert.IsFalse(candidate.Completion.IsCompleted);
        history.Add("new user message");
        Assert.AreEqual(2, await candidate.CommitAtBoundaryAsync(history));
        CollectionAssert.AreEqual(new[] { "summary", "new user message" }, history);
    }

    [TestMethod]
    public async Task PreparationFailureWakesHardWaitWithoutOpeningCommitGate()
    {
        await using var candidate = new BackgroundCompactionCandidate<int, int>((_, _) => Task.FromResult(-1), CancellationToken.None);
        await candidate.WaitUntilReadyAsync(CancellationToken.None);
        Assert.AreEqual(-1, await candidate.Completion);
        Assert.IsFalse(candidate.IsReady);
    }

    [TestMethod]
    public async Task TurnCancellationJoinsCandidateAndNeverCommits()
    {
        var committed = false;
        var candidate = new BackgroundCompactionCandidate<int, int>(async (preparation, ct) =>
        {
            await preparation.AwaitBoundaryAsync(ct);
            committed = true;
            return 1;
        }, CancellationToken.None);
        await candidate.WaitUntilReadyAsync(CancellationToken.None);
        await candidate.DisposeAsync();
        Assert.IsFalse(committed);
        Assert.IsTrue(candidate.Completion.IsCompleted);
        Assert.AreEqual("candidate_owner_disposed", candidate.CancellationReason);
    }

    // Deterministic one-shot timers let lifecycle tests advance minutes without wall-clock sleeps.
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_sync) return _ticks; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        internal void Advance(TimeSpan duration)
        {
            List<ManualTimer> due;
            lock (_sync)
            {
                _ticks += duration.Ticks;
                due = _timers.Where(t => t.Due <= _ticks).ToList();
                foreach (var timer in due) timer.Due = long.MaxValue;
            }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal long Due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._sync) Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                return true;
            }
            internal void Fire() => callback(state);
            public void Dispose() { lock (clock._sync) Due = long.MaxValue; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
