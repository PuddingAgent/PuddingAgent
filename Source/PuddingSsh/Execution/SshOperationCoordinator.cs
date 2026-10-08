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
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _stopping = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);

        var drained = DrainedTask();
        if (drained is null)
        {
            return;
        }

        await Task.WhenAny(drained, Task.Delay(_options.CleanupGrace, cancellationToken)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

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

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        operationCts.CancelAfter(timeout);

        var admission = await AdmitAsync(GateKey(host.Target), host.Limits.MaxConcurrentOperations, operationCts.Token)
            .ConfigureAwait(false);
        if (admission.Kind != AdmissionKind.Admitted)
        {
            var code = ResolveAdmissionCode(admission.Kind, cancellationToken);
            return TestFailure(host, SshPhase.Queued, code, DescribeAdmission(admission.Kind), stopwatch);
        }

        phaseDurations["queue"] = Elapsed(stopwatch);
        try
        {
            SshIdentityMaterial identity;
            try
            {
                identity = await _identities.GetIdentityAsync(host.Identity, operationCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = SshFailureClassifier.Classify(ex, new SshFailureFacts(SshPhase.Credentials, false));
                return TestFailure(host, SshPhase.Credentials, failure.ErrorCode, ex.GetType().Name, stopwatch);
            }
            catch (OperationCanceledException)
            {
                return TestFailure(host, SshPhase.Credentials, ResolveOperationCode(cancellationToken), "取凭据阶段被取消/到期。", stopwatch);
            }

            phaseDurations["credentials"] = Elapsed(stopwatch) - phaseDurations["queue"];

            ISshTransportSession? session = null;
            try
            {
                var connectTimeout = host.Limits.ConnectTimeout;
                if (connectTimeout > timeout)
                {
                    connectTimeout = timeout;
                }

                var connectRequest = new SshTransportConnectRequest(
                    request.Target,
                    identity,
                    host.Trust.CreateVerifier(request.Target.Host, request.Target.Port),
                    connectTimeout);

                try
                {
                    session = await _transport.ConnectAsync(connectRequest, operationCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var failure = SshFailureClassifier.Classify(ex, new SshFailureFacts(SshPhase.Connecting, false));
                    var code = ex is OperationCanceledException
                        ? ResolveOperationCode(cancellationToken)
                        : failure.ErrorCode;
                    return TestFailure(
                        host,
                        SshPhase.Connecting,
                        code,
                        ex.GetType().Name,
                        stopwatch);
                }

                phaseDurations["connect"] = Elapsed(stopwatch) - phaseDurations["queue"] - phaseDurations["credentials"];

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
                    DurationMs = Elapsed(stopwatch),
                    PhaseDurationsMs = phaseDurations,
                };
            }
            finally
            {
                identity.Dispose();
                if (session is not null)
                {
                    await DisposeSessionAsync(session).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            admission.Release();
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

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        operationCts.CancelAfter(timeout);

        var admission = await AdmitAsync(GateKey(host.Target), host.Limits.MaxConcurrentOperations, operationCts.Token)
            .ConfigureAwait(false);
        if (admission.Kind != AdmissionKind.Admitted)
        {
            var code = ResolveAdmissionCode(admission.Kind, cancellationToken);
            return ExecuteFailure(host, SshPhase.Queued, code, DescribeAdmission(admission.Kind), code == SshErrorCodes.Busy, stopwatch);
        }

        try
        {
            SshIdentityMaterial identity;
            try
            {
                identity = await _identities.GetIdentityAsync(host.Identity, operationCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = SshFailureClassifier.Classify(ex, new SshFailureFacts(SshPhase.Credentials, false));
                return ExecuteFailure(host, SshPhase.Credentials, failure.ErrorCode, ex.GetType().Name, failure.RetrySafe, stopwatch);
            }
            catch (OperationCanceledException)
            {
                return ExecuteFailure(
                    host,
                    SshPhase.Credentials,
                    ResolveOperationCode(cancellationToken),
                    "取凭据阶段被取消/到期。",
                    retrySafe: false,
                    stopwatch);
            }

            ISshTransportSession? session = null;
            try
            {
                var connectTimeout = host.Limits.ConnectTimeout;
                if (connectTimeout > timeout)
                {
                    connectTimeout = timeout;
                }

                var connectRequest = new SshTransportConnectRequest(
                    request.Target,
                    identity,
                    host.Trust.CreateVerifier(request.Target.Host, request.Target.Port),
                    connectTimeout);

                try
                {
                    session = await _transport.ConnectAsync(connectRequest, operationCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var failure = SshFailureClassifier.Classify(ex, new SshFailureFacts(SshPhase.Connecting, false));
                    var code = ex is OperationCanceledException
                        ? ResolveOperationCode(cancellationToken)
                        : failure.ErrorCode;
                    return ExecuteFailure(
                        host,
                        SshPhase.Connecting,
                        code,
                        ex.GetType().Name,
                        SshFailureClassifier.IsRetrySafe(code, commandSubmitted: false),
                        stopwatch);
                }

                var result = await ExecuteOnSessionAsync(host, session, plan, timeout, outputBudget, operationCts, cancellationToken, stopwatch)
                    .ConfigureAwait(false);
                return result;
            }
            finally
            {
                identity.Dispose();
                if (session is not null)
                {
                    await DisposeSessionAsync(session).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            admission.Release();
        }
    }

    private async Task<SshExecuteResult> ExecuteOnSessionAsync(
        SshHostContext host,
        ISshTransportSession session,
        SshPosixExecutionPlan plan,
        TimeSpan timeout,
        int outputBudget,
        CancellationTokenSource operationCts,
        CancellationToken callerToken,
        Stopwatch stopwatch)
    {
        Task<SshExecuteResult> executeTask;
        try
        {
            executeTask = session.ExecuteAsync(plan.ExecutedCommand, timeout, outputBudget, operationCts.Token);
        }
        catch (Exception ex)
        {
            var failure = SshFailureClassifier.Classify(ex, new SshFailureFacts(SshPhase.Submitting, false));
            return ExecuteFailure(host, SshPhase.Submitting, failure.ErrorCode, ex.GetType().Name, failure.RetrySafe, stopwatch);
        }

        var first = await Task.WhenAny(executeTask, Task.Delay(Timeout.InfiniteTimeSpan, operationCts.Token))
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
                    ex.GetType().Name,
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
                        ex.GetType().Name,
                        retrySafe: false,
                        stopwatch,
                        SshExecutionState.Unknown);
                }
            }
            else
            {
                var cancelled = callerToken.IsCancellationRequested;
                return ExecuteFailure(
                    host,
                    SshPhase.Cleanup,
                    cancelled ? SshErrorCodes.Cancelled : SshErrorCodes.Timeout,
                    "清理宽限内会话未返回；已释放独占连接，远端进程状态未知。",
                    retrySafe: false,
                    stopwatch,
                    SshExecutionState.Unknown);
            }
        }

        // 会话可能把「本端到期」报成 Cancelled（它只看到外层 token 被取消）：这里按事实改写为 timeout。
        if (result.Status == SshExecutionStatus.Cancelled
            && !callerToken.IsCancellationRequested
            && operationCts.IsCancellationRequested)
        {
            result = result with
            {
                Status = SshExecutionStatus.TimedOut,
                ErrorCode = SshErrorCodes.Timeout,
                Diagnostic = "操作总时长/宿主 deadline 到期（远端已收到 TERM；是否退出需以 execution_state 为准）。",
            };
        }

        return result with { DurationMs = Elapsed(stopwatch) };
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

    private async Task<Admission> AdmitAsync(string gateKey, int hostLimit, CancellationToken cancellationToken)
    {
        var hostGate = _hostGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(Math.Max(1, hostLimit), Math.Max(1, hostLimit)));

        if (_coreGate.Wait(0))
        {
            if (hostGate.Wait(0))
            {
                return Admission.Admitted(hostGate, _coreGate);
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
                return Admission.Admitted(hostGate, _coreGate);
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

            return Admission.Admitted(hostGate, _coreGate);
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

    private static string ResolveAdmissionCode(AdmissionKind kind, CancellationToken callerToken) => kind switch
    {
        AdmissionKind.Busy => SshErrorCodes.Busy,
        _ => ResolveOperationCode(callerToken),
    };

    private static string ResolveOperationCode(CancellationToken callerToken) =>
        callerToken.IsCancellationRequested ? SshErrorCodes.Cancelled : SshErrorCodes.Timeout;

    private static string DescribeAdmission(AdmissionKind kind) => kind switch
    {
        AdmissionKind.Busy => "等待队列已满（ssh.busy）：未创建连接，也未提交命令。",
        _ => "排队期间到期/被取消：未创建连接，也未提交命令。",
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

    /// <summary>释放会话本身也要有界：恶意/卡死的 Dispose 不得拖住操作结果。</summary>
    private async Task DisposeSessionAsync(ISshTransportSession session)
    {
        try
        {
            var disposeTask = session.DisposeAsync().AsTask();
            var finished = await Task.WhenAny(disposeTask, Task.Delay(_options.CleanupGrace, CancellationToken.None))
                .ConfigureAwait(false);
            if (finished == disposeTask)
            {
                await disposeTask.ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 释放路径不得抛出新错误覆盖业务结果。
        }
    }

    private static SshExecuteResult ExecuteFailure(
        SshHostContext host,
        SshPhase phase,
        string errorCode,
        string? diagnostic,
        bool retrySafe,
        Stopwatch stopwatch,
        SshExecutionState state = SshExecutionState.NotSubmitted) => new()
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
        Diagnostic = diagnostic,
        RetrySafe = retrySafe,
        DurationMs = Elapsed(stopwatch),
    };

    private static SshTestResult TestFailure(
        SshHostContext host,
        SshPhase phase,
        string errorCode,
        string? diagnostic,
        Stopwatch stopwatch) => new()
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
        Diagnostic = diagnostic,
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
