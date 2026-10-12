using System.Diagnostics;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;
using Renci.SshNet.Common;

namespace PuddingSshTests;

/// <summary>
/// A3 的**假传输故障矩阵**：协调器的准入、deadline、凭据、建连、执行、清理与停止全部在纯组件进程里被断言，
/// 不需要 sshd、不装配 Core（组件化交付规程 S2/S3）。
/// </summary>
[TestClass]
public sealed class SshOperationCoordinatorFaultMatrixTests
{
    private static SshCoordinatorOptions Options(
        int coreMax = 8,
        int waitQueue = 32,
        int cleanupGraceMs = 150) => new()
    {
        CoreMaxConcurrentOperations = coreMax,
        MaxWaitQueueLength = waitQueue,
        CleanupGrace = TimeSpan.FromMilliseconds(cleanupGraceMs),
    };

    /// <summary>可阻塞的假机群：连接立即成功，命令挂在测试控制的信号上。</summary>
    private sealed class BlockingFleet
    {
        private readonly List<TaskCompletionSource> _releases = [];
        private readonly object _gate = new();
        private int _executing;
        private bool _releaseAll;

        public BlockingFleet()
        {
            Factory.OnConnect = (_, _) =>
            {
                var session = new FakeSession();
                session.OnExecute = async (_, _, _, _) =>
                {
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_gate)
                    {
                        if (++_executing > MaxConcurrentExecutes)
                        {
                            MaxConcurrentExecutes = _executing;
                        }

                        _releases.Add(release);

                        // 「全部放行」是一个开关：之后才被准入的操作也立刻放行，
                        // 否则后进入队列的操作会永远等不到自己的信号。
                        if (_releaseAll)
                        {
                            release.TrySetResult();
                        }
                    }

                    try
                    {
                        await release.Task.ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _executing--;
                        }
                    }

                    return FakeResults.Completed();
                };

