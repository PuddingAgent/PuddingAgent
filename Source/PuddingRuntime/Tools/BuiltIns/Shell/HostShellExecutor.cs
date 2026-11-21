using System.Diagnostics;
using System.Text;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Executes command-line tools directly on the host with an explicit shell mode.
/// </summary>
public static partial class HostShellExecutor
{
    private static readonly HashSet<string> SupportedShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto",
        "wsl",
        "bash",
        "cmd",
        "powershell",
        "pwsh",
    };

    public static async Task<HostShellResult> ExecuteAsync(
        HostShellRequest request,
        ILogger logger,
        CancellationToken ct = default,
        ITerminalCommandPolicy? commandPolicy = null)
    {
        var command = request.Command?.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return Fail("Command is required.");

        try
        {
            (commandPolicy ?? DefaultTerminalCommandPolicy.Instance)
                .EnsureInvariantAllowed(command);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(
                "[HostShell] Security invariant blocked cmd={Command}: {Reason}",
                command.Length > 120 ? command[..120] + "..." : command,
                ex.Message);
            return Fail(ex.Message);
        }

        var requestedShell = string.IsNullOrWhiteSpace(request.Shell)
            ? "auto"
            : request.Shell.Trim();
        if (!SupportedShells.Contains(requestedShell))
            return Fail($"Unsupported shell '{requestedShell}'. Supported shells: auto, wsl, bash, cmd, powershell, pwsh.");

        var timeoutSeconds = request.TimeoutSeconds ?? 30;
        if (timeoutSeconds is < 1 or > 600)
            return Fail("timeout_seconds must be between 1 and 600.");

        var workingDirectory = ResolveWorkingDirectory(request.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
            return Fail($"working_directory does not exist: {workingDirectory}");

        var shell = ResolveShell(requestedShell);
        if (shell == "powershell" && CmdDirectoryPrefixRegex().IsMatch(command))
            return Fail(
                "shell_syntax_mismatch: cd /d is CMD syntax, but this call selected PowerShell. " +
                "Set shell=cmd explicitly for CMD commands, or remove the cd prefix and set working_directory. " +
                "The command was not executed; do not retry the same prefix with different trailing commands.",
                shell: shell, workingDirectory: workingDirectory);
        var startInfoResult = BuildStartInfo(shell, command, workingDirectory);
        if (!startInfoResult.Success)
            return Fail(startInfoResult.Error!);

        var startInfo = startInfoResult.StartInfo!;
        logger.LogInformation(
            "[HostShell] shell={Shell} file={File} cwd={Cwd} timeout={Timeout}s cmd={Command}",
            shell,
            startInfo.FileName,
            workingDirectory,
            timeoutSeconds,
            command.Length > 120 ? command[..120] + "..." : command);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return Fail($"Failed to start shell '{shell}'.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                var partial = await CollectPartialOutputAsync(stdoutTask, stderrTask, shell, workingDirectory);
                return Fail(
                    BuildTimeoutError(timeoutSeconds, partial),
                    exitCode: -1, shell, workingDirectory, output: partial);
            }

            var streams = await ReadStreamsAsync(stdoutTask, stderrTask, OutputReadGraceMs);
            var stdout = TruncateOutput(StripAnsiEscape(streams.Stdout.TrimEnd()));
            var stderr = TruncateOutput(StripAnsiEscape(streams.Stderr.TrimEnd()));
            var merged = MergeOutput(stdout, stderr);
            if (streams.TimedOut)
                merged = AppendReadTimeoutNotice(merged);
            var output = PrependExecutionMetadata(merged, shell, workingDirectory);
            return new HostShellResult
            {
                Success = process.ExitCode == 0,
                Output = output,
                Error = process.ExitCode == 0 ? null : $"exit code {process.ExitCode}",
                ExitCode = process.ExitCode,
                Shell = shell,
                WorkingDirectory = workingDirectory,
                Executable = startInfo.FileName,
            };
        }
        catch (OperationCanceledException)
        {
            return Fail($"Command timed out after {timeoutSeconds} seconds.", exitCode: -1, shell, workingDirectory);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[HostShell] execution failed shell={Shell}", shell);
            return Fail($"Host shell execution failed: {ex.Message}", shell: shell, workingDirectory: workingDirectory);
        }
    }

    private static HostShellStartInfoResult BuildStartInfo(
        string shell,
        string command,
        string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        ConfigureUtf8Environment(psi);

        if (!shell.Equals("wsl", StringComparison.OrdinalIgnoreCase))
            psi.WorkingDirectory = workingDirectory;

        switch (shell.ToLowerInvariant())
        {
            case "powershell":
            {
                var exe = ResolvePowerShellExecutable();
                if (exe is null)
                    return HostShellStartInfoResult.Fail("Neither pwsh nor powershell.exe is available.");
                psi.FileName = exe;
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(BuildPowerShellCommand(command));
                return HostShellStartInfoResult.Ok(psi);
            }
            case "cmd":
                if (!OperatingSystem.IsWindows())
                    return HostShellStartInfoResult.Fail("cmd shell is only available on Windows hosts.");
                psi.FileName = "cmd.exe";
                // CMD does not understand the CRT backslash quoting used by ArgumentList.
                // Match TerminalProcessManager: /s removes only this outer quote pair.
                psi.Arguments = "/d /s /c \"" + command + "\"";
                return HostShellStartInfoResult.Ok(psi);
            case "wsl":
                if (!OperatingSystem.IsWindows())
                    return HostShellStartInfoResult.Fail("wsl shell is only available on Windows hosts.");
                if (!CommandExists("wsl.exe"))
                    return HostShellStartInfoResult.Fail("wsl.exe is not available on this host.");
                psi.FileName = "wsl.exe";
                psi.ArgumentList.Add("--cd");
                psi.ArgumentList.Add(workingDirectory);
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add("bash");
                psi.ArgumentList.Add("-lc");
                psi.ArgumentList.Add(command);
                return HostShellStartInfoResult.Ok(psi);
            // auto 在非 Windows 上会退到 sh（bash 不可用时）；两者调用方式相同。
            case "bash":
            case "sh":
                if (!CommandExists(shell))
                    return HostShellStartInfoResult.Fail($"{shell} is not available on this host.");
                psi.FileName = shell;
                psi.ArgumentList.Add("-lc");
                psi.ArgumentList.Add(command);
                return HostShellStartInfoResult.Ok(psi);
            default:
                return HostShellStartInfoResult.Fail($"Unsupported shell '{shell}'.");
        }
    }

    private static string ResolveShell(string requestedShell)
    {
        if (requestedShell.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
            return "powershell";

        if (!requestedShell.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return requestedShell.ToLowerInvariant();

        if (!OperatingSystem.IsWindows())
            return CommandExists("bash") ? "bash" : "sh";

        // Windows 的 auto 解析链：pwsh 7 > Windows PowerShell 5.1 > cmd。
        // PowerShell 家族统一解析为 "powershell"（具体可执行文件由
        // ResolvePowerShellExecutable 决定，它同样优先 pwsh）；两者都不可用时退到 cmd，
        // 而不是直接失败——auto 的语义是「选一个能跑的」，不是「必须给我 PowerShell」。
        return CommandExists("pwsh") || CommandExists("powershell.exe")
            ? "powershell"
            : "cmd";
    }

    private static string ResolveWorkingDirectory(string? workingDirectory) =>
        HostFileToolPaths.ResolveWorkspaceRoot(workingDirectory);

    private static string? ResolvePowerShellExecutable()
    {
        if (CommandExists("pwsh"))
            return "pwsh";

        return OperatingSystem.IsWindows() && CommandExists("powershell.exe")
            ? "powershell.exe"
            : null;
    }

    private static void ConfigureUtf8Environment(ProcessStartInfo psi)
    {
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        // 2026-08-22 冗余治理：模型不需要 ANSI 颜色码。NO_COLOR 是跨工具标准；
        // pwsh 的 $PSStyle 在 BuildPowerShellCommand 中单独关闭。
        psi.Environment["NO_COLOR"] = "1";
    }

    private static string BuildPowerShellCommand(string command) =>
        "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false; " +
        "$OutputEncoding = [Console]::OutputEncoding; " +
        // pwsh7 格式化输出（表格/高亮）默认带 ANSI 颜色；PlainText 从源头关闭。
        // powershell.exe 5.1 无 $PSStyle，try/catch 保持兼容。
        "try { $PSStyle.OutputRendering = 'PlainText' } catch { }; " +
        command;

    /// <summary>
    /// 兜底剥离 ANSI/VT 转义序列（颜色/光标控制）。子进程可能无视 NO_COLOR
    /// （如部分 node CLI），模型历史不应为颜色码付 token。实测探查类命令
    /// 输出中 ANSI+表格填充占 40-60%。
    /// </summary>
    private static string StripAnsiEscape(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('\x1b'))
            return text;

        return AnsiEscapeRegex().Replace(text, string.Empty);
    }

    [System.Text.RegularExpressions.GeneratedRegex("\x1b\\[[0-9;?]*[ -/]*[@-~]|\x1b\\][^\\x07]*(?:\x07|\x1b\\\\)")]
    private static partial System.Text.RegularExpressions.Regex AnsiEscapeRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*(?:cd|chdir)\s+/d(?:\s|$)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex CmdDirectoryPrefixRegex();

    private static bool CommandExists(string command)
    {
        try
        {
            var check = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where.exe" : "which",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            check.ArgumentList.Add(command);
            using var process = Process.Start(check);
            if (process is null)
                return false;
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 超时/取消后尽力取回进程已经产生的输出。
    /// 读取任务不再持有取消令牌，因此进程被杀、管道关闭后它们会正常完成；
    /// 仍用 grace 超时兜底，避免 kill 失败时把调用方挂死。
    /// 合并口径与成功路径完全一致（StripAnsiEscape → TruncateOutput → MergeOutput → PrependExecutionMetadata），
    /// 让「超时的输出」与「正常结束的输出」格式相同，消费方无需特判。
    /// </summary>
    private static async Task<string> CollectPartialOutputAsync(
        Task<string> stdoutTask,
        Task<string> stderrTask,
        string shell,
        string workingDirectory,
        int graceMs = 2_000)
    {
        var streams = await ReadStreamsAsync(stdoutTask, stderrTask, graceMs);

        var stdout = TruncateOutput(StripAnsiEscape(streams.Stdout.TrimEnd()));
        var stderr = TruncateOutput(StripAnsiEscape(streams.Stderr.TrimEnd()));
        var merged = MergeOutput(stdout, stderr);

        return string.IsNullOrWhiteSpace(merged)
            ? string.Empty
            : PrependExecutionMetadata(merged, shell, workingDirectory);
    }

    private const int OutputReadGraceMs = 2_000;

    /// <summary>stdout/stderr 的读取结果；TimedOut 表示至少一路没能在 grace 内读完。</summary>
    private readonly record struct StreamRead(string Stdout, string Stderr, bool TimedOut);

    /// <summary>
    /// 并行读取两路输出，各自最多等 graceMs。
    /// 正常路径毫秒级完成；只有「子进程仍持有管道」时才会用满 grace。
    /// </summary>
    private static async Task<StreamRead> ReadStreamsAsync(
        Task<string> stdoutTask,
        Task<string> stderrTask,
        int graceMs)
    {
        var reads = await Task.WhenAll(
            TryReadAsync(stdoutTask, graceMs),
            TryReadAsync(stderrTask, graceMs)).ConfigureAwait(false);

        return new StreamRead(
            reads[0] ?? string.Empty,
            reads[1] ?? string.Empty,
            reads[0] is null || reads[1] is null);
    }

    /// <summary>
    /// 读取超时时追加明确标记。
    /// 宁可写「可能不完整」，也不让消费方把「没读到」当成「进程没有输出」。
    /// </summary>
    private static string AppendReadTimeoutNotice(string merged)
    {
        const string notice =
            "[stream read timed out: a child process still holds the output pipe; the output above may be incomplete]";
        return string.IsNullOrWhiteSpace(merged) ? notice : merged + Environment.NewLine + notice;
    }

    /// <summary>读取任务的最长等待；超时或异常都返回 null，由调用方按「没有输出」处理。</summary>
    private static async Task<string?> TryReadAsync(Task<string> task, int graceMs)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromMilliseconds(graceMs)).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 超时错误文本：明确告知已产生的输出被保留在 output 字段里。
    /// 不写「无输出」——那会把「真的没来得及输出」和「我们没去取」混为一谈。
    /// </summary>
    private static string BuildTimeoutError(int timeoutSeconds, string partialOutput) =>
        string.IsNullOrEmpty(partialOutput)
            ? $"Command timed out after {timeoutSeconds} seconds."
            : $"Command timed out after {timeoutSeconds} seconds. Output produced before the timeout is preserved in output.";

    private static string MergeOutput(string stdout, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return stdout;
        if (string.IsNullOrWhiteSpace(stdout))
            return "[stderr]: " + stderr;
        return stdout + "\n[stderr]: " + stderr;
    }

    private static string PrependExecutionMetadata(string output, string shell, string workingDirectory)
    {
        var metadata = $"[shell={shell} cwd={workingDirectory}]";
        return string.IsNullOrWhiteSpace(output)
            ? metadata
            : metadata + Environment.NewLine + output;
    }

    private static string TruncateOutput(string text, int maxChars = 100_000)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
        return text[..maxChars] + $"\n... (截断，原始 {text.Length} 字符)";
    }

    private static HostShellResult Fail(
        string error,
        int exitCode = 1,
        string shell = "",
        string workingDirectory = "",
        string output = "") =>
        new()
        {
            Success = false,
            Output = output,
            Error = error,
            ExitCode = exitCode,
            Shell = shell,
            WorkingDirectory = workingDirectory,
            Executable = string.Empty,
        };

    private sealed record HostShellStartInfoResult(
        bool Success,
        ProcessStartInfo? StartInfo,
        string? Error)
    {
        public static HostShellStartInfoResult Ok(ProcessStartInfo startInfo) =>
            new(true, startInfo, null);

        public static HostShellStartInfoResult Fail(string error) =>
            new(false, null, error);
    }
}

public sealed record HostShellRequest
{
    public required string Command { get; init; }
    public string? Shell { get; init; }
    public string? WorkingDirectory { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed record HostShellResult
{
    public required bool Success { get; init; }
    public required string Output { get; init; }
    public string? Error { get; init; }
    public int ExitCode { get; init; }
    public required string Shell { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string Executable { get; init; }
}
