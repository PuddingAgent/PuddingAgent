using System.Diagnostics;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Maintenance;
using Directory = System.IO.Directory;

namespace PuddingFullTextIndexTests;

/// <summary>
/// S3d：<see cref="LuceneFullTextIndexMaintenance"/>（维护循环本体）的组件侧断言 ——
/// 三源（watcher / mtime 补偿 / 体检）汇成同一份按路径变更集，交给执行层，并按
/// <c>CheckpointAdvancePolicy</c> 推进 checkpoint。
/// <para>
/// 本文件覆盖 M1~M6、M9~M12（M7 探针三态与 M8 体检退避在 <c>MaintenanceIntegrityTests.cs</c>）；
/// 测试名即任务书 §2 的映射表。全部语料 / 索引 / checkpoint 落在 <c>%TEMP%</c>（<see cref="MaintenanceRig"/> 构造期硬断言），
/// 本文件零 git 操作、不接宿主、不重启宿主。
/// </para>
/// <para>
/// 确定性来源：冻结业务时钟（<see cref="FakeTimeProvider"/>）+ 长去抖窗口 ⇒ 泵任务永不自动 flush，
/// 测试用 internal 驱动接缝（<c>FlushPendingAsync</c> / <c>RunRecoveryScanAsync</c> / <c>RunHealthCheckSliceAsync</c>）
/// 显式推进，因此「合并成一次处理」「未变文件不重复写」都是**确定**断言而不是 sleep 竞态。
/// </para>
/// </summary>
[TestClass]
public sealed class MaintenanceLoopTests
{
    public TestContext TestContext { get; set; } = null!;

    private static readonly TimeSpan RoutineWait = TimeSpan.FromSeconds(30);

    // ── M1：默认关闭零副作用 ─────────────────────────────────────────────

    /// <summary>
    /// M1：未 <c>StartAsync</c> 时**不得**解析 / 探测 scope、不得访问索引根、不得创建 checkpoint 目录、
    /// 不得创建 watcher / 线程、不得获取租约。证据 = 目录不存在（连创建都没有）+ 计数探针全 0 + 执行层 0 次调用。
    /// </summary>
    [TestMethod]
    public void M1_DefaultOff_CreatesNothingAndTouchesNothing()
    {
        using var rig = new MaintenanceRig("m1");
        rig.WriteCorpusFile("a.txt", "zzm1alpha");

        var snapshot = rig.Maintenance.GetSnapshot();
        Assert.IsFalse(snapshot.Running, "未 StartAsync ⇒ Running 必须为 false");
        Assert.AreEqual(0, snapshot.Scopes.Count, "未 StartAsync ⇒ Scopes 必须为空");
        Assert.IsNull(snapshot.StartedUtc);
        Assert.IsNull(snapshot.StoppedUtc);

        // ★ 零副作用：连「创建」都没发生（更不用说访问）
        Assert.IsFalse(Directory.Exists(rig.IndexRoot), "未 StartAsync ⇒ 不得创建 / 访问索引根");
        Assert.IsFalse(Directory.Exists(rig.StateDirectory), "未 StartAsync ⇒ 不得创建 checkpoint 目录");
        Assert.IsFalse(
            Directory.Exists(Path.Combine(rig.IndexRoot, ".supply-leases")),
            "未 StartAsync ⇒ 不得获取跨进程租约（租约目录存在即为已获取）");

        var diagnostics = rig.Maintenance.GetDiagnostics();
        Assert.AreEqual(0, diagnostics.WatchersCreated, "未 StartAsync ⇒ 不得创建 watcher");
        Assert.AreEqual(0, diagnostics.LiveWatchers);
        Assert.AreEqual(0, diagnostics.PumpTasksCreated, "未 StartAsync ⇒ 不得创建泵任务");
        Assert.AreEqual(0, diagnostics.LivePumpTasks);
        Assert.IsFalse(diagnostics.HealthThreadRunning, "未 StartAsync ⇒ 不得启动体检线程");
        Assert.AreEqual(0, rig.Engine.BatchCount, "未 StartAsync ⇒ 不得调用执行层");

        // 对照：取两次快照都不应产生任何副作用（不是「第一次恰好没做」）
        _ = rig.Maintenance.GetSnapshot();
        Assert.IsFalse(Directory.Exists(rig.IndexRoot));
        Assert.AreEqual(0, rig.Engine.BatchCount);
    }

    /// <summary>
    /// M1b：<c>MaintenanceOptions.Enabled = false</c> 时，即使调用方显式 <c>StartAsync</c> 也必须是空操作
    /// （契约「未显式 StartAsync（**或配置开关为 false**）时零副作用」的第二种形态）。
    /// </summary>
    [TestMethod]
    public async Task M1b_DisabledOption_StartAsyncIsANoOpWithZeroSideEffects()
    {
        using var rig = new MaintenanceRig("m1b", o => o with { Enabled = false });
        rig.WriteCorpusFile("a.txt", "zzm1balpha");

        await rig.Maintenance.StartAsync(rig.Scopes);

        var snapshot = rig.Maintenance.GetSnapshot();
        Assert.IsFalse(snapshot.Running, "开关关闭 ⇒ 不得进入运行态");
        Assert.AreEqual(0, snapshot.Scopes.Count);
        StringAssert.Contains(snapshot.LastError, "Enabled", "必须如实说明为什么没启动");

        Assert.IsFalse(Directory.Exists(rig.IndexRoot));
        Assert.IsFalse(Directory.Exists(rig.StateDirectory));

        var diagnostics = rig.Maintenance.GetDiagnostics();
        Assert.AreEqual(0, diagnostics.WatchersCreated);
        Assert.AreEqual(0, diagnostics.PumpTasksCreated);
        Assert.IsFalse(diagnostics.HealthThreadRunning);
        Assert.AreEqual(0, rig.Engine.BatchCount);
    }

