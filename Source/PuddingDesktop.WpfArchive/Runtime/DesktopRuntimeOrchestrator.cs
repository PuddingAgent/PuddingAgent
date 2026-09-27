using PuddingDesktop.Core;

namespace PuddingDesktop.Runtime;

/// <summary>
/// Applies restart policy around the single-process Core supervisor.
/// The injected supervisor retains ownership of the process and its disposal.
/// </summary>
public sealed class DesktopRuntimeOrchestrator : IDesktopRuntimeOrchestrator
{
    private readonly ICoreProcessSupervisor _supervisor;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _recoveryGate = new();

    private CoreProcessStartOptions? _options;
    private CoreRestartPolicy _policy = new();
    private CoreRestartAttemptWindow _attemptWindow = new(3, TimeSpan.FromSeconds(60));
    private DesktopRuntimeSnapshot _snapshot = new();
    private CancellationTokenSource? _recoveryCts;
    private Task? _recoveryTask;
    private CoreProcessExitedEventArgs? _pendingUnexpectedExit;
    private bool _userStopRequested;
    private int _disposeState;

    public DesktopRuntimeOrchestrator(
        ICoreProcessSupervisor supervisor,
        TimeProvider? timeProvider = null)
    {
        _supervisor = supervisor;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _supervisor.UnexpectedExit += OnSupervisorUnexpectedExit;
    }

    public DesktopRuntimeSnapshot Snapshot => _snapshot;

    public event EventHandler<DesktopRuntimeChangedEventArgs>? Changed;

