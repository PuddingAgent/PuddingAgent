using System.Diagnostics;
using System.Text;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Executes command-line tools directly on the host with an explicit shell mode.
/// </summary>
public static class HostShellExecutor
{
    private static readonly HashSet<string> SupportedShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto",
        "wsl",
        "bash",
        "cmd",
        "powershell",
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
            return Fail($"Unsupported shell '{requestedShell}'. Supported shells: auto, wsl, bash, cmd, powershell.");

        var timeoutSeconds = request.TimeoutSeconds ?? 30;
        if (timeoutSeconds is < 1 or > 600)
            return Fail("timeout_seconds must be between 1 and 600.");

        var workingDirectory = ResolveWorkingDirectory(request.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
            return Fail($"working_directory does not exist: {workingDirectory}");

        var shell = ResolveShell(requestedShell);
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

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return Fail($"Command timed out after {timeoutSeconds} seconds.", exitCode: -1, shell, workingDirectory);
            }

            var stdout = TruncateOutput((await stdoutTask).TrimEnd());
            var stderr = TruncateOutput((await stderrTask).TrimEnd());
            var output = PrependExecutionMetadata(MergeOutput(stdout, stderr), shell, workingDirectory);
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
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/s");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(command);
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
            case "bash":
                if (!CommandExists("bash"))
                    return HostShellStartInfoResult.Fail("bash is not available on this host.");
                psi.FileName = "bash";
                psi.ArgumentList.Add("-lc");
                psi.ArgumentList.Add(command);
                return HostShellStartInfoResult.Ok(psi);
            default:
                return HostShellStartInfoResult.Fail($"Unsupported shell '{shell}'.");
        }
    }

    private static string ResolveShell(string requestedShell)
    {
        if (!requestedShell.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return requestedShell.ToLowerInvariant();

        return OperatingSystem.IsWindows() ? "powershell" : "bash";
    }

    private static string ResolveWorkingDirectory(string? workingDirectory) =>
        string.IsNullOrWhiteSpace(workingDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(workingDirectory);

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
    }

    private static string BuildPowerShellCommand(string command) =>
        "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false; " +
        "$OutputEncoding = [Console]::OutputEncoding; " +
        command;

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
        string workingDirectory = "") =>
        new()
        {
            Success = false,
            Output = string.Empty,
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
