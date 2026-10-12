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
    private int _activeLeases;
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
        _quarantine = new SshCleanupQuarantine(_options.MaxQuarantinedResources);
        _stageWorkers = new SshStageWorkerPool(_options.MaxStageWorkers);
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

    /// <summary>阶段 worker 占用数（含已超时但线程尚未归还的）。</summary>
    public int ActiveStageWorkers => _stageWorkers.Active;

    public int QuarantineCapacity => _quarantine.Capacity;

    public int StageWorkerCapacity => _stageWorkers.Capacity;

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
        await _shutdown.CancelAsync().ConfigureAwait(false);

        var drained = DrainedTask();
        var timedOut = false;
        if (drained is not null)
        {
            // 停止等待本身有界：宽限到期就返回并**如实报告**仍有在飞操作/隔离资源。
            var winner = await Task.WhenAny(drained, Task.Delay(_options.CleanupGrace, cancellationToken))
                .ConfigureAwait(false);
            timedOut = winner != drained;
        }

        return new SshStopReport(InFlightOperations, _quarantine.Pending, timedOut);
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
                // 阶段端口在**有界 worker** 上启动：同步启动段可能阻塞线程，不得卡住业务调用线程（ADR-096 D3）。
                var identityTask = _stageWorkers.Start(() => _identities.GetIdentityAsync(host.Identity, lifetime.Token).AsTask());
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

                SshStageOutcome<ISshTransportSession> connectOutcome;
                try
                {
                    connectOutcome = await lifetime
                        .WaitStageAsync(
                            _stageWorkers.Start(() => _transport.ConnectAsync(connectRequest, lifetime.Token)),
                            connectBudget,
                            SshStopReason.ConnectDeadline,
                            onLateSuccess: late =>
                            {
                                // 迟到连接只能被回收：业务路径不再释放身份，由迟到 owner 在关闭连接后清零。
                                lease.TransferLateOwnership();
                                lease.AttachSession(late);
                                _ = CloseLateResourcesAsync(lease);
                            },
                            onLateFailure: error =>
                            {
                                _ = error;
                                lease.TransferLateOwnership();
                                _ = CloseLateResourcesAsync(lease);
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
                    return TestFailure(
                        host,
                        SshPhase.Connecting,
                        StopCode(connectOutcome.Reason),
                        StopDiagnostic(connectOutcome.Reason, "建连阶段"),
                        stopwatch);
                }

                session = connectOutcome.Result!;
                lease.AttachSession(session);
                if (TryTestFence(lifetime, host, stopwatch, out testFenced))
                {
                    var fencedCleanup = await lease.CloseSessionAsync(lifetime.RemainingCleanup(_options.CleanupGrace)).ConfigureAwait(false);
                    return testFenced! with
                    {
                        CleanupState = fencedCleanup.Completed ? SshCleanupState.Completed : SshCleanupState.Pending,
                    };
                }

                phaseDurations["connect"] = Elapsed(stopwatch) - phaseDurations["queue"] - phaseDurations["credentials"];

                // 有界关闭：同一条连接在预算内没结束就进隔离区（不得声称已释放）。
                var cleanup = await lease.CloseSessionAsync(lifetime.RemainingCleanup(_options.CleanupGrace)).ConfigureAwait(false);
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
                    _ = await lease.CloseSessionAsync(lifetime.RemainingCleanup(_options.CleanupGrace)).ConfigureAwait(false);
                }

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

        // 执行串在**审批前**即可确定：审批看到的就是将要提交的精确字符串。
        SshPosixExecutionPlan plan;
        try
        {
            plan = SshPosixCommand.Compose(request.Command, request.Cwd, host.DefaultCwd);
        }
        catch (SshComponentException ex)
        {
            return ExecuteFailure(host, SshPhase.Validation, ex.ErrorCode, ex.Message, retrySafe: false, stopwatch);
        }

        var timeout = host.Limits.ResolveOperationTimeout(request.TimeoutSeconds);
        var outputBudget = host.Limits.ResolveOutputBudget(request.MaxOutputBytes);

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
        try
        {
            if (TryFence(lifetime, host, stopwatch, out fenced))
            {
                return fenced!;
            }

            SshStageOutcome<SshIdentityMaterial> identityOutcome;
            try
            {
                // 阶段端口在**有界 worker** 上启动：同步启动段可能阻塞线程，不得卡住业务调用线程（ADR-096 D3）。
                var identityTask = _stageWorkers.Start(() => _identities.GetIdentityAsync(host.Identity, lifetime.Token).AsTask());
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

                SshStageOutcome<ISshTransportSession> connectOutcome;
                try
                {
                    connectOutcome = await lifetime
                        .WaitStageAsync(
                            _stageWorkers.Start(() => _transport.ConnectAsync(connectRequest, lifetime.Token)),
                            connectBudget,
                            SshStopReason.ConnectDeadline,
                            onLateSuccess: late =>
                            {
                                // 迟到连接只能被回收：业务路径不再释放身份，由迟到 owner 在关闭连接后清零。
                                lease.TransferLateOwnership();
                                lease.AttachSession(late);
                                _ = CloseLateResourcesAsync(lease);
                            },
                            onLateFailure: error =>
                            {
                                _ = error;
                                lease.TransferLateOwnership();
                                _ = CloseLateResourcesAsync(lease);
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
                    return ExecuteFailure(
                        host,
                        SshPhase.Connecting,
                        StopCode(connectOutcome.Reason),
                        StopDiagnostic(connectOutcome.Reason, "建连阶段"),
                        retrySafe: false,
                        stopwatch);
                }

                session = connectOutcome.Result!;
                lease.AttachSession(session);

                // 提交前围栏：会话已到手但已终止时不得发 exec。
                if (TryFence(lifetime, host, stopwatch, out fenced))
                {
                    var fencedCleanup = await lease.CloseSessionAsync(lifetime.RemainingCleanup(_options.CleanupGrace)).ConfigureAwait(false);
                    return fenced! with
                    {
                        CleanupState = fencedCleanup.Completed ? SshCleanupState.Completed : SshCleanupState.Pending,
                    };
                }

                var result = await ExecuteOnSessionAsync(host, session, plan, lifetime, outputBudget, stopwatch)
                    .ConfigureAwait(false);

                // 有界关闭（与 TERM/排空共享同一份预算）；没结束就进隔离区并如实标注。
                var cleanup = await lease.CloseSessionAsync(lifetime.RemainingCleanup(_options.CleanupGrace)).ConfigureAwait(false);
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
                    _ = await lease.CloseSessionAsync(lifetime.RemainingCleanup(_options.CleanupGrace)).ConfigureAwait(false);
                }

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

            executeTask = session.ExecuteAsync(plan.ExecutedCommand, remaining, outputBudget, lifetime.Token);
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
                // 会话在提交后抛出：可能已执行，一律按「状态未知」处理（设计 §7.3/§9.2）。
                var failure = SshFailureClassifier.Classify(ex, new SshFailureFacts(SshPhase.Collecting, true));
                return ExecuteFailure(
                    host,
                    SshPhase.Collecting,
                    failure.ErrorCode,
                    SshDiagnosticText.FromException(ex),
                    retrySafe: false,
                    stopwatch,
                    SshExecutionState.Unknown);
            }
        }
        else
        {
            // 已到期/被取消：只再给一次**资源回收**宽限；超时就不再等会话（不延长业务许可）。
            var grace = await Task.WhenAny(executeTask, Task.Delay(_options.CleanupGrace, CancellationToken.None))
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
                    StopDiagnostic(reason, "清理宽限内会话未返回；已释放独占连接，远端进程状态未知"),
                    retrySafe: false,
                    stopwatch,
                    SshExecutionState.Unknown);
            }
        }

        // 会话可能把「本端到期」报成 Cancelled（它只看到外层 token 被取消）：这里按终止原因改写为 timeout。
        if (result.Status == SshExecutionStatus.Cancelled
            && lifetime.StopReason is SshStopReason.OperationDeadline or SshStopReason.ConnectDeadline)
        {
            result = result with
            {
                Status = SshExecutionStatus.TimedOut,
                ErrorCode = SshErrorCodes.Timeout,
                Diagnostic = StopDiagnostic(lifetime.StopReason, "操作总时长/宿主 deadline 到期（远端已收到 TERM；是否退出需以 execution_state 为准）"),
            };
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

    /// <summary>结束记账：释放业务资源（身份 + 准入槽位）并减少活跃数。</summary>
    private void EndLease(SshResourceLease lease)
    {
        lease.ReleaseBusinessResources();
        Interlocked.Decrement(ref _activeLeases);
    }

    /// <summary>迟到 owner 的收尾：在有界预算内关闭迟到的连接，并清零仍在手边的身份材料。</summary>
    private async Task CloseLateResourcesAsync(SshResourceLease lease)
    {
        try
        {
            await lease.CloseSessionAsync(_options.CleanupGrace).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 迟到收尾不得抛出未观察异常。
        }
        finally
        {
            lease.ReleaseIdentity();
            lease.ReleaseAdmission();
        }
    }

    private static string? AppendDiagnostic(string? existing, string extra) =>
        string.IsNullOrEmpty(existing) ? extra : existing + "｜" + extra;

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
        if (!lifetime.IsStopped)
        {
            return Admission.Admitted(hostGate, _coreGate);
        }

        hostGate.Release();
        _coreGate.Release();
        return Admission.Expired();
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
