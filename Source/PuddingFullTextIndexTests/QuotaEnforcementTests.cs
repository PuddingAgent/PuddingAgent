using System.Text;
using System.Text.Json;
using Lucene.Net.Index;
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
/// S3b：**写入期 quota 硬限**（<see cref="QuotaEnforcingDirectory"/> + <see cref="IndexSizeReport"/>）+ 体积增长报告。
/// <para>
/// 依据（唯一权威）：方案 §4.1 硬门禁「若 S3 无法证明 quota 失败后 rollback 保留旧 commit 且不突破预算，
/// 直写方案不得进入 S5」、§4.2「Lucene Directory 输出由 quota wrapper 统计实际增长，在超限前拒绝继续写 /
/// writer rollback 保留上一个 commit / 自动 merge 必须纳入预算」、§4.5「未 commit 的文档对查询不可见」、
/// §6 S3 完成标准⑥「连续局部更新的体积增长有机器可读报告」。
/// </para>
/// <para>
/// 五条不变量 → 本文件逐条钉死（测试名即映射）：
/// <list type="bullet">
/// <item><description>I8 预算充足时与不装 wrapper 逐位一致 ⇒
/// <c>I8_QuotaWrapperUnderGenerousBudget_KeepsContentIdenticalToTheUnwrappedBuildPath</c>
/// （+ S3a 的 I1~I7 用例整体仍全绿）。</description></item>
/// <item><description>I9 写入期超限 ⇒ 明确异常中止 + rollback + 旧 commit 完整 + 磁盘字节 ≤ 预算 + 结果不是 Applied ⇒
/// <c>I9_WriteTimeQuotaBreach_AbortsBatch_RollsBack_AndKeepsIndexWithinBudget</c>（★M3/M4 靶点）。</description></item>
/// <item><description>I10 auto-merge 纳入同一预算、提交后不留无界临时段 ⇒
/// <c>I10_MergeOutputIsCountedAgainstTheSameBudget_AndMergeOverrunLeavesNoResidue</c>（★M3 靶点）。</description></item>
/// <item><description>I11 rollback 后 <c>ListAll</c> 集合与本批开始前逐字一致 ⇒
/// <c>I11_RollbackRestoresExactFileSet_NoResidualSegmentFiles</c>（★M4 靶点）。</description></item>
/// <item><description>I12 连续 N 轮体积增长是机器可读（NDJSON/TSV，可解析可断言）⇒
/// <c>I12_ConsecutiveLocalUpdates_EmitMachineReadableSizeGrowthReport</c>。</description></item>
/// </list>
/// 另有两条机制断言（防止上面的断言空洞）：<c>Quota_CountingCoversEveryWritePrimitive_...</c>（计数覆盖
/// <c>DataOutput</c> 的所有写出原语，不只是 <c>WriteBytes</c>）与 I12 内的「无 writer 会话」对照轮。
/// </para>
/// <para>
/// 所有语料与索引根都落在 <c>%TEMP%</c> 下（<see cref="Initialize"/> 有硬断言，绝不以 <c>D:\data</c> 开头）；
/// 本文件零后台线程、零 git 操作。
/// </para>
/// </summary>
[TestClass]
public sealed class QuotaEnforcementTests
{
    /// <summary>默认集合预算（1 GiB）——「预算充足」的对照组用它。</summary>
    private const long DefaultMaxIndexBytes = 1_073_741_824L;

    /// <summary>默认单批路径数上限（<c>MaintenanceOptions.MaxBatchPaths</c>）。</summary>
    private const int DefaultMaxPaths = 512;

    public TestContext TestContext { get; set; } = null!;

    private string _root = null!;
    private string _corpus = null!;
    private string _indexRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-fts-s3b-" + Guid.NewGuid().ToString("N"));

        Assert.IsTrue(
            _root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            $"测试根必须在 %TEMP% 下，实际 {_root}");
        Assert.IsFalse(
            _root.StartsWith(@"D:\data", StringComparison.OrdinalIgnoreCase),
            $"测试根绝不允许落在生产数据根下，实际 {_root}");

        _corpus = Path.Combine(_root, "corpus");
        _indexRoot = Path.Combine(_root, "index");
        Directory.CreateDirectory(_corpus);
        Directory.CreateDirectory(_indexRoot);
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

    // ── I8：预算充足时与「不装 wrapper」的内容逐位一致 ────────────────────

