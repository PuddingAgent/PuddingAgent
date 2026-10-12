using System.Collections.Concurrent;
using System.Diagnostics;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Validation;

namespace PuddingSsh.Execution;

/// <summary>
/// <see cref="ISshClient"/> 的实现：**一次操作独占一条连接**的协调器（设计 §1.2 第 2 条、§9）。
/// <para>
/// 它按设计 §8 的顺序执行组件这一侧的门：形状/revision 校验 → **并发准入（排队可取消）** → 凭据 →
/// 主机验证 → 命令提交 → 有界采集 → 释放。能力/审批、主机 ACL、远程命令不变量属引用方（A5）。
/// </para>
/// <para>
/// 三条容易退化的语义在这里被显式落实：
/// <list type="number">
///   <item><b>操作总时长从「受理」起算</b>，含排队与建连；排队中到期即 <c>ssh.timeout</c>，且**不会**建连。</item>
///   <item><b>等待队列满额立即 <c>ssh.busy</c></b>，绝不创建无界等待任务。</item>
///   <item><b>清理宽限是资源回收而不是执行许可</b>：截止后最多再等 <see cref="SshCoordinatorOptions.CleanupGrace"/>，
///   然后释放连接并把「已提交但无退出证据」如实报成 <c>execution_state=unknown</c>。</item>
/// </list>
/// </para>
/// </summary>
public sealed class SshOperationCoordinator : ISshClient, IAsyncDisposable
{
    private readonly ISshTransportFactory _transport;
    private readonly ISshIdentityProvider _identities;
    private readonly SshCoordinatorOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _coreGate;
    private readonly SshCleanupQuarantine _quarantine;
    private readonly SshStageWorkerPool _stageWorkers;
    private readonly SshStageWorkerPool _closeWorkers;
    private int _activeLeases;
    private int _pendingLateResources;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _inFlightGate = new();

    private TaskCompletionSource? _drained;
    private int _inFlight;
    private int _waiters;
    private volatile bool _stopping;

    public SshOperationCoordinator(
        ISshTransportFactory transport,
        ISshIdentityProvider identities,
        SshCoordinatorOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
        _options = options ?? SshCoordinatorOptions.Default;
        _time = timeProvider ?? TimeProvider.System;

        if (_options.CoreMaxConcurrentOperations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CoreMaxConcurrentOperations 必须 ≥ 1");
        }

        if (_options.MaxWaitQueueLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxWaitQueueLength 不能为负");
        }

        _coreGate = new SemaphoreSlim(_options.CoreMaxConcurrentOperations, _options.CoreMaxConcurrentOperations);
        // 非法协调器配置在构造处就拒绝：不让越权配置「看起来生效」。
        _options.Validate();
        _quarantine = new SshCleanupQuarantine(_options.MaxQuarantinedResources);
        _stageWorkers = new SshStageWorkerPool(_options.MaxStageWorkers);
        _closeWorkers = new SshStageWorkerPool(_options.MaxCloseWorkers);
    }

    /// <summary>当前在飞操作数（含排队）；供诊断与测试断言「槽位没有泄漏」。</summary>
    public int InFlightOperations
    {
        get
        {
            lock (_inFlightGate)
            {
                return _inFlight;
            }
        }
    }

    /// <summary>当前正在等待门（global/host）的操作数。</summary>
    public int WaitQueueLength => Volatile.Read(ref _waiters);

    /// <summary>持有资源租约的操作数（业务路径尚未收尾的数量）。</summary>
    public int ActiveLeases => Volatile.Read(ref _activeLeases);

    /// <summary>清理隔离区里仍未结束的资源数；满额时新增工作会被拒绝（<c>ssh.busy</c>）。</summary>
    public int QuarantinedResources => _quarantine.Pending;

    /// <summary>
    /// **业务已返回但物理资源仍未结束**的租约数（L1）。它与 <see cref="ActiveLeases"/> 的区别是：
    /// 业务等待有界返回之后，exec/reader/close/迟到回收仍可能在跑，那部分必须继续占账，
    /// 不能用 <c>ActiveLeases == 0</c> 证明资源已收敛。
    /// </summary>
    public int PendingResourceLeases => Volatile.Read(ref _pendingLateResources);

    /// <summary>阶段 worker 占用数（含已超时但线程尚未归还的）。</summary>
    public int ActiveStageWorkers => _stageWorkers.Active;

    public int QuarantineCapacity => _quarantine.Capacity;

    public int StageWorkerCapacity => _stageWorkers.Capacity;

    /// <summary>阶段 worker 的历史峰值：并发 barrier 测试断言「从未越限」。</summary>
    public int PeakStageWorkers => _stageWorkers.Peak;

    public int StageWorkerRejections => _stageWorkers.Rejected;

    /// <summary>关闭 worker 占用与容量（关闭工作有独立容量，不被阶段池饿死）。</summary>
    public int ActiveCloseWorkers => _closeWorkers.Active;

    public int PeakCloseWorkers => _closeWorkers.Peak;

    public int CloseWorkerCapacity => _closeWorkers.Capacity;

    /// <summary>隔离区历史峰值：容量是硬上限，峰值也不得越限。</summary>
    public int PeakQuarantinedResources => _quarantine.Peak;

