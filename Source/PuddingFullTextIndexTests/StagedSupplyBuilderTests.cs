using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndexTests;

/// <summary>
/// A2a 组件内单元断言：staging 构建（R1）、预算硬限两道（R2）、原子切换与回滚（R3）、
/// reader 失效接缝（R4）、残留清理（R5）、可观察性（R6）。
/// <para>
/// 全部用替身（fake live 引擎 / fake staging 引擎 / fake 移动原语），落在 <see cref="Path.GetTempPath"/> 下；
/// 真实 Lucene 的端到端断言在 <c>StagedSupplyEndToEndTests</c>。
/// </para>
/// </summary>
[TestClass]
public sealed class StagedSupplyBuilderTests
{
    // ── A1：切换成功 ────────────────────────────────────────────────────

    [TestMethod]
    public async Task A1_Swap_Succeeds_And_The_Staging_Directory_Disappears()
    {
        using var rig = new StagedRig(stagingWriteBytes: 4096);
        var scope = rig.Fixture.ScopeFor(corpusBytes: 1024);

        Directory.CreateDirectory(rig.LiveDirectory);
        File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "old.bin"), new byte[512]);
        var liveDirectory = rig.LiveDirectory;

        var result = await rig.Builder.BuildAsync(scope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.Swap, "staged 路径必须给出切换口径快照");
        Assert.AreEqual(SupplySwapOutcome.Swapped, result.Swap!.Outcome);
        Assert.AreEqual(4096L, result.Swap.StagingBytes, "stagingBytes 必须是**实测**字节");
        Assert.AreEqual(512L, result.Swap.LiveBytesBefore, "liveBytesBefore = 切换前全部 live scope 实测字节");
        Assert.AreEqual(4096L, result.Swap.LiveBytesAfter, "切换后 live = 新索引实测字节");
        Assert.AreEqual(rig.Options.DefaultBudgetBytes, result.Swap.BudgetBytes);

        Assert.IsFalse(Directory.Exists(rig.StagingRootFor(scope)), "切换后 .staging 下该 job 目录必须消失");
        Assert.IsTrue(Directory.Exists(liveDirectory), "live 必须就位");
        Assert.AreEqual(4096L, new FileInfo(Path.Combine(liveDirectory, "segments_1")).Length);
        Assert.IsFalse(File.Exists(Path.Combine(liveDirectory, "old.bin")), "旧 live 内容不得残留在 live");

        Assert.IsEmpty(Directory.GetFileSystemEntries(rig.Fixture.StagingRoot()), ".staging 不得有残留");
        Assert.IsEmpty(Directory.GetFileSystemEntries(rig.Fixture.TrashRoot()), ".trash 不得有残留");
        Assert.AreEqual(0, rig.LiveEngine.BuildCallCount, "staged 路径不得让 live 引擎直接构建（写 live 只能经切换）");
    }

    // ── A2：预检拒绝（第一道）───────────────────────────────────────────

    [TestMethod]
    public async Task A2_Precheck_Rejects_Over_Budget_And_The_Build_Never_Starts()
    {
        using var rig = new StagedRig(budgetBytes: 10_000, stagingWriteBytes: 4096);
        var scope = rig.Fixture.ScopeFor(corpusBytes: 100_000);   // 预测 = ceil(100000 × 1.13) = 113000 > 10000

        Assert.IsFalse(Directory.Exists(rig.Fixture.IndexRoot), "前置：索引根一开始不存在");

        var result = await rig.Builder.BuildAsync(scope);

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error, "OverBudget");
        StringAssert.Contains(result.Error, "预检");
        StringAssert.Contains(result.Error, "113000", "消息必须给出预测值");
        StringAssert.Contains(result.Error, "10000", "消息必须给出预算");
        Assert.IsNotNull(result.Swap);
        Assert.AreEqual(SupplySwapOutcome.RejectedOverBudget, result.Swap!.Outcome);
        Assert.AreEqual(0L, result.Swap.StagingBytes, "预检拒绝时根本没有开始构建");
        Assert.AreEqual(0L, result.Swap.LiveBytesBefore);

        Assert.IsEmpty(rig.StagingEngines, "预检拒绝 ⇒ 不得创建 staging 引擎实例（更不得构建）");
        Assert.AreEqual(0, rig.LiveEngine.BuildCallCount);
        Assert.IsFalse(Directory.Exists(rig.Fixture.IndexRoot), "预检拒绝不得动盘：live 目录、staging 目录都不许出现");
    }

    // ── A3：实测硬限（第二道；预检被预测骗过时兜底）─────────────────────

    [TestMethod]
    public async Task A3_Measured_Limit_Rejects_And_Clears_Staging_Leaving_Live_Untouched()
    {
        using var rig = new StagedRig(budgetBytes: 3_000, stagingWriteBytes: 4096);
        var scope = rig.Fixture.ScopeFor(corpusBytes: 100);      // 预测 113 ≤ 3000 ⇒ 预检通过

        Directory.CreateDirectory(rig.LiveDirectory);
        var liveFile = Path.Combine(rig.LiveDirectory, "old.bin");
        File.WriteAllBytes(liveFile, new byte[512]);
        var before = StagedSupplyTestHelpers.SnapshotTree(rig.LiveDirectory);

        var result = await rig.Builder.BuildAsync(scope);

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error, "OverBudget");
        StringAssert.Contains(result.Error, "staging 实测");
        StringAssert.Contains(result.Error, "4096");
        Assert.AreEqual(SupplySwapOutcome.RejectedOverBudget, result.Swap!.Outcome);
        Assert.AreEqual(4096L, result.Swap.StagingBytes);
        Assert.AreEqual(512L, result.Swap.LiveBytesBefore, "live 侧是**合计**口径（512 字节）");
        Assert.AreEqual(512L, result.Swap.LiveBytesAfter, "被拒时 live 一字节不动");

        Assert.AreEqual(before, StagedSupplyTestHelpers.SnapshotTree(rig.LiveDirectory), "live 目录内容必须逐字节不变");
        Assert.AreEqual(512L, new FileInfo(liveFile).Length);
        Assert.HasCount(1, rig.StagingEngines);
        Assert.AreEqual(1, rig.StagingEngines[0].BuildCallCount, "预检已过 ⇒ 引擎确实构建过（证明拦截发生在实测环节）");
        Assert.AreEqual(0, rig.LiveEngine.BuildCallCount);
        Assert.IsFalse(Directory.Exists(rig.StagingRootFor(scope)), "被拒后 staging 该 job 目录必须清掉");
        Assert.IsEmpty(Directory.GetFileSystemEntries(rig.Fixture.StagingRoot()));
    }

    // ── A4：切换第 3 步失败 ⇒ 回滚 ──────────────────────────────────────

    [TestMethod]
    public async Task A4_Swap_Failure_Rolls_The_Old_Live_Back()
    {
        using var rig = new StagedRig(
            stagingWriteBytes: 4096,
            swapperFactory: events => new FakeIndexDirectorySwapper(
                events,
                failureFactory: (_, _, call) => call == 2 ? new IOException("injected-swap-failure") : null));

        var scope = rig.Fixture.ScopeFor(corpusBytes: 100);

        Directory.CreateDirectory(rig.LiveDirectory);
        File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "old.bin"), new byte[512]);
        var liveDirectory = rig.LiveDirectory;
        var before = StagedSupplyTestHelpers.SnapshotTree(liveDirectory);

        var result = await rig.Builder.BuildAsync(scope);

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error, "切换失败");
        StringAssert.Contains(result.Error, "回滚");
        Assert.AreEqual(SupplySwapOutcome.RolledBack, result.Swap!.Outcome);
        Assert.AreEqual(512L, result.Swap.LiveBytesAfter, "回滚后 live 用量必须回到切换前");

        Assert.IsTrue(Directory.Exists(liveDirectory), "回滚后 live 必须存在");
        Assert.AreEqual(before, StagedSupplyTestHelpers.SnapshotTree(liveDirectory), "回滚后 live 必须仍是旧索引（逐字节）");
        Assert.IsEmpty(Directory.GetFileSystemEntries(rig.Fixture.TrashRoot()), "回滚后 .trash 不应留下副本");
        Assert.IsFalse(Directory.Exists(rig.StagingRootFor(scope)), "回滚后 staging 残留必须清理");

        var swapper = (FakeIndexDirectorySwapper)rig.Swapper;
        Assert.HasCount(3, swapper.Moves, "步骤数：移旧 live、移新索引（失败）、回滚移回");
        Assert.IsFalse(swapper.Moves[0].Failed, "第 2 步（旧 live → .trash）应成功");
        Assert.IsTrue(swapper.Moves[1].Failed, "第 3 步（staging → live）被注入失败");
        Assert.IsFalse(swapper.Moves[2].Failed, "回滚（.trash → live）应成功");
        Assert.HasCount(2, rig.LiveEngine.InvalidateRequests, "失效一次在切换前、一次在回滚后");
    }

    // ── A5（单元侧）：失效时机与切换顺序 ────────────────────────────────

    [TestMethod]
    public async Task A5_Invalidation_Brackets_The_Swap_In_The_Mandated_Order()
    {
        using var rig = new StagedRig(stagingWriteBytes: 4096);
        var scope = rig.Fixture.ScopeFor(corpusBytes: 100);

        Directory.CreateDirectory(rig.LiveDirectory);
        File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "old.bin"), new byte[512]);

        var result = await rig.Builder.BuildAsync(scope);
        Assert.IsTrue(result.Success, result.Error);

        var corpus = rig.Fixture.Corpus;
        var events = rig.Events;
        var buildAt = events.FindIndex(e => e.StartsWith("build:", StringComparison.Ordinal));
        var invalidateBefore = events.IndexOf($"invalidate:{corpus}");
        var move1 = events.FindIndex(e => e.StartsWith("move:", StringComparison.Ordinal));
        var move2 = events.FindIndex(move1 + 1, e => e.StartsWith("move:", StringComparison.Ordinal));
        var invalidateAfter = events.FindLastIndex(e => e == $"invalidate:{corpus}");

        Assert.IsTrue(buildAt >= 0 && buildAt < invalidateBefore, "构建发生在失效之前");
        Assert.IsLessThan(
            move1,
            invalidateBefore,
            "切换**之前**必须先失效 live reader 缓存：陈旧 Reader 会看到过期文档，且 Windows 下句柄会阻止目录移动");
        Assert.IsLessThan(move2, move1, "先移走旧 live，再把 staging 移入 live");
        Assert.IsLessThan(invalidateAfter, move2, "切换完成后必须再失效一次，保证后续查询看到新索引");
        Assert.HasCount(2, rig.LiveEngine.InvalidateRequests);

        var swapper = (FakeIndexDirectorySwapper)rig.Swapper;
        Assert.AreEqual(rig.LiveDirectory, swapper.Moves[0].Source, "第 1 次移动的源是旧 live");
        Assert.AreEqual(rig.LiveDirectory, swapper.Moves[1].Destination, "第 2 次移动的目标是 live");
    }

    // ── A6：预算是**集合**口径（不是每 scope 各自比较）──────────────────

    [TestMethod]
    public async Task A6_Budget_Is_Aggregated_Across_All_Live_Scopes()
    {
        using var rig = new StagedRig(budgetBytes: 1_000_000, stagingWriteBytes: 4096);

        // 别的 scope 已占 1024 字节 live（64 位 hex 目录名 ⇒ 计入 live 用量口径）
        var otherScopeLive = Path.Combine(rig.Fixture.IndexRoot, new string('a', 64));
        Directory.CreateDirectory(otherScopeLive);
        File.WriteAllBytes(Path.Combine(otherScopeLive, "segments_1"), new byte[1024]);

        // ① 若按「每 scope 各自与预算比」，预算 4096 ≥ staging 4096 就该放行；
        //    集合口径必须拒绝（live 1024 + staging 4096 > 4096）。
        var rejected = await rig.Builder.BuildAsync(
            rig.Fixture.ScopeFor(budgetBytes: 4096, corpusBytes: 100, jobId: "job-a6-reject"));

        Assert.IsFalse(rejected.Success, "live 已有 1024 字节时，4096 的预算装不下 4096 的新索引");
        StringAssert.Contains(rejected.Error!, "OverBudget");
        StringAssert.Contains(rejected.Error!, "live 全部 scope 索引 1024 字节");
        Assert.AreEqual(SupplySwapOutcome.RejectedOverBudget, rejected.Swap!.Outcome);
        Assert.AreEqual(1024L, rejected.Swap.LiveBytesBefore);
        Assert.AreEqual(1024L, rejected.Swap.LiveBytesAfter);
        Assert.AreEqual(1024L, new FileInfo(Path.Combine(otherScopeLive, "segments_1")).Length, "别的 scope 的 live 不许被碰");

        // ② 对照组：同一个 rig、同一份语料、同一个 staging 体积，只把预算改成 1024 + 4096 ⇒ 必须放行
        var swapped = await rig.Builder.BuildAsync(
            rig.Fixture.ScopeFor(budgetBytes: 1024 + 4096, corpusBytes: 100, jobId: "job-a6-accept"));

        Assert.IsTrue(swapped.Success, swapped.Error);
        Assert.AreEqual(SupplySwapOutcome.Swapped, swapped.Swap!.Outcome);
        Assert.AreEqual(1024L, swapped.Swap.LiveBytesBefore);
        Assert.AreEqual(1024L + 4096L, swapped.Swap.LiveBytesAfter, "合计口径：别的 scope 的 1024 字节仍在预算里");
    }

    // ── A7：残留清理只清过期的 ──────────────────────────────────────────

    [TestMethod]
    public async Task A7_Stale_Cleanup_Removes_Only_Entries_Older_Than_The_Threshold()
    {
        using var rig = new StagedRig(stagingWriteBytes: 4096, staleArtifactMaxAge: TimeSpan.FromHours(24));

        var oldStaging = rig.Fixture.PlantResidue(".staging/old-staging", ageHours: 48);
        var oldTrash = rig.Fixture.PlantResidue(".trash/old-trash", ageHours: 30);
        var freshStaging = rig.Fixture.PlantResidue(".staging/fresh-staging", ageHours: 1);
        var freshTrash = rig.Fixture.PlantResidue(".trash/fresh-trash", ageHours: 0.5);

        var result = await rig.Builder.BuildAsync(rig.Fixture.ScopeFor(corpusBytes: 100));

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.Swap);
        CollectionAssert.AreEqual(
            new[] { ".staging/old-staging", ".trash/old-trash" },
            result.Swap!.CleanedArtifacts!.ToArray(),
            "只允许清掉早于阈值的条目，并如实给出条目名");
        Assert.AreEqual(2, result.Swap.CleanedArtifactCount);
        Assert.IsNull(result.Swap.CleanupError);

        Assert.IsFalse(Directory.Exists(oldStaging), "48h 前的 staging 残留必须被清掉");
        Assert.IsFalse(Directory.Exists(oldTrash), "30h 前的 trash 残留必须被清掉");
        Assert.IsTrue(Directory.Exists(freshStaging), "1h 前的 staging 条目可能正被并发进程使用，必须保留");
        Assert.IsTrue(Directory.Exists(freshTrash), "30 分钟前的 trash 条目必须保留");
    }

    // ── A8：staging / trash 与 live 同卷（同一 <IndexRoot> 前缀）────────

    [TestMethod]
    public async Task A8_Staging_And_Trash_Stay_Under_The_Index_Root()
    {
        using var rig = new StagedRig(stagingWriteBytes: 4096);
        var scope = rig.Fixture.ScopeFor(corpusBytes: 100);

        Directory.CreateDirectory(rig.LiveDirectory);

        var result = await rig.Builder.BuildAsync(scope);
        Assert.IsTrue(result.Success, result.Error);

        var indexRoot = Path.GetFullPath(rig.Fixture.IndexRoot).TrimEnd(Path.DirectorySeparatorChar);
        var stagingDirectory = Path.GetFullPath(result.Swap!.StagingDirectory!);
        var liveDirectory = Path.GetFullPath(rig.LiveDirectory);
        var trashDirectory = Path.GetFullPath(
            SupplyIndexDirectoryLayout.ResolveTrashDirectory(rig.Fixture.IndexRoot, scope.ScopeKey, DateTimeOffset.UtcNow));

        Assert.IsTrue(
            stagingDirectory.StartsWith(indexRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"staging 必须在 <IndexRoot> 之下：{stagingDirectory}");
        Assert.IsTrue(
            trashDirectory.StartsWith(
                Path.Combine(indexRoot, SupplyIndexDirectoryLayout.TrashDirectoryName) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase),
            $"trash 必须在 <IndexRoot>/.trash 之下：{trashDirectory}");
        Assert.AreEqual(
            Path.GetPathRoot(indexRoot),
            Path.GetPathRoot(stagingDirectory),
            "staging 必须与 IndexRoot 同卷（跨卷 Move 不是重命名语义，切换就不原子了）");
        Assert.AreEqual(Path.GetPathRoot(indexRoot), Path.GetPathRoot(liveDirectory));
        Assert.AreEqual(Path.GetPathRoot(indexRoot), Path.GetPathRoot(trashDirectory));
        Assert.AreNotEqual(
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            stagingDirectory,
            "staging 不得直接落在 %TEMP% 根部（这里只是借临时目录做测试根）");
    }

    // ── R1：staging 目录被占用 ⇒ 拒绝复用 ───────────────────────────────

    [TestMethod]
    public async Task R1_Staging_Directory_Occupied_Is_Refused_Without_Touching_Live()
    {
        using var rig = new StagedRig(stagingWriteBytes: 4096);
        var scope = rig.Fixture.ScopeFor(corpusBytes: 100);

        var occupied = rig.Fixture.StagingRootFor(scope);
        Directory.CreateDirectory(occupied);
        var keeper = Path.Combine(occupied, "keep.txt");
        File.WriteAllText(keeper, "keep");

        Directory.CreateDirectory(rig.LiveDirectory);
        File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "old.bin"), new byte[512]);
        var before = StagedSupplyTestHelpers.SnapshotTree(rig.LiveDirectory);

        var result = await rig.Builder.BuildAsync(scope);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, "已存在");
        Assert.AreEqual(SupplySwapOutcome.None, result.Swap!.Outcome);
        Assert.IsTrue(File.Exists(keeper), "不得覆盖/删除已存在的 staging 目录");
        Assert.AreEqual(before, StagedSupplyTestHelpers.SnapshotTree(rig.LiveDirectory));
        Assert.AreEqual(0, rig.LiveEngine.BuildCallCount);
    }

    // ── R1：staging 构建本身失败 ⇒ live 不动 ────────────────────────────

    [TestMethod]
    public async Task R1_Staging_Build_Failure_Leaves_Live_Untouched()
    {
        using var rig = new StagedRig(
            stagingCustomizer: engine =>
            {
                engine.BuildBehaviour = (_, _) => Task.FromResult(new FullTextIndexResult(false, 0, 0, 7, "staging-engine-boom"));
                return engine;
            });

        var scope = rig.Fixture.ScopeFor(corpusBytes: 100);

        Directory.CreateDirectory(rig.LiveDirectory);
        File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "old.bin"), new byte[512]);
        var before = StagedSupplyTestHelpers.SnapshotTree(rig.LiveDirectory);

        var result = await rig.Builder.BuildAsync(scope);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, "staging 构建失败");
        StringAssert.Contains(result.Error!, "staging-engine-boom");
        Assert.AreEqual(SupplySwapOutcome.None, result.Swap!.Outcome);
        Assert.AreEqual(before, StagedSupplyTestHelpers.SnapshotTree(rig.LiveDirectory), "staging 失败不得动 live");
        Assert.IsFalse(Directory.Exists(rig.StagingRootFor(scope)), "staging 残留必须清理");
        Assert.AreEqual(0, rig.LiveEngine.BuildCallCount);
    }

    // ── R1：直写模式（对照）不做 staging、不做硬限 ───────────────────────

    [TestMethod]
    public async Task R1_Direct_Mode_Without_Staging_Writes_Live_And_Reports_No_Swap()
    {
        using var rig = new StagedRig(useStaging: false);

        var result = await rig.Builder.BuildAsync(rig.Fixture.ScopeFor(corpusBytes: 100_000_000));

        Assert.IsTrue(result.Success, "直写模式是 A1 的旧行为：不做预算硬限");
        Assert.IsNull(result.Swap, "直写模式没有切换，因此没有切换口径快照");
        Assert.AreEqual(1, rig.LiveEngine.BuildCallCount);
        Assert.IsEmpty(rig.StagingEngines);
        Assert.IsTrue(Directory.Exists(rig.LiveDirectory), "直写模式直接写 live 目录");
        Assert.IsFalse(Directory.Exists(rig.Fixture.StagingRoot()), "直写模式不得建 .staging");
    }

    // ── R3.4：trash 副本删除失败**不得**让 job 变失败 ───────────────────

    [TestMethod]
    public async Task R3_Trash_Delete_Failure_Is_Recorded_But_The_Job_Still_Succeeds()
    {
        FileStream? hold = null;
        try
        {
            using var rig = new StagedRig(
                stagingWriteBytes: 4096,
                swapperFactory: events => new FakeIndexDirectorySwapper(
                    events,
                    afterMove: (_, destination, call) =>
                    {
                        if (call != 1)
                            return;

                        // 旧 live 已被移入 .trash：钉住其中一个文件，让后续的「尽力删除」失败。
                        var locked = Path.Combine(destination, "locked.tmp");
                        File.WriteAllText(locked, "hold");
                        hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    }));

            var scope = rig.Fixture.ScopeFor(corpusBytes: 100);
            Directory.CreateDirectory(rig.LiveDirectory);
            File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "old.bin"), new byte[512]);
            var liveDirectory = rig.LiveDirectory;

            var result = await rig.Builder.BuildAsync(scope);

            Assert.IsTrue(result.Success, "live 已经就位 ⇒ job 必须算成功");
            Assert.AreEqual(SupplySwapOutcome.Swapped, result.Swap!.Outcome);
            Assert.IsNotNull(result.Swap.CleanupError, "删不掉的残留必须如实登记，不静默");
            StringAssert.Contains(result.Swap.CleanupError, "trash 残留删除失败");
            Assert.IsNotNull(result.Swap.TrashDirectory, "有残留时必须给出 trash 路径");
            Assert.IsTrue(Directory.Exists(result.Swap.TrashDirectory));
            Assert.AreEqual(4096L, new FileInfo(Path.Combine(liveDirectory, "segments_1")).Length);
        }
        finally
        {
            hold?.Dispose();
        }
    }

    // ── R5：清理失败不得中断供给 ────────────────────────────────────────

    [TestMethod]
    public async Task R5_Cleanup_Failure_Is_Recorded_And_Does_Not_Interrupt_The_Supply()
    {
        using var rig = new StagedRig(stagingWriteBytes: 4096, staleArtifactMaxAge: TimeSpan.FromHours(24));

        var lockedResidue = rig.Fixture.PlantResidue(".staging/old-locked", ageHours: 48);
        using var hold = new FileStream(
            Path.Combine(lockedResidue, "stale.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await rig.Builder.BuildAsync(rig.Fixture.ScopeFor(corpusBytes: 100));

        Assert.IsTrue(result.Success, "清理失败不得中断供给");
        Assert.IsNotNull(result.Swap);
        Assert.IsNotNull(result.Swap!.CleanupError);
        StringAssert.Contains(result.Swap.CleanupError, "删除失败");
        Assert.AreEqual(0, result.Swap.CleanedArtifactCount);
        Assert.IsTrue(Directory.Exists(lockedResidue), "删不掉的残留保持原样（可见、可后处理）");
    }

    // ── R6：可观察性字段齐全 ────────────────────────────────────────────

    [TestMethod]
    public async Task R6_Swap_Report_Carries_All_Five_Observability_Fields()
    {
        using var rig = new StagedRig(stagingWriteBytes: 2048);
        var scope = rig.Fixture.ScopeFor(corpusBytes: 512);

        Directory.CreateDirectory(rig.LiveDirectory);
        File.WriteAllBytes(Path.Combine(rig.LiveDirectory, "old.bin"), new byte[256]);

        var result = await rig.Builder.BuildAsync(scope);

        Assert.IsTrue(result.Success, result.Error);
        var described = result.Swap!.Describe();

        foreach (var field in new[] { "outcome=", "stagingBytes=", "liveBytesBefore=", "liveBytesAfter=", "budgetBytes=" })
            StringAssert.Contains(described, field, $"R6 要求终态可见性字段：{field}");

        StringAssert.Contains(described, "outcome=Swapped");
        Assert.AreEqual(2048L, result.Swap.StagingBytes);
        Assert.AreEqual(256L, result.Swap.LiveBytesBefore);
        Assert.AreEqual(2048L, result.Swap.LiveBytesAfter);
    }
}