    // ── M2：生命周期幂等 / 可重入 ────────────────────────────────────────

    /// <summary>
    /// M2：连续两次 <c>StartAsync</c> ⇒ 不重复创建 watcher / 泵任务（计数探针断言）；
    /// 连续两次 <c>StopAsync</c> 不抛；<c>StartAsync → StopAsync → StartAsync</c> 可重入；
    /// 停止后 live watcher / live 泵任务归零（无残留）。
    /// </summary>
    [TestMethod]
    public async Task M2_Lifecycle_IsIdempotentAndReentrant()
    {
        using var rig = new MaintenanceRig("m2");
        rig.WriteCorpusFile("a.txt", "zzm2alpha");
        Assert.IsTrue(rig.BuildInitialIndex().Success, "初始索引必须建成功（否则补偿轮次会被「索引不存在」短路）");

        var threadsBefore = Process.GetCurrentProcess().Threads.Count;

        await rig.Maintenance.StartAsync(rig.Scopes);
        await rig.Maintenance.StartAsync(rig.Scopes);

        var first = rig.Maintenance.GetDiagnostics();
        Assert.AreEqual(1, first.WatchersCreated, "两次 StartAsync 只能创建 1 个 watcher（幂等）");
        Assert.AreEqual(1, first.PumpTasksCreated, "两次 StartAsync 只能创建 1 条泵任务（幂等）");
        Assert.AreEqual(1, first.LiveWatchers);
        Assert.AreEqual(1, first.LivePumpTasks);
        Assert.IsTrue(first.HealthThreadRunning, "有 scope ⇒ 体检线程必须起来");
        Assert.AreEqual(ThreadPriority.BelowNormal, first.HealthThreadPriority, "体检线程必须是低优先级");
        Assert.IsTrue(rig.Maintenance.GetSnapshot().Running);
        Assert.AreEqual(1, rig.Maintenance.GetSnapshot().Scopes.Count, "同一 scope 不得被注册两次");

        await rig.Maintenance.StopAsync();
        await rig.Maintenance.StopAsync();

        var stopped = rig.Maintenance.GetDiagnostics();
        Assert.AreEqual(0, stopped.LiveWatchers, "StopAsync 后必须无残留 watcher");
        Assert.AreEqual(0, stopped.LivePumpTasks, "StopAsync 后必须无残留泵任务");
        Assert.IsFalse(stopped.HealthThreadRunning, "StopAsync 后体检线程必须已退出");
        Assert.IsFalse(rig.Maintenance.GetSnapshot().Running);
        Assert.AreEqual(0, rig.Maintenance.GetSnapshot().Scopes.Count);
        Assert.IsNotNull(rig.Maintenance.GetSnapshot().StoppedUtc);

        await rig.Maintenance.StartAsync(rig.Scopes);
        var restarted = rig.Maintenance.GetDiagnostics();
        Assert.AreEqual(1, restarted.LiveWatchers, "可重入：再次 StartAsync 必须重新起来");
        Assert.AreEqual(1, restarted.LivePumpTasks);
        Assert.AreEqual(2, restarted.PumpTasksCreated, "重入是新的一条泵任务（不是复用上一轮的）");
        Assert.AreEqual(2, restarted.WatchersCreated);

        await rig.Maintenance.StopAsync();
        Assert.AreEqual(0, rig.Maintenance.GetDiagnostics().LivePumpTasks);

        // 线程基线的**对照证据**（进程线程数不是硬断言：线程池会自行涨落，硬断言会变成 flaky 测试）：
        // 硬断言由「自有计数归零」（LiveWatchers / LivePumpTasks / HealthThreadRunning）与
        // 「整树可删」（Windows 上未释放的句柄会阻止删除）共同承担。
        var threadsAfter = Process.GetCurrentProcess().Threads.Count;
        TestContext.WriteLine($"S3D_THREAD_BASELINE before_start={threadsBefore} after_stop={threadsAfter}");
        Assert.IsTrue(
            threadsAfter <= threadsBefore + 4,
            $"StopAsync 后线程数必须回到基线附近（before={threadsBefore} after={threadsAfter}）");

        // 结构证明：Windows 上未释放的 watcher / reader 句柄会阻止删除 ⇒ 能删掉 = 真的释放了
        Directory.Delete(rig.Root, recursive: true);
        Assert.IsFalse(Directory.Exists(rig.Root), "StopAsync 后测试根必须可以被整树删除（无残留句柄）");
    }

    // ── M3：watcher 低延迟收集 + 折叠 ────────────────────────────────────

