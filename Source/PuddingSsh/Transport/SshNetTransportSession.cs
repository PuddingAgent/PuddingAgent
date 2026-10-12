using System.Diagnostics;
using System.Text;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace PuddingSsh.Transport;

/// <summary>
/// 一次操作独占的连接会话（设计 §1.2 第 2 条 / ADR-096 D3）：
/// Exec + 双流有界采集 + **单一关闭状态机** + 资源可见性。
/// <para>
/// 终止与清理的合同：
/// <list type="bullet">
///   <item><see cref="RequestAbort"/> 只登记中止请求并在有界 worker 上启动关闭，**不阻塞调用线程**。</item>
///   <item>TERM、关闭命令、关闭连接、dispose 客户端与密钥**共用同一份清理预算**（<see cref="SshOperationLimits.CleanupGrace"/>），
///         每一步只拿剩余额度，不存在「每步各自 2 秒」。</item>
///   <item>排空到期即请求中止并**不再无条件 await** 原任务；迟到完成由 continuation 观察。</item>
///   <item>取消只是发送 TERM：远端是否真的退出仍以退出码/信号证据为准。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class SshNetTransportSession : ISshTransportSession, ISshTransportSessionDiagnostics
{
    private readonly SshClient _client;
    private readonly SshTargetRef _target;
    private readonly SshIdentityMaterial _identity;
    private readonly PrivateKeyFile _privateKey;
    private readonly Stream _keyStream;
    private readonly object _closeGate = new();

    private Task? _closeTask;
    private SshCommand? _currentCommand;
    private int _abortRequested;
    private int _closed;
    private int _pendingReaders;
    private int _submissionEvidence;
    private bool _disposed;

    public SshNetTransportSession(
        SshClient client,
        SshTargetRef target,
        SshIdentityMaterial identity,
        PrivateKeyFile privateKey,
        Stream keyStream,
        string? verifiedHostKeySha256,
        string? hostKeyAlgorithm)
    {
        _client = client;
        _target = target;
        _identity = identity;
        _privateKey = privateKey;
        _keyStream = keyStream;
        VerifiedHostKeySha256 = verifiedHostKeySha256 ?? string.Empty;
        HostKeyAlgorithm = hostKeyAlgorithm ?? string.Empty;
    }

    public string VerifiedHostKeySha256 { get; }

    public string HostKeyAlgorithm { get; }

    public bool IsConnected => !_disposed && _client.IsConnected;

    public SshSubmissionEvidence SubmissionEvidence =>
        Volatile.Read(ref _submissionEvidence) == 1 ? SshSubmissionEvidence.SubmissionAttempted : SshSubmissionEvidence.NotSent;

    public int PendingReaders => Volatile.Read(ref _pendingReaders);

    public bool AbortRequested => Volatile.Read(ref _abortRequested) == 1;

    public bool Closed => Volatile.Read(ref _closed) == 1;

    /// <summary>非阻塞中止：登记请求并把关闭交给 <see cref="EnsureCloseAsync"/> 的单次状态机。</summary>
    public void RequestAbort()
    {
        Volatile.Write(ref _abortRequested, 1);
        _ = EnsureCloseAsync();
    }

    public async Task<SshExecuteResult> ExecuteAsync(
        string command,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(command);

        var stopwatch = Stopwatch.StartNew();
        var collector = new SshBoundedOutputCollector(maxOutputBytes);

        var phase = SshPhase.Submitting;
        var status = SshExecutionStatus.Unknown;
        var state = SshExecutionState.NotSubmitted;
        string? errorCode = null;
        string? diagnostic = null;
        int? exitCode = null;
        string? exitSignal = null;
        var submitted = false;
        var outputComplete = false;
        string? outputIncompleteReason = SshOutputIncompleteReasons.Unknown;

        SshCommand? sshCommand = null;
        try
        {
            sshCommand = _client.CreateCommand(command, Encoding.UTF8);
            sshCommand.CommandTimeout = timeout;
            _currentCommand = sshCommand;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);

            // 提交事实必须在**可能发送 exec 的调用之前**推进（ADR-096 D4）。
            Volatile.Write(ref _submissionEvidence, 1);

            // ExecuteAsync 只负责「提交并等待」；输出必须同时从两条流排空（设计 §9.1）。
            Task executeTask = sshCommand.ExecuteAsync(linked.Token);
            submitted = true;
            state = SshExecutionState.Submitted;
            phase = SshPhase.Executing;

            Task<SshStreamDrainResult> stdoutDrain = TrackDrain(collector.DrainStdoutAsync(sshCommand.OutputStream));
            Task<SshStreamDrainResult> stderrDrain = TrackDrain(collector.DrainStderrAsync(sshCommand.ExtendedOutputStream));

            try
            {
                await executeTask.ConfigureAwait(false);
                status = SshExecutionStatus.Completed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                status = SshExecutionStatus.Cancelled;
                errorCode = SshErrorCodes.Cancelled;
                diagnostic = "调用方取消：已对远端 exec channel 发送 TERM。";
            }
            catch (OperationCanceledException)
            {
                status = SshExecutionStatus.TimedOut;
                errorCode = SshErrorCodes.Timeout;
                diagnostic = "截止时间到期：已对远端 exec channel 发送 TERM。";
            }
            catch (SshOperationTimeoutException ex)
            {
                status = SshExecutionStatus.TimedOut;
                errorCode = SshErrorCodes.Timeout;
                diagnostic = "库命令超时（" + ex.GetType().Name + "）。";
            }
            catch (Exception ex)
            {
                status = SshExecutionStatus.Failed;
                errorCode = SshFailureClassifier.Classify(ex, submitted);
                diagnostic = ex.GetType().Name;
            }

            phase = SshPhase.Collecting;

            // 排空与清理**共用同一份预算**：到期就请求中止，绝不再无条件 await（ADR-096 D3）。
            var drains = Task.WhenAll(stdoutDrain, stderrDrain);
            var drainsCompleted = await SshBoundedDrain
                .WaitAsync(
                    drains,
                    RemainingCleanupBudget(stopwatch, timeout),
                    onTimeout: RequestAbort)
                .ConfigureAwait(false);

            // 排空事实：没在预算内结束 ⇒ 未读到 EOF 且被中止（不得冒充完整输出）。
            outputComplete = drainsCompleted && collector.ReachedEnd;
            outputIncompleteReason = collector.IncompleteReason;
            var outputReadFailed = drainsCompleted && collector.AnyReadFailed;
            var outputAborted = !drainsCompleted || collector.AnyAborted;

            // 退出证据：即使被取消，服务端也可能已经回报信号（SIGTERM ⇒ ExitSignal="TERM"）。
            exitCode = sshCommand.ExitStatus;
            exitSignal = sshCommand.ExitSignal;
            if (exitCode is not null || exitSignal is not null)
            {
                state = SshExecutionState.Exited;
            }
            else if (submitted)
            {
                state = SshExecutionState.Unknown;
            }

            // 退出证据与输出完整性分别表达（ADR-096 D4）：只有「明确退出码 0 + 两条流读完 + 无执行失败」才是 Completed。
            var decision = SshOutcomePolicy.Decide(
                executionFailed: status != SshExecutionStatus.Completed,
                status,
                errorCode,
                exitCode,
                exitSignal,
                outputComplete,
                outputReadFailed,
                outputAborted);

            status = decision.Status;
            errorCode = decision.ErrorCode;

            if (status == SshExecutionStatus.Completed)
            {
                phase = SshPhase.Completed;
            }
            else if (errorCode == SshErrorCodes.OutputIncomplete)
            {
                diagnostic = "exit=" + (exitCode?.ToString() ?? "null") + "；输出未读完（reason=" + (outputIncompleteReason ?? SshOutputIncompleteReasons.Unknown) + "）。";
            }
        }
        catch (Exception ex)
        {
            status = SshExecutionStatus.Failed;
            errorCode = SshFailureClassifier.Classify(ex, submitted);
            diagnostic = ex.GetType().Name;
            if (submitted)
            {
                state = SshExecutionState.Unknown;
            }
        }
        finally
        {
            phase = phase == SshPhase.Completed ? phase : SshPhase.Cleanup;

            // 取消/超时后登记中止（非阻塞）；正常结束由调用方释放会话。
            if (status is SshExecutionStatus.Cancelled or SshExecutionStatus.TimedOut)
            {
                RequestAbort();
            }
        }

        return new SshExecuteResult
        {
            Status = status,
            ExecutionState = state,
            HostId = _target.HostId,
            HostRevision = _target.HostRevision,
            VerifiedHostKeySha256 = VerifiedHostKeySha256,
            ExitCode = exitCode,
            ExitSignal = exitSignal,
            Stdout = collector.DecodeStdout(),
            Stderr = collector.DecodeStderr(),
            CapturedBytes = collector.CapturedBytes,
            ReceivedBytes = collector.ReceivedBytes,
            OutputTruncated = collector.IsTruncated,
            OutputComplete = outputComplete,
            OutputIncompleteReason = outputIncompleteReason,
            RetrySafe = false,
            ErrorCode = errorCode,
            Phase = phase,
            Diagnostic = diagnostic,
            DurationMs = (int)stopwatch.ElapsedMilliseconds,
        };
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        // 关闭状态机自身有预算（TERM/连接/密钥共享一份）；等待它结束是为了让上层能判断
        // 「是否真的释放」——上层仍会用**自己的**预算给这次等待封顶（超时进隔离区）。
        await EnsureCloseAsync().ConfigureAwait(false);
    }

    /// <summary>单一关闭状态机：无论谁先请求（取消/中止/释放），只会执行一次。</summary>
    private Task EnsureCloseAsync()
    {
        lock (_closeGate)
        {
            return _closeTask ??= Task.Run(() => CloseCoreAsync());
        }
    }

    private async Task CloseCoreAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        TimeSpan Remaining()
        {
            var left = SshOperationLimits.CleanupGrace - stopwatch.Elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        var command = _currentCommand;

        // 1) 命令仍在跑：TERM（有界等待服务端应答），再释放命令对象。
        if (command is not null && SubmissionEvidence == SshSubmissionEvidence.SubmissionAttempted)
        {
            try
            {
                var waitMs = (int)Math.Max(1, Remaining().TotalMilliseconds);
                command.CancelAsync(forceKill: false, millisecondsTimeout: waitMs);
            }
            catch (Exception)
            {
                // 服务端不应答/已结束：继续关闭连接。
            }
        }

        if (command is not null)
        {
            try
            {
                command.Dispose();
            }
            catch (Exception)
            {
                // 释放路径静默。
            }
        }

        // 2) 关闭连接（socket/channel），再释放客户端。
        try
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }
        }
        catch (Exception)
        {
            // 连接可能已被远端关闭。
        }

        try
        {
            _client.Dispose();
        }
        catch (Exception)
        {
            // 同上。
        }

        // 3) 释放密钥与认证材料（材料释放是幂等的）。
        try
        {
            _privateKey.Dispose();
        }
        catch (Exception)
        {
            // 同上。
        }

        try
        {
            _keyStream.Dispose();
        }
        catch (Exception)
        {
            // 同上。
        }

        _identity.Dispose();
        Volatile.Write(ref _closed, 1);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>把排空任务登记进「仍在读取」计数，供资源可见性断言使用。</summary>
    private async Task<SshStreamDrainResult> TrackDrain(Task<SshStreamDrainResult> drain)
    {
        Interlocked.Increment(ref _pendingReaders);
        try
        {
            return await drain.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 读取失败由排空事实表达；这里只负责计数与观察。
            return SshStreamDrainResult.ReadFailedResult;
        }
        finally
        {
            Interlocked.Decrement(ref _pendingReaders);
        }
    }

    private static TimeSpan RemainingCleanupBudget(Stopwatch stopwatch, TimeSpan timeout)
    {
        // 清理预算 = 清理宽限，且不超过本次操作剩余时间（两者取小）。
        var left = timeout - stopwatch.Elapsed;
        if (left < TimeSpan.Zero)
        {
            left = TimeSpan.Zero;
        }

        return left < SshOperationLimits.CleanupGrace ? left : SshOperationLimits.CleanupGrace;
    }
}