    [TestMethod]
    public async Task I8_QuotaWrapperUnderGenerousBudget_KeepsContentIdenticalToTheUnwrappedBuildPath()
    {
        var a = WriteCorpusFile("a.txt", "alpha first line zzolda\nzzalpha second");
        WriteCorpusFile("b.txt", "beta first zzbetab");
        WriteCorpusFile("c.txt", "gamma zzgammaone");

        // 路径 1：局部写内核（**装了配额包装层**）—— 先全量建索引，再走一个真实批次（改写 a.txt + 新增 d.txt）
        using var batched = NewHarness("batched");
        var batchedBuild = await batched.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(batchedBuild.Success, $"初始全量构建必须成功：{batchedBuild.Error}");
        var batchedInventoryAfterBuild = await InventoryAsync(batched.Maintenance);
        var batchedDocsAfterBuild = ReadLiveDocuments(batched.IndexDirectory);
        Assert.AreEqual(3, batchedInventoryAfterBuild.Count, "初始清册必须恰好 3 个路径");

        File.WriteAllText(a, "alpha rewritten zznewa\nzzalpha rewritten second");
        var d = WriteCorpusFile("d.txt", "delta zzd");
        var (applied, report) = await ApplyWithReportAsync(
            batched,
            GenerousBudget(MeasureBytes(batched.IndexDirectory)),
            Upsert(a),
            Upsert(d));

        Assert.AreEqual(FullTextMutationState.Applied, applied.State, applied.Message);
        Assert.IsTrue(applied.CheckpointAdvanced, "全成功的完整轮次必须允许推进 checkpoint");

        // ★ 仪器纪律：包装层必须**真的在计数**（否则下面的「内容一致」是空洞的）
        Assert.IsTrue(report.WriterSessionOpened, "本批必须真的打开过 writer 会话");
        Assert.IsTrue(report.BytesWrittenByWriter > 0, $"包装层必须真的数到了写出字节：{report.ToJsonLine()}");
        Assert.IsTrue(report.SegmentFilesAdded >= 1, $"本批至少写了一个新段文件：{report.ToJsonLine()}");
        Assert.IsFalse(report.QuotaExceeded, "预算充足（1 GiB）不得触发配额");
        Assert.IsTrue(report.WithinBudget, $"预算充足必须落在预算内：{report.ToJsonLine()}");
        Assert.AreEqual(applied.IndexBytesAfter! - applied.IndexBytesBefore!.Value, report.Delta!.Value, "Delta 必须等于实测差值");

        // 路径 2：不装 wrapper 的**全量构建路径**（同一个语料根的最终状态）
        using var fullBuild = NewHarness("fullbuild");
        var build = await fullBuild.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(build.Success, $"对照全量构建必须成功：{build.Error}");
        Assert.AreEqual(4, build.IndexedFileCount, "对照全量构建必须索引到 4 个文件");

        // 内容逐位一致：每个存活文档的 (path, line_text) 多重集必须完全相同
        var batchedDocs = ReadLiveDocuments(batched.IndexDirectory);
        var fullDocs = ReadLiveDocuments(fullBuild.IndexDirectory);
        Assert.AreEqual(
            fullDocs.Count,
            batchedDocs.Count,
            $"两条写入路径的存活文档数必须一致（batched={batchedDocs.Count}，full={fullDocs.Count}）");
        CollectionAssert.AreEqual(fullDocs.ToArray(), batchedDocs.ToArray(), "两条写入路径的 (path, line_text) 必须逐条一致");

        // 清册（路径 + 文档数）也必须一致
        var batchedInventory = await InventoryAsync(batched.Maintenance);
        Assert.AreEqual(4, batchedInventory.Count, $"批次后清册必须是 4 个路径：{Describe(batchedInventory)}");
        Assert.AreEqual(2, MustFind(batchedInventory, a).DocumentCount, "改写后 a.txt 恰好 2 个文档");
        Assert.AreEqual(1, MustFind(batchedInventory, d).DocumentCount);
        Assert.IsTrue(
            batchedDocsAfterBuild.Count < batchedDocs.Count,
            "本批必须真的改变了索引（否则上面的比对是空洞的）");
    }

    // ── I9：写入期超限 ⇒ 明确异常中止 + rollback + 旧 commit 完整 + 不突破预算 ──

