using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace PuddingDesktop.Core;

/// <summary>
/// Owns exactly one Pudding Core child process and its stdout control protocol.
/// </summary>
public sealed class CoreProcessSupervisor : ICoreProcessSupervisor
{
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CoreProcessLogBuffer _logBuffer = new(500);
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    private Process? _process;
    private CancellationTokenSource? _ioCts;
    private Task? _stdoutReadTask;
    private Task? _stderrReadTask;
    private CoreProcessState _state = CoreProcessState.Idle;
    private CoreProcessSession? _currentSession;
    private CoreProcessStartOptions? _lastOptions;
    private bool _stopRequested;
    private int _disposeState;

    public CoreProcessState State
    {
        get => _state;
        private set
        {
            if (_state == value)
                return;

            var previous = _state;
            _state = value;
            StateChanged?.Invoke(
                this,
                new CoreProcessStateChangedEventArgs(previous, value, _currentSession));
        }
    }

    public CoreProcessSession? CurrentSession => _currentSession;
    public CoreProcessLogBuffer LogBuffer => _logBuffer;

    public event EventHandler<CoreProcessStateChangedEventArgs>? StateChanged;
    public event EventHandler<CoreProcessExitedEventArgs>? UnexpectedExit;

    public async Task<CoreProcessSession> StartAsync(
        CoreProcessStartOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_state is CoreProcessState.Starting or CoreProcessState.Ready or CoreProcessState.Stopping)
                throw new InvalidOperationException("Core is already running or changing state.");

            await CleanupProcessResourcesAsync(_process);

            _lastOptions = options;
            _currentSession = null;
            _stopRequested = false;
            State = CoreProcessState.Starting;

