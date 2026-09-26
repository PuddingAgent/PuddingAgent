using System.Text;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure;
using PuddingFullTextIndex.Infrastructure.Maintenance;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Supply;
using PuddingFullTextIndex.Infrastructure.Text;
using Directory = System.IO.Directory;

namespace PuddingFullTextIndexTests;

/// <summary>
/// S3a：<see cref="LuceneFullTextIndexMaintenanceEngine"/>（真实 Lucene 局部写内核 + path inventory）的组件侧断言。
/// <para>
/// 依据（唯一权威）：方案 §3.6 执行层单批顺序（① gate ③ 最终 stat/过滤/提取 ④ 提取失败不进 delete 集合
/// ⑤ 单 writer CREATE_OR_APPEND ⑥ 先删后加 ⑦ 确认删除 ⑧ 预算硬限 ⑨ 单批 commit）、§4.1「直写 live」硬门禁
/// （quota 失败后 rollback 保留旧 commit 且不突破预算）、§4.2「内容先在内存构造成待写文档」与预检口径、
/// §4.5「未 commit 的文档对查询不可见 / 批次期间查询看到上一个一致 commit」。
/// </para>
/// <para>
/// 七条不变量 → 本文件逐条钉死（测试名即映射）：
/// <list type="bullet">
/// <item><description>I1 内容先提取后 delete/add、提取失败绝不进 delete 集合 ⇒
/// <c>I1_ExtractionFailure_KeepsOldDocuments_AndDoesNotTouchTheIndex</c>（★M1 靶点）+
/// 正对照 <c>I1_Control_ExtractionSuccess_DeletesOldDocumentsBeforeAddingTheNewOnes</c>。</description></item>
/// <item><description>I2 单批 commit / 未 commit 不可见 ⇒ <c>I2_SingleBatchCommit_IsAtomic_AndUncommittedDocumentsStayInvisible</c>
/// （并配合 I5/I3 的「拒绝 / 取消后新文档不可见」）。</description></item>
/// <item><description>I3 取消 ⇒ rollback、不提交、返回 Cancelled ⇒ <c>I3_CancelBeforeSubmit_...</c> + <c>I3_CancelDuringContentExtraction_...</c>。</description></item>
/// <item><description>I4 writer lock Busy ⇒ <c>I4_WriterLockBusy_ReturnsBusyWithoutWriting</c>（含释放后成功的对照）。</description></item>
/// <item><description>I5 quota 预检 ⇒ <c>I5_QuotaPrecheck_RejectsWithoutCommitting_AndKeepsIndexBytesEqual</c>（★M2 靶点）。</description></item>
/// <item><description>I6 只变对应 path ⇒ <c>I6_CreateModifyDeleteRename_ChangeOnlyTheTargetPath</c>。</description></item>
/// <item><description>I7 <c>CheckpointAdvanced</c> 是产物 ⇒ <c>I7_CheckpointAdvanced_IsProducedByPolicy_AndLyingInputCannotChangeIt</c>。</description></item>
/// </list>
/// 另有 <c>Guards_...</c> 收口门禁（live 索引不存在、单批路径上限、空变更集、探针未实现）。
/// </para>
/// <para>
/// 所有语料与索引根都落在 <c>%TEMP%</c> 下（<see cref="Initialize"/> 有硬断言），结构上碰不到生产索引根
/// <c>D:\data\fulltext-index</c>；本文件零后台线程、零 git 操作。
/// </para>
/// </summary>
[TestClass]
public sealed class LuceneMaintenanceEngineTests
{
    private const string DocPdf = "doc.pdf";
    private const string KeepTxt = "keep.txt";

    /// <summary>计划里的集合总预算默认值（1 GiB）——「预算充足」的对照组用它。</summary>
    private const long DefaultMaxIndexBytes = 1_073_741_824L;

    /// <summary>计划里的单批路径数上限默认值（<c>MaintenanceOptions.MaxBatchPaths</c>）。</summary>
    private const int DefaultMaxPaths = 512;

    public TestContext TestContext { get; set; } = null!;

    private string _root = null!;
    private string _corpus = null!;
    private string _indexRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-fts-s3a-" + Guid.NewGuid().ToString("N"));

        // 硬断言：语料与索引只允许落在 %TEMP% 下（红线：不得碰 D:\data\fulltext-index）
        Assert.IsTrue(
            _root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            $"测试根必须在 %TEMP% 下，实际 {_root}");
        Assert.IsFalse(
            _root.StartsWith(@"D:\data", StringComparison.OrdinalIgnoreCase),
            $"测试根绝不允许落在生产数据根下，实际 {_root}");