    [TestMethod]
    public async Task I9_WriteTimeQuotaBreach_AbortsBatch_RollsBack_AndKeepsIndexWithinBudget()
    {
        var a = WriteCorpusFile("a.txt", "alpha first zzolda");
        using var harness = NewHarness("i9");
        var build = await harness.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");

        var indexDirectory = harness.IndexDirectory;
        var inventoryBefore = await InventoryAsync(harness.Maintenance);
        var bytesBefore = MeasureBytes(indexDirectory);
        var filesBefore = ListIndexFiles(indexDirectory);
        Assert.AreEqual(1, inventoryBefore.Count, $"初始清册必须恰好 1 个路径：{Describe(inventoryBefore)}");

        // 把 a.txt 换成「源文件本身不大、但写进 Lucene 的输出必然更大」的内容：
        // 每行 6 个唯一 token ⇒ 词表 / 倒排表 / 每文档存储开销全部不可压缩地膨胀。
        File.WriteAllText(a, BigUniqueContent(lineCount: 5000, marker: "zzbigmarker"));
        var statBytes = new FileInfo(a).Length;
        Assert.IsTrue(statBytes > 200_000, $"语料必须足够大才有稳定的比值，实际 {statBytes} 字节");

        // 预算 = live + 本批 stat 字节 ⇒ 写前预检**恰好通过**（越界只可能发生在写入期）
        var budget = new FullTextMutationBudget(
            MaxIndexBytes: bytesBefore + statBytes,
            LiveIndexBytes: bytesBefore,
            MaxPaths: DefaultMaxPaths);
        Assert.IsTrue(
            SupplyBudgetCalculator.Fits(bytesBefore, statBytes, budget.MaxIndexBytes),
            "本用例的前提是写前预检必须通过（否则红的是预检那道门，不是写入期配额）");

        var (result, report) = await ApplyWithReportAsync(harness, budget, Upsert(a));

        // ① 结果是「明确中止」，绝不是 Applied
        Assert.AreNotEqual(FullTextMutationState.Applied, result.State, $"超限不得声称成功：{result.Message}");
        Assert.AreEqual(FullTextMutationState.Rejected, result.State, $"超限必须走拒绝终态：{result.Message}");
        Assert.IsNull(result.CommitMilliseconds, "超限 ⇒ 绝不 commit");
        Assert.IsFalse(result.CheckpointAdvanced, "超限 ⇒ checkpoint 不推进");

        // ② 中止来自**写入期配额**（专用异常），不是写前预检
        StringAssert.Contains(result.Message, "写入期配额超限");
        StringAssert.Contains(result.Message, nameof(IndexWriteQuotaExceededException));
        Assert.IsFalse(
            result.Message!.Contains("本批待写文件"),
            $"必须是写入期配额拒绝，而不是写前预检拒绝：{result.Message}");

        // ③ 报告：包装层确实越界（而不是「压根没写」）
        Assert.IsTrue(report.WriterSessionOpened, "本批必须打开过 writer 会话（预检已通过）");
        Assert.IsTrue(report.QuotaExceeded, $"包装层必须记录越界事实：{report.ToJsonLine()}");
        Assert.IsTrue(report.OvershootBytes > 0, $"越界量必须为正：{report.ToJsonLine()}");
        Assert.IsTrue(
            report.BytesWrittenByWriter > report.AllowedGrowthBytes,
            $"写出字节必须真的越过允许增长：{report.ToJsonLine()}");
        Assert.IsTrue(report.FilesCreated > 0, "越界前必须已经写出过文件（否则谈不上「写入期」）");
        Assert.IsTrue(report.RolledBack, "超限路径必须走 rollback");

        // ④ 字节级证据：磁盘索引目录总字节 ≤ 预算，且回到本批开始前
        var bytesAfter = MeasureBytes(indexDirectory);
        Assert.IsTrue(
            result.IndexBytesAfter <= budget.MaxIndexBytes,
            $"IndexBytesAfter({result.IndexBytesAfter}) 必须 ≤ 预算({budget.MaxIndexBytes})");
        Assert.IsTrue(bytesAfter <= budget.MaxIndexBytes, $"独立度量 {bytesAfter} 必须 ≤ 预算 {budget.MaxIndexBytes}");
        Assert.AreEqual(bytesBefore, result.IndexBytesBefore, "本批开始前的实测字节必须如实上报");
        Assert.AreEqual(bytesBefore, result.IndexBytesAfter, "rollback ⇒ 索引字节必须回到上一个 commit");
        Assert.AreEqual(bytesBefore, bytesAfter, "独立度量必须与引擎度量一致");
        // ④ 两个维度一起断言：越界事件确实发生（QuotaExceeded）且回滚后磁盘状态仍在预算内（WithinBudget）
        Assert.IsTrue(report.QuotaExceeded, $"写入期必须真的越过配额：{report.ToJsonLine()}");
        Assert.IsTrue(report.WithinBudget, $"回滚后磁盘状态必须仍在预算内：{report.ToJsonLine()}");
        Assert.AreEqual(0, report.Delta, "回滚后净增长必须为 0");

        // ⑤ 旧 commit 完整可查（双向核对：旧内容在、被中止的新内容绝不可见）
        AssertInventoryEqual(inventoryBefore, await InventoryAsync(harness.Maintenance), "回滚 ⇒ 清册不变");
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zzolda"), "旧内容必须仍然可查");
        Assert.AreEqual(0, await CountHitsAsync(harness.Search, "zzbigmarker"), "被中止的内容绝不可见");
        AssertNoUnreferencedFiles(indexDirectory);

        // ⑥ 证据落盘（写进测试输出，使字节级结论可从 TRX 复核，而不是只存在于本次对话里）
        TestContext.WriteLine($"I9_QUOTA_REPORT={report.ToJsonLine()}");
        TestContext.WriteLine(
            $"I9_BYTES budget={budget.MaxIndexBytes} statBytes={statBytes} before={bytesBefore} "
            + $"engineAfter={result.IndexBytesAfter} measuredAfter={bytesAfter} "
            + $"allowedGrowth={report.AllowedGrowthBytes} writtenByWriter={report.BytesWrittenByWriter} "
            + $"overshoot={report.OvershootBytes} filesCreated={report.FilesCreated} "
            + $"filesDeleted={report.FilesDeleted} removedByRollback={report.FilesRemovedByRollback}");
        TestContext.WriteLine($"I9_FILES_BEFORE={string.Join(',', filesBefore)}");
        TestContext.WriteLine($"I9_FILES_AFTER={string.Join(',', ListIndexFiles(indexDirectory))}");
        TestContext.WriteLine(
            $"I9_OLD_COMMIT hits_zzolda={await CountHitsAsync(harness.Search, "zzolda")} "
            + $"hits_zzbigmarker={await CountHitsAsync(harness.Search, "zzbigmarker")} "
            + $"inventory={Describe(await InventoryAsync(harness.Maintenance))}");

        // ⑦ 对照组：同一批量、充足预算 ⇒ 真的能写进去（证明上面的中止不是「这条代码路径写不进去」）
        var (control, controlReport) = await ApplyWithReportAsync(
            harness,
            GenerousBudget(MeasureBytes(indexDirectory)),
            Upsert(a));
        Assert.AreEqual(FullTextMutationState.Applied, control.State, control.Message);
        Assert.IsTrue(controlReport.WithinBudget, controlReport.ToJsonLine());
        Assert.IsFalse(controlReport.QuotaExceeded, controlReport.ToJsonLine());
        Assert.IsTrue(MeasureBytes(indexDirectory) > bytesBefore, "对照组装载后索引必须真的增长");
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zzbigmarker"), "对照组的同一内容必须可搜到");
    }

    // ── I10：auto-merge 写出的新段也计入同一预算，且超限不留残余 ────────────

