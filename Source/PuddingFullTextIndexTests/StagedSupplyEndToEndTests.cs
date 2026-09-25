using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndexTests;

/// <summary>
/// A2a **真实 Lucene 端到端**断言：用真实引擎 + 真实清点 + 真实文件租约 + 真实 <see cref="Directory.Move"/>
/// 走完「构建 → 原子切换 → 查询」全链路。
/// <list type="bullet">
/// <item>A1：切换成功后 <c>.staging</c> 下该 job 目录消失、live 可被 <c>SearchAsync</c> **真查询命中**；</item>
/// <item>A5：**同一个引擎实例**在切换前查旧内容、切换后能查到新内容（陈旧 reader 会继续看到旧索引）；</item>
/// <item>A3 + 完成标准 #4：极小预算再构建 ⇒ 被拒且旧索引仍可被查询命中、live 逐字节不变。</item>
/// </list>
/// ⚠️ 夹具只在 <see cref="Path.GetTempPath"/> 下工作，绝不触碰真实索引根。
/// </summary>
[TestClass]
public sealed class StagedSupplyEndToEndTests
{
    [TestMethod]
    public async Task A1_A5_Real_Lucene_Swap_Is_Queryable_And_The_Same_Engine_Sees_The_New_Content()
    {
        using var fixture = new TempSupplyFixture();
        fixture.Write("alpha.cs", "class Alpha { public const string Token = \"berryneedle\"; }");

        using var liveEngine = new LuceneSearchEngine(fixture.Options);
        var coordinator = CreateCoordinator(fixture, liveEngine);

        // ① 首次供给：构建 + 切换
        var first = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        var firstJob = await SupplyTestHelpers.AwaitJobAsync(coordinator, first.JobId!);
        Assert.AreEqual(SupplyJobState.Succeeded, firstJob.State, firstJob.Message);
        StringAssert.Contains(firstJob.Message!, "outcome=Swapped");

        var beforeSwap = await liveEngine.SearchAsync("berryneedle", fixture.Corpus);
        Assert.IsTrue(beforeSwap.Success, beforeSwap.Error);
        Assert.IsNotEmpty(beforeSwap.Matches, "切换后 live 必须能被真查询命中");

        AssertNoResidue(fixture, trashRootMustExist: false);

        // ② 语料换内容 → 再供给（第二个 job）：同一引擎实例必须看到新内容
        fixture.Write("alpha.cs", "class Alpha { public const string Token = \"cinnamontoken\"; }");

        var second = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        var secondJob = await SupplyTestHelpers.AwaitJobAsync(coordinator, second.JobId!);
        Assert.AreEqual(SupplyJobState.Succeeded, secondJob.State, secondJob.Message);
        StringAssert.Contains(secondJob.Message!, "outcome=Swapped");

        var afterSwap = await liveEngine.SearchAsync("cinnamontoken", fixture.Corpus);
        Assert.IsTrue(afterSwap.Success, afterSwap.Error);
        Assert.IsNotEmpty(
            afterSwap.Matches,
            "切换后同一引擎实例必须看到新内容 —— 陈旧 reader 会继续看到旧索引（这正是 R4 要修掉的缺陷）");

        var stale = await liveEngine.SearchAsync("berryneedle", fixture.Corpus);
        Assert.IsEmpty(stale.Matches, "整目录替换后旧内容必须不可见");

        // 第二次供给确实替换过旧 live ⇒ .trash 必然产生过，且必须已被清空（R3.4）
        AssertNoResidue(fixture, trashRootMustExist: true);

        Assert.AreNotEqual(first.JobId, second.JobId, "第二次供给必须是新的 job（不得被幂等合并吞掉）");
    }