    /// <summary>
    /// M3：同文件多次快速写入 ⇒ **合并成一次**处理；rename（旧名消失 + 新名出现）⇒ 折叠成
    /// 「新名 Upsert + 旧名 Delete」而**不是**两条独立事件（同批 2 条变更，恰好 1 个批次）。
    /// </summary>
    [TestMethod]
    public async Task M3_WatcherEvents_AreCoalescedPerPath_AndRenameFoldsToDeletePlusUpsert()
    {
        using var rig = new MaintenanceRig("m3");
        var a = rig.WriteCorpusFile("a.txt", "zzm3aoldtext");

        Assert.IsTrue(rig.BuildInitialIndex().Success);
        await rig.Maintenance.StartAsync(rig.Scopes);
        Assert.IsTrue(await rig.Maintenance.WaitUntilIdleAsync(RoutineWait), "Startup 补偿必须排空");

        var batchesBefore = rig.Engine.BatchCount;

        // ① 同文件三次快速写入（模拟 watcher 三连事件）⇒ 折叠成一次处理
        var changed = rig.WriteCorpusFile("a.txt", "zzm3anewtext");
        rig.Maintenance.SubmitPathObservation(rig.Corpus, changed);
        rig.Maintenance.SubmitPathObservation(rig.Corpus, changed);
        rig.Maintenance.SubmitPathObservation(rig.Corpus, changed);

        Assert.AreEqual(1, rig.ScopeSnapshot().PendingChangeCount, "同一路径的三次事件必须折叠成 1 条待处理路径");

        Assert.IsTrue(await rig.Maintenance.FlushPendingAsync(rig.Corpus), "flush 必须产生变更");
        Assert.AreEqual(batchesBefore + 1, rig.Engine.BatchCount, "三次事件必须恰好产生**一个**批次");

        var coalescedBatch = rig.Engine.ChangeSets[^1];
        Assert.AreEqual(1, coalescedBatch.Changes.Count, "合并后只应有 1 条变更");
        Assert.AreEqual(changed, coalescedBatch.Changes[0].FullPath);
        Assert.AreEqual(FullTextChangeKind.Upsert, coalescedBatch.Changes[0].Kind);
        Assert.AreEqual(FullTextChangeSource.Watcher, coalescedBatch.Changes[0].Sources);
        Assert.AreEqual(1, await rig.CountHitsAsync("zzm3anewtext"), "合并后的内容必须最终可见");

        // ② rename ⇒ 旧名 Delete + 新名 Upsert（同批 2 条，而不是两条独立事件）
        var oldPath = rig.WriteCorpusFile("old.txt", "zzm3renameoldtext");
        Assert.IsTrue(await rig.Maintenance.RunRecoveryScanAsync(rig.Corpus, FullTextRecoveryReason.Interval));
        Assert.AreEqual(1, await rig.CountHitsAsync("zzm3renameoldtext"));

        var newPath = rig.CorpusFile("new.txt");
        File.Move(oldPath, newPath);

        var batchesBeforeRename = rig.Engine.BatchCount;
        rig.Maintenance.SubmitPathObservation(rig.Corpus, oldPath);
        rig.Maintenance.SubmitPathObservation(rig.Corpus, newPath);
        Assert.AreEqual(2, rig.ScopeSnapshot().PendingChangeCount, "两个不同路径必须各占一条待处理项");

        Assert.IsTrue(await rig.Maintenance.FlushPendingAsync(rig.Corpus));
        Assert.AreEqual(batchesBeforeRename + 1, rig.Engine.BatchCount, "rename 的两侧必须落在**同一个**批次里");

        var renameBatch = rig.Engine.ChangeSets[^1];
        Assert.AreEqual(2, renameBatch.Changes.Count, "rename 必须折叠成 2 条最终动作（旧名 Delete + 新名 Upsert）");
        Assert.IsTrue(
            renameBatch.Changes.Any(c => c.FullPath == oldPath && c.Kind == FullTextChangeKind.Delete),
            "旧名必须产出 Delete");
        Assert.IsTrue(
            renameBatch.Changes.Any(c => c.FullPath == newPath && c.Kind == FullTextChangeKind.Upsert),
            "新名必须产出 Upsert");

        var inventory = await rig.InventoryAsync();
        Assert.IsFalse(
            inventory.Any(e => e.NormalizedPath == MaintenanceRig.Normalize(oldPath)),
            "旧名必须已从索引清册消失");
        Assert.IsTrue(
            inventory.Any(e => e.NormalizedPath == MaintenanceRig.Normalize(newPath)),
            "新名必须已进入索引清册");
        Assert.AreEqual(1, await rig.CountHitsAsync("zzm3renameoldtext"), "rename 后内容必须仍恰好命中 1 次");

        await rig.Maintenance.StopAsync();
    }

    /// <summary>
    /// M3b：**真实** <c>FileSystemWatcher</c> 的端到端接线（不是只测内部投递接缝）：
    /// 写文件 ⇒ 在有界等待内**最终可见**（S3c 已证明不能依赖「读者看不到新 commit」做可见性断言）。
    /// </summary>
    [TestMethod]
    public async Task M3b_RealFileSystemWatcher_DeliversChangesToTheIndex()
    {
        using var rig = new MaintenanceRig(
            "m3b",
            o => o with { Debounce = TimeSpan.FromMilliseconds(80), MaxCoalesceWait = TimeSpan.FromMilliseconds(400) },
            useSystemClock: true);

        Assert.IsTrue(rig.BuildInitialIndex().Success);
        await rig.Maintenance.StartAsync(rig.Scopes);

        const string marker = "zzm3bwatchermarker";
        rig.WriteCorpusFile("watched.txt", marker);

        var visible = await rig.WaitUntilAsync(
            async () => await rig.CountHitsAsync(marker) == 1 && rig.Engine.BatchCount >= 1,
            TimeSpan.FromSeconds(30));

        // 两个条件必须放在**同一个**等待里：commit 落盘与「记录器登记该批次」之间有一个极短的窗口
        // （可见性由引擎提交后失效 reader 达成，而记录在调用返回后），分开断言会撞上这个窗口。
        Assert.IsTrue(visible, "真实 watcher 的写入必须在有界等待内最终可见，并且留下至少一个批次");

        await rig.Maintenance.StopAsync();
    }

    // ── M4：mtime checkpoint 补偿（独立可达 + 未变不重写）────────────────