    [TestMethod]
    public async Task I10_MergeOutputIsCountedAgainstTheSameBudget_AndMergeOverrunLeavesNoResidue()
    {
        // 直接对包装层做真实合并：不经过引擎的批次（不依赖合并策略的触发时机），确定性最高。
        var indexDirectory = Path.Combine(_root, "merge-index");
        Directory.CreateDirectory(indexDirectory);
        var analyzer = new JiebaAnalyzer();
        var version = LuceneVersion.LUCENE_48;

        // ① 先造出「多个段」的索引（每轮一个 commit ⇒ 一个段；不触发合并，因为 5 < segmentsPerTier=10）
        var seedDirectory = new QuotaEnforcingDirectory(FSDirectory.Open(indexDirectory), DefaultMaxIndexBytes, DefaultMaxIndexBytes);
        using (var seedWriter = new IndexWriter(
                   seedDirectory,
                   new IndexWriterConfig(version, analyzer)
                   {
                       OpenMode = OpenMode.CREATE_OR_APPEND,
                       MergeScheduler = new SerialMergeScheduler(),
                   }))
        {
            for (var i = 0; i < 5; i++)
            {
                LuceneSearchEngine.AddDocument(
                    seedWriter,
                    Path.Combine(_corpus, $"seg{i}.txt"),
                    BigUniqueContent(lineCount: 200, marker: $"zzseg{i}"));
                seedWriter.Commit();
            }
        }

        var filesBeforeMerge = ListIndexFiles(indexDirectory);
        var bytesBeforeMerge = MeasureBytes(indexDirectory);

        Assert.IsTrue(bytesBeforeMerge > 20_000, $"前置：总量太小就没有可观察的合并输出，实际 {bytesBeforeMerge} 字节");
        var segmentsBefore = CountSegments(indexDirectory);
        Assert.IsTrue(segmentsBefore >= 3, $"前置条件：必须真的存在多个段才能观察合并，实际 {segmentsBefore} 个");
        TestContext.WriteLine($"merge 前置：segments={segmentsBefore} files={filesBeforeMerge.Length} bytes={bytesBeforeMerge}");

        // ② 同一目录换上一个「自校准的极小允许增长」包装层，只做合并 ⇒ 合并写出的新段必然越界。
        // 自校准依据：把 N 个段合成 1 个段写出的数据量与原总量同量级（还因为超大段不复合而更大）⇒
        // 允许增长取原总量的一半即为确定越界。
        var tinyAllowed = bytesBeforeMerge / 2;
        Assert.IsTrue(tinyAllowed > 0, "允许增长必须为正");
        var mergeDirectory = new QuotaEnforcingDirectory(FSDirectory.Open(indexDirectory), DefaultMaxIndexBytes, tinyAllowed);
        IndexWriter? mergeWriter = null;
        Exception? thrown = null;
        try
        {
            mergeWriter = new IndexWriter(
                mergeDirectory,
                new IndexWriterConfig(version, analyzer)
                {
                    OpenMode = OpenMode.CREATE_OR_APPEND,
                    MergeScheduler = new SerialMergeScheduler(),
                });

            // 只合并、不写文档：任何新增字节都只能来自**合并输出**
            mergeWriter.ForceMerge(1);

            // 若合并没越界（例如合并策略认为无段可合），这里必须显式失败而不是让用例假绿
            Assert.Fail(
                $"合并必须越界（允许增长 {tinyAllowed} 字节）：{mergeDirectory.BytesWritten} 字节写出、"
                + $"创建 {mergeDirectory.SegmentFileCreatedCount} 个段文件");
        }
        catch (Exception ex) when (ex is not AssertFailedException)
        {
            thrown = ex;
        }
        finally
        {
            if (mergeWriter is not null)
            {
                // rollback 自身即关闭 + 删除本批新建的（合并）段文件
                mergeWriter.Rollback();
            }
        }

        var filesAfterMerge = ListIndexFiles(indexDirectory);
        var bytesAfterMerge = MeasureBytes(indexDirectory);

        // ③ 断言：合并输出被计数 + 越界被明确中止 + 回滚不留残余
        Assert.IsNotNull(thrown, "合并越过配额必须抛出（不能被静默吞掉）");
        Assert.IsNotNull(mergeDirectory.Violation, $"必须记录越界事实（异常可能是 {thrown.GetType().Name}）");
        Assert.IsTrue(
            mergeDirectory.BytesWritten > tinyAllowed,
            $"合并写出的字节必须被计入：written={mergeDirectory.BytesWritten} allowed={tinyAllowed}");
        Assert.IsTrue(
            mergeDirectory.Violation!.FileName.StartsWith('_'),
            $"越界必须发生在合并写出的段文件上，实际 '{mergeDirectory.Violation.FileName}'");
        Assert.IsTrue(mergeDirectory.SegmentFileCreatedCount >= 1, "合并必须写出过段文件");
        Assert.IsTrue(mergeDirectory.SegmentFileDeletedCount >= 1, "回滚必须清掉合并写出的段文件");

        CollectionAssert.AreEqual(
            filesBeforeMerge,
            filesAfterMerge,
            $"合并越界 + rollback 后文件集合必须与本批开始前逐字一致（before={string.Join(',', filesBeforeMerge)}；after={string.Join(',', filesAfterMerge)}）");
        Assert.AreEqual(bytesBeforeMerge, bytesAfterMerge, "合并越界 + rollback 后目录字节必须回到本批开始前");
        Assert.AreEqual(segmentsBefore, CountSegments(indexDirectory), "rollback 不得改变段数");

        // ④ 且没有「不被当前 commit 引用的文件」残留（§4.2「不能在提交后无界产生临时段」）
        AssertNoUnreferencedFiles(indexDirectory);

        TestContext.WriteLine(
            $"merge 越界：written={mergeDirectory.BytesWritten} allowed={tinyAllowed} "
            + $"fileName={mergeDirectory.Violation!.FileName} filesCreated={mergeDirectory.CreatedFileCount} "
            + $"filesDeleted={mergeDirectory.DeletedFileCount} thrown={thrown.GetType().Name}");
    }

