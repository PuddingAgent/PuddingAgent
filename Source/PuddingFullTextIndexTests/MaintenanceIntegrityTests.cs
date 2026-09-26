using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure;
using PuddingFullTextIndex.Infrastructure.Maintenance;
using Directory = System.IO.Directory;

namespace PuddingFullTextIndexTests;

/// <summary>
/// S3d：<c>ProbeIntegrityAsync</c>（只读完整性探针，三态）与低优先级体检（M7 / M8）的组件侧断言。
/// <para>
/// M7 的三条硬性质：① 三态**可区分**且都能被真实构造（索引不存在 / 存在但不可读 / 有差异）；
/// ② **只读**（探针前后索引目录的字节与文件数逐位相同）；③ **绝不自动重建**（探针跑完，磁盘上多出来的文件仍然不在索引里）。
/// </para>
/// <para>
/// M8 的三条硬性质：① 资源不可采样 / 超阈值 ⇒ **退避**（不跑探针、不产生变更，原因如实写进 <c>LastMessage</c>）；
/// ② 修复按 <c>HealthCheckSliceFiles</c> **切片**（每轮最多发布这么多条修复，且**不推进 checkpoint**）；
/// ③ 可被取消。全部语料 / 索引落在 <c>%TEMP%</c>。
/// </para>
/// </summary>
[TestClass]
public sealed class MaintenanceIntegrityTests
{
    private static readonly TimeSpan RoutineWait = TimeSpan.FromSeconds(30);

    // ── M7：探针三态 + 只读 ──────────────────────────────────────────────

    /// <summary>M7a：索引目录**不存在** ⇒ <c>ManualRebuildRequired</c>，而且探针**绝不**创建它（不偷偷局部初始化）。</summary>
    [TestMethod]
    public async Task M7a_Probe_MissingIndex_IsManualRebuildRequired_AndCreatesNothing()
    {
        using var rig = new MaintenanceRig("m7a");
        rig.WriteCorpusFile("a.txt", "zzm7aalpha");

        Assert.IsFalse(Directory.Exists(rig.ScopeIndexDirectory), "前置：索引目录尚不存在");

        var probe = await rig.InnerEngine.ProbeIntegrityAsync(rig.Corpus);

        Assert.AreEqual(FullTextIndexIntegrityState.ManualRebuildRequired, probe.State, probe.Message);
        Assert.IsFalse(probe.IndexDirectoryExists, "索引目录确实不存在");
        Assert.IsNull(probe.IndexedPathCount, "不可读必须报 null，不得伪报 0");
        Assert.IsNull(probe.IndexedDocumentCount);
        Assert.IsNull(probe.IndexBytes, "索引目录不存在 ⇒ 字节不可测 ⇒ null");
        Assert.AreEqual(0, probe.CheckedPathCount);
        Assert.AreEqual(0, probe.MismatchCount);
        Assert.AreEqual(0, probe.MismatchedPaths.Count);
        Assert.AreEqual(MaintenanceRig.Normalize(rig.Corpus), probe.ScopeKey);

        Assert.IsFalse(
            Directory.Exists(rig.ScopeIndexDirectory),
            "★ 探针只读：绝不允许通过局部写偷偷创建一份「初始全库索引」");
    }

    /// <summary>
    /// M7b：索引目录**存在但不可读**（不是索引 / segments 损坏）⇒ 同样 <c>ManualRebuildRequired</c>
    /// —— 「无法证明新鲜」不许宣称健康，也不得自动修复。
    /// </summary>
    [TestMethod]
    public async Task M7b_Probe_UnreadableIndex_IsManualRebuildRequired()
    {
        using var rig = new MaintenanceRig("m7b");
        rig.WriteCorpusFile("a.txt", "zzm7balpha");

        Directory.CreateDirectory(rig.ScopeIndexDirectory);
        File.WriteAllText(Path.Combine(rig.ScopeIndexDirectory, "not-an-index.bin"), "zzm7bgarbage");

        var missingSegments = await rig.InnerEngine.ProbeIntegrityAsync(rig.Corpus);
        Assert.AreEqual(
            FullTextIndexIntegrityState.ManualRebuildRequired,
            missingSegments.State,
            $"目录存在但不是索引 ⇒ 必须要求手动重建：{missingSegments.Message}");
        Assert.IsTrue(missingSegments.IndexDirectoryExists);
        Assert.IsNull(missingSegments.IndexedPathCount, "读不出 ⇒ null（不得伪报 0）");

        // 形态二：看起来有 segments 文件但内容损坏 ⇒ 走「读不出」分支，结论不变
        File.WriteAllText(Path.Combine(rig.ScopeIndexDirectory, "segments_1"), "zzm7bcorrupted-segments");

        var corrupted = await rig.InnerEngine.ProbeIntegrityAsync(rig.Corpus);
        Assert.AreEqual(
            FullTextIndexIntegrityState.ManualRebuildRequired,
            corrupted.State,
            $"损坏的 segments ⇒ 必须要求手动重建：{corrupted.Message}");
        Assert.IsTrue(corrupted.IndexDirectoryExists);
        Assert.IsNull(corrupted.IndexedPathCount);

        // 探针绝不修复：那个垃圾文件必须原样还在（没有被「顺手清理」）
        Assert.IsTrue(File.Exists(Path.Combine(rig.ScopeIndexDirectory, "not-an-index.bin")));
    }

