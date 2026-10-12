using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// **原子执行账本**（方案 §4.2）的不变量套件。语义：同一连接世代的同一 operationId **只执行一次**；
/// 同指纹在途 ⇒ 等同一完成源；同指纹已完成 ⇒ 复用终态；**指纹不同 ⇒ 冲突且不透露结果**。
/// 另钉两条边界：**结果未知不复用也不当安全重试**；**在途记录不被容量/保留期驱逐**。
/// </summary>
public sealed class BrowserOperationLedgerTests
{
    private static readonly BrowserOperationLedgerKey Key =
        new("proc-test", ConnectionGeneration: 7, "op-1");

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    [Fact]
    public void FirstBegin_WinsAndExecutes()
    {
        var ledger = new BrowserOperationLedger();

        var decision = ledger.TryBegin(Key, "fp-a");

        Assert.Equal(BrowserOperationLedgerState.Execute, decision.State);
    }

    [Fact]
    public void SameFingerprintWhilePending_AwaitsTheSameCompletionSource()
    {
        var ledger = new BrowserOperationLedger();
        ledger.TryBegin(Key, "fp-a");

        var again = ledger.TryBegin(Key, "fp-a");

        Assert.Equal(BrowserOperationLedgerState.AwaitPending, again.State);
        Assert.NotNull(again.Pending);
        Assert.False(again.Pending!.IsCompleted);
    }

    [Fact]
    public void SameFingerprintAfterCompletion_ReplaysTheSameTerminal()
    {
        var ledger = new BrowserOperationLedger();
        ledger.TryBegin(Key, "fp-a");
        ledger.Complete(Key, "ok:done");

        var again = ledger.TryBegin(Key, "fp-a");

        Assert.Equal(BrowserOperationLedgerState.ReplayCompleted, again.State);
        Assert.Equal("ok:done", again.Completed);
    }

    [Fact]
    public void DifferentFingerprint_ConflictsAndDoesNotLeakTheOriginalResult()
    {
        var ledger = new BrowserOperationLedger();
        ledger.TryBegin(Key, "fp-a");
        ledger.Complete(Key, "ok:secret");

        var conflict = ledger.TryBegin(Key, "fp-b");

        Assert.Equal(BrowserOperationLedgerState.Conflict, conflict.State);
        Assert.Null(conflict.Completed);
        Assert.Null(conflict.Pending);
    }

    [Fact]
    public void DifferentConnectionGeneration_IsANewNamespace()
    {
        var ledger = new BrowserOperationLedger();
        ledger.TryBegin(Key, "fp-a");
        ledger.Complete(Key, "ok:old");

        var other = new BrowserOperationLedgerKey("proc-test", ConnectionGeneration: 8, "op-1");
        var decision = ledger.TryBegin(other, "fp-a");

        Assert.Equal(BrowserOperationLedgerState.Execute, decision.State);
    }

    [Fact]
    public void UnknownOutcome_IsNeitherReplayedNorRetried()
    {
        var ledger = new BrowserOperationLedger();
        ledger.TryBegin(Key, "fp-a");
        ledger.Abandon(Key, "outcome_unknown: side effects may have landed");

        var again = ledger.TryBegin(Key, "fp-a");

        // 复用未知结果是谎报；当成安全重试会重复副作用 ⇒ 一律冲突（fail closed）。
        Assert.Equal(BrowserOperationLedgerState.Conflict, again.State);
        Assert.Null(again.Completed);
    }

    [Fact]
    public void CapacityTrim_NeverEvictsPendingEntries()
    {
        var ledger = new BrowserOperationLedger(capacity: 2);

        var pendingKey = new BrowserOperationLedgerKey("proc-test", 7, "op-pending");
        ledger.TryBegin(pendingKey, "fp-p"); // 在途，不得被驱逐

        for (var i = 0; i < 5; i++)
        {
            var key = new BrowserOperationLedgerKey("proc-test", 7, $"op-{i}");
            ledger.TryBegin(key, "fp-c");
            ledger.Complete(key, "ok");
        }

        // 在途记录仍在 ⇒ 重传仍得到"等在途"，绝不会变成第二次执行。
        Assert.Equal(BrowserOperationLedgerState.AwaitPending, ledger.TryBegin(pendingKey, "fp-p").State);
        Assert.True(ledger.Count <= 3, $"completed entries must be trimmed to the capacity; count={ledger.Count}");
    }

    [Fact]
    public void RetentionExpiry_LetsCompletedOperationsRunAgainButNotUnknownOnes()
    {
        var clock = new TestClock();
        var ledger = new BrowserOperationLedger(retention: TimeSpan.FromMinutes(1), clock: clock);

        var known = new BrowserOperationLedgerKey("proc-test", 7, "op-known");
        ledger.TryBegin(known, "fp-k");
        ledger.Complete(known, "ok");

        var unknown = new BrowserOperationLedgerKey("proc-test", 7, "op-unknown");
        ledger.TryBegin(unknown, "fp-u");
        ledger.Abandon(unknown, "outcome_unknown");

        clock.Advance(TimeSpan.FromMinutes(2));

        // 已知终态过窗口后无法再证明幂等 ⇒ 允许重新执行。
        Assert.Equal(BrowserOperationLedgerState.Execute, ledger.TryBegin(known, "fp-k").State);

        // 未知结果即便过窗口**也不得**当作安全重试：仍冲突。
        Assert.Equal(BrowserOperationLedgerState.Conflict, ledger.TryBegin(unknown, "fp-u").State);
    }
}
