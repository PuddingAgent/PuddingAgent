using System.Diagnostics;
using System.Net.Http;
using System.Text;
using PuddingDesktop.Core;

namespace PuddingDesktop.Debug;

public enum FrontendDevState
{
    Idle,
    Installing,
    Starting,
    Ready,
    Stopped,
    Failed,
}

public sealed record FrontendDevStartOptions
{
    public required string WorkingDirectory { get; init; }
    public required int Port { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(180);
    public TimeSpan InstallTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Owns the pnpm frontend dev server (`pnpm run start:dev`) for debug mode.
/// Installs node_modules on first use, waits for the dev server to answer
/// /admin/ before reporting Ready, and captures stdout/stderr into a ring
/// buffer for the diagnostics view. Stop kills the whole process tree
/// (cmd.exe → pnpm → node).
/// </summary>
public sealed class FrontendDevSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CoreProcessLogBuffer _logBuffer = new(500);
    private readonly HttpClient _probeClient;

    private Process? _process;
    private CancellationTokenSource? _ioCts;
    private Task? _stdoutReadTask;
    private Task? _stderrReadTask;
    private FrontendDevState _state = FrontendDevState.Idle;
    private int _port;
    private int _disposeState;

    public FrontendDevState State
    {
        get => _state;
        private set
        {
            if (_state == value)
                return;

            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public CoreProcessLogBuffer LogBuffer => _logBuffer;

    public string? LastError { get; private set; }

    public Uri BaseAddress => new($"http://127.0.0.1:{_port}");

    public event EventHandler<FrontendDevState>? StateChanged;

    public FrontendDevSupervisor()
    {
        _probeClient = new HttpClient { Timeout = ProbeTimeout };
    }

    public async Task StartAsync(FrontendDevStartOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options));
        ThrowIfDisposed();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_state is FrontendDevState.Installing or FrontendDevState.Starting or FrontendDevState.Ready)
                throw new InvalidOperationException("Frontend dev server is already running or starting.");

            _port = options.Port;

            if (!Directory.Exists(Path.Combine(options.WorkingDirectory, "node_modules")))
            {
                State = FrontendDevState.Installing;
                _logBuffer.Append("[Debug] node_modules missing; running pnpm install.");
                await RunInstallAsync(options, cancellationToken);
            }

            State = FrontendDevState.Starting;
            var startInfo = CreateProcessStartInfo(options.WorkingDirectory, options.Port);
            _logBuffer.Append(
                $"[Debug] Starting frontend: {startInfo.FileName} {startInfo.Arguments} (cwd {options.WorkingDirectory})");

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
                throw new InvalidOperationException("The operating system refused to start the frontend dev server.");

            _process = process;
            _ioCts = new CancellationTokenSource();
            _stdoutReadTask = ReadLinesAsync(process.StandardOutput, "[frontend] ", _ioCts.Token);
            _stderrReadTask = ReadLinesAsync(process.StandardError, "[frontend:err] ", _ioCts.Token);

            var deadline = DateTimeOffset.UtcNow + options.StartupTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsProcessExited(process))
                {
                    State = FrontendDevState.Failed;
                    throw new InvalidOperationException(
                        "Frontend dev server exited during startup. " +
                        $"Last output:{Environment.NewLine}{_logBuffer.GetTail(40)}");
                }

                if (await ProbeReadyAsync(cancellationToken))
                {
                    _logBuffer.Append($"[Debug] Frontend ready: {BaseAddress}/admin/");
                    State = FrontendDevState.Ready;
                    return;
                }

                await Task.Delay(ProbeInterval, cancellationToken);
            }

            await KillProcessAsync(process);
            State = FrontendDevState.Failed;
            throw new TimeoutException(
                $"Frontend dev server did not answer /admin/ within " +
                $"{options.StartupTimeout.TotalSeconds:0} seconds. " +
                $"Last output:{Environment.NewLine}{_logBuffer.GetTail(40)}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (_state is FrontendDevState.Idle
                or FrontendDevState.Installing
                or FrontendDevState.Starting)
            {
                State = FrontendDevState.Failed;
            }

            throw;
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
            var process = _process;
            if (process is not null)
            {
                await KillProcessAsync(process);
                await CleanupProcessResourcesAsync();
            }

            if (_state is not FrontendDevState.Failed)
                State = FrontendDevState.Stopped;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RunInstallAsync(FrontendDevStartOptions options, CancellationToken cancellationToken)
    {
        var startInfo = CreateInstallStartInfo(options.WorkingDirectory);
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logBuffer.Append($"[install] {e.Data}");
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logBuffer.Append($"[install:err] {e.Data}");
        };

        if (!process.Start())
            throw new InvalidOperationException("The operating system refused to start pnpm install.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(options.InstallTimeout, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"pnpm install did not finish within {options.InstallTimeout.TotalMinutes:0} minutes.");
        }

        if (process.ExitCode != 0)
        {
            State = FrontendDevState.Failed;
            throw new InvalidOperationException(
                $"pnpm install failed with exit code {process.ExitCode}. " +
                $"Last output:{Environment.NewLine}{_logBuffer.GetTail(40)}");
        }
    }

    private async Task<bool> ProbeReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _probeClient.GetAsync(
                new Uri(BaseAddress, "/admin/"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Single probe timeout; keep polling until the overall deadline.
            return false;
        }
    }

    internal static ProcessStartInfo CreateProcessStartInfo(string workingDirectory, int port) => new()
    {
        FileName = "cmd.exe",
        Arguments = $"/c pnpm run start:dev -- --host 127.0.0.1 --port {port}",
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        CreateNoWindow = true,
    };

    internal static ProcessStartInfo CreateInstallStartInfo(string workingDirectory) => new()
    {
        FileName = "cmd.exe",
        Arguments = "/c pnpm install",
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        CreateNoWindow = true,
    };

    private async Task ReadLinesAsync(StreamReader reader, string prefix, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                    return;

                _logBuffer.Append($"{prefix}{line}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Reader failure must not take down the supervisor.
        }
    }

    private async Task CleanupProcessResourcesAsync()
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
        _process?.Dispose();
        _process = null;
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

    private static bool IsProcessExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
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
            var process = _process;
            if (process is not null)
                await KillProcessAsync(process);
            await CleanupProcessResourcesAsync();
        }
        finally
        {
            _lock.Release();
        }

        _probeClient.Dispose();
        _lock.Dispose();
    }
}
