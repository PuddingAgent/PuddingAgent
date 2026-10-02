using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// D2「持久维护账本」的纯逻辑门禁（2026-10-02 高磁盘读取修复）。
/// <para>
/// 锁定的是单调版本与水位语义：提交只确认被**捕获**的版本；执行期间到达的新变化把账本置为
/// 「还有工作」；扫描水位只在完整、无未解决路径、无待重试且捕获版本即当前期望时推进；
/// 失败按有界阶梯退避且不会把整个范围退回全量重建。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeSourceMaintenanceLedgerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private static CodeSourceMaintenanceLedger Ledger(
        long epoch = 0,
        TimeSpan? retryBase = null,
        TimeSpan? retryMax = null) =>
        new("ws", "scope", epoch, retryBase: retryBase, retryMax: retryMax);

    private static CodeSourceCommitCompletion Completion(
        long epoch,
        long capturedVersion,
        DateTimeOffset? scanStarted = null,
        bool scanComplete = true,
        int unresolvedPathCount = 0,
        params CodeSourceProviderAdvance[] advances) =>
        new(epoch, capturedVersion, advances, scanStarted, scanComplete, unresolvedPathCount);

    // ── 期望版本与提交确认 ───────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void RecordObservedChanges_BumpsTheDesiredVersionOncePerBatch()
    {
        var ledger = Ledger();

        Assert.AreEqual(1, ledger.RecordObservedChanges());
        Assert.AreEqual(2, ledger.RecordObservedChanges());
        Assert.AreEqual(2, ledger.DesiredVersion);
        Assert.IsTrue(ledger.IsCurrent(2));
        Assert.IsFalse(ledger.IsCurrent(1));
    }

    [TestMethod]
    public void CompleteCommit_ConfirmsTheCapturedVersionAndAdvancesConsumerWatermarks()
    {
        var ledger = Ledger();
        var captured = ledger.RecordObservedChanges();

        var outcome = ledger.CompleteCommit(Completion(
            ledger.Epoch,
            captured,
            scanStarted: T0.AddMinutes(10),
            advances: [new CodeSourceProviderAdvance("csharp", 7), new CodeSourceProviderAdvance("fulltext", 3)]));

        Assert.AreEqual(CodeSourceCommitOutcome.Committed, outcome);
        Assert.AreEqual(captured, ledger.CommittedVersion);
        Assert.IsFalse(ledger.Snapshot().DirtyAgain);

        var snapshot = ledger.Snapshot();
        Assert.AreEqual(7, snapshot.ConsumerAppliedVersions["csharp"]);
        Assert.AreEqual(3, snapshot.ConsumerAppliedVersions["fulltext"]);
        Assert.AreEqual(T0.AddMinutes(10), snapshot.ScanWatermarkUtc);
    }

    [TestMethod]
    public void ChangesArrivingDuringACommit_DoNotGetConfirmedByIt()
    {
        var ledger = Ledger();
        var captured = ledger.RecordObservedChanges();

        // 执行期间又出现了新变化。
        var newer = ledger.RecordObservedChanges();

        var outcome = ledger.CompleteCommit(Completion(
            ledger.Epoch,
            captured,
            scanStarted: T0.AddMinutes(10),
            advances: [new CodeSourceProviderAdvance("csharp", 1)]));

        Assert.AreEqual(CodeSourceCommitOutcome.Superseded, outcome);
        Assert.AreEqual(captured, ledger.CommittedVersion, "只确认被捕获的版本");
        Assert.IsTrue(ledger.Snapshot().DirtyAgain, "较新变化必须继续补跑");
        Assert.AreEqual(1, ledger.Snapshot().ConsumerAppliedVersions["csharp"]);
        Assert.AreEqual(newer, ledger.DesiredVersion);
    }

    [TestMethod]
    public void ConsumerWatermarksOnlyMoveForward()
    {
        var ledger = Ledger();
        var first = ledger.RecordObservedChanges();
        ledger.CompleteCommit(Completion(
            ledger.Epoch, first, T0.AddMinutes(1), advances: [new CodeSourceProviderAdvance("csharp", 5)]));

        var second = ledger.RecordObservedChanges();
        ledger.CompleteCommit(Completion(
            ledger.Epoch, second, T0.AddMinutes(2), advances: [new CodeSourceProviderAdvance("csharp", 3)]));

        Assert.AreEqual(5, ledger.Snapshot().ConsumerAppliedVersions["csharp"], "水位不回退");
    }

    // ── 扫描水位：不掩盖失败 ────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ScanWatermark_RequiresAFullScanWithNoUnresolvedWork()
    {
        var incomplete = Ledger();
        var capturedIncomplete = incomplete.RecordObservedChanges();
        Assert.AreEqual(
            CodeSourceCommitOutcome.Superseded,
            incomplete.CompleteCommit(Completion(
                incomplete.Epoch, capturedIncomplete, T0.AddMinutes(10), scanComplete: false)));
        Assert.IsNull(incomplete.Snapshot().ScanWatermarkUtc, "不完整扫描不推进水位");

        var unresolved = Ledger();
        var capturedUnresolved = unresolved.RecordObservedChanges();
        Assert.AreEqual(
            CodeSourceCommitOutcome.Superseded,
            unresolved.CompleteCommit(Completion(
                unresolved.Epoch, capturedUnresolved, T0.AddMinutes(10), unresolvedPathCount: 1)));
        Assert.IsNull(unresolved.Snapshot().ScanWatermarkUtc, "有未解决路径就不推进水位");

        var clean = Ledger();
        var capturedClean = clean.RecordObservedChanges();
        Assert.AreEqual(
            CodeSourceCommitOutcome.Committed,
            clean.CompleteCommit(Completion(
                clean.Epoch, capturedClean, T0.AddMinutes(10))));
        Assert.AreEqual(T0.AddMinutes(10), clean.Snapshot().ScanWatermarkUtc);
    }

    [TestMethod]
    public void ScanWatermark_NeverMovesBackwards()
    {
        var ledger = Ledger();
        var first = ledger.RecordObservedChanges();
        ledger.CompleteCommit(Completion(ledger.Epoch, first, T0.AddMinutes(10)));

        var second = ledger.RecordObservedChanges();
        ledger.CompleteCommit(Completion(ledger.Epoch, second, T0.AddMinutes(5)));

        Assert.AreEqual(T0.AddMinutes(10), ledger.Snapshot().ScanWatermarkUtc);
    }

    [TestMethod]
    public void WatcherOnlyCommit_DoesNotAdvanceTheScanWatermark()
    {
        var ledger = Ledger();
        var captured = ledger.RecordObservedChanges();

        var outcome = ledger.CompleteCommit(Completion(
            ledger.Epoch, captured, scanStarted: null, scanComplete: true));

        Assert.AreEqual(CodeSourceCommitOutcome.Superseded, outcome);
        Assert.IsNull(ledger.Snapshot().ScanWatermarkUtc, "watcher 批次没有枚举磁盘，不推进扫描水位");
    }

    // ── 世代 ────────────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void StaleEpochCompletion_IsIgnoredEntirely()
    {
        var ledger = Ledger();
        var captured = ledger.RecordObservedChanges();
        var staleEpoch = ledger.Epoch;

        var newEpoch = ledger.BeginEpoch();
        Assert.AreEqual(staleEpoch + 1, newEpoch);

        var outcome = ledger.CompleteCommit(Completion(
            staleEpoch,
            captured,
            T0.AddMinutes(10),
            advances: [new CodeSourceProviderAdvance("csharp", 9)]));

        Assert.AreEqual(CodeSourceCommitOutcome.StaleEpoch, outcome);
        var snapshot = ledger.Snapshot();
        Assert.AreEqual(0, snapshot.CommittedVersion, "过期世代的结果不得记入");
        Assert.IsEmpty(snapshot.ConsumerAppliedVersions);
        Assert.IsNull(snapshot.ScanWatermarkUtc);
        Assert.IsTrue(snapshot.DirtyAgain, "换世代本身就意味着要重新核对");
        Assert.IsFalse(ledger.IsCurrentEpoch(staleEpoch));
        Assert.IsTrue(ledger.IsCurrentEpoch(newEpoch));
    }

    // ── 待重试路径与退避阶梯 ────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void RecordFailure_UsesABoundedExponentialLadder()
    {
        var ledger = Ledger(retryBase: TimeSpan.FromSeconds(1), retryMax: TimeSpan.FromSeconds(4));

        var first = ledger.RecordFailure(@"C:\repo\a.cs", "extract_failed", T0);
        var second = ledger.RecordFailure(@"C:\repo\a.cs", "extract_failed", T0);
        var third = ledger.RecordFailure(@"C:\repo\a.cs", "extract_failed", T0);
        var fourth = ledger.RecordFailure(@"C:\repo\a.cs", "extract_failed", T0);

        Assert.AreEqual(1, first.Attempts);
        Assert.AreEqual(T0.AddSeconds(1), first.NextAttemptAtUtc);
        Assert.AreEqual(T0.AddSeconds(2), second.NextAttemptAtUtc);
        Assert.AreEqual(T0.AddSeconds(4), third.NextAttemptAtUtc);
        Assert.AreEqual(T0.AddSeconds(4), fourth.NextAttemptAtUtc, "封顶后不再增长");
        Assert.AreEqual(T0, fourth.FirstFailedAtUtc, "首次失败时刻保持不变");
    }

    [TestMethod]
    public void DueRetries_ReturnsOnlyMaturedPathsInStableOrder()
    {
        var ledger = Ledger(retryBase: TimeSpan.FromMinutes(1), retryMax: TimeSpan.FromMinutes(10));
        ledger.RecordFailure(@"C:\repo\b.cs", "failed", T0);
        ledger.RecordFailure(@"C:\repo\a.cs", "failed", T0);

        Assert.IsEmpty(ledger.DueRetries(T0), "刚失败时都还没到期");
        Assert.AreEqual(2, ledger.DueRetries(T0.AddMinutes(1)).Count);

        var due = ledger.DueRetries(T0.AddMinutes(1));
        CollectionAssert.AreEqual(new[] { @"C:\repo\a.cs", @"C:\repo\b.cs" }, due.Select(r => r.FilePath).ToArray());
    }

    [TestMethod]
    public void ClearRetry_RemovesAPathThatSucceeded()
    {
        var ledger = Ledger(retryBase: TimeSpan.FromMinutes(1));
        ledger.RecordFailure(@"C:\repo\a.cs", "failed", T0);
        Assert.AreEqual(1, ledger.PendingRetryCount);

        Assert.IsTrue(ledger.ClearRetry(@"C:\repo\a.cs"));
        Assert.AreEqual(0, ledger.PendingRetryCount);
        Assert.IsFalse(ledger.ClearRetry(@"C:\repo\a.cs"), "重复清除是安全的 no-op");
        Assert.IsEmpty(ledger.DueRetries(T0.AddMinutes(5)));
    }

    [TestMethod]
    public void PendingRetries_HoldTheScanWatermarkUntilTheyAreCleared()
    {
        var ledger = Ledger(retryBase: TimeSpan.FromMinutes(1));
        var captured = ledger.RecordObservedChanges();
        ledger.RecordFailure(@"C:\repo\a.cs", "extract_failed", T0);

        Assert.AreEqual(
            CodeSourceCommitOutcome.Superseded,
            ledger.CompleteCommit(Completion(ledger.Epoch, captured, T0.AddMinutes(10))));
        Assert.IsNull(ledger.Snapshot().ScanWatermarkUtc, "有待重试路径时不推进水位");

        ledger.ClearRetry(@"C:\repo\a.cs");

        var next = ledger.RecordObservedChanges();
        Assert.AreEqual(
            CodeSourceCommitOutcome.Committed,
            ledger.CompleteCommit(Completion(ledger.Epoch, next, T0.AddMinutes(20))));
        Assert.AreEqual(T0.AddMinutes(20), ledger.Snapshot().ScanWatermarkUtc);
    }

    // ── 快照与边界 ──────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Snapshot_IsAnIndependentCopy()
    {
        var ledger = Ledger(retryBase: TimeSpan.FromMinutes(1));
        ledger.RecordObservedChanges();
        ledger.RecordFailure(@"C:\repo\a.cs", "failed", T0);

        var snapshot = ledger.Snapshot();
        ledger.RecordObservedChanges();
        ledger.ClearRetry(@"C:\repo\a.cs");

        Assert.AreEqual(1, snapshot.DesiredVersion);
        Assert.AreEqual(1, snapshot.PendingRetries.Count);
    }

    [TestMethod]
    public void BackoffInterval_IsTotalAndBounded()
    {
        var ladder = Enumerable.Range(1, 40)
            .Select(attempt => CodeSourceMaintenanceLedger.BackoffInterval(
                attempt, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(30)))
            .ToArray();

        Assert.AreEqual(TimeSpan.FromSeconds(60), ladder[0]);
        Assert.AreEqual(TimeSpan.FromSeconds(120), ladder[1]);
        Assert.IsTrue(ladder.All(interval => interval <= TimeSpan.FromMinutes(30)));
        Assert.AreEqual(TimeSpan.FromMinutes(30), ladder[^1]);
        Assert.AreEqual(
            TimeSpan.FromSeconds(60),
            CodeSourceMaintenanceLedger.BackoffInterval(0, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(30)),
            "非正的尝试次数按第一次处理");
    }

    [TestMethod]
    public void Ledger_RejectsInvalidConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Ledger(retryBase: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ledger(retryBase: TimeSpan.FromMinutes(2), retryMax: TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentException>(() => new CodeSourceMaintenanceLedger("ws", "  "));
    }
}