    // ── I11：rollback 后 ListAll 集合与本批开始前一致（无残余段文件）────────

    [TestMethod]
    public async Task I11_RollbackRestoresExactFileSet_NoResidualSegmentFiles()
    {
        var a = WriteCorpusFile("a.txt", "alpha zzolda");
        using var harness = NewHarness("i11");
        var build = await harness.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");

        var indexDirectory = harness.IndexDirectory;

        // 先用几轮真实批次把索引造成「多段」形态（这样「残余段文件」才会显形）
        for (var round = 0; round < 4; round++)
        {
            File.WriteAllText(a, $"alpha round {round} zzround{round}");
            var (applied, _) = await ApplyWithReportAsync(
                harness,
                GenerousBudget(MeasureBytes(indexDirectory)),
                Upsert(a));
            Assert.AreEqual(FullTextMutationState.Applied, applied.State, applied.Message);
        }

        var filesBefore = ListIndexFiles(indexDirectory);
        var bytesBefore = MeasureBytes(indexDirectory);
        var inventoryBefore = await InventoryAsync(harness.Maintenance);
        var segmentsBefore = CountSegments(indexDirectory);
        Assert.IsTrue(filesBefore.Length >= 3, $"前置：多段索引才对「残余」敏感，实际文件 {string.Join(',', filesBefore)}");

        // 写入期触发配额：预算 = live + 本批 stat 字节 ⇒ 写前预检恰好通过（与 I9 同一构造）
        File.WriteAllText(a, BigUniqueContent(lineCount: 4000, marker: "zzbigmarker11"));
        var statBytes = new FileInfo(a).Length;
        var budget = new FullTextMutationBudget(
            MaxIndexBytes: bytesBefore + statBytes,
            LiveIndexBytes: bytesBefore,
            MaxPaths: DefaultMaxPaths);
        Assert.IsTrue(
            SupplyBudgetCalculator.Fits(bytesBefore, statBytes, budget.MaxIndexBytes),
            "前提：写前预检必须通过（否则测的是预检那道门）");

        var (result, report) = await ApplyWithReportAsync(harness, budget, Upsert(a));

        Assert.AreEqual(FullTextMutationState.Rejected, result.State, result.Message);
        Assert.IsTrue(report.QuotaExceeded, report.ToJsonLine());
        Assert.IsTrue(report.RolledBack, report.ToJsonLine());
        Assert.IsTrue(
            report.FilesRemovedByRollback > 0,
            $"回滚必须真的清掉了本批新建的文件（否则「无残余」这条断言是空洞的）：{report.ToJsonLine()}");

        var filesAfter = ListIndexFiles(indexDirectory);
        CollectionAssert.AreEqual(
            filesBefore,
            filesAfter,
            $"rollback 后 ListAll 集合必须逐字一致（before={string.Join(',', filesBefore)}；after={string.Join(',', filesAfter)}）");
        Assert.AreEqual(bytesBefore, MeasureBytes(indexDirectory), "rollback 后目录字节必须逐位回到本批开始前");
        Assert.AreEqual(segmentsBefore, CountSegments(indexDirectory), "rollback 不得改变段数");
        AssertInventoryEqual(inventoryBefore, await InventoryAsync(harness.Maintenance), "rollback ⇒ 清册不变");
        Assert.AreEqual(1, await CountHitsAsync(harness.Search, "zzround3"), "上一个 commit 的内容必须完整可查");
        Assert.AreEqual(0, await CountHitsAsync(harness.Search, "zzbigmarker11"), "被中止的内容绝不可见");

        AssertNoUnreferencedFiles(indexDirectory);

        TestContext.WriteLine($"I11_QUOTA_REPORT={report.ToJsonLine()}");
        TestContext.WriteLine($"I11_BYTES budget={budget.MaxIndexBytes} before={bytesBefore} after={MeasureBytes(indexDirectory)}");
        TestContext.WriteLine($"I11_FILES_BEFORE={string.Join(',', filesBefore)}");
        TestContext.WriteLine($"I11_FILES_AFTER={string.Join(',', filesAfter)}");
        TestContext.WriteLine($"I11_SEGMENTS before={segmentsBefore} after={CountSegments(indexDirectory)}");
    }

    // ── I12：连续 N 轮局部更新的体积增长是机器可读报告 ─────────────────────