    /// <summary>
    /// M4：watcher 未收到事件时，改文件 ⇒ <c>RequestRecoveryScanAsync(…, Interval)</c> 后**仍能**写进索引
    /// （补偿是独立可达路径）；未变的文件**不**被重复写（按引擎入参的路径集合断言）。
    /// </summary>
    [TestMethod]
    public async Task M4_RecoveryScan_IsAnIndependentPath_AndUnchangedFilesAreNotRewritten()
    {
        using var rig = new MaintenanceRig("m4");
        var a = rig.WriteCorpusFile("a.txt", "zzm4aold");
        rig.WriteCorpusFile("b.txt", "zzm4bold");
        rig.WriteCorpusFile("c.txt", "zzm4cold");
        Assert.IsTrue(rig.BuildInitialIndex().Success);

        await rig.Maintenance.StartAsync(rig.Scopes);
        Assert.IsTrue(await rig.Maintenance.WaitUntilIdleAsync(RoutineWait), "Startup 补偿必须排空");

        var afterStartup = rig.ScopeSnapshot();
        Assert.IsNotNull(afterStartup.WatermarkUtc, "Startup 补偿必须建立水位线");
        Assert.IsTrue(afterStartup.Generation >= 1, "Startup 补偿必须推进 generation");

        // ① 未变 ⇒ 不重复写：本轮**不得**产生任何批次
        var batchesBefore = rig.Engine.BatchCount;
        Assert.IsTrue(await rig.Maintenance.RunRecoveryScanAsync(rig.Corpus, FullTextRecoveryReason.Interval));
        Assert.AreEqual(batchesBefore, rig.Engine.BatchCount, "未变的文件不得被重复写（本轮必须零批次）");
        Assert.AreEqual(
            batchesBefore,
            rig.Engine.ChangeSets.Count,
            "零变更 ⇒ 连空批次都不该产生（空变更集不开 writer、不 commit）");

        // ② 改一个文件（不给 watcher 任何事件 = 模拟「事件已丢」）⇒ 补偿必须独立把它写进索引
        //   ⚠️ 顺序要紧：先写内容（mtime = 真实现在），再把 mtime 拨到业务时钟时刻
        rig.WriteCorpusFile("a.txt", "zzm4anewtext");
        rig.TouchAfterWatermark(a);

        Assert.IsTrue(await rig.Maintenance.RunRecoveryScanAsync(rig.Corpus, FullTextRecoveryReason.Interval));

        var recoveryBatch = rig.Engine.LastBatchWithSource(FullTextChangeSource.MTimeRecovery);
        Assert.IsNotNull(recoveryBatch, "补偿扫描必须产出 MTimeRecovery 来源的变更集");
        Assert.AreEqual(1, recoveryBatch!.Changes.Count, "只有 a.txt 变化 ⇒ 恰好 1 条变更（未变文件不重复写）");
        Assert.AreEqual(a, recoveryBatch.Changes[0].FullPath);
        Assert.AreEqual(FullTextChangeKind.Upsert, recoveryBatch.Changes[0].Kind);
        Assert.IsNotNull(recoveryBatch.ScanStartedUtc, "变更集必须带上「扫描开始时刻」（watermark 的唯一合法取值）");
        Assert.AreEqual(1, await rig.CountHitsAsync("zzm4anewtext"), "补偿路径必须真的把新内容写进索引");

        await rig.Maintenance.StopAsync();
    }

    // ── M5 ★：关闭 → 改文件 → 重开 ⇒ 恢复 ───────────────────────────────

    /// <summary>
    /// ★ M5（§S3 完成标准原文「Core 停止情形通过『关闭维护器→修改文件→重开维护器』恢复」）：
    /// 本片最重要的用例 —— 它就是「宿主停机期不丢更新」的可测形式。
    /// </summary>
    [TestMethod]
    public async Task M5_StopModifyRestart_RecoversAndIndexMatchesDisk()
    {
        using var rig = new MaintenanceRig("m5");
        var keep = rig.WriteCorpusFile("keep.txt", "zzm5keeptext");
        var changed = rig.WriteCorpusFile("changed.txt", "zzm5beforetext");
        var removed = rig.WriteCorpusFile("removed.txt", "zzm5removedtext");
        Assert.IsTrue(rig.BuildInitialIndex().Success);

        // ── 关前状态 ──
        await rig.Maintenance.StartAsync(rig.Scopes);
        Assert.IsTrue(await rig.Maintenance.WaitUntilIdleAsync(RoutineWait), "Startup 补偿必须排空");

        Assert.AreEqual(1, await rig.CountHitsAsync("zzm5beforetext"));
        Assert.AreEqual(1, await rig.CountHitsAsync("zzm5removedtext"));
        Assert.AreEqual(1, await rig.CountHitsAsync("zzm5keeptext"));
        var beforeStop = rig.ScopeSnapshot();
        Assert.IsNotNull(beforeStop.WatermarkUtc);
        Assert.IsTrue(File.Exists(rig.CheckpointPath), "Startup 补偿必须把 checkpoint 落盘");

        await rig.Maintenance.StopAsync();
        Assert.IsFalse(rig.Maintenance.GetSnapshot().Running);

        // ── 停机期：改 / 删 / 新增（维护器不存在 ⇒ 没有任何 watcher / 线程在跑） ──
        rig.Advance(TimeSpan.FromMinutes(30));
        rig.WriteCorpusFile("changed.txt", "zzm5aftertext");
        rig.TouchAfterWatermark(changed);
        File.Delete(removed);
        var added = rig.WriteCorpusFile("added.txt", "zzm5addedtext");
        rig.TouchAfterWatermark(added);

        // ── 重开 ⇒ Startup 补偿必须恢复 ──
        await rig.Maintenance.StartAsync(rig.Scopes);
        Assert.IsTrue(
            await rig.Maintenance.WaitUntilIdleAsync(RoutineWait),
            "重开后的 Startup 补偿必须在有界时间内排空");

        Assert.AreEqual(0, await rig.CountHitsAsync("zzm5beforetext"), "停机期被改写 ⇒ 旧内容必须不再命中");
        Assert.AreEqual(1, await rig.CountHitsAsync("zzm5aftertext"), "停机期的修改必须被恢复");
        Assert.AreEqual(0, await rig.CountHitsAsync("zzm5removedtext"), "停机期删除的文件必须从索引移除");
        Assert.AreEqual(1, await rig.CountHitsAsync("zzm5addedtext"), "停机期新增的文件必须进入索引");
        Assert.AreEqual(1, await rig.CountHitsAsync("zzm5keeptext"), "未动过的文件必须仍在索引里（对照组）");

        // ── 索引与磁盘一致（只读探针与维护侧共用同一走查器 / 同一可索引判定） ──
        var probe = await rig.InnerEngine.ProbeIntegrityAsync(rig.Corpus);
        Assert.AreEqual(FullTextIndexIntegrityState.Healthy, probe.State, $"重开后索引必须与磁盘一致：{probe.Message}");
        Assert.AreEqual(3, probe.IndexedPathCount, "keep.txt + changed.txt + added.txt");

        var afterRestart = rig.ScopeSnapshot();
        Assert.IsTrue(
            afterRestart.Generation > beforeStop.Generation,
            $"重开后的补偿轮次必须推进 generation（{beforeStop.Generation} → {afterRestart.Generation}）");

        await rig.Maintenance.StopAsync();
    }

