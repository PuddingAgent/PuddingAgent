using System.Diagnostics;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;

namespace PuddingSshTests;

/// <summary>
/// L3 回归（ADR-096 D3 补充约束）：**同步入口隔离、停止回调有界、单一清理预算、终止事实不被覆盖、关闭证据**。
/// <para>
/// 对应独立复核 F6（P1）：exec/Dispose 的同步入口不被有界调度保护（deadline 120 ms、宽限 50 ms，
/// 实测 820 ms 返回且仍报 Completed；Dispose 同步阻塞时 `ssh_test` 实测 806 ms）。
/// </para>
/// </summary>
[TestClass]
public sealed class SshSyncEntryBoundaryTests
{
    private static async Task<T> WaitOrFailAsync<T>(Task<T> operation, int timeoutMs = 6000)
    {
        var winner = await Task.WhenAny(operation, Task.Delay(timeoutMs));
        Assert.AreSame(operation, winner, $"业务调用在 {timeoutMs} ms 内未返回");
        return await operation;
    }

    // ---------- F6：同步入口（exec / Dispose）必须在有界 worker 上 ----------

    [TestMethod]
    public async Task SynchronouslyBlockingExec_IsBounded_AndNeverReportsCompleted()
    {
        // 3 秒同步阻塞：足以在取消时仍处于阻塞中（用例前提必须成立）。
        var session = new FakeSession { SynchronousBlockBeforeExecute = TimeSpan.FromSeconds(3) };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };

        await using var coordinator = new SshOperationCoordinator(
            factory,
            new FakeIdentityProvider(),
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(50) });

        using var cts = new CancellationTokenSource();
        var operation = coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), cts.Token);

        // 采样：同步阻塞期间它必须占着有界 worker（记录峰值，不只看结束后为零）。
        var maxWorkers = 0;
        var execInvocations = 0;
        var sampling = Stopwatch.StartNew();
        while (sampling.ElapsedMilliseconds < 300)
        {
            maxWorkers = Math.Max(maxWorkers, coordinator.ActiveStageWorkers);
            execInvocations = Math.Max(execInvocations, session.ExecuteCount);
            await Task.Delay(10);
        }

        Assert.AreEqual(1, execInvocations, "用例前提：exec 已被调用");
        Assert.IsGreaterThanOrEqualTo(
            1,
            maxWorkers,
            $"同步阻塞的 exec 必须占着有界 worker（peakWorkers={coordinator.PeakStageWorkers}）");

        var stopwatch = Stopwatch.StartNew();
        await cts.CancelAsync();
        var result = await WaitOrFailAsync(operation);
        stopwatch.Stop();

        Assert.IsLessThan(1000, (int)stopwatch.ElapsedMilliseconds, "同步阻塞的 exec 不得拖住业务等待");
        Assert.AreNotEqual(SshExecutionStatus.Completed, result.Status, "本端已取消：不得报 Completed");
        Assert.AreEqual(SshErrorCodes.Cancelled, result.ErrorCode);

        // exec 仍在同步阻塞：必须留在记账里，而不是消失（F6 的关键断言）。
        Assert.IsGreaterThan(
            0,
            coordinator.ActiveStageWorkers + coordinator.PendingResourceLeases + coordinator.QuarantinedResources,
            $"仍在同步阻塞的 exec 必须继续占账（workers={coordinator.ActiveStageWorkers} pending={coordinator.PendingResourceLeases} quarantine={coordinator.QuarantinedResources}）");
    }

    [TestMethod]
    public async Task SynchronouslyBlockingDispose_IsBounded_OnBothEntries()
    {
        foreach (var entry in new[] { "execute", "test" })
        {
            var session = new FakeSession { SynchronousBlockBeforeDispose = TimeSpan.FromMilliseconds(800) };
            var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };

            await using var coordinator = new SshOperationCoordinator(
                factory,
                new FakeIdentityProvider(),
                new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(60) });

            var stopwatch = Stopwatch.StartNew();
            if (entry == "execute")
            {
                await WaitOrFailAsync(coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None));
            }
            else
            {
                await WaitOrFailAsync(coordinator.TestAsync(FakeSsh.Host(), FakeSsh.Test(), CancellationToken.None));
            }

            stopwatch.Stop();
            Assert.IsLessThan(1000, (int)stopwatch.ElapsedMilliseconds, $"{entry}：同步阻塞的 Dispose 不得拖住业务等待");
            Assert.IsTrue(
                await FakeSsh.WaitUntilAsync(() => coordinator.ActiveCloseWorkers == 1, 3000),
                $"{entry}：同步阻塞的 Dispose 必须留在独立关闭池里被追踪");
        }
    }

    // ---------- Stop：进入即建立有界 deadline，阻塞的取消回调不拖住 Stop ----------

    [TestMethod]
    public async Task BlockingCancellationCallback_DoesNotBlockStopAsync()
    {
        var session = new FakeSession
        {
            OnExecute = async (_, _, _, ct) =>
            {
                // 可阻塞的取消回调：旧实现在 Stop 里 await CancelAsync 会被它拖住 2 秒。
                using var registration = ct.Register(() => Thread.Sleep(2000));
                await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
                return FakeResults.Completed();
            },
        };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };

        await using var coordinator = new SshOperationCoordinator(
            factory,
            new FakeIdentityProvider(),
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(200) });

        var operation = coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => session.ExecuteCount == 1));

        var stopwatch = Stopwatch.StartNew();
        var report = await WaitOrFailAsync(coordinator.StopAsync());
        stopwatch.Stop();

        Assert.IsLessThan(2000, (int)stopwatch.ElapsedMilliseconds, "Stop 不得被阻塞的取消回调拖住");
        Assert.IsTrue(report.CancelCallbacksPending, "取消回调未返回时必须如实报告");
        Assert.IsGreaterThan(
            0,
            report.PendingOperations + report.PendingResources + report.PendingCleanups,
            "仍有在飞操作/资源时必须如实报告");

        await WaitOrFailAsync(operation, 8000);
    }

    // ---------- 单一清理预算：不受业务 deadline 影响 ----------

    [TestMethod]
    public async Task CleanupBudget_IsIndependentOfOperationDeadline()
    {
        var session = new FakeSession { DisposeDelay = TimeSpan.FromMilliseconds(300) };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };

        await using var coordinator = new SshOperationCoordinator(
            factory,
            new FakeIdentityProvider(),
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(600) });

        var result = await WaitOrFailAsync(coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None));

        // 旧实现按「业务剩余」取清理额度（业务额度耗尽 ⇒ 0 ⇒ 立刻进隔离区，实际根本没试过关闭）。
        Assert.AreEqual(SshCleanupState.Completed, result.CleanupState, "清理预算必须独立于业务 deadline");
        Assert.IsTrue(session.Disposed);
        Assert.AreEqual(0, coordinator.QuarantinedResources);
    }

    // ---------- 迟到完成不得覆盖已确立的终止事实 ----------

    [TestMethod]
    public async Task LateExecSuccessWithinGrace_DoesNotOverwriteLocalTermination()
    {
        var execGate = new TaskCompletionSource<SshExecuteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession { OnExecute = (_, _, _, _) => execGate.Task };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };

        await using var coordinator = new SshOperationCoordinator(
            factory,
            new FakeIdentityProvider(),
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(500) });

        using var cts = new CancellationTokenSource();
        var operation = coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), cts.Token);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => session.ExecuteCount == 1));
        await cts.CancelAsync();

        // 宽限内迟到成功：状态必须保持本端取消，但远端退出证据要保留。
        execGate.SetResult(FakeResults.Completed() with { ExitCode = 0, ExecutionState = SshExecutionState.Exited });
        var result = await WaitOrFailAsync(operation);

        Assert.AreEqual(SshExecutionStatus.Cancelled, result.Status, "迟到 Completed 不得覆盖已确立的取消事实");
        Assert.AreEqual(SshErrorCodes.Cancelled, result.ErrorCode);
        Assert.AreEqual(SshExecutionState.Exited, result.ExecutionState, "远端退出证据必须保留");
        Assert.IsFalse(result.RetrySafe);
    }

    // ---------- 关闭证据：CleanupState=Completed 不能只靠「调用过 Abort」 ----------

    [TestMethod]
    public async Task CleanupState_RequiresCloseEvidence_AndFaultedDisposeIsNotCompleted()
    {
        // 1) 会话自报仍未关闭（诊断接口 Closed=false）：Dispose 完成也不算 Completed。
        var unclosed = new UnclosedSession();
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(unclosed) };
        await using (var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), new SshCoordinatorOptions()))
        {
            var result = await WaitOrFailAsync(coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None));
            Assert.AreNotEqual(SshCleanupState.Completed, result.CleanupState, "没有关闭证据不得报 Completed");
        }

        // 2) Dispose fault：同样不得报 Completed，且失败原因被记录。
        var faulted = new FaultingDisposeSession();
        var faultFactory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(faulted) };
        await using (var coordinator = new SshOperationCoordinator(
            faultFactory,
            new FakeIdentityProvider(),
            new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(80), MaxQuarantinedResources = 4 }))
        {
            var result = await WaitOrFailAsync(coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None));
            Assert.AreNotEqual(SshCleanupState.Completed, result.CleanupState, "faulted Dispose 不得报 Completed");
        }
    }

    [TestMethod]
    public async Task ConcurrentCloseCalls_ShareExactlyOneCloseTask()
    {
        for (var round = 0; round < 6; round++)
        {
            var connectGate = new TaskCompletionSource<ISshTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = new FakeSession { DisposeDelay = TimeSpan.FromMilliseconds(60) };
            var factory = new FakeTransportFactory { OnConnect = (_, _) => connectGate.Task };

            await using var coordinator = new SshOperationCoordinator(
                factory,
                new FakeIdentityProvider(),
                new SshCoordinatorOptions { CleanupGrace = TimeSpan.FromMilliseconds(200) });

            using var cts = new CancellationTokenSource();
            var operation = coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), cts.Token);
            Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => factory.ConnectCount == 1));

            // 取消与迟到交还同时发生：业务 finally 与迟到 owner 都会请求关闭。
            await cts.CancelAsync();
            connectGate.SetResult(late);

            await WaitOrFailAsync(operation);
            Assert.IsTrue(
                await FakeSsh.WaitUntilAsync(() => coordinator.PendingResourceLeases == 0, 8000),
                $"第 {round + 1} 轮：资源必须收敛");
            Assert.AreEqual(1, late.DisposeCallCount, $"第 {round + 1} 轮：关闭必须只执行一次（共享关闭 Task）");
        }
    }

    /// <summary>自报仍未关闭的会话：用于验证 CleanupState 需要关闭证据。</summary>
    private sealed class UnclosedSession : ISshTransportSession, ISshTransportSessionDiagnostics
    {
        private int _abort;

        public string VerifiedHostKeySha256 => "SHA256:unclosed";

        public string HostKeyAlgorithm => "ssh-ed25519";

        public bool IsConnected => true;

        public SshSubmissionEvidence SubmissionEvidence => SshSubmissionEvidence.SubmissionAttempted;

        public int PendingReaders => 0;

        public bool AbortRequested => Volatile.Read(ref _abort) == 1;

        public bool Closed => false;

        public void RequestAbort() => Volatile.Write(ref _abort, 1);

        public Task<SshExecuteResult> ExecuteAsync(string command, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken) =>
            Task.FromResult(FakeResults.Completed());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>释放会抛错的会话：验证「异常已观察 ≠ 关闭成功」。</summary>
    private sealed class FaultingDisposeSession : ISshTransportSession, ISshTransportSessionDiagnostics
    {
        public string VerifiedHostKeySha256 => "SHA256:faulting";

        public string HostKeyAlgorithm => "ssh-ed25519";

        public bool IsConnected => true;

        public SshSubmissionEvidence SubmissionEvidence => SshSubmissionEvidence.SubmissionAttempted;

        public int PendingReaders => 0;

        public bool AbortRequested => true;

        public bool Closed => false;

        public void RequestAbort()
        {
        }

        public Task<SshExecuteResult> ExecuteAsync(string command, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken) =>
            Task.FromResult(FakeResults.Completed());

        public ValueTask DisposeAsync() => throw new IOException("dispose fault");
    }
}