            var process = CreateProcess(options);
            var readyTcs = new TaskCompletionSource<CoreReadyMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) => OnProcessExited(process, readyTcs);
            _process = process;

            var startedAt = DateTimeOffset.UtcNow;
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("The operating system refused to start Core.");

                _ioCts = new CancellationTokenSource();
                _stdoutReadTask = ReadStdoutAsync(process, readyTcs, _ioCts.Token);
                _stderrReadTask = ReadStderrAsync(process, _ioCts.Token);

                CoreReadyMessage readyMessage;
                try
                {
                    readyMessage = await readyTcs.Task.WaitAsync(options.StartupTimeout, cancellationToken);
                }
                catch (TimeoutException ex)
                {
                    throw new TimeoutException(
                        $"Core did not emit a Ready signal within {options.StartupTimeout.TotalSeconds:0.###} seconds.",
                        ex);
                }

                if (readyMessage.ProtocolVersion != 1)
                    throw new InvalidOperationException(
                        $"Unsupported Core desktop protocol version {readyMessage.ProtocolVersion}.");

                if (readyMessage.ProcessId != process.Id)
                    throw new InvalidOperationException(
                        $"Core Ready processId {readyMessage.ProcessId} did not match child process {process.Id}.");

                if (HasExited(process))
                    throw CreateExitedBeforeReadyException(process);

                _logBuffer.Append($"[Desktop] Core Ready signal received: {readyMessage.BaseAddress}");
                var startupDeadline = startedAt + options.StartupTimeout;
                if (!await WaitForHealthAsync(readyMessage.BaseAddress, startupDeadline, cancellationToken))
                    throw new InvalidOperationException(
                        "Core emitted Ready but /health/ready did not become healthy before the startup deadline.");

                if (HasExited(process))
                    throw CreateExitedBeforeReadyException(process);

                _currentSession = new CoreProcessSession
                {
                    ProcessId = process.Id,
                    BaseAddress = readyMessage.BaseAddress,
                    StartedAt = startedAt,
                    ReadyAt = DateTimeOffset.UtcNow,
                };

                _logBuffer.Append($"[Desktop] Core healthy: {readyMessage.BaseAddress}");
                State = CoreProcessState.Ready;
                return _currentSession;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logBuffer.Append("[Desktop] Core startup cancelled.");
                await TerminateAndCleanupAsync(process);
                State = CoreProcessState.Stopped;
                throw;
            }
            catch (Exception ex)
            {
                _logBuffer.Append($"[Desktop] Core startup failed: {ex.Message}");
                await TerminateAndCleanupAsync(process);
                State = CoreProcessState.Failed;
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await StopUnderLockAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private Process CreateProcess(CoreProcessStartOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--desktop-child");
        startInfo.ArgumentList.Add("--desktop-parent-pid");
        startInfo.ArgumentList.Add(options.ParentProcessId.ToString());
        startInfo.ArgumentList.Add("--data-root");
        startInfo.ArgumentList.Add(options.DataRoot);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add($"http://127.0.0.1:{options.Port}");

        _logBuffer.Append(
            $"[Desktop] Starting Core: {options.ExecutablePath} {string.Join(" ", startInfo.ArgumentList)}");

        return new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }

    private async Task StopUnderLockAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null)
        {
            if (_state is not CoreProcessState.Idle and not CoreProcessState.Stopped)
                State = CoreProcessState.Stopped;
            return;
        }

        _stopRequested = true;
        if (HasExited(process))
        {
            CompleteStoppedSession(process);
            await CleanupProcessResourcesAsync(process);
            State = CoreProcessState.Stopped;
            return;
        }

        State = CoreProcessState.Stopping;
        var shutdownTimeout = _lastOptions?.ShutdownTimeout ?? TimeSpan.FromSeconds(15);

        if (_currentSession is not null
            && _lastOptions is not null
            && !string.IsNullOrWhiteSpace(_lastOptions.ControlToken))
        {
            try
            {
                var accepted = await TryGracefulShutdownAsync(
                    _currentSession.BaseAddress,
                    _lastOptions.ControlToken,
                    cancellationToken);
                _logBuffer.Append(accepted
                    ? "[Desktop] Graceful shutdown accepted by Core."
                    : "[Desktop] Core rejected the graceful shutdown request.");
            }
            catch (Exception ex)
            {
                _logBuffer.Append($"[Desktop] Graceful shutdown request failed: {ex.Message}");
            }
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(shutdownTimeout, CancellationToken.None);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logBuffer.Append("[Desktop] Graceful shutdown did not complete; terminating Core.");
            await KillProcessAsync(process);
        }

        CompleteStoppedSession(process);
        await CleanupProcessResourcesAsync(process);
        State = CoreProcessState.Stopped;
    }

    private void OnProcessExited(
        Process process,
        TaskCompletionSource<CoreReadyMessage> readyTcs)
    {
        var exitCode = TryGetExitCode(process);
        _logBuffer.Append($"[Desktop] Core process {TryGetProcessId(process)} exited. Code: {exitCode}");
        readyTcs.TrySetException(CreateExitedBeforeReadyException(process));

        if (!ReferenceEquals(process, _process) || _stopRequested)
            return;

        CompleteStoppedSession(process);
        if (_state is CoreProcessState.Starting or CoreProcessState.Ready)
        {
            State = CoreProcessState.Failed;
            UnexpectedExit?.Invoke(
                this,
                new CoreProcessExitedEventArgs(TryGetProcessId(process), exitCode, expected: false));
        }
    }

    private async Task<bool> WaitForHealthAsync(
        Uri baseAddress,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var healthUrl = new Uri(baseAddress, "/health/ready");
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestCts.CancelAfter(remaining < TimeSpan.FromSeconds(2)
                    ? remaining
                    : TimeSpan.FromSeconds(2));
                using var response = await _httpClient.GetAsync(healthUrl, requestCts.Token);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A single probe timed out; retry until the overall startup deadline.
            }
            catch (HttpRequestException)
            {
                // The listener can race the stdout Ready line by a few milliseconds.
            }

            var delay = deadline - DateTimeOffset.UtcNow;
            if (delay <= TimeSpan.Zero)
                break;
            await Task.Delay(delay < HealthPollInterval ? delay : HealthPollInterval, cancellationToken);
        }

        return false;
    }

    private async Task<bool> TryGracefulShutdownAsync(
        Uri baseAddress,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseAddress, "/internal/desktop/shutdown"));
        request.Headers.Add("X-Pudding-Desktop-Token", token);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        return response.IsSuccessStatusCode;
    }

    private async Task TerminateAndCleanupAsync(Process process)
    {
        _stopRequested = true;
        await KillProcessAsync(process);
        await CleanupProcessResourcesAsync(process);
    }

    private static async Task KillProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch
        {
            // The process can exit between HasExited and Kill.
        }
    }

    private async Task CleanupProcessResourcesAsync(Process? process)
    {
        if (process is null)
            return;

        if (ReferenceEquals(process, _process))
        {
            _ioCts?.Cancel();
            var readTasks = new[] { _stdoutReadTask, _stderrReadTask }
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            if (readTasks.Length > 0)
            {
                try { await Task.WhenAll(readTasks); }
                catch { }
            }

            _ioCts?.Dispose();
            _ioCts = null;
            _stdoutReadTask = null;
            _stderrReadTask = null;
            _process = null;
        }

        process.Dispose();
    }

    private async Task ReadStdoutAsync(
        Process process,
        TaskCompletionSource<CoreReadyMessage> readyTcs,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                    return;

                _logBuffer.Append($"[stdout] {line}");
                try
                {
                    var parsed = CoreReadyMessageParser.TryParse(line);
                    if (parsed is not null)
                        readyTcs.TrySetResult(parsed);
                }
                catch (Exception ex)
                {
                    readyTcs.TrySetException(ex);
                    _logBuffer.Append($"[Desktop] Invalid Ready signal: {ex.Message}");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            readyTcs.TrySetException(ex);
            _logBuffer.Append($"[Desktop] stdout reader failed: {ex.Message}");
        }
    }

    private async Task ReadStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                    return;
                _logBuffer.Append($"[stderr] {line}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logBuffer.Append($"[Desktop] stderr reader failed: {ex.Message}");
        }
    }

    private void CompleteStoppedSession(Process process)
    {
        if (_currentSession is null)
            return;

        _currentSession = _currentSession with
        {
            StoppedAt = DateTimeOffset.UtcNow,
            HasExited = true,
            ExitCode = TryGetExitCode(process),
        };
    }

    private static InvalidOperationException CreateExitedBeforeReadyException(Process process) =>
        new($"Core process exited with code {TryGetExitCode(process)} before startup completed.");

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static int TryGetExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    private static int TryGetProcessId(Process process)
    {
        try { return process.Id; }
        catch { return 0; }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        await _lock.WaitAsync();
        try
        {
            await StopUnderLockAsync(CancellationToken.None);
            await CleanupProcessResourcesAsync(_process);
        }
        finally
        {
            _lock.Release();
        }

        _httpClient.Dispose();
        _lock.Dispose();
    }
}
