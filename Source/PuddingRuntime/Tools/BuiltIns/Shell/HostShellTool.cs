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
    description: "Execute a command on the host using auto, WSL/Bash, CMD, or PowerShell mode. Provide 'reason' when running in agent private directories.",
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
                WorkingDirectory = args.WorkingDirectory,
                TimeoutSeconds = args.TimeoutSeconds,
            },
            _logger,
            ct,
            _commandPolicy);

                _audit.Write(zone, "shell", context.AgentInstanceId,
            args.Command, args.Reason, result.Success, sw.ElapsedMilliseconds, context.Trace);

        var output = result.Output;
        var tailLines = args.TailLines ?? 0;
        if (tailLines > 0 && !string.IsNullOrEmpty(output))
        {
            var lines = output.Split('\n');
            if (lines.Length > tailLines)
                output = $"[truncated: showing last {tailLines} of {lines.Length} lines]\n"
                    + string.Join("\n", lines.Skip(lines.Length - tailLines));
        }

        return new ToolExecutionResult
        {
            Success = result.Success,
            Output = output,
            Error = result.Error,
            ExitCode = result.ExitCode,
        };
    }
}

public sealed record HostShellToolArgs
{
    [ToolParam("Command to execute on the host. Relative paths inside the command are resolved against working_directory when it is provided; avoid repeating the same directory prefix in both fields. Use absolute paths when unsure.")]
    public required string Command { get; init; }

    [ToolParam("Shell mode: auto, wsl, bash, cmd, or powershell. Default: auto.")]
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
