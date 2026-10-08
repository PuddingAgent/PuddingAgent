using System.Diagnostics;
using System.Text;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace PuddingSsh.Transport;

/// <summary>
/// 一次操作独占的连接会话（设计 §1.2 第 2 条）：Exec + 双流有界采集 + 终止 + 释放。
/// <para>
/// 终止语义（设计 §9.2）：取消/超时对当前 exec channel 发送 **TERM**，随后关闭 channel 与独占连接；
/// 不发送远程 <c>killall/pkill</c>、不自动强杀整机进程。远端没有给出退出码/信号证据时
/// <see cref="SshExecutionState"/> 必须是 <c>unknown</c>，不得声称远端进程已消失。
/// </para>
/// </summary>
internal sealed class SshNetTransportSession : ISshTransportSession
{
    private readonly SshClient _client;
    private readonly SshTargetRef _target;
    private readonly SshIdentityMaterial _identity;
    private readonly PrivateKeyFile _privateKey;
    private readonly Stream _keyStream;
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

        SshCommand? sshCommand = null;
        try
        {
            sshCommand = _client.CreateCommand(command, Encoding.UTF8);
            sshCommand.CommandTimeout = timeout;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);

            // ExecuteAsync 只负责「提交并等待」；输出必须同时从两条流排空（设计 §9.1）。
            Task executeTask = sshCommand.ExecuteAsync(linked.Token);
            submitted = true;
            state = SshExecutionState.Submitted;
            phase = SshPhase.Executing;

            Task stdoutDrain = collector.DrainStdoutAsync(sshCommand.OutputStream);
            Task stderrDrain = collector.DrainStderrAsync(sshCommand.ExtendedOutputStream);

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
                diagnostic = ex.GetType().Name + ": " + ex.Message;
            }

            phase = SshPhase.Collecting;
            await DrainWithGraceAsync(stdoutDrain, stderrDrain).ConfigureAwait(false);

            // 退出证据：即使被取消，服务端也可能已经回报信号（SIGTERM ⇒ ExitSignal="TERM"）。
            exitCode = ToExitCode(sshCommand.ExitStatus);
            exitSignal = sshCommand.ExitSignal;
            if (exitCode is not null || exitSignal is not null)
            {
                state = SshExecutionState.Exited;
            }
            else if (submitted)
            {
                state = SshExecutionState.Unknown;
            }

            if (status == SshExecutionStatus.Completed)
            {
                if (exitCode is null && exitSignal is null)
                {
                    // 已结束但**没有**退出状态或信号证据：不得伪造 0。
                    status = SshExecutionStatus.Failed;
                    errorCode = SshErrorCodes.ExitStatusMissing;
                    diagnostic = "服务端未返回 exit-status；远端退出状态未知。";
                }
                else if (exitCode is not null and not 0)
                {
                    status = SshExecutionStatus.Failed;
                    errorCode = SshErrorCodes.RemoteExitNonzero;
                }
            }

            if (status == SshExecutionStatus.Completed)
            {
                phase = SshPhase.Completed;
            }
        }
        catch (Exception ex)
        {
            status = SshExecutionStatus.Failed;
            errorCode = SshFailureClassifier.Classify(ex, submitted);
            diagnostic = ex.GetType().Name + ": " + ex.Message;
            if (submitted)
            {
                state = SshExecutionState.Unknown;
            }
        }
        finally
        {
            phase = phase == SshPhase.Completed ? phase : SshPhase.Cleanup;
            sshCommand?.Dispose();

            // 取消/超时按清理宽限关闭独占连接；正常结束由调用方释放。
            if (status is SshExecutionStatus.Cancelled or SshExecutionStatus.TimedOut && _client.IsConnected)
            {
                try
                {
                    _client.Disconnect();
                }
                catch (Exception)
                {
                    // 释放路径不得抛出新错误覆盖业务结果。
                }
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
            RetrySafe = status == SshExecutionStatus.Failed && !submitted,
            ErrorCode = errorCode,
            Phase = phase,
            Diagnostic = diagnostic,
            DurationMs = (int)stopwatch.ElapsedMilliseconds,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await Task.Run(() =>
        {
            try
            {
                if (_client.IsConnected)
                {
                    _client.Disconnect();
                }
            }
            catch (Exception)
            {
                // 释放路径静默：连接可能已被远端关闭。
            }
            finally
            {
                _client.Dispose();
                _privateKey.Dispose();
                _keyStream.Dispose();

                // 认证材料只在内存中交给第三方库，连接释放时清零（设计 §6.2）。
                _identity.Dispose();
            }
        }).ConfigureAwait(false);
    }

    private static async Task DrainWithGraceAsync(Task stdoutDrain, Task stderrDrain)
    {
        var drains = Task.WhenAll(stdoutDrain, stderrDrain);
        await Task.WhenAny(drains, Task.Delay(SshOperationLimits.CleanupGrace, CancellationToken.None))
            .ConfigureAwait(false);

        try
        {
            // 排空任务自身不抛（异常被视为「流结束」），这里再隔离一次，避免清理路径污染执行事实。
            await drains.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 忽略：执行事实由主任务与退出证据决定。
        }
    }

    /// <summary>库的退出码是 <c>int?</c>（未收到 exit-status 时为 <see langword="null"/>）；这里保持可空语义。</summary>
    private static int? ToExitCode(int? exitStatus) => exitStatus;
}