    public Task<SshTestResult> TestAsync(SshHostContext host, SshTestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request);
        return TrackAsync(() => TestCoreAsync(host, request, cancellationToken));
    }

    public Task<SshExecuteResult> ExecuteAsync(SshHostContext host, SshExecuteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request);
        return TrackAsync(() => ExecuteCoreAsync(host, request, cancellationToken));
    }

    /// <summary>
    /// 停止受理新操作、取消在飞操作，并最多等待 <see cref="SshCoordinatorOptions.CleanupGrace"/> 回收资源
    /// （设计 §9.2）。每个操作自己的路径负责在宽限内关闭独占连接，本方法不延长业务许可。
    /// </summary>
    public async Task<SshStopReport> StopAsync(CancellationToken cancellationToken = default)
    {
        _stopping = true;

        // 停止 deadline 从**进入方法**起算：取消回调可能阻塞，因此不 await CancelAsync，
        // 而是把 Cancel 放到 worker 上并纳入同一次有界等待（L3）。
        var cancelTask = Task.Run(
            () =>
            {
                try
                {
                    _shutdown.Cancel();
                }
                catch (Exception)
                {
                    // 回调抛错不得让 Stop 失败；如实通过 CancelCallbacksPending 表达未收敛。
                }
            });

        var drained = DrainedTask();
        var wait = drained is null ? cancelTask : Task.WhenAll(drained, cancelTask);
        var winner = await Task.WhenAny(wait, Task.Delay(_options.CleanupGrace, cancellationToken))
            .ConfigureAwait(false);
        var timedOut = winner != wait;

        return new SshStopReport(
            InFlightOperations,
            _quarantine.Pending,
            PendingResourceLeases,
            timedOut,
            CancelCallbacksPending: !cancelTask.IsCompleted);
    }

    public async ValueTask DisposeAsync()
    {
        _ = await StopAsync().ConfigureAwait(false);

        // 刻意不释放 _coreGate / _shutdown：StopAsync 的宽限到期后，仍可能有操作在自己的
        // finally 里走 Release() 或读 _shutdown.Token；释放它们会把「停止」变成 ObjectDisposedException。
    }

    private async Task<SshTestResult> TestCoreAsync(SshHostContext host, SshTestRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var startedAt = _time.GetUtcNow();
        var phaseDurations = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            SshRequestValidator.ValidateTarget(request.Target);
        }
        catch (SshComponentException ex)
        {
            return TestFailure(host, SshPhase.Validation, ex.ErrorCode, ex.Message, stopwatch);
        }

        if (!TargetsMatch(host.Target, request.Target))
        {
            return TestFailure(
                host,
                SshPhase.Validation,
                SshErrorCodes.TargetChanged,
                "请求目标与冻结主机快照不一致（配置可能已变化）；请重新发现并重新审批。",
                stopwatch);
        }

        try
        {
            // ssh_test 与 ssh_execute 共用机器校验：非法主机限额一律拒绝，不做静默夹取。
            host.Limits.Validate();
        }
        catch (SshComponentException ex)
        {
            return TestFailure(host, SshPhase.Validation, ex.ErrorCode, ex.Message, stopwatch);
        }

        var timeout = host.Limits.ResolveOperationTimeout(null);
        if (ResolveDeadlineBudget(host, startedAt, ref timeout, out var deadlineFailure))
        {
            return TestFailure(host, SshPhase.Validation, SshErrorCodes.Timeout, deadlineFailure!, stopwatch);
        }

        if (_stopping)
        {
            return TestFailure(host, SshPhase.Queued, SshErrorCodes.Busy, "组件正在停止，未受理新操作。", stopwatch);
        }

        if (_quarantine.IsFull)
        {
            return TestFailure(host, SshPhase.Queued, SshErrorCodes.Busy, "清理隔离区已满（ssh.busy）：拒绝新增工作，直到已知资源真正结束。", stopwatch);
        }

        using var lifetime = new SshOperationLifetime(_time, timeout, cancellationToken, _shutdown.Token);

        // 受理处的前置围栏：已取消/已过期/宿主已停止的请求不读身份、不建连、不提交（ADR-096 D2）。
        if (TryTestFence(lifetime, host, stopwatch, out var testFenced))
        {
            return testFenced!;
        }

        var admission = await AdmitAsync(GateKey(host.Target), host.Limits.MaxConcurrentOperations, lifetime)
            .ConfigureAwait(false);
        if (admission.Kind != AdmissionKind.Admitted)
        {
            var code = ResolveAdmissionCode(admission.Kind, lifetime);
            return TestFailure(host, SshPhase.Queued, code, DescribeAdmission(admission.Kind, lifetime), stopwatch);
        }

        phaseDurations["queue"] = Elapsed(stopwatch);
        var lease = BeginLease(admission);
        StageHandoff? handoff = null;
        var cleanupBudget = new SshCleanupBudget(_time, _options.CleanupGrace);
        try
        {
            // 身份阶段：拿到槽位后再检查一次终止；身份与建连都只等**剩余额度**。
            if (TryTestFence(lifetime, host, stopwatch, out testFenced))
            {
                return testFenced!;
            }

            SshStageOutcome<SshIdentityMaterial> identityOutcome;
            try
            {
                // 阶段端口在**有界 worker** 上启动（原子令牌；登记先于调度）：同步启动段不卡业务线程（ADR-096 D3）。
                var identityTask = _stageWorkers.StartAsync(() => _identities.GetIdentityAsync(host.Identity, lifetime.Token).AsTask());
                lease.RegisterWork(identityTask, "identity");
                identityOutcome = await lifetime
                    .WaitStageAsync(
                        identityTask,
                        lifetime.RemainingOperation,
                        SshStopReason.OperationDeadline,
                        onLateSuccess: material => material.Dispose(),
                        onLateFailure: _ => { })
                    .ConfigureAwait(false);
            }
            catch (SshStageCapacityExceededException)
            {
                return TestFailure(host, SshPhase.Queued, SshErrorCodes.Busy, "阶段 worker 已满（ssh.busy）：未读取身份、未创建连接。", stopwatch);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = SshFailureClassifier.Classify(ex, new SshFailureFacts(SshPhase.Credentials, false));
                return TestFailure(host, SshPhase.Credentials, failure.ErrorCode, SshDiagnosticText.FromException(ex), stopwatch);
            }

            if (!identityOutcome.Completed)
            {
                return TestFailure(
                    host,
                    SshPhase.Credentials,
                    StopCode(identityOutcome.Reason),
                    StopDiagnostic(identityOutcome.Reason, "取凭据阶段"),
                    stopwatch);
            }

            var identity = identityOutcome.Result!;
            lease.AttachIdentity(identity);
            phaseDurations["credentials"] = Elapsed(stopwatch) - phaseDurations["queue"];

            ISshTransportSession? session = null;
            try
            {
                var connectBudget = lifetime.RemainingConnect(host.Limits.ConnectTimeout);
                var connectRequest = new SshTransportConnectRequest(
                    request.Target,
                    identity,
                    host.Trust.CreateVerifier(request.Target.Host, request.Target.Port),
                    connectBudget);

                // L2：在把身份交付 connect **之前**完成所有权转移（不是等迟到回调才置位）。
                lease.BeginIdentityConnect();
                handoff = new StageHandoff(lease);
                SshStageOutcome<ISshTransportSession> connectOutcome;
                try
                {
                    var connectTask = _stageWorkers.StartAsync(() => _transport.ConnectAsync(connectRequest, lifetime.Token));
                    lease.RegisterWork(connectTask, "connect");
                    connectOutcome = await lifetime
                        .WaitStageAsync(
                            connectTask,
                            connectBudget,
                            SshStopReason.ConnectDeadline,
                            onLateSuccess: late =>
                            {
                                // 迟到连接只能被回收：关闭走独立关闭池，身份由租约在所有资源结束后释放。
                                lease.MarkIdentitySessionOwned();
                                lease.AttachSession(late);
                                _ = CloseLateResourcesAsync(lease, handoff);
                            },
                            onLateFailure: error =>
                            {
                                _ = error;
                                _ = CloseLateResourcesAsync(lease, handoff);
                            })
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var failure = SshFailureClassifier.Classify(ex, new SshFailureFacts(SshPhase.Connecting, false));
                    var code = ex is OperationCanceledException ? StopCode(lifetime.StopReason) : failure.ErrorCode;
                    return TestFailure(host, SshPhase.Connecting, code, SshDiagnosticText.FromException(ex), stopwatch);
                }

                if (!connectOutcome.Completed)
                {
                    // 与停止选择不可分割：立刻把交接义务的结算权交给迟到 owner。
                    handoff.MarkAbandoned();
                    return TestFailure(
                        host,
                        SshPhase.Connecting,
                        StopCode(connectOutcome.Reason),
                        StopDiagnostic(connectOutcome.Reason, "建连阶段"),
                        stopwatch);
                }

                session = connectOutcome.Result!;
                lease.MarkIdentitySessionOwned();
                lease.AttachSession(session);
                if (TryTestFence(lifetime, host, stopwatch, out testFenced))
                {
                    var fencedCleanup = await CloseSessionOnceAsync(lease, cleanupBudget.Remaining).ConfigureAwait(false);
                    return testFenced! with
                    {
                        CleanupState = fencedCleanup.Completed ? SshCleanupState.Completed : SshCleanupState.Pending,
                    };
                }

                phaseDurations["connect"] = Elapsed(stopwatch) - phaseDurations["queue"] - phaseDurations["credentials"];

                // 有界关闭：同一条连接在预算内没结束就进隔离区（不得声称已释放）。
                var cleanup = await CloseSessionOnceAsync(lease, cleanupBudget.Remaining).ConfigureAwait(false);
                var cleanupState = cleanup.Completed ? SshCleanupState.Completed : SshCleanupState.Pending;

                return new SshTestResult
                {
                    Status = SshExecutionStatus.Completed,
                    HostId = host.Target.HostId,
                    HostRevision = host.Target.HostRevision,
                    Target = request.Target,
                    VerifiedHostKeySha256 = session.VerifiedHostKeySha256,
                    HostKeyAlgorithm = string.IsNullOrEmpty(session.HostKeyAlgorithm) ? null : session.HostKeyAlgorithm,
                    AuthenticationSucceeded = true,
                    Phase = SshPhase.Completed,
                    CleanupState = cleanupState,
                    DurationMs = Elapsed(stopwatch),
                    PhaseDurationsMs = phaseDurations,
                };
            }
            finally
            {
                // 兜底：任何提前返回（例如提交前围栏）都不得把已建立的连接漏在租约外。
                if (lease.HasSession)
                {
                    _ = await CloseSessionOnceAsync(lease, cleanupBudget.Remaining).ConfigureAwait(false);
                }

                // 交接义务在**所有登记完成之后**结算：迟到路径与及时路径各只结算一次。
                handoff?.SettleFromBusiness();
                lease.ReleaseBusinessResources();
            }
        }
        finally
        {
            EndLease(lease);
        }
    }

    private async Task<SshExecuteResult> ExecuteCoreAsync(SshHostContext host, SshExecuteRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var startedAt = _time.GetUtcNow();

        try
        {
            SshRequestValidator.ValidateTarget(request.Target);
        }
        catch (SshComponentException ex)
        {
            return ExecuteFailure(host, SshPhase.Validation, ex.ErrorCode, ex.Message, retrySafe: false, stopwatch);
        }

        if (!TargetsMatch(host.Target, request.Target))
        {
            return ExecuteFailure(
                host,
                SshPhase.Validation,
                SshErrorCodes.TargetChanged,
                "请求目标与冻结主机快照不一致（配置可能已变化）；请重新发现并重新审批。",
                retrySafe: false,
                stopwatch);
        }

        // 与 ssh_test 共用同一个 validator：限额校验 + 请求校验 + 执行串（审批看到的就是将要提交的精确字符串）。
        SshPosixExecutionPlan plan;
        TimeSpan timeout;
        int outputBudget;
        try
        {
            (plan, timeout, outputBudget) = SshRequestValidator.ValidateExecute(request, host.Limits, host.DefaultCwd);
        }
        catch (SshComponentException ex)
        {
            return ExecuteFailure(host, SshPhase.Validation, ex.ErrorCode, ex.Message, retrySafe: false, stopwatch);
        }

        if (ResolveDeadlineBudget(host, startedAt, ref timeout, out var deadlineFailure))
        {
            return ExecuteFailure(host, SshPhase.Validation, SshErrorCodes.Timeout, deadlineFailure!, retrySafe: false, stopwatch);
        }

        if (_stopping)
        {
            return ExecuteFailure(host, SshPhase.Queued, SshErrorCodes.Busy, "组件正在停止，未受理新操作。", retrySafe: true, stopwatch);
        }

        if (_quarantine.IsFull)
        {
            return ExecuteFailure(host, SshPhase.Queued, SshErrorCodes.Busy, "清理隔离区已满（ssh.busy）：拒绝新增工作，直到已知资源真正结束。", retrySafe: true, stopwatch);
        }

        using var lifetime = new SshOperationLifetime(_time, timeout, cancellationToken, _shutdown.Token);

        // 受理处的前置围栏：已取消/已过期/宿主已停止的请求不读身份、不建连、不提交（ADR-096 D2）。
        if (TryFence(lifetime, host, stopwatch, out var fenced))
        {
            return fenced!;
        }

        var admission = await AdmitAsync(GateKey(host.Target), host.Limits.MaxConcurrentOperations, lifetime)
            .ConfigureAwait(false);
        if (admission.Kind != AdmissionKind.Admitted)
        {
            var code = ResolveAdmissionCode(admission.Kind, lifetime);
            return ExecuteFailure(host, SshPhase.Queued, code, DescribeAdmission(admission.Kind, lifetime), code == SshErrorCodes.Busy, stopwatch);
        }

        var lease = BeginLease(admission);
        StageHandoff? handoff = null;
        var cleanupBudget = new SshCleanupBudget(_time, _options.CleanupGrace);
        try
        {
            if (TryFence(lifetime, host, stopwatch, out fenced))
            {
                return fenced!;
            }

            SshStageOutcome<SshIdentityMaterial> identityOutcome;
            try
            {
                // 阶段端口在**有界 worker** 上启动（原子令牌；登记先于调度）：同步启动段不卡业务线程（ADR-096 D3）。
                var identityTask = _stageWorkers.StartAsync(() => _identities.GetIdentityAsync(host.Identity, lifetime.Token).AsTask());
                lease.RegisterWork(identityTask, "identity");
                identityOutcome = await lifetime
                    .WaitStageAsync(
                        identityTask,
                        lifetime.RemainingOperation,
                        SshStopReason.OperationDeadline,
                        onLateSuccess: material => material.Dispose(),
                        onLateFailure: _ => { })
                    .ConfigureAwait(false);
            }
            catch (SshStageCapacityExceededException)
            {
                return ExecuteFailure(host, SshPhase.Queued, SshErrorCodes.Busy, "阶段 worker 已满（ssh.busy）：未读取身份、未创建连接。", retrySafe: true, stopwatch);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = SshFailureClassifier.Classify(ex, new SshFailureFacts(SshPhase.Credentials, false));
                return ExecuteFailure(host, SshPhase.Credentials, failure.ErrorCode, SshDiagnosticText.FromException(ex), failure.RetrySafe, stopwatch);
            }

            if (!identityOutcome.Completed)
            {
                return ExecuteFailure(
                    host,
                    SshPhase.Credentials,
                    StopCode(identityOutcome.Reason),
                    StopDiagnostic(identityOutcome.Reason, "取凭据阶段"),
                    retrySafe: false,
                    stopwatch);
            }

            var identity = identityOutcome.Result!;
            lease.AttachIdentity(identity);
            ISshTransportSession? session = null;
            try
            {
                var connectBudget = lifetime.RemainingConnect(host.Limits.ConnectTimeout);
                var connectRequest = new SshTransportConnectRequest(
                    request.Target,
                    identity,
                    host.Trust.CreateVerifier(request.Target.Host, request.Target.Port),
                    connectBudget);

                // L2：在把身份交付 connect **之前**完成所有权转移（不是等迟到回调才置位）。
                lease.BeginIdentityConnect();
                handoff = new StageHandoff(lease);
                SshStageOutcome<ISshTransportSession> connectOutcome;
                try
                {
                    var connectTask = _stageWorkers.StartAsync(() => _transport.ConnectAsync(connectRequest, lifetime.Token));
                    lease.RegisterWork(connectTask, "connect");
                    connectOutcome = await lifetime
                        .WaitStageAsync(
                            connectTask,
                            connectBudget,
                            SshStopReason.ConnectDeadline,
                            onLateSuccess: late =>
                            {
                                // 迟到连接只能被回收：关闭走独立关闭池，身份由租约在所有资源结束后释放。
                                lease.MarkIdentitySessionOwned();
                                lease.AttachSession(late);
                                _ = CloseLateResourcesAsync(lease, handoff);
                            },
                            onLateFailure: error =>
                            {
                                _ = error;
                                _ = CloseLateResourcesAsync(lease, handoff);
                            })
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var failure = SshFailureClassifier.Classify(ex, new SshFailureFacts(SshPhase.Connecting, false));
                    var code = ex is OperationCanceledException ? StopCode(lifetime.StopReason) : failure.ErrorCode;
                    return ExecuteFailure(
                        host,
                        SshPhase.Connecting,
                        code,
                        SshDiagnosticText.FromException(ex),
                        SshFailureClassifier.IsRetrySafe(code, commandSubmitted: false),
                        stopwatch);
                }

                if (!connectOutcome.Completed)
                {
                    // 与停止选择不可分割：立刻把交接义务的结算权交给迟到 owner。
                    handoff.MarkAbandoned();
                    return ExecuteFailure(
                        host,
                        SshPhase.Connecting,
                        StopCode(connectOutcome.Reason),
                        StopDiagnostic(connectOutcome.Reason, "建连阶段"),
                        retrySafe: false,
                        stopwatch);
                }

                session = connectOutcome.Result!;
                lease.MarkIdentitySessionOwned();
                lease.AttachSession(session);

                // 提交前围栏：会话已到手但已终止时不得发 exec。
                if (TryFence(lifetime, host, stopwatch, out fenced))
                {
                    var fencedCleanup = await CloseSessionOnceAsync(lease, cleanupBudget.Remaining).ConfigureAwait(false);
                    return fenced! with
                    {
                        CleanupState = fencedCleanup.Completed ? SshCleanupState.Completed : SshCleanupState.Pending,
                    };
                }

                var result = await ExecuteOnSessionAsync(host, session, plan, lifetime, lease, cleanupBudget, outputBudget, stopwatch)
                    .ConfigureAwait(false);

                // 有界关闭（与 TERM/排空共享同一份预算）；没结束就进隔离区并如实标注。
                var cleanup = await CloseSessionOnceAsync(lease, cleanupBudget.Remaining).ConfigureAwait(false);
                return cleanup.Completed
                    ? result with { CleanupState = SshCleanupState.Completed }
                    : result with
                    {
                        CleanupState = SshCleanupState.Pending,
                        Diagnostic = AppendDiagnostic(result.Diagnostic, "cleanup_pending：清理未在预算内结束，已进入隔离区，不得声称独占连接已释放。"),
                    };
            }
            finally
            {
                // 兜底：任何提前返回（例如提交前围栏）都不得把已建立的连接漏在租约外。
                if (lease.HasSession)
                {
                    _ = await CloseSessionOnceAsync(lease, cleanupBudget.Remaining).ConfigureAwait(false);
                }

                // 交接义务在**所有登记完成之后**结算：迟到路径与及时路径各只结算一次。
                handoff?.SettleFromBusiness();
                lease.ReleaseBusinessResources();
            }
        }
        finally
        {
            EndLease(lease);
        }
    }

    private async Task<SshExecuteResult> ExecuteOnSessionAsync(
        SshHostContext host,
        ISshTransportSession session,
        SshPosixExecutionPlan plan,
        SshOperationLifetime lifetime,
        SshResourceLease lease,
        SshCleanupBudget cleanupBudget,
        int outputBudget,
        Stopwatch stopwatch)
    {
        Task<SshExecuteResult> executeTask;
        try
        {
            // exec 阶段只拿**剩余**额度：不得在换阶段时重新得到完整的原始预算（ADR-096 D2）。
            var remaining = lifetime.RemainingOperation;
            if (remaining <= TimeSpan.Zero)
            {
                return ExecuteFailure(
                    host,
                    SshPhase.Cleanup,
                    SshErrorCodes.Timeout,
                    StopDiagnostic(SshStopReason.OperationDeadline, "提交前"),
                    retrySafe: false,
                    stopwatch);
            }

            // L3：exec 的**同步启动段**也在有界 worker 上（外部线程立即拿到可等待句柄）。
            executeTask = _stageWorkers.StartAsync(() => session.ExecuteAsync(plan.ExecutedCommand, remaining, outputBudget, lifetime.Token));
            lease.RegisterWork(executeTask, "exec");
        }
        catch (Exception ex)
        {
            // 提交窗口异常：只有底层明确「未发送」才可能保持 NotSubmitted 与 retry_safe（ADR-096 D4）。
            var evidence = session.SubmissionEvidence;
            var submitted = evidence == SshSubmissionEvidence.SubmissionAttempted;
            var failure = SshFailureClassifier.Classify(ex, new SshFailureFacts(SshPhase.Submitting, submitted));
            return ExecuteFailure(
                host,
                SshPhase.Submitting,
                failure.ErrorCode,
                SshDiagnosticText.FromException(ex),
                SshFailureClassifier.IsRetrySafe(failure.ErrorCode, submitted),
                stopwatch,
                submitted ? SshExecutionState.Unknown : SshExecutionState.NotSubmitted);
        }

        var first = await Task.WhenAny(executeTask, Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token))
            .ConfigureAwait(false);

        SshExecuteResult result;
        if (first == executeTask)
        {
            try
            {
                result = await executeTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 提交事实决定失败形状（ADR-096 D4）：`NotSent` 才允许保持未提交与可重试，
                // `SubmissionAttempted` 一律「远端状态未知、不可重试」。
                var submitted = session.SubmissionEvidence == SshSubmissionEvidence.SubmissionAttempted;
                var failure = SshFailureClassifier.Classify(ex, new SshFailureFacts(SshPhase.Submitting, submitted));
                return ExecuteFailure(
                    host,
                    SshPhase.Submitting,
                    failure.ErrorCode,
                    SshDiagnosticText.FromException(ex),
                    SshFailureClassifier.IsRetrySafe(failure.ErrorCode, submitted),
                    stopwatch,
                    submitted ? SshExecutionState.Unknown : SshExecutionState.NotSubmitted);
            }
        }
        else
        {
            // 已到期/被取消：只再给一次**资源回收**宽限；超时就不再等会话（不延长业务许可）。
            var grace = await Task.WhenAny(executeTask, Task.Delay(cleanupBudget.Remaining, CancellationToken.None))
                .ConfigureAwait(false);

            if (grace == executeTask)
            {
                try
                {
                    result = await executeTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var failure = SshFailureClassifier.Classify(ex, new SshFailureFacts(SshPhase.Cleanup, true));
                    return ExecuteFailure(
                        host,
                        SshPhase.Cleanup,
                        failure.ErrorCode,
                        SshDiagnosticText.FromException(ex),
                        retrySafe: false,
                        stopwatch,
                        SshExecutionState.Unknown);
                }
            }
            else
            {
                var reason = lifetime.StopReason;
                return ExecuteFailure(
                    host,
                    SshPhase.Cleanup,
                    StopCode(reason),
                    StopDiagnostic(reason, "清理宽限内会话未返回；远端进程状态未知，连接释放证据见 cleanup_state"),
                    retrySafe: false,
                    stopwatch,
                    SshExecutionState.Unknown);
            }
        }

        // 本地终止事实（取消/截止）一旦确立，**迟到的 Completed 不得覆盖它**；
        // 远端退出证据（execution_state/exit_code/exit_signal/输出）继续保留，两类事实分开表达（L3）。
        if (lifetime.IsStopped && result.Status == SshExecutionStatus.Completed)
        {
            result = RewriteAsLocalTermination(result, lifetime.StopReason);
        }
        else if (result.Status == SshExecutionStatus.Cancelled
            && lifetime.StopReason is SshStopReason.OperationDeadline or SshStopReason.ConnectDeadline)
        {
            // 会话把「本端到期」报成 Cancelled（它只看到外层 token 被取消）：按终止原因改写为 timeout。
            result = RewriteAsLocalTermination(result, lifetime.StopReason);
        }

        return result with { DurationMs = Elapsed(stopwatch) };
    }

    /// <summary>开始记账：创建资源租约并计入活跃数。</summary>
    private SshResourceLease BeginLease(Admission admission)
    {
        var lease = new SshResourceLease(_quarantine, admission.Release);
        Interlocked.Increment(ref _activeLeases);
        return lease;
    }

    /// <summary>
    /// 结束**业务**记账（L1）：业务返回只结束业务计数。仍挂起的 exec/reader/关闭工作由租约继续占账，
    /// 计入 <see cref="PendingResourceLeases"/>，并在资源真正结束时归还（此时才释放准入槽与身份材料）。
    /// </summary>
    private void EndLease(SshResourceLease lease)
    {
        lease.EndBusiness();
        Interlocked.Decrement(ref _activeLeases);

        if (!lease.ResourcesCompleted.IsCompleted)
        {
            Interlocked.Increment(ref _pendingLateResources);
            _ = lease.ResourcesCompleted.ContinueWith(
                static (_, state) => Interlocked.Decrement(ref ((SshOperationCoordinator)state!)._pendingLateResources),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>迟到 owner 的收尾：在共享清理预算内关闭迟到的连接；身份由租约在所有资源结束后统一释放。</summary>
    private async Task CloseLateResourcesAsync(SshResourceLease lease, StageHandoff handoff)
    {
        try
        {
            await CloseSessionOnceAsync(lease, _options.CleanupGrace).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 迟到收尾不得抛出未观察异常。
        }
        finally
        {
            handoff.SettleFromLateOwner();
        }
    }

    /// <summary>
    /// 关闭会话的**单次状态机**（L3）：所有调用者共享同一个关闭 Task；
    /// 关闭工作走**独立的**有界池，不会被卡死的 connect/exec 饿死。
    /// <c>CleanupState=Completed</c> 需要关闭证据（会话自报 Closed）而不是「调用过 Abort」。
    /// </summary>
    private Task<SshCleanupOutcome> CloseSessionOnceAsync(SshResourceLease lease, TimeSpan budget) =>
        lease.CloseOnceAsync(() => CloseCoreAsync(lease, budget));

    private async Task<SshCleanupOutcome> CloseCoreAsync(SshResourceLease lease, TimeSpan budget)
    {
        var session = lease.Session;
        if (session is null)
        {
            return new SshCleanupOutcome(true, false, false, 0);
        }

        // 非阻塞中止请求：真正的 TERM/关闭由会话自己的状态机执行。
        try
        {
            session.RequestAbort();
        }
        catch (Exception)
        {
            // 中止请求不得把清理路径变成失败路径。
        }

        Task closeWork;
        try
        {
            closeWork = _closeWorkers.StartAsync(() => session.DisposeAsync().AsTask());
        }
        catch (SshStageCapacityExceededException)
        {
            // 关闭容量耗尽：资源保持未关闭，租约继续占账（不得假装已收敛）。
            lease.RegisterWork(Task.Delay(Timeout.InfiniteTimeSpan), "close-capacity");
            return new SshCleanupOutcome(false, false, true, PendingReaders(session));
        }

        lease.RegisterWork(closeWork, "close");
        var completed = await SshBoundedDrain
            .WaitAsync(closeWork, budget, onTimeout: static () => { })
            .ConfigureAwait(false);

        lease.CaptureCloseEvidence(session);

        // 关闭证据：实现诊断接口的会话必须自报 Closed；不实现的按「Dispose 完成」为证据。
        var closed = lease.Closed || session is not ISshTransportSessionDiagnostics;
        return new SshCleanupOutcome(completed && closed, !completed, true, PendingReaders(session));
    }

    private static int PendingReaders(ISshTransportSession session) =>
        session is ISshTransportSessionDiagnostics diagnostics ? diagnostics.PendingReaders : -1;

    /// <summary>
    /// 一次「可能产生迟到工作」的交接（L2/L3）：无论及时路径还是迟到 owner 接手，义务只结算一次；
    /// 义务未结算前租约不会释放准入槽与身份材料（迟到连接读取期间材料保持有效）。
    /// </summary>
    private sealed class StageHandoff
    {
        private const int BusinessOwned = 0;
        private const int AbandonedToLateOwner = 1;
        private const int Settled = 2;

        private readonly SshResourceLease _lease;
        private int _state = BusinessOwned;

        public StageHandoff(SshResourceLease lease)
        {
            _lease = lease;
            _lease.BeginObligation();
        }

        /// <summary>
        /// 放弃等待：把结算权**原子**交给迟到 owner。必须紧跟 <c>WaitStageAsync</c> 返回
        /// <c>Completed == false</c> 之后调用——这是与「停止选择」不可分割的一步（L2 要求）。
        /// </summary>
        public void MarkAbandoned() => Interlocked.CompareExchange(ref _state, AbandonedToLateOwner, BusinessOwned);

        /// <summary>业务路径结算：只有**没有**放弃过才真的结算（放弃时由迟到 owner 负责）。</summary>
        public void SettleFromBusiness()
        {
            if (Interlocked.CompareExchange(ref _state, Settled, BusinessOwned) == BusinessOwned)
            {
                _lease.EndObligation();
            }
        }

        /// <summary>迟到 owner 结算：接管并结束义务（幂等）。</summary>
        public void SettleFromLateOwner()
        {
            if (Interlocked.Exchange(ref _state, Settled) != Settled)
            {
                _lease.EndObligation();
            }
        }
    }

    private static string? AppendDiagnostic(string? existing, string extra) =>
        SshDiagnosticText.BoundedJoin(existing, extra);

    /// <summary>把结果改写为「本地终止事实」：状态/码按停止原因，远端退出证据保留（L3）。</summary>
    private static SshExecuteResult RewriteAsLocalTermination(SshExecuteResult result, SshStopReason reason)
    {
        var code = StopCode(reason);
        return result with
        {
            Status = code == SshErrorCodes.Timeout ? SshExecutionStatus.TimedOut : SshExecutionStatus.Cancelled,
            ErrorCode = code,
            Phase = SshPhase.Cleanup,
            RetrySafe = false,
            Diagnostic = AppendDiagnostic(
                result.Diagnostic,
                StopDiagnostic(reason, "本地终止事实已确立：迟到完成不覆盖状态；远端退出证据另行保留")),
        };
    }

    /// <summary>把宿主绝对 deadline 折算进操作预算；已过期则拒绝（不建连）。</summary>
    private bool ResolveDeadlineBudget(SshHostContext host, DateTimeOffset startedAt, ref TimeSpan timeout, out string? failure)
    {
        failure = null;
        if (host.ExecutionDeadlineUtc is not { } deadline)
        {
            return false;
        }

        var remaining = deadline - startedAt;
        if (remaining <= TimeSpan.Zero)
        {
            failure = "宿主 deadline 已过期，未提交任何命令。";
            return true;
        }

        if (remaining < timeout)
        {
            timeout = remaining;
        }

        return false;
    }

    private static bool TargetsMatch(SshTargetRef frozen, SshTargetRef requested) =>
        string.Equals(frozen.HostId, requested.HostId, StringComparison.Ordinal)
        && string.Equals(frozen.HostRevision, requested.HostRevision, StringComparison.Ordinal)
        && string.Equals(frozen.TrustRevision, requested.TrustRevision, StringComparison.Ordinal)
        && string.Equals(frozen.Host, requested.Host, StringComparison.Ordinal)
        && string.Equals(frozen.Username, requested.Username, StringComparison.Ordinal)
        && frozen.Port == requested.Port;

    private static string GateKey(SshTargetRef target) => target.HostId + "\u001f" + target.HostRevision;

    private async Task<Admission> AdmitAsync(string gateKey, int hostLimit, SshOperationLifetime lifetime)
    {
        var cancellationToken = lifetime.Token;

        // 等待任何门之前的前置围栏（ADR-096 D2）。
        if (lifetime.IsStopped)
        {
            return Admission.Expired();
        }

        var hostGate = _hostGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(Math.Max(1, hostLimit), Math.Max(1, hostLimit)));

        if (_coreGate.Wait(0))
        {
            if (hostGate.Wait(0))
            {
                return AdmitOrRelease(hostGate, lifetime);
            }

            // 已占住 Core 槽位、只差主机槽位：计入等待队列。
            if (Interlocked.Increment(ref _waiters) > _options.MaxWaitQueueLength)
            {
                Interlocked.Decrement(ref _waiters);
                _coreGate.Release();
                return Admission.Busy();
            }

            try
            {
                await hostGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                return AdmitOrRelease(hostGate, lifetime);
            }
            catch (OperationCanceledException)
            {
                _coreGate.Release();
                return Admission.Expired();
            }
            finally
            {
                Interlocked.Decrement(ref _waiters);
            }
        }

        if (Interlocked.Increment(ref _waiters) > _options.MaxWaitQueueLength)
        {
            Interlocked.Decrement(ref _waiters);
            return Admission.Busy();
        }

        try
        {
            await _coreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await hostGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _coreGate.Release();
                throw;
            }

            return AdmitOrRelease(hostGate, lifetime);
        }
        catch (OperationCanceledException)
        {
            return Admission.Expired();
        }
        finally
        {
            Interlocked.Decrement(ref _waiters);
        }
    }

    /// <summary>取得槽位**之后**再检查一次终止：已取消的操作不得带着槽位继续读身份。</summary>
    private Admission AdmitOrRelease(SemaphoreSlim hostGate, SshOperationLifetime lifetime)
    {
        // 取得槽位之后、读身份之前再核对停止与**资源准入**状态（L1.5）：
        // 隔离区满额时，预先排队的请求也不得开启新连接。
        if (lifetime.IsStopped || _quarantine.IsFull || _closeWorkers.IsFull)
        {
            hostGate.Release();
            _coreGate.Release();
            return lifetime.IsStopped ? Admission.Expired() : Admission.Busy();
        }

        return Admission.Admitted(hostGate, _coreGate);
    }

    /// <summary>前置围栏（ssh_test）：已终止时返回失败结果，不读身份、不建连。</summary>
    private bool TryTestFence(
        SshOperationLifetime lifetime,
        SshHostContext host,
        Stopwatch stopwatch,
        out SshTestResult? failure)
    {
        failure = null;
        if (!lifetime.IsStopped)
        {
            return false;
        }

        var reason = lifetime.StopReason;
        failure = TestFailure(host, SshPhase.Validation, StopCode(reason), StopDiagnostic(reason, "受理前置围栏"), stopwatch);
        return true;
    }

    /// <summary>前置围栏（ssh_execute）：已终止时返回该阶段的失败结果（不读身份、不建连、不提交）。</summary>
    private bool TryFence(
        SshOperationLifetime lifetime,
        SshHostContext host,
        Stopwatch stopwatch,
        out SshExecuteResult? failure)
    {
        failure = null;
        if (!lifetime.IsStopped)
        {
            return false;
        }

        var reason = lifetime.StopReason;
        failure = ExecuteFailure(
            host,
            SshPhase.Validation,
            StopCode(reason),
            StopDiagnostic(reason, "受理前置围栏"),
            retrySafe: false,
            stopwatch);
        return true;
    }

    private static string StopCode(SshStopReason reason) => reason switch
    {
        SshStopReason.OperationDeadline or SshStopReason.ConnectDeadline => SshErrorCodes.Timeout,
        SshStopReason.CallerCancelled or SshStopReason.HostStopping => SshErrorCodes.Cancelled,
        _ => SshErrorCodes.Cancelled,
    };

    private static string StopDiagnostic(SshStopReason reason, string detail) =>
        reason.ToDiagnostic() + "：" + detail;

    private static string ResolveAdmissionCode(AdmissionKind kind, SshOperationLifetime lifetime) => kind switch
    {
        AdmissionKind.Busy => SshErrorCodes.Busy,
        _ => StopCode(lifetime.StopReason),
    };

    private static string DescribeAdmission(AdmissionKind kind, SshOperationLifetime lifetime) => kind switch
    {
        AdmissionKind.Busy => "等待队列已满（ssh.busy）：未创建连接，也未提交命令。",
        _ => StopDiagnostic(lifetime.StopReason, "排队期间到期/被取消：未创建连接，也未提交命令。"),
    };

    private async Task<T> TrackAsync<T>(Func<Task<T>> body)
    {
        lock (_inFlightGate)
        {
            if (_inFlight == 0)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _inFlight++;
        }

        try
        {
            return await body().ConfigureAwait(false);
        }
        finally
        {
            lock (_inFlightGate)
            {
                _inFlight--;
                if (_inFlight == 0)
                {
                    _drained?.TrySetResult();
                }
            }
        }
    }

    private Task? DrainedTask()
    {
        lock (_inFlightGate)
        {
            return _inFlight == 0 ? null : _drained?.Task;
        }
    }

    private static SshExecuteResult ExecuteFailure(
        SshHostContext host,
        SshPhase phase,
        string errorCode,
        string? diagnostic,
        bool retrySafe,
        Stopwatch stopwatch,
        SshExecutionState state = SshExecutionState.NotSubmitted,
        SshCleanupState cleanupState = SshCleanupState.NotStarted) => new()
    {
        Status = errorCode switch
        {
            SshErrorCodes.Timeout => SshExecutionStatus.TimedOut,
            SshErrorCodes.Cancelled => SshExecutionStatus.Cancelled,
            _ => SshExecutionStatus.Failed,
        },
        ExecutionState = state,
        HostId = host.Target.HostId,
        HostRevision = host.Target.HostRevision,
        ErrorCode = errorCode,
        Phase = phase,
        Diagnostic = SshDiagnosticText.Bound(diagnostic),
        CleanupState = cleanupState,
        RetrySafe = retrySafe,
        DurationMs = Elapsed(stopwatch),
    };

    private static SshTestResult TestFailure(
        SshHostContext host,
        SshPhase phase,
        string errorCode,
        string? diagnostic,
        Stopwatch stopwatch,
        SshCleanupState cleanupState = SshCleanupState.NotStarted) => new()
    {
        Status = errorCode switch
        {
            SshErrorCodes.Timeout => SshExecutionStatus.TimedOut,
            SshErrorCodes.Cancelled => SshExecutionStatus.Cancelled,
            _ => SshExecutionStatus.Failed,
        },
        HostId = host.Target.HostId,
        HostRevision = host.Target.HostRevision,
        Target = host.Target,
        Phase = phase,
        ErrorCode = errorCode,
        Diagnostic = SshDiagnosticText.Bound(diagnostic),
        CleanupState = cleanupState,
        DurationMs = Elapsed(stopwatch),
    };

    private static int Elapsed(Stopwatch stopwatch) => (int)stopwatch.ElapsedMilliseconds;

    private enum AdmissionKind
    {
        Admitted,
        Busy,
        Expired,
    }

    private sealed class Admission
    {
        private readonly SemaphoreSlim? _hostGate;
        private readonly SemaphoreSlim? _coreGate;
        private int _released;

        private Admission(AdmissionKind kind, SemaphoreSlim? hostGate, SemaphoreSlim? coreGate)
        {
            Kind = kind;
            _hostGate = hostGate;
            _coreGate = coreGate;
        }

        public AdmissionKind Kind { get; }

        public static Admission Admitted(SemaphoreSlim hostGate, SemaphoreSlim coreGate) =>
            new(AdmissionKind.Admitted, hostGate, coreGate);

        public static Admission Busy() => new(AdmissionKind.Busy, null, null);

        public static Admission Expired() => new(AdmissionKind.Expired, null, null);

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            _hostGate?.Release();
            _coreGate?.Release();
        }
    }
}
