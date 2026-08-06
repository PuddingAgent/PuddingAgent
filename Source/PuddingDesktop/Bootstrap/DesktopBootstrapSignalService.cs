using System.Diagnostics;
using System.Text;
using PuddingCode.Configuration;
using PuddingDesktop.Configuration;
using PuddingDesktop.Diagnostics;
using PuddingDesktop.Hosting;
using PuddingDesktop.Runtime;

namespace PuddingDesktop.Bootstrap;

/// <summary>
/// Guided-bootstrap signal service.
/// Polls a signal file (default &lt;DataRoot&gt;\config\rebuild.signal) every second.
/// On a valid "rebuild-restart" signal it runs the closed loop:
///   stop Core → dotnet incremental build → write yolo.signal (optional)
///   → restart Core → write &lt;SignalPath&gt;.result.json.
/// Every attempt (success or failure) writes a result file; malformed signals are
/// deleted to prevent a retry loop. No behavior change when the signal never appears.
/// </summary>
public sealed class DesktopBootstrapSignalService : IAsyncDisposable
{
    private const int BuildLogTailLines = 30;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly DesktopApplicationCoordinator _coordinator;
    private readonly IDesktopControlTokenService _tokenService;
    private readonly string _dataRoot;
    private readonly string _signalPath;
    private readonly string _resultPath;
    private readonly string _buildLogPath;
    private readonly string _repositoryRoot;
    private readonly string _yoloSignalPath;
    private readonly string _buildProjectRelativePath;
    private readonly IReadOnlyList<string> _buildArguments;
    private readonly bool _autoYolo;
    private readonly TimeSpan _buildTimeout;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public DesktopBootstrapSignalService(
        DesktopApplicationCoordinator coordinator,
        string dataRoot,
        PuddingDesktopBootstrapConfig config,
        IDesktopControlTokenService tokenService)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        _coordinator = coordinator;
        _tokenService = tokenService;
        _dataRoot = dataRoot;