    /// <summary>
    /// M7c：<c>Healthy</c> 与 <c>Degraded</c> 都能被真实构造；两次探针**前后索引目录字节与文件数逐位相同**；
    /// 探针**绝不自动重建**（磁盘上多出来的文件在探针之后仍然不在索引里）。
    /// </summary>
    [TestMethod]
    public async Task M7c_Probe_DegradedThenHealthy_AndIsBitIdenticalReadOnly()
    {
        using var rig = new MaintenanceRig("m7c");
        rig.WriteCorpusFile("a.txt", "zzm7calpha");
        rig.WriteCorpusFile("b.txt", "zzm7cbeta");
        Assert.IsTrue(rig.BuildInitialIndex().Success);

        var before = MaintenanceRig.MeasureDirectory(rig.ScopeIndexDirectory);
        Assert.IsTrue(before.Files > 0, "前置：索引目录里必须真的有文件（否则「逐位相同」没有意义）");

        // ① Healthy：索引与磁盘路径集合一致
        var healthy = await rig.InnerEngine.ProbeIntegrityAsync(rig.Corpus);
        Assert.AreEqual(FullTextIndexIntegrityState.Healthy, healthy.State, healthy.Message);
        Assert.AreEqual(2, healthy.IndexedPathCount, "a.txt + b.txt");
        Assert.AreEqual(0, healthy.MismatchCount);
        Assert.AreEqual(0, healthy.MismatchedPaths.Count);
        Assert.IsNotNull(healthy.IndexBytes);
        Assert.IsTrue(healthy.CheckedPathCount >= 2);

        // ② Degraded：磁盘上多出一个尚未入索引的文件
        var c = rig.WriteCorpusFile("c.txt", "zzm7cgamma");
        var degraded = await rig.InnerEngine.ProbeIntegrityAsync(rig.Corpus);

        Assert.AreEqual(FullTextIndexIntegrityState.Degraded, degraded.State, degraded.Message);
        Assert.AreEqual(1, degraded.MismatchCount, "差异必须恰好是那一个未入索引的文件");
        Assert.AreEqual(1, degraded.MismatchedPaths.Count);
        Assert.AreEqual(
            MaintenanceRig.Normalize(c),
            MaintenanceRig.Normalize(degraded.MismatchedPaths[0]),
            "差异清单必须点出 c.txt");
        Assert.AreEqual(2, degraded.IndexedPathCount, "索引里的路径数不变（探针只读）");
        Assert.AreEqual(3, degraded.CheckedPathCount, "索引 2 路径 ∪ 磁盘 3 路径（并集 = 3）");

        // ③ 只读：探针前后索引目录（文件数, 字节）逐位相同
        var after = MaintenanceRig.MeasureDirectory(rig.ScopeIndexDirectory);
        Assert.AreEqual(before.Files, after.Files, "★ 探针前后文件数必须逐位相同");
        Assert.AreEqual(before.Bytes, after.Bytes, "★ 探针前后字节必须逐位相同");

        // ④ 绝不自动重建：c.txt 在探针之后仍然不在索引里（要么由维护层发布变更集，要么由人重建）
        var inventory = await rig.InventoryAsync();
        Assert.IsFalse(
            inventory.Any(e => e.NormalizedPath == MaintenanceRig.Normalize(c)),
            "★ 探针绝不自动重建 / 局部初始化：c.txt 不得因为探针而进入索引");
        Assert.AreEqual(0, rig.Engine.BatchCount, "探针不得调用执行层");

        // 对照组：把 c.txt 交给维护层（体检切片）之后，探针必须回到 Healthy —— 证明 Degraded 不是恒真
        rig.Pressure.Next = new ResourcePressureSample(1.0, null, "test-idle");
        await rig.Maintenance.StartAsync(rig.Scopes);
        var slice = await rig.Maintenance.RunHealthCheckSliceAsync(rig.Corpus);
        Assert.AreEqual(FullTextIndexIntegrityState.Degraded, slice.State);
        Assert.AreEqual(1, slice.RepairedPathCount, "该切片必须发布 1 条修复");
        await rig.Maintenance.StopAsync();

        var reconciled = await rig.InnerEngine.ProbeIntegrityAsync(rig.Corpus);
        Assert.AreEqual(
            FullTextIndexIntegrityState.Healthy,
            reconciled.State,
            $"修复后探针必须回到 Healthy：{reconciled.Message}");
    }

    // ── M8：低优先级体检 + 小切片 + 退避 + 可取消 ────────────────────────

