using System.Diagnostics;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;
using Renci.SshNet.Common;

namespace PuddingSshTests;

/// <summary>
/// R2 回归（ADR-096 D2）：取消/截止是**前置围栏**，不能只把 token 传给下一层。
/// <para>
/// 审阅报告 F2 / P1 的最小反例：调用前已取消仍会读身份、建连、执行；身份与建连忽略 token 时永不返回；
/// 阶段晚返回后仍然继续下一阶段（甚至提交 exec）。这些用例用 TCS 控制晚返回与调用计数，
/// 断言「截止后 Connect/Execute 数为 0」，不靠长 sleep。
/// </para>
/// </summary>
[TestClass]
public sealed class SshOperationLifetimeFenceTests
{
    private static SshCoordinatorOptions Options(int cleanupGraceMs = 150) => new()
    {
        CleanupGrace = TimeSpan.FromMilliseconds(cleanupGraceMs),
    };

    /// <summary>操作必须在给定时限内返回；旧实现缺少前置围栏时会挂起，这里让取红表现为断言失败。</summary>
    private static async Task<T> WaitOrFailAsync<T>(Task<T> operation, int timeoutMs = 3000)
    {
        var winner = await Task.WhenAny(operation, Task.Delay(timeoutMs));
        Assert.AreSame(operation, winner, $"操作在 {timeoutMs} ms 内未返回（前置围栏缺失）");
        return await operation;
    }

    private static SshHostContext Host(DateTimeOffset? deadline = null, int operationTimeoutSeconds = 60) =>
        FakeSsh.Host(deadline: deadline, operationTimeoutSeconds: operationTimeoutSeconds);

    // ---------- 预先取消 ----------

    [TestMethod]
    public async Task PreCancelled_Execute_HasZeroSideEffects()
    {
        var factory = new FakeTransportFactory();
        var session = new FakeSession();
        factory.OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session);
        var identities = new FakeIdentityProvider();
        await using var coordinator = new SshOperationCoordinator(factory, identities, Options());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await WaitOrFailAsync(coordinator.ExecuteAsync(Host(), FakeSsh.Execute(), cts.Token));

