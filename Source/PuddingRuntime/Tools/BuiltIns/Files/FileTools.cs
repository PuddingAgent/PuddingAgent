using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

internal static class HostFileToolPaths
{
    public static string WorkspaceRoot => Path.GetFullPath(Directory.GetCurrentDirectory());

    public static bool TryResolveInsideWorkspace(
        string path, out string fullPath, out string error, bool skipWorkspaceCheck = false)
    {
        fullPath = null!;
        error = null!;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path is required.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(
                Path.IsPathRooted(path) ? path : Path.Combine(WorkspaceRoot, path));

            if (skipWorkspaceCheck)
                return true;

            var root = Path.GetFullPath(WorkspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;

                                    error = $"Access denied: path '{path}' is outside the workspace.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Invalid path '{path}': {ex.Message}. Workspace root: {WorkspaceRoot}";
            return false;
        }
    }
}

[Tool(
    id: "file_read",
    name: "Read file",
    description: "Read a UTF-8 text file from the host workspace.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 40)]
public sealed class FileReadTool : PuddingToolBase<FileReadArgs>
{
    public FileReadTool()
    {
    }

    public FileReadTool(ILogger<FileReadTool> logger)
    {
    }

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        FileReadArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        // file_read �ǵͷ���ֻ�����ߣ�����������·������
        if (!HostFileToolPaths.TryResolveInsideWorkspace(args.Path, out var fullPath, out var error, skipWorkspaceCheck: true))
            return Task.FromResult(ToolExecutionResult.Fail(error));

        if (!File.Exists(fullPath))
            return Task.FromResult(ToolExecutionResult.Fail($"File not found: {args.Path}"));

        try
        {
            var content = File.ReadAllText(fullPath, Encoding.UTF8);
            var totalChars = content.Length;
            var totalLines = content.Count(c => c == '\n') + 1;

            var meta = $"[META: size={totalChars} chars, lines={totalLines}, encoding=utf-8]";

            if (args.MaxChars.HasValue && totalChars > args.MaxChars.Value)
            {
                content = content[..args.MaxChars.Value];
                return Task.FromResult(ToolExecutionResult.Ok(
                    $"{meta}\n{content}\n... (truncated at {args.MaxChars.Value} chars, total {totalChars} chars, {totalLines} lines, encoding=utf-8)"));
            }

            if (args.HeadLines.HasValue)
            {
                var lines = content.Split('\n');
                content = string.Join("\n", lines.Take(args.HeadLines.Value));
            }
            else if (args.TailLines.HasValue)
            {
                var lines = content.Split('\n');
                content = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - args.TailLines.Value)));
            }
            else if (args.OffsetLines.HasValue)
            {
                var lines = content.Split('\n');
                var limit = args.LimitLines ?? (lines.Length - args.OffsetLines.Value);
                content = string.Join("\n", lines.Skip(args.OffsetLines.Value).Take(limit));
            }

            return Task.FromResult(ToolExecutionResult.Ok($"{meta}\n{content}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolExecutionResult.Fail($"Failed to read file '{args.Path}': {ex.Message}"));
        }
    }
}

public sealed record FileReadArgs
{
    [ToolParam("Absolute or relative file path inside the host workspace.")]
    public required string Path { get; init; }

    [ToolParam("Maximum characters to return. Default: 100000.")]
    public int? MaxChars { get; init; }

    [ToolParam("Read the first N lines. Highest priority pagination option.")]
    public int? HeadLines { get; init; }

    [ToolParam("Read the last N lines.")]
    public int? TailLines { get; init; }

    [ToolParam("0-based line offset to start reading from. Use with LimitLines.")]
    public int? OffsetLines { get; init; }

    [ToolParam("Maximum lines to read. Use with OffsetLines for arbitrary window.")]
    public int? LimitLines { get; init; }
}

[Tool(
    id: "file_write",
    name: "Write file",
    description: "Create or overwrite a UTF-8 text file in the host workspace.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.RequiresFileWrite | ToolSafetyFlags.Destructive,
    SortOrder = 42)]
public sealed class FileWriteTool : PuddingToolBase<FileWriteArgs>
{
    private readonly PuddingDataPaths _dataPaths;
    private readonly AuditLogger _audit;
    private readonly ILogger<FileWriteTool> _logger;

    public FileWriteTool()
        : this(CreateDefaultDataPaths(), new AuditLogger(CreateDefaultDataPaths()), NullLogger<FileWriteTool>.Instance)
    {
    }

