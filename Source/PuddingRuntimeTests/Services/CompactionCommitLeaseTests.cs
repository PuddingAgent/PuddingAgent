using Microsoft.Extensions.Logging.Abstractions;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingCode.Runtime;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 压缩提交单飞（ADR-095 D4）：warm-prefix 此前是唯一绕开 <see cref="CompactionCoordinator"/>
/// 的压缩入口，而 DB 水合与自动/手动压缩都走这个锁。本套断言该锁的语义与
/// <see cref="ContextWindowManager.AcquireCompactionCommitLeaseAsync"/> 的接线。
/// </summary>
[TestClass]
public sealed class CompactionCommitLeaseTests
{
    private const string SessionId = "session-compaction-lease";

    /// <summary>同一会话的第二个提交必须等第一个释放（单飞），且等待期间 IsRunning 为真。</summary>
    [TestMethod]
    public async Task Lease_SerializesConcurrentCommits_OnSameSession()
    {
        var manager = CreateManager(out var coordinator);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Task.Run(async () =>
        {
            await using var lease = await manager.AcquireCompactionCommitLeaseAsync(SessionId, CancellationToken.None);
            firstEntered.SetResult();
            await releaseFirst.Task;
        });

        await firstEntered.Task;
        Assert.IsTrue(coordinator.IsRunning(SessionId), "持有租约期间必须报告「正在压缩」");

        var second = Task.Run(async () =>
        {
            await using var lease = await manager.AcquireCompactionCommitLeaseAsync(SessionId, CancellationToken.None);
            secondEntered.SetResult();
        });

        // 第一个还没释放 ⇒ 第二个不得进入临界区。
        var earlyEntry = await Task.WhenAny(secondEntered.Task, Task.Delay(200));
        Assert.AreNotSame(secondEntered.Task, earlyEntry, "第二个提交必须在单飞锁外等待");

        releaseFirst.SetResult();
        await first;
        await second;
        Assert.IsTrue(secondEntered.Task.IsCompletedSuccessfully, "第一个释放后第二个必须进入");
        Assert.IsFalse(coordinator.IsRunning(SessionId), "全部释放后必须报告「未在压缩」");
    }

    /// <summary>不同会话互不阻塞（per-session 锁，不是全局锁）。</summary>
    [TestMethod]
    public async Task Lease_DoesNotBlockOtherSessions()
    {
        var manager = CreateManager(out var coordinator);

        await using var held = await manager.AcquireCompactionCommitLeaseAsync(SessionId, CancellationToken.None);

        var otherEntered = Task.Run(async () =>
        {
            await using var lease = await manager.AcquireCompactionCommitLeaseAsync("session-other", CancellationToken.None);
        });

        var completed = await Task.WhenAny(otherEntered, Task.Delay(2_000));
        Assert.AreSame(otherEntered, completed, "另一会话不得被本会话的提交锁阻塞");
        await otherEntered;
        Assert.IsTrue(coordinator.IsRunning(SessionId));
    }

    /// <summary>
    /// 取消语义：等待中的提交被取消时**不得**持有锁（异常直接向上传播），
    /// 否则后续压缩会被永久饿死。
    /// </summary>
    [TestMethod]
    public async Task Lease_CancelledWaiter_DoesNotHoldTheLock()
    {
        var manager = CreateManager(out var coordinator);

        await using var held = await manager.AcquireCompactionCommitLeaseAsync(SessionId, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var waiter = manager.AcquireCompactionCommitLeaseAsync(SessionId, cts.Token);
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await waiter);
        Assert.IsTrue(coordinator.IsRunning(SessionId), "被取消的等待者不得释放别人持有的锁");

        // 释放后必须能再次正常获取（没有被取消路径留下的坏状态）。
        await held.DisposeAsync();
        await using var again = await manager.AcquireCompactionCommitLeaseAsync(SessionId, CancellationToken.None);
        Assert.IsTrue(coordinator.IsRunning(SessionId));
    }

    /// <summary>提交完成登记：RecordCompactionCommitCompleted 会清掉该会话的无收益抑制。</summary>
    [TestMethod]
    public async Task RecordCompactionCommitCompleted_ClearsNoGainSuppression()
    {
        var manager = CreateManager(out var coordinator);
        coordinator.RecordNoGain(SessionId, "fingerprint-1", generation: 3);
        Assert.IsTrue(coordinator.TryGetNoGainSkipReason(SessionId, "fingerprint-1", out _));

        await using (await manager.AcquireCompactionCommitLeaseAsync(SessionId, CancellationToken.None))
        {
            manager.RecordCompactionCommitCompleted(SessionId);
        }

        Assert.IsFalse(
            coordinator.TryGetNoGainSkipReason(SessionId, "fingerprint-1", out _),
            "真正改写过历史后必须清除同候选的无收益抑制");
    }

    private static ContextWindowManager CreateManager(out CompactionCoordinator coordinator)
    {
        coordinator = new CompactionCoordinator();
        return new ContextWindowManager(
            new AgentSessionManager(NullLogger<AgentSessionManager>.Instance),
            new InMemoryRuntimeSessionStore(),
            new ExecutionControlRegistry(),
            new ExecutionJournal(),
            NullLogger<ContextWindowManager>.Instance,
            compactionCoordinator: coordinator);
    }
}
