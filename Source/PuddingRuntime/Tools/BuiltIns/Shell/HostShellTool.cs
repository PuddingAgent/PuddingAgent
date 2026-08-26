using System.Diagnostics;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Executes a command directly on the host through a selected shell mode.
/// Supports three-tier safety: workspace safe commands auto-approve,
/// agent-private directory commands require reason, external paths require approval.
/// </summary>
[Tool(
    id: "shell",
    name: "Shell 命令执行",
    description: "在宿主机上执行命令，支持 auto、WSL/Bash、CMD 或 PowerShell 模式。在 Windows 上需要真实 Unix/Linux 语义时显式使用 shell=wsl；working_directory 会通过 wsl.exe --cd 映射到发行版路径，但不要假设 WSL 已安装 rg，内容检索优先 search_grep。在 Agent 私有目录运行时需提供 reason。Execute a command on the host using auto, WSL/Bash, CMD, or PowerShell mode.",
    category: ToolCategory.Execute,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.RequiresShell)]
public sealed class HostShellTool : PuddingToolBase<HostShellToolArgs>
{
    private readonly PuddingDataPaths _dataPaths;
    private readonly AuditLogger _audit;
    private readonly ILogger<HostShellTool> _logger;
    private readonly ITerminalCommandPolicy _commandPolicy;

    public HostShellTool(
        PuddingDataPaths dataPaths,
        AuditLogger audit,
        ILogger<HostShellTool> logger,
        ITerminalCommandPolicy? commandPolicy = null)
    {
        _dataPaths = dataPaths;
        _audit = audit;
        _logger = logger;
        _commandPolicy = commandPolicy ?? DefaultTerminalCommandPolicy.Instance;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        HostShellToolArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var zone = OperationZoneClassifier.ClassifyShellCommand(
            args.Command, args.WorkingDirectory,
            _dataPaths, context.WorkspaceId, context.AgentInstanceId);

        // 级别 2：Agent 私有目录 → 需要 reason
        if (zone == OperationZone.AgentPrivate && string.IsNullOrWhiteSpace(args.Reason))
        {
            _audit.Write(zone, "shell", context.AgentInstanceId,
                args.Command, args.Reason, false, 0, context.Trace);
            return ToolExecutionResult.Fail(
                "Running commands in agent private directory requires a 'reason' parameter. Please explain the purpose.");
        }

        var sw = Stopwatch.StartNew();
        var result = await HostShellExecutor.ExecuteAsync(
            new HostShellRequest
            {
                Command = args.Command,
                Shell = args.Shell,
                WorkingDirectory = args.WorkingDirectory ?? context.WorkingDirectory,
                TimeoutSeconds = args.TimeoutSeconds,
            },
            _logger,
            ct,
            _commandPolicy);

        var expectedNoMatch = HarnessToolCompatibilityAdapter.IsExpectedNoMatchExit(
            args.Command,
            result.ExitCode,
            result.Output);
        var effectiveSuccess = result.Success || expectedNoMatch;

        _audit.Write(zone, "shell", context.AgentInstanceId,
            args.Command, args.Reason, effectiveSuccess, sw.ElapsedMilliseconds, context.Trace);

        var output = result.Output;
        if (expectedNoMatch)
        {
            const string noMatchMessage =
                "[status=no_match exit_code=1] rg/grep completed normally and found no matches.";
            output = string.IsNullOrWhiteSpace(output)
                ? noMatchMessage
                : output + "\n" + noMatchMessage;
        }
        var tailLines = args.TailLines ?? 0;
        if (tailLines > 0 && !string.IsNullOrEmpty(output))
        {
            var lines = output.Split('\n');
            if (lines.Length > tailLines)
                output = $"[truncated: showing last {tailLines} of {lines.Length} lines]\n"
                    + string.Join("\n", lines.Skip(lines.Length - tailLines));
        }

        // 2026-08-22 模型倾向适配：模型天然爱用 shell 探查文件（本 run 94.8% 的成功
        // shell 是 Get-ChildItem/Select-String/Get-Content）。用返回值教育比工具描述教育
        // 有效——一句提示把下一次调用引导到零噪声、带游标的专用工具。
        var tip = BuildSpecializedToolTip(args.Command);
        if (tip is not null && (effectiveSuccess || HarnessToolCompatibilityAdapter.IsRipgrepCommand(args.Command)))
            output = string.IsNullOrEmpty(output) ? tip : output + "\n" + tip;

        return new ToolExecutionResult
        {
            Success = effectiveSuccess,
            Output = output,
            Error = expectedNoMatch ? null : result.Error,
            ExitCode = result.ExitCode,
            Status = expectedNoMatch ? ToolResultStatuses.NoMatch : null,
        };
    }

    private static string? BuildSpecializedToolTip(string command)
    {
        var first = command.TrimStart().Split(' ', 2).FirstOrDefault()?.TrimEnd(':').ToLowerInvariant();
        return first switch
        {
            "select-string" or "sls" or "grep" or "findstr" or "rg" =>
                "Tip: use `search_grep` next time — regex content search with clean output and no quoting pitfalls.",
            "get-childitem" or "gci" or "dir" or "ls" =>
                "Tip: use `list_dir` next time — structured directory listing without table-format noise.",
            "get-content" or "gc" or "cat" or "type" =>
                "Tip: use `file_read` next time — line-windowed reads with offset/limit and stable metadata.",
            "test-path" =>
                "Tip: use `list_dir` (parent directory) or `file_read` (first lines) to check existence with structured output.",
            _ => null,
        };
    }
}

public sealed record HostShellToolArgs
{
    [ToolParam("Command to execute on the host. Relative paths inside the command are resolved against working_directory when it is provided; avoid repeating the same directory prefix in both fields. Use absolute paths when unsure.")]
    public required string Command { get; init; }

    [ToolParam("Shell mode: auto, wsl, bash, cmd, powershell, or pwsh (alias of powershell). Default: auto.")]
    public string? Shell { get; init; }

    [ToolParam("Host working directory. Default: current runtime directory. If this points at the workspace directory, command paths should be relative to that directory or absolute, not prefixed again with the workspace path.")]
    public string? WorkingDirectory { get; init; }

    [ToolParam("Timeout in seconds, 1-600. Default: 30.")]
    public int? TimeoutSeconds { get; init; }

        [ToolParam("Reason for running this command. Required when running in agent private directories.")]
    public string? Reason { get; init; }

    [ToolParam("Return only the last N lines of output. Useful for build/test commands with large output. Default: 0 (return all).")]
    public int? TailLines { get; init; }
}