    [TestMethod]
    public async Task I12_ConsecutiveLocalUpdates_EmitMachineReadableSizeGrowthReport()
    {
        var a = WriteCorpusFile("a.txt", "round seed zzmark");
        using var harness = NewHarness("i12");
        var build = await harness.Search.BuildIndexAsync(_corpus);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");

        var indexDirectory = harness.IndexDirectory;
        var bytesAfterBuild = MeasureBytes(indexDirectory);
        const long headroom = 8L * 1024 * 1024;
        var maxIndexBytes = bytesAfterBuild + headroom;

        var reports = new List<IndexSizeReport>();
        var results = new List<FullTextMutationResult>();

        for (var round = 1; round <= 10; round++)
        {
            File.WriteAllText(a, BigUniqueContent(lineCount: round * 400, marker: $"zzmark{round:D2}"));
            var live = MeasureBytes(indexDirectory);
            var budget = new FullTextMutationBudget(MaxIndexBytes: maxIndexBytes, LiveIndexBytes: live, MaxPaths: DefaultMaxPaths);

            var (result, report) = await ApplyWithReportAsync(harness, budget, $"01K-S3B-R{round:D2}", Upsert(a));
            results.Add(result);
            reports.Add(report);

            Assert.AreEqual(FullTextMutationState.Applied, result.State, $"第 {round} 轮必须成功：{result.Message}");
        }

        // ⑨ 无 writer 会话的对照轮：报告必须能区分「压根没写」与「写了但没超限」
        var liveBeforeNoOp = MeasureBytes(indexDirectory);
        var (noOpResult, noOpReport) = await ApplyWithReportAsync(
            harness,
            new FullTextMutationBudget(maxIndexBytes, liveBeforeNoOp, DefaultMaxPaths),
            "01K-S3B-R11");
        Assert.AreEqual(FullTextMutationState.Applied, noOpResult.State, noOpResult.Message);
        Assert.IsFalse(noOpReport.WriterSessionOpened, $"空变更集不得打开 writer 会话：{noOpReport.ToJsonLine()}");
        Assert.AreEqual(0, noOpReport.SegmentFilesAdded);
        Assert.AreEqual(0, noOpReport.BytesWrittenByWriter);
        Assert.AreEqual(0, noOpReport.Delta);

        // ⑩ 逐轮断言：增长量级符合预期 + 落在预算内 + 报告字段自洽
        for (var i = 0; i < reports.Count; i++)
        {
            var report = reports[i];
            var line = report.ToJsonLine();

            Assert.AreEqual($"01K-S3B-R{i + 1:D2}", report.BatchId, line);
            Assert.AreEqual(
                FullTextChangeCoalescer.NormalizeComparisonKey(_corpus),
                report.ScopeKey,
                line);
            Assert.AreEqual(FullTextMutationState.Applied.ToString(), report.Outcome, line);
            Assert.AreEqual(maxIndexBytes, report.BudgetBytes, line);
            Assert.IsFalse(report.QuotaExceeded, line);
            Assert.IsTrue(report.WithinBudget, line);
            Assert.IsTrue(report.WriterSessionOpened, line);
            Assert.IsTrue(report.Delta > 0, $"每轮都改写成更大的语料 ⇒ 净增长必须为正：{line}");
            Assert.AreEqual(report.IndexBytesBefore + report.Delta, report.IndexBytesAfter, line);
            Assert.IsTrue(report.IndexBytesAfter <= maxIndexBytes, line);
            Assert.IsTrue(report.SegmentFilesAdded >= 1, $"每轮都必须至少写出一个新段：{line}");
            Assert.IsNotNull(report.CommitMilliseconds, line);
            Assert.IsTrue(report.CommitMilliseconds >= 0, line);
        }

        // ⑪ 机器可读：NDJSON / TSV 双形式往返必须逐字段相同（能解析、能断言）
        var jsonLines = reports.Select(r => r.ToJsonLine()).ToArray();
        var tsvLines = new[] { IndexSizeReport.TsvHeader }.Concat(reports.Select(r => r.ToTsvRow())).ToArray();

        for (var i = 0; i < reports.Count; i++)
        {
            var parsedJson = IndexSizeReport.FromJsonLine(jsonLines[i]);
            Assert.IsNotNull(parsedJson, $"NDJSON 第 {i + 1} 行必须可解析：{jsonLines[i]}");
            Assert.AreEqual(reports[i], parsedJson, $"NDJSON 往返必须逐字段相同：{jsonLines[i]}");

            var parsedTsv = IndexSizeReport.FromTsvRow(tsvLines[i + 1]);
            Assert.AreEqual(reports[i], parsedTsv, $"TSV 往返必须逐字段相同：{tsvLines[i + 1]}");

            Assert.AreEqual(20, tsvLines[i + 1].Split('\t').Length, "TSV 列数必须与表头一致");
            Assert.AreEqual(20, IndexSizeReport.TsvHeader.Split('\t').Length);
        }

        // ① 逐行用 BCL 的 JsonDocument 独立核对（不依赖被测类型的反序列化器）
        foreach (var jsonLine in jsonLines)
        {
            using var doc = JsonDocument.Parse(jsonLine);
            Assert.AreEqual(JsonValueKind.Object, doc.RootElement.ValueKind, jsonLine);
        }

        // ⑫ 落盘：真实产物（机器可读），路径同时写进测试输出便于归档
        var artifactDirectory = Path.Combine(Path.GetTempPath(), "pudding-fts-s3b");
        Directory.CreateDirectory(artifactDirectory);
        var ndjsonPath = Path.Combine(artifactDirectory, "s3b-size-growth.ndjson");
        var tsvPath = Path.Combine(artifactDirectory, "s3b-size-growth.tsv");
        File.WriteAllLines(ndjsonPath, jsonLines);
        File.WriteAllLines(tsvPath, tsvLines);

        TestContext.WriteLine($"S3B_SIZE_REPORT_NDJSON={ndjsonPath}");
        TestContext.WriteLine($"S3B_SIZE_REPORT_TSV={tsvPath}");
        TestContext.WriteLine(IndexSizeReport.TsvHeader);
        foreach (var row in tsvLines.Skip(1))
            TestContext.WriteLine(row);

        // 磁盘上的产物必须能被独立解析（不靠被测类型）
        var linesOnDisk = File.ReadAllLines(ndjsonPath);
        Assert.AreEqual(reports.Count, linesOnDisk.Length, "落盘的 NDJSON 行数必须与轮数一致");
        for (var i = 0; i < linesOnDisk.Length; i++)
        {
            using var doc = JsonDocument.Parse(linesOnDisk[i]);
            var root = doc.RootElement;
            Assert.AreEqual(
                reports[i].BatchId,
                root.GetProperty("BatchId").GetString(),
                $"落盘 JSON 的 BatchId 必须一致：{linesOnDisk[i]}");
            Assert.AreEqual(
                reports[i].IndexBytesAfter!.Value,
                root.GetProperty("IndexBytesAfter").GetInt64(),
                $"落盘 JSON 的 IndexBytesAfter 必须一致：{linesOnDisk[i]}");
            Assert.AreEqual(
                reports[i].Delta!.Value,
                root.GetProperty("Delta").GetInt64(),
                $"落盘 JSON 的 Delta 必须一致：{linesOnDisk[i]}");
            Assert.AreEqual(reports[i].BudgetBytes, root.GetProperty("BudgetBytes").GetInt64(), linesOnDisk[i]);
            Assert.IsTrue(root.GetProperty("WithinBudget").GetBoolean(), linesOnDisk[i]);
            Assert.IsTrue(root.GetProperty("QuotaExceeded").GetBoolean() == false, linesOnDisk[i]);
            Assert.IsTrue(root.TryGetProperty("CommitMilliseconds", out _), linesOnDisk[i]);
            Assert.IsTrue(root.TryGetProperty("SegmentFilesAdded", out _), linesOnDisk[i]);
            Assert.IsTrue(root.TryGetProperty("SegmentFilesRemoved", out _), linesOnDisk[i]);
            Assert.AreEqual(reports[i].ScopeKey, root.GetProperty("ScopeKey").GetString(), linesOnDisk[i]);
        }

        TestContext.WriteLine(
            $"10 轮体积增长：{bytesAfterBuild} → {MeasureBytes(indexDirectory)} 字节（预算 {maxIndexBytes}）");
    }