    // ── M6：checkpoint 只在允许时推进 + 失败重放 ─────────────────────────

    /// <summary>
    /// M6：制造一次 <c>PartiallyApplied</c>（提取失败）⇒ 水位线与 generation **原地不动**、
    /// 旧文档保留、下一轮**重放**该路径并被修复（修复后才推进）。
    /// </summary>
    [TestMethod]
    public async Task M6_ExtractionFailure_KeepsOldDocuments_AndNeverAdvancesTheCheckpoint_ThenReplays()
    {
        var extractor = new ScriptedFactsExtractor();
        using var rig = new MaintenanceRig("m6", extractors: new IFileContentExtractor[] { extractor });

        var pdf = rig.WriteCorpusFile("doc.pdf", "%PDF-1.4 placeholder");
        extractor.Set(pdf, "pdfzzoldline");
        rig.WriteCorpusFile("keep.txt", "zzm6keeptext");
        Assert.IsTrue(rig.BuildInitialIndex().Success);

        await rig.Maintenance.StartAsync(rig.Scopes);
        Assert.IsTrue(await rig.Maintenance.WaitUntilIdleAsync(RoutineWait), "Startup 补偿必须排空");

        var baseline = rig.ScopeSnapshot();
        Assert.IsNotNull(baseline.WatermarkUtc);
        Assert.IsTrue(rig.Engine.BatchCount >= 1);

        // 让 doc.pdf 的提取必然失败，并让它「刚变过」（水位线判定需要它）
        extractor.FailingPaths.Add(pdf);
        rig.TouchAfterWatermark(pdf);

        Assert.IsFalse(
            await rig.Maintenance.RunRecoveryScanAsync(rig.Corpus, FullTextRecoveryReason.Interval),
            "有保留项的轮次**绝不能**推进 checkpoint");

        var failed = rig.ScopeSnapshot();
        Assert.AreEqual(baseline.WatermarkUtc, failed.WatermarkUtc, "水位线必须原地不动");
        Assert.AreEqual(baseline.Generation, failed.Generation, "generation 必须原地不动");

        var failedResult = rig.Engine.Results[^1];
        Assert.AreEqual(FullTextMutationState.PartiallyApplied, failedResult.State, failedResult.Message);
        Assert.AreEqual(1, failedResult.RetainedOldCount);
        Assert.IsFalse(failedResult.CheckpointAdvanced);
        Assert.AreEqual(1, await rig.CountHitsAsync("pdfzzoldline"), "★ 提取失败必须保留旧文档（不得删旧）");

        // 修复 ⇒ 下一轮重放同一路径并把新内容写进索引，随后才推进
        extractor.FailingPaths.Clear();
        extractor.Set(pdf, "pdfzznewline");

        Assert.IsTrue(
            await rig.Maintenance.RunRecoveryScanAsync(rig.Corpus, FullTextRecoveryReason.Interval),
            "修复后的重放必须成功并推进 checkpoint");

        Assert.AreEqual(1, await rig.CountHitsAsync("pdfzznewline"), "重放必须把新内容写进索引");
        Assert.AreEqual(0, await rig.CountHitsAsync("pdfzzoldline"), "重放成功后旧内容必须不再命中");

        var repaired = rig.ScopeSnapshot();
        Assert.IsTrue(repaired.Generation > baseline.Generation, "成功轮次必须推进 generation");

        await rig.Maintenance.StopAsync();
    }

