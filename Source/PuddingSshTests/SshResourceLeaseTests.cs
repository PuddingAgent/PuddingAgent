using System.Diagnostics;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;
using Renci.SshNet.Common;

namespace PuddingSshTests;

/// <summary>
/// R3 回归（ADR-096 D3/D4）：**提交事实**与**有界资源回收**。
/// <list type="bullet">
///   <item>业务调用（Test/Execute/Stop）必须在预算内返回，不因阶段端口或清理不合作而无限等待。</item>
///   <item>预算内没结束的资源仍被追踪并占用计数（隔离区），满额即拒绝新增工作。</item>
///   <item>只有底层明确「未发送」时才可能保持未提交与可重试；否则一律远端状态未知、不可重试。</item>
///   <item>连续失败/取消/超时之后，租约、worker、隔离区计数必须回到基线。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class SshResourceLeaseTests
{
    private static SshCoordinatorOptions Options(
        int cleanupGraceMs = 150,
        int maxQuarantined = 8,
        int maxStageWorkers = 8) => new()
    {
        CleanupGrace = TimeSpan.FromMilliseconds(cleanupGraceMs),
        MaxQuarantinedResources = maxQuarantined,
        MaxStageWorkers = maxStageWorkers,
    };

    private static async Task<T> WaitOrFailAsync<T>(Task<T> operation, int timeoutMs = 4000)
    {
        var winner = await Task.WhenAny(operation, Task.Delay(timeoutMs));
        Assert.AreSame(operation, winner, $"业务调用在 {timeoutMs} ms 内未返回（资源回收无界）");
        return await operation;
    }

    // ---------- 同步启动段必须在有界 worker 上 ----------

    [TestMethod]
    public async Task SynchronouslyBlockingConnect_DoesNotBlockTheBusinessWait()
    {
        var factory = new FakeTransportFactory
        {
            // 连接方法在返回 Task 之前同步阻塞 1.5 秒：业务等待不得被它卡住。
            OnConnect = (_, _) =>
            {
                Thread.Sleep(1500);
                return Task.FromResult<ISshTransportSession>(new FakeSession());
            },
        };

        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var stopwatch = Stopwatch.StartNew();
        var result = await WaitOrFailAsync(
            coordinator.ExecuteAsync(
                FakeSsh.Host(deadline: DateTimeOffset.UtcNow.AddMilliseconds(200)),
                FakeSsh.Execute(),
                CancellationToken.None));
        stopwatch.Stop();

        Assert.AreEqual(SshExecutionStatus.TimedOut, result.Status);
        Assert.IsLessThan(1000, (int)stopwatch.ElapsedMilliseconds, "同步启动段不得让业务等待超过 deadline+宽限");
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => coordinator.ActiveStageWorkers == 0, 4000), "worker 最终必须归还");
    }

    [TestMethod]
    public async Task StageWorkerPoolFull_ReturnsBusyWithoutConnecting()
    {
        var gate = new TaskCompletionSource<ISshTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeTransportFactory { OnConnect = (_, _) => gate.Task };
        await using var coordinator = new SshOperationCoordinator(
            factory,
            new FakeIdentityProvider(),
            Options(maxStageWorkers: 1));

        var first = coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => coordinator.ActiveStageWorkers == 1));

        var rejected = await WaitOrFailAsync(coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None));
        Assert.AreEqual(SshErrorCodes.Busy, rejected.ErrorCode);
        Assert.AreEqual(SshPhase.Queued, rejected.Phase);

        gate.SetResult(new FakeSession());
        await WaitOrFailAsync(first);
    }

    // ---------- 有界关闭与隔离区 ----------

    [TestMethod]
    public async Task SessionThatNeverCloses_IsQuarantined_AndReportedAsPending()
    {
        var session = new FakeSession { DisposeNeverCompletes = true };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var stopwatch = Stopwatch.StartNew();
        var result = await WaitOrFailAsync(coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None));
        stopwatch.Stop();

        Assert.AreEqual(SshExecutionStatus.Completed, result.Status, "清理问题不得改写业务结果");
        Assert.IsLessThan(2000, (int)stopwatch.ElapsedMilliseconds, "清理必须有界");
        StringAssert.Contains(result.Diagnostic ?? string.Empty, "cleanup_pending");
        Assert.AreEqual(1, coordinator.QuarantinedResources, "未结束的清理必须仍在追踪");
        Assert.IsTrue(session.AbortRequested, "预算用尽前必须已登记中止请求");
    }

    [TestMethod]
    public async Task QuarantineFull_RefusesNewWorkWithBusy()
    {
        var session = new FakeSession { DisposeNeverCompletes = true };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };
        await using var coordinator = new SshOperationCoordinator(
            factory,
            new FakeIdentityProvider(),
            Options(maxQuarantined: 1));

        await WaitOrFailAsync(coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None));
        Assert.AreEqual(1, coordinator.QuarantinedResources);

        var refused = await WaitOrFailAsync(coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None));
        Assert.AreEqual(SshErrorCodes.Busy, refused.ErrorCode);
        Assert.AreEqual(SshPhase.Queued, refused.Phase);
        Assert.AreEqual(1, factory.ConnectCount, "隔离区满额时不得再创建连接");
    }

    [TestMethod]
    public async Task QuarantineDrains_WhenTheResourceFinallyEnds()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession();
        var gateSession = new GatedDisposeSession(session, release);
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(gateSession) };
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options(maxQuarantined: 4));

        await WaitOrFailAsync(coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None));
        Assert.AreEqual(1, coordinator.QuarantinedResources);

        release.SetResult();
        Assert.IsTrue(
            await FakeSsh.WaitUntilAsync(() => coordinator.QuarantinedResources == 0, 4000),
            "资源真正结束后隔离计数必须归零（不得泄漏）");
    }

    /// <summary>释放挂在测试信号上的会话：用于证明「隔离区最终会归还」。</summary>
    private sealed class GatedDisposeSession(ISshTransportSession inner, TaskCompletionSource gate) : ISshTransportSession
    {
        public string VerifiedHostKeySha256 => inner.VerifiedHostKeySha256;

        public string HostKeyAlgorithm => inner.HostKeyAlgorithm;

        public bool IsConnected => inner.IsConnected;

        public SshSubmissionEvidence SubmissionEvidence => inner.SubmissionEvidence;

        public void RequestAbort() => inner.RequestAbort();

        public Task<SshExecuteResult> ExecuteAsync(string command, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken) =>
            inner.ExecuteAsync(command, timeout, maxOutputBytes, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await gate.Task.ConfigureAwait(false);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---------- 停止与连续超时 ----------

    [TestMethod]
    public async Task StopAsync_IsBounded_AndReportsPendingHonestly()
    {
        var session = new FakeSession
        {
            OnExecute = async (_, _, _, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return FakeResults.Completed();
            },
            DisposeNeverCompletes = true,
        };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options(cleanupGraceMs: 200));

        var operation = coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => session.ExecuteCount == 1));

        var stopwatch = Stopwatch.StartNew();
        var report = await WaitOrFailAsync(coordinator.StopAsync(), 4000);
        stopwatch.Stop();

        Assert.IsLessThan(2500, (int)stopwatch.ElapsedMilliseconds, "StopAsync 必须有界");
        Assert.IsTrue(
            report.PendingOperations >= 0 && report.PendingCleanups >= 0 && report.PendingResources >= 0,
            "报告不得为负");
        Assert.IsTrue(
            report.PendingOperations + report.PendingResources + report.PendingCleanups > 0,
            "存在在飞操作或未结束的释放工作时，Stop 必须如实报告（不得宣称全部收敛）");

        var result = await WaitOrFailAsync(operation);
        Assert.AreEqual(SshExecutionStatus.Cancelled, result.Status);
    }

    [TestMethod]
    public async Task ConsecutiveTimeouts_ReturnAllCountersToBaseline()
    {
        var factory = new FakeTransportFactory
        {
            OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(new FakeSession()),
        };
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        for (var i = 0; i < 5; i++)
        {
            var result = await WaitOrFailAsync(
                coordinator.ExecuteAsync(
                    FakeSsh.Host(deadline: DateTimeOffset.UtcNow.AddMilliseconds(60)),
                    FakeSsh.Execute(),
                    CancellationToken.None));
            Assert.IsTrue(
                result.Status is SshExecutionStatus.TimedOut or SshExecutionStatus.Completed,
                result.ErrorCode ?? result.Status.ToString());
        }

        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => coordinator.ActiveLeases == 0, 4000), "租约必须回到基线");
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => coordinator.ActiveStageWorkers == 0, 4000), "worker 必须回到基线");
        Assert.AreEqual(0, coordinator.QuarantinedResources, "隔离区不得有残留");
        Assert.AreEqual(0, coordinator.WaitQueueLength);
    }

    [TestMethod]
    public async Task NoAutoReplay_SubmissionWindowFailureRunsExecExactlyOnce()
    {
        var session = new FakeSession
        {
            OnExecute = async (_, _, _, _) =>
            {
                await Task.Yield();
                throw new SshConnectionException("reset in submit window");
            },
        };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };

        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());
        var result = await WaitOrFailAsync(coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None));

        Assert.AreEqual(1, session.ExecuteCount, "不得自动重放");
        Assert.IsFalse(result.RetrySafe);
        Assert.AreEqual(SshExecutionState.Unknown, result.ExecutionState);
    }

    // ---------- 迟到资源的所有权 ----------

    [TestMethod]
    public async Task LateConnect_Success_IsClosedAndIdentityZeroedOnce()
    {
        var connectGate = new TaskCompletionSource<ISshTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeTransportFactory { OnConnect = (_, _) => connectGate.Task };
        var material = FakeSsh.Material();
        var identities = new FakeIdentityProvider { OnGet = _ => material };
        await using var coordinator = new SshOperationCoordinator(factory, identities, Options());

        var result = await WaitOrFailAsync(
            coordinator.ExecuteAsync(
                FakeSsh.Host(deadline: DateTimeOffset.UtcNow.AddMilliseconds(150)),
                FakeSsh.Execute(),
                CancellationToken.None));
        Assert.AreEqual(SshExecutionStatus.TimedOut, result.Status);

        var late = new FakeSession();
        connectGate.SetResult(late);

        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => late.Disposed, 4000), "迟到的连接必须被关闭");
        Assert.AreEqual(0, late.ExecuteCount, "迟到连接不得提交命令");
        Assert.ThrowsExactly<ObjectDisposedException>(() => material.OpenKeyStream(), "身份材料必须已清零");
        Assert.AreEqual(0, coordinator.QuarantinedResources);
    }

    [TestMethod]
    public async Task LateIdentity_Success_IsZeroed_AndNeverConnects()
    {
        var identityGate = new TaskCompletionSource<SshIdentityMaterial>(TaskCreationOptions.RunContinuationsAsynchronously);
        var material = FakeSsh.Material();
        var identities = new FakeIdentityProvider { OnGetAsync = (_, _) => identityGate.Task };
        var factory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(factory, identities, Options());

        await WaitOrFailAsync(
            coordinator.ExecuteAsync(
                FakeSsh.Host(deadline: DateTimeOffset.UtcNow.AddMilliseconds(150)),
                FakeSsh.Execute(),
                CancellationToken.None));

        identityGate.SetResult(material);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() =>
        {
            try
            {
                material.OpenKeyStream().Dispose();
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }, 4000), "迟到的身份材料必须被清零");

        Assert.AreEqual(0, factory.ConnectCount);
    }

    [TestMethod]
    public async Task TestEntry_AlsoUsesBoundedWorkersAndQuarantine()
    {
        var session = new FakeSession { DisposeNeverCompletes = true };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var result = await WaitOrFailAsync(coordinator.TestAsync(FakeSsh.Host(), FakeSsh.Test(), CancellationToken.None));

        Assert.AreEqual(SshExecutionStatus.Completed, result.Status);
        Assert.AreEqual(1, coordinator.QuarantinedResources, "ssh_test 的释放同样必须被追踪");
        Assert.AreEqual(0, coordinator.ActiveLeases);
    }
}