    // ── 机制：计数覆盖 DataOutput 的**所有**写出原语（不只是 WriteBytes）────────

    [TestMethod]
    public void Quota_CountingCoversEveryWritePrimitive_NotJustWriteBytes()
    {
        using var ram = new RAMDirectory();
        var quota = new QuotaEnforcingDirectory(ram, budgetBytes: 1_048_576, allowedGrowthBytes: 1_048_576);

        var output = quota.CreateOutput("_0.tst", IOContext.DEFAULT);
        output.WriteByte(0x01);              // 1
        output.WriteBytes(new byte[10], 0, 10); // 10
        output.WriteInt32(1234);             // 4（经 WriteByte 实现）
        output.WriteInt64(5678L);            // 8（经 WriteByte 实现）
        output.WriteString("abc");           // WriteVInt32(1) + 3 = 4
        output.Dispose();

        Assert.AreEqual(27, quota.BytesWritten, "计数字节必须覆盖 WriteByte / WriteBytes / WriteInt32 / WriteInt64 / WriteString");

        // 超限：先记账、后写盘 ⇒ 越界的那次写出不发生
        var strict = new QuotaEnforcingDirectory(ram, budgetBytes: 1_048_576, allowedGrowthBytes: 5);
        var strictOutput = strict.CreateOutput("_1.tst", IOContext.DEFAULT);
        strictOutput.WriteBytes(new byte[5], 0, 5);
        Assert.AreEqual(5, strict.BytesWritten, "刚好等于允许值时不得越界");
        Assert.IsFalse(strict.QuotaExceeded);

        var ex = Assert.ThrowsExactly<IndexWriteQuotaExceededException>(
            () => strictOutput.WriteByte(0x02));
        Assert.AreEqual(6, ex.BytesWritten, "越界时必须报出「已写出 + 本次」的累计值");
        Assert.AreEqual(5, ex.AllowedGrowthBytes);
        Assert.AreEqual(1, ex.OvershootBytes);
        Assert.IsTrue(strict.QuotaExceeded, "越界事实必须被记录（与异常如何被包装无关）");

        // 越界之后再写任何字节都必须继续被拒绝（同一个已记录的越界事实）
        Assert.ThrowsExactly<IndexWriteQuotaExceededException>(() => strictOutput.WriteByte(0x03));
        Assert.AreEqual(7, strict.BytesWritten);

        // 参数校验：负数允许增长一律拒绝（上游量错了，不猜测）
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new QuotaEnforcingDirectory(ram, budgetBytes: 100, allowedGrowthBytes: -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new QuotaEnforcingDirectory(ram, budgetBytes: 0, allowedGrowthBytes: 0));
    }

    // ── 帮助 ──────────────────────────────────────────────────────────────

    private static async Task<(FullTextMutationResult Result, IndexSizeReport Report)> ApplyWithReportAsync(
        Harness harness,
        FullTextMutationBudget budget,
        params FullTextFileChange[] changes)
        => await ApplyWithReportAsync(harness, budget, "01K-S3B-BATCH", changes);

    private static async Task<(FullTextMutationResult Result, IndexSizeReport Report)> ApplyWithReportAsync(
        Harness harness,
        FullTextMutationBudget budget,
        string batchId,
        params FullTextFileChange[] changes)
        => await harness.Maintenance.ApplyChangesWithReportAsync(harness.NewChangeSet(changes, batchId), budget);

    private static FullTextFileChange Upsert(string fullPath)
        => new(fullPath, FullTextChangeKind.Upsert, LastWriteUtc: null, Length: null, Sources: FullTextChangeSource.Watcher);

    private static FullTextMutationBudget GenerousBudget(long liveIndexBytes)
        => new(MaxIndexBytes: DefaultMaxIndexBytes, LiveIndexBytes: liveIndexBytes, MaxPaths: DefaultMaxPaths);

