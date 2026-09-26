using System.Text;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
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
/// S3c：**跨进程租约（每批一次 / 有界等待等待不丢 / 取后重新 stat）+ 全局串行 + commit 后失效查询侧 reader + 并发可见性**。
/// <para>
/// 依据（唯一权威）：任务书 <c>temp/s3c-lease-and-visibility-task.md</c> §1~§8；设计原文
/// <c>temp/codex-plan-incremental-supply.md</c> §4.2 末条（<c>多 scope 增量提交默认全局串行</c>）、
/// §4.4（<c>每个合并批次获取一次</c> / <c>租约等待期间的变更不能丢</c> / <c>获取租约后必须重新 stat 最终状态</c>）、
/// §4.5（<c>未 commit 的文档对查询不可见</c> / <c>查询在批次期间继续看到上一个一致 commit</c> /
/// <c>commit 成功后显式 InvalidateScope</c>）。
/// </para>
/// <para>
/// 七条不变量 → 测试名逐条对应（L6 的调用计数与 L7 的可见性分开钉）：
/// <list type="bullet">
/// <item><description>L1 每批一次租约 + 无条件释放 ⇒
/// <c>L1_OneBatchAcquiresTheLeaseExactlyOnce_AndReleasesIt</c> +
/// <c>L1_LeaseIsReleasedOnCancelAndOnFailure_NeverReleasedWhenNotAcquired</c>。</description></item>
/// <item><description>L2 有界等待 + 等待期变更不丢 ⇒
/// <c>L2_MissingLease_IsBoundedWaited_ThenBusy_WithoutWritingAnything_AndReplayingTheSameChangeSetSucceeds</c>。</description></item>
/// <item><description>L3 取租约后重新 stat（内容变了 / 消失了两种终极状态）⇒
/// <c>L3_FileRewrittenWhileWaitingForTheLease_IsIndexedFromThePostLeaseStat</c> +
/// <c>L3_FileDeletedWhileWaitingForTheLease_IsNotReindexedFromThePreLeaseObservation</c>。</description></item>
/// <item><description>L4 多 scope 全局串行 ⇒ <c>L4_TwoScopesOnOneIndexRoot_AreGloballySerialized</c>。</description></item>
/// <item><description>L5 index-root 预算并发不被绕过 ⇒ <c>L5_TwoConcurrentBatches_CannotDoubleSpendTheCollectionBudget</c>。</description></item>
/// <item><description>L6 commit 成功 ⇒ 失效恰好 1 次；未提交 ⇒ 0 次 ⇒
/// <c>L6_InvalidationHappensExactlyOnceOnCommit_AndNeverWithoutCommit</c>。</description></item>
/// <item><description>L7 不见半批 + commit 后最终可见 ⇒
/// <c>L7_ReadersNeverSeeAHalfBatch_AndSeeTheNewCommitWithoutRestart</c>。</description></item>
/// </list>
/// 另有 §3 前置探测两条永久化：<c>Probe1_...</c>（已缓存 reader 是否自动刷新 ⇒ 决定 M5 的形态）与
/// <c>Probe2_...</c>（缓存 reader 是否阻止索引目录删除 ⇒ 决定「行为型 M5」是否可行）。
/// </para>
/// <para>
/// 红线：语料与索引根一律落在 <c>%TEMP%</c>（<see cref="Initialize"/> 有硬断言、绝不以 <c>D:\data</c> 开头）；
/// 本文件零后台线程（并发只用调用方线程上的异步交织 + <see cref="TaskCompletionSource"/>）、零 git 操作。
/// </para>
/// </summary>
[TestClass]
public sealed class LeaseAndVisibilityTests
{
    private const long DefaultMaxIndexBytes = 1_073_741_824L;
    private const int DefaultMaxPaths = 512;

    public TestContext TestContext { get; set; } = null!;

    private string _root = null!;
    private string _indexRoot = null!;
    private string _corpusA = null!;
    private string _corpusB = null!;
    private string _corpusC = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-fts-s3c-" + Guid.NewGuid().ToString("N"));

        // 硬断言（红线 R2）：语料与索引只允许落在 %TEMP% 下
        Assert.IsTrue(
            _root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            $"测试根必须在 %TEMP% 下，实际 {_root}");
        Assert.IsFalse(
            _root.StartsWith(@"D:\data", StringComparison.OrdinalIgnoreCase),
            $"测试根绝不允许落在生产数据根下，实际 {_root}");