        _corpus = Path.Combine(_root, "corpus");
        _indexRoot = Path.Combine(_root, "index");
        Directory.CreateDirectory(_corpus);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不应把测试判红
        }
    }

    // ── I1：提取失败 ⇒ 旧文档保留（★M1 靶点）──────────────────────────────

    [TestMethod]
    public async Task I1_ExtractionFailure_KeepsOldDocuments_AndDoesNotTouchTheIndex()
    {
        var extractor = new ScriptedExtractor();
        var pdfPath = WriteCorpusFile(DocPdf, "%PDF-1.4 s3a placeholder bytes");
        extractor.Set(pdfPath, "pdf first line zzoldone\npdf second line zzoldtwo");
        WriteCorpusFile(KeepTxt, "keep zzkeepone");

        using var harness = NewHarness(extractor);
        var build = await harness.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");
        Assert.AreEqual(2, build.IndexedFileCount, "初始构建必须真的索引到 doc.pdf 与 keep.txt（否则下面的断言会假绿）");

        var before = await InventoryAsync(harness.Maintenance);
        Assert.AreEqual(2, before.Count, $"初始清册必须恰好 2 个路径（doc.pdf + keep.txt）：{Describe(before)}");
        Assert.AreEqual(2, MustFind(before, pdfPath).DocumentCount, "初始索引里 doc.pdf 有 2 行 ⇒ 2 个文档");
        var bytesBefore = MeasureBytes(ScopeIndexDirectory);

        // 让 doc.pdf 的提取必然失败（模拟共享冲突 / 提取器故障）
        extractor.FailingPaths.Add(pdfPath);

        var result = await harness.Maintenance.ApplyChangesAsync(
            NewChangeSet(new[] { Upsert(pdfPath) }),
            GenerousBudget(bytesBefore));

        Assert.AreEqual(FullTextMutationState.PartiallyApplied, result.State, $"提取失败的批次不得声称完全成功：{result.Message}");
        Assert.AreEqual(0, result.UpsertAppliedCount, "提取失败的路径不得被应用");
        Assert.AreEqual(1, result.RetainedOldCount, "提取失败的路径必须进入「保留旧文档」集合");
        Assert.AreEqual(1, result.FailedCount, "FailedCount 必须是保留项与其它失败项的合计");
        CollectionAssert.Contains(result.RetainedOldPaths.ToArray(), pdfPath);
        CollectionAssert.Contains(result.FailedPaths.ToArray(), pdfPath);

        // ★ I1 的要害（**放在最前**）：旧文档必须原样保留（提取失败绝不进入 delete 集合）
        var after = await InventoryAsync(harness.Maintenance);
        AssertInventoryEqual(before, after, "提取失败不得改变任何路径 / 文档数");
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zzoldone"), "旧内容必须仍可搜到");
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zzoldtwo"), "旧内容的第二行也必须仍在");

        Assert.IsFalse(result.CheckpointAdvanced, "有保留项 ⇒ checkpoint 绝不推进");
        Assert.IsNull(result.CommitMilliseconds, "无事可写 ⇒ 不得打开 writer、不得 commit");
        Assert.AreEqual(bytesBefore, result.IndexBytesBefore, "IndexBytesBefore 必须是实测值");
        Assert.AreEqual(bytesBefore, result.IndexBytesAfter, "未提交 ⇒ 索引字节必须逐位不变");
        StringAssert.Contains(result.Message, "内容提取失败", "失败原因必须可诊断（不得静默吞掉）");
    }

    [TestMethod]
    public async Task I1_Control_ExtractionSuccess_DeletesOldDocumentsBeforeAddingTheNewOnes()
    {
        var extractor = new ScriptedExtractor();
        var pdfPath = WriteCorpusFile(DocPdf, "%PDF-1.4 s3a placeholder bytes");
        extractor.Set(pdfPath, "pdf first line zzoldone\npdf second line zzoldtwo");

        using var harness = NewHarness(extractor);
        var build = await harness.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");
        Assert.AreEqual(1, build.IndexedFileCount, "初始构建必须真的索引到 doc.pdf");

        var before = await InventoryAsync(harness.Maintenance);
        Assert.AreEqual(2, MustFind(before, pdfPath).DocumentCount);
        var bytesBefore = MeasureBytes(ScopeIndexDirectory);

        // 旧 2 行 → 新 1 行：重写后文档数必须是 1（先删后加），而不是 3（叠加）
        extractor.Set(pdfPath, "pdf rewritten line zznewone");

        var result = await harness.Maintenance.ApplyChangesAsync(
            NewChangeSet(new[] { Upsert(pdfPath) }),
            GenerousBudget(bytesBefore));

        Assert.AreEqual(FullTextMutationState.Applied, result.State, result.Message);
        Assert.AreEqual(1, result.UpsertAppliedCount);
        Assert.AreEqual(0, result.RetainedOldCount);
        Assert.AreEqual(0, result.FailedCount);
        Assert.IsTrue(result.CheckpointAdvanced, "全成功且为完整补偿轮次 ⇒ 允许推进");
        Assert.IsNotNull(result.CommitMilliseconds, "提交必须被度量");

        var after = await InventoryAsync(harness.Maintenance);
        Assert.AreEqual(1, after.Count, $"清册必须仍只有 doc.pdf：{Describe(after)}");
        Assert.AreEqual(1, after[0].DocumentCount, "重写后必须是 1 个文档（旧 2 行被真正删除，未与新文档叠加）");
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zznewone"));
        Assert.AreEqual(0, await CountHitsAsync(harness.Search, "zzoldone"), "旧行必须被删除");
        Assert.AreEqual(0, await CountHitsAsync(harness.Search, "zzoldtwo"), "旧行必须被删除");
    }

    // ── I2：单批 commit / 未 commit 不可见 ────────────────────────────────

    [TestMethod]
    public async Task I2_SingleBatchCommit_IsAtomic_AndUncommittedDocumentsStayInvisible()
    {
        var a = WriteCorpusFile("a.txt", "alpha first zzolda");
        var b = WriteCorpusFile("b.txt", "beta first zzoldb");

        using var harness = NewHarness();
        var build = await harness.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");

        // 前置对照：新标记在批次之前本来就搜不到（防止「本来就能搜到」的假绿）
        Assert.AreEqual(0, await CountHitsAsync(harness.Search, "zznewa"));
        Assert.AreEqual(0, await CountHitsAsync(harness.Search, "zznewb"));

        // 读侧快照：批次期间它必须继续看到上一个一致 commit（§4.5）
        using var snapshotDirectory = FSDirectory.Open(ScopeIndexDirectory);
        using var snapshotReader = DirectoryReader.Open(snapshotDirectory);
        var snapshotSearcher = new IndexSearcher(snapshotReader);
        var snapshotDocsBefore = snapshotReader.NumDocs;

        File.WriteAllText(a, "alpha rewritten zznewa");
        File.WriteAllText(b, "beta rewritten zznewb");

        var result = await harness.Maintenance.ApplyChangesAsync(
            NewChangeSet(new[] { Upsert(a), Upsert(b) }),
            GenerousBudget(MeasureBytes(ScopeIndexDirectory)));

        Assert.AreEqual(FullTextMutationState.Applied, result.State, result.Message);
        Assert.AreEqual(2, result.UpsertAppliedCount, "两条 Upsert 必须在同一批里一起提交");

        // ① 整批一次可见（不是半批）
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zznewa"), "整批提交后两条新内容都必须可见");
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zznewb"), "整批提交后两条新内容都必须可见");

        // ② 批次期间的读侧快照仍停在「上一个一致 commit」：新文档对它不可见
        Assert.AreEqual(snapshotDocsBefore, snapshotReader.NumDocs, "旧快照的文档数不得被批次改写");
        Assert.AreEqual(0, snapshotSearcher.Search(new TermQuery(new Term("content", "zznewa")), 10).TotalHits);
        Assert.AreEqual(0, snapshotSearcher.Search(new TermQuery(new Term("content", "zznewb")), 10).TotalHits);
    }

    // ── I5：quota 预检不过 ⇒ Rejected 且不提交（★M2 靶点）─────────────────

    [TestMethod]
    public async Task I5_QuotaPrecheck_RejectsWithoutCommitting_AndKeepsIndexBytesEqual()
    {
        var a = WriteCorpusFile("a.txt", "alpha first zzolda");

        using var harness = NewHarness();
        var build = await harness.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");

        var before = await InventoryAsync(harness.Maintenance);
        var liveBytes = MeasureBytes(ScopeIndexDirectory);
        Assert.AreEqual(
            liveBytes,
            LuceneFullTextIndexMaintenanceEngine.MeasureIndexBytes(ScopeIndexDirectory),
            "测试侧独立度量必须与引擎侧度量一致（仪器交叉校验）");

        File.WriteAllText(a, "alpha rewritten zznewa");
        var b = WriteCorpusFile("b.txt", "beta brand new zznewbs3a");
        var changeSet = NewChangeSet(new[] { Upsert(a), Upsert(b) });

        // 预算恰好等于当前 live 字节 ⇒ 集合口径下不得有任何增长（RemainingBytes == 0）
        var blocked = new FullTextMutationBudget(MaxIndexBytes: liveBytes, LiveIndexBytes: liveBytes, MaxPaths: DefaultMaxPaths);
        var result = await harness.Maintenance.ApplyChangesAsync(changeSet, blocked);

        Assert.AreEqual(FullTextMutationState.Rejected, result.State, $"预算不足必须被拒绝：{result.Message}");
        Assert.AreEqual(0, result.UpsertAppliedCount, "被拒绝 ⇒ 一个 Upsert 都不得应用");
        Assert.AreEqual(0, result.DeleteAppliedCount);
        Assert.IsNull(result.CommitMilliseconds, "被拒绝 ⇒ 绝不 commit");
        Assert.AreEqual(liveBytes, result.IndexBytesBefore);
        Assert.AreEqual(liveBytes, result.IndexBytesAfter, "§4.1 硬门禁：拒绝后必须保留上一个 commit、索引字节不变");
        Assert.AreEqual(liveBytes, MeasureBytes(ScopeIndexDirectory), "独立度量也必须不变");
        Assert.IsFalse(result.CheckpointAdvanced, "被拒绝 ⇒ checkpoint 不推进");
        StringAssert.Contains(result.Message, "预算硬限");
        StringAssert.Contains(result.Message, "集合口径");

        // 未 commit 的文档对查询不可见 + 旧文档原样保留
        Assert.AreEqual(0, await CountHitsAsync(harness.Search, "zznewa"), "未 commit 的文档对查询不可见");
        Assert.AreEqual(0, await CountHitsAsync(harness.Search, "zznewbs3a"), "未 commit 的文档对查询不可见");
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zzolda"), "旧文档必须原样保留");
        AssertInventoryEqual(before, await InventoryAsync(harness.Maintenance), "被拒绝 ⇒ 清册不变");

        // 对照组：同一批量、同一代码路径，预算充足 ⇒ 全部应用
        // （证明上面的「不可见」不是「这条代码路径压根写不进去」）
        var applied = await harness.Maintenance.ApplyChangesAsync(changeSet, GenerousBudget(liveBytes));
        Assert.AreEqual(FullTextMutationState.Applied, applied.State, applied.Message);
        Assert.AreEqual(2, applied.UpsertAppliedCount);
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zznewa"));
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zznewbs3a"));
        Assert.AreEqual(2, (await InventoryAsync(harness.Maintenance)).Count, "对照组必须真的多出一个路径（a.txt 改写 + 新增 b.txt）");
    }

    // ── I3：取消 ⇒ rollback、不提交、返回 Cancelled ───────────────────────

    [TestMethod]
    public async Task I3_CancelBeforeSubmit_ReturnsCancelledAndCommitsNothing()
    {
        var a = WriteCorpusFile("a.txt", "alpha first zzolda");

        using var harness = NewHarness();
        var build = await harness.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");

        var before = await InventoryAsync(harness.Maintenance);
        var bytesBefore = MeasureBytes(ScopeIndexDirectory);
        File.WriteAllText(a, "alpha rewritten zznewa");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await harness.Maintenance.ApplyChangesAsync(
            NewChangeSet(new[] { Upsert(a) }),
            GenerousBudget(bytesBefore),
            cts.Token);

        Assert.AreEqual(FullTextMutationState.Cancelled, result.State, $"取消必须被显式报告：{result.Message}");
        Assert.IsNull(result.CommitMilliseconds, "取消 ⇒ 绝不 commit");
        Assert.AreEqual(0, result.UpsertAppliedCount + result.DeleteAppliedCount + result.RetainedOldCount);
        Assert.AreEqual(bytesBefore, result.IndexBytesAfter, "取消 ⇒ 未提交 ⇒ 索引字节不变");
        Assert.IsFalse(result.CheckpointAdvanced, "取消 ⇒ checkpoint 不推进");
        AssertInventoryEqual(before, await InventoryAsync(harness.Maintenance), "取消 ⇒ 清册不变");
        Assert.AreEqual(0, await CountHitsAsync(harness.Search, "zznewa"), "未提交的文档不可见");
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zzolda"), "取消不得删掉旧文档");
    }

    [TestMethod]
    public async Task I3_CancelDuringContentExtraction_ReturnsCancelledAndKeepsOldDocuments()
    {
        var extractor = new ScriptedExtractor();
        var pdfPath = WriteCorpusFile(DocPdf, "%PDF-1.4 s3a placeholder bytes");
        extractor.Set(pdfPath, "pdf old line zzoldone");

        using var harness = NewHarness(extractor);
        var build = await harness.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");
        Assert.AreEqual(1, build.IndexedFileCount, "初始构建必须真的索引到 doc.pdf");

        var before = await InventoryAsync(harness.Maintenance);
        var bytesBefore = MeasureBytes(ScopeIndexDirectory);

        extractor.Set(pdfPath, "pdf rewritten zznewone");

        using var cts = new CancellationTokenSource();
        extractor.CancelOnRead = (pdfPath, cts); // 提取到该文件时取消令牌并抛 OperationCanceledException

        var result = await harness.Maintenance.ApplyChangesAsync(
            NewChangeSet(new[] { Upsert(pdfPath) }),
            GenerousBudget(bytesBefore),
            cts.Token);

        Assert.AreEqual(FullTextMutationState.Cancelled, result.State, $"提取中途取消必须返回 Cancelled：{result.Message}");
        Assert.IsNull(result.CommitMilliseconds, "取消 ⇒ 绝不 commit");
        Assert.AreEqual(bytesBefore, result.IndexBytesAfter, "取消 ⇒ 未提交 ⇒ 索引字节不变");
        Assert.IsFalse(result.CheckpointAdvanced);
        AssertInventoryEqual(before, await InventoryAsync(harness.Maintenance), "取消 ⇒ 清册不变");
        Assert.AreEqual(0, await CountHitsAsync(harness.Search, "zznewone"), "未提交的文档不可见");
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zzoldone"), "取消不得删掉旧文档");
    }

    // ── I4：writer lock Busy ⇒ Busy、不抛、不提交 ─────────────────────────

    [TestMethod]
    public async Task I4_WriterLockBusy_ReturnsBusyWithoutWriting()
    {
        var a = WriteCorpusFile("a.txt", "alpha first zzolda");

        using var harness = NewHarness();
        var build = await harness.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");

        var before = await InventoryAsync(harness.Maintenance);
        var bytesBefore = MeasureBytes(ScopeIndexDirectory);
        File.WriteAllText(a, "alpha rewritten zznewa");
        var changeSet = NewChangeSet(new[] { Upsert(a) });

        // 另一个 writer 持同一索引目录的 write.lock（模拟另一进程 / 手动供给 CLI）
        using (var blockerDirectory = FSDirectory.Open(ScopeIndexDirectory))
        using (var blocker = new IndexWriter(
            blockerDirectory,
            new IndexWriterConfig(LuceneVersion.LUCENE_48, new JiebaAnalyzer()) { OpenMode = OpenMode.CREATE_OR_APPEND }))
        {
            var result = await harness.Maintenance.ApplyChangesAsync(changeSet, GenerousBudget(bytesBefore));

            Assert.AreEqual(FullTextMutationState.Busy, result.State, $"锁被占用必须返回 Busy 而不是抛异常：{result.Message}");
            Assert.IsNull(result.CommitMilliseconds, "Busy ⇒ 不提交");
            Assert.AreEqual(0, result.UpsertAppliedCount);
            Assert.AreEqual(bytesBefore, result.IndexBytesAfter, "Busy ⇒ 未写入任何东西");
            Assert.IsFalse(result.CheckpointAdvanced, "Busy ⇒ checkpoint 不推进");
            StringAssert.Contains(result.Message, "writer lock");
            AssertInventoryEqual(before, await InventoryAsync(harness.Maintenance), "Busy ⇒ 清册不变");
            Assert.AreEqual(0, await CountHitsAsync(harness.Search, "zznewa"), "未写入 ⇒ 新内容不可见");
            Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zzolda"), "Busy 不得删掉旧文档");
        }

        // 对照组：锁释放后，同一批量必须成功（证明 Busy 不是恒真）
        var applied = await harness.Maintenance.ApplyChangesAsync(changeSet, GenerousBudget(bytesBefore));
        Assert.AreEqual(FullTextMutationState.Applied, applied.State, applied.Message);
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zznewa"), "锁释放后同一批量必须真的写入");
    }

    // ── I6：真实 create / modify / delete / rename ⇒ 只变对应 path ────────

    [TestMethod]
    public async Task I6_CreateModifyDeleteRename_ChangeOnlyTheTargetPath()
    {
        var a = WriteCorpusFile("a.txt", "zzalphaone\nzzalphatwo");
        var b = WriteCorpusFile("b.txt", "zzbetaone");

        using var harness = NewHarness();
        var build = await harness.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");

        var trace = new StringBuilder();
        var s0 = await InventoryAsync(harness.Maintenance);
        trace.AppendLine($"step0 build        : {Describe(s0)}");
        Assert.AreEqual(2, s0.Count, Describe(s0));
        Assert.AreEqual(2, MustFind(s0, a).DocumentCount, "a.txt 两行 ⇒ 2 个文档");
        Assert.AreEqual(1, MustFind(s0, b).DocumentCount);

        // ① create：新增 c.txt
        var c = WriteCorpusFile("c.txt", "zzgammaone");
        var created = await ApplyAsync(harness, Upsert(c));
        Assert.AreEqual(FullTextMutationState.Applied, created.State, created.Message);
        var s1 = await InventoryAsync(harness.Maintenance);
        trace.AppendLine($"step1 create c.txt : {Describe(s1)}");
        Assert.AreEqual(3, s1.Count, Describe(s1));
        Assert.AreEqual(1, MustFind(s1, c).DocumentCount);
        AssertPathsUnchanged(s0, s1, "create", a, b);
        AssertContentUnchanged(harness.Search, "create", "zzalphaone", "zzbetaone");

        // ② modify：a.txt 从 2 行改写成 1 行
        File.WriteAllText(a, "zzalphathree");
        var modified = await ApplyAsync(harness, Upsert(a));
        Assert.AreEqual(FullTextMutationState.Applied, modified.State, modified.Message);
        var s2 = await InventoryAsync(harness.Maintenance);
        trace.AppendLine($"step2 modify a.txt : {Describe(s2)}");
        Assert.AreEqual(3, s2.Count, Describe(s2));
        Assert.AreEqual(1, MustFind(s2, a).DocumentCount, "改写后 a.txt 恰好 1 个文档（旧 2 行被真正删除）");
        AssertPathsUnchanged(s1, s2, "modify", b, c);
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zzalphathree"), "新内容必须可搜到");
        Assert.AreEqual(0, await CountHitsAsync(harness.Search, "zzalphatwo"), "被改写的旧行不得残留");
        AssertContentUnchanged(harness.Search, "modify", "zzbetaone", "zzgammaone");

        // ③ delete：磁盘上删除 b.txt
        File.Delete(b);
        var deleted = await ApplyAsync(harness, Delete(b));
        Assert.AreEqual(FullTextMutationState.Applied, deleted.State, deleted.Message);
        var s3 = await InventoryAsync(harness.Maintenance);
        trace.AppendLine($"step3 delete b.txt : {Describe(s3)}");
        Assert.AreEqual(2, s3.Count, Describe(s3));
        Assert.IsNull(Find(s3, b), $"b.txt 必须从清册里消失：{Describe(s3)}");
        AssertPathsUnchanged(s2, s3, "delete", a, c);
        Assert.AreEqual(0, await CountHitsAsync(harness.Search, "zzbetaone"), "被删路径的文档必须真的从索引里消失");
        AssertContentUnchanged(harness.Search, "delete", "zzalphathree", "zzgammaone");

        // ④ rename：c.txt → d.txt（watcher 只收到一侧事件时，用 delete 旧 + upsert 新 表达）
        var d = Path.Combine(_corpus, "d.txt");
        File.Move(c, d);
        var renamed = await ApplyAsync(harness, Delete(c), Upsert(d));
        Assert.AreEqual(FullTextMutationState.Applied, renamed.State, renamed.Message);
        var s4 = await InventoryAsync(harness.Maintenance);
        trace.AppendLine($"step4 rename c->d   : {Describe(s4)}");
        Assert.AreEqual(2, s4.Count, Describe(s4));
        Assert.IsNull(Find(s4, c), $"旧路径 c.txt 必须消失：{Describe(s4)}");
        Assert.AreEqual(1, MustFind(s4, d).DocumentCount);
        AssertPathsUnchanged(s3, s4, "rename", a);
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zzgammaone"), "改名后的内容必须能在新路径下搜到");
        AssertContentUnchanged(harness.Search, "rename", "zzalphathree");

        TestContext.WriteLine(trace.ToString());
    }

    // ── I7：CheckpointAdvanced 是产物 ─────────────────────────────────────

    [TestMethod]
    public async Task I7_CheckpointAdvanced_IsProducedByPolicy_AndLyingInputCannotChangeIt()
    {
        var a = WriteCorpusFile("a.txt", "alpha first zzolda");

        using var harness = NewHarness();
        var build = await harness.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");

        // ① 全成功 + 调用方声明「是完整补偿轮次」⇒ 允许推进
        File.WriteAllText(a, "alpha rewritten zznewa");
        var ok = await harness.Maintenance.ApplyChangesAsync(
            NewChangeSet(new[] { Upsert(a) }, requiresCheckpointAdvance: true),
            GenerousBudget(MeasureBytes(ScopeIndexDirectory)));
        Assert.AreEqual(FullTextMutationState.Applied, ok.State, ok.Message);
        Assert.IsTrue(ok.CheckpointAdvanced, "全成功且为完整补偿轮次 ⇒ 允许推进");
        Assert.IsTrue(CheckpointAdvancePolicy.AllowsAdvance(ok), "产物必须与 CheckpointAdvancePolicy 的判定一致");

        // ② 同样的批次但调用方声明「不是完整补偿轮次」⇒ 标志必须为 false（前置条件参与合取）
        File.WriteAllText(a, "alpha rewritten again zznewb");
        var notACompleteRound = await harness.Maintenance.ApplyChangesAsync(
            NewChangeSet(new[] { Upsert(a) }, requiresCheckpointAdvance: false),
            GenerousBudget(MeasureBytes(ScopeIndexDirectory)));
        Assert.AreEqual(FullTextMutationState.Applied, notACompleteRound.State, notACompleteRound.Message);
        Assert.IsFalse(notACompleteRound.CheckpointAdvanced, "调用方前置条件为 false ⇒ 不得推进");

        // ③ ★ 输入说谎：本批确有提取失败，却在变更集里声称「可推进」⇒ 策略否决必须胜过输入
        var extractor = new ScriptedExtractor();
        var pdfPath = WriteCorpusFile(DocPdf, "%PDF-1.4 s3a placeholder bytes");
        extractor.Set(pdfPath, "pdf old line zzoldone");

        using var pdfHarness = NewHarness(extractor);
        Assert.IsTrue((await pdfHarness.Search.BuildIndexAsync(_corpus)).Success);
        extractor.FailingPaths.Add(pdfPath);

        var lying = await pdfHarness.Maintenance.ApplyChangesAsync(
            NewChangeSet(new[] { Upsert(pdfPath) }, requiresCheckpointAdvance: true),
            GenerousBudget(MeasureBytes(ScopeIndexDirectory)));

        Assert.AreEqual(FullTextMutationState.PartiallyApplied, lying.State, lying.Message);
        Assert.IsFalse(
            lying.CheckpointAdvanced,
            "★★ 输入声称「可推进」，但本批有保留项 ⇒ 判定必须仍为「不推进」（策略否决胜过输入）");
        Assert.IsFalse(CheckpointAdvancePolicy.AllowsAdvance(lying));
        Assert.AreEqual(
            CheckpointAdvanceBlockReason.PartiallyApplied,
            CheckpointAdvancePolicy.Decide(lying).Reason,
            "阻止原因必须精确可诊断（零成功 + 有保留项 ⇒ PartiallyApplied）");

        // ④ 反向：把产物字段改成 true，策略仍不得放行 ⇒ 证明 CheckpointAdvanced 是产物、不是判定输入
        Assert.IsFalse(
            CheckpointAdvancePolicy.AllowsAdvance(lying with { CheckpointAdvanced = true }),
            "策略绝不回读 CheckpointAdvanced 字段");
    }

    // ── 门禁收口 ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Guards_RejectWithoutWriting_AndNeverCreateAnInitialIndex()
    {
        var a = WriteCorpusFile("a.txt", "alpha zzalphaone");

        using var harness = NewHarness();
        var indexDirectory = ScopeIndexDirectory;
        Assert.IsFalse(Directory.Exists(indexDirectory), "前置：索引目录尚不存在");

        // ① 索引不存在时枚举清册 ⇒ 空清册 + 绝不创建索引目录（只读）
        Assert.AreEqual(0, (await InventoryAsync(harness.Maintenance)).Count, "没有索引 ⇒ 清册为空");
        Assert.IsFalse(Directory.Exists(indexDirectory), "枚举清册是只读的：不得创建索引目录");

        // ② 索引不存在时局部写 ⇒ Rejected（§3.5：绝不通过局部写入偷偷创建「初始全库索引」）
        var noIndex = await harness.Maintenance.ApplyChangesAsync(
            NewChangeSet(new[] { Upsert(a) }),
            GenerousBudget(0));
        Assert.AreEqual(FullTextMutationState.Rejected, noIndex.State, noIndex.Message);
        Assert.IsFalse(Directory.Exists(indexDirectory), "★ 局部写不得创建索引目录");
        StringAssert.Contains(noIndex.Message, "live 索引目录不存在");
        Assert.IsNull(noIndex.IndexBytesBefore, "索引目录不存在 ⇒ 字节不可测 ⇒ null（不得伪报 0）");
        Assert.IsNull(noIndex.IndexBytesAfter);

        // ③ ProbeIntegrityAsync 本片未实现（属 S3d）⇒ 显式 NotSupportedException，绝不伪装成 Healthy
        await Assert.ThrowsExactlyAsync<NotSupportedException>(
            async () => { await harness.Maintenance.ProbeIntegrityAsync(_corpus); });

        // ④ 空变更集 ⇒ Applied（合法的零变更）：不打开 writer、不 commit、字节不变
        var build = await harness.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(build.Success, build.Error);
        var bytes = MeasureBytes(indexDirectory);

        var empty = await harness.Maintenance.ApplyChangesAsync(
            NewChangeSet(Array.Empty<FullTextFileChange>()),
            GenerousBudget(bytes));
        Assert.AreEqual(FullTextMutationState.Applied, empty.State, empty.Message);
        Assert.AreEqual(0, empty.UpsertAppliedCount + empty.DeleteAppliedCount + empty.RetainedOldCount);
        Assert.IsNull(empty.CommitMilliseconds, "空变更集不得 commit");
        Assert.AreEqual(bytes, empty.IndexBytesAfter, "空变更集不得改变索引字节");
        Assert.IsTrue(empty.CheckpointAdvanced, "空批次是合法的零变更 ⇒ 不阻止推进（调用方前置条件为 true）");

        // ⑤ 超过单批路径上限 ⇒ Rejected（不写入）
        var many = Enumerable.Range(0, 3)
            .Select(i => Upsert(Path.Combine(_corpus, $"m{i}.txt")))
            .ToArray();
        var tooMany = await harness.Maintenance.ApplyChangesAsync(
            NewChangeSet(many),
            new FullTextMutationBudget(MaxIndexBytes: DefaultMaxIndexBytes, LiveIndexBytes: bytes, MaxPaths: 2));

        Assert.AreEqual(FullTextMutationState.Rejected, tooMany.State, tooMany.Message);
        StringAssert.Contains(tooMany.Message, "单批上限");
        Assert.AreEqual(bytes, tooMany.IndexBytesAfter);
        Assert.IsNull(tooMany.CommitMilliseconds);
    }

    // ── 帮助 ──────────────────────────────────────────────────────────────

    /// <summary>查询侧与该 scope 的 live 索引目录（由命名哈希的公开真源独立推导，不复用引擎内部方法）。</summary>
    private string ScopeIndexDirectory => FullTextIndexPaths.ResolveIndexDirectory(_indexRoot, _corpus);

    private static async Task<FullTextMutationResult> ApplyAsync(Harness harness, params FullTextFileChange[] changes)
        => await harness.Maintenance.ApplyChangesAsync(
            harness.NewChangeSet(changes),
            GenerousBudget(MeasureBytes(harness.ScopeIndexDirectory)));

    private static FullTextFileChange Upsert(string fullPath)
        => new(fullPath, FullTextChangeKind.Upsert, LastWriteUtc: null, Length: null, Sources: FullTextChangeSource.Watcher);

    private static FullTextFileChange Delete(string fullPath)
        => new(fullPath, FullTextChangeKind.Delete, LastWriteUtc: null, Length: null, Sources: FullTextChangeSource.Watcher);

    /// <summary>预算充足（1 GiB 集合预算）：只用于「本批必须成功」的对照组与其它不测预算的用例。</summary>
    private static FullTextMutationBudget GenerousBudget(long liveIndexBytes)
        => new(MaxIndexBytes: DefaultMaxIndexBytes, LiveIndexBytes: liveIndexBytes, MaxPaths: DefaultMaxPaths);

    private Harness NewHarness(params IFileContentExtractor[] extractors)
    {
        var parsedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extractor in extractors)
        {
            foreach (var extension in extractor.SupportedExtensions)
                parsedExtensions.Add(extension);
        }

        var options = new FullTextIndexOptions
        {
            IndexRootDirectory = _indexRoot,
            ParsedExtensions = parsedExtensions,
        };

        var all = new List<IFileContentExtractor>(extractors) { new PlainTextExtractor(options) };
        return new Harness(options, _corpus, all.ToArray());
    }

    private string WriteCorpusFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_corpus, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    private static async Task<List<IndexedPathEntry>> InventoryAsync(IFullTextIndexMaintenanceEngine maintenance, string scopeRoot)
    {
        var entries = new List<IndexedPathEntry>();
        await foreach (var entry in maintenance.EnumerateIndexedPathsAsync(scopeRoot))
            entries.Add(entry);

        return entries;
    }

    private Task<List<IndexedPathEntry>> InventoryAsync(IFullTextIndexMaintenanceEngine maintenance)
        => InventoryAsync(maintenance, _corpus);

    private async Task<int> CountHitsAsync(LuceneSearchEngine engine, string query)
    {
        var result = await engine.SearchAsync(query, _corpus, maxResults: 50);
        Assert.IsTrue(result.Success, $"搜索 '{query}' 必须成功：{result.Error}");
        return result.Matches.Count;
    }

    /// <summary>测试侧独立的目录字节度量（仪器交叉校验用；与引擎内部度量互不依赖）。</summary>
    private static long MeasureBytes(string directory)
    {
        Assert.IsTrue(Directory.Exists(directory), $"测试侧度量要求目录存在：{directory}");
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    private static string Describe(IReadOnlyList<IndexedPathEntry> entries)
        => entries.Count == 0
            ? "(empty)"
            : string.Join(
                " | ",
                entries
                    .OrderBy(e => e.NormalizedPath, StringComparer.Ordinal)
                    .Select(e => $"{Path.GetFileName(e.FullPath)}={e.DocumentCount}"));

    private static IndexedPathEntry? Find(IReadOnlyList<IndexedPathEntry> entries, string fullPath)
        => entries.FirstOrDefault(e => string.Equals(e.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

    private static IndexedPathEntry MustFind(IReadOnlyList<IndexedPathEntry> entries, string fullPath)
    {
        var found = Find(entries, fullPath);
        Assert.IsNotNull(found, $"清册里必须存在 {fullPath}（实际：{Describe(entries)}）");
        return found!;
    }

    /// <summary>本测试的语料根对应的变更集（scope key 与供给链同口径）。</summary>
    private FullTextChangeSet NewChangeSet(FullTextFileChange[] changes, bool requiresCheckpointAdvance = true)
        => new(
            "01K-S3A-BATCH",
            _corpus,
            FullTextChangeCoalescer.NormalizeComparisonKey(_corpus),
            changes,
            ScanStartedUtc: DateTimeOffset.UtcNow,
            PreviousWatermarkUtc: null,
            RequiresCheckpointAdvance: requiresCheckpointAdvance);

    /// <summary>逐 path 对比两份清册：路径集合与每个路径的文档数都必须一致（I6 的「其它 path 不变」）。</summary>
    private static void AssertInventoryEqual(
        IReadOnlyList<IndexedPathEntry> before,
        IReadOnlyList<IndexedPathEntry> after,
        string because)
    {
        Assert.AreEqual(
            before.Count,
            after.Count,
            $"{because}：路径数必须一致（before={Describe(before)}；after={Describe(after)}）");

        foreach (var entry in before)
        {
            var other = Find(after, entry.FullPath);
            Assert.IsNotNull(other, $"{because}：路径 {entry.FullPath} 不得消失（after={Describe(after)}）");
            Assert.AreEqual(
                entry.DocumentCount,
                other!.DocumentCount,
                $"{because}：路径 {entry.FullPath} 的文档数必须不变（after={Describe(after)}）");
        }
    }

    /// <summary>指定路径（应为「非目标路径」）在两次清册之间必须逐条不变。</summary>
    private static void AssertPathsUnchanged(
        IReadOnlyList<IndexedPathEntry> before,
        IReadOnlyList<IndexedPathEntry> after,
        string step,
        params string[] paths)
    {
        foreach (var path in paths)
        {
            var oldEntry = MustFind(before, path);
            var newEntry = MustFind(after, path);
            Assert.AreEqual(
                oldEntry.DocumentCount,
                newEntry.DocumentCount,
                $"{step}：非目标路径 {Path.GetFileName(path)} 的文档数必须不变"
                + $"（before={Describe(before)}；after={Describe(after)}）");
            Assert.AreEqual(newEntry.NormalizedPath, FullTextChangeCoalescer.NormalizeComparisonKey(oldEntry.FullPath));
        }
    }

    private async Task AssertContentUnchanged(LuceneSearchEngine engine, string step, params string[] markers)
    {
        foreach (var marker in markers)
        {
            var result = await engine.SearchAsync(marker, _corpus, maxResults: 50);
            Assert.IsTrue(result.Success, $"{step}：搜索 '{marker}' 失败：{result.Error}");
            Assert.AreEqual(1, result.Matches.Count, $"{step}：非目标路径的内容 '{marker}' 必须仍然恰好命中 1 次");
        }
    }

    // ── 测试替身 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 可编排的提取器（<c>.pdf</c>）：按路径给出内容；命中 <see cref="FailingPaths"/> 抛
    /// <see cref="IOException"/>；命中 <see cref="CancelOnRead"/> 先取消令牌再抛
    /// <see cref="OperationCanceledException"/>（把「提取中途取消」变成确定性输入）。
    /// </summary>
    private sealed class ScriptedExtractor : IFileContentExtractor
    {
        private readonly Dictionary<string, string> _contents = new(StringComparer.OrdinalIgnoreCase);

        internal HashSet<string> FailingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal (string Path, CancellationTokenSource Cts)? CancelOnRead { get; set; }

        public IReadOnlySet<string> SupportedExtensions { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf" };

        internal void Set(string path, string content) => _contents[path] = content;

        public Task<string> ExtractAsync(string filePath, CancellationToken ct = default)
        {
            if (CancelOnRead is { } cancel
                && string.Equals(cancel.Path, filePath, StringComparison.OrdinalIgnoreCase))
            {
                cancel.Cts.Cancel();
                throw new OperationCanceledException(cancel.Cts.Token);
            }

            if (FailingPaths.Contains(filePath))
                throw new IOException($"s3a-injected-extract-failure: {Path.GetFileName(filePath)}");

            if (_contents.TryGetValue(filePath, out var content))
                return Task.FromResult(content);

            // 仪器纪律：未编排内容的路径一律掷地有声（否则会静默返回空串、让用例假绿）
            throw new InvalidOperationException($"s3a-extractor: no scripted content for '{filePath}'");
        }
    }

    private sealed class Harness : IDisposable
    {
        internal Harness(FullTextIndexOptions options, string corpusRoot, IFileContentExtractor[] extractors)
        {
            Options = options;
            CorpusRoot = corpusRoot;
            Search = new LuceneSearchEngine(options, new JiebaAnalyzer(), extractors);
            // S3c：真实跨进程租约 + 有界等待上界（取自 MaintenanceOptions 的唯一真源）+ 查询侧 reader 失效接缝。
            // 三者都是**真实实现**：不存在「可选参数 = null 表示不取租约」这类绕过租约的装配。
            Maintenance = new LuceneFullTextIndexMaintenanceEngine(
                Search,
                options,
                new FileSupplyLease(options),
                MaintenanceOptions.DefaultLeaseWaitUpperBound,
                new SearchEngineScopeReaderInvalidation(Search));
        }

        internal FullTextIndexOptions Options { get; }

        internal string CorpusRoot { get; }

        internal string ScopeIndexDirectory => FullTextIndexPaths.ResolveIndexDirectory(Options.IndexRootDirectory, CorpusRoot);

        internal FullTextChangeSet NewChangeSet(FullTextFileChange[] changes, bool requiresCheckpointAdvance = true)
            => new(
                "01K-S3A-BATCH",
                CorpusRoot,
                FullTextChangeCoalescer.NormalizeComparisonKey(CorpusRoot),
                changes,
                ScanStartedUtc: DateTimeOffset.UtcNow,
                PreviousWatermarkUtc: null,
                RequiresCheckpointAdvance: requiresCheckpointAdvance);

        internal LuceneSearchEngine Search { get; }

        internal IFullTextIndexMaintenanceEngine Maintenance { get; }

        public void Dispose() => Search.Dispose();
    }
}