    /// <summary>
    /// M8：① 资源不可采样 ⇒ 退避（不跑探针、不产生变更、原因写进 <c>LastMessage</c>）；
    /// ② 资源正常 ⇒ 按 <c>HealthCheckSliceFiles</c> **小切片**发布修复（每轮一条），修完回到 Healthy；
    /// ③ 切片可被取消。体检**不推进 checkpoint**。
    /// </summary>
    [TestMethod]
    public async Task M8_HealthCheck_BacksOffWhenUnsampleable_AndRunsSmallSlicesWhenResourcesAreOk()
    {
        using var rig = new MaintenanceRig("m8", o => o with { HealthCheckSliceFiles = 1 });
        rig.WriteCorpusFile("a.txt", "zzm8alpha");
        Assert.IsTrue(rig.BuildInitialIndex().Success);

        await rig.Maintenance.StartAsync(rig.Scopes);
        Assert.IsTrue(await rig.Maintenance.WaitUntilIdleAsync(RoutineWait));

        var baseline = rig.ScopeSnapshot();
        var batchesBefore = rig.Engine.BatchCount;
        var probesBefore = rig.Engine.ProbeCallCount;

        // ① 资源不可采样 ⇒ 退避
        rig.Pressure.Next = null;
        var backedOff = await rig.Maintenance.RunHealthCheckSliceAsync(rig.Corpus);

        Assert.IsFalse(backedOff.Ran, "不可采样 ⇒ 不得强行运行体检");
        Assert.IsTrue(backedOff.BackedOff);
        StringAssert.Contains(backedOff.Message, "不可采样");
        Assert.AreEqual(probesBefore, rig.Engine.ProbeCallCount, "退避 ⇒ 连只读探针都不许跑");
        Assert.AreEqual(batchesBefore, rig.Engine.BatchCount, "退避 ⇒ 不得产生任何变更");
        StringAssert.Contains(rig.ScopeSnapshot().LastMessage, "体检退避", "退避原因必须如实写在 LastMessage");
        Assert.IsNull(rig.ScopeSnapshot().LastProbeState, "退避不算体检 ⇒ 不得写入体检结论");

        // 对照组：CPU 超阈值同样退避（不是只有「不可采样」才退避）
        rig.Pressure.Next = new ResourcePressureSample(99.0, null, "busy");
        var cpuBusy = await rig.Maintenance.RunHealthCheckSliceAsync(rig.Corpus);
        Assert.IsFalse(cpuBusy.Ran);
        StringAssert.Contains(cpuBusy.Message, "CPU");

        // ② 资源正常 ⇒ Healthy 结论
        rig.Pressure.Next = new ResourcePressureSample(1.0, null, "idle");
        var healthy = await rig.Maintenance.RunHealthCheckSliceAsync(rig.Corpus);
        Assert.IsTrue(healthy.Ran);
        Assert.AreEqual(FullTextIndexIntegrityState.Healthy, healthy.State, healthy.Message);
        Assert.AreEqual(FullTextIndexIntegrityState.Healthy, rig.ScopeSnapshot().LastProbeState);

        // ③ Degraded ⇒ 每轮最多修复 HealthCheckSliceFiles（=1）条
        rig.WriteCorpusFile("b.txt", "zzm8beta");
        rig.WriteCorpusFile("c.txt", "zzm8gamma");

        var firstSlice = await rig.Maintenance.RunHealthCheckSliceAsync(rig.Corpus);
        Assert.AreEqual(FullTextIndexIntegrityState.Degraded, firstSlice.State);
        Assert.AreEqual(2, firstSlice.MismatchCount, "差异有 2 条");
        Assert.AreEqual(1, firstSlice.RepairedPathCount, "★ 小切片：每轮最多发布 HealthCheckSliceFiles 条修复");

        var healthBatch = rig.Engine.LastBatchWithSource(FullTextChangeSource.IntegrityCheck);
        Assert.IsNotNull(healthBatch, "体检必须产出 IntegrityCheck 来源的同一份变更集");
        Assert.IsFalse(
            healthBatch!.RequiresCheckpointAdvance,
            "★ 体检不是完整补偿轮次 ⇒ 变更集必须声明「不推进 checkpoint」");

        var secondSlice = await rig.Maintenance.RunHealthCheckSliceAsync(rig.Corpus);
        Assert.AreEqual(1, secondSlice.RepairedPathCount, "第二条差异在被切到下一轮");

        var thirdSlice = await rig.Maintenance.RunHealthCheckSliceAsync(rig.Corpus);
        Assert.AreEqual(FullTextIndexIntegrityState.Healthy, thirdSlice.State, "修完必须回到 Healthy（对照：Degraded 不是恒真）");

        Assert.AreEqual(1, await rig.CountHitsAsync("zzm8beta"));
        Assert.AreEqual(1, await rig.CountHitsAsync("zzm8gamma"));

        // 体检绝不推进 checkpoint
        Assert.AreEqual(baseline.Generation, rig.ScopeSnapshot().Generation, "体检不得推进 generation");
        Assert.AreEqual(baseline.WatermarkUtc, rig.ScopeSnapshot().WatermarkUtc, "体检不得推进 watermark");

        // ④ 可被取消（取消是控制流，必须外抛而不是被吞成「空结果」）
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                async () => await rig.Maintenance.RunHealthCheckSliceAsync(rig.Corpus, cts.Token));
        }

        await rig.Maintenance.StopAsync();
    }
}