    public FileWriteTool(ILogger<FileWriteTool> logger)
        : this(CreateDefaultDataPaths(), new AuditLogger(CreateDefaultDataPaths()), logger)
    {
    }

    public FileWriteTool(PuddingDataPaths dataPaths, AuditLogger audit, ILogger<FileWriteTool> logger)
    {
        _dataPaths = dataPaths;
        _audit = audit;
        _logger = logger;
    }

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        FileWriteArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (!HostFileToolPaths.TryResolveInsideWorkspace(args.Path, out var fullPath, out var error, skipWorkspaceCheck: context.IsYoloMode))
        {
            _audit.Write(OperationZone.External, "file_write", context.AgentInstanceId,
                args.Path, args.Reason, false, 0, context.Trace);
            return Task.FromResult(ToolExecutionResult.Fail(error));
        }

        var zone = OperationZoneClassifier.ClassifyPath(
            fullPath, _dataPaths, context.WorkspaceId, context.AgentInstanceId);

        if (zone == OperationZone.AgentPrivate && string.IsNullOrWhiteSpace(args.Reason))
        {
            _audit.Write(zone, "file_write", context.AgentInstanceId,
                args.Path, args.Reason, false, 0, context.Trace);
            return Task.FromResult(ToolExecutionResult.Fail(
                "Writing agent private files requires a 'reason' parameter. Please explain the purpose of this write."));
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
            if (args.Append == true && File.Exists(fullPath))
            {
                var existing = File.ReadAllText(fullPath, Encoding.UTF8);
                File.WriteAllText(tmpPath, existing + (args.Content ?? string.Empty), Encoding.UTF8);
            }
            else
            {
                File.WriteAllText(tmpPath, args.Content ?? string.Empty, Encoding.UTF8);
            }
            File.Move(tmpPath, fullPath, overwrite: true);

            _logger.LogInformation("[FileWriteTool] path={Path} append={Append}", fullPath, args.Append);
            _audit.Write(zone, "file_write", context.AgentInstanceId,
                args.Path, args.Reason, true, sw.ElapsedMilliseconds, context.Trace);
            return Task.FromResult(ToolExecutionResult.Ok($"Wrote {args.Path}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FileWriteTool] failed path={Path}", fullPath);
            _audit.Write(zone, "file_write", context.AgentInstanceId,
                args.Path, args.Reason, false, sw.ElapsedMilliseconds, context.Trace);
            return Task.FromResult(ToolExecutionResult.Fail($"Failed to write file '{args.Path}': {ex.Message}"));
        }
    }

    private static PuddingDataPaths CreateDefaultDataPaths() =>
        PuddingDataPaths.FromRoot(Path.Combine(HostFileToolPaths.WorkspaceRoot, "temp", "tool-audit-data"));
}

public sealed record FileWriteArgs
{
    [ToolParam("Absolute or relative file path inside the host workspace.")]
    public required string Path { get; init; }

    [ToolParam("Text content to write.")]
    public required string Content { get; init; }

    [ToolParam("Reason for writing this file. Required when writing to agent private directory.")]
    public string? Reason { get; init; }

    [ToolParam("Append content to end of file instead of overwriting.")]
    public bool? Append { get; init; }
}