        _signalPath = DesktopBootstrapSignalParser.ResolveSignalPath(config.SignalPath, dataRoot);
        _resultPath = DesktopBootstrapSignalParser.ResolveResultPath(_signalPath);
        _buildLogPath = DesktopBootstrapSignalParser.ResolveBuildLogPath(dataRoot);
        _repositoryRoot = DesktopBootstrapSignalParser.ResolveRepositoryRoot(
            Environment.GetEnvironmentVariable(DesktopBootstrapSignalParser.RepositoryRootEnvironmentVariable),
            AppContext.BaseDirectory);
        _yoloSignalPath = DesktopBootstrapSignalParser.ResolveYoloSignalPath(_repositoryRoot);
        _buildProjectRelativePath = string.IsNullOrWhiteSpace(config.BuildProjectRelativePath)
            ? DesktopBootstrapSignalParser.DefaultBuildProjectRelativePath
            : config.BuildProjectRelativePath;
        _buildArguments = DesktopBootstrapSignalParser.SplitArguments(config.BuildArguments);
        _autoYolo = config.AutoYolo;
        _buildTimeout = TimeSpan.FromSeconds(
            config.BuildTimeoutSeconds > 0
                ? config.BuildTimeoutSeconds
                : DesktopBootstrapSignalParser.DefaultBuildTimeoutSeconds);
    }

    /// <summary>Starts the background polling loop. Idempotent.</summary>
    public void Start(CancellationToken cancellationToken)
    {
        if (_loopTask is not null)
            return;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _loopTask = Task.Run(() => RunLoopAsync(linked.Token), CancellationToken.None);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(_signalPath))
                    await ProcessSignalAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                DesktopDiagnosticLog.Write("BootstrapSignalLoop", ex);
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessSignalAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        var buildLogTail = new List<string>();
        int? buildExitCode = null;
        var buildSucceeded = false;
        var yoloSignalWritten = false;
        var coreRestarted = false;
        var writeResult = true;

        try
        {
            string content;
            try
            {
                content = await File.ReadAllTextAsync(_signalPath, cancellationToken);
            }
            catch (Exception ex)
            {
                errors.Add($"读取信号文件失败: {ex.Message}");
                DesktopDiagnosticLog.Write("BootstrapSignalRead", ex);
                return;
            }

            var signal = DesktopBootstrapSignalParser.TryParseSignal(content);
            if (signal is null)
            {
                errors.Add("信号文件不是合法的 JSON，已忽略并删除。");
                return;
            }

            if (!DesktopBootstrapSignalParser.IsSupportedAction(signal.Action))
            {
                errors.Add($"不支持的 action: {signal.Action ?? "(null)"}");
                return;
            }

            var expectedToken = await _tokenService.GetOrCreateAsync(_dataRoot, cancellationToken);
            if (!DesktopBootstrapSignalParser.IsTokenValid(signal.Token, expectedToken))
            {
                // Deliberately do NOT leak the expected token value.
                errors.Add("令牌校验失败，信号已拒绝。");
                return;
            }

            // a) Stop Core. No-op when Core is already stopped.
            try
            {
                await _coordinator.StopCoreAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                errors.Add($"停止 Core 失败: {ex.Message}");
            }

            // b) Incremental dotnet build (working directory = repository root).
            try
            {
                var build = await RunBuildAsync(cancellationToken);
                buildExitCode = build.ExitCode;
                buildLogTail = build.LogTail;
                buildSucceeded = build.ExitCode == 0;
                if (!buildSucceeded)
                    errors.Add($"dotnet build 失败，退出码: {build.ExitCode}");
            }
            catch (Exception ex)
            {
                errors.Add($"dotnet build 异常: {ex.Message}");
                DesktopDiagnosticLog.Write("BootstrapBuild", ex);
            }

            // c) yolo.signal (only after a successful build).
            if (buildSucceeded && _autoYolo && signal.Yolo)
            {
                try
                {
                    await WriteYoloSignalAsync(signal.RequestedBy, cancellationToken);
                    yoloSignalWritten = true;
                }
                catch (Exception ex)
                {
                    errors.Add($"写入 yolo.signal 失败: {ex.Message}");
                }
            }

            // d) Restart Core with the last configured start options.
            //    On build failure this restores Core with the old binary.
            try
            {
                await _coordinator.StartCoreAsync(cancellationToken);
                coreRestarted = _coordinator.RuntimeSnapshot.State == DesktopRuntimeState.Ready;
            }
            catch (Exception ex)
            {
                errors.Add($"启动 Core 失败: {ex.Message}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown in progress: leave the signal file in place so the next
            // launch can retry; skip result write to avoid a misleading file.
            writeResult = false;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            DesktopDiagnosticLog.Write("BootstrapSignal", ex);
        }
        finally
        {
            if (writeResult)
            {
                var result = new DesktopBootstrapResult
                {
                    Success = buildSucceeded && coreRestarted && errors.Count == 0,
                    Action = DesktopBootstrapSignalParser.RebuildRestartAction,
                    StartedAt = startedAt,
                    FinishedAt = DateTimeOffset.UtcNow,
                    BuildExitCode = buildExitCode,
                    BuildLogTail = buildLogTail,
                    CoreRestarted = coreRestarted,
                    YoloSignalWritten = yoloSignalWritten,
                    Errors = errors,
                };

                try
                {
                    await DesktopBootstrapResultWriter.WriteAsync(
                        _resultPath, result, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    DesktopDiagnosticLog.Write("BootstrapResultWrite", ex);
                }

                TryDeleteSignal();
            }
        }
    }

    private async Task<(int ExitCode, List<string> LogTail)> RunBuildAsync(
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(_buildProjectRelativePath);
        foreach (var argument in _buildArguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("dotnet 进程启动失败。");

        var stdoutTask = ReadToEndSafelyAsync(process.StandardOutput);
        var stderrTask = ReadToEndSafelyAsync(process.StandardError);

        var timedOut = false;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_buildTimeout);
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout or shutdown: kill the whole child tree so nothing lingers.
            try { process.Kill(entireProcessTree: true); }
            catch { }

            await WaitForExitSafelyAsync(process);
            timedOut = !cancellationToken.IsCancellationRequested;
        }

        string stdout;
        string stderr;
        try
        {
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch
        {
            stdout = string.Empty;
            stderr = string.Empty;
        }

        var fullLog = stdout + Environment.NewLine + stderr;
        await AppendToBuildLogAsync(fullLog, CancellationToken.None);

        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        if (timedOut)
            throw new TimeoutException(
                $"dotnet build 超过 {_buildTimeout.TotalSeconds:0} 秒未完成，已终止。");

        return (process.ExitCode, TakeTail(fullLog));
    }

    private async Task WriteYoloSignalAsync(string? requestedBy, CancellationToken cancellationToken)
    {
        var content = string.Join(
            Environment.NewLine,
            "writtenBy=PuddingDesktop.bootstrap",
            "action=rebuild-restart",
            $"at={DateTimeOffset.UtcNow:O}",
            $"requestedBy={requestedBy ?? "unknown"}");

        await File.WriteAllTextAsync(_yoloSignalPath, content, cancellationToken);
    }

    private async Task AppendToBuildLogAsync(string content, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(_buildLogPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await File.AppendAllTextAsync(
                _buildLogPath,
                $"{DateTimeOffset.Now:O} [bootstrap build]{Environment.NewLine}{content}{Environment.NewLine}",
                cancellationToken);
        }
        catch
        {
            // Build log is best-effort diagnostics only.
        }
    }

    private void TryDeleteSignal()
    {
        try
        {
            if (File.Exists(_signalPath))
                File.Delete(_signalPath);
        }
        catch (Exception ex)
        {
            DesktopDiagnosticLog.Write("BootstrapSignalDelete", ex);
        }
    }

    private static async Task<string> ReadToEndSafelyAsync(StreamReader reader)
    {
        try { return await reader.ReadToEndAsync().ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static async Task WaitForExitSafelyAsync(Process process)
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch { }
    }

    private static List<string> TakeTail(string text)
        => text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(BuildLogTailLines)
            .ToList();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch { }
            _loopTask = null;
        }

        _cts.Dispose();
    }
}