        Assert.AreEqual(SshExecutionStatus.Cancelled, result.Status);
        Assert.AreEqual(SshErrorCodes.Cancelled, result.ErrorCode);
        Assert.AreEqual(SshExecutionState.NotSubmitted, result.ExecutionState);
        Assert.AreEqual(0, identities.Calls, "已取消的请求不得读取身份");
        Assert.AreEqual(0, factory.ConnectCount, "已取消的请求不得建连");
        Assert.AreEqual(0, session.ExecuteCount, "已取消的请求不得提交命令");
        StringAssert.Contains(result.Diagnostic ?? string.Empty, "stop_reason=caller_cancelled");
    }

    [TestMethod]
    public async Task PreCancelled_Test_HasZeroSideEffects()
    {
        var factory = new FakeTransportFactory();
        var identities = new FakeIdentityProvider();
        await using var coordinator = new SshOperationCoordinator(factory, identities, Options());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await WaitOrFailAsync(coordinator.TestAsync(Host(), FakeSsh.Test(), cts.Token));

        Assert.AreEqual(SshExecutionStatus.Cancelled, result.Status);
        Assert.AreEqual(0, identities.Calls);
        Assert.AreEqual(0, factory.ConnectCount);
    }

    [TestMethod]
    public async Task PreExpiredHostDeadline_Execute_HasZeroSideEffects()
    {
        var factory = new FakeTransportFactory();
        var identities = new FakeIdentityProvider();
        await using var coordinator = new SshOperationCoordinator(factory, identities, Options());

        var result = await WaitOrFailAsync(coordinator.ExecuteAsync(
            Host(deadline: DateTimeOffset.UtcNow.AddSeconds(-1)),
            FakeSsh.Execute(),
            CancellationToken.None));

        Assert.AreEqual(SshExecutionStatus.TimedOut, result.Status);
        Assert.AreEqual(0, identities.Calls);
        Assert.AreEqual(0, factory.ConnectCount);
    }

    // ---------- 身份阶段 ----------

    [TestMethod]
    public async Task Identity_IgnoringToken_IsBounded_AndLateSuccessNeverConnects()
    {
        var identityGate = new TaskCompletionSource<SshIdentityMaterial>(TaskCreationOptions.RunContinuationsAsynchronously);
        var material = FakeSsh.Material();
        var identities = new FakeIdentityProvider { OnGetAsync = (_, _) => identityGate.Task };

        var factory = new FakeTransportFactory();
        var session = new FakeSession();
        factory.OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session);
        await using var coordinator = new SshOperationCoordinator(factory, identities, Options());

        var host = Host(deadline: DateTimeOffset.UtcNow.AddMilliseconds(150));
        var stopwatch = Stopwatch.StartNew();
        var result = await WaitOrFailAsync(coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None));
        stopwatch.Stop();

        Assert.AreEqual(SshExecutionStatus.TimedOut, result.Status);
        Assert.AreEqual(SshPhase.Credentials, result.Phase);
        Assert.IsLessThan(3000, (int)stopwatch.ElapsedMilliseconds, "身份阶段必须有界收敛");

        // 晚成功只能触发材料销毁，绝不能开启下一阶段。
        identityGate.SetResult(material);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => !identityGate.Task.IsFaulted), "gate 已交还");
        await Task.Delay(150);

        Assert.AreEqual(0, factory.ConnectCount, "迟到的身份不得触发建连");
        Assert.AreEqual(0, session.ExecuteCount, "迟到的身份不得提交命令");
        Assert.ThrowsExactly<ObjectDisposedException>(() => material.OpenKeyStream());
    }

    [TestMethod]
    public async Task Identity_LateFailure_IsObserved_AndNeverConnects()
    {
        var identityGate = new TaskCompletionSource<SshIdentityMaterial>(TaskCreationOptions.RunContinuationsAsynchronously);
        var identities = new FakeIdentityProvider { OnGetAsync = (_, _) => identityGate.Task };

        var factory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(factory, identities, Options());

        var result = await WaitOrFailAsync(coordinator.ExecuteAsync(
            Host(deadline: DateTimeOffset.UtcNow.AddMilliseconds(150)),
            FakeSsh.Execute(),
            CancellationToken.None));

        Assert.AreEqual(SshExecutionStatus.TimedOut, result.Status);

        identityGate.SetException(new IOException("late identity failure"));
        await Task.Delay(150);

        Assert.AreEqual(0, factory.ConnectCount, "迟到的身份失败不得触发建连");
    }

    // ---------- 建连阶段 ----------

    [TestMethod]
    public async Task Connect_IgnoringToken_IsBounded_AndLateSessionIsRecycledWithoutExec()
    {
        var connectGate = new TaskCompletionSource<ISshTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeTransportFactory { OnConnect = (_, _) => connectGate.Task };
        var lateSession = new FakeSession();
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var stopwatch = Stopwatch.StartNew();
        var result = await WaitOrFailAsync(coordinator.ExecuteAsync(
            Host(deadline: DateTimeOffset.UtcNow.AddMilliseconds(150)),
            FakeSsh.Execute(),
            CancellationToken.None));
        stopwatch.Stop();

        Assert.AreEqual(SshExecutionStatus.TimedOut, result.Status);
        Assert.AreEqual(SshPhase.Connecting, result.Phase);
        Assert.IsLessThan(3000, (int)stopwatch.ElapsedMilliseconds, "建连阶段必须有界收敛");
        Assert.AreEqual(0, lateSession.ExecuteCount);

        // 迟到的连接只能被回收，不能提交 exec。
        connectGate.SetResult(lateSession);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => lateSession.Disposed), "迟到的连接必须被关闭");
        Assert.AreEqual(0, lateSession.ExecuteCount, "迟到的连接不得提交命令");
    }

    [TestMethod]
    public async Task Connect_LateFailure_IsObserved()
    {
        var connectGate = new TaskCompletionSource<ISshTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeTransportFactory { OnConnect = (_, _) => connectGate.Task };
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var result = await WaitOrFailAsync(coordinator.ExecuteAsync(
            Host(deadline: DateTimeOffset.UtcNow.AddMilliseconds(150)),
            FakeSsh.Execute(),
            CancellationToken.None));

        Assert.AreEqual(SshExecutionStatus.TimedOut, result.Status);

        connectGate.SetException(new SshConnectionException("late connect failure"));
        await Task.Delay(150);
    }

    [TestMethod]
    public async Task Connect_LateHostKeyRejection_IsObserved()
    {
        var connectGate = new TaskCompletionSource<ISshTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeTransportFactory { OnConnect = (_, _) => connectGate.Task };
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var result = await WaitOrFailAsync(coordinator.ExecuteAsync(
            Host(deadline: DateTimeOffset.UtcNow.AddMilliseconds(150)),
            FakeSsh.Execute(),
            CancellationToken.None));

        Assert.AreEqual(SshExecutionStatus.TimedOut, result.Status);

        connectGate.SetException(new SshHostKeyRejectedException(SshErrorCodes.HostKeyMismatch, "late rejection"));
        await Task.Delay(150);
    }

    // ---------- 共享预算与停止原因 ----------

    [TestMethod]
    public async Task OperationDeadline_IsSharedAcrossStages_ConnectDoesNotResetTheBudget()
    {
        var factory = new FakeTransportFactory();
        var session = new FakeSession();
        factory.OnConnect = async (_, ct) =>
        {
            await Task.Delay(120, ct); // 吃掉大部分预算
            return session;
        };

        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var stopwatch = Stopwatch.StartNew();
        var result = await WaitOrFailAsync(coordinator.ExecuteAsync(
            Host(deadline: DateTimeOffset.UtcNow.AddMilliseconds(400)),
            FakeSsh.Execute(),
            CancellationToken.None));
        stopwatch.Stop();

        Assert.AreEqual(SshExecutionStatus.Completed, result.Status, result.ErrorCode ?? "null");
        Assert.IsTrue(session.LastTimeout.HasValue, "会话应当收到剩余预算");
        Assert.IsLessThan(
            400,
            (int)session.LastTimeout!.Value.TotalMilliseconds,
            "exec 阶段不得重新拿到完整的原始预算");
        Assert.IsLessThan(3000, (int)stopwatch.ElapsedMilliseconds);
    }

    [TestMethod]
    public async Task HostStopping_IsReportedAsCancelledWithItsOwnReason_NotAsTimeout()
    {
        var factory = new FakeTransportFactory
        {
            OnConnect = async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable");
            },
        };

        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());
        var operation = coordinator.ExecuteAsync(Host(), FakeSsh.Execute(), CancellationToken.None);
        await Task.Delay(120);

        await coordinator.StopAsync();
        var result = await WaitOrFailAsync(operation);

        Assert.AreEqual(SshExecutionStatus.Cancelled, result.Status, "Core 停止不得被写成超时");
        Assert.AreEqual(SshErrorCodes.Cancelled, result.ErrorCode);
        StringAssert.Contains(result.Diagnostic ?? string.Empty, "stop_reason=host_stopping");
    }

    [TestMethod]
    public async Task CallerCancellationDuringConnect_ReportsCallerCancelled()
    {
        var factory = new FakeTransportFactory
        {
            OnConnect = async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable");
            },
        };

        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());
        using var cts = new CancellationTokenSource();
        var operation = coordinator.ExecuteAsync(Host(), FakeSsh.Execute(), cts.Token);
        await Task.Delay(120);
        await cts.CancelAsync();

        var result = await WaitOrFailAsync(operation);

        Assert.AreEqual(SshExecutionStatus.Cancelled, result.Status);
        StringAssert.Contains(result.Diagnostic ?? string.Empty, "stop_reason=caller_cancelled");
    }

    [TestMethod]
    public async Task CancelledWhileQueued_NeverReadsIdentity()
    {
        var fleet = new BlockingFleetForFence();
        var identities = new FakeIdentityProvider();
        await using var coordinator = new SshOperationCoordinator(fleet.Factory, identities, Options());
        var host = FakeSsh.Host(maxConcurrentOperations: 1);

        var running = coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => fleet.ConnectCount == 1));

        using var cts = new CancellationTokenSource();
        var queued = coordinator.ExecuteAsync(host, FakeSsh.Execute(), cts.Token);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => coordinator.WaitQueueLength == 1));

        await cts.CancelAsync();
        var result = await WaitOrFailAsync(queued);

        Assert.AreEqual(SshExecutionStatus.Cancelled, result.Status);
        Assert.AreEqual(SshPhase.Queued, result.Phase);
        Assert.AreEqual(1, identities.Calls, "排队中被取消的操作不得读取身份（只有已运行的那一条读过）");
        Assert.AreEqual(1, fleet.ConnectCount);

        fleet.ReleaseAll();
        await running;
    }

    /// <summary>只服务 R2 的阻塞机群：连接立即成功、exec 阻塞到测试放行。</summary>
    private sealed class BlockingFleetForFence
    {
        private readonly List<TaskCompletionSource> _releases = [];
        private readonly object _gate = new();
        private bool _releaseAll;

        public BlockingFleetForFence()
        {
            Factory.OnConnect = (_, _) =>
            {
                var session = new FakeSession();
                session.OnExecute = async (_, _, _, _) =>
                {
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_gate)
                    {
                        _releases.Add(release);
                        if (_releaseAll)
                        {
                            release.TrySetResult();
                        }
                    }

                    await release.Task;
                    return FakeResults.Completed();
                };

                return Task.FromResult<ISshTransportSession>(session);
            };
        }

        public FakeTransportFactory Factory { get; } = new();

        public int ConnectCount => Factory.ConnectCount;

        public void ReleaseAll()
        {
            TaskCompletionSource[] pending;
            lock (_gate)
            {
                _releaseAll = true;
                pending = _releases.Where(r => !r.Task.IsCompleted).ToArray();
            }

            foreach (var release in pending)
            {
                release.TrySetResult();
            }
        }
    }
}
