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
    private static Lazy<string> s_workspaceRoot = new(ResolveWorkspaceRootInternal);

    public static string WorkspaceRoot => s_workspaceRoot.Value;

    private static string ResolveWorkspaceRootInternal()
    {
        // 1) PUDDING_REPOSITORY_ROOT env var (highest priority)
        var envRoot = Environment.GetEnvironmentVariable("PUDDING_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
            return Path.GetFullPath(envRoot);

        // 2) Walk up from BaseDirectory up to 8 levels looking for repo markers
        var current = new DirectoryInfo(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory));
        for (var depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git"))
                || File.Exists(Path.Combine(current.FullName, "dev-up.py"))
                || File.Exists(Path.Combine(current.FullName, "checkpoint.json")))
            {
                return current.FullName;
            }
        }

        // 3) Final fallback: process current directory
        return Path.GetFullPath(Directory.GetCurrentDirectory());
    }

    /// <summary>
    /// Test-only: reset the cached WorkspaceRoot so the next access re-resolves.
    /// </summary>
    internal static void InvalidateWorkspaceRootCache()
    {
        var field = typeof(HostFileToolPaths).GetField(
            "s_workspaceRoot",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (field?.GetValue(null) is Lazy<string>)
        {
            field.SetValue(null, new Lazy<string>(ResolveWorkspaceRootInternal));
        }
    }

    public static string ResolveWorkspaceRoot(string? executionWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(executionWorkingDirectory))
            return WorkspaceRoot;

        return Path.GetFullPath(executionWorkingDirectory);
    }

    public static bool TryResolveInsideWorkspace(
        string path,
        out string fullPath,
        out string error,
        bool skipWorkspaceCheck = false,
        string? executionWorkingDirectory = null,
        bool isYoloMode = false)
    {
        fullPath = null!;
        error = null!;
        var requestedRoot = string.IsNullOrWhiteSpace(executionWorkingDirectory)
            ? WorkspaceRoot
            : executionWorkingDirectory;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path is required.";
            return false;
        }

        try
        {
            var workspaceRoot = ResolveWorkspaceRoot(executionWorkingDirectory);
            fullPath = Path.GetFullPath(
                Path.IsPathRooted(path) ? path : Path.Combine(workspaceRoot, path));

            if (skipWorkspaceCheck)
                return true;

            var root = workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;

            // YOLO mode: degrade to warning and allow
            if (isYoloMode)
            {
                error = BuildYoloWarning(path, fullPath, workspaceRoot);
                return true;
            }

            error = BuildAccessDeniedError(path, workspaceRoot);
            return false;
        }
        catch (Exception ex)
        {
            error =
                $"Invalid path '{path}': {ex.Message}. " +
                $"Execution root: {requestedRoot}";
            return false;
        }
    }

    private static string BuildAccessDeniedError(string path, string workspaceRoot)
    {
        var isBinDirectory = workspaceRoot.IndexOf(
            Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) >= 0;

        var sb = new StringBuilder();
        sb.AppendLine($"Path '{path}' is outside the current execution root.");
        sb.AppendLine($"Current execution root: {workspaceRoot}");
        sb.AppendLine();
        sb.AppendLine("This typically happens when a sub-agent inherits the parent runtime process working directory");
        sb.AppendLine("(e.g. a build output bin/ directory) instead of the actual project workspace.");
        sb.AppendLine();
        sb.AppendLine("Recommendations:");
        sb.AppendLine("  1. When spawning a sub-agent, pass the target project/workspace directory as the");
        sb.AppendLine("     working_directory parameter so file operations resolve correctly.");
        sb.AppendLine("  2. Set the PUDDING_REPOSITORY_ROOT environment variable to the repository root");
        sb.AppendLine("     before starting the runtime.");

        if (isBinDirectory)
        {
            sb.AppendLine();
            sb.AppendLine("\u26a0 WARNING: The current execution root appears to be a build output (bin/) directory.");
            sb.AppendLine("This is the parent runtime's own binary directory. Do NOT build, write, or modify");
            sb.AppendLine("files inside it — doing so could corrupt or kill the running parent process.");
        }

        sb.AppendLine();
        sb.AppendLine("Examples:");
        sb.AppendLine("  terminal_start: { \"command\": \"dotnet build\", \"cwd\": \"E:/github/MyRepo\" }");
        sb.AppendLine("  file_write: pass working_directory via execution context pointing to the real workspace");

        return sb.ToString().TrimEnd();
    }

    private static string BuildYoloWarning(string path, string fullPath, string workspaceRoot)
    {
        return $"YOLO bypass: path '{path}' resolves to '{fullPath}' which is outside " +
               $"the execution root '{workspaceRoot}'. Operation allowed because runtime is in YOLO mode. " +
               $"Consider setting PUDDING_REPOSITORY_ROOT or passing working_directory to avoid this warning.";
    }
}