[Tool(
    id: "list_dir",
    name: "List directory",
    description: "Return a structured listing for a directory in the host workspace. Prefer this over shell ls/dir when the agent needs file names and metadata.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 39)]
public sealed class ListDirectoryTool : PuddingToolBase<ListDirectoryArgs>
{
    private static readonly HashSet<string> s_defaultExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "bin",
        "obj",
        "node_modules",
    };

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        ListDirectoryArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(args.Path) ? "." : args.Path;
        if (!HostFileToolPaths.TryResolveInsideWorkspace(path, out var fullPath, out var error))
            return Task.FromResult(ToolExecutionResult.Fail(error));

        if (!Directory.Exists(fullPath))
            return Task.FromResult(ToolExecutionResult.Fail($"Directory not found: {path}"));

        var recursive = args.Recursive == true;
        var includeHidden = args.IncludeHidden == true;
        var maxEntries = Math.Clamp(args.MaxEntries ?? 200, 1, 2_000);
        var pattern = string.IsNullOrWhiteSpace(args.Pattern) ? "*" : args.Pattern;
        var entries = new List<ListDirectoryEntry>();
        var truncated = false;

        try
        {
            Walk(fullPath, fullPath, recursive, includeHidden, pattern, maxEntries, entries, ref truncated, ct);
            var payload = new ListDirectoryResult(
                Path.GetRelativePath(HostFileToolPaths.WorkspaceRoot, fullPath),
                entries.Count,
                truncated,
                entries);
            return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(payload)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolExecutionResult.Fail($"Failed to list directory '{path}': {ex.Message}"));
        }
    }

    private static void Walk(
        string root,
        string directory,
        bool recursive,
        bool includeHidden,
        string pattern,
        int maxEntries,
        List<ListDirectoryEntry> entries,
        ref bool truncated,
        CancellationToken ct)
    {
        if (entries.Count >= maxEntries)
        {
            truncated = true;
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            var attributes = File.GetAttributes(entry);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var name = Path.GetFileName(entry);
            var isHidden = attributes.HasFlag(FileAttributes.Hidden) || name.StartsWith(".", StringComparison.Ordinal);

            if (!includeHidden && isHidden)
                continue;

            if (isDirectory && s_defaultExcludedDirectories.Contains(name))
                continue;

            var relativePath = Path.GetRelativePath(root, entry);
            var matchesPattern = isDirectory || FileSearchPatternMatcher.MatchesFileOrPath(relativePath, pattern);

            if (matchesPattern)
            {
                var info = new FileInfo(entry);
                entries.Add(new ListDirectoryEntry(
                    relativePath,
                    name,
                    isDirectory ? "directory" : "file",
                    isDirectory ? null : info.Length,
                    File.GetLastWriteTimeUtc(entry)));

                if (entries.Count >= maxEntries)
                {
                    truncated = true;
                    return;
                }
            }

            if (recursive && isDirectory)
                Walk(root, entry, recursive, includeHidden, pattern, maxEntries, entries, ref truncated, ct);
        }
    }
}

public sealed record ListDirectoryArgs
{
    [ToolParam("Directory path relative to the host workspace. Default: current workspace root.")]
    public string? Path { get; init; }

    [ToolParam("When true, recursively list child directories. Default: false.")]
    public bool? Recursive { get; init; }

    [ToolParam("Maximum entries to return. Default: 200, max: 2000.")]
    public int? MaxEntries { get; init; }

    [ToolParam("When true, include hidden files and directories. Default: false.")]
    public bool? IncludeHidden { get; init; }

    [ToolParam("Optional glob-like pattern for file names or relative paths. Directories are still returned so recursive results retain structure.")]
    public string? Pattern { get; init; }
}

public sealed record ListDirectoryEntry(
    string Path,
    string Name,
    string Type,
    long? SizeBytes,
    DateTime LastWriteTimeUtc);

public sealed record ListDirectoryResult(
    string Path,
    int Count,
    bool Truncated,
    IReadOnlyList<ListDirectoryEntry> Entries);

[Tool(
    id: "apply_patch",
    name: "Apply patch",
    description: "Apply a unified diff to one or more existing files in the host workspace. Use this for multi-hunk or multi-file edits.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.RequiresFileWrite | ToolSafetyFlags.Destructive,
    SortOrder = 46)]
public sealed class ApplyPatchTool : PuddingToolBase<ApplyPatchArgs>
{
    private readonly PuddingDataPaths _dataPaths;
    private readonly AuditLogger _audit;

    public ApplyPatchTool()
        : this(CreateDefaultDataPaths(), new AuditLogger(CreateDefaultDataPaths()))
    {
    }

    public ApplyPatchTool(PuddingDataPaths dataPaths, AuditLogger audit)
    {
        _dataPaths = dataPaths;
        _audit = audit;
    }

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        ApplyPatchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.PatchText))
            return Task.FromResult(ToolExecutionResult.Fail("patch_text is required."));

        return Task.FromResult(UnifiedDiffPatchRunner.Apply(
            args.PatchText,
            args.Reason,
            args.DryRun,
            context,
            _dataPaths,
            _audit,
            "apply_patch"));
    }

    private static PuddingDataPaths CreateDefaultDataPaths() =>
        PuddingDataPaths.FromRoot(Path.Combine(HostFileToolPaths.WorkspaceRoot, "temp", "tool-audit-data"));
}

public sealed record ApplyPatchArgs
{
    [ToolParam("Unified diff text to apply transactionally to existing files.")]
    [JsonPropertyName("patch_text")]
    public required string PatchText { get; init; }

    [ToolParam("Reason for applying this patch. Required when patching agent private files.")]
    public string? Reason { get; init; }

    [ToolParam("If true, return diff preview without modifying files. Default: true (set false to apply changes).")]
    public bool? DryRun { get; init; }
}