        _indexRoot = Path.Combine(_root, "index");
        _corpusA = Path.Combine(_root, "corpusA");
        _corpusB = Path.Combine(_root, "corpusB");
        _corpusC = Path.Combine(_root, "corpusC");
        Directory.CreateDirectory(_corpusA);
        Directory.CreateDirectory(_corpusB);
        Directory.CreateDirectory(_corpusC);
    }

    /// <summary>
    /// R8：**失败路径**（断言失败 / 变异取红）也必须把 <c>%TEMP%</c> 清干净。
    /// <para>
    /// 旧写法是 <c>catch (IOException) { }</c> —— 静默吞掉清理失败 ⇒ 失败一轮就在 <c>%TEMP%</c> 留下
    /// <c>pudding-fts-*</c> 残留目录，且没有任何人被告知。这里改成：先删 → 必要时 GC + 短重试 →
    /// 仍失败就把残留**响亮报错**（测试转红），绝不静默。
    /// </para>
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        var remaining = TryDeleteRoot();

        for (var attempt = 0; attempt < 5 && remaining is not null; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(50);
            remaining = TryDeleteRoot();
        }

        Assert.IsNull(remaining, $"失败路径也必须清理 %TEMP%（残留目录 {_root}：{remaining}）");
    }

    private string? TryDeleteRoot()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);

            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    // ── L1：每个合并批次获取一次租约；批次结束无条件释放 ──────────────────

    [TestMethod]
    public async Task L1_OneBatchAcquiresTheLeaseExactlyOnce_AndReleasesIt()
    {
        var a = WriteCorpusFile(_corpusA, "a.txt", "alpha zzolda");
        var lease = new ScriptedLease();
        var invalidation = new CountingInvalidation();
        using var harness = NewHarness(_corpusA, lease, invalidation);

        var build = await harness.Search.BuildIndexAsync(_corpusA);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");

        File.WriteAllText(a, "alpha zznewa");

        // ★ scope 键故意**不是** NormalizeComparisonKey(ScopeRoot) 的产物：用来证明引擎把
        //   changeSet.ScopeKey **逐字**交给租约，而不是自己再规范化一份（任务书 §1「租约键直接用 ScopeKey」）。
        var changeSet = harness.ChangeSet(new[] { Upsert(a) }, scopeKey: "L1-SCOPE-KEY-VERBATIM");
        var result = await harness.Maintenance.ApplyChangesAsync(changeSet, harness.GenerousBudget());

        Assert.AreEqual(FullTextMutationState.Applied, result.State, result.Message);
        Assert.AreEqual(1, lease.TryCalls, "一个批次只能取一次租约（无争用时 TryAcquireAsync 恰好 1 次）");
        Assert.AreEqual(1, lease.AcquiredCount, "批次内恰好取得 1 次");
        Assert.AreEqual(1, lease.ReleaseCalls, "批次结束必须释放恰好 1 次");
        Assert.AreEqual(0, lease.RenewCalls, "维护批次不长持租约 ⇒ 本片不发生续期");
        Assert.AreEqual(0, lease.DescribeCalls, "成功路径不需要查询持有者");

        CollectionAssert.AreEqual(
            new[] { "L1-SCOPE-KEY-VERBATIM" },
            lease.TryScopeKeys.Distinct(StringComparer.Ordinal).ToArray(),
            "租约键必须逐字等于 changeSet.ScopeKey（不得再规范化一份）");
        Assert.AreEqual("L1-SCOPE-KEY-VERBATIM", result.ScopeKey);
        Assert.AreEqual(changeSet.BatchId, lease.LastJobId, "租约里记录的 job 必须是本批的 BatchId（可查「谁在跑哪个 job」）");
        Assert.AreEqual(
            SupplyLeaseOwner.ForCurrentProcess(SupplyLeaseRole.Maintenance).OwnerId,
            lease.LastOwnerId,
            "持有者身份必须是当前进程的**维护**角色（角色是身份的一部分，见 SupplyLeaseRole）");
        Assert.AreEqual(0, lease.ConcurrentHolders, "释放后不得还持有");

        Assert.AreEqual(1, invalidation.Count, "commit 成功 ⇒ 失效恰好 1 次");
        Assert.AreEqual(1, await CountHitsAsync(harness, "zznewa"));
        Assert.AreEqual(0, await CountHitsAsync(harness, "zzolda"));

        TestContext.WriteLine(
            $"L1 attempt={lease.TryCalls} acquired={lease.AcquiredCount} released={lease.ReleaseCalls} "
            + $"invalidate={invalidation.Count} scopeKey={lease.TryScopeKeys[0]} job={lease.LastJobId}");
    }

    [TestMethod]
    public async Task L1_LeaseIsReleasedOnCancelAndOnFailure_NeverReleasedWhenNotAcquired()
    {
        // ① 取消（租约**已取得**之后取消：写阶段里取消令牌）⇒ Cancelled + 释放 1 次
        {
            var a = WriteCorpusFile(_corpusA, "a.txt", "alpha zzcancelold");
            var analyzer = new HookAnalyzer();
            var lease = new ScriptedLease();
            var invalidation = new CountingInvalidation();
            using var harness = NewHarness(_corpusA, lease, invalidation, analyzer: analyzer);

            Assert.IsTrue((await harness.Search.BuildIndexAsync(_corpusA)).Success);
            File.WriteAllText(a, "alpha zzcancelnew");

            using var cts = new CancellationTokenSource();
            analyzer.Arm(() => cts.Cancel()); // 写阶段（AddDocument）里取消 ⇒ 提交前的取消检查命中

            var cancelled = await harness.Maintenance.ApplyChangesAsync(
                harness.ChangeSet(new[] { Upsert(a) }), harness.GenerousBudget(), cts.Token);

            Assert.IsTrue(analyzer.Fired, "仪器对照：写阶段钩子必须真的触发（否则本用例没测到取消路径）");
            Assert.AreEqual(FullTextMutationState.Cancelled, cancelled.State, cancelled.Message);
            Assert.IsNull(cancelled.CommitMilliseconds, "取消 ⇒ 未提交");
            Assert.AreEqual(1, lease.TryCalls);
            Assert.AreEqual(1, lease.ReleaseCalls, "取得租约后取消 ⇒ 必须先释放（否则同 scope 被自己永久 Busy）");
            Assert.AreEqual(0, lease.ConcurrentHolders);
            Assert.AreEqual(0, invalidation.Count, "未提交 ⇒ 不得失效 reader");
            Assert.AreEqual(0, await CountHitsAsync(harness, "zzcancelnew"), "取消批次不得写入");
            Assert.AreEqual(1, await CountHitsAsync(harness, "zzcancelold"), "旧 commit 必须完整保留");
        }

        // ② 异常（写阶段抛非取消异常）⇒ Failed + 释放 1 次 + rollback 保留旧 commit
        {
            var b = WriteCorpusFile(_corpusB, "b.txt", "beta zzfailold");
            var analyzer = new HookAnalyzer();
            var lease = new ScriptedLease();
            var invalidation = new CountingInvalidation();
            using var harness = NewHarness(_corpusB, lease, invalidation, analyzer: analyzer);

            Assert.IsTrue((await harness.Search.BuildIndexAsync(_corpusB)).Success);
            File.WriteAllText(b, "beta zzfailnew");

            analyzer.Arm(() => throw new InvalidOperationException("s3c-injected-write-phase-failure"));

            var failed = await harness.Maintenance.ApplyChangesAsync(
                harness.ChangeSet(new[] { Upsert(b) }), harness.GenerousBudget());

            Assert.IsTrue(analyzer.Fired, "仪器对照：写阶段钩子必须真的触发");
            Assert.AreEqual(FullTextMutationState.Failed, failed.State, $"异常必须落 Failed：{failed.Message}");
            Assert.IsNull(failed.CommitMilliseconds, "异常 ⇒ 未提交");
            Assert.AreEqual(1, lease.TryCalls);
            Assert.AreEqual(1, lease.ReleaseCalls, "异常路径也必须释放租约");
            Assert.AreEqual(0, lease.ConcurrentHolders);
            Assert.AreEqual(0, invalidation.Count, "未提交 ⇒ 不得失效 reader");
            Assert.AreEqual(0, await CountHitsAsync(harness, "zzfailnew"), "失败批次不得写入");
            Assert.AreEqual(1, await CountHitsAsync(harness, "zzfailold"), "rollback ⇒ 旧 commit 完整");
        }

        // ③ 未取得（等待期取消）⇒ Cancelled + **不得**释放（没拿到的东西不能去释放）
        {
            var c = WriteCorpusFile(_corpusC, "c.txt", "gamma zzinitialc");
            var lease = new ScriptedLease { FailAll = true };
            var invalidation = new CountingInvalidation();
            using var harness = NewHarness(_corpusC, lease, invalidation);

            Assert.IsTrue((await harness.Search.BuildIndexAsync(_corpusC)).Success);
            File.WriteAllText(c, "gamma zzneverowned"); // 变更发生在构建之后 ⇒ 只有本批能把它写进索引

            using var cts = new CancellationTokenSource();
            lease.OnFirstTryAcquire = () => cts.Cancel(); // 第一次尝试之后立刻取消 ⇒ Task.Delay(ct) 抛出

            var cancelled = await harness.Maintenance.ApplyChangesAsync(
                harness.ChangeSet(new[] { Upsert(c) }), harness.GenerousBudget(), cts.Token);

            Assert.AreEqual(FullTextMutationState.Cancelled, cancelled.State, cancelled.Message);
            Assert.AreEqual(1, lease.TryCalls);
            Assert.AreEqual(0, lease.AcquiredCount);
            Assert.AreEqual(0, lease.ReleaseCalls, "未取得租约 ⇒ 不得调用 ReleaseAsync");
            Assert.AreEqual(0, invalidation.Count);
            Assert.AreEqual(0, await CountHitsAsync(harness, "zzneverowned"));
        }
    }

    // ── L2：有界等待；超时 ⇒ Busy、未写入、未推进 checkpoint、下一轮重放成功 ──

    [TestMethod]
    public async Task L2_MissingLease_IsBoundedWaited_ThenBusy_WithoutWritingAnything_AndReplayingTheSameChangeSetSucceeds()
    {
        var a = WriteCorpusFile(_corpusA, "a.txt", "alpha zzbusyold");

        var busyLease = new ScriptedLease { FailAll = true };
        var busyInvalidation = new CountingInvalidation();
        var waitUpperBound = TimeSpan.FromMilliseconds(150);
        using (var harness = NewHarness(_corpusA, busyLease, busyInvalidation, waitUpperBound))
        {
            var build = await harness.Search.BuildIndexAsync(_corpusA);
            Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");

            var indexDirectory = harness.IndexDirectory;
            var filesBefore = ListFiles(indexDirectory);
            var bytesBefore = MeasureDirectoryBytes(indexDirectory);
            var inventoryBefore = await InventoryAsync(harness);

            File.WriteAllText(a, "alpha zzbusynew");
            var changeSet = harness.ChangeSet(new[] { Upsert(a) });

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var busy = await harness.Maintenance.ApplyChangesAsync(changeSet, harness.GenerousBudget());
            stopwatch.Stop();

            Assert.AreEqual(FullTextMutationState.Busy, busy.State, $"拿不到租约必须返回 Busy：{busy.Message}");
            StringAssert.Contains(busy.Message, "跨进程租约", "Busy 消息必须说清是跨进程租约导致的");
            Assert.IsTrue(
                busyLease.TryCalls >= 2,
                $"有界等待必须**真的重试**（否则「等 150ms 再放弃」与「试一次就放弃」不可区分）：实际 {busyLease.TryCalls} 次");
            Assert.IsTrue(
                stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"有界等待必须有上界（实测 {stopwatch.Elapsed.TotalMilliseconds:F0} ms，上界 {waitUpperBound.TotalMilliseconds:F0} ms）");

            // 未写入任何字节 / 未推进 checkpoint / 未失效 reader
            CollectionAssert.AreEqual(
                filesBefore,
                ListFiles(indexDirectory),
                "Busy ⇒ 索引目录文件集合必须逐字不变（未写入任何字节）");
            Assert.AreEqual(bytesBefore, MeasureDirectoryBytes(indexDirectory), "Busy ⇒ 索引目录字节不变");
            Assert.AreEqual(bytesBefore, busy.IndexBytesAfter, "Busy ⇒ 引擎实测的提交后字节 == 提交前字节");
            Assert.IsFalse(busy.CheckpointAdvanced, "Busy ⇒ checkpoint 不得推进");
            Assert.IsNull(busy.CommitMilliseconds, "Busy ⇒ 不得 commit");
            Assert.AreEqual(0, busyInvalidation.Count, "未提交 ⇒ 0 次失效");

            Assert.AreEqual(0, await CountHitsAsync(harness, "zzbusynew"), "Busy ⇒ 新内容不可见");
            Assert.AreEqual(1, await CountHitsAsync(harness, "zzbusyold"), "Busy ⇒ 旧内容必须完好");

            TestContext.WriteLine(
                $"L2 attempts={busyLease.TryCalls} acquired={busyLease.AcquiredCount} released={busyLease.ReleaseCalls} "
                + $"elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0} boundMs={waitUpperBound.TotalMilliseconds:F0} "
                + $"inventory={Describe(inventoryBefore)} files={filesBefore.Length} bytes={bytesBefore}");

            Assert.AreEqual(0, busyLease.ReleaseCalls, "未取得租约 ⇒ 不得释放");
        }

        // ★ 「等待期间变更不丢」的可测形式：**同一变更集下一轮重放必须成功**
        var replayLease = new ScriptedLease();
        var replayInvalidation = new CountingInvalidation();
        using (var replay = NewHarness(_corpusA, replayLease, replayInvalidation))
        {
            var replayed = await replay.Maintenance.ApplyChangesAsync(
                replay.ChangeSet(new[] { Upsert(a) }),
                replay.GenerousBudget());

            Assert.AreEqual(FullTextMutationState.Applied, replayed.State, $"重放必须成功：{replayed.Message}");
            Assert.IsTrue(replayed.CheckpointAdvanced, "重放成功后允许推进 checkpoint");
            Assert.AreEqual(1, await CountHitsAsync(replay, "zzbusynew"), "重放 ⇒ 等待期间被搁置的变更必须真的落库");
            Assert.AreEqual(0, await CountHitsAsync(replay, "zzbusyold"));
            Assert.AreEqual(1, replayInvalidation.Count, "重放发生了一次真实 commit ⇒ 失效 1 次");
        }
    }

    // ── L3：取得租约**之后**重新 stat 最终状态 ────────────────────────────

    [TestMethod]
    public async Task L3_FileRewrittenWhileWaitingForTheLease_IsIndexedFromThePostLeaseStat()
    {
        var a = WriteCorpusFile(_corpusA, "a.txt", "alpha zzpreleasemark");

        // 确定性构造：第一次取租约**失败**，并在这一次失败里把文件内容换掉（= 等待期间的变更）。
        // 引擎只有在「取得租约之后」才 stat，才会看到新内容 ⇒ 本用例能把顺序倒置实现变红。
        var lease = new ScriptedLease
        {
            Inner = RealFileLease(),
            AcquireFailuresRemaining = 1,
            OnFirstTryAcquire = () => File.WriteAllText(a, "alpha zzpostleasemark"),
        };
        var invalidation = new CountingInvalidation();
        using var harness = NewHarness(_corpusA, lease, invalidation);

        var build = await harness.Search.BuildIndexAsync(_corpusA);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");
        Assert.AreEqual(1, await CountHitsAsync(harness, "zzpreleasemark"), "前置：旧内容确实已入库");

        var result = await harness.Maintenance.ApplyChangesAsync(
            harness.ChangeSet(new[] { Upsert(a) }), harness.GenerousBudget());

        Assert.AreEqual(FullTextMutationState.Applied, result.State, result.Message);
        Assert.AreEqual(2, lease.TryCalls, "本用例要求「先失败一次、等待后再取得」（证明确实经过了等待窗口）");
        Assert.AreEqual(1, lease.ReleaseCalls);
        Assert.AreEqual(1, result.UpsertAppliedCount);

        Assert.AreEqual(1, await CountHitsAsync(harness, "zzpostleasemark"), "必须写入等待**之后**的磁盘事实（新内容）");
        Assert.AreEqual(0, await CountHitsAsync(harness, "zzpreleasemark"), "绝不允许把等待前的旧内容写入");
        Assert.AreEqual(1, invalidation.Count, "commit 成功 ⇒ 失效 1 次");

        TestContext.WriteLine(
            $"L3_REWRITE attempts={lease.TryCalls} released={lease.ReleaseCalls} "
            + $"hits_new={await CountHitsAsync(harness, "zzpostleasemark")} hits_old={await CountHitsAsync(harness, "zzpreleasemark")}");
    }

    [TestMethod]
    public async Task L3_FileDeletedWhileWaitingForTheLease_IsNotReindexedFromThePreLeaseObservation()
    {
        var a = WriteCorpusFile(_corpusA, "a.txt", "alpha zzvanishmark");

        var lease = new ScriptedLease
        {
            Inner = RealFileLease(),
            AcquireFailuresRemaining = 1,
            OnFirstTryAcquire = () => File.Delete(a), // 等待期间文件消失
        };
        var invalidation = new CountingInvalidation();
        using var harness = NewHarness(_corpusA, lease, invalidation);

        Assert.IsTrue((await harness.Search.BuildIndexAsync(_corpusA)).Success);
        Assert.AreEqual(1, await CountHitsAsync(harness, "zzvanishmark"));

        var result = await harness.Maintenance.ApplyChangesAsync(
            harness.ChangeSet(new[] { Upsert(a) }), harness.GenerousBudget());

        // 既有语义（S3a §3.6③）：最终 stat 读不到 ⇒ **保留旧文档**并登记待重试，绝不在这里产出 Delete
        // ⇒ 本批「无事可写」：不打开 writer、不 commit。
        Assert.AreEqual(FullTextMutationState.PartiallyApplied, result.State, result.Message);
        Assert.AreEqual(1, result.RetainedOldCount, "读不到 ⇒ 进入「保留旧文档」集合");
        Assert.AreEqual(0, result.UpsertAppliedCount, "消失的文件不得被重新写进去");
        Assert.IsNull(result.CommitMilliseconds, "无事可写 ⇒ 不得 commit");
        Assert.AreEqual(0, invalidation.Count, "未提交 ⇒ 0 次失效");
        Assert.AreEqual(2, lease.TryCalls, "必须经过等待窗口后再 stat");

        Assert.AreEqual(1, await CountHitsAsync(harness, "zzvanishmark"), "旧文档必须保留（不得因读不到而当作删除）");

        TestContext.WriteLine(
            $"L3_DELETE attempts={lease.TryCalls} state={result.State} retained={result.RetainedOldCount} "
            + $"commitMs={result.CommitMilliseconds?.ToString() ?? "null"} invalidate={invalidation.Count}");
    }

    // ── L4：多 scope 全局串行 ─────────────────────────────────────────────

    [TestMethod]
    public async Task L4_TwoScopesOnOneIndexRoot_AreGloballySerialized()
    {
        var fa = WriteCorpusFile(_corpusA, "a.txt", "alpha zzseriala");
        var fb = WriteCorpusFile(_corpusB, "b.txt", "beta zzserialb");

        var probe = new ConcurrencyProbe();
        // A 批：第一次取租约即**挂起**在租约里（此时它已经持有全局 index-root gate）
        var leaseA = new ScriptedLease(probe) { BlockFirstTry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var leaseB = new ScriptedLease(probe);
        var invalidationA = new CountingInvalidation();
        var invalidationB = new CountingInvalidation();

        using var harnessA = NewHarness(_corpusA, leaseA, invalidationA);
        using var harnessB = NewHarness(_corpusB, leaseB, invalidationB);

        Assert.IsTrue((await harnessA.Search.BuildIndexAsync(_corpusA)).Success);
        Assert.IsTrue((await harnessB.Search.BuildIndexAsync(_corpusB)).Success);

        var tA = harnessA.Maintenance.ApplyChangesAsync(harnessA.ChangeSet(new[] { Upsert(fa) }), harnessA.GenerousBudget());
        Assert.IsFalse(tA.IsCompleted, "A 批必须真的挂起在租约里（否则下面的并发断言没有意义）");

        // A 批已进入临界区（持全局 gate + per-scope gate，正阻塞在租约内）；此刻启动 B 批：
        var tB = harnessB.Maintenance.ApplyChangesAsync(harnessB.ChangeSet(new[] { Upsert(fb) }), harnessB.GenerousBudget());
        var bBlockedWhileAHeld = leaseB.TryCalls == 0;

        Assert.IsTrue(bBlockedWhileAHeld, "★ B 批在 A 批持有全局写者 gate 期间**根本没走到取租约**（全局 gate 在租约外层）");
        Assert.IsFalse(tB.IsCompleted, "★ B 批必须还没完成（写者会话不得与 A 重叠）");
        Assert.AreEqual(1, leaseA.TryCalls, "A 批必须已经进入取租约那一步（此刻阻塞在租约内）");
        Assert.AreEqual(0, probe.ActiveHolders, "A 尚未完成取租约 ⇒ 此刻还没有活跃持有者（活跃峰值在两者都结束后看）");

        leaseA.ReleaseFirstTry(); // 放开 A

        var results = await Task.WhenAll(tA, tB);
        Assert.AreEqual(FullTextMutationState.Applied, results[0].State, results[0].Message);
        Assert.AreEqual(FullTextMutationState.Applied, results[1].State, results[1].Message);
        Assert.AreEqual(1, leaseA.TryCalls);
        Assert.AreEqual(1, leaseB.TryCalls);
        Assert.AreEqual(1, leaseA.ReleaseCalls);
        Assert.AreEqual(1, leaseB.ReleaseCalls);
        Assert.AreEqual(1, invalidationA.Count);
        Assert.AreEqual(1, invalidationB.Count);

        // 租约持有区间不重叠 ⇒ 写者会话不重叠（租约覆盖从「取到」到「提交/回滚完成」的整段写者会话）
        Assert.AreEqual(1, probe.MaxActiveHolders, "两个 scope 的写者会话在所有时刻都不得重叠");
        Assert.AreEqual(0, probe.ActiveHolders);

        Assert.AreEqual(1, await CountHitsAsync(harnessA, "zzseriala"));
        Assert.AreEqual(1, await CountHitsAsync(harnessB, "zzserialb"));

        TestContext.WriteLine(
            $"L4 maxActiveHolders={probe.MaxActiveHolders} leaseA(try={leaseA.TryCalls}) "
            + $"leaseB(try={leaseB.TryCalls}) bBlockedWhileAHeld={bBlockedWhileAHeld}");
    }

    // ── L5：index-root 预算门禁在并发下不被绕过 ────────────────────────────

    [TestMethod]
    public async Task L5_TwoConcurrentBatches_CannotDoubleSpendTheCollectionBudget()
    {
        var fa = WriteCorpusFile(_corpusA, "a.txt", BigContent(1200, "zzl5a"));
        var fb = WriteCorpusFile(_corpusB, "b.txt", BigContent(1200, "zzl5b"));
        var fc = WriteCorpusFile(_corpusC, "c.txt", BigContent(1200, "zzl5c"));

        var probe = new ConcurrencyProbe();
        using var harnessA = NewHarness(_corpusA, new ScriptedLease(probe), new CountingInvalidation());
        using var harnessB = NewHarness(_corpusB, new ScriptedLease(probe), new CountingInvalidation());
        using var harnessC = NewHarness(_corpusC, new ScriptedLease(), new CountingInvalidation());

        Assert.IsTrue((await harnessA.Search.BuildIndexAsync(_corpusA)).Success);
        Assert.IsTrue((await harnessB.Search.BuildIndexAsync(_corpusB)).Success);
        Assert.IsTrue((await harnessC.Search.BuildIndexAsync(_corpusC)).Success);

        // ③ 标定：C 批单独跑一次（把语料**写大一倍**），实测一个批次的
        //    · 写出字节 W（QuotaEnforcingDirectory 的保守上界，含 merge 输出）
        //    · 净增长 G（集合口径）
        File.WriteAllText(fc, BigContent(2400, "zzl5c2"));
        var liveBeforeCalibration = MeasureLiveIndexBytes();
        var (calibrated, calibrationReport) = await harnessC.Maintenance.ApplyChangesWithReportAsync(
            harnessC.ChangeSet(new[] { Upsert(fc) }),
            harnessC.GenerousBudget());
        Assert.AreEqual(FullTextMutationState.Applied, calibrated.State, calibrated.Message);
        var live0 = MeasureLiveIndexBytes();
        var writtenPerBatch = calibrationReport.BytesWrittenByWriter;
        var netGrowthPerBatch = live0 - liveBeforeCalibration;
        Assert.IsTrue(writtenPerBatch > 0, $"标定必须写出正字节（含 merge）：{calibrationReport.ToJsonLine()}");
        Assert.IsTrue(netGrowthPerBatch > 0, $"标定必须产生正净增长，实际 {netGrowthPerBatch}");

        // ④ 预算 = live0 + W + G/2：
        //    · 第一块：允许增长 = W + G/2 ≥ W（它需要 ≈ W）⇒ 必须成功
        //    · 第二块：允许增长 = (W + G/2) − G = W − G/2 < W（它仍需要 ≈ W）⇒ 必须被拒
        var maxIndexBytes = live0 + writtenPerBatch + (netGrowthPerBatch / 2);
        var statA = new FileInfo(fa).Length;
        var statB = new FileInfo(fb).Length;

        File.WriteAllText(fa, BigContent(2400, "zzl5a2"));
        File.WriteAllText(fb, BigContent(2400, "zzl5b2"));

        // 两块批次用的是**同一份陈旧 live**（调用方在进锁之前量的）—— 这是并发双花的发生条件
        var staleBudgetA = new FullTextMutationBudget(maxIndexBytes, live0, DefaultMaxPaths);
        var staleBudgetB = new FullTextMutationBudget(maxIndexBytes, live0, DefaultMaxPaths);

        // 仪器对照：单看调用方这两份**陈旧**预算，两块批次都「合得上」⇒ 若无临界区内重测，两块都会照写，
        // 合计必然超过预算。本用例正是钉死「重测把这条路堵住」。
        Assert.IsTrue(
            SupplyBudgetCalculator.Fits(live0, statA, maxIndexBytes),
            $"敏感性前提：陈旧预算下 A 批必须判为合规（live={live0} incoming={statA} budget={maxIndexBytes}）");
        Assert.IsTrue(
            SupplyBudgetCalculator.Fits(live0, statB, maxIndexBytes),
            $"敏感性前提：陈旧预算下 B 批必须判为合规（live={live0} incoming={statB} budget={maxIndexBytes}）");

        var tA = harnessA.Maintenance.ApplyChangesAsync(harnessA.ChangeSet(new[] { Upsert(fa) }), staleBudgetA);
        var tB = harnessB.Maintenance.ApplyChangesAsync(harnessB.ChangeSet(new[] { Upsert(fb) }), staleBudgetB);
        var results = await Task.WhenAll(tA, tB);

        var liveFinal = MeasureLiveIndexBytes();
        var applied = results.Count(r => r.State == FullTextMutationState.Applied);
        var rejected = results.Count(r => r.State == FullTextMutationState.Rejected);

        TestContext.WriteLine(
            $"L5 live0={live0} writtenPerBatch={writtenPerBatch} netGrowthPerBatch={netGrowthPerBatch} max={maxIndexBytes} "
            + $"liveFinal={liveFinal} statA={statA} statB={statB} applied={applied} rejected={rejected} "
            + $"maxActiveHolders={probe.MaxActiveHolders} states=[{results[0].State},{results[1].State}]");

        Assert.IsTrue(
            liveFinal <= maxIndexBytes,
            $"★ 两个 scope 并发提交后，集合口径的 live 字节 {liveFinal} 不得突破预算 {maxIndexBytes}（超出 {liveFinal - maxIndexBytes}）");
        Assert.IsTrue(applied >= 1, "预算必须真的支撑起一块批次（否则本用例是空洞的：什么都没发生也能满足 ≤ 预算）");
        Assert.IsTrue(liveFinal > live0, "必须真的发生过写入（否则「未超预算」不是结论）");
        Assert.IsTrue(rejected >= 1, "预算只够一个批次 ⇒ 至少一块必须被拒绝（否则就是并发双花）");
        Assert.AreEqual(2, applied + rejected, "两块批次都必须有明确终态（Applied / Rejected）");
        Assert.AreEqual(1, probe.MaxActiveHolders, "全局串行 ⇒ 两个写者会话不得重叠");

        // 被拒的那一块不得留下任何写入痕迹（终态是 Rejected 而不是半成品）
        foreach (var rejectedResult in results.Where(r => r.State == FullTextMutationState.Rejected))
        {
            Assert.IsNull(rejectedResult.CommitMilliseconds, rejectedResult.Message);
            Assert.IsFalse(rejectedResult.CheckpointAdvanced, rejectedResult.Message);
        }
    }

    // ── L6：commit 成功 ⇒ 失效恰好 1 次；未提交 ⇒ 0 次 ────────────────────

    [TestMethod]
    public async Task L6_InvalidationHappensExactlyOnceOnCommit_AndNeverWithoutCommit()
    {
        var a = WriteCorpusFile(_corpusA, "a.txt", "alpha zzinvalold");
        var lease = new ScriptedLease();
        var invalidation = new CountingInvalidation();
        using var harness = NewHarness(_corpusA, lease, invalidation);

        Assert.IsTrue((await harness.Search.BuildIndexAsync(_corpusA)).Success);

        // ① 空变更集：Applied 但**没有 commit** ⇒ 0 次（「未提交 ⇒ 0 次」的关键反例）
        var empty = await harness.Maintenance.ApplyChangesAsync(
            harness.ChangeSet(Array.Empty<FullTextFileChange>()),
            harness.GenerousBudget());
        Assert.AreEqual(FullTextMutationState.Applied, empty.State, empty.Message);
        Assert.IsNull(empty.CommitMilliseconds, "空变更集不打开 writer、不 commit");
        Assert.AreEqual(0, invalidation.Count, "未提交（无事可写）⇒ 不得失效 reader");

        // ② 被门禁拒绝（写前预检不通过）⇒ 0 次
        var rejected = await harness.Maintenance.ApplyChangesAsync(
            harness.ChangeSet(new[] { Upsert(a) }),
            new FullTextMutationBudget(MaxIndexBytes: 64, LiveIndexBytes: 64, MaxPaths: DefaultMaxPaths));
        Assert.AreEqual(FullTextMutationState.Rejected, rejected.State, rejected.Message);
        Assert.AreEqual(0, invalidation.Count, "Rejected ⇒ 不得失效 reader");

        // ③ 拿到不租约（Busy）⇒ 0 次
        lease.FailAll = true;
        var busy = await harness.Maintenance.ApplyChangesAsync(
            harness.ChangeSet(new[] { Upsert(a) }),
            harness.GenerousBudget());
        Assert.AreEqual(FullTextMutationState.Busy, busy.State, busy.Message);
        Assert.AreEqual(0, invalidation.Count, "Busy ⇒ 不得失效 reader");
        lease.FailAll = false;

        // ④ 真正提交一次 ⇒ 恰好 1 次，且参数是 changeSet.ScopeRoot（语料根，不是索引目录）
        File.WriteAllText(a, "alpha zzinvalnew");
        var changeSet = harness.ChangeSet(new[] { Upsert(a) });
        var applied = await harness.Maintenance.ApplyChangesAsync(changeSet, harness.GenerousBudget());

        Assert.AreEqual(FullTextMutationState.Applied, applied.State, applied.Message);
        Assert.IsNotNull(applied.CommitMilliseconds, "本批必须真的提交过");
        Assert.AreEqual(1, invalidation.Count, "commit 成功 ⇒ 失效恰好 1 次");
        CollectionAssert.AreEqual(
            new[] { changeSet.ScopeRoot },
            invalidation.ScopeRoots.ToArray(),
            "失效参数必须是 changeSet.ScopeRoot（语料根）");
        Assert.AreEqual(0, invalidation.DelegatedCount, "本用例的替身刻意**不**转发给真实引擎（只做计数）");

        TestContext.WriteLine(
            $"L6 invalidateCount={invalidation.Count} scopes=[{string.Join(',', invalidation.ScopeRoots)}] "
            + $"empty(commit={empty.CommitMilliseconds?.ToString() ?? "null"}) rejected/busy=0");
    }

    // ── L7：不见半批 + commit 后最终可见 ──────────────────────────────────

    [TestMethod]
    public async Task L7_ReadersNeverSeeAHalfBatch_AndSeeTheNewCommitWithoutRestart()
    {
        var a = WriteCorpusFile(_corpusA, "a.txt", "alpha zzolda\nsecond zzolda");
        var b = WriteCorpusFile(_corpusA, "b.txt", "beta zzbetaline");
        var c = Path.Combine(_corpusA, "c.txt"); // 本批新增

        var analyzer = new HookAnalyzer();
        var lease = new ScriptedLease();
        var invalidation = new CountingInvalidation();
        using var harness = NewHarness(_corpusA, lease, invalidation, analyzer: analyzer);

        // L7 走**真实**失效接缝（本用例验证的是端到端可见性，不是计数）
        invalidation.AttachToRealEngine(harness.Search);

        var build = await harness.Search.BuildIndexAsync(_corpusA);
        Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");

        // 预热查询侧缓存（本用例要考的就是「已缓存的 reader 在批次进行中会看到什么」）
        Assert.AreEqual(2, await CountHitsAsync(harness, "zzolda"), "前置：a.txt 两行 ⇒ 2 个文档");
        Assert.AreEqual(1, await CountHitsAsync(harness, "zzbetaline"));

        var committedBefore = ReadLiveRows(harness.IndexDirectory);
        Assert.AreEqual(3, committedBefore.Count, $"前置：上一个一致 commit 恰好 3 行文档：{string.Join('|', committedBefore)}");

        // 本批：改写 a.txt（新内容）、新增 c.txt、删除 b.txt —— 三条动作跨越 delete+add，最能暴露「半批」
        File.WriteAllText(a, "alpha zznewmarker");
        File.WriteAllText(c, "gamma zzgammamarker");
        File.Delete(b);

        var probe = new BatchPhaseProbe();
        analyzer.Arm(() =>
        {
            // ★ 这里在 **write phase 内部**（DeleteDocuments 与 AddDocument 已在内存中执行、尚未 commit）
            probe.HitsNew = CountHitsAsync(harness, "zznewmarker").GetAwaiter().GetResult();
            probe.HitsOld = CountHitsAsync(harness, "zzolda").GetAwaiter().GetResult();
            probe.HitsDeleted = CountHitsAsync(harness, "zzbetaline").GetAwaiter().GetResult();
            probe.HitsAdded = CountHitsAsync(harness, "zzgammamarker").GetAwaiter().GetResult();
            probe.CommittedRows = ReadLiveRows(harness.IndexDirectory);
        });

        var result = await harness.Maintenance.ApplyChangesAsync(
            harness.ChangeSet(new[]
            {
                Upsert(a),
                Upsert(c),
                new FullTextFileChange(b, FullTextChangeKind.Delete, null, null, FullTextChangeSource.Watcher),
            }),
            harness.GenerousBudget());

        Assert.IsTrue(analyzer.Fired, "仪器对照：写阶段探针必须真的触发（否则「不见半批」是空洞的）");
        Assert.AreEqual(FullTextMutationState.Applied, result.State, result.Message);

        // ① 批次进行中：另一个 reader 只看到**上一个一致 commit**，绝无半批
        Assert.AreEqual(0, probe.HitsNew, "★ 批次进行中不得看到新内容（未 commit 的文档对查询不可见）");
        Assert.AreEqual(2, probe.HitsOld, "★ 批次进行中必须仍看到旧内容（批次期间查询看到上一个一致 commit）");
        Assert.AreEqual(1, probe.HitsDeleted, "★ 批次进行中不得提前看到「已删除」的旧路径");
        Assert.AreEqual(0, probe.HitsAdded, "★ 批次进行中不得看到新加入的路径");
        CollectionAssert.AreEqual(
            committedBefore,
            probe.CommittedRows,
            "★ 批次进行中的可见文档集合必须与上一个 commit **逐字相同**（既不是半批，也不得混合）");

        // ② commit 返回后：无需重启任何东西即可见新内容、且看不到已删内容
        // ⚠️ 本用例**只**考可见性：失效调用的**计数**由 L6 钉死，这里刻意不重复断言。
        // 理由（实测）：本进程内可见性靠 `GetOrRefreshSearcher` 自刷新（见 Probe1），删除失效调用也照样能看到新内容
        // ⇒ 若在这里再断言计数，M5 变异会因为「计数不匹配」而红，反而掩盖了「可见性自身测不到该差异」这个真结论。
        TestContext.WriteLine($"L7 invalidateCount={invalidation.Count}（计数断言见 L6）");
        Assert.AreEqual(1, await CountHitsAsync(harness, "zznewmarker"), "★ commit 后必须可见新内容（无需重启）");
        Assert.AreEqual(0, await CountHitsAsync(harness, "zzolda"), "★ commit 后看不到被替换掉的旧内容");
        Assert.AreEqual(1, await CountHitsAsync(harness, "zzgammamarker"), "★ commit 后可见新加入的文件");
        Assert.AreEqual(0, await CountHitsAsync(harness, "zzbetaline"), "★ commit 后看不到已删除的文件");

        var committedAfter = ReadLiveRows(harness.IndexDirectory);
        Assert.AreEqual(2, committedAfter.Count, $"新 commit 恰好 2 行文档：{string.Join('|', committedAfter)}");
        Assert.IsFalse(committedAfter.Any(r => r.Contains("b.txt", StringComparison.Ordinal)), "已删路径不得留在 commit 里");
        Assert.IsFalse(committedAfter.Any(r => r.Contains("zzolda", StringComparison.Ordinal)));

        TestContext.WriteLine(
            $"L7 midWriteState new={probe.HitsNew} old={probe.HitsOld} deletedPath={probe.HitsDeleted} addedPath={probe.HitsAdded} "
            + $"rowsBefore={committedBefore.Count} rowsMid={probe.CommittedRows.Count} rowsAfter={committedAfter.Count}");
    }

    // ── §3 前置探测（永久化：它决定 M5 的形态，必须可复核）────────────────

    /// <summary>
    /// 探测 1：**已缓存的 reader 在另一个 writer commit 之后是否自动刷新？**
    /// <para>
    /// 做法：先用一次搜索把 reader 放进缓存；再用「不转发」的失效替身跑一次真实 commit
    /// （引擎会调用失效接缝，但替身什么都不做）⇒ 此时查询侧缓存**确实没被显式失效**。
    /// 若随后的搜索仍能看到新内容 ⇒ 说明 <c>GetOrRefreshSearcher</c> 里的
    /// <c>DirectoryReader.OpenIfChanged</c> 自刷新生效 ⇒ **删除 InvalidateScope 的变异在进程内取不了红**。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Probe1_QuerySideReaderCache_AutoRefreshesAfterInPlaceCommit()
    {
        var a = WriteCorpusFile(_corpusA, "a.txt", "alpha zzprobe1old");

        var lease = new ScriptedLease();
        var invalidation = new CountingInvalidation(); // 只计数、**不转发** ⇒ 缓存不会被显式失效
        using var harness = NewHarness(_corpusA, lease, invalidation);

        Assert.IsTrue((await harness.Search.BuildIndexAsync(_corpusA)).Success);
        Assert.AreEqual(1, await CountHitsAsync(harness, "zzprobe1old"), "预热：把 reader 放进缓存");

        File.WriteAllText(a, "alpha zzprobe1new");
        var result = await harness.Maintenance.ApplyChangesAsync(
            harness.ChangeSet(new[] { Upsert(a) }), harness.GenerousBudget());
        Assert.AreEqual(FullTextMutationState.Applied, result.State, result.Message);

        Assert.AreEqual(1, invalidation.Count, "引擎**确实**请求了失效（计数 1）");
        Assert.AreEqual(0, invalidation.DelegatedCount, "但替身没有真的失效缓存 ⇒ 本探测测的就是自刷新");

        var newHits = await CountHitsAsync(harness, "zzprobe1new");
        var oldHits = await CountHitsAsync(harness, "zzprobe1old");

        TestContext.WriteLine(
            $"PROBE1 autoRefresh={(newHits == 1 && oldHits == 0 ? "YES" : "NO")} "
            + $"newHitsWithoutExplicitInvalidation={newHits} oldHitsWithoutExplicitInvalidation={oldHits} "
            + $"invalidationRequested={invalidation.Count} actuallyDelegated={invalidation.DelegatedCount}");

        Assert.AreEqual(
            1,
            newHits,
            "★ 探测结论：缓存未被显式失效时，搜索**仍**能看到新 commit ⇒ GETORREFRESHSearcher 会自刷新 "
            + "⇒ 任务书 §6 的原形态 M5（删掉 InvalidateScope ⇒ 可见性红）在本进程内取不了红，必须换等价变异");
        Assert.AreEqual(0, oldHits, "自刷新后旧内容必须消失");
    }

    /// <summary>
    /// 探测 2：**缓存中的 reader 是否阻止索引目录被删除？**（决定「行为型 M5」是否可行）
    /// <para>
    /// 若这里变红（删除被句柄挡住），说明平台句柄语义变了 ⇒ M5 可以改用行为型变异重新评估；
    /// 当前实测结论见 <c>temp/s3c-report.md</c>。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Probe2_CachedReader_DoesNotBlockIndexDirectoryDeletion()
    {
        var a = WriteCorpusFile(_corpusA, "a.txt", "alpha zzprobe2");
        using var harness = NewHarness(_corpusA, new ScriptedLease(), new CountingInvalidation());

        Assert.IsTrue((await harness.Search.BuildIndexAsync(_corpusA)).Success);
        Assert.AreEqual(1, await CountHitsAsync(harness, "zzprobe2"), "预热：缓存里确实有 reader");

        var indexDirectory = harness.IndexDirectory;
        Assert.IsTrue(Directory.Exists(indexDirectory));

        string outcome;
        try
        {
            Directory.Delete(indexDirectory, recursive: true);
            outcome = "deleted-ok";
        }
        catch (Exception ex)
        {
            outcome = $"{ex.GetType().Name}: {ex.Message}";
        }

        TestContext.WriteLine($"PROBE2 deleteWithCachedReader={outcome}");

        Assert.AreEqual(
            "deleted-ok",
            outcome,
            "实测结论：缓存中的 reader **不**阻止索引目录删除（无 FILE_SHARE_DELETE 依赖）"
            + " ⇒ 任务书 §6 M5 的「行为型等价变异」不可用，只能取「调用计数」形态的等价变异");
    }

    // ── 测试替身 ──────────────────────────────────────────────────────────

    /// <summary>可编排租约替身：计数 + 可注入「第一次尝试时的行为」+ 可阻塞 + 可模拟持续拿不到。</summary>
    private sealed class ScriptedLease : IFullTextSupplyLease
    {
        private readonly ConcurrencyProbe? _probe;
        private readonly List<string> _scopeKeys = new();
        private int _tryCalls;
        private int _acquired;
        private int _releases;
        private int _renewCalls;
        private int _describeCalls;
        private int _concurrentHolders;
        private bool _held;

        internal ScriptedLease(ConcurrencyProbe? probe = null) => _probe = probe;

        /// <summary>非 null ⇒ 第 2 步起把取得 / 释放委托给它（用于「真实文件租约 + 计数」组合）。</summary>
        internal IFullTextSupplyLease? Inner { get; init; }

        /// <summary>前 N 次尝试直接返回「未取得」（模拟其它进程持租约）。</summary>
        internal int AcquireFailuresRemaining { get; init; }

        /// <summary>true ⇒ 永远返回「未取得」（L2 的 Busy 路径）。</summary>
        internal bool FailAll { get; set; }

        /// <summary>第一次尝试开始时的一次性回调（用来确定性地「在等待期间改文件」或取消令牌）。</summary>
        internal Action? OnFirstTryAcquire { get; set; }

        /// <summary>非 null ⇒ 第一次尝试在此 TCS 上挂起（制造真实临界区争用）。</summary>
        internal TaskCompletionSource? BlockFirstTry { get; init; }

        internal int TryCalls => Volatile.Read(ref _tryCalls);

        internal int AcquiredCount => Volatile.Read(ref _acquired);

        internal int ReleaseCalls => Volatile.Read(ref _releases);

        internal int RenewCalls => Volatile.Read(ref _renewCalls);

        internal int DescribeCalls => Volatile.Read(ref _describeCalls);

        internal int ConcurrentHolders => Volatile.Read(ref _concurrentHolders);

        internal string? LastJobId { get; private set; }

        internal string? LastOwnerId { get; private set; }

        internal IReadOnlyList<string> TryScopeKeys
        {
            get { lock (_scopeKeys) { return _scopeKeys.ToArray(); } }
        }

        internal void ReleaseFirstTry() =>
            (BlockFirstTry ?? throw new InvalidOperationException("没有挂起的第一次尝试")).TrySetResult();

        public async Task<SupplyLeaseAcquireResult> TryAcquireAsync(
            string scopeKey,
            SupplyLeaseOwner owner,
            string? jobId = null,
            CancellationToken ct = default)
        {
            var attempt = Interlocked.Increment(ref _tryCalls);
            lock (_scopeKeys)
            {
                _scopeKeys.Add(scopeKey);
            }

            if (attempt == 1)
            {
                OnFirstTryAcquire?.Invoke();
                if (BlockFirstTry is { } block)
                    await block.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            if (FailAll || attempt <= AcquireFailuresRemaining)
            {
                return new SupplyLeaseAcquireResult(
                    false,
                    null,
                    new SupplyLeaseHolder(
                        "other-process#999999",
                        999999,
                        "OTHER-MACHINE",
                        DateTimeOffset.UtcNow.AddMinutes(-1),
                        DateTimeOffset.UtcNow,
                        "other-job",
                        false,
                        null),
                    "替身：scope 被其它进程持有（未取得）。");
            }

            Interlocked.Increment(ref _acquired);
            Interlocked.Increment(ref _concurrentHolders);
            _probe?.Enter();
            _held = true;
            LastJobId = jobId;
            LastOwnerId = owner.OwnerId;

            if (Inner is { } inner)
            {
                var delegated = await inner.TryAcquireAsync(scopeKey, owner, jobId, ct).ConfigureAwait(false);
                if (!delegated.Acquired)
                    return delegated;
            }

            return new SupplyLeaseAcquireResult(
                true,
                new SupplyLease(
                    scopeKey,
                    owner,
                    jobId ?? string.Empty,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    null,
                    null),
                null,
                "替身：已取得 scope 租约。");
        }

        public async Task<bool> ReleaseAsync(string scopeKey, string ownerId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _releases);
            if (_held)
            {
                _held = false;
                Interlocked.Decrement(ref _concurrentHolders);
                _probe?.Exit();
            }

            return Inner is { } inner
                ? await inner.ReleaseAsync(scopeKey, ownerId, ct).ConfigureAwait(false)
                : true;
        }

        public Task<bool> RenewAsync(string scopeKey, string ownerId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _renewCalls);
            return Inner is { } inner ? inner.RenewAsync(scopeKey, ownerId, ct) : Task.FromResult(true);
        }

        public Task<SupplyLeaseHolder?> DescribeHolderAsync(string scopeKey, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _describeCalls);
            return Inner is { } inner ? inner.DescribeHolderAsync(scopeKey, ct) : Task.FromResult<SupplyLeaseHolder?>(null);
        }
    }

    /// <summary>失效接缝替身：计数 + 可选转发给真实实现（用来把「调用次数」与「真实行为」分开考）。</summary>
    private sealed class CountingInvalidation : IScopeReaderInvalidation
    {
        private readonly List<string> _scopeRoots = new();
        private SearchEngineScopeReaderInvalidation? _inner;
        private int _count;
        private int _delegated;

        /// <summary>把替身接到真实引擎上（此后既计数又真的失效缓存）。</summary>
        internal void AttachToRealEngine(LuceneSearchEngine engine) =>
            _inner = new SearchEngineScopeReaderInvalidation(engine);

        internal int Count => Volatile.Read(ref _count);

        internal int DelegatedCount => Volatile.Read(ref _delegated);

        internal IReadOnlyList<string> ScopeRoots
        {
            get { lock (_scopeRoots) { return _scopeRoots.ToArray(); } }
        }

        public void InvalidateScope(string corpusRootPath)
        {
            Interlocked.Increment(ref _count);
            lock (_scopeRoots)
            {
                _scopeRoots.Add(corpusRootPath);
            }

            if (_inner is not null)
            {
                Interlocked.Increment(ref _delegated);
                _inner.InvalidateScope(corpusRootPath);
            }
        }
    }

    /// <summary>并发观测：活跃持有者峰值（两个 scope 的写者会话是否重叠）。</summary>
    private sealed class ConcurrencyProbe
    {
        private int _active;
        private int _max;

        internal int ActiveHolders => Volatile.Read(ref _active);

        internal int MaxActiveHolders => Volatile.Read(ref _max);

        internal void Enter()
        {
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var current = Volatile.Read(ref _max);
                if (active <= current || Interlocked.CompareExchange(ref _max, active, current) == current)
                    return;
            }
        }

        internal void Exit() => Interlocked.Decrement(ref _active);
    }

    /// <summary>写阶段探针的记录（L7 的「不见半批」证据）。</summary>
    private sealed class BatchPhaseProbe
    {
        internal int HitsNew { get; set; }

        internal int HitsOld { get; set; }

        internal int HitsDeleted { get; set; }

        internal int HitsAdded { get; set; }

        internal List<string> CommittedRows { get; set; } = new();
    }

    /// <summary>
    /// 可挂钩的分析链：与 <c>JiebaAnalyzer</c> 同一分析链（Jieba 分词 + 小写），额外提供一次性钩子。
    /// <para>
    /// 钩子挂在 <see cref="GetTokenStream(string, System.IO.TextReader)"/>（文档进入 writer 时每个字段都会被调用）⇒
    /// 它落在**写阶段内部**（<c>DeleteDocuments</c> 已执行、<c>Commit</c> 尚未发生），这是能把「半批」窗口变成确定性输入的注入点。
    /// </para>
    /// <para>
    /// ⚠️ **不能**把钩子挂在 <c>CreateComponents</c> 上：Lucene 的默认复用策略（<c>GLOBAL_REUSE_STRATEGY</c>）
    /// 会按（线程, 字段）缓存 <c>TokenStreamComponents</c>，同一线程上第二次索引同一字段时**根本不会再调用**
    /// <c>CreateComponents</c> —— 那会让钩子时灵时不灵（实测：初次写成 <c>CreateComponents</c> 时 L1b 的钩子没触发）。
    /// </para>
    /// </summary>
    private sealed class HookAnalyzer : Analyzer
    {
        private Action? _hook;

        /// <summary>用**不复用** components 的策略构造：见 <see cref="NoReuseStrategy"/>。</summary>
        internal HookAnalyzer()
            : base(new NoReuseStrategy())
        {
        }

        internal bool Fired { get; private set; }

        internal void Arm(Action hook)
        {
            ArgumentNullException.ThrowIfNull(hook);
            if (Interlocked.CompareExchange(ref _hook, hook, null) is not null)
                throw new InvalidOperationException("钩子是一次性的：本实例已经装填过。");
        }

        protected override TokenStreamComponents CreateComponents(string fieldName, System.IO.TextReader reader)
        {
            var hook = Interlocked.Exchange(ref _hook, null);
            if (hook is not null)
            {
                Fired = true;
                hook();
            }

            var tokenizer = new JiebaTokenizer(reader, JiebaSegmenterPool.Instance);
            return new TokenStreamComponents(tokenizer, new LowerCaseFilter(LuceneVersion.LUCENE_48, tokenizer));
        }

        /// <summary>
        /// 不复用任何 <c>TokenStreamComponents</c> ⇒ 每次取 token stream 都会**重新**走
        /// <see cref="CreateComponents"/>，钩子因此必然触发。
        /// <para>
        /// 为什么需要它：Lucene 默认的 <c>GLOBAL_REUSE_STRATEGY</c> 按（线程, 字段）缓存 components，
        /// 同一线程上第二次索引同一字段时不会再调 <c>CreateComponents</c> ⇒ 钩子会时灵时不灵
        /// （实测：挂在 <c>CreateComponents</c> 上时 L1b 的钩子没触发）。本策略下测试代价可忽略。
        /// </para>
        /// </summary>
        private sealed class NoReuseStrategy : ReuseStrategy
        {
            public override TokenStreamComponents GetReusableComponents(Analyzer analyzer, string fieldName) => null!;

            public override void SetReusableComponents(
                Analyzer analyzer,
                string fieldName,
                TokenStreamComponents components)
            {
                // 刻意不缓存（本类的唯一目的就是让 CreateComponents 每次都被调用）
            }
        }
    }

    // ── 帮助 ──────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        internal Harness(
            FullTextIndexOptions options,
            LuceneSearchEngine search,
            LuceneFullTextIndexMaintenanceEngine maintenance,
            string corpusRoot)
        {
            Options = options;
            Search = search;
            Maintenance = maintenance;
            CorpusRoot = corpusRoot;
        }

        internal FullTextIndexOptions Options { get; }

        internal LuceneSearchEngine Search { get; }

        internal LuceneFullTextIndexMaintenanceEngine Maintenance { get; }

        internal string CorpusRoot { get; }

        internal string IndexDirectory => FullTextIndexPaths.ResolveIndexDirectory(Options.IndexRootDirectory, CorpusRoot);

        internal FullTextChangeSet ChangeSet(
            IReadOnlyList<FullTextFileChange> changes,
            string? scopeKey = null)
            => new(
                "01K-S3C-BATCH",
                CorpusRoot,
                scopeKey ?? FullTextChangeCoalescer.NormalizeComparisonKey(CorpusRoot),
                changes,
                ScanStartedUtc: DateTimeOffset.UtcNow,
                PreviousWatermarkUtc: null,
                RequiresCheckpointAdvance: true);

        /// <summary>充足预算：MaxIndexBytes 取默认 1 GiB，LiveIndexBytes 取**集合口径**的真实实测值。</summary>
        internal FullTextMutationBudget GenerousBudget()
            => new(
                MaxIndexBytes: DefaultMaxIndexBytes,
                LiveIndexBytes: SupplyIndexDirectoryLayout.MeasureLiveIndexBytes(Options.IndexRootDirectory),
                MaxPaths: DefaultMaxPaths);

        public void Dispose() => Search.Dispose();
    }

    private Harness NewHarness(
        string corpusRoot,
        IFullTextSupplyLease lease,
        CountingInvalidation invalidation,
        TimeSpan? leaseWaitUpperBound = null,
        Analyzer? analyzer = null)
    {
        var options = new FullTextIndexOptions { IndexRootDirectory = _indexRoot };
        var search = analyzer is null
            ? new LuceneSearchEngine(options)
            : new LuceneSearchEngine(options, analyzer, new List<IFileContentExtractor> { new PlainTextExtractor(options) });

        var engine = new LuceneFullTextIndexMaintenanceEngine(
            search,
            options,
            lease,
            leaseWaitUpperBound ?? MaintenanceOptions.DefaultLeaseWaitUpperBound,
            invalidation);

        return new Harness(options, search, engine, corpusRoot);
    }

    /// <summary>真实文件租约（`.supply-leases/&lt;sha256(scopeKey)&gt;.json`，测试索引根在 %TEMP% 下）。</summary>
    private IFullTextSupplyLease RealFileLease() =>
        new FileSupplyLease(new FullTextIndexOptions { IndexRootDirectory = _indexRoot });

    private string WriteCorpusFile(string corpusRoot, string relativePath, string content)
    {
        var fullPath = Path.Combine(corpusRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    private static FullTextFileChange Upsert(string fullPath)
        => new(fullPath, FullTextChangeKind.Upsert, LastWriteUtc: null, Length: null, Sources: FullTextChangeSource.Watcher);

    private async Task<int> CountHitsAsync(Harness harness, string query)
    {
        var result = await harness.Search.SearchAsync(query, harness.CorpusRoot, maxResults: 50);
        Assert.IsTrue(result.Success, $"搜索 '{query}' 必须成功：{result.Error}");
        return result.Matches.Count;
    }

    private async Task<List<IndexedPathEntry>> InventoryAsync(Harness harness)
    {
        var entries = new List<IndexedPathEntry>();
        await foreach (var entry in harness.Maintenance.EnumerateIndexedPathsAsync(harness.CorpusRoot))
            entries.Add(entry);

        return entries;
    }

    private long MeasureLiveIndexBytes() =>
        SupplyIndexDirectoryLayout.MeasureLiveIndexBytes(_indexRoot);

    private static long MeasureDirectoryBytes(string directory)
    {
        Assert.IsTrue(Directory.Exists(directory), $"测试侧度量要求目录存在：{directory}");
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    private static string[] ListFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

    /// <summary>独立仪器：用**新开**的 reader 读当前 commit 的存活文档（不复用引擎缓存）。</summary>
    private static List<string> ReadLiveRows(string indexDirectory)
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
                rows.Add($"{Path.GetFileName(document.Get("path"))}::{document.Get("line_text")}");
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

    /// <summary>确定性高熵内容（与 S3b 同一手法：每行 6 个唯一 token ⇒ 索引输出必然大于源文件）。</summary>
    private static string BigContent(int lineCount, string marker)
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
}