[Tool(
    id: "file_read",
    name: "Read file",
    description: "从宿主工作区读取 UTF-8 文本文件。Read a UTF-8 text file from the host workspace. 大文件/日志最佳实践：优先用 TailLines=N 读末尾最新 N 行，或用 OffsetLines+LimitLines 分段读取；避免对超大文件用 FullFile=true 以免塞满上下文。默认超过 300 行或 40KB 会触发护栏只返回前 200 行并附 META 头（总行数/字节数/截断提示）。参数：HeadLines/TailLines/OffsetLines/LimitLines 行级分页，MaxChars 字符级截断，FullFile=true 绕过护栏。优先级：同时指定 MaxChars 与行级分页时，MaxChars 优先生效（先按字符截断）。",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 40)]
public sealed class FileReadTool : PuddingToolBase<FileReadArgs>
{
    private readonly FileChunkService _chunk;
    private readonly ILogger<FileReadTool> _logger;

    public FileReadTool()
        : this(NullLogger<FileReadTool>.Instance, new FileChunkService())
    {
    }

    public FileReadTool(ILogger<FileReadTool> logger)
        : this(logger, new FileChunkService())
    {
    }

    public FileReadTool(ILogger<FileReadTool> logger, FileChunkService chunk)
    {
        _logger = logger;
        _chunk = chunk;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        FileReadArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        // file_read is low-risk read-only — always skip workspace boundary check
        if (!HostFileToolPaths.TryResolveInsideWorkspace(
                args.Path,
                out var fullPath,
                out var error,
                skipWorkspaceCheck: true,
                executionWorkingDirectory: context.WorkingDirectory))
            return ToolExecutionResult.Fail(error);

        if (!File.Exists(fullPath))
            return ToolExecutionResult.Fail($"File not found: {args.Path}");

        try
        {
            var fileInfo = new FileInfo(fullPath);
            var isLargeFile = fileInfo.Length > FileChunkService.LargeFileByteThreshold;

            if (!isLargeFile)
            {
                // Fast path: small file — full read then post-hoc pagination
                var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                var totalChars = content.Length;
                var newlineCount = content.Count(c => c == '\n');
                var endsWithNewline = content.Length > 0 && content[^1] == '\n';
                var totalLines = newlineCount + (endsWithNewline ? 0 : 1);

                // Logical lines: strip the single trailing empty entry a trailing newline produces.
                var lines = content.Split('\n');
                if (endsWithNewline && lines.Length > 0)
                    lines = lines[..^1];

                var meta = $"[META: size={totalChars} chars, lines={totalLines}, encoding=utf-8]";

                // Guardrail: auto-truncate large files when no explicit pagination or FullFile
                var hasExplicitSlice = args.HeadLines.HasValue || args.TailLines.HasValue || args.OffsetLines.HasValue;
                if (!hasExplicitSlice && args.FullFile != true && (totalLines > 300 || fileInfo.Length > 40_000))
                {
                    var preview = string.Join("\n", lines.Take(200));
                    return ToolExecutionResult.Ok(
                        $"{meta}\n{preview}\n... [GUARDRAIL: {totalLines} lines, {totalChars} chars — showing first 200 lines. Use HeadLines/TailLines/OffsetLines/LimitLines for line-level windowing, or FullFile=true to read the complete file.]");
                }

                if (args.MaxChars.HasValue && totalChars > args.MaxChars.Value)
                {
                    content = content[..args.MaxChars.Value];
                    return ToolExecutionResult.Ok(
                        $"{meta}\n{content}\n... (truncated at {args.MaxChars.Value} chars, total {totalChars} chars, {totalLines} lines, encoding=utf-8)");
                }

                if (args.HeadLines.HasValue)
                {
                    content = string.Join("\n", lines.Take(args.HeadLines.Value));
                }
                else if (args.TailLines.HasValue)
                {
                    content = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - args.TailLines.Value)));
                }
                else if (args.OffsetLines.HasValue)
                {
                    var offset = Math.Max(0, args.OffsetLines.Value);
                    if (offset >= lines.Length)
                    {
                        // Out-of-range offset: return empty (consistent with the large-file path).
                        content = string.Empty;
                    }
                    else
                    {
                        var limit = args.LimitLines ?? (lines.Length - offset);
                        content = string.Join("\n", lines.Skip(offset).Take(Math.Max(0, limit)));
                    }
                }