    [TestMethod]
    public async Task A3_Over_Budget_Rebuild_Is_Rejected_And_The_Old_Index_Is_Still_Queryable()
    {
        using var fixture = new TempSupplyFixture();
        fixture.Write("alpha.cs", "class Alpha { public const string Token = \"berryneedle\"; }");

        using var liveEngine = new LuceneSearchEngine(fixture.Options);
        var coordinator = CreateCoordinator(fixture, liveEngine);

        // ① 先成功供给一次（默认 1 GiB 预算）
        var ok = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        var okJob = await SupplyTestHelpers.AwaitJobAsync(coordinator, ok.JobId!);
        Assert.AreEqual(SupplyJobState.Succeeded, okJob.State, okJob.Message);

        var liveDirectory = LiveDirectory(liveEngine, fixture.Corpus);
        var liveSnapshotBefore = StagedSupplyTestHelpers.SnapshotTree(liveDirectory);
        var liveBytesBefore = SupplyIndexDirectoryLayout.MeasureDirectoryBytes(liveDirectory);
        Assert.IsGreaterThan(0L, liveBytesBefore, "前置：live 索引必须真的占字节");

        // ② 用极小预算再构建一次 ⇒ 必被拒
        fixture.Write("alpha.cs", "class Alpha { public const string Token = \"cinnamontoken\"; }");

        var cramped = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus, budgetBytes: 1));
        var crampedJob = await SupplyTestHelpers.AwaitJobAsync(coordinator, cramped.JobId!);

        Assert.AreEqual(SupplyJobState.Failed, crampedJob.State);
        StringAssert.Contains(crampedJob.Message!, "OverBudget");
        StringAssert.Contains(crampedJob.Message!, "outcome=RejectedOverBudget");
        StringAssert.Contains(crampedJob.Message!, "budgetBytes=1");

        // ③ 旧索引仍可被查询命中，且 live 逐字节不变
        var stillQueryable = await liveEngine.SearchAsync("berryneedle", fixture.Corpus);
        Assert.IsTrue(stillQueryable.Success, stillQueryable.Error);
        Assert.IsNotEmpty(stillQueryable.Matches, "被拒后旧索引必须仍可被查询命中");

        var newContent = await liveEngine.SearchAsync("cinnamontoken", fixture.Corpus);
        Assert.IsEmpty(newContent.Matches, "被拒 ⇒ 新内容绝不可见");

        Assert.AreEqual(
            liveSnapshotBefore,
            StagedSupplyTestHelpers.SnapshotTree(liveDirectory),
            "被拒时 live 必须逐字节不变（连一个文件都不许动）");
        Assert.AreEqual(liveBytesBefore, SupplyIndexDirectoryLayout.MeasureDirectoryBytes(liveDirectory));
        Assert.IsTrue(
            !Directory.Exists(SupplyIndexDirectoryLayout.StagingRoot(fixture.IndexRoot))
            || Directory.GetFileSystemEntries(SupplyIndexDirectoryLayout.StagingRoot(fixture.IndexRoot)).Length == 0,
            "被拒后不得留下 staging 残留");
    }

    private static string LiveDirectory(IFullTextIndexRootedEngine engine, string corpusRootPath) =>
        engine.ResolveIndexDirectory(corpusRootPath);

    /// <summary>
    /// 残留断言：<c>.staging</c>/<c>.trash</c> 不存在**或**为空都算「无残留」（实现是惰性建目录：
    /// 没有旧 live 时不会产生 <c>.trash</c>）。
    /// </summary>
    private static void AssertNoResidue(TempSupplyFixture fixture, bool trashRootMustExist)
    {
        AssertNoEntries(SupplyIndexDirectoryLayout.StagingRoot(fixture.IndexRoot), ".staging");
        AssertNoEntries(SupplyIndexDirectoryLayout.TrashRoot(fixture.IndexRoot), ".trash");

        if (trashRootMustExist)
        {
            Assert.IsTrue(
                Directory.Exists(SupplyIndexDirectoryLayout.TrashRoot(fixture.IndexRoot)),
                "本次供给替换过旧 live ⇒ .trash 根必须被创建过（否则说明切换根本没发生）");
        }
    }

    private static void AssertNoEntries(string directory, string label)
    {
        if (!Directory.Exists(directory))
            return;

        Assert.IsEmpty(Directory.GetFileSystemEntries(directory), $"{label} 不得有残留");
    }

    private static FullTextIndexSupplyCoordinator CreateCoordinator(TempSupplyFixture fixture, LuceneSearchEngine liveEngine)
    {
        var options = new SupplyCoordinatorOptions
        {
            DefaultBudgetBytes = SupplyCoordinatorOptions.DefaultMaxIndexBytes,
            // 每个请求都必须真的重建：否则「同 scope 12h 内成功过」的合并规则会把第二次供给吞掉。
            MinRebuildInterval = TimeSpan.Zero,
        };

        var builder = new StagedFullTextIndexBuilder(
            liveEngine,
            stagingOptions => new LuceneSearchEngine(stagingOptions),
            fixture.Options,
            options);

        return new FullTextIndexSupplyCoordinator(
            new FileSystemSupplyInventory(fixture.Options),
            builder,
            new FileSupplyLease(fixture.Options),
            options);
    }
}
