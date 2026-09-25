using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure;
using PuddingFullTextIndex.Infrastructure.Maintenance;

namespace PuddingFullTextIndexTests;

/// <summary>
/// S1a：全文索引「局部维护」的**契约 + 纯逻辑**组件侧断言（方案 §2.2 / §2.3 / §2.5 / §3.1 / §3.2 / §7.3）。
/// <para>
/// 本用例**零 IO**：所有类目只做纯函数 / 纯字符串 / 反射断言，索引根与语料根一律落在
/// <see cref="Path.GetTempPath"/> 之下且**只做路径运算、不建目录、不写文件**。
/// </para>
/// <para>
/// 两条变异取红（S1a 任务书 §4 第 6 步）在本文件里各有一条**判别性断言**：
/// ① <c>&gt;=</c> 边界（<see cref="MTime_GreaterOrEqualBoundary_RequiresProcessing"/>）；
/// ② checkpoint 取扫描开始时刻（<see cref="ScanWindow_WatermarkTakesScanStart_SoFileWrittenAfterEnumerationIsStillProcessedNextRound"/>
/// 与 <see cref="CheckpointAdvance_UsesScanStartAsWatermark_AndIncrementsGeneration"/>）。
/// </para>
/// </summary>
[TestClass]
public sealed class MaintenanceCoreTests
{
    private static readonly string TempRoot = Path.GetTempPath();
    private static readonly string ScopeRoot = Path.Combine(TempRoot, "pudding-fts-s1a-scope");
    private static readonly string IndexRoot = Path.Combine(TempRoot, "pudding-fts-s1a-index-root");

    private static readonly DateTimeOffset ScanStart = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Overlap = TimeSpan.FromSeconds(2);

    // ── mtime 比较（方案 §2.2 / §2.3 / §2.5）──────────────────────────────

    /// <summary>
    /// ★ 变异取红 M1 的判别断言：判定式必须是 <c>&gt;=</c>。
    /// <c>mtime == watermark</c> 与 <c>mtime == watermark - overlap</c> 都**必须**判需处理。
    /// </summary>
    [TestMethod]
    public void MTime_GreaterOrEqualBoundary_RequiresProcessing()
    {
        Assert.AreEqual(
            ScanStart - Overlap,
            MTimeComparison.ComputeEffectiveWatermark(ScanStart, Overlap),
            "有效水位线必须 = watermark - overlap");

        // 恰等于把 watermark 本身 ⇒ 需处理（不是严格 >）
        Assert.IsTrue(
            MTimeComparison.RequiresProcessing(ScanStart, ScanStart, Overlap),
            "mtime == watermark 必须判需处理（判定式含等号）");

        // 恰等于 effective 边界 ⇒ 需处理（M1 把 >= 改成 > 时，本条变红）
        Assert.IsTrue(
            MTimeComparison.RequiresProcessing(ScanStart - Overlap, ScanStart, Overlap),
            "mtime == watermark - overlap 必须判需处理（边界含等号）");

        // 比 watermark 更新 ⇒ 需处理
        Assert.IsTrue(MTimeComparison.RequiresProcessing(ScanStart + TimeSpan.FromSeconds(1), ScanStart, Overlap));

        // 唯一允许跳过的一条：严格早于有效水位线
        Assert.IsFalse(
            MTimeComparison.RequiresProcessing(ScanStart - Overlap - TimeSpan.FromTicks(1), ScanStart, Overlap),
            "只有严格早于 watermark - overlap 的文件才允许跳过");

        // 反向对照：若判定式被写成恒真，上面「必须跳过」这条会失败 ⇒ 证明断言不是同义反复
        Assert.IsFalse(MTimeComparison.RequiresProcessing(ScanStart - TimeSpan.FromDays(1), ScanStart, Overlap));
    }

    /// <summary>mtime 读不到 / 无 watermark ⇒ <b>一律判需处理</b>（fail-stale，安全方向）。</summary>
    [TestMethod]
    public void MTime_UnreadableMtimeOrMissingWatermark_FailsStale()
    {
        Assert.IsTrue(
            MTimeComparison.RequiresProcessing(null, ScanStart, Overlap),
            "mtime 读不到必须判需处理 —— 绝不允许「读不到就跳过」");

        Assert.IsTrue(
            MTimeComparison.RequiresProcessing(ScanStart, null, Overlap),
            "没有 watermark（无 checkpoint / 不可读）必须判需处理");

        Assert.IsTrue(MTimeComparison.RequiresProcessing(null, null, Overlap));

        // 反向对照：确实存在「判为无需处理」的分支（否则上面几条可能是恒真）
        Assert.IsFalse(MTimeComparison.RequiresProcessing(ScanStart - TimeSpan.FromHours(1), ScanStart, Overlap));
    }

