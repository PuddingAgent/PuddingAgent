using System.Text.Json;
using CliWrap.Buffered;
using PuddingAssistant.Abstractions;
using PuddingAssistant.Core;
using PuddingAssistant.Models;

namespace PuddingAssistant.Skills.BuiltIn;

/// <summary>
/// Environment skills — file and shell operations.
/// Wraps the existing FileTool/ShellTool capabilities as attributed skills
/// so they can be discovered, role-checked, and schema-generated automatically.
/// Usable by all roles (Worker scoping is enforced at PermissionGuard level).
/// </summary>
public sealed class EnvironmentSkills
{
    private readonly ProjectContext? _project;
    private readonly PermissionGuard? _guard;
    private readonly IOutputDistiller _distiller;

    public EnvironmentSkills(
        ProjectContext? project = null,
        PermissionGuard? guard = null,
        IOutputDistiller? distiller = null)
    {
        _project = project;
        _guard = guard;
        _distiller = distiller ?? new DefaultDistiller();
    }

    /// <summary>Reads a file and returns its contents.</summary>
    [PuddingSkill("Read a file's contents. Supports partial reads with startLine/count for large files.",
        Group = "Environment")]
    public async Task<string> ReadFile(
        [SkillParam("File path (relative to project root or absolute)")] string path,
        [SkillParam("Starting line number (1-based, optional)")] int? startLine,
        [SkillParam("Number of lines to read (optional)")] int? count,
        CancellationToken ct)
    {
        var resolved = ResolvePath(path);

        if (_guard is not null)
        {
            var perm = _guard.ValidateFileRead(resolved);
            if (!perm.IsAllowed)
                return perm.DenialReason ?? "Permission denied.";
        }

        if (!File.Exists(resolved))
            return $"File not found: {path}";

        var lines = await File.ReadAllLinesAsync(resolved, ct);

        if (startLine.HasValue || count.HasValue)
        {
            var start = Math.Max(0, (startLine ?? 1) - 1);
            var take = count ?? (lines.Length - start);
            var slice = lines.Skip(start).Take(take);
            return string.Join("\n", slice.Select((l, i) => $"{start + i + 1}: {l}"));
        }

        return string.Join("\n", lines);
    }

    /// <summary>Writes content to a file, creating directories if needed.</summary>
    [PuddingSkill("Write content to a file. Creates parent directories automatically.",
        Group = "Environment")]
    public async Task<string> WriteFile(
        [SkillParam("File path (relative to project root or absolute)")] string path,
        [SkillParam("Content to write")] string content,
        [SkillParam("Overwrite existing file? (default: true)")] bool overwrite,
        CancellationToken ct)
    {
        var resolved = ResolvePath(path);

        if (_guard is not null)
        {
            var perm = _guard.ValidateFileWrite(resolved);
            if (!perm.IsAllowed)
                return perm.DenialReason ?? "Permission denied.";
        }

        if (!overwrite && File.Exists(resolved))
            return $"File already exists: {path}. Set overwrite=true to replace.";

        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(resolved, content, ct);
        return $"Written {content.Length} chars to {path}";
    }

    /// <summary>Lists files and directories at the given path.</summary>
    [PuddingSkill("List files and directories at a given path.",
        Group = "Environment")]
    public Task<string> ListDirectory(
        [SkillParam("Directory path (relative to project root or absolute)")] string path,
        CancellationToken ct)
    {
        var resolved = ResolvePath(path);

        if (_guard is not null)
        {
            var perm = _guard.ValidateDirectoryList(resolved);
            if (!perm.IsAllowed)
                return Task.FromResult(perm.DenialReason ?? "Permission denied.");
        }

        if (!Directory.Exists(resolved))
            return Task.FromResult($"Directory not found: {path}");

        var entries = Directory.GetFileSystemEntries(resolved);
        return Task.FromResult(string.Join("\n", entries));
    }

    /// <summary>Executes a shell command and returns output.</summary>
    [PuddingSkill("Execute a shell command and return stdout/stderr. Use for builds, tests, git, etc.",
        Group = "Environment")]
    public async Task<string> ExecuteCommand(
        [SkillParam("Shell command to execute (e.g. 'dotnet build', 'git status')")] string command,
        [SkillParam("Working directory (optional, defaults to project root)")] string? workingDir,
        CancellationToken ct)
    {
        if (_guard is not null)
        {
            var perm = _guard.ValidateCommand(command);
            if (!perm.IsAllowed)
                return perm.DenialReason ?? "Permission denied.";
        }

        var isWindows = OperatingSystem.IsWindows();
        var shell = isWindows ? "cmd.exe" : "/bin/sh";
        var prefix = isWindows ? "/c" : "-c";
        var workDir = workingDir
                      ?? _project?.RootPath
                      ?? Environment.CurrentDirectory;

        try
        {
            var result = await CliWrap.Cli.Wrap(shell)
                .WithArguments([prefix, command])
                .WithWorkingDirectory(workDir)
                .WithValidation(CliWrap.CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            var rawOutput = $"Exit code: {result.ExitCode}";
            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                rawOutput += $"\n--- stdout ---\n{result.StandardOutput}";
            if (!string.IsNullOrWhiteSpace(result.StandardError))
                rawOutput += $"\n--- stderr ---\n{result.StandardError}";

            var cmdName = command.Split(' ', 2)[0];
            var ctx = new DistillContext(cmdName, result.ExitCode, workDir);
            return _distiller.Distill(rawOutput, ctx).Summary;
        }
        catch (Exception ex)
        {
            return $"Error executing command: {ex.Message}";
        }
    }

    private string ResolvePath(string path) =>
        _project is not null && !Path.IsPathRooted(path)
            ? _project.Resolve(path)
            : path;
}
