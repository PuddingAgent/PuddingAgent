using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Maintenance;

namespace PuddingFullTextIndexTests;

/// <summary>
/// S2a：checkpoint 的**崩溃与幂等重放**断言（方案 §2.4 崩溃矩阵 + §2.5 失效模式）。
/// <para>
/// 覆盖对照（方案 §2.4 原文表格）：<br/>
/// ① 「扫描开始后、索引提交前」/「Lucene commit 前」⇒ checkpoint 未推进、下轮重扫
/// （<see cref="PreCommitFailures_BlockAdvance_SoTheNextRoundRescansEveryCandidate"/>）；<br/>
/// ② 「Lucene commit 后、checkpoint 前」⇒ 下轮重复提交同一批、delete-then-add 幂等、不漏文件
/// （<see cref="CrashAfterCommitBeforeCheckpointWrite_ReplaysTheSameBatch_WithoutLosingAnyFile"/>）；<br/>
/// ③ 「checkpoint 临时文件写入中」⇒ 正式 checkpoint 不变、残留绝不当作已提交
/// （<see cref="CrashDuringTemporaryWrite_KeepsCommittedCheckpointUnchanged_AndResidueIsNeverCommitted"/>）；<br/>
/// ④ 「checkpoint 原子替换后」⇒ 新 checkpoint 才生效（generation 单调 +1、watermark = 扫描开始时刻）
/// （<see cref="AtomicReplacement_NewCheckpointTakesEffect_WithMonotonicGeneration"/>）；<br/>
/// ⑤ 「checkpoint 损坏 / 不可读」⇒ fail-closed，绝不当作「无需处理」
/// （<see cref="CorruptOrUnreadableCheckpoint_IsFailClosed_AndNeverMeansNoWorkNeeded"/>）。
/// </para>
/// <para>
/// 本类以**纯函数**为主；唯一的落盘用例（
/// <see cref="ResidueAlongsideCommitted_OnlyTheCommittedFileIsRead"/>）只在系统
/// <see cref="Path.GetTempPath"/> 下的唯一子目录里写文件，并在 finally 里删除；
/// 绝不触碰真实索引目录（<c>D:\data\fulltext-index</c>）。
/// </para>
/// </summary>
[TestClass]
public sealed class CheckpointCrashSafetyTests
{
    private static readonly string TempRoot = Path.GetTempPath();
    private static readonly string ScopeRoot = Path.Combine(TempRoot, "pudding-fts-s2a-crash-scope");
    private static readonly string IndexRoot = Path.Combine(TempRoot, "pudding-fts-s2a-crash-index");

    private static readonly DateTimeOffset ScanStart = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PreviousWatermark = ScanStart - TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Overlap = MTimeComparison.DefaultMTimeOverlap;

    /// <summary>崩溃矩阵 ③：checkpoint 临时文件写入中 ⇒ 正式 checkpoint 不变，残留绝不算「已提交」。</summary>
    [TestMethod]
    public void CrashDuringTemporaryWrite_KeepsCommittedCheckpointUnchanged_AndResidueIsNeverCommitted()
    {
        var committedJson = BuildCheckpoint(PreviousWatermark, generation: 41, batchId: "batch-41").ToJson();
        var committedName = MaintenanceCheckpoint.FileName;
        var residueName = Path.GetFileName(
            MaintenanceCheckpoint.ResolveTemporaryCheckpointPath(IndexRoot, ScopeRoot, "42"));

        // 归类：只有正式文件名算 Committed；带 .tmp- 标记的残留一律 TemporaryResidue
        Assert.AreEqual(MaintenanceCheckpointFileKind.Committed, MaintenanceCheckpoint.ClassifyFile(committedName));
        Assert.IsTrue(MaintenanceCheckpoint.IsCommittedCheckpointFile(committedName));
        Assert.AreEqual(MaintenanceCheckpointFileKind.TemporaryResidue, MaintenanceCheckpoint.ClassifyFile(residueName));
        Assert.IsFalse(MaintenanceCheckpoint.IsTemporaryResidue(committedName));
        Assert.IsFalse(
            MaintenanceCheckpoint.IsCommittedCheckpointFile(residueName),
            "写盘中断留下的临时文件绝不可被当作已提交 checkpoint");

        // 正式 checkpoint 保持不变（崩溃发生在写临时文件期间 ⇒ 还没发生替换）
        Assert.AreEqual(MaintenanceCheckpointReadStatus.Ok, MaintenanceCheckpoint.TryParse(committedJson, out var reread));
        Assert.IsNotNull(reread);
        Assert.AreEqual(41, reread!.Generation);
        Assert.AreEqual(PreviousWatermark, reread.WatermarkUtc);
        Assert.IsTrue(MaintenanceCheckpoint.MatchesScope(reread, ScopeRoot));

        // 写到一半的残留内容：若被误当正式文件，解析必须失败（fail-closed），绝不返回「可用状态」
        var halfWritten = committedJson.Substring(0, committedJson.Length / 2);
        Assert.AreEqual(
            MaintenanceCheckpointReadStatus.Malformed,
            MaintenanceCheckpoint.TryParse(halfWritten, out var residueParsed),
            "半截 JSON 必须判 Malformed（不得被当成可用 checkpoint）");
        Assert.IsNull(residueParsed);

        // 反向对照：同一份完整文本可解析 ⇒ 上面不是「解析器恒失败」造成的假绿
        Assert.AreEqual(MaintenanceCheckpointReadStatus.Ok, MaintenanceCheckpoint.TryParse(committedJson, out var control));
        Assert.AreEqual(41, control!.Generation);
    }