    /// <summary>负的重叠窗口是编程错误，必须立刻失败（合法值由 options 校验层拦）。</summary>
    [TestMethod]
    public void MTime_NegativeOverlap_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MTimeComparison.RequiresProcessing(ScanStart, ScanStart, TimeSpan.FromSeconds(-1)));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MTimeComparison.ComputeEffectiveWatermark(ScanStart, TimeSpan.FromTicks(-1)));
    }

    /// <summary>
    /// ★ 变异取红 M2 的判别断言：checkpoint 取**扫描开始**时刻。
    /// 模拟「枚举之后、扫描结束之前被写入」的文件：取开始时刻 ⇒ 下轮仍被判定需处理；取结束时刻 ⇒ 永久漏掉。
    /// </summary>
    [TestMethod]
    public void ScanWindow_WatermarkTakesScanStart_SoFileWrittenAfterEnumerationIsStillProcessedNextRound()
    {
        var scanFinish = ScanStart + TimeSpan.FromSeconds(30);
        var writtenAfterEnumeration = ScanStart + TimeSpan.FromSeconds(10);   // 已枚举过、扫描未结束

        var nextWatermark = MTimeComparison.ComputeNextWatermark(ScanStart, scanFinish);

        Assert.AreEqual(
            ScanStart,
            nextWatermark,
            "checkpoint 的 watermark 必须取扫描**开始**时刻（取结束时刻 ⇒ M2 变异）");

        Assert.IsTrue(
            MTimeComparison.RequiresProcessing(writtenAfterEnumeration, nextWatermark, Overlap),
            "枚举之后、扫描结束之前被写入的文件，下一轮必须仍被判需处理");

        // 反向对照：证明「取结束时刻」真的会漏掉这个文件（不是空口断言）
        Assert.IsFalse(
            MTimeComparison.RequiresProcessing(writtenAfterEnumeration, scanFinish, Overlap),
            "反向对照：若 watermark 取结束时刻，该文件会被判「无需处理」= 永久漏掉");
    }

    /// <summary>时钟回拨（<c>scanStart &lt; watermark</c>）与「无 baseline」都返回需全范围校准；本片不做校准动作。</summary>
    [TestMethod]
    public void ClockRollback_RequiresFullScopeRecalibration()
    {
        Assert.AreEqual(
            MTimeCalibrationDecision.FullScopeRecalibrationRequired,
            MTimeComparison.DecideCalibration(ScanStart - TimeSpan.FromMinutes(5), ScanStart),
            "scanStart < watermark（时钟回拨）必须要求全范围局部校准");

        Assert.AreEqual(
            MTimeCalibrationDecision.Incremental,
            MTimeComparison.DecideCalibration(ScanStart, ScanStart),
            "scanStart == watermark 是合法增量基线（严格小于才算回拨）");

        Assert.AreEqual(
            MTimeCalibrationDecision.Incremental,
            MTimeComparison.DecideCalibration(ScanStart + TimeSpan.FromSeconds(1), ScanStart));

        Assert.AreEqual(
            MTimeCalibrationDecision.FullScopeRecalibrationRequired,
            MTimeComparison.DecideCalibration(ScanStart, null),
            "无 watermark ⇒ 按陈旧处理，绝不是「无需维护」");
    }

    /// <summary>读取前后两次 stat 必须双侧可读且逐位一致，否则视为不稳定（放弃本次内容）。</summary>
    [TestMethod]
    public void StatStability_RequiresBothSidesReadableAndEqual()
    {
        Assert.IsTrue(MTimeComparison.IsStatStable(ScanStart, 10, ScanStart, 10));

        Assert.IsFalse(MTimeComparison.IsStatStable(ScanStart, 10, ScanStart + TimeSpan.FromTicks(1), 10), "mtime 变了 ⇒ 不稳定");
        Assert.IsFalse(MTimeComparison.IsStatStable(ScanStart, 10, ScanStart, 11), "length 变了 ⇒ 不稳定");
        Assert.IsFalse(MTimeComparison.IsStatStable(null, 10, ScanStart, 10), "读不到前值 ⇒ 不稳定（fail-closed）");
        Assert.IsFalse(MTimeComparison.IsStatStable(ScanStart, null, ScanStart, 10), "读不到前长度 ⇒ 不稳定");
        Assert.IsFalse(MTimeComparison.IsStatStable(ScanStart, 10, null, 10), "读不到后值 ⇒ 不稳定");
        Assert.IsFalse(MTimeComparison.IsStatStable(ScanStart, 10, ScanStart, null), "读不到后长度 ⇒ 不稳定");
    }

    // ── 变更折叠（方案 §3.1 合并规则）────────────────────────────────────

    private static FullTextCoalesceResult Coalesce(params FullTextChangeObservation[] observations)
        => FullTextChangeCoalescer.Coalesce(observations, ScopeRoot);

    /// <summary>同一规范化路径在一个窗口内最多保留一个最终动作，且以**最后一条**观察为准。</summary>
    [TestMethod]
    public void Coalesce_SamePathMultipleEvents_KeepsSingleFinalAction_LastWins()
    {
        var file = Path.Combine(ScopeRoot, "src", "a.cs");

        var result = Coalesce(
            new(file, FullTextChangeSource.Watcher, FullTextPathObservation.Missing),
            new(file, FullTextChangeSource.Watcher, FullTextPathObservation.IndexableFile, ScanStart, 12));

        Assert.AreEqual(1, result.Changes.Count, "同路径多次事件只能有一个最终动作");
        Assert.AreEqual(FullTextChangeKind.Upsert, result.Changes[0].Kind, "最终观察是可索引文件 ⇒ Upsert");
        Assert.AreEqual(file, result.Changes[0].FullPath);
        Assert.AreEqual(12, result.Changes[0].Length);
        Assert.AreEqual(0, result.DeferredPaths.Count);
        Assert.AreEqual(0, result.RejectedPaths.Count);
    }

    /// <summary>多来源命中同一路径 ⇒ <c>Sources</c> 位或合并（诊断用，不参与正确性判定）。</summary>
    [TestMethod]
    public void Coalesce_MergesSourcesByBitwiseOr()
    {
        var file = Path.Combine(ScopeRoot, "src", "b.cs");

        var result = Coalesce(
            new(file, FullTextChangeSource.Watcher, FullTextPathObservation.IndexableFile),
            new(file, FullTextChangeSource.MTimeRecovery, FullTextPathObservation.IndexableFile),
            new(file, FullTextChangeSource.IntegrityCheck, FullTextPathObservation.IndexableFile));

        Assert.AreEqual(1, result.Changes.Count);
        Assert.AreEqual(
            FullTextChangeSource.Watcher | FullTextChangeSource.MTimeRecovery | FullTextChangeSource.IntegrityCheck,
            result.Changes[0].Sources,
            "多来源必须按位或合并");
    }

    /// <summary>rename ⇒ 旧路径 <c>Delete</c> + 新路径 <c>Upsert</c>（各一次）。</summary>
    [TestMethod]
    public void Coalesce_Rename_FoldsToDeleteOldAndUpsertNew()
    {
        var oldPath = Path.Combine(ScopeRoot, "src", "old-name.cs");
        var newPath = Path.Combine(ScopeRoot, "src", "new-name.cs");

        var result = FullTextChangeCoalescer.Coalesce(
            FullTextChangeCoalescer.FromRename(oldPath, newPath, FullTextChangeSource.Watcher, ScanStart, 7),
            ScopeRoot);

        Assert.AreEqual(2, result.Changes.Count, "rename 必须折叠成恰好两条动作");
        Assert.AreEqual(0, result.DeferredPaths.Count);
        Assert.AreEqual(0, result.RejectedPaths.Count);

        var deleted = result.Changes.Single(c => string.Equals(c.FullPath, oldPath, StringComparison.Ordinal));
        var upserted = result.Changes.Single(c => string.Equals(c.FullPath, newPath, StringComparison.Ordinal));

        Assert.AreEqual(FullTextChangeKind.Delete, deleted.Kind);
        Assert.AreEqual(FullTextChangeKind.Upsert, upserted.Kind);
        Assert.AreEqual(7, upserted.Length);
        Assert.AreEqual(ScanStart, upserted.LastWriteUtc);
    }

    /// <summary>暂时不可读 ⇒ **不产出动作**、保留旧索引并标记待重试。</summary>
    [TestMethod]
    public void Coalesce_Unreadable_ProducesNoAction_AndDefers()
    {
        var file = Path.Combine(ScopeRoot, "src", "locked.cs");

        var result = Coalesce(new FullTextChangeObservation(file, FullTextChangeSource.MTimeRecovery, FullTextPathObservation.Unreadable));

        Assert.AreEqual(0, result.Changes.Count, "不可读时产出 Delete 就等于把「读不到」当「不存在」，会误删有效旧文档");
        Assert.AreEqual(1, result.DeferredPaths.Count);
        Assert.AreEqual(file, result.DeferredPaths[0]);
        Assert.AreEqual(0, result.RejectedPaths.Count);
    }

    /// <summary>不存在 / 变目录 / 扩展名不再允许 / 命中噪声 / 空文件 / 超限 ⇒ 最终动作一律 <c>Delete</c>。</summary>
    [TestMethod]
    public void Coalesce_NonIndexableFinalStates_ProduceDelete()
    {
        var cases = new (FullTextPathObservation Observation, string FileName)[]
        {
            (FullTextPathObservation.Missing, "gone.cs"),
            (FullTextPathObservation.Directory, "was-a-file"),
            (FullTextPathObservation.ExtensionNotAllowed, "renamed.bin"),
            (FullTextPathObservation.Noisy, "bin"),
            (FullTextPathObservation.Empty, "empty.cs"),
            (FullTextPathObservation.Oversized, "huge.cs"),
        };

        foreach (var (observation, fileName) in cases)
        {
            var path = Path.Combine(ScopeRoot, "src", fileName);
            var result = Coalesce(new FullTextChangeObservation(path, FullTextChangeSource.IntegrityCheck, observation));

            Assert.AreEqual(1, result.Changes.Count, $"{observation} 必须产出恰好一条动作");
            Assert.AreEqual(FullTextChangeKind.Delete, result.Changes[0].Kind, $"{observation} 的最终动作必须是 Delete");
            Assert.AreEqual(0, result.DeferredPaths.Count, $"{observation} 不是「暂时不可读」，不应待重试");
        }
    }

    /// <summary>路径规范化大小写不敏感（Windows First），但**输出保留原始大小写**用于诊断。</summary>
    [TestMethod]
    public void Coalesce_PathKeyIsCaseInsensitive_ButOutputKeepsOriginalCasing()
    {
        var upper = Path.Combine(ScopeRoot, "Src", "MixedCase.CS");
        var lower = Path.Combine(ScopeRoot, "src", "mixedcase.cs");

        var result = Coalesce(
            new(upper, FullTextChangeSource.Watcher, FullTextPathObservation.Missing),
            new(lower, FullTextChangeSource.MTimeRecovery, FullTextPathObservation.IndexableFile, ScanStart, 3));

        Assert.AreEqual(1, result.Changes.Count, "Windows 上大小写不同但同一文件 ⇒ 只能有一个最终动作");
        Assert.AreEqual(lower, result.Changes[0].FullPath, "输出保留最后一条观察的原始大小写");
        Assert.AreEqual(
            FullTextChangeSource.Watcher | FullTextChangeSource.MTimeRecovery,
            result.Changes[0].Sources);
    }

    /// <summary>
    /// 路径越界语义（本片**选定**的语义，见交付报告）：**拒绝并如实返回**，绝不静默过滤。
    /// 覆盖三种越界形态：完全不同根 / 名字前缀相同但非同层子树 / scope 根本身。
    /// </summary>
    [TestMethod]
    public void Coalesce_PathOutsideScope_IsRejectedNotSilentlyDropped()
    {
        var otherRoot = Path.Combine(TempRoot, "pudding-fts-s1a-other-scope", "a.cs");
        var siblingPrefix = Path.Combine(ScopeRoot + "-sibling", "a.cs");

        var result = Coalesce(
            new(otherRoot, FullTextChangeSource.Watcher, FullTextPathObservation.IndexableFile),
            new(siblingPrefix, FullTextChangeSource.Watcher, FullTextPathObservation.IndexableFile),
            new(ScopeRoot, FullTextChangeSource.Watcher, FullTextPathObservation.IndexableFile));

        Assert.AreEqual(0, result.Changes.Count, "越界路径不得进入本 scope 的变更集");
        Assert.AreEqual(3, result.RejectedPaths.Count, "越界路径必须被如实返回，不得静默丢弃");
        Assert.IsTrue(
            result.RejectedPaths.All(r => r.Reason == FullTextPathRejectionReason.OutsideScope),
            "拒绝原因必须是 OutsideScope");
        Assert.IsTrue(result.RejectedPaths.All(r => r.Message.Length > 0));
    }

    /// <summary>空路径 / 未声明来源同样被拒绝（不静默丢弃）。</summary>
    [TestMethod]
    public void Coalesce_BlankPathAndMissingSource_AreRejected()
    {
        var result = Coalesce(
            new("   ", FullTextChangeSource.Watcher, FullTextPathObservation.IndexableFile),
            new(Path.Combine(ScopeRoot, "b.cs"), default, FullTextPathObservation.IndexableFile));

        Assert.AreEqual(0, result.Changes.Count);
        Assert.AreEqual(2, result.RejectedPaths.Count);
        Assert.AreEqual(FullTextPathRejectionReason.BlankPath, result.RejectedPaths[0].Reason);
        Assert.AreEqual(FullTextPathRejectionReason.NoSource, result.RejectedPaths[1].Reason);
    }

    /// <summary>未知的观察取值必须抛错而不是被猜成 Delete（猜测会误删索引）。</summary>
    [TestMethod]
    public void Coalesce_UnknownObservationValue_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Coalesce(
            new FullTextChangeObservation(Path.Combine(ScopeRoot, "c.cs"), FullTextChangeSource.Watcher, (FullTextPathObservation)99)));
    }

    /// <summary>scope 内 / 外判定的边界：路径分隔符必须凑满，前缀相同不算子树。</summary>
    [TestMethod]
    public void IsWithinScope_RejectsSiblingWithSharedNamePrefix()
    {
        Assert.IsTrue(FullTextChangeCoalescer.IsWithinScope(ScopeRoot, Path.Combine(ScopeRoot, "a.cs")));
        Assert.IsTrue(FullTextChangeCoalescer.IsWithinScope(ScopeRoot, Path.Combine(ScopeRoot, "sub", "a.cs")));
        Assert.IsTrue(FullTextChangeCoalescer.IsWithinScope(ScopeRoot.ToUpperInvariant(), Path.Combine(ScopeRoot, "A.CS")), "大小写不敏感");

        Assert.IsFalse(FullTextChangeCoalescer.IsWithinScope(ScopeRoot, ScopeRoot), "scope 根本身是目录，不是文件变更对象");
        Assert.IsFalse(FullTextChangeCoalescer.IsWithinScope(ScopeRoot, ScopeRoot + Path.DirectorySeparatorChar));
        Assert.IsFalse(FullTextChangeCoalescer.IsWithinScope(ScopeRoot, ScopeRoot + "-sibling"));
        Assert.IsFalse(FullTextChangeCoalescer.IsWithinScope(ScopeRoot, Path.Combine(ScopeRoot + "-sibling", "a.cs")));
    }

    // ── options 默认值与 fail-closed 校验（方案 §7.1 / §7.3）──────────────

    private static MaintenanceOptions ValidOptions() => new()
    {
        Enabled = true,
        Scopes = new[] { ScopeRoot },
        IndexRootDirectory = IndexRoot,
    };

    /// <summary>默认值必须「关闭」且与冻结方案 §7.1 的取值逐条一致。</summary>
    [TestMethod]
    public void Options_Defaults_AreDisabledAndMatchTheFrozenPlanValues()
    {
        var options = new MaintenanceOptions();

        Assert.IsFalse(options.Enabled, "局部维护默认必须关闭");
        Assert.AreEqual(0, options.Scopes.Count);
        Assert.IsNull(options.WorkspaceRoot);
        Assert.IsNull(options.IndexRootDirectory);
        Assert.AreEqual(1_073_741_824L, options.MaxIndexBytes);
        Assert.AreEqual(4096, options.QueueCapacity);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), options.Debounce);
        Assert.AreEqual(TimeSpan.FromSeconds(2), options.MaxCoalesceWait);
        Assert.AreEqual(512, options.MaxBatchPaths);
        Assert.AreEqual(TimeSpan.FromMinutes(15), options.RecoveryScanInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(2), options.MTimeOverlap);
        Assert.AreEqual(MTimeComparison.DefaultMTimeOverlap, options.MTimeOverlap, "默认重叠窗口必须来自单一常量（2 秒）");
        Assert.AreEqual(TimeSpan.FromHours(24), options.HealthCheckInterval);
        Assert.AreEqual(64, options.HealthCheckSliceFiles);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), options.HealthCheckSliceDelay);
        Assert.AreEqual(35, options.CpuHighWatermarkPercent);
        Assert.AreEqual(20, options.DiskHighWatermarkPercent);
        Assert.AreEqual(TimeSpan.FromSeconds(30), options.PressureBackoff);
        Assert.AreEqual(0, options.ContentCacheMaxBytes);
    }

    /// <summary>默认关闭 ⇒ 零副作用：不要求任何目录存在、不推导任何路径（无 Scopes / 无 IndexRoot 也算合法）。</summary>
    [TestMethod]
    public void Options_Disabled_RequiresNothingOnDisk_AndNeedsNoScopes()
    {
        var result = MaintenanceOptions.Validate(new MaintenanceOptions { Enabled = false });

        Assert.IsTrue(result.IsValid, "Enabled=false 必须零副作用：不探测 scope、不要求索引根存在、不推导路径");
        Assert.AreEqual(0, result.Violations.Count);
    }

    /// <summary>合法组合必须通过，且不产生任何违规项。</summary>
    [TestMethod]
    public void Options_ValidEnabledConfiguration_Passes()
    {
        var result = MaintenanceOptions.Validate(ValidOptions());

        Assert.IsTrue(result.IsValid, $"合法配置被拒：{string.Join(" | ", result.Violations.Select(v => v.Option + ": " + v.Message))}");
        Assert.AreEqual(0, result.Violations.Count);
    }

    /// <summary>非法组合必须被 **fail-closed 拒绝**（返回结构化错误，不做「纠正后继续」）。</summary>
    [TestMethod]
    public void Options_InvalidCombinations_AreRejected()
    {
        var tooLargeOverlap = MaintenanceOptions.MaxMTimeOverlap + TimeSpan.FromSeconds(1);

        var cases = new (string Label, MaintenanceOptions Options)[]
        {
            ("Enabled=true 但 Scopes 为空", ValidOptions() with { Scopes = Array.Empty<string>() }),
            ("MaxIndexBytes <= 0", ValidOptions() with { MaxIndexBytes = 0 }),
            ("MTimeOverlap 为负", ValidOptions() with { MTimeOverlap = TimeSpan.FromSeconds(-1) }),
            ("MTimeOverlap 过大", ValidOptions() with { MTimeOverlap = tooLargeOverlap }),
            ("RecoveryScanInterval 过短", ValidOptions() with { RecoveryScanInterval = TimeSpan.FromSeconds(1) }),
            ("RecoveryScanInterval 过长", ValidOptions() with { RecoveryScanInterval = TimeSpan.FromDays(30) }),
            ("Debounce <= 0", ValidOptions() with { Debounce = TimeSpan.Zero }),
            ("MaxCoalesceWait < Debounce", ValidOptions() with { MaxCoalesceWait = TimeSpan.FromMilliseconds(100) }),
            ("QueueCapacity <= 0", ValidOptions() with { QueueCapacity = 0 }),
            ("MaxBatchPaths 超上限", ValidOptions() with { MaxBatchPaths = MaintenanceOptions.MaxBatchPathsAllowed + 1 }),
            ("HealthCheckInterval 过短", ValidOptions() with { HealthCheckInterval = TimeSpan.FromSeconds(1) }),
            ("HealthCheckSliceFiles <= 0", ValidOptions() with { HealthCheckSliceFiles = 0 }),
            ("CPU 阈值越界", ValidOptions() with { CpuHighWatermarkPercent = 101 }),
            ("磁盘阈值越界", ValidOptions() with { DiskHighWatermarkPercent = 0 }),
            ("ContentCacheMaxBytes < 0", ValidOptions() with { ContentCacheMaxBytes = -1 }),
            ("相对 scope 且无 WorkspaceRoot", ValidOptions() with { Scopes = new[] { "src" }, WorkspaceRoot = null }),
            ("重复 scope（规范化后相同）", ValidOptions() with { Scopes = new[] { @"C:\Corpus\Alpha", @"c:\corpus\alpha\" } }),
            ("IndexRoot 落在 scope 之内", ValidOptions() with { IndexRootDirectory = Path.Combine(ScopeRoot, "index-root") }),
            ("IndexRootDirectory 缺失", ValidOptions() with { IndexRootDirectory = null }),
        };

        Assert.IsTrue(cases.Length >= 4, "任务书要求至少 4 个非法组合被拒");

        foreach (var (label, options) in cases)
        {
            var result = MaintenanceOptions.Validate(options);

            Assert.IsFalse(result.IsValid, $"必须被拒绝：{label}");
            Assert.IsTrue(result.Violations.Count > 0, $"拒绝必须带结构化违规项：{label}");
            Assert.IsTrue(
                result.Violations.All(v => v.Option.Length > 0 && v.Value is not null && v.Message.Length > 0),
                $"违规项必须「选项名 + 取值 + 消息」齐全：{label}");
        }
    }

    /// <summary>
    /// 数值域校验与开关**无关**（纯算术、零 IO、零路径推导）：关闭状态下的明显非法值同样被拒。
    /// 这是本片对「关闭即零副作用」与「fail-closed」两难处的**选定口径**，已记入交付报告。
    /// </summary>
    [TestMethod]
    public void Options_NumericValidationStillAppliesWhenDisabled()
    {
        var result = MaintenanceOptions.Validate(new MaintenanceOptions
        {
            Enabled = false,
            MTimeOverlap = TimeSpan.FromSeconds(-1),
        });

        Assert.IsFalse(result.IsValid, "负值属于纯算术违规，不涉及任何目录探测，关闭状态下仍必须被拒绝");
        Assert.AreEqual(nameof(MaintenanceOptions.MTimeOverlap), result.Violations[0].Option);
    }

    // ── checkpoint 路径推导与崩溃残留（方案 §2.1）────────────────────────

    private static string ExpectedIndexDirectoryName()
        => Path.GetFileName(FullTextIndexPaths.ResolveIndexDirectory(IndexRoot, ScopeRoot));

    /// <summary>
    /// checkpoint 位置只能由 <c>FullTextIndexPaths</c> 推导：状态目录名里的目录名必须**就是**
    /// <c>FullTextIndexPaths.ResolveIndexDirectory</c> 返回的目录名（同一真源 ⇒ 无第二处哈希实现）。
    /// </summary>
    [TestMethod]
    public void CheckpointPath_IsDerivedFromTheSingleHashSource()
    {
        var expectedDirectoryName = ExpectedIndexDirectoryName();

        var directory = MaintenanceCheckpoint.ResolveStateDirectory(IndexRoot, ScopeRoot);
        var checkpointPath = MaintenanceCheckpoint.ResolveCheckpointPath(IndexRoot, ScopeRoot);

        Assert.AreEqual(64, expectedDirectoryName.Length, "live 索引目录名必须是 FullTextIndexPaths 返回的 64 位 hex");
        Assert.AreEqual(".maintenance", MaintenanceCheckpoint.StateDirectoryName);
        Assert.AreEqual("checkpoint.v1.json", MaintenanceCheckpoint.FileName);
        Assert.AreEqual(
            Path.Combine(IndexRoot, ".maintenance", expectedDirectoryName),
            directory,
            "状态目录必须是 <IndexRoot>/.maintenance/<FullTextIndexPaths 推导出的目录名>");
        Assert.AreEqual(Path.Combine(directory, "checkpoint.v1.json"), checkpointPath);

        // checkpoint 绝不落在语料根之内（否则会污染用户仓库并触发 watcher、形成自反馈环）
        Assert.IsFalse(
            FullTextChangeCoalescer.IsWithinScope(ScopeRoot, checkpointPath),
            "checkpoint 不得落在语料根之内");

        // 反向对照：不同语料根必须给出不同状态目录（否则「相等」可能只是函数恒定）
        var otherScope = Path.Combine(TempRoot, "pudding-fts-s1a-scope-2");
        Assert.AreNotEqual(
            directory,
            MaintenanceCheckpoint.ResolveStateDirectory(IndexRoot, otherScope),
            "不同语料根必须映射到不同维护状态目录");
    }

    /// <summary>路径推导是纯计算：索引根不存在也不得创建任何目录（本片零 IO）。</summary>
    [TestMethod]
    public void CheckpointPath_DoesNotRequireOrCreateDirectories()
    {
        var missingIndexRoot = Path.Combine(TempRoot, "pudding-fts-s1a-missing-" + Guid.NewGuid().ToString("N"));

        Assert.IsFalse(Directory.Exists(missingIndexRoot));

        var path = MaintenanceCheckpoint.ResolveCheckpointPath(missingIndexRoot, ScopeRoot);

        Assert.IsTrue(path.StartsWith(missingIndexRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(Directory.Exists(missingIndexRoot), "解析路径不得创建目录");
        Assert.IsFalse(File.Exists(path), "解析路径不得创建文件");
    }

    /// <summary>崩溃残留的临时文件**绝不**可被当作已提交 checkpoint。</summary>
    [TestMethod]
    public void CheckpointTemporaryResidue_IsNeverTreatedAsCommitted()
    {
        var committed = MaintenanceCheckpoint.FileName;
        var temporary = Path.GetFileName(
            MaintenanceCheckpoint.ResolveTemporaryCheckpointPath(IndexRoot, ScopeRoot, "gen42"));

        Assert.AreEqual("checkpoint.v1.json", committed);
        Assert.AreEqual("checkpoint.v1.json.tmp-gen42", temporary);

        Assert.AreEqual(MaintenanceCheckpointFileKind.Committed, MaintenanceCheckpoint.ClassifyFile(committed));
        Assert.IsTrue(MaintenanceCheckpoint.IsCommittedCheckpointFile(committed));

        Assert.AreEqual(MaintenanceCheckpointFileKind.TemporaryResidue, MaintenanceCheckpoint.ClassifyFile(temporary));
        Assert.IsTrue(MaintenanceCheckpoint.IsTemporaryResidue(temporary));
        Assert.IsFalse(
            MaintenanceCheckpoint.IsCommittedCheckpointFile(temporary),
            "写盘中断留下的临时文件绝不可被当作已提交 checkpoint");

        Assert.AreEqual(MaintenanceCheckpointFileKind.Foreign, MaintenanceCheckpoint.ClassifyFile("checkpoint.v2.json"));
        Assert.AreEqual(MaintenanceCheckpointFileKind.Foreign, MaintenanceCheckpoint.ClassifyFile("random.txt"));
        Assert.AreEqual(MaintenanceCheckpointFileKind.Foreign, MaintenanceCheckpoint.ClassifyFile(null));
        Assert.AreEqual(MaintenanceCheckpointFileKind.Foreign, MaintenanceCheckpoint.ClassifyFile("   "));

        // 只取末段：传完整路径也能正确归类；大小写不敏感
        Assert.AreEqual(
            MaintenanceCheckpointFileKind.Committed,
            MaintenanceCheckpoint.ClassifyFile(@"C:\data\fulltext-index\.maintenance\abc\CHECKPOINT.V1.JSON"));
        Assert.AreEqual(
            MaintenanceCheckpointFileKind.TemporaryResidue,
            MaintenanceCheckpoint.ClassifyFile(@"C:\data\.maintenance\abc\checkpoint.v1.json.TMP-3"));
    }

    /// <summary>临时文件 token 必须是纯文件名片段（防路径穿越 / 非法字符）。</summary>
    [TestMethod]
    public void CheckpointTemporaryPath_RejectsPathSeparatorsInToken()
    {
        Assert.Throws<ArgumentException>(
            () => MaintenanceCheckpoint.ResolveTemporaryCheckpointPath(IndexRoot, ScopeRoot, @"..\evil"));

        Assert.Throws<ArgumentException>(
            () => MaintenanceCheckpoint.ResolveTemporaryCheckpointPath(IndexRoot, ScopeRoot, "  "));
    }

    /// <summary>JSON 形状与方案 §2.1 的示例字段一致，且可无损往返。</summary>
    [TestMethod]
    public void CheckpointJson_RoundTripsTheFrozenShape()
    {
        var watermark = new DateTimeOffset(2026, 9, 25, 8, 0, 0, 123, TimeSpan.Zero).AddTicks(4567);
        var checkpoint = new MaintenanceCheckpoint
        {
            Version = MaintenanceCheckpoint.CurrentVersion,
            ScopeRoot = ScopeRoot,
            PolicyFingerprint = "0123456789ab",
            WatermarkUtc = watermark,
            Generation = 42,
            LastCompletedBatchId = "01K-BATCH",
            WrittenUtc = watermark + TimeSpan.FromSeconds(7),
        };

        var json = checkpoint.ToJson();

        StringAssert.Contains(json, "\"version\":1");
        StringAssert.Contains(json, "\"scopeRoot\":");
        StringAssert.Contains(json, "\"policyFingerprint\":\"0123456789ab\"");
        // 协议口径：UTC ISO-8601 + 7 位小数 + 字面 Z（方案 §2.1 的示例形状；时间字段一律 UTC）
        StringAssert.Contains(json, "\"watermarkUtc\":\"2026-09-25T08:00:00.1234567Z\"");
        StringAssert.Contains(json, "\"writtenUtc\":\"2026-09-25T08:00:07.1234567Z\"");
        StringAssert.Contains(json, "\"generation\":42");
        StringAssert.Contains(json, "\"lastCompletedBatchId\":\"01K-BATCH\"");

        Assert.AreEqual(
            MaintenanceCheckpointReadStatus.Ok,
            MaintenanceCheckpoint.TryParse(json, out var parsed));
        Assert.IsNotNull(parsed);
        Assert.AreEqual(checkpoint.Version, parsed!.Version);
        Assert.AreEqual(checkpoint.ScopeRoot, parsed.ScopeRoot);
        Assert.AreEqual(checkpoint.PolicyFingerprint, parsed.PolicyFingerprint);
        Assert.AreEqual(checkpoint.WatermarkUtc, parsed.WatermarkUtc, "ISO 往返必须保留 7 位小数精度与 UTC 偏移");
        Assert.AreEqual(checkpoint.Generation, parsed.Generation);
        Assert.AreEqual(checkpoint.LastCompletedBatchId, parsed.LastCompletedBatchId);
        Assert.AreEqual(checkpoint.WrittenUtc, parsed.WrittenUtc);
    }

    /// <summary>读失败按**陈旧**处理：缺失 / 损坏 / 版本不支持都必须给出明确状态，不得「当作没问题」。</summary>
    [TestMethod]
    public void CheckpointParse_FailsClosedOnMissing_Malformed_AndUnsupportedVersion()
    {
        Assert.AreEqual(MaintenanceCheckpointReadStatus.Missing, MaintenanceCheckpoint.TryParse(null, out var a));
        Assert.IsNull(a);
        Assert.AreEqual(MaintenanceCheckpointReadStatus.Missing, MaintenanceCheckpoint.TryParse("   ", out _));
        Assert.AreEqual(MaintenanceCheckpointReadStatus.Malformed, MaintenanceCheckpoint.TryParse("{ not json", out _));
        Assert.AreEqual(
            MaintenanceCheckpointReadStatus.Malformed,
            MaintenanceCheckpoint.TryParse("{}", out _),
            "必填字段缺失必须判 Malformed（不得默认成当前版本）");
        Assert.AreEqual(
            MaintenanceCheckpointReadStatus.Malformed,
            MaintenanceCheckpoint.TryParse("{\"version\":1,\"scopeRoot\":\"   \",\"generation\":1}", out _),
            "scopeRoot 空白必须判 Malformed");
        Assert.AreEqual(
            MaintenanceCheckpointReadStatus.UnsupportedVersion,
            MaintenanceCheckpoint.TryParse("{\"version\":2,\"scopeRoot\":\"C:\\\\corpus\",\"generation\":1}", out _),
            "版本不支持必须显式返回，不得按当前版本继续");
    }

    /// <summary>
    /// ★ 变异取红 M2 的第二个判别断言（走 checkpoint 层）：推进后的 watermark 必须 = 扫描**开始**时刻。
    /// </summary>
    [TestMethod]
    public void CheckpointAdvance_UsesScanStartAsWatermark_AndIncrementsGeneration()
    {
        var scanFinish = ScanStart + TimeSpan.FromSeconds(30);
        var previous = new MaintenanceCheckpoint
        {
            Version = MaintenanceCheckpoint.CurrentVersion,
            ScopeRoot = ScopeRoot,
            PolicyFingerprint = "fp-1",
            WatermarkUtc = ScanStart - TimeSpan.FromMinutes(15),
            Generation = 41,
            LastCompletedBatchId = "batch-0",
        };

        var next = MaintenanceCheckpoint.Advance(
            previous,
            ScopeRoot,
            policyFingerprint: "fp-1",
            scanStartedUtc: ScanStart,
            scanFinishedUtc: scanFinish,
            batchId: "batch-1",
            writtenUtc: scanFinish);

        Assert.AreEqual(ScanStart, next.WatermarkUtc, "推进后的 watermark 必须取扫描开始时刻（取结束时刻 ⇒ M2 变异）");
        Assert.AreNotEqual(scanFinish, next.WatermarkUtc, "反向对照：结束时刻绝不是我们记录的值");
        Assert.AreEqual(42, next.Generation, "generation 必须递增");
        Assert.AreEqual("batch-1", next.LastCompletedBatchId);
        Assert.AreEqual(MaintenanceCheckpoint.CurrentVersion, next.Version);
        Assert.AreEqual(ScopeRoot, next.ScopeRoot, "scopeRoot 必须逐字盖章");

        // 无旧 checkpoint：generation 从 1 开始
        var fresh = MaintenanceCheckpoint.Advance(
            null, ScopeRoot, null, ScanStart, scanFinish, "batch-fresh", scanFinish);
        Assert.AreEqual(1, fresh.Generation);
        Assert.AreEqual(ScanStart, fresh.WatermarkUtc);
    }

    /// <summary>跨 scope 沿用旧 checkpoint 会漏处理 ⇒ 必须拒绝（fail-closed）。</summary>
    [TestMethod]
    public void CheckpointAdvance_RejectsPreviousFromAnotherScope()
    {
        var otherScope = Path.Combine(TempRoot, "pudding-fts-s1a-scope-other");
        var foreign = new MaintenanceCheckpoint
        {
            Version = MaintenanceCheckpoint.CurrentVersion,
            ScopeRoot = otherScope,
            WatermarkUtc = ScanStart,
            Generation = 7,
        };

        Assert.Throws<InvalidOperationException>(() => MaintenanceCheckpoint.Advance(
            foreign,
            ScopeRoot,
            policyFingerprint: null,
            scanStartedUtc: ScanStart,
            scanFinishedUtc: ScanStart,
            batchId: "batch-x",
            writtenUtc: ScanStart));
    }

    /// <summary>checkpoint 的 scope 匹配必须大小写 / 尾分隔符不敏感，且排除真正的子树。</summary>
    [TestMethod]
    public void CheckpointMatchesScope_IsCaseAndSeparatorInsensitive()
    {
        var checkpoint = new MaintenanceCheckpoint
        {
            Version = MaintenanceCheckpoint.CurrentVersion,
            ScopeRoot = ScopeRoot,
            Generation = 1,
        };

        Assert.IsTrue(MaintenanceCheckpoint.MatchesScope(checkpoint, ScopeRoot.ToUpperInvariant()));
        Assert.IsTrue(MaintenanceCheckpoint.MatchesScope(checkpoint, ScopeRoot + Path.DirectorySeparatorChar));
        Assert.IsFalse(MaintenanceCheckpoint.MatchesScope(checkpoint, Path.Combine(ScopeRoot, "sub")));
        Assert.IsFalse(MaintenanceCheckpoint.MatchesScope(checkpoint, Path.Combine(TempRoot, "pudding-fts-s1a-scope-other")));
    }

    // ── 契约形状（方案 §3.1 / §3.2）──────────────────────────────────────

    /// <summary>
    /// 硬性约束：两个新接口必须**完全独立**，<c>IFullTextSearchEngine</c> 的成员清单逐字不变
    /// （它被 CLI 工程共同实现，加成员会破坏 CLI 编译 —— CLI 是红线）。
    /// </summary>
    [TestMethod]
    public void MaintenanceContracts_DoNotExtendTheExistingSearchEngineContracts()
    {
        var searchEngine = typeof(IFullTextSearchEngine);
        var maintenanceEngine = typeof(IFullTextIndexMaintenanceEngine);
        var maintenance = typeof(IFullTextIndexMaintenance);

        Assert.AreEqual(0, searchEngine.GetInterfaces().Length, "既有搜索接口不得新增基接口");
        Assert.AreEqual(0, maintenanceEngine.GetInterfaces().Length, "维护执行接口必须完全独立");
        Assert.AreEqual(0, maintenance.GetInterfaces().Length, "维护生命周期接口必须完全独立");

        CollectionAssert.AreEquivalent(
            new[] { "HasIndex", "SearchAsync", "BuildIndexAsync", "RemoveIndex" },
            searchEngine.GetMethods().Select(m => m.Name).ToArray(),
            "IFullTextSearchEngine 的成员清单必须逐字不变");

        CollectionAssert.AreEquivalent(
            new[] { "ApplyChangesAsync", "EnumerateIndexedPathsAsync", "ProbeIntegrityAsync" },
            maintenanceEngine.GetMethods().Select(m => m.Name).ToArray(),
            "IFullTextIndexMaintenanceEngine 必须恰好是方案 §3.2 的 3 个成员");

        CollectionAssert.AreEquivalent(
            new[] { "StartAsync", "StopAsync", "RequestRecoveryScanAsync", "GetSnapshot" },
            maintenance.GetMethods().Select(m => m.Name).ToArray(),
            "IFullTextIndexMaintenance 必须恰好是方案 §3.2 的 4 个成员");

        // DTO 一律 sealed record（契约不可变）
        foreach (var type in new[]
                 {
                     typeof(FullTextFileChange), typeof(FullTextChangeSet),
                     typeof(FullTextMutationBudget), typeof(FullTextMutationResult),
                     typeof(IndexedPathEntry), typeof(FullTextIndexIntegrityProbe),
                     typeof(FullTextMaintenanceScope), typeof(FullTextMaintenanceScopeSnapshot),
                     typeof(FullTextMaintenanceSnapshot),
                 })
        {
            Assert.IsTrue(type.IsSealed, $"{type.Name} 必须是 sealed record");
            Assert.IsTrue(
                type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEquatable<>)),
                $"{type.Name} 必须是 record（应实现 IEquatable<T>）");
        }
    }

    /// <summary>统一变更集的形状与方案 §3.1 一致（含「缺失水位线与空串必须可区分」）。</summary>
    [TestMethod]
    public void ChangeSet_ExposesTheFrozenShape()
    {
        var file = Path.Combine(ScopeRoot, "src", "a.cs");
        var change = new FullTextFileChange(
            file,
            FullTextChangeKind.Upsert,
            LastWriteUtc: ScanStart,
            Length: 12,
            Sources: FullTextChangeSource.Watcher);

        var set = new FullTextChangeSet(
            "batch-1",
            ScopeRoot,
            "scope-key",
            new[] { change },
            ScanStart,
            PreviousWatermarkUtc: null,
            RequiresCheckpointAdvance: false);

        Assert.AreEqual("batch-1", set.BatchId);
        Assert.AreEqual(ScopeRoot, set.ScopeRoot);
        Assert.AreEqual("scope-key", set.ScopeKey);
        Assert.AreEqual(1, set.Changes.Count);
        Assert.AreEqual(FullTextChangeKind.Upsert, set.Changes[0].Kind);
        Assert.AreEqual(ScanStart, set.Changes[0].LastWriteUtc);
        Assert.AreEqual(12, set.Changes[0].Length);
        Assert.AreEqual(FullTextChangeSource.Watcher, set.Changes[0].Sources);
        Assert.AreEqual(ScanStart, set.ScanStartedUtc);
        Assert.IsNull(set.PreviousWatermarkUtc, "无 checkpoint ⇒ null，而不是某个伪造的时间戳");
        Assert.IsFalse(set.RequiresCheckpointAdvance);

        // 读不到 mtime ⇒ null（与「长度 0 / 空串」必须可区分）
        var unreadable = new FullTextFileChange(file, FullTextChangeKind.Delete, null, null, FullTextChangeSource.MTimeRecovery);
        Assert.IsNull(unreadable.LastWriteUtc);
        Assert.IsNull(unreadable.Length);
    }

    /// <summary>来源位标与动作枚举的取值逐字冻结（位或合并依赖它，改了会静默改变诊断语义）。</summary>
    [TestMethod]
    public void ChangeSources_HaveTheFrozenFlagValues()
    {
        Assert.AreEqual(1, (int)FullTextChangeSource.Watcher);
        Assert.AreEqual(2, (int)FullTextChangeSource.MTimeRecovery);
        Assert.AreEqual(4, (int)FullTextChangeSource.IntegrityCheck);
        Assert.AreEqual(5, (int)(FullTextChangeSource.Watcher | FullTextChangeSource.IntegrityCheck));

        Assert.AreEqual(0, (int)FullTextChangeKind.Upsert);
        Assert.AreEqual(1, (int)FullTextChangeKind.Delete);
    }

    /// <summary>预算判定是**集合口径**：剩余量 = MaxIndexBytes − LiveIndexBytes，且钳到非负。</summary>
    [TestMethod]
    public void MutationBudget_RemainingBytesFollowsTheAggregateRule()
    {
        Assert.AreEqual(300, new FullTextMutationBudget(MaxIndexBytes: 1000, LiveIndexBytes: 700, MaxPaths: 512).RemainingBytes);
        Assert.AreEqual(0, new FullTextMutationBudget(MaxIndexBytes: 1000, LiveIndexBytes: 1000, MaxPaths: 512).RemainingBytes);
        Assert.AreEqual(
            0,
            new FullTextMutationBudget(MaxIndexBytes: 1000, LiveIndexBytes: 1200, MaxPaths: 512).RemainingBytes,
            "已超限时剩余量必须钳到 0，不得为负");
    }
}
