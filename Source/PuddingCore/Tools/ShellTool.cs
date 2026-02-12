using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Core;
using PuddingCode.Models;

namespace PuddingCode.Tools;

public sealed class ShellTool : ITool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ProjectContext? _project;
    private readonly PermissionGuard? _guard;
    private readonly IAgentWorkspaceGuard? _workspaceGuard;
    private readonly ILogger<ShellTool>? _logger;
    private readonly IOutputDistiller _distiller;
    private readonly string _ownerAgentId;

    public ShellTool(
        ProjectContext? project = null,
        PermissionGuard? guard = null,
        IAgentWorkspaceGuard? workspaceGuard = null,
        ILogger<ShellTool>? logger = null,
        IOutputDistiller? distiller = null,
        string ownerAgentId = "spirit")
    {
        _project = project;
        _guard = guard;
        _workspaceGuard = workspaceGuard;
        _logger = logger;
        _distiller = distiller ?? new DefaultDistiller();
        _ownerAgentId = ownerAgentId;
    }

    public string Name => "shell";
    public string Description => "Execute a shell command and return stdout/stderr.";

    public ToolParameterSchema Parameters => new(
        [
            new("command", "string", "The shell command to execute"),
            new("workingDirectory", "string", "Working directory (optional, defaults to project root)")
        ],
        ["command"]);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var args = JsonSerializer.Deserialize<ShellToolArgs>(argumentsJson, s_jsonOptions);
        if (args?.Command is null) return "Error: command is required";

        // ── Workspace guard: tool permission check ──
        if (_workspaceGuard is not null)
        {
            var toolDecision = _workspaceGuard.CanExecuteTool(_ownerAgentId, Name);
            if (!toolDecision.Allowed)
            {
                _logger?.LogWarning(
                    "ShellTool execution denied for agent {AgentId}: {Reason} (rule: {Rule})",
                    _ownerAgentId, toolDecision.Reason, toolDecision.MatchedRule);
                throw new UnauthorizedAccessException(
                    $"Shell tool execution denied by workspace guard: {toolDecision.Reason}");
            }
        }

        // ── Permission check (Task 11) ──
        if (_guard is not null)
        {
            var perm = _guard.ValidateCommand(args.Command);
            if (!perm.IsAllowed)
                return perm.DenialReason ?? "Permission denied.";
        }

        var workDir = args.WorkingDirectory
                      ?? _project?.RootPath
                      ?? Environment.CurrentDirectory;

        // ── Workspace guard: working directory check ──
        if (_workspaceGuard is not null && _project is not null)
        {
            var dirDecision = _workspaceGuard.CanRead(_ownerAgentId, _project.RootPath, workDir);
            if (!dirDecision.Allowed)
            {
                _logger?.LogWarning(
                    "ShellTool working directory denied for agent {AgentId} path {Path}: {Reason} (rule: {Rule})",
                    _ownerAgentId, workDir, dirDecision.Reason, dirDecision.MatchedRule);
                throw new UnauthorizedAccessException(
                    $"Shell working directory denied by workspace guard: {dirDecision.Reason}");
            }
        }

        var isWindows = OperatingSystem.IsWindows();
        var shell = isWindows ? "cmd.exe" : "/bin/sh";
        var prefix = isWindows ? "/c" : "-c";

        try
        {
            var result = await Cli.Wrap(shell)
                .WithArguments([prefix, args.Command])
                .WithWorkingDirectory(workDir)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            var rawOutput = $"Exit code: {result.ExitCode}";
            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                rawOutput += $"\n--- stdout ---\n{result.StandardOutput}";
            if (!string.IsNullOrWhiteSpace(result.StandardError))
                rawOutput += $"\n--- stderr ---\n{result.StandardError}";

            // ── Output distillation (Task 12) ──
            var cmdName = args.Command.Split(' ', 2)[0];
            var ctx = new DistillContext(cmdName, result.ExitCode, workDir);
            var distilled = _distiller.Distill(rawOutput, ctx);
            return distilled.Summary;
        }
        catch (Exception ex)
        {
            return $"Error executing command: {ex.Message}";
        }
    }
}

file record ShellToolArgs(string? Command, string? WorkingDirectory);