    /// <summary>
    /// 崩溃矩阵 ③ 的落盘形态：临时残留与正式文件**同时存在**时，正式文件仍是唯一被读的那个，
    /// 且残留内容（哪怕它是一份更「新」的合法 JSON）不得影响解析结果。
    /// </summary>
    [TestMethod]
    public void ResidueAlongsideCommitted_OnlyTheCommittedFileIsRead()
    {
        var stateDirectory = Path.Combine(TempRoot, "pudding-fts-s2a-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);

        try
        {
            var committedPath = Path.Combine(stateDirectory, MaintenanceCheckpoint.FileName);
            var residuePath = Path.Combine(
                stateDirectory,
                MaintenanceCheckpoint.FileName + MaintenanceCheckpoint.TemporaryFileMarker + "42");
            var foreignPath = Path.Combine(stateDirectory, "notes.txt");

            File.WriteAllText(committedPath, BuildCheckpoint(PreviousWatermark, generation: 41, batchId: "batch-41").ToJson());
            File.WriteAllText(residuePath, BuildCheckpoint(ScanStart, generation: 99, batchId: "batch-42").ToJson());
            File.WriteAllText(foreignPath, "not a checkpoint");

            // 读盘协议（S3 真实写盘/读盘必须遵守）：只认 ClassifyFile == Committed 的那一个文件
            var selected = Directory
                .EnumerateFiles(stateDirectory)
                .Where(MaintenanceCheckpoint.IsCommittedCheckpointFile)
                .ToArray();

            Assert.AreEqual(1, selected.Length, "状态目录里唯一可被读取的必须是正式 checkpoint 文件");
            Assert.IsTrue(
                selected[0].StartsWith(TempRoot, StringComparison.OrdinalIgnoreCase),
                "本用例只允许在系统 Temp 下落盘（断言前缀必须是 Temp 路径）");

            Assert.AreEqual(
                MaintenanceCheckpointReadStatus.Ok,
                MaintenanceCheckpoint.TryParse(File.ReadAllText(selected[0]), out var parsed));
            Assert.IsNotNull(parsed);
            Assert.AreEqual(41, parsed!.Generation, "残留文件（generation=99）不得影响解析结果");
            Assert.AreEqual(PreviousWatermark, parsed.WatermarkUtc);

            // 反向对照：残留文件本身是合法 JSON ⇒ 若选择逻辑把残留也算进来，上面两条断言必然变红
            Assert.AreEqual(
                MaintenanceCheckpointFileKind.TemporaryResidue,
                MaintenanceCheckpoint.ClassifyFile(residuePath));
            Assert.AreEqual(
                MaintenanceCheckpointReadStatus.Ok,
                MaintenanceCheckpoint.TryParse(File.ReadAllText(residuePath), out var residueParsed));
            Assert.AreEqual(99, residueParsed!.Generation);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    /// <summary>崩溃矩阵 ④：原子替换后新 checkpoint 才生效 —— generation 单调 +1、watermark = 扫描开始时刻。</summary>
    [TestMethod]
    public void AtomicReplacement_NewCheckpointTakesEffect_WithMonotonicGeneration()
    {
        var scanFinish = ScanStart + TimeSpan.FromSeconds(30);
        var previous = BuildCheckpoint(PreviousWatermark, generation: 41, batchId: "batch-41");

        var next = MaintenanceCheckpoint.Advance(
            previous,
            ScopeRoot,
            policyFingerprint: "fp-1",
            scanStartedUtc: ScanStart,
            scanFinishedUtc: scanFinish,
            batchId: "batch-42",
            writtenUtc: scanFinish);

        Assert.AreEqual(42, next.Generation, "generation 必须单调 +1");
        Assert.IsTrue(next.Generation > previous.Generation);
        Assert.AreEqual(ScanStart, next.WatermarkUtc, "watermark 必须取扫描**开始**时刻");
        Assert.AreNotEqual(scanFinish, next.WatermarkUtc, "反向对照：结束时刻绝不是记录值");

        // 替换后的内容必须能原样往返（旧 watermark 不再出现）
        Assert.AreEqual(
            MaintenanceCheckpointReadStatus.Ok,
            MaintenanceCheckpoint.TryParse(next.ToJson(), out var reread));
        Assert.IsNotNull(reread);
        Assert.AreEqual(42, reread!.Generation);
        Assert.AreEqual(ScanStart, reread.WatermarkUtc);
        Assert.IsTrue(MaintenanceCheckpoint.MatchesScope(reread, ScopeRoot));

        // 跨轮仍单调：第二轮再 +1
        var third = MaintenanceCheckpoint.Advance(
            reread,
            ScopeRoot,
            policyFingerprint: "fp-1",
            scanStartedUtc: ScanStart + TimeSpan.FromMinutes(1),
            scanFinishedUtc: scanFinish + TimeSpan.FromMinutes(1),
            batchId: "batch-43",
            writtenUtc: scanFinish + TimeSpan.FromMinutes(1));

        Assert.AreEqual(43, third.Generation, "generation 必须跨轮单调");
        Assert.AreEqual(ScanStart + TimeSpan.FromMinutes(1), third.WatermarkUtc);
    }

    /// <summary>
    /// 崩溃矩阵 ②：commit 已成功、checkpoint 尚未写 ⇒ 崩溃后 checkpoint 仍是旧值；
    /// 下一轮以同一批文件**整体重放**（允许重复处理，**不允许漏文件**），且同一批的判定必须逐位相同（幂等）。
    /// </summary>
    [TestMethod]
    public void CrashAfterCommitBeforeCheckpointWrite_ReplaysTheSameBatch_WithoutLosingAnyFile()
    {
        var files = new[]
        {
            Path.Combine(ScopeRoot, "a.cs"),
            Path.Combine(ScopeRoot, "b.cs"),
            Path.Combine(ScopeRoot, "c.cs"),
            Path.Combine(ScopeRoot, "d.cs"),
        };

        var observedMTime = files.ToDictionary(
            path => path,
            _ => PreviousWatermark,
            StringComparer.OrdinalIgnoreCase);

        // 本批：4 个文件全部成功（UpsertAppliedCount = 4），允许推进
        var committedBatch = BuildMutationResult(
            FullTextMutationState.Applied,
            failedCount: 0,
            retainedOldCount: 0,
            upsertAppliedCount: files.Length);

        var decisionBeforeCrash = CheckpointAdvancePolicy.Decide(committedBatch);
        Assert.IsTrue(decisionBeforeCrash.Allowed, "完全成功 ⇒ 允许推进（前置条件）");

        // 崩溃：checkpoint 没有被写 ⇒ 磁盘上仍是旧内容（generation=41 / 旧 watermark）
        var onDisk = BuildCheckpoint(PreviousWatermark, generation: 41, batchId: "batch-41");
        Assert.AreEqual(
            MaintenanceCheckpointReadStatus.Ok,
            MaintenanceCheckpoint.TryParse(onDisk.ToJson(), out var stillOld));
        Assert.IsNotNull(stillOld);
        Assert.AreEqual(41, stillOld!.Generation);
        Assert.AreEqual(PreviousWatermark, stillOld.WatermarkUtc);

        // 下一轮：watermark 不变 ⇒ 同一批 4 个文件全部重新成为候选
        var replayed = files
            .Where(path => MTimeComparison.RequiresProcessing(observedMTime[path], stillOld.WatermarkUtc, Overlap))
            .ToArray();

        CollectionAssert.AreEquivalent(files, replayed, "崩溃后重放必须覆盖同一批文件的全部（重复允许，遗漏不允许）");
        Assert.AreEqual(files.Length, replayed.Length);

        // 幂等：同一批再应用一次，判定必须逐位相同（delete-then-add 幂等）
        var replayedBatch = BuildMutationResult(
            FullTextMutationState.Applied,
            failedCount: 0,
            retainedOldCount: 0,
            upsertAppliedCount: files.Length);

        Assert.AreEqual(decisionBeforeCrash, CheckpointAdvancePolicy.Decide(replayedBatch), "同一批重放两次的判定必须相同");

        // 处理过的路径并集 == 原始集合（没有新增、没有丢失）
        var processedAcrossRounds = files.Concat(replayed).ToArray();
        CollectionAssert.AreEquivalent(
            files,
            processedAcrossRounds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            "两轮处理过的路径并集必须与原始 4 个文件逐个相等（允许重复，不允许漏）");

        // 崩溃的那一轮不产生 generation 增量：只有真正落盘的那一次推进才计数
        var afterReplayWrite = MaintenanceCheckpoint.Advance(
            stillOld,
            ScopeRoot,
            policyFingerprint: "fp-1",
            scanStartedUtc: ScanStart + TimeSpan.FromMinutes(1),
            scanFinishedUtc: ScanStart + TimeSpan.FromMinutes(2),
            batchId: "batch-42",
            writtenUtc: ScanStart + TimeSpan.FromMinutes(2));

        Assert.AreEqual(42, afterReplayWrite.Generation, "崩溃的那一轮不得计入 generation（只有落盘的推进才 +1）");
    }

    /// <summary>
    /// 崩溃矩阵 ①：「扫描开始后、索引提交前」/「Lucene commit 前」/ 取消 / 预算拒绝 / 互斥 ⇒
    /// checkpoint 未推进，于是下一轮把**每一个候选**都重扫（不漏文件）。
    /// </summary>
    [TestMethod]
    public void PreCommitFailures_BlockAdvance_SoTheNextRoundRescansEveryCandidate()
    {
        var files = new[]
        {
            Path.Combine(ScopeRoot, "a.cs"),
            Path.Combine(ScopeRoot, "b.cs"),
            Path.Combine(ScopeRoot, "c.cs"),
            Path.Combine(ScopeRoot, "d.cs"),
        };

        var observedMTime = files.ToDictionary(
            path => path,
            _ => PreviousWatermark,
            StringComparer.OrdinalIgnoreCase);

        var preCommitOutcomes = new[]
        {
            FullTextMutationState.PartiallyApplied, // 单文件失败被隔离 ⇒ 成功项已提交但存在失败/保留项
            FullTextMutationState.Rejected,         // 预算硬限 / 守卫拒绝（未写入）
            FullTextMutationState.Busy,             // 租约 / writer lock 冲突（未写入）
            FullTextMutationState.Cancelled,        // 取消（§3.6 末句：rollback/外抛，不写 checkpoint）
            FullTextMutationState.Failed,           // 致命失败（rollback 或沿用上一个 commit）
        };

        foreach (var state in preCommitOutcomes)
        {
            var result = BuildMutationResult(state, failedCount: 1, retainedOldCount: 1, upsertAppliedCount: 3);

            Assert.IsFalse(
                CheckpointAdvancePolicy.AllowsAdvance(result),
                $"未完整成功（{state}）⇒ checkpoint 绝不推进");

            foreach (var (path, mtime) in observedMTime)
            {
                Assert.IsTrue(
                    MTimeComparison.RequiresProcessing(mtime, PreviousWatermark, Overlap),
                    $"checkpoint 未推进 ⇒ {path} 必须在下一轮重扫");
            }
        }

        // 保留项（Applied + RetainedOldCount > 0）同样阻止推进，理由与失败同构
        Assert.IsFalse(
            CheckpointAdvancePolicy.AllowsAdvance(
                BuildMutationResult(FullTextMutationState.Applied, failedCount: 0, retainedOldCount: 1, upsertAppliedCount: 3)),
            "存在待重试（保留旧索引）的路径 ⇒ checkpoint 不推进");
    }

    /// <summary>
    /// 崩溃矩阵 ⑤ / 方案 §2.5「checkpoint 损坏 / 不可读」：解析 fail-closed，
    /// 且**绝不**等于「无需维护」——读不到基线必须走全范围校准 / 判需处理。
    /// </summary>
    [TestMethod]
    public void CorruptOrUnreadableCheckpoint_IsFailClosed_AndNeverMeansNoWorkNeeded()
    {
        var validJson = BuildCheckpoint(PreviousWatermark, generation: 41, batchId: "batch-41").ToJson();
        var halfWritten = validJson.Substring(0, validJson.Length / 2);

        Assert.AreEqual(MaintenanceCheckpointReadStatus.Missing, MaintenanceCheckpoint.TryParse(null, out var missing));
        Assert.IsNull(missing);
        Assert.AreEqual(MaintenanceCheckpointReadStatus.Missing, MaintenanceCheckpoint.TryParse("   ", out _));

        Assert.AreEqual(
            MaintenanceCheckpointReadStatus.Malformed,
            MaintenanceCheckpoint.TryParse(halfWritten, out var malformed),
            "写到一半的 checkpoint 必须判损坏");
        Assert.IsNull(malformed);

        var unsupportedJson = BuildCheckpoint(PreviousWatermark, generation: 41, batchId: "batch-9", version: 2).ToJson();
        Assert.AreEqual(
            MaintenanceCheckpointReadStatus.UnsupportedVersion,
            MaintenanceCheckpoint.TryParse(unsupportedJson, out var unsupported),
            "版本不受支持必须显式拒绝");
        Assert.IsNull(unsupported, "版本不支持时绝不可返回一个「可用」checkpoint");

        // ★ 关键：读不到基线 ⇒ 需全范围校准 / 需处理，绝不按「无需维护」处理
        Assert.AreEqual(
            MTimeCalibrationDecision.FullScopeRecalibrationRequired,
            MTimeComparison.DecideCalibration(ScanStart, watermarkUtc: null),
            "没有可用 watermark ⇒ 必须走全范围局部校准（不是「跳过」）");

        foreach (var path in new[] { Path.Combine(ScopeRoot, "a.cs"), Path.Combine(ScopeRoot, "b.cs") })
        {
            Assert.IsTrue(
                MTimeComparison.RequiresProcessing(PreviousWatermark, null, Overlap),
                $"{path} 在无基线时必须判需处理");
        }

        // 反向对照：确实存在「正常增量」这一分支（否则上面几条 fail-closed 断言可能是恒真）
        Assert.AreEqual(
            MTimeCalibrationDecision.Incremental,
            MTimeComparison.DecideCalibration(ScanStart, PreviousWatermark));
        Assert.IsFalse(MTimeComparison.RequiresProcessing(
            PreviousWatermark - Overlap - TimeSpan.FromTicks(1),
            PreviousWatermark,
            Overlap));
    }

    // ── 构造帮助 ─────────────────────────────────────────────────────────

    private static MaintenanceCheckpoint BuildCheckpoint(
        DateTimeOffset? watermarkUtc,
        long generation,
        string? batchId,
        int version = MaintenanceCheckpoint.CurrentVersion)
        => new()
        {
            Version = version,
            ScopeRoot = ScopeRoot,
            PolicyFingerprint = "fp-1",
            WatermarkUtc = watermarkUtc,
            Generation = generation,
            LastCompletedBatchId = batchId,
            WrittenUtc = watermarkUtc is null ? null : watermarkUtc.Value + TimeSpan.FromSeconds(1),
        };

    private static FullTextMutationResult BuildMutationResult(
        FullTextMutationState state,
        int failedCount,
        int retainedOldCount,
        int upsertAppliedCount)
        => new(
            "01K-S2A-BATCH",
            ScopeRoot,
            state,
            upsertAppliedCount,
            0,
            retainedOldCount,
            failedCount,
            Array.Empty<string>(),
            Array.Empty<string>(),
            null,
            null,
            null,
            false,
            null);
}
