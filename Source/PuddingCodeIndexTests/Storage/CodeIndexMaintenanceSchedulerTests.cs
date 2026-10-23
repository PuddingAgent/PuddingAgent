using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndexTests.Storage;

/// <summary>
/// Tests for the maintenance scheduler. Every gate is exercised here rather than in the loop
/// wiring, because the gates are where the damage would be: a batch that competes with the writer,
/// a loop that spins on a database where reclamation frees nothing, or a loop that ignores its
/// hourly ceiling.
/// </summary>
[TestClass]
public sealed class CodeIndexMaintenanceSchedulerTests
{
    private const long PageSize = 4096;
    private static readonly DateTimeOffset Noon = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Below_Enter_Bound_Does_Not_Run()
    {
        var state = State(freelistPages: 1_000, pageCount: 200_000);

        var decision = CodeIndexMaintenanceScheduler.Decide(state);

        Assert.IsFalse(decision.ShouldRun);
        Assert.AreEqual(0, decision.BatchPages);
        Assert.IsFalse(decision.NextReclaiming, "未达标时不应进入回收状态");
        StringAssert.Contains(decision.Reason, "below the enter bound");
    }

    [TestMethod]
    public void Busy_Writer_Blocks_A_Qualifying_Database()
    {
        // Free space qualifies, but the writer is busy: reclamation must wait.
        var state = State(freelistPages: 100_000, pageCount: 300_000, writeIdle: false);

        var decision = CodeIndexMaintenanceScheduler.Decide(state);

        Assert.IsFalse(decision.ShouldRun, "写入繁忙时不得抢写锁做维护");
        Assert.IsFalse(decision.NextReclaiming);
        StringAssert.Contains(decision.Reason, "writer is busy");
    }

    [TestMethod]
    public void Idle_Writer_And_Qualifying_Space_Runs_A_Batch()
    {
        var state = State(freelistPages: 100_000, pageCount: 300_000, writeIdle: true);

        var decision = CodeIndexMaintenanceScheduler.Decide(state);

        Assert.IsTrue(decision.ShouldRun);
        Assert.AreEqual(CodeIndexStoragePolicy.DefaultBatchPages, decision.BatchPages);
        Assert.IsTrue(decision.NextReclaiming, "跑过一批后应保持在回收状态");
    }

    [TestMethod]
    public void Minimum_Spacing_Is_Enforced()
    {
        var state = State(
            freelistPages: 100_000,
            pageCount: 300_000,
            writeIdle: true,
            currentlyReclaiming: true,
            lastBatchAt: Noon.AddSeconds(-(CodeIndexMaintenanceScheduler.MinimumSecondsBetweenBatches - 1)));

        var decision = CodeIndexMaintenanceScheduler.Decide(state);

        Assert.IsFalse(decision.ShouldRun);
        Assert.IsTrue(decision.NextReclaiming, "只是等待间隔，仍处于回收状态");
        StringAssert.Contains(decision.Reason, "minimum spacing");
    }

    [TestMethod]
    public void Hourly_Ceiling_Is_Enforced()
    {
        var state = State(
            freelistPages: 100_000,
            pageCount: 300_000,
            writeIdle: true,
            currentlyReclaiming: true,
            lastBatchAt: Noon.AddMinutes(-10),
            batchesInLastHour: CodeIndexMaintenanceScheduler.MaxBatchesPerHour);

        var decision = CodeIndexMaintenanceScheduler.Decide(state);

        Assert.IsFalse(decision.ShouldRun);
        Assert.IsTrue(decision.NextReclaiming);
        StringAssert.Contains(decision.Reason, "ceiling");
    }

    [TestMethod]
    public void Sustained_Zero_Progress_Pauses_And_Leaves_Reclaim_State()
    {
        var state = State(
            freelistPages: 100_000,
            pageCount: 300_000,
            writeIdle: true,
            currentlyReclaiming: true,
            lastBatchAt: Noon.AddMinutes(-10),
            consecutiveZeroBatches: CodeIndexMaintenanceScheduler.ZeroProgressBatchesBeforePause);

        var decision = CodeIndexMaintenanceScheduler.Decide(state);

        Assert.IsFalse(decision.ShouldRun, "连续零进展必须暂停，否则会把 pragma 无操作当成在工作");
        Assert.IsFalse(decision.NextReclaiming, "暂停时同时退出回收状态，避免永远卡住");
        StringAssert.Contains(decision.Reason, "reclaimed nothing");
    }

    [TestMethod]
    public void Falling_Below_Exit_Bound_Leaves_Reclaim_State()
    {
        // 100 MiB free: below the 128 MiB exit bound while already reclaiming.
        var state = State(
            freelistPages: 25_600,
            pageCount: 200_000,
            writeIdle: true,
            currentlyReclaiming: true);

        var decision = CodeIndexMaintenanceScheduler.Decide(state);

        Assert.IsFalse(decision.ShouldRun);
        Assert.IsFalse(decision.NextReclaiming, "跌破退出边界应离开回收状态");
        StringAssert.Contains(decision.Reason, "exit bound");
    }

    [TestMethod]
    public void Non_Incremental_Database_Never_Schedules_Work()
    {
        var state = State(freelistPages: 500_000, pageCount: 600_000, writeIdle: true, autoVacuumMode: 0);

        var decision = CodeIndexMaintenanceScheduler.Decide(state);

        Assert.IsFalse(decision.ShouldRun);
        Assert.IsFalse(decision.NextReclaiming);
    }

    [TestMethod]
    public void Every_Decision_Carries_A_Reason()
    {
        var cases = new[]
        {
            State(freelistPages: 1_000, pageCount: 200_000),
            State(freelistPages: 100_000, pageCount: 300_000, writeIdle: false),
            State(freelistPages: 100_000, pageCount: 300_000, writeIdle: true),
        };

        foreach (var state in cases)
        {
            var decision = CodeIndexMaintenanceScheduler.Decide(state);
            Assert.IsFalse(string.IsNullOrWhiteSpace(decision.Reason), "每个决策都必须带原因，便于排障");
        }
    }

    private static CodeIndexMaintenanceState State(
        long freelistPages,
        long pageCount,
        bool writeIdle = false,
        bool currentlyReclaiming = false,
        DateTimeOffset? lastBatchAt = null,
        int batchesInLastHour = 0,
        int consecutiveZeroBatches = 0,
        int autoVacuumMode = 2) =>
        new(
            Now: Noon,
            Stats: new CodeIndexStorageStats(PageSize, pageCount, freelistPages, autoVacuumMode, 0, 0),
            WriteIdle: writeIdle,
            CurrentlyReclaiming: currentlyReclaiming,
            LastBatchAt: lastBatchAt,
            BatchesInLastHour: batchesInLastHour,
            ConsecutiveZeroBatches: consecutiveZeroBatches);
}
