using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIndexTests.Storage;

/// <summary>
/// Tests for the background reclamation driver.
/// <para>
/// The driving contract is: counters survive between ticks, busy outcomes never masquerade as
/// zero progress, and the loop converges on the policy band instead of oscillating around a single
/// threshold. All three are observable here without a host or a timer.
/// </para>
/// </summary>
[TestClass]
public sealed class CodeIndexMaintenanceRunnerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    // The production band starts at 256 MiB, which a temporary test database never reaches.
    // Scaling the band down keeps the same gate logic under test without inflating fixtures.
    private static readonly CodeIndexStoragePolicy TestPolicy = new(
        EnterFreeBytes: 64L * 1024,
        EnterFreeRatio: 0.05d,
        ExitFreeBytes: 32L * 1024,
        ExitFreeRatio: 0.02d);

    [TestMethod]
    public async Task Does_Nothing_When_Free_Space_Is_Below_The_Band()
    {
        var root = NewRoot();
        try
        {
            var store = new SqliteCodeIndexStore(Path.Combine(root, "db", "index.db"));
            await store.InitializeAsync();
            var runner = new CodeIndexMaintenanceRunner(store);

            var outcome = await runner.TickAsync(Noon, writeIdle: true);

            Assert.IsFalse(outcome.Ran);
            Assert.AreEqual(0L, outcome.ReclaimedPages);
            Assert.IsFalse(runner.CurrentlyReclaiming);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task Runs_A_Batch_When_Space_Qualifies_And_Writer_Is_Idle()
    {
        var root = NewRoot();
        try
        {
            var (store, runner) = await BuildWithFreePagesAsync(root);

            var outcome = await runner.TickAsync(Noon, writeIdle: true);

            Assert.IsTrue(outcome.Ran, $"应执行回收，原因：{outcome.Decision.Reason}");
            Assert.IsTrue(outcome.ReclaimedPages > 0, $"应实测回收，实际 {outcome.ReclaimedPages}");
            Assert.IsTrue(runner.CurrentlyReclaiming, "跑过一批后应保持在回收状态");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task Busy_Writer_Does_Not_Count_As_Zero_Progress()
    {
        var root = NewRoot();
        try
        {
            var (_, runner) = await BuildWithFreePagesAsync(root);

            // Three consecutive ticks with the writer busy. If these counted as zero progress the
            // runner would pause itself after the third one, which is exactly the confusion this
            // test exists to prevent.
            for (var i = 0; i < CodeIndexMaintenanceScheduler.ZeroProgressBatchesBeforePause; i++)
            {
                var busy = await runner.TickAsync(Noon.AddMinutes(i * 2), writeIdle: false);
                Assert.IsFalse(busy.Ran);
                StringAssert.Contains(busy.Decision.Reason, "writer is busy");
            }

            // The writer goes idle: the loop must still be armed.
            var outcome = await runner.TickAsync(Noon.AddMinutes(10), writeIdle: true);

            Assert.IsTrue(outcome.Ran, $"写入空闲后应恢复回收，原因：{outcome.Decision.Reason}");
            Assert.IsTrue(outcome.ReclaimedPages > 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task Minimum_Spacing_Survives_Across_Ticks()
    {
        var root = NewRoot();
        try
        {
            var (_, runner) = await BuildWithFreePagesAsync(root);

            var first = await runner.TickAsync(Noon, writeIdle: true);
            Assert.IsTrue(first.Ran);

            // Too soon afterwards: the spacing gate must hold even though space still qualifies.
            var tooSoon = await runner.TickAsync(Noon.AddSeconds(5), writeIdle: true);
            Assert.IsFalse(tooSoon.Ran);
            StringAssert.Contains(tooSoon.Decision.Reason, "minimum spacing");
            Assert.IsTrue(runner.CurrentlyReclaiming, "只是等待间隔，仍应处于回收状态");

            // After the minimum spacing: allowed again.
            var later = await runner.TickAsync(
                Noon.AddSeconds(CodeIndexMaintenanceScheduler.MinimumSecondsBetweenBatches + 1),
                writeIdle: true);
            Assert.IsTrue(later.Ran, $"间隔满足后应允许回收，原因：{later.Decision.Reason}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task Converges_Then_Stops_When_Free_Space_Falls_Out_Of_The_Band()
    {
        var root = NewRoot();
        try
        {
            var (_, runner) = await BuildWithFreePagesAsync(root);

            var now = Noon;
            var ran = 0;
            for (var i = 0; i < 200; i++)
            {
                now = now.AddSeconds(CodeIndexMaintenanceScheduler.MinimumSecondsBetweenBatches + 1);
                var outcome = await runner.TickAsync(now, writeIdle: true);
                if (!outcome.Ran)
                    break;
                ran++;
            }

            Assert.IsTrue(ran > 0, "至少应跑过一批");
            Assert.IsFalse(runner.CurrentlyReclaiming, "空闲页回落到退出边界后应离开回收状态");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<(SqliteCodeIndexStore Store, CodeIndexMaintenanceRunner Runner)>
        BuildWithFreePagesAsync(string root)
    {
        var store = new SqliteCodeIndexStore(Path.Combine(root, "db", "index.db"));
        await store.InitializeAsync();

        const int symbolCount = 20_000;
        var symbols = Enumerable.Range(0, symbolCount)
            .Select(i => new CodeSymbolRecord(
                WorkspaceId: "ws",
                ProjectId: "proj",
                FilePath: "src/bulk.cs",
                SymbolId: $"sym-{i:D6}",
                Name: $"GeneratedSymbol{i}",
                Kind: CodeSymbolKind.Class,
                StartLine: i,
                EndLine: i + 1))
            .ToList();

        await store.UpsertSymbolsAsync("ws", "proj", symbols);
        await store.ClearSymbolsForFileAsync("ws", "proj", "src/bulk.cs");

        return (store, new CodeIndexMaintenanceRunner(store, TestPolicy));
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "pudding-maint-runner", Guid.NewGuid().ToString("N"));

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Temporary cleanup must not mask the assertion result.
        }
    }
}