                return Task.FromResult<ISshTransportSession>(session);
            };
        }

        public FakeTransportFactory Factory { get; } = new();

        public int ConnectCount => Factory.ConnectCount;

        public int MaxConcurrentExecutes { get; private set; }

        public int Running
        {
            get
            {
                lock (_gate)
                {
                    return _executing;
                }
            }
        }

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

        public void ReleaseOne()
        {
            TaskCompletionSource? first;
            lock (_gate)
            {
                first = _releases.FirstOrDefault(r => !r.Task.IsCompleted);
            }

            first?.TrySetResult();
        }
    }

    // ---------- 并发准入 ----------

    [TestMethod]
    public async Task PerHostLimit_ThirdOperationWaitsForASlot()
    {
        var fleet = new BlockingFleet();
        await using var coordinator = new SshOperationCoordinator(fleet.Factory, new FakeIdentityProvider(), Options());
        var host = FakeSsh.Host(maxConcurrentOperations: 2);

        var first = coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);
        var second = coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);
        var third = coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);

        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => fleet.ConnectCount == 2), "前两条应当已建连");
        await Task.Delay(60);
        Assert.AreEqual(2, fleet.ConnectCount, "第三条必须排队，不得建连");
        Assert.AreEqual(1, coordinator.WaitQueueLength);

        fleet.ReleaseOne();
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => fleet.ConnectCount == 3), "释放一个槽位后第三条应当被准入");
        fleet.ReleaseAll();

        var results = await Task.WhenAll(first, second, third);
        foreach (var result in results)
        {
            Assert.AreEqual(SshExecutionStatus.Completed, result.Status);
        }

        Assert.IsLessThanOrEqualTo(2, fleet.MaxConcurrentExecutes, "每主机并行不得超过 2");
        Assert.AreEqual(0, coordinator.WaitQueueLength, "操作结束后等待计数必须归零");
    }

    [TestMethod]
    public async Task HostGate_IsKeyedByRevision_SoAChangedConfigDoesNotShareTheOldGate()
    {
        var fleet = new BlockingFleet();
        await using var coordinator = new SshOperationCoordinator(fleet.Factory, new FakeIdentityProvider(), Options());
        var oldRevision = FakeSsh.Host(FakeSsh.Target(revision: "rev-1"), maxConcurrentOperations: 1);
        var newRevision = FakeSsh.Host(FakeSsh.Target(revision: "rev-2"), maxConcurrentOperations: 1);

        var first = coordinator.ExecuteAsync(oldRevision, FakeSsh.Execute(FakeSsh.Target(revision: "rev-1")), CancellationToken.None);
        var second = coordinator.ExecuteAsync(newRevision, FakeSsh.Execute(FakeSsh.Target(revision: "rev-2")), CancellationToken.None);

        // 每主机上限是 1，但两条操作属于**不同 revision**（不同的门），因此应当并发被准入。
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => fleet.Running == 2), "不同 revision 不得共用同一个并发门");
        Assert.AreEqual(0, coordinator.WaitQueueLength);

        fleet.ReleaseAll();
        await Task.WhenAll(first, second);
    }

    [TestMethod]
    public async Task WaitQueueFull_ReturnsBusyWithoutConnecting()
    {
        var fleet = new BlockingFleet();
        await using var coordinator = new SshOperationCoordinator(
            fleet.Factory,
            new FakeIdentityProvider(),
            Options(waitQueue: 1));
        var host = FakeSsh.Host(maxConcurrentOperations: 1);

        var running = coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => fleet.ConnectCount == 1));

        var queued = coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => coordinator.WaitQueueLength == 1), "第二条应当进入队列");

        var rejected = await coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.Busy, rejected.ErrorCode);
        Assert.AreEqual(SshPhase.Queued, rejected.Phase);
        Assert.IsTrue(rejected.RetrySafe);
        Assert.AreEqual(SshExecutionState.NotSubmitted, rejected.ExecutionState);
        Assert.AreEqual(1, fleet.ConnectCount, "队满必须立即拒绝：不得为它创建连接");

        fleet.ReleaseAll();
        await running;
        await queued;
    }

    [TestMethod]
    public async Task QueuedOperation_CallerCancellation_ReturnsCancelledAndNeverConnects()
    {
        var fleet = new BlockingFleet();
        await using var coordinator = new SshOperationCoordinator(fleet.Factory, new FakeIdentityProvider(), Options());
        var host = FakeSsh.Host(maxConcurrentOperations: 1);

        var running = coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => fleet.ConnectCount == 1));

        using var cts = new CancellationTokenSource();
        var queued = coordinator.ExecuteAsync(host, FakeSsh.Execute(), cts.Token);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => coordinator.WaitQueueLength == 1));

        await cts.CancelAsync();
        var result = await queued;

        Assert.AreEqual(SshExecutionStatus.Cancelled, result.Status);
        Assert.AreEqual(SshErrorCodes.Cancelled, result.ErrorCode);
        Assert.AreEqual(SshPhase.Queued, result.Phase);
        Assert.AreEqual(1, fleet.ConnectCount, "排队中被取消不得建连");

        fleet.ReleaseAll();
        await running;
    }

    [TestMethod]
    public async Task QueuedOperation_DeadlineExpiry_ReturnsTimeoutAndNeverConnects()
    {
        var fleet = new BlockingFleet();
        await using var coordinator = new SshOperationCoordinator(fleet.Factory, new FakeIdentityProvider(), Options());
        var host = FakeSsh.Host(maxConcurrentOperations: 1);

        var running = coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => fleet.ConnectCount == 1));

        var queuedHost = host with { ExecutionDeadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(150) };
        var result = await coordinator.ExecuteAsync(queuedHost, FakeSsh.Execute(), CancellationToken.None);

        Assert.AreEqual(SshExecutionStatus.TimedOut, result.Status);
        Assert.AreEqual(SshErrorCodes.Timeout, result.ErrorCode);
        Assert.AreEqual(SshPhase.Queued, result.Phase);
        Assert.AreEqual(1, fleet.ConnectCount, "排队中到期不得建连");

        fleet.ReleaseAll();
        await running;
    }

    [TestMethod]
    public async Task CoreGate_LimitsTotalConcurrencyAcrossHosts()
    {
        var fleet = new BlockingFleet();
        await using var coordinator = new SshOperationCoordinator(fleet.Factory, new FakeIdentityProvider(), Options(coreMax: 2));

        var operations = new[]
        {
            coordinator.ExecuteAsync(FakeSsh.Host(FakeSsh.Target(hostId: "h1")), FakeSsh.Execute(FakeSsh.Target(hostId: "h1")), CancellationToken.None),
            coordinator.ExecuteAsync(FakeSsh.Host(FakeSsh.Target(hostId: "h2")), FakeSsh.Execute(FakeSsh.Target(hostId: "h2")), CancellationToken.None),
            coordinator.ExecuteAsync(FakeSsh.Host(FakeSsh.Target(hostId: "h3")), FakeSsh.Execute(FakeSsh.Target(hostId: "h3")), CancellationToken.None),
        };

        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => fleet.Running == 2));
        await Task.Delay(80);
        Assert.AreEqual(2, fleet.ConnectCount, "Core 总并行 2：第三条不得建连");
        Assert.AreEqual(1, coordinator.WaitQueueLength);

        fleet.ReleaseAll();
        var results = await Task.WhenAll(operations);

        Assert.AreEqual(3, fleet.ConnectCount);
        Assert.IsLessThanOrEqualTo(2, fleet.MaxConcurrentExecutes);
        foreach (var result in results)
        {
            Assert.AreEqual(SshExecutionStatus.Completed, result.Status);
        }
    }

    [TestMethod]
    public async Task AdmissionSlots_AreReleasedAfterFailures()
    {
        var factory = new FakeTransportFactory
        {
            OnConnect = (_, _) => throw new SshConnectionException("refused"),
        };

        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());
        var host = FakeSsh.Host(maxConcurrentOperations: 2);

        for (var i = 0; i < 4; i++)
        {
            var failed = await coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);
            Assert.AreEqual(SshErrorCodes.ConnectFailed, failed.ErrorCode);
        }

        // 槽位没有泄漏：随后两条并发操作应当都能被准入（而不是卡在门里）。
        var fleet = new BlockingFleet();
        await using var second = new SshOperationCoordinator(fleet.Factory, new FakeIdentityProvider(), Options());
        var first = second.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);
        var other = second.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => fleet.ConnectCount == 2));
        fleet.ReleaseAll();
        await Task.WhenAll(first, other);
        Assert.AreEqual(0, second.InFlightOperations);
    }

    // ---------- 校验与 revision ----------

    [TestMethod]
    public async Task TargetRevisionMismatch_IsTargetChanged_WithoutIdentityOrConnect()
    {
        var factory = new FakeTransportFactory();
        var identities = new FakeIdentityProvider();
        await using var coordinator = new SshOperationCoordinator(factory, identities, Options());
        var host = FakeSsh.Host();

        var stale = FakeSsh.Execute(FakeSsh.Target(revision: "rev-OLD"));
        var result = await coordinator.ExecuteAsync(host, stale, CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.TargetChanged, result.ErrorCode);
        Assert.AreEqual(SshPhase.Validation, result.Phase);
        Assert.IsFalse(result.RetrySafe);
        Assert.AreEqual(0, factory.ConnectCount);
        Assert.AreEqual(0, identities.Calls, "revision 不匹配时不得取凭据");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("echo\0hidden")]
    public async Task InvalidCommand_IsRequestInvalid_WithoutConnect(string command)
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var result = await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(command: command), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.RequestInvalid, result.ErrorCode);
        Assert.AreEqual(SshPhase.Validation, result.Phase);
        Assert.AreEqual(0, factory.ConnectCount);
    }

    [TestMethod]
    public async Task InvalidCwd_IsRequestInvalid_AndConfigCwdIsConfigInvalid()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var badRequest = await coordinator.ExecuteAsync(
            FakeSsh.Host(),
            FakeSsh.Execute(cwd: "relative/path"),
            CancellationToken.None);
        Assert.AreEqual(SshErrorCodes.RequestInvalid, badRequest.ErrorCode);

        var badConfig = await coordinator.ExecuteAsync(
            FakeSsh.Host(defaultCwd: "relative-default"),
            FakeSsh.Execute(),
            CancellationToken.None);
        Assert.AreEqual(SshErrorCodes.ConfigInvalid, badConfig.ErrorCode);

        Assert.AreEqual(0, factory.ConnectCount);
    }

    [TestMethod]
    public async Task ExpiredHostDeadline_IsTimeout_WithoutConnect()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var host = FakeSsh.Host(deadline: DateTimeOffset.UtcNow.AddSeconds(-1));
        var result = await coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);

        Assert.AreEqual(SshExecutionStatus.TimedOut, result.Status);
        Assert.AreEqual(SshErrorCodes.Timeout, result.ErrorCode);
        Assert.AreEqual(SshPhase.Validation, result.Phase);
        Assert.AreEqual(0, factory.ConnectCount);
    }

    [TestMethod]
    public async Task InvalidTarget_IsConfigInvalid()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var badTarget = FakeSsh.Target(port: 0);
        var result = await coordinator.ExecuteAsync(
            FakeSsh.Host(target: badTarget),
            FakeSsh.Execute(target: badTarget),
            CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.ConfigInvalid, result.ErrorCode);
        Assert.AreEqual(0, factory.ConnectCount);
    }

    // ---------- 凭据 ----------

    [TestMethod]
    public async Task IdentityPassphraseRequired_IsReported_WithoutConnect()
    {
        var factory = new FakeTransportFactory();
        var identities = new FakeIdentityProvider
        {
            OnGet = _ => throw new SshComponentException(SshErrorCodes.PassphraseRequired, "encrypted key"),
        };

        await using var coordinator = new SshOperationCoordinator(factory, identities, Options());
        var result = await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.PassphraseRequired, result.ErrorCode);
        Assert.AreEqual(SshPhase.Credentials, result.Phase);
        Assert.IsFalse(result.RetrySafe);
        Assert.AreEqual(0, factory.ConnectCount);
    }

    [TestMethod]
    public async Task IdentityIoFailure_IsCredentialUnavailable()
    {
        var factory = new FakeTransportFactory();
        var identities = new FakeIdentityProvider
        {
            OnGet = _ => throw new IOException("denied"),
        };

        await using var coordinator = new SshOperationCoordinator(factory, identities, Options());
        var result = await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.CredentialUnavailable, result.ErrorCode);
        Assert.AreEqual(SshPhase.Credentials, result.Phase);
        Assert.AreEqual(0, factory.ConnectCount);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task IdentityMaterial_IsDisposedAfterTheOperation(bool connectSucceeds)
    {
        var factory = new FakeTransportFactory();
        if (!connectSucceeds)
        {
            factory.OnConnect = (_, _) => throw new SshConnectionException("refused");
        }

        var material = FakeSsh.Material();
        var identities = new FakeIdentityProvider { OnGet = _ => material };
        await using var coordinator = new SshOperationCoordinator(factory, identities, Options());

        var result = await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);
        Assert.AreEqual(connectSucceeds ? SshExecutionStatus.Completed : SshExecutionStatus.Failed, result.Status);

        // 认证材料必须已销毁（内存里的私钥被清零并标记释放）。
        Assert.ThrowsExactly<ObjectDisposedException>(() => material.OpenKeyStream());
    }

    // ---------- 建连 ----------

    [TestMethod]
    public async Task ConnectFailure_IsConnectFailed_AndRetrySafe()
    {
        var factory = new FakeTransportFactory
        {
            OnConnect = (_, _) => throw new SshConnectionException("refused"),
        };

        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());
        var result = await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.ConnectFailed, result.ErrorCode);
        Assert.AreEqual(SshPhase.Connecting, result.Phase);
        Assert.AreEqual(SshExecutionState.NotSubmitted, result.ExecutionState);
        Assert.IsTrue(result.RetrySafe, "确定未提交的连通失败可安全再次尝试");
    }

    [TestMethod]
    public async Task HostKeyRejection_CarriesItsCode_AndIsNotRetrySafe()
    {
        var factory = new FakeTransportFactory
        {
            OnConnect = (_, _) => throw new SshHostKeyRejectedException(SshErrorCodes.HostKeyMismatch, "fingerprint changed"),
        };

        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());
        var result = await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.HostKeyMismatch, result.ErrorCode);
        Assert.AreEqual(SshPhase.Connecting, result.Phase);
        Assert.IsFalse(result.RetrySafe, "指纹不可信需要用户核对，不得标记为可重试");
    }

    [TestMethod]
    public async Task ConnectDeadline_IsBounded_ReturnsTimeoutWithConnectingPhase()
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
        var host = FakeSsh.Host(deadline: DateTimeOffset.UtcNow.AddMilliseconds(150));

        var stopwatch = Stopwatch.StartNew();
        var result = await coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);
        stopwatch.Stop();

        Assert.AreEqual(SshExecutionStatus.TimedOut, result.Status);
        Assert.AreEqual(SshErrorCodes.Timeout, result.ErrorCode);
        Assert.AreEqual(SshPhase.Connecting, result.Phase);
        Assert.IsLessThan(3000, (int)stopwatch.ElapsedMilliseconds, "建连必须有界收敛");
    }

    [TestMethod]
    public async Task SessionIsDisposed_AfterExecute()
    {
        var session = new FakeSession();
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };

        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());
        await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);

        Assert.IsTrue(session.Disposed, "独占连接必须在操作结束后释放");
        Assert.AreEqual("uname -a", session.LastCommand);
        Assert.AreEqual(64 * 1024, session.LastOutputBudget);
    }

    // ---------- 执行 ----------

    [TestMethod]
    public async Task SessionResult_IsPassedThrough_WithTotalDuration()
    {
        var session = new FakeSession
        {
            OnExecute = (_, _, _, _) => Task.FromResult(FakeResults.Completed(stdout: "Linux probe")),
        };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };

        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());
        var result = await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);

        Assert.AreEqual(SshExecutionStatus.Completed, result.Status);
        Assert.AreEqual(SshExecutionState.Exited, result.ExecutionState);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("Linux probe", result.Stdout);
        Assert.AreEqual(FakeSsh.HostId, result.HostId);
        Assert.AreEqual(FakeSsh.Revision, result.HostRevision);
    }

    [TestMethod]
    public async Task ExecutionPlan_IsTheExactApprovalString()
    {
        var session = new FakeSession();
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };

        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());
        await coordinator.ExecuteAsync(
            FakeSsh.Host(defaultCwd: "/srv/app"),
            FakeSsh.Execute(command: "echo it's ok"),
            CancellationToken.None);

        Assert.AreEqual("cd -- '/srv/app' || exit $?\necho it's ok", session.LastCommand);
    }

    [TestMethod]
    public async Task SessionThrowsAfterSubmit_IsConnectionLost_NotRetrySafe_AndUnknownState()
    {
        var session = new FakeSession
        {
            // 从异步路径抛出 ⇒ 命令**已提交**，这是「提交后断线」的形状。
            OnExecute = async (_, _, _, _) =>
            {
                await Task.Yield();
                throw new SshConnectionException("reset by peer");
            },
        };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };

        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());
        var result = await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.ConnectionLost, result.ErrorCode);
        Assert.AreEqual(SshExecutionState.Unknown, result.ExecutionState, "提交后断线可能已执行");
        Assert.AreEqual(SshPhase.Collecting, result.Phase);
        Assert.IsFalse(result.RetrySafe);
    }

    [TestMethod]
    public async Task SessionThrowsBeforeSubmit_KeepsSubmittingPhaseAndIsRetrySafe()
    {
        var session = new FakeSession
        {
            // 同步抛出（连 Task 都没返回）⇒ 命令**未提交**，与上一条的形状必须可区分。
            OnExecute = (_, _, _, _) => throw new SshConnectionException("channel not opened"),
        };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };

        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());
        var result = await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.ConnectFailed, result.ErrorCode);
        Assert.AreEqual(SshPhase.Submitting, result.Phase);
        Assert.AreEqual(SshExecutionState.NotSubmitted, result.ExecutionState);
        Assert.IsTrue(result.RetrySafe, "未提交的失败可以安全再次尝试（phase 区分它与建连阶段失败）");
    }

    [TestMethod]
    public async Task CallerCancellation_ReturnsCancelled()
    {
        var session = new FakeSession
        {
            OnExecute = async (_, _, _, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return FakeResults.Completed();
            },
        };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        using var cts = new CancellationTokenSource();
        var operation = coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), cts.Token);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => session.ExecuteCount == 1));
        await cts.CancelAsync();

        var result = await operation;

        Assert.AreEqual(SshExecutionStatus.Cancelled, result.Status);
        Assert.AreEqual(SshErrorCodes.Cancelled, result.ErrorCode);
        Assert.AreEqual(SshExecutionState.Unknown, result.ExecutionState);
        Assert.IsTrue(session.Disposed);
    }

    [TestMethod]
    public async Task OperationDeadline_ConvertsSessionCancelledToTimeout()
    {
        var session = new FakeSession
        {
            // 真实会话在 token 触发时返回结果（不是抛异常）：这里复刻该形状。
            OnExecute = async (_, _, _, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    return FakeResults.Completed() with
                    {
                        Status = SshExecutionStatus.Cancelled,
                        ErrorCode = SshErrorCodes.Cancelled,
                        ExecutionState = SshExecutionState.Unknown,
                        ExitCode = null,
                    };
                }

                return FakeResults.Completed();
            },
        };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var host = FakeSsh.Host(deadline: DateTimeOffset.UtcNow.AddMilliseconds(200));
        var result = await coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);

        Assert.AreEqual(SshExecutionStatus.TimedOut, result.Status, "本端到期必须报 timeout，而不是把会话的 cancelled 原样透出");
        Assert.AreEqual(SshErrorCodes.Timeout, result.ErrorCode);
    }

    [TestMethod]
    public async Task HostileSession_IsAbandonedAfterGrace_StateUnknown_AndSessionDisposed()
    {
        var session = new FakeSession
        {
            // 恶意/卡死会话：完全忽略取消 token。
            OnExecute = async (_, _, _, _) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return FakeResults.Completed();
            },
        };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };
        await using var coordinator = new SshOperationCoordinator(
            factory,
            new FakeIdentityProvider(),
            Options(cleanupGraceMs: 150));

        var host = FakeSsh.Host(deadline: DateTimeOffset.UtcNow.AddMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();
        var result = await coordinator.ExecuteAsync(host, FakeSsh.Execute(), CancellationToken.None);
        stopwatch.Stop();

        Assert.AreEqual(SshExecutionStatus.TimedOut, result.Status);
        Assert.AreEqual(SshExecutionState.Unknown, result.ExecutionState, "无退出证据时不得声称远端已结束");
        Assert.AreEqual(SshPhase.Cleanup, result.Phase);
        Assert.IsLessThan(3000, (int)stopwatch.ElapsedMilliseconds, "清理宽限后必须放弃等待");
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => session.Disposed), "放弃等待后仍要释放独占连接");
    }

    [TestMethod]
    public async Task HostileDispose_DoesNotBlockBeyondGrace()
    {
        var session = new FakeSession { DisposeDelay = TimeSpan.FromSeconds(10) };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };
        await using var coordinator = new SshOperationCoordinator(
            factory,
            new FakeIdentityProvider(),
            Options(cleanupGraceMs: 150));

        var stopwatch = Stopwatch.StartNew();
        var result = await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);
        stopwatch.Stop();

        Assert.AreEqual(SshExecutionStatus.Completed, result.Status, "释放卡住不得改写业务结果");
        Assert.IsLessThan(3000, (int)stopwatch.ElapsedMilliseconds, "释放本身必须是有界的");
    }

    // ---------- 停止与 ssh_test ----------

    [TestMethod]
    public async Task StopAsync_CancelsInFlightOperation()
    {
        var session = new FakeSession
        {
            OnExecute = async (_, _, _, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return FakeResults.Completed();
            },
        };
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var operation = coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => session.ExecuteCount == 1));

        var stopwatch = Stopwatch.StartNew();
        await coordinator.StopAsync();
        stopwatch.Stop();

        var result = await operation;
        Assert.AreEqual(SshExecutionStatus.Cancelled, result.Status);
        Assert.IsLessThan(3000, (int)stopwatch.ElapsedMilliseconds, "停止不应被在飞操作拖住");
        Assert.IsTrue(await FakeSsh.WaitUntilAsync(() => coordinator.InFlightOperations == 0));
    }

    [TestMethod]
    public async Task RequestsAfterStop_ReturnBusyAndNeverConnect()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());
        await coordinator.StopAsync();

        var result = await coordinator.ExecuteAsync(FakeSsh.Host(), FakeSsh.Execute(), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.Busy, result.ErrorCode);
        Assert.AreEqual(SshPhase.Queued, result.Phase);
        Assert.AreEqual(0, factory.ConnectCount);
    }

    [TestMethod]
    public async Task TestAsync_ConnectsWithoutExecuting()
    {
        var session = new FakeSession();
        var factory = new FakeTransportFactory { OnConnect = (_, _) => Task.FromResult<ISshTransportSession>(session) };
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var result = await coordinator.TestAsync(FakeSsh.Host(), FakeSsh.Test(), CancellationToken.None);

        Assert.AreEqual(SshExecutionStatus.Completed, result.Status);
        Assert.IsTrue(result.AuthenticationSucceeded);
        Assert.AreEqual("SHA256:fake-verified-key", result.VerifiedHostKeySha256);
        Assert.AreEqual("ssh-ed25519", result.HostKeyAlgorithm);
        Assert.AreEqual(0, session.ExecuteCount, "ssh_test 不得执行 shell");
        Assert.IsTrue(session.Disposed);
        Assert.AreEqual(1, factory.ConnectCount);
        CollectionAssert.IsSubsetOf(
            new[] { "queue", "credentials", "connect" },
            result.PhaseDurationsMs.Keys.ToArray());
    }

    [TestMethod]
    public async Task TestAsync_ConnectFailure_IsReportedWithStableCode()
    {
        var factory = new FakeTransportFactory
        {
            OnConnect = (_, _) => throw new SshAuthenticationException("denied"),
        };
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var result = await coordinator.TestAsync(FakeSsh.Host(), FakeSsh.Test(), CancellationToken.None);

        Assert.AreEqual(SshExecutionStatus.Failed, result.Status);
        Assert.AreEqual(SshErrorCodes.AuthenticationFailed, result.ErrorCode);
        Assert.AreEqual(SshPhase.Connecting, result.Phase);
        Assert.IsFalse(result.AuthenticationSucceeded);
    }

    [TestMethod]
    public async Task TestAsync_UsesTheFrozenTrustSnapshotForTheRequestedEndpoint()
    {
        var factory = new FakeTransportFactory();
        await using var coordinator = new SshOperationCoordinator(factory, new FakeIdentityProvider(), Options());

        var target = FakeSsh.Target(host: "::1", port: 22122);
        await coordinator.TestAsync(FakeSsh.Host(target: target), FakeSsh.Test(target: target), CancellationToken.None);

        Assert.HasCount(1, factory.Requests);
        Assert.AreEqual("::1", factory.Requests[0].Target.Host);
        Assert.AreEqual(22122, factory.Requests[0].Target.Port);
        Assert.IsNotNull(factory.Requests[0].HostKeyVerifier);
    }
}