    /// <summary>
    /// ★ M6b（★M-R1 靶点）：文件**暂时读不到**时，维护层必须把它归到「保留旧文档 + 待重试」，
    /// **绝不**产出任何动作（尤其不能是 Delete）。判据是「旧文档仍在索引里 + 清册里该路径仍在 +
    /// 执行层一次都没被调用」。
    /// </summary>
    [TestMethod]
    public async Task M6b_UnreadableFile_IsDeferred_KeepsOldDocuments_AndProducesNoDelete()
    {
        using var rig = new MaintenanceRig("m6b");
        var a = rig.WriteCorpusFile("a.txt", "zzm6boldtext");
        Assert.IsTrue(rig.BuildInitialIndex().Success);

        await rig.Maintenance.StartAsync(rig.Scopes);
        Assert.IsTrue(await rig.Maintenance.WaitUntilIdleAsync(RoutineWait), "Startup 补偿必须排空");

        var batchesBefore = rig.Engine.BatchCount;
        Assert.AreEqual(1, await rig.CountHitsAsync("zzm6boldtext"));

        // 独占句柄：连最宽松的读都拿不到 ⇒ 维护层必须判「不可读」
        using (var exclusive = new FileStream(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            rig.Maintenance.SubmitPathObservation(rig.Corpus, a);
            Assert.AreEqual(1, rig.ScopeSnapshot().PendingChangeCount, "该路径必须进入折叠缓冲");

            var flushed = await rig.Maintenance.FlushPendingAsync(rig.Corpus);

            // ★★ 本用例的**主判据**（★M-R1 靶点）：读不到 ⇒ 旧文档必须原样保留。
            //   先断言它，是为了让「把不可读当不存在」的回归**恰好红在这一行**。
            Assert.AreEqual(1, await rig.CountHitsAsync("zzm6boldtext"), "★ 读不到绝不能删掉旧文档");
            Assert.IsTrue(
                (await rig.InventoryAsync()).Any(e => e.NormalizedPath == MaintenanceRig.Normalize(a)),
                "★ 该路径必须仍在索引清册里");

            Assert.IsFalse(flushed, "不可读 ⇒ 必须产出**空批**（不产出任何动作）");
            Assert.AreEqual(
                batchesBefore,
                rig.Engine.BatchCount,
                "不可读 ⇒ 绝不允许向执行层投递任何变更集（尤其不能是 Delete）");
        }

        StringAssert.Contains(rig.ScopeSnapshot().LastMessage, "不可读", "必须如实说明「不可读」");
        Assert.AreEqual(1, rig.Maintenance.GetDiagnostics().DeferredObservations);

        await rig.Maintenance.StopAsync();
    }

    // ── M9：连续 N 轮 RetainedOld ⇒ 告警（且绝不自动跳过）────────────────

    /// <summary>
    /// M9（用户裁定③）：同一路径连续 N 轮 <c>RetainedOld</c> ⇒ 产生告警（N 来自
    /// <see cref="MaintenanceOptions.RetainedOldWarnThreshold"/>），并且**每一轮仍然重放该文件**
    /// —— 判据是「允许成功文件被重复处理，漏掉文件不允许」，告警**不得**被实现成「自动跳过」。
    /// </summary>
    [TestMethod]
    public async Task M9_RepeatedRetainedOld_WarnsWithoutSkippingTheFile()
    {
        const int threshold = 3;
        var extractor = new ScriptedFactsExtractor();
        using var rig = new MaintenanceRig(
            "m9",
            o => o with { RetainedOldWarnThreshold = threshold },
            extractors: new IFileContentExtractor[] { extractor });

        var pdf = rig.WriteCorpusFile("doc.pdf", "%PDF-1.4 placeholder");
        extractor.Set(pdf, "zzm9oldline");
        Assert.IsTrue(rig.BuildInitialIndex().Success);

        await rig.Maintenance.StartAsync(rig.Scopes);
        Assert.IsTrue(await rig.Maintenance.WaitUntilIdleAsync(RoutineWait), "Startup 补偿必须排空");

        extractor.FailingPaths.Add(pdf);

        for (var round = 1; round <= threshold; round++)
        {
            rig.TouchAfterWatermark(pdf);

            Assert.IsFalse(
                await rig.Maintenance.RunRecoveryScanAsync(rig.Corpus, FullTextRecoveryReason.Interval),
                $"第 {round} 轮有保留项 ⇒ 不得推进 checkpoint");

            var batch = rig.Engine.ChangeSets[^1];
            Assert.IsTrue(
                batch.Changes.Any(c => c.FullPath == pdf),
                $"★ 第 {round} 轮必须仍然**重放** {pdf}（告警绝不等于自动跳过）");

            if (round < threshold)
                Assert.AreEqual(0, rig.Maintenance.GetDiagnostics().RetainedOldWarnings, $"第 {round} 轮还不该告警");
        }

        Assert.AreEqual(
            1,
            rig.Maintenance.GetDiagnostics().RetainedOldWarnings,
            $"连续 {threshold} 轮 RetainedOld 必须恰好产生 1 条告警");
        StringAssert.Contains(rig.ScopeSnapshot().LastMessage, "告警", "告警必须在快照里可观测");
        Assert.AreEqual(1, await rig.CountHitsAsync("zzm9oldline"), "只告警：旧文档必须仍在索引里");

        await rig.Maintenance.StopAsync();
    }

    // ── M10：快照语义 + 有界队列溢出 ─────────────────────────────────────

    /// <summary>
    /// M10：<c>GetSnapshot()</c> 每次返回**新实例**（快照与逐 scope 快照都不相同）、字段如实
    /// （<c>IndexExists</c> 真实、未体检时 <c>LastProbeState</c> 为 null、<c>OverflowCount</c> 只在真溢出时 &gt; 0）。
    /// </summary>
    [TestMethod]
    public async Task M10_Snapshot_ReturnsFreshInstances_AndFieldsAreTruthful()
    {
        using var rig = new MaintenanceRig("m10");
        rig.WriteCorpusFile("a.txt", "zzm10alpha");
        Assert.IsTrue(rig.BuildInitialIndex().Success);

        var beforeStart = rig.Maintenance.GetSnapshot();
        Assert.IsFalse(ReferenceEquals(beforeStart, rig.Maintenance.GetSnapshot()), "每次必须返回新实例");
        Assert.IsFalse(ReferenceEquals(beforeStart.Scopes, rig.Maintenance.GetSnapshot().Scopes));

        await rig.Maintenance.StartAsync(rig.Scopes);
        Assert.IsTrue(await rig.Maintenance.WaitUntilIdleAsync(RoutineWait));

        var first = rig.Maintenance.GetSnapshot();
        var second = rig.Maintenance.GetSnapshot();
        Assert.IsFalse(ReferenceEquals(first, second), "快照必须是新实例");
        Assert.IsFalse(ReferenceEquals(first.Scopes, second.Scopes), "scope 列表必须是新实例");
        Assert.IsFalse(ReferenceEquals(first.Scopes[0], second.Scopes[0]), "逐 scope 快照必须是新实例");

        Assert.IsTrue(first.Running);
        Assert.AreEqual(1, first.Scopes.Count);
        Assert.IsTrue(first.Scopes[0].IndexExists, "索引已建立 ⇒ IndexExists 必须为 true");
        Assert.IsNotNull(first.Scopes[0].WatermarkUtc, "已推进过 ⇒ 水位线非 null");
        Assert.IsTrue(first.Scopes[0].Generation >= 1);
        Assert.AreEqual(0, first.Scopes[0].PendingChangeCount);
        Assert.AreEqual(0, first.Scopes[0].OverflowCount, "没有溢出 ⇒ OverflowCount 必须恰好为 0（不是恒 0，见 M10b）");
        Assert.IsNull(first.Scopes[0].LastProbeState, "未做过体检 ⇒ LastProbeState 必须为 null");
        Assert.IsNotNull(first.Scopes[0].IndexDirectory, "IndexDirectory 必须由单一真源推导出来");
        Assert.AreEqual(rig.ScopeIndexDirectory, first.Scopes[0].IndexDirectory);

        await rig.Maintenance.StopAsync();
    }

    /// <summary>
    /// M10b：有界队列**溢出必须计数 + 请求补偿**（绝不静默丢弃）——「快照字段如实」的负向对照。
    /// </summary>
    [TestMethod]
    public async Task M10b_QueueOverflow_IsCountedAndRequestsACompensationScan()
    {
        using var rig = new MaintenanceRig("m10b", o => o with { QueueCapacity = 1 });
        rig.WriteCorpusFile("a.txt", "zzm10balpha");
        Assert.IsTrue(rig.BuildInitialIndex().Success);

        await rig.Maintenance.StartAsync(rig.Scopes);
        Assert.IsTrue(await rig.Maintenance.WaitUntilIdleAsync(RoutineWait));

        var batchesBefore = rig.Engine.BatchCount;

        rig.Maintenance.SubmitPathObservation(rig.Corpus, rig.CorpusFile("a.txt"));
        rig.Maintenance.SubmitPathObservation(rig.Corpus, rig.CorpusFile("b.txt"));

        var snapshot = rig.ScopeSnapshot();
        Assert.AreEqual(1, snapshot.OverflowCount, "容量 1 的第 2 条路径必须被计为一次溢出");
        Assert.AreEqual(1, rig.Maintenance.GetDiagnostics().RejectedObservations);
        StringAssert.Contains(snapshot.LastMessage, "QueuePressure", "溢出必须请求补偿扫描（不得静默丢弃）");

        var compensated = await rig.WaitUntilAsync(
            () => Task.FromResult(rig.Engine.BatchCount > batchesBefore || rig.Engine.LastBatchWithSource(FullTextChangeSource.MTimeRecovery) is not null),
            TimeSpan.FromSeconds(30));
        Assert.IsTrue(compensated, "溢出请求必须真的驱动一次补偿扫描");

        await rig.Maintenance.StopAsync();
    }

    // ── M11：取消 / 停止 ────────────────────────────────────────────────

    /// <summary>
    /// M11a：停止后调用 <c>RequestRecoveryScanAsync</c> 的行为由本片裁定并测试 = **明确拒绝**
    /// （不留下跨生命周期状态，避免与 <c>StartAsync</c> 自带的 Startup 补偿语义含混）；
    /// 未注册 scope 同样拒绝。
    /// </summary>
    [TestMethod]
    public async Task M11a_RecoveryRequestAfterStopOrUnknownScope_IsRejectedDeterministically()
    {
        using var rig = new MaintenanceRig("m11a");
        rig.WriteCorpusFile("a.txt", "zzm11aalpha");
        Assert.IsTrue(rig.BuildInitialIndex().Success);

        await rig.Maintenance.StartAsync(rig.Scopes);
        await rig.Maintenance.StopAsync();

        var batchesBefore = rig.Engine.BatchCount;
        await rig.Maintenance.RequestRecoveryScanAsync(rig.Corpus, FullTextRecoveryReason.ManualRequest);

        var stopped = rig.Maintenance.GetSnapshot();
        Assert.IsFalse(stopped.Running);
        StringAssert.Contains(stopped.LastError, "被拒绝", "停止后的请求必须是**明确拒绝**，不是静默受理");
        StringAssert.Contains(stopped.LastError, "ManualRequest", "拒绝原因必须点名触发源");

        await Task.Delay(300);
        Assert.AreEqual(batchesBefore, rig.Engine.BatchCount, "被拒绝的请求不得触发任何扫描");

        // 对照：未注册的 scope 也必须拒绝
        await rig.Maintenance.StartAsync(rig.Scopes);
        var unknown = Path.Combine(Path.GetTempPath(), "pudding-fts-s3d-unknown-scope");
        await rig.Maintenance.RequestRecoveryScanAsync(unknown, FullTextRecoveryReason.Interval);
        StringAssert.Contains(rig.Maintenance.GetSnapshot().LastError, "未注册");

        await rig.Maintenance.StopAsync();
    }

    /// <summary>
    /// M11b：<c>StopAsync</c> 期间到达的取消**不得被吞** —— 在飞批次必须收口为 <c>Cancelled</c>、
    /// 不提交、不推进 checkpoint；停止后无残留泵任务 / watcher。
    /// </summary>
    [TestMethod]
    public async Task M11b_StopCancelsInFlightBatch_WithoutCommittingOrAdvancing()
    {
        var gated = new GatedExtractor();
        using var rig = new MaintenanceRig("m11b", extractors: new IFileContentExtractor[] { gated });

        var pdf = rig.WriteCorpusFile("doc.pdf", "%PDF-1.4 placeholder");
        Assert.IsTrue(rig.BuildInitialIndex().Success);

        await rig.Maintenance.StartAsync(rig.Scopes);
        Assert.IsTrue(await rig.Maintenance.WaitUntilIdleAsync(RoutineWait), "Startup 补偿必须排空");

        var baseline = rig.ScopeSnapshot();
        Assert.AreEqual(1, await rig.CountHitsAsync("gatedzzcontent"));

        // 让下一批**卡在提取阶段**（确定性时机：Entered 被点亮即代表批次在飞行中）
        gated.Armed = true;
        rig.TouchAfterWatermark(pdf);
        await rig.Maintenance.RequestRecoveryScanAsync(rig.Corpus, FullTextRecoveryReason.Interval);

        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // 在飞期间停止
        await rig.Maintenance.StopAsync();
        gated.Release.TrySetResult();

        var last = rig.Engine.Results[^1];
        Assert.AreEqual(
            FullTextMutationState.Cancelled,
            last.State,
            $"在飞批次必须收口为 Cancelled（不得伪装成功）：{last.Message}");
        Assert.IsFalse(last.CheckpointAdvanced, "取消 ⇒ 绝不推进 checkpoint");
        Assert.IsNull(last.CommitMilliseconds, "取消 ⇒ 未提交");

        var afterStop = rig.Maintenance.GetDiagnostics();
        Assert.AreEqual(0, afterStop.LivePumpTasks, "停止后不得残留泵任务");
        Assert.AreEqual(0, afterStop.LiveWatchers, "停止后不得残留 watcher");
        Assert.IsFalse(rig.Maintenance.GetSnapshot().Running);

        // 水位线不动 ⇒ 下一轮还会重放（这里用 checkpoint 文件的内容独立核对）
        var raw = File.ReadAllText(rig.CheckpointPath);
        Assert.AreEqual(
            MaintenanceCheckpointReadStatus.Ok,
            MaintenanceCheckpoint.TryParse(raw, out var onDisk),
            "checkpoint 必须仍是可解析的已提交状态");
        Assert.IsNotNull(onDisk);
        Assert.AreEqual(baseline.Generation, onDisk.Generation, "取消的轮次不得推进 generation");
        Assert.AreEqual(baseline.WatermarkUtc, onDisk.WatermarkUtc, "取消的轮次不得推进 watermark");

        Assert.AreEqual(1, await rig.CountHitsAsync("gatedzzcontent"), "取消 ⇒ 旧内容必须仍在（未写半批）");
    }

    // ── M12：体积增长机器可读报告 ───────────────────────────────────────

    /// <summary>
    /// M12：连续多批局部更新后能产出机器可读的 <c>IndexSizeReport</c>（出口 = 引擎的 internal
    /// <c>ApplyChangesWithReportAsync</c>，**未**为测试改动任何 public 面）：可解析、批次间字节连续。
    /// </summary>
    [TestMethod]
    public async Task M12_SizeReports_AreMachineReadableAcrossConsecutiveBatches()
    {
        using var rig = new MaintenanceRig("m12");
        rig.WriteCorpusFile("a.txt", "zzm12alpha");
        Assert.IsTrue(rig.BuildInitialIndex().Success);

        await rig.Maintenance.StartAsync(rig.Scopes);
        Assert.IsTrue(await rig.Maintenance.WaitUntilIdleAsync(RoutineWait));

        for (var i = 1; i <= 3; i++)
        {
            rig.Advance(TimeSpan.FromMinutes(10));
            var path = rig.WriteCorpusFile($"m{i}.txt", "zzm12content" + i);
            rig.TouchAfterWatermark(path);

            Assert.IsTrue(
                await rig.Maintenance.RunRecoveryScanAsync(rig.Corpus, FullTextRecoveryReason.Interval),
                $"第 {i} 批局部更新必须成功并推进 checkpoint");
        }

        var reports = rig.Engine.Reports;
        Assert.IsTrue(reports.Count >= 3, $"连续局部更新必须每批产出一份体积报告（实际 {reports.Count} 份）");

        long? previousAfter = null;
        foreach (var report in reports)
        {
            Assert.IsNotNull(report.IndexBytesBefore, "批前字节不可测时必须为 null，而不是伪报 0 —— 这里必须可测");
            Assert.IsNotNull(report.IndexBytesAfter);

            var parsed = IndexSizeReport.FromJsonLine(report.ToJsonLine());
            Assert.IsNotNull(parsed, "NDJSON 单行必须可机器解析");
            Assert.AreEqual(report, IndexSizeReport.FromTsvRow(report.ToTsvRow()), "TSV 行必须可往返解析");
            Assert.IsFalse(report.QuotaExceeded, "预算充足 ⇒ 不得触发写入期配额");

            if (previousAfter is not null)
                Assert.AreEqual(previousAfter, report.IndexBytesBefore, "批次之间必须字节连续（无黑箱增长）");

            previousAfter = report.IndexBytesAfter;
        }

        Assert.AreEqual(1, await rig.CountHitsAsync("zzm12content3"));

        await rig.Maintenance.StopAsync();
    }
}