    /// <summary>
    /// 确定性「高熵」内容：每行 6 个唯一 token（xorshift 生成）⇒ 词表与倒排表不可压缩地膨胀，
    /// 从而**稳定地**保证「Lucene 写出的字节 &gt; 源文件 stat 字节」（写入期配额才可能在预检通过后触发）。
    /// </summary>
    private static string BigUniqueContent(int lineCount, string marker)
    {
        var sb = new StringBuilder();
        sb.Append(marker).Append(' ').Append("marker alpha beta gamma\n");

        uint state = 0x9E3779B9u;
        for (var i = 0; i < lineCount; i++)
        {
            sb.Append('l').Append(i).Append(' ');
            for (var t = 0; t < 6; t++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                sb.Append('t').Append((state & 0xFFFFFF).ToString("x6")).Append(' ');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    private Harness NewHarness(string indexRootName, params IFileContentExtractor[] extractors)
    {
        var parsedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extractor in extractors)
        {
            foreach (var extension in extractor.SupportedExtensions)
                parsedExtensions.Add(extension);
        }

        var indexRoot = Path.Combine(_root, indexRootName);
        Directory.CreateDirectory(indexRoot);

        var options = new FullTextIndexOptions
        {
            IndexRootDirectory = indexRoot,
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

    /// <summary>测试侧独立的目录字节度量（与引擎内部度量互不依赖）。</summary>
    private static long MeasureBytes(string directory)
    {
        Assert.IsTrue(Directory.Exists(directory), $"测试侧度量要求目录存在：{directory}");
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    /// <summary>索引目录下的文件名集合（排序后逐字可比）。</summary>
    private static string[] ListIndexFiles(string indexDirectory)
    {
        if (!Directory.Exists(indexDirectory))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(indexDirectory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetFileName(file))
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static int CountSegments(string indexDirectory)
    {
        using var directory = FSDirectory.Open(indexDirectory);
        using var reader = DirectoryReader.Open(directory);
        return reader.Leaves.Count;
    }

    /// <summary>
    /// 目录里不得留下「不被当前 commit 引用的<b>段数据文件 / 段清单文件</b>」——即「无残余临时段」。
    /// <para>
    /// 三类合法的非 commit 文件必须放行，且它们的名字都<b>不</b>以 <c>_</c> 开头、也不以 <c>segments</c> 开头（除
    /// <c>segments.gen</c>）：<c>write.lock</c>（writer 锁）、<c>segments.gen</c>（Lucene 世代文件）、
    /// <c>.last_indexed</c>（本组件的索引时间戳）。因此判据取「名字以 <c>_</c> 开头」或
    /// 「名字以 <c>segments</c> 开头且不是 <c>segments.gen</c>」且不在 commit 文件清单里。
    /// </para>
    /// </summary>
    private static void AssertNoUnreferencedFiles(string indexDirectory)
    {
        using var directory = FSDirectory.Open(indexDirectory);
        using var reader = DirectoryReader.Open(directory);
        var referenced = new HashSet<string>(reader.IndexCommit.FileNames, StringComparer.Ordinal);

        var all = directory.ListAll();
        var extraSegmentData = all
            .Where(name => name.Length > 0 && name[0] == '_' && !referenced.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.AreEqual(
            0,
            extraSegmentData.Length,
            $"索引目录不得留下不被 commit 引用的段数据文件（残余：{string.Join(',', extraSegmentData)}）");

        var extraCommitFiles = all
            .Where(name => name.StartsWith("segments", StringComparison.Ordinal)
                           && !name.Equals("segments.gen", StringComparison.Ordinal)
                           && !referenced.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.AreEqual(
            0,
            extraCommitFiles.Length,
            $"索引目录不得留下多余的 commit 清单文件（残余：{string.Join(',', extraCommitFiles)}）");
    }

    /// <summary>测试侧独立读取索引里**存活文档**的 (path, line_text) 多重集（行式快照）。</summary>
    private static List<string> ReadLiveDocuments(string indexDirectory)
    {
        var rows = new List<string>();
        using var directory = FSDirectory.Open(indexDirectory);
        using var reader = DirectoryReader.Open(directory);
        var fields = new HashSet<string>(StringComparer.Ordinal) { "path", "line_text" };

        foreach (var leaf in reader.Leaves)
        {
            var atomic = (AtomicReader)leaf.Reader;
            var liveDocs = atomic.LiveDocs;
            for (var docId = 0; docId < atomic.MaxDoc; docId++)
            {
                if (liveDocs is not null && !liveDocs.Get(docId))
                    continue;

                var document = atomic.Document(docId, fields);
                rows.Add($"{document.Get("path")}::{document.Get("line_text")}");
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
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

    private sealed class Harness : IDisposable
    {
        internal Harness(FullTextIndexOptions options, string corpusRoot, IFileContentExtractor[] extractors)
        {
            Options = options;
            CorpusRoot = corpusRoot;
            Search = new LuceneSearchEngine(options, new JiebaAnalyzer(), extractors);
            Maintenance = new LuceneFullTextIndexMaintenanceEngine(Search, options);
        }

        internal FullTextIndexOptions Options { get; }

        internal string CorpusRoot { get; }

        internal string IndexDirectory => FullTextIndexPaths.ResolveIndexDirectory(Options.IndexRootDirectory, CorpusRoot);

        internal LuceneSearchEngine Search { get; }

        internal LuceneFullTextIndexMaintenanceEngine Maintenance { get; }

        internal FullTextChangeSet NewChangeSet(FullTextFileChange[] changes, string batchId = "01K-S3B-BATCH")
            => new(
                batchId,
                CorpusRoot,
                FullTextChangeCoalescer.NormalizeComparisonKey(CorpusRoot),
                changes,
                ScanStartedUtc: DateTimeOffset.UtcNow,
                PreviousWatermarkUtc: null,
                RequiresCheckpointAdvance: true);

        public void Dispose() => Search.Dispose();
    }
}