                return ToolExecutionResult.Ok($"{meta}\n{content}");
            }

            // Large file path: use FileChunkService for streaming reads
            var totalLinesLarge = await _chunk.CountLinesAsync(fullPath, ct);
            var totalCharsLarge = (int)Math.Min(fileInfo.Length, int.MaxValue);
            var metaLarge = $"[META: size={totalCharsLarge} chars, lines={totalLinesLarge}, encoding=utf-8]";

            // Guardrail: auto-truncate large files when no explicit pagination or FullFile
            var hasExplicitSliceLarge = args.HeadLines.HasValue || args.TailLines.HasValue || args.OffsetLines.HasValue;
            if (!hasExplicitSliceLarge && args.FullFile != true && (totalLinesLarge > 300 || fileInfo.Length > 40_000))
            {
                var preview = await _chunk.ReadChunkAsync(fullPath, 0, 200, ct);
                return ToolExecutionResult.Ok(
                    $"{metaLarge}\n{preview}\n... [GUARDRAIL: {totalLinesLarge} lines, {totalCharsLarge} chars — showing first 200 lines. Use HeadLines/TailLines/OffsetLines/LimitLines for line-level windowing, or FullFile=true to read the complete file.]");
            }

            // MaxChars requires full read for accurate char count — warn and truncate
            if (args.MaxChars.HasValue)
            {
                _logger.LogWarning("[FileReadTool] large file {Path} ({Bytes} bytes) with MaxChars — full read required", args.Path, fileInfo.Length);
                var fullContent = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                var fullChars = fullContent.Length;
                if (fullChars > args.MaxChars.Value)
                {
                    fullContent = fullContent[..args.MaxChars.Value];
                    return ToolExecutionResult.Ok(
                        $"{metaLarge}\n{fullContent}\n... (truncated at {args.MaxChars.Value} chars, total {fullChars} chars, {totalLinesLarge} lines, encoding=utf-8)");
                }
                return ToolExecutionResult.Ok($"{metaLarge}\n{fullContent}");
            }

            string windowContent;
            if (args.HeadLines.HasValue)
            {
                windowContent = await _chunk.ReadChunkAsync(fullPath, 0, args.HeadLines.Value, ct);
            }
            else if (args.TailLines.HasValue)
            {
                var offset = Math.Max(0, totalLinesLarge - args.TailLines.Value);
                windowContent = await _chunk.ReadChunkAsync(fullPath, offset, args.TailLines.Value, ct);
            }
            else if (args.OffsetLines.HasValue)
            {
                var limit = args.LimitLines ?? (totalLinesLarge - args.OffsetLines.Value);
                windowContent = await _chunk.ReadChunkAsync(fullPath, args.OffsetLines.Value, limit, ct);
            }
            else
            {
                // No pagination args on a large file — read all via chunk (full streaming)
                windowContent = await _chunk.ReadChunkAsync(fullPath, 0, totalLinesLarge, ct);
            }

            return ToolExecutionResult.Ok($"{metaLarge}\n{windowContent}");
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail($"Failed to read file '{args.Path}': {ex.Message}");
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

    [ToolParam("Set true to bypass the guardrail and read the full file content, regardless of size.")]
    public bool? FullFile { get; init; }
}

[Tool(
    id: "file_write",
    name: "Write file",
    description: "在宿主工作区创建或覆盖 UTF-8 文本文件。【何时用】创建新文件或整体重写整个文件时使用；只改既有文件的局部内容时优先用 file_patch（增量修改，范围更小、不易误伤）。【怎么用】传 path（工作区内绝对/相对路径）+ content 即可；append=true 可追加到文件末尾而非覆盖；写入 Agent 私有目录必须提供 reason。【坑】是「覆盖」语义——覆盖已有文件前先用 file_read 确认当前内容；写入为无 BOM 的 UTF-8，不要期待文件头带 BOM；工作区外路径默认被拒（YOLO 模式除外）。Create or overwrite a UTF-8 text file in the host workspace — use for new files or full rewrites, prefer file_patch for incremental edits; pass path+content (append=true to append instead of overwrite, reason required for agent-private paths); overwrite is destructive so read the file first, output is UTF-8 without BOM, and paths outside the workspace are rejected unless YOLO mode.",
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
        if (!HostFileToolPaths.TryResolveInsideWorkspace(
                args.Path,
                out var fullPath,
                out var error,
                skipWorkspaceCheck: context.IsYoloMode,
                executionWorkingDirectory: context.WorkingDirectory,
                isYoloMode: context.IsYoloMode))
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
                File.WriteAllText(tmpPath, existing + (args.Content ?? string.Empty), new UTF8Encoding(false));
            }
            else
            {
                File.WriteAllText(tmpPath, args.Content ?? string.Empty, new UTF8Encoding(false));
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
    description: "返回宿主工作区目录的结构化清单。需要文件名和元数据时优先使用此工具而非 shell ls/dir。Return a structured listing for a directory in the host workspace. Prefer this over shell ls/dir when the agent needs file names and metadata.",
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
        if (!HostFileToolPaths.TryResolveInsideWorkspace(
                path,
                out var fullPath,
                out var error,
                executionWorkingDirectory: context.WorkingDirectory,
                isYoloMode: context.IsYoloMode))
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
                Path.GetRelativePath(
                    HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory),
                    fullPath),
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
    description: "对宿主工作区中的一个或多个现有文件应用统一差异补丁（unified diff），用于多区块或多文件编辑。Apply a unified diff to one or more existing files in the host workspace. Use this for multi-hunk or multi-file edits.",
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

    [ToolParam("If true, return diff preview without modifying files. Default: false (changes are applied directly; set true to preview only).")]
    public bool? DryRun { get; init; }
}
