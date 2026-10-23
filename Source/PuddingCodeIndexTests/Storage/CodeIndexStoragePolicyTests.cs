using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndexTests.Storage;

/// <summary>
/// Boundary tests for the storage policy. The policy is pure logic, so every boundary can be
/// exercised without touching a database; the point is to prove the band is hysteretic and that a
/// non-incremental database never enters the reclaim state (where the pragma would be a no-op).
/// </summary>
[TestClass]
public sealed class CodeIndexStoragePolicyTests
{
    private const long PageSize = 4096;
    private const long EnterPages = 65_536;   // 256 MiB
    private const long ExitPages = 32_768;    // 128 MiB

    [TestMethod]
    public void Default_Policy_Uses_Astra_Band()
    {
        var policy = CodeIndexStoragePolicy.Default;

        Assert.AreEqual(256L * 1024 * 1024, policy.EnterFreeBytes);
        Assert.AreEqual(0.20d, policy.EnterFreeRatio);
        Assert.AreEqual(128L * 1024 * 1024, policy.ExitFreeBytes);
        Assert.AreEqual(0.10d, policy.ExitFreeRatio);
        Assert.AreEqual(128, CodeIndexStoragePolicy.DefaultBatchPages);
        Assert.AreEqual(EnterPages * PageSize, policy.EnterFreeBytes);
        Assert.AreEqual(ExitPages * PageSize, policy.ExitFreeBytes);
    }

    [TestMethod]
    public void Does_Not_Enter_When_Absolute_Amount_Is_Below_Floor()
    {
        // 200 MiB free but 25 percent of the file: proportion is fine, absolute amount is not.
        var stats = Stats(freelistPages: 50_000, pageCount: 200_000);

        Assert.IsFalse(CodeIndexStoragePolicy.Default.ShouldReclaim(stats, currentlyReclaiming: false),
            "空闲页绝对量不足 256 MiB 时不应进入回收状态");
    }

    [TestMethod]
    public void Does_Not_Enter_When_Ratio_Is_Below_Floor()
    {
        // ~285 MiB free but only 18.25 percent of the file.
        var stats = Stats(freelistPages: 73_000, pageCount: 400_000);

        Assert.IsFalse(CodeIndexStoragePolicy.Default.ShouldReclaim(stats, currentlyReclaiming: false),
            "空闲页比例不足 20% 时不应进入回收状态");
    }

    [TestMethod]
    public void Enters_When_Both_Absolute_And_Ratio_Clear_Their_Floors()
    {
        // ~312 MiB free, 26.7 percent of the file.
        var stats = Stats(freelistPages: 80_000, pageCount: 300_000);

        Assert.IsTrue(CodeIndexStoragePolicy.Default.ShouldReclaim(stats, currentlyReclaiming: false),
            "两轴都达标时应进入回收状态");
    }

    [TestMethod]
    public void Keeps_Reclaiming_While_Above_Exit_Bounds()
    {
        // ~156 MiB free, 13.3 percent of the file: below the enter floors, above the exit floors.
        var stats = Stats(freelistPages: 40_000, pageCount: 300_000);

        Assert.IsTrue(CodeIndexStoragePolicy.Default.ShouldReclaim(stats, currentlyReclaiming: true),
            "已进入回收状态时，只要仍高于退出阈值就继续（这就是滞回带）");
    }

    [TestMethod]
    public void Stops_When_Absolute_Amount_Drops_Below_Exit_Floor()
    {
        // ~117 MiB free: below the 128 MiB exit floor.
        var stats = Stats(freelistPages: 30_000, pageCount: 100_000);

        Assert.IsFalse(CodeIndexStoragePolicy.Default.ShouldReclaim(stats, currentlyReclaiming: true),
            "空闲页绝对量跌破 128 MiB 应停止回收");
    }

    [TestMethod]
    public void Stops_When_Ratio_Drops_Below_Exit_Floor()
    {
        // ~195 MiB free but only 8.3 percent of the file.
        var stats = Stats(freelistPages: 50_000, pageCount: 600_000);

        Assert.IsFalse(CodeIndexStoragePolicy.Default.ShouldReclaim(stats, currentlyReclaiming: true),
            "空闲页比例跌破 10% 应停止回收");
    }

    [TestMethod]
    public void Exit_Bounds_Are_Exclusive()
    {
        // Exactly 128 MiB and exactly 10 percent: the exit comparison is strict (>), so this stops.
        var stats = Stats(freelistPages: ExitPages, pageCount: ExitPages * 10);

        Assert.IsFalse(CodeIndexStoragePolicy.Default.ShouldReclaim(stats, currentlyReclaiming: true),
            "恰好落在退出阈值上应停止（退出边界为严格大于）");
    }

    [TestMethod]
    public void Never_Reclaims_When_AutoVacuum_Is_Not_Incremental()
    {
        // Plenty of free space, but the pragma would be a silent no-op.
        var stats = Stats(freelistPages: 500_000, pageCount: 600_000, autoVacuumMode: 0);

        Assert.IsFalse(stats.SupportsIncrementalReclaim);
        Assert.IsFalse(CodeIndexStoragePolicy.Default.ShouldReclaim(stats, currentlyReclaiming: false),
            "非 INCREMENTAL 模式不得进入回收状态，否则循环会空转");
        Assert.IsFalse(CodeIndexStoragePolicy.Default.ShouldReclaim(stats, currentlyReclaiming: true),
            "非 INCREMENTAL 模式即使标记为回收中也必须立即退出");
    }

    [TestMethod]
    public void Stats_Derive_Free_Bytes_And_Ratio_From_Freelist()
    {
        var stats = Stats(freelistPages: 1_000, pageCount: 4_000);

        Assert.AreEqual(PageSize * 1_000, stats.FreeBytes);
        Assert.AreEqual(0.25d, stats.FreeRatio, 1e-9);
    }

    [TestMethod]
    public void Stats_Tolerate_Empty_Database()
    {
        // A freshly created file can report no pages yet; ratio must not divide by zero.
        var stats = new CodeIndexStorageStats(PageSize, PageCount: 0, FreelistCount: 0, AutoVacuumMode: 2, 0, 0);

        Assert.AreEqual(0d, stats.FreeRatio);
        Assert.AreEqual(0L, stats.FreeBytes);
    }

    private static CodeIndexStorageStats Stats(long freelistPages, long pageCount, int autoVacuumMode = 2) =>
        new(PageSize, pageCount, freelistPages, autoVacuumMode, DatabaseFileBytes: 0, WalFileBytes: 0);
}
