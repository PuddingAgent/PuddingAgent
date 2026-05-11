using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// ITerminalProcessManager 实现——管理 OS 级进程的完整生命周期。
/// 
/// 设计要点：
///   · 进程通过 System.Diagnostics.Process 启动，stdout/stderr 通过 Channel<string> 缓冲。
///   · 进程独立于 Agent/前端连接：断开后继续运行，重连后通过 SubscribeAsync 继续获取输出。
///   · 输出同时写入 data/terminal/{processId}.log 文件用于持久审计。
///   · KillAsync 使用 Kill(entireProcessTree: true) 确保子进程也被终止。
/// </summary>
public sealed class TerminalProcessManager : ITerminalProcessManager, IDisposable
{
    private readonly ConcurrentDictionary<string, TerminalProcess> _processes = new(StringComparer.Ordinal);
    private readonly ILogger<TerminalProcessManager> _logger;
    private readonly string _logDir;

    public TerminalProcessManager(ILogger<TerminalProcessManager> logger)
    {
        _logger = logger;
        _logDir = Path.Combine(AppContext.BaseDirectory, "data", "terminal");
        Directory.CreateDirectory(_logDir);
    }

    public Task<TerminalProcessInfo> StartAsync(
        string sessionId, string command, string workingDir, CancellationToken ct = default)
    {
        var processId = Guid.NewGuid().ToString("N")[..12];
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        var logFilePath = Path.Combine(_logDir, $"{processId}.log");
        var logStream = new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        var logWriter = new StreamWriter(logStream) { AutoFlush = true };

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
            Arguments = OperatingSystem.IsWindows()
                ? $"/c \"{command}\""
                : $"-c \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? process = null;
        try
        {
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var tp = new TerminalProcess
            {
                ProcessId = processId,
                SessionId = sessionId,
                Command = command,
                WorkingDir = workingDir,
                StartedAt = DateTimeOffset.UtcNow,
                Status = TerminalProcessStatus.Running,
                Channel = channel,
                LogWriter = logWriter,
                LogFilePath = logFilePath,
            };

            // 注册 stdout/stderr 事件处理
            process.OutputDataReceived += async (_, e) =>
            {
                if (e.Data is null) return;
                await channel.Writer.WriteAsync(e.Data, CancellationToken.None);
                await WriteLogAsync(logWriter, $"[stdout] {e.Data}");
            };
            process.ErrorDataReceived += async (_, e) =>
            {
                if (e.Data is null) return;
                await channel.Writer.WriteAsync(e.Data, CancellationToken.None);
                await WriteLogAsync(logWriter, $"[stderr] {e.Data}");
            };

            // 进程退出时更新状态并关闭 Channel
            process.Exited += async (_, _) =>
            {
                try
                {
                    tp.ExitCode = process.ExitCode;
                    tp.Status = process.ExitCode == 0
                        ? TerminalProcessStatus.Exited
                        : TerminalProcessStatus.Failed;
                    channel.Writer.Complete();
                    await logWriter.DisposeAsync();
                    await logStream.DisposeAsync();
                    process.Dispose();

                    _logger.LogInformation(
                        "[Terminal] pid={Pid} cmd={Cmd} exitCode={ExitCode} status={Status}",
                        processId, command[..Math.Min(command.Length, 80)], tp.ExitCode, tp.Status);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Terminal] pid={Pid} cleanup error", processId);
                }
            };

            if (!process.Start())
            {
                channel.Writer.Complete();
                throw new InvalidOperationException($"Failed to start process: {command}");
            }

            tp.OsProcess = process;
            _processes[processId] = tp;

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _logger.LogInformation(
                "[Terminal] pid={Pid} session={Session} cmd={Cmd} started",
                processId, sessionId, command[..Math.Min(command.Length, 80)]);

            return Task.FromResult(tp.ToInfo());
        }
        catch
        {
            process?.Dispose();
            channel.Writer.Complete();
            logWriter.Dispose();
            logStream.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<string> SubscribeAsync(
        string processId, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_processes.TryGetValue(processId, out var tp))
        {
            yield return $"[ERROR] Process '{processId}' not found.";
            yield break;
        }

        await foreach (var line in tp.Channel.Reader.ReadAllAsync(ct))
        {
            yield return line;
        }

        // Channel 完成后返回最终状态
        var info = tp.ToInfo();
        if (info.ExitCode.HasValue)
        {
            yield return $"[EXIT] code={info.ExitCode} status={info.Status}";
        }
    }

    public Task<bool> KillAsync(string processId)
    {
        if (!_processes.TryGetValue(processId, out var tp))
            return Task.FromResult(false);

        if (tp.Status != TerminalProcessStatus.Running)
            return Task.FromResult(false);

        try
        {
            var osProcess = tp.OsProcess;
            if (osProcess is { HasExited: false })
            {
                osProcess.Kill(entireProcessTree: true);
                tp.Status = TerminalProcessStatus.Killed;
                tp.ExitCode = -1;
                tp.Channel.Writer.Complete();

                _logger.LogInformation("[Terminal] pid={Pid} killed", processId);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Terminal] pid={Pid} kill failed", processId);
            tp.Status = TerminalProcessStatus.Failed;
            return Task.FromResult(false);
        }
    }

    public IReadOnlyList<TerminalProcessInfo> ListProcesses(string? sessionId = null)
    {
        var query = _processes.Values.AsEnumerable();
        if (sessionId is not null)
            query = query.Where(p => p.SessionId == sessionId);
        return query.Select(p => p.ToInfo()).ToList();
    }

    public Task<int> ReapAsync()
    {
        var toRemove = _processes
            .Where(kv => kv.Value.Status is not TerminalProcessStatus.Running)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            if (_processes.TryRemove(key, out var tp))
            {
                tp.Channel.Writer.TryComplete();
                tp.OsProcess?.Dispose();
            }
        }

        _logger.LogInformation("[Terminal] Reaped {Count} zombie processes", toRemove.Count);
        return Task.FromResult(toRemove.Count);
    }

    public void Dispose()
    {
        foreach (var (_, tp) in _processes)
        {
            tp.Channel.Writer.TryComplete();
            try { tp.OsProcess?.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            tp.OsProcess?.Dispose();
            tp.LogWriter?.Dispose();
        }
        _processes.Clear();
    }

    private static async Task WriteLogAsync(StreamWriter writer, string line)
    {
        try
        {
            await writer.WriteLineAsync($"{DateTimeOffset.UtcNow:O} {line}");
        }
        catch
        {
            // 日志写入失败不阻塞主流程
        }
    }

    /// <summary>终端进程内部状态。</summary>
    private sealed class TerminalProcess
    {
        public string ProcessId { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
        public string Command { get; init; } = string.Empty;
        public string WorkingDir { get; init; } = string.Empty;
        public DateTimeOffset StartedAt { get; init; }
        public int? ExitCode { get; set; }
        public TerminalProcessStatus Status { get; set; }
        public Channel<string> Channel { get; init; } = null!;
        public Process? OsProcess { get; set; }
        public StreamWriter LogWriter { get; init; } = null!;
        public string LogFilePath { get; init; } = string.Empty;

        public TerminalProcessInfo ToInfo() => new()
        {
            ProcessId = ProcessId,
            SessionId = SessionId,
            Command = Command,
            WorkingDir = WorkingDir,
            StartedAt = StartedAt,
            ExitCode = ExitCode,
            Status = Status,
        };
    }
}
