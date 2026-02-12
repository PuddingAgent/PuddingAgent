using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Tools;

public sealed class ShellTool : ITool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ProjectContext? _project;

    public ShellTool(ProjectContext? project = null) => _project = project;

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

        var isWindows = OperatingSystem.IsWindows();
        var shell = isWindows ? "cmd.exe" : "/bin/sh";
        var prefix = isWindows ? "/c" : "-c";
        var workDir = args.WorkingDirectory
                      ?? _project?.RootPath
                      ?? Environment.CurrentDirectory;

        try
        {
            var result = await Cli.Wrap(shell)
                .WithArguments([prefix, args.Command])
                .WithWorkingDirectory(workDir)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            var output = $"Exit code: {result.ExitCode}";
            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                output += $"\n--- stdout ---\n{result.StandardOutput}";
            if (!string.IsNullOrWhiteSpace(result.StandardError))
                output += $"\n--- stderr ---\n{result.StandardError}";
            return output;
        }
        catch (Exception ex)
        {
            return $"Error executing command: {ex.Message}";
        }
    }
}

file record ShellToolArgs(string? Command, string? WorkingDirectory);