    public void Configure(CoreProcessStartOptions options, CoreRestartPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        ThrowIfDisposed();

        _options = options;
        _policy = policy.Validate();
        _attemptWindow = new CoreRestartAttemptWindow(
            _policy.MaxAttempts,
            TimeSpan.FromSeconds(_policy.WindowSeconds));
        Publish(_snapshot with { AutoRestartEnabled = _policy.Enabled });
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        CancelRecovery();
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _userStopRequested = false;
            _attemptWindow.Reset();
            await StartUnderLockAsync(cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _userStopRequested = true;
        CancelRecovery();

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            Publish(_snapshot with
            {
                State = DesktopRuntimeState.Stopping,
                UserStopRequested = true,
                NextRestartAt = null,
            });
            await _supervisor.StopAsync(cancellationToken);
            Publish(_snapshot with
            {
                State = DesktopRuntimeState.Stopped,
                Session = _supervisor.CurrentSession,
                UserStopRequested = true,
                NextRestartAt = null,
                LastError = null,
            });
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _userStopRequested = true;
        CancelRecovery();

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            Publish(_snapshot with
            {
                State = DesktopRuntimeState.Stopping,
                UserStopRequested = true,
                NextRestartAt = null,
            });
            await _supervisor.StopAsync(cancellationToken);

            _userStopRequested = false;
            _attemptWindow.Reset();
            await StartUnderLockAsync(cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task SetAutoRestartAsync(bool enabled, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!enabled)
            CancelRecovery();

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _policy = _policy with { Enabled = enabled };
            Publish(_snapshot with
            {
                AutoRestartEnabled = enabled,
                NextRestartAt = enabled ? _snapshot.NextRestartAt : null,
            });
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task StartUnderLockAsync(CancellationToken cancellationToken)
    {
        var options = _options
            ?? throw new InvalidOperationException("Core 启动参数尚未配置。");

        Publish(_snapshot with
        {
            State = DesktopRuntimeState.Starting,
            UserStopRequested = false,
            NextRestartAt = null,
            LastError = null,
        });

        try
        {
            var session = await _supervisor.StartAsync(options, cancellationToken);
            Publish(_snapshot with
            {
                State = DesktopRuntimeState.Ready,
                Session = session,
                LastProcessId = session.ProcessId,
                UserStopRequested = false,
                NextRestartAt = null,
                LastError = null,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(_snapshot with
            {
                State = DesktopRuntimeState.Stopped,
                Session = _supervisor.CurrentSession,
                NextRestartAt = null,
            });
            throw;
        }
        catch (Exception ex)
        {
            Publish(_snapshot with
            {
                State = DesktopRuntimeState.Failed,
                Session = _supervisor.CurrentSession,
                NextRestartAt = null,
                LastError = ex.Message,
            });
            throw;
        }
    }

    private void OnSupervisorUnexpectedExit(object? sender, CoreProcessExitedEventArgs e)
    {
        if (Volatile.Read(ref _disposeState) != 0 || _userStopRequested)
            return;

        lock (_recoveryGate)
        {
            if (_recoveryTask is { IsCompleted: false })
            {
                _pendingUnexpectedExit = e;
                return;
            }

            _recoveryCts = new CancellationTokenSource();
            _recoveryTask = RecoverAsync(e, _recoveryCts.Token);
        }
    }

    private async Task RecoverAsync(
        CoreProcessExitedEventArgs initialExit,
        CancellationToken cancellationToken)
    {
        CoreProcessExitedEventArgs? exit = initialExit;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = _timeProvider.GetUtcNow();
                await _operationLock.WaitAsync(cancellationToken);
                TimeSpan delay;
                try
                {
                    if (_userStopRequested || !_policy.Enabled || _options is null)
                    {
                        PublishExitFailure(exit, autoRestartEnabled: _policy.Enabled);
                        return;
                    }

                    if (!_attemptWindow.TryRegister(now, out var attemptNumber))
                    {
                        Publish(_snapshot with
                        {
                            State = DesktopRuntimeState.CircuitOpen,
                            Session = _supervisor.CurrentSession,
                            LastProcessId = exit.ProcessId,
                            LastExitCode = exit.ExitCode,
                            LastExitAt = now,
                            RestartAttemptsInWindow = _attemptWindow.Count(now),
                            NextRestartAt = null,
                            LastError = $"Core 在 {_policy.WindowSeconds} 秒内连续失败，自动恢复已熔断。",
                        });
                        return;
                    }

                    delay = _policy.GetDelay(attemptNumber);
                    Publish(_snapshot with
                    {
                        State = DesktopRuntimeState.RestartScheduled,
                        Session = _supervisor.CurrentSession,
                        LastProcessId = exit.ProcessId,
                        LastExitCode = exit.ExitCode,
                        LastExitAt = now,
                        RestartAttemptsInWindow = attemptNumber,
                        NextRestartAt = now + delay,
                        LastError = $"Core 意外退出，将在 {delay.TotalSeconds:0} 秒后尝试自动恢复。",
                    });
                }
                finally
                {
                    _operationLock.Release();
                }

                await Task.Delay(delay, _timeProvider, cancellationToken);

                await _operationLock.WaitAsync(cancellationToken);
                try
                {
                    await StartUnderLockAsync(cancellationToken);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    var failedSession = _supervisor.CurrentSession;
                    exit = new CoreProcessExitedEventArgs(
                        failedSession?.ProcessId ?? exit.ProcessId,
                        failedSession?.ExitCode ?? -1,
                        expected: false);
                    Publish(_snapshot with
                    {
                        State = DesktopRuntimeState.Failed,
                        Session = failedSession,
                        LastError = $"Core 自动恢复失败：{ex.Message}",
                    });
                }
                finally
                {
                    _operationLock.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            CoreProcessExitedEventArgs? pending;
            lock (_recoveryGate)
            {
                _recoveryCts?.Dispose();
                _recoveryCts = null;
                _recoveryTask = null;
                pending = _pendingUnexpectedExit;
                _pendingUnexpectedExit = null;
            }

            if (pending is not null && !_userStopRequested)
                OnSupervisorUnexpectedExit(this, pending);
        }
    }

    private void PublishExitFailure(CoreProcessExitedEventArgs exit, bool autoRestartEnabled)
    {
        var now = _timeProvider.GetUtcNow();
        Publish(_snapshot with
        {
            State = DesktopRuntimeState.Failed,
            Session = _supervisor.CurrentSession,
            LastProcessId = exit.ProcessId,
            LastExitCode = exit.ExitCode,
            LastExitAt = now,
            AutoRestartEnabled = autoRestartEnabled,
            UserStopRequested = _userStopRequested,
            NextRestartAt = null,
            LastError = $"Core 进程意外退出 (PID: {exit.ProcessId}, exit code: {exit.ExitCode})。",
        });
    }

    private void Publish(DesktopRuntimeSnapshot next)
    {
        var previous = _snapshot;
        _snapshot = next;
        Changed?.Invoke(this, new DesktopRuntimeChangedEventArgs(previous, next));
    }

    private void CancelRecovery()
    {
        lock (_recoveryGate)
            _recoveryCts?.Cancel();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _userStopRequested = true;
        _supervisor.UnexpectedExit -= OnSupervisorUnexpectedExit;
        CancelRecovery();

        Task? recoveryTask;
        lock (_recoveryGate)
            recoveryTask = _recoveryTask;
        if (recoveryTask is not null)
        {
            try { await recoveryTask; }
            catch { }
        }

        _operationLock.Dispose();
    }
}
