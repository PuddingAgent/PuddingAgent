using System.Diagnostics;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndexTests;

/// <summary>
/// A22a **暂存切换的回归闸门**：体积合规不等于内容可信。
/// <para>
/// 2026-09-25 生产事故：仓库根 scope 的 ~98 MB live 满索引被一次只含 <b>0~99 文档</b>的构建通过
/// 原子切换静默替换（同命令 4 次中 2 次得 99/0 文件却全报 <c>Succeeded</c>）。本文件的用例复刻该形态，
/// 断言「坏结果不再能被提升为 live」。
/// </para>
/// <para>
/// ⚠️ 硬护栏：所有用例只在 <see cref="Path.GetTempPath"/> 下工作（<see cref="TempSupplyFixture"/> 构造时
/// 直接断言，结构上碰不到真实索引根 <c>D:\data\fulltext-index</c>），且每个用例额外做一次显式断言。
/// </para>
/// </summary>
[TestClass]
public sealed class StagedSupplyRegressionGateTests
{
    // ── A1：staging 0 文档 ⇒ 拒绝，live 一字节不动 ─────────────────────

    [TestMethod]
    public async Task A1_Zero_Document_Staging_Is_Rejected_And_Live_Stays_Untouched()
    {
        using var rig = new StagedRig(
            stagingWriteBytes: 4096,
            stagingCustomizer: engine => { engine.DocumentsOnProbe = 0; return engine; });
        AssertTempRoot(rig);

        var scope = rig.Fixture.ScopeFor(corpusBytes: 1024);

        // live = 健康满索引：100 篇文档 + 真实字节
        rig.LiveEngine.DocumentsOnProbe = 100;
        Directory.CreateDirectory(rig.LiveDirectory);
        File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "segments_1"), new byte[8192]);
        var liveSnapshot = StagedSupplyTestHelpers.SnapshotTree(rig.LiveDirectory);

        var result = await rig.Builder.BuildAsync(scope);

        Assert.IsFalse(result.Success, "0 文档的 staging 绝不能被提升为 live");
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error, "SuspiciousRegression");
        StringAssert.Contains(result.Error, "G2", "拒绝原因必须点名「0 文档」这条规则");
        StringAssert.Contains(result.Error, "stagingDocs=0", "终态消息必须自带可判定数字");
        StringAssert.Contains(result.Error, "liveDocs=100");
        StringAssert.Contains(result.Error, "ratio=0");
        StringAssert.Contains(result.Error, "判定式=");

        Assert.IsNotNull(result.Swap);
        Assert.AreEqual(SupplySwapOutcome.RejectedSuspiciousRegression, result.Swap!.Outcome);
        Assert.AreEqual(0L, result.Swap.StagingDocs);
        Assert.AreEqual(100L, result.Swap.LiveDocsBefore);
        Assert.AreEqual(0d, result.Swap.RegressionRatio, 1e-9);
        StringAssert.Contains(result.Swap.RegressionVerdict!, "G2");
        Assert.AreEqual(result.Swap.LiveBytesBefore, result.Swap.LiveBytesAfter, "被拒时 live 字节必须不变");
        Assert.AreEqual(8192L, result.Swap.LiveBytesAfter);

        Assert.AreEqual(liveSnapshot, StagedSupplyTestHelpers.SnapshotTree(rig.LiveDirectory), "live 目录必须逐字节不变");
        Assert.IsFalse(Directory.Exists(rig.Fixture.TrashRoot()), "被拒 ⇒ 不得创建 .trash（根本没发生切换）");
        Assert.IsEmpty(((FakeIndexDirectorySwapper)rig.Swapper).Moves, "被拒 ⇒ 一次目录移动都不许有");
        Assert.IsEmpty(rig.LiveEngine.InvalidateRequests, "被拒 ⇒ 不得失效 live reader 缓存（live 未改动）");
        Assert.IsFalse(Directory.Exists(rig.StagingRootFor(scope)), "被拒后 staging 该 job 目录必须清掉");
        Assert.IsEmpty(Directory.GetFileSystemEntries(rig.Fixture.StagingRoot()));

        Assert.HasCount(1, rig.StagingEngines);
        Assert.AreEqual(1, rig.StagingEngines[0].BuildCallCount, "拦截发生在构建之后（staging 确实建过）");
        Assert.AreEqual(0, rig.LiveEngine.BuildCallCount, "live 引擎永远不直接构建（写 live 只能经切换）");
    }

    // ── A1b：G2 单独生效（live 不存在也拒绝 0 文档）────────────────────

    [TestMethod]
    public async Task A1b_Zero_Document_Staging_Is_Rejected_Even_Without_A_Live_Index()
    {
        using var rig = new StagedRig(
            stagingWriteBytes: 4096,
            stagingCustomizer: engine => { engine.DocumentsOnProbe = 0; return engine; });
        AssertTempRoot(rig);

        Assert.IsFalse(Directory.Exists(rig.LiveDirectory), "前置：live 不存在（首次构建）");

        var result = await rig.Builder.BuildAsync(rig.Fixture.ScopeFor(corpusBytes: 100));

        Assert.IsFalse(result.Success, "「首次构建」不豁免 0 文档：空索引不得被提升为 live");
        Assert.IsNotNull(result.Swap);
        Assert.AreEqual(SupplySwapOutcome.RejectedSuspiciousRegression, result.Swap!.Outcome);
        StringAssert.Contains(result.Swap.RegressionVerdict!, "G2");
        Assert.IsNull(result.Swap.LiveDocsBefore, "live 不存在 ⇒ 文档数未知，不得伪报 0");
        Assert.IsFalse(result.Swap.HasRegressionRatio, "live 不存在 ⇒ 比值不可计算（不冒充 0）");
        Assert.IsFalse(Directory.Exists(rig.LiveDirectory), "被拒 ⇒ live 保持不存在");
        Assert.IsFalse(Directory.Exists(rig.Fixture.TrashRoot()));
    }

    // ── A2：staging=1 / live=100 ⇒ 拒绝（G3）──────────────────────────

    [TestMethod]
    public async Task A2_Staging_One_Doc_Vs_Live_Hundred_Is_Rejected_By_The_Ratio()
    {
        using var rig = new StagedRig(
            stagingWriteBytes: 4096,
            stagingCustomizer: engine => { engine.DocumentsOnProbe = 1; return engine; });
        AssertTempRoot(rig);

        rig.LiveEngine.DocumentsOnProbe = 100;
        Directory.CreateDirectory(rig.LiveDirectory);
        File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "segments_1"), new byte[8192]);
        var liveSnapshot = StagedSupplyTestHelpers.SnapshotTree(rig.LiveDirectory);

        var result = await rig.Builder.BuildAsync(rig.Fixture.ScopeFor(corpusBytes: 1024));

        Assert.IsFalse(result.Success, "1/100 文档 = 0.01 < 0.5 ⇒ 必须拒绝");
        StringAssert.Contains(result.Error!, "G3");
        Assert.IsNotNull(result.Swap);
        Assert.AreEqual(SupplySwapOutcome.RejectedSuspiciousRegression, result.Swap!.Outcome);
        Assert.AreEqual(1L, result.Swap.StagingDocs);
        Assert.AreEqual(100L, result.Swap.LiveDocsBefore);
        Assert.AreEqual(0.01d, result.Swap.RegressionRatio, 1e-9);
        StringAssert.Contains(result.Swap.RegressionVerdict!, "G3");
        StringAssert.Contains(result.Swap.RegressionVerdict!, "0.5", "拒绝原因必须给出生效阈值");

        Assert.AreEqual(liveSnapshot, StagedSupplyTestHelpers.SnapshotTree(rig.LiveDirectory), "live 目录必须逐字节不变");
        Assert.IsFalse(Directory.Exists(rig.Fixture.TrashRoot()));
        Assert.IsEmpty(((FakeIndexDirectorySwapper)rig.Swapper).Moves);
    }

    // ── A3：staging=60 / live=100 ⇒ 放行（防过度拦截）──────────────────

    [TestMethod]
    public async Task A3_Sixty_Docs_Vs_Live_Hundred_Is_Still_Swapped()
    {
        using var rig = new StagedRig(
            stagingWriteBytes: 4096,
            stagingCustomizer: engine => { engine.DocumentsOnProbe = 60; return engine; });
        AssertTempRoot(rig);

        rig.LiveEngine.DocumentsOnProbe = 100;
        Directory.CreateDirectory(rig.LiveDirectory);
        File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "old.bin"), new byte[512]);

        var result = await rig.Builder.BuildAsync(rig.Fixture.ScopeFor(corpusBytes: 100));

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.Swap);
        Assert.AreEqual(SupplySwapOutcome.Swapped, result.Swap!.Outcome);
        Assert.AreEqual(60L, result.Swap.StagingDocs);
        Assert.AreEqual(100L, result.Swap.LiveDocsBefore);
        Assert.AreEqual(0.6d, result.Swap.RegressionRatio, 1e-9);
        Assert.IsNull(result.Swap.RegressionVerdict, "放行 ⇒ 不得有拒绝原因");

        Assert.AreEqual(4096L, new FileInfo(Path.Combine(rig.LiveDirectory, "segments_1")).Length, "staging 已就位 = 切换确实发生");
        Assert.IsFalse(File.Exists(Path.Combine(rig.LiveDirectory, "old.bin")), "旧 live 不得残留在 live");
        Assert.HasCount(2, ((FakeIndexDirectorySwapper)rig.Swapper).Moves, "旧 live → .trash、staging → live");
        Assert.IsEmpty(Directory.GetFileSystemEntries(rig.Fixture.StagingRoot()));
    }

    // ── A4：首次构建（live 不存在）不得被误判为回归 ────────────────────

    [TestMethod]
    public async Task A4_First_Build_With_Three_Docs_Is_Allowed()
    {
        using var rig = new StagedRig(
            stagingWriteBytes: 4096,
            stagingCustomizer: engine => { engine.DocumentsOnProbe = 3; return engine; });
        AssertTempRoot(rig);

        var scope = rig.Fixture.ScopeFor(corpusBytes: 100);
        Assert.IsFalse(Directory.Exists(rig.LiveDirectory), "前置：无 live（首次构建）");

        var result = await rig.Builder.BuildAsync(scope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.Swap);
        Assert.AreEqual(SupplySwapOutcome.Swapped, result.Swap!.Outcome);
        Assert.IsNull(result.Swap.LiveDocsBefore, "live 不存在 ⇒ 基线为 null（不是 0）");
        Assert.AreEqual(3L, result.Swap.StagingDocs);
        Assert.IsNull(result.Swap.RegressionVerdict, "首次构建不得被拦");
        Assert.AreEqual(4096L, new FileInfo(Path.Combine(rig.LiveDirectory, "segments_1")).Length);
        Assert.IsFalse(Directory.Exists(rig.Fixture.TrashRoot()), "首次切换没有旧 live ⇒ 不产生 .trash 残留");
    }

    // ── A5（最强，真 Lucene）：近空 staging 不得替换满 live，旧内容仍可命中 ──

    [TestMethod]
    public async Task A5_Real_Lucene_Near_Empty_Staging_Cannot_Replace_A_Populated_Live_Index()
    {
        using var fixture = new TempSupplyFixture();
        AssertTempRoot(fixture);

        const string needle = "berryneedle";
        for (var i = 0; i < 5; i++)
            fixture.Write($"doc{i}.cs", $"class Doc{i} {{ public const string Token = \"{needle}\"; }}");

        using var liveEngine = new LuceneSearchEngine(fixture.Options);
        var liveBuild = await liveEngine.BuildIndexAsync(fixture.Corpus);
        Assert.IsTrue(liveBuild.Success, liveBuild.Error);

        var rooted = (IFullTextIndexRootedEngine)liveEngine;
        var liveDirectory = rooted.ResolveIndexDirectory(fixture.Corpus);
        var liveProbeBefore = rooted.ProbeDocuments(fixture.Corpus);
        Assert.IsTrue(liveProbeBefore.Exists, "前置：live 索引目录必须存在");
        Assert.IsGreaterThan(0L, liveProbeBefore.Documents!.Value, "前置：live 必须有文档");

        var liveSnapshot = StagedSupplyTestHelpers.SnapshotTree(liveDirectory);
        var liveBytes = SupplyIndexDirectoryLayout.MeasureDirectoryBytes(liveDirectory);

        // 复刻事故形态：语料里**还有文件**，但一个都索引不进去（全部换成不可索引扩展名）
        // ⇒ 真引擎报 Success，而索引里 0 篇文档。
        foreach (var file in Directory.GetFiles(fixture.Corpus))
            File.Delete(file);

        for (var i = 0; i < 20; i++)
            File.WriteAllBytes(Path.Combine(fixture.Corpus, $"blob{i}.bin"), new byte[1024]);

        var options = new SupplyCoordinatorOptions
        {
            DefaultBudgetBytes = SupplyCoordinatorOptions.DefaultMaxIndexBytes,
        };

        var builder = new StagedFullTextIndexBuilder(
            liveEngine,
            stagingOptions => new LuceneSearchEngine(stagingOptions),
            fixture.Options,
            options);

        var scope = new SupplyScope(fixture.ScopeKey, fixture.Corpus, BudgetBytes: null, CorpusBytes: 20 * 1024, JobId: "job-a22a-a5");
        var result = await builder.BuildAsync(scope);

        Assert.IsFalse(result.Success, "真引擎报 Success 但 0 文档 ⇒ 必须拒绝（这正是本次事故的形态）");
        Assert.IsNotNull(result.Swap);
        Assert.AreEqual(SupplySwapOutcome.RejectedSuspiciousRegression, result.Swap!.Outcome);
        StringAssert.Contains(result.Swap.RegressionVerdict!, "G2");
        Assert.AreEqual(0L, result.Swap.StagingDocs, "staging 必须被探到 0 篇");
        Assert.AreEqual(liveProbeBefore.Documents, result.Swap.LiveDocsBefore);
        Assert.AreEqual(0d, result.Swap.RegressionRatio, 1e-9);

        // ★ 关键断言：live 逐字节不变，且**同一引擎实例**仍能命中旧内容（证明 live 真的没被触碰）
        Assert.AreEqual(liveSnapshot, StagedSupplyTestHelpers.SnapshotTree(liveDirectory), "live 索引目录必须逐字节不变");
        Assert.AreEqual(liveBytes, SupplyIndexDirectoryLayout.MeasureDirectoryBytes(liveDirectory));

        var stillHit = await liveEngine.SearchAsync(needle, fixture.Corpus);
        Assert.IsTrue(stillHit.Success, stillHit.Error);
        Assert.IsNotEmpty(stillHit.Matches, "被拒 ⇒ 事故前的 live 必须仍可被真查询命中");

        Assert.IsFalse(Directory.Exists(SupplyIndexDirectoryLayout.TrashRoot(fixture.IndexRoot)), "被拒 ⇒ 不得产生 .trash");
        // 注：`.staging` **容器**目录由布局惰性创建后保留（既有语义）；要断言的是「该 job 目录被清掉」。
        var stagingRoot = SupplyIndexDirectoryLayout.StagingRoot(fixture.IndexRoot);
        Assert.IsTrue(
            !Directory.Exists(stagingRoot) || Directory.GetFileSystemEntries(stagingRoot).Length == 0,
            "被拒 ⇒ staging 不得有残留（该 job 目录必须已清）");
    }

    // ── A6：Describe() 必须携带文档数事实 ──────────────────────────────

    [TestMethod]
    public async Task A6_Describe_Carries_Staging_Live_Docs_And_Ratio()
    {
        // ① 被拒路径（G2）
        using (var rig = new StagedRig(
            stagingWriteBytes: 4096,
            stagingCustomizer: engine => { engine.DocumentsOnProbe = 0; return engine; }))
        {
            AssertTempRoot(rig);
            rig.LiveEngine.DocumentsOnProbe = 100;
            Directory.CreateDirectory(rig.LiveDirectory);
            File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "segments_1"), new byte[8192]);

            var rejected = await rig.Builder.BuildAsync(rig.Fixture.ScopeFor(corpusBytes: 1024));
            var described = rejected.Swap!.Describe();

            StringAssert.Contains(described, "outcome=RejectedSuspiciousRegression");
            StringAssert.Contains(described, "stagingDocs=0");
            StringAssert.Contains(described, "liveDocs=100");
            StringAssert.Contains(described, "ratio=0");
            StringAssert.Contains(described, "regressionVerdict=G2");
        }

        // ② 放行路径（Swapped）：字段同样对外可见（不是只在拒绝时才写）
        using (var rig = new StagedRig(
            stagingWriteBytes: 2048,
            stagingCustomizer: engine => { engine.DocumentsOnProbe = 60; return engine; }))
        {
            AssertTempRoot(rig);
            rig.LiveEngine.DocumentsOnProbe = 100;
            Directory.CreateDirectory(rig.LiveDirectory);
            File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "old.bin"), new byte[256]);

            var swapped = await rig.Builder.BuildAsync(rig.Fixture.ScopeFor(corpusBytes: 100));
            var described = swapped.Swap!.Describe();

            StringAssert.Contains(described, "outcome=Swapped");
            StringAssert.Contains(described, "stagingDocs=60");
            StringAssert.Contains(described, "liveDocs=100");
            StringAssert.Contains(described, "ratio=0.6");
            Assert.IsFalse(described.Contains("regressionVerdict=", StringComparison.Ordinal), "无拒绝原因时不得凭空造一个");
        }

        // ③ 不可计算时写 <null>，不把「无意义」伪装成 0
        using (var rig = new StagedRig(
            stagingWriteBytes: 2048,
            stagingCustomizer: engine => { engine.DocumentsOnProbe = 3; return engine; }))
        {
            AssertTempRoot(rig);
            var first = await rig.Builder.BuildAsync(rig.Fixture.ScopeFor(corpusBytes: 100));
            var described = first.Swap!.Describe();

            StringAssert.Contains(described, "stagingDocs=3");
            StringAssert.Contains(described, "liveDocs=<null>");
            StringAssert.Contains(described, "ratio=<null>");
        }
    }

    // ── A7：阈值非法 ⇒ 回落 0.5（绝不按 0 放行）────────────────────────

    [TestMethod]
    public async Task A7_Invalid_Min_Ratio_Falls_Back_To_The_Default()
    {
        var warnings = new List<string>();
        using var listener = new CollectingTraceListener(warnings);

        // 该场景（staging=40 / live=100）默认阈值 0.5 会拒绝；若把非法值当 0 处理则会放行 ⇒ 取红点。
        foreach (var invalid in new[] { 0d, -1d, 1.5d, double.NaN })
        {
            using var rig = new StagedRig(
                stagingWriteBytes: 4096,
                minStagingToLiveDocRatio: invalid,
                stagingCustomizer: engine => { engine.DocumentsOnProbe = 40; return engine; });
            AssertTempRoot(rig);

            rig.LiveEngine.DocumentsOnProbe = 100;
            Directory.CreateDirectory(rig.LiveDirectory);
            File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "segments_1"), new byte[8192]);

            var result = await rig.Builder.BuildAsync(rig.Fixture.ScopeFor(corpusBytes: 1024));

            Assert.IsFalse(result.Success, $"阈值 {invalid} 非法 ⇒ 必须回落 0.5 并拒绝（40 < 50）");
            Assert.IsNotNull(result.Swap);
            Assert.AreEqual(SupplySwapOutcome.RejectedSuspiciousRegression, result.Swap!.Outcome);
            StringAssert.Contains(result.Swap.RegressionVerdict!, "G3");
            StringAssert.Contains(
                result.Swap.RegressionVerdict!, "0.5", $"阈值 {invalid} 非法 ⇒ 生效阈值必须是回落的 0.5");
        }

        // 对照组：阈值 0.4 合法 ⇒ 同一场景放行（证明上面那批用例确实在判「回落 vs 放行」）
        using (var rig = new StagedRig(
            stagingWriteBytes: 4096,
            minStagingToLiveDocRatio: 0.4,
            stagingCustomizer: engine => { engine.DocumentsOnProbe = 40; return engine; }))
        {
            AssertTempRoot(rig);
            rig.LiveEngine.DocumentsOnProbe = 100;
            Directory.CreateDirectory(rig.LiveDirectory);
            File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "segments_1"), new byte[8192]);

            var allowed = await rig.Builder.BuildAsync(rig.Fixture.ScopeFor(corpusBytes: 1024));

            Assert.IsTrue(allowed.Success, allowed.Error);
            Assert.AreEqual(SupplySwapOutcome.Swapped, allowed.Swap!.Outcome);
        }

        Assert.IsNotEmpty(warnings, "非法阈值必须告警（Trace），不得静默回落");
        Assert.IsTrue(
            warnings.Any(w => w.Contains("MinStagingToLiveDocRatio", StringComparison.Ordinal)
                              && w.Contains("非法", StringComparison.Ordinal)),
            "告警必须点名阈值字段；实际：" + string.Join(" | ", warnings));
    }

    // ── 附加：G1 / G4 两条 fail-closed 分支（「读不出」≠「确实是 0」）────

    [TestMethod]
    public async Task G1_Unreadable_Staging_Document_Count_Is_Rejected()
    {
        using var rig = new StagedRig(
            stagingWriteBytes: 4096,
            stagingCustomizer: engine =>
            {
                engine.ProbeOverride = new IndexDocumentProbe(Exists: true, Documents: null);
                return engine;
            });
        AssertTempRoot(rig);

        rig.LiveEngine.DocumentsOnProbe = 100;
        Directory.CreateDirectory(rig.LiveDirectory);
        File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "segments_1"), new byte[8192]);

        var result = await rig.Builder.BuildAsync(rig.Fixture.ScopeFor(corpusBytes: 1024));

        Assert.IsFalse(result.Success, "staging 文档数读不出 ⇒ fail-closed 拒绝（既不伪报 0，也不放行）");
        Assert.IsNotNull(result.Swap);
        StringAssert.Contains(result.Swap!.RegressionVerdict!, "G1");
        Assert.IsNull(result.Swap.StagingDocs);
        Assert.IsFalse(Directory.Exists(rig.Fixture.TrashRoot()));
    }

    [TestMethod]
    public async Task G1b_Successful_Build_That_Wrote_No_Index_Directory_Is_Rejected()
    {
        // 复刻事故运行 ③ 的形态：构建报 Success / 0 文件 / 297 ms，但 staging 下**根本没有索引目录**。
        using var rig = new StagedRig(
            stagingWriteBytes: null,
            stagingCustomizer: engine =>
            {
                engine.BuildBehaviour = (_, _) => Task.FromResult(new FullTextIndexResult(true, 0, 0, 297, null));
                return engine;
            });
        AssertTempRoot(rig);

        rig.LiveEngine.DocumentsOnProbe = 99;
        Directory.CreateDirectory(rig.LiveDirectory);
        File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "segments_1"), new byte[8192]);
        var liveSnapshot = StagedSupplyTestHelpers.SnapshotTree(rig.LiveDirectory);

        var result = await rig.Builder.BuildAsync(rig.Fixture.ScopeFor(corpusBytes: 1024));

        Assert.IsFalse(result.Success, "staging 连索引目录都没有（0 文件）⇒ 必须拒绝");
        Assert.IsNotNull(result.Swap);
        Assert.AreEqual(SupplySwapOutcome.RejectedSuspiciousRegression, result.Swap!.Outcome);
        StringAssert.Contains(result.Swap.RegressionVerdict!, "G1");
        Assert.IsNull(result.Swap.StagingDocs, "目录不存在 ⇒ 文档数未知（不是 0）");
        Assert.AreEqual(99L, result.Swap.LiveDocsBefore);
        Assert.AreEqual(liveSnapshot, StagedSupplyTestHelpers.SnapshotTree(rig.LiveDirectory), "live 必须逐字节不变");
        Assert.IsFalse(Directory.Exists(rig.Fixture.TrashRoot()));
        Assert.IsEmpty(((FakeIndexDirectorySwapper)rig.Swapper).Moves);
        Assert.IsEmpty(rig.LiveEngine.InvalidateRequests);
    }

    [TestMethod]
    public async Task G4_Unreadable_Live_Document_Count_Is_Rejected()
    {
        using var rig = new StagedRig(
            stagingWriteBytes: 4096,
            stagingCustomizer: engine => { engine.DocumentsOnProbe = 50; return engine; });
        AssertTempRoot(rig);

        rig.LiveEngine.ProbeOverride = new IndexDocumentProbe(Exists: true, Documents: null);
        Directory.CreateDirectory(rig.LiveDirectory);
        File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "segments_1"), new byte[8192]);

        var result = await rig.Builder.BuildAsync(rig.Fixture.ScopeFor(corpusBytes: 1024));

        Assert.IsFalse(result.Success, "live 存在但读不出 ⇒ 无法建立基线，fail-closed 拒绝");
        Assert.IsNotNull(result.Swap);
        StringAssert.Contains(result.Swap!.RegressionVerdict!, "G4");
        Assert.IsNull(result.Swap.LiveDocsBefore, "读不出 ≠ 0：必须是 null");
        Assert.IsFalse(Directory.Exists(rig.Fixture.TrashRoot()));
    }

    // ── 护栏与工具 ──────────────────────────────────────────────────────

    private static void AssertTempRoot(StagedRig rig) => AssertTempRoot(rig.Fixture);

    private static void AssertTempRoot(TempSupplyFixture fixture)
    {
        Assert.IsTrue(
            fixture.Root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            $"A22a 用例只允许使用系统临时目录，实际根：{fixture.Root}");
        Assert.IsFalse(
            fixture.IndexRoot.StartsWith(@"D:\data", StringComparison.OrdinalIgnoreCase),
            "绝不允许把索引根指向真实索引根 D:\\data\\fulltext-index");
    }

    /// <summary>把 <see cref="Trace"/> 告警收集起来（A22a R4 的「回落必须告警」断言）；构造即挂载、释放即卸载。</summary>
    private sealed class CollectingTraceListener : TraceListener
    {
        private readonly List<string> _messages;

        internal CollectingTraceListener(List<string> messages)
        {
            _messages = messages;
            Trace.Listeners.Add(this);
        }

        public override void Write(string? message)
        {
            if (!string.IsNullOrEmpty(message))
                _messages.Add(message);
        }

        public override void WriteLine(string? message) => Write(message);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Trace.Listeners.Remove(this);

            base.Dispose(disposing);
        }
    }
}
