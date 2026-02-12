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

[Tool(
    id: "file_search",
    name: "Search files",
    description: "Search file names. Defaults to Everything Provider if available. Everything requires an absolute directory; validation errors include available drive roots enumerated from the host. To search multiple drives, call once per drive root. If Everything is unavailable, falls back to BuiltInRecursiveFileSearch. Use action=list to inspect providers.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    SortOrder = 41)]
public sealed class FileSearchTool : PuddingToolBase<FileSearchArgs>
{
    private readonly IEnumerable<IFileSearchProvider> _providers;

    public FileSearchTool(IEnumerable<IFileSearchProvider> providers)
    {
        _providers = providers;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        FileSearchArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var action = (args.Action ?? "search").Trim().ToLowerInvariant();
        if (action == "list")
        {
            var infos = _providers.Select(p => new { id = p.ProviderId, name = p.DisplayName, available = p.IsAvailable });
            return ToolExecutionResult.Ok(JsonSerializer.Serialize(infos));
        }

        // Ĭ������ʹ�� Everything���죩��������ʱ���˵� BuiltInRecursiveFileSearch������
        string providerId;
        IFileSearchProvider? provider;

        if (!string.IsNullOrWhiteSpace(args.Provider))
        {
            // �û���ʽָ���� provider
            providerId = args.Provider;
            provider = _providers.FirstOrDefault(p =>
                string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (provider == null)
                return ToolExecutionResult.Fail($"File search provider not found: {providerId}");
            if (!provider.IsAvailable)
                return ToolExecutionResult.Fail($"Provider {providerId} is not available on this host.");
        }
        else
        {
            // δָ�� provider �� ���� Everything������ BuiltInRecursiveFileSearch
            var everythingProvider = _providers.FirstOrDefault(p =>
                string.Equals(p.ProviderId, "Everything", StringComparison.OrdinalIgnoreCase));
            var builtInProvider = _providers.FirstOrDefault(p =>
                string.Equals(p.ProviderId, "BuiltInRecursiveFileSearch", StringComparison.OrdinalIgnoreCase));

            if (everythingProvider is { IsAvailable: true })
            {
                provider = everythingProvider;
                providerId = "Everything";
            }
            else if (builtInProvider is { IsAvailable: true })
            {
                provider = builtInProvider;
                providerId = "BuiltInRecursiveFileSearch";
            }
            else
            {
                return ToolExecutionResult.Fail("No file search provider available.");
            }
        }

        if (string.Equals(providerId, "BuiltInRecursiveFileSearch", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(args.Directory))
        {
            return ToolExecutionResult.Fail(
                "Directory is required for provider BuiltInRecursiveFileSearch. " +
                "Use provider=Everything to search with the default directory, or pass an existing directory.");
        }

        if (IsEverythingProvider(providerId))
        {
            if (string.IsNullOrWhiteSpace(args.Directory))
                return ToolExecutionResult.Fail(BuildEverythingDirectoryGuidance("Everything requires an absolute directory."));

            if (!Path.IsPathRooted(args.Directory))
                return ToolExecutionResult.Fail(BuildEverythingDirectoryGuidance(
                    $"Everything requires an absolute directory. Received relative directory '{args.Directory}'."));
        }

        var directory = string.IsNullOrWhiteSpace(args.Directory) ? "." : args.Directory;
        if (!Path.IsPathRooted(directory))
            directory = Path.GetFullPath(Path.Combine(HostFileToolPaths.WorkspaceRoot, directory));

        if (!Directory.Exists(directory))
        {
            var message = $"Directory not found: {directory}";
            return ToolExecutionResult.Fail(IsEverythingProvider(providerId)
                ? BuildEverythingDirectoryGuidance(message)
                : message);
        }

        var pattern = string.IsNullOrWhiteSpace(args.Pattern) ? "*" : args.Pattern;
        var recursive = args.Recursive ?? true;
        var maxResults = Math.Clamp(args.MaxResults ?? 50, 1, 500);

        // ** ͨ���������Ч Windows �ļ�����ģʽ��.NET Directory.EnumerateFiles ��֧�֣�
        if (pattern.Contains("**"))
        {
            return ToolExecutionResult.Fail(
                $"Pattern '{pattern}' contains '**' which is not supported by Windows file search. " +
                "Use '*' for single-directory wildcard (e.g. '*.cs') or set recursive=true to search subdirectories. " +
                "Example: directory='<absolute-or-workspace-relative-directory>', pattern='*stats*', recursive=true");
        }

        try
        {
            var results = await provider.SearchAsync(directory, pattern, recursive, maxResults, ct);
            var output = JsonSerializer.Serialize(results);
            if (results.Count == 0)
                output += Environment.NewLine + Environment.NewLine + BuildNoResultsGuidance(providerId, directory, pattern, recursive);
            return ToolExecutionResult.Ok(output);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail($"File search failed using provider {providerId}: {ex.Message}");
        }
    }

    private static bool IsEverythingProvider(string providerId) =>
        string.Equals(providerId, "Everything", StringComparison.OrdinalIgnoreCase);

    private static string BuildEverythingDirectoryGuidance(string problem) =>
        problem + Environment.NewLine +
        "Guidance: provider=Everything searches the Everything index under one absolute directory at a time. " +
        "Pass an absolute directory such as a drive root or project root. If the target may be on multiple drives, call file_search once per relevant drive root." +
        Environment.NewLine +
        $"Available drive roots: {GetAvailableDriveRootsText()}" + Environment.NewLine +
        """Examples: {"provider":"Everything","directory":"<drive-root>","pattern":"*.cs","recursive":true}; {"provider":"BuiltInRecursiveFileSearch","directory":"Source","pattern":"*.cs","recursive":true}""";

    private static string BuildNoResultsGuidance(string providerId, string directory, string pattern, bool recursive)
    {
        var sb = new StringBuilder();
        sb.AppendLine("No files matched the file_search request.");
        sb.AppendLine($"Provider: {providerId}");
        sb.AppendLine($"Directory: {directory}");
        sb.AppendLine($"Pattern: {pattern}");
        sb.AppendLine($"Recursive: {recursive}");
        sb.AppendLine("Guidance: check that the directory is the intended search root, simplify the pattern, or use a broader pattern such as '*' or '*.cs'.");
        if (IsEverythingProvider(providerId))
        {
            sb.AppendLine("Everything guidance: pass an absolute directory. Everything searches one root at a time; if the file may be on another drive, retry once per relevant drive root.");
            sb.AppendLine($"Available drive roots: {GetAvailableDriveRootsText()}");
        }
        else
        {
            sb.AppendLine("BuiltInRecursiveFileSearch guidance: relative directories are resolved under the current workspace; use recursive=true to include subdirectories.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string GetAvailableDriveRootsText()
    {
        try
        {
            var roots = DriveInfo.GetDrives()
                .Select(d => d.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (roots.Length > 0)
                return string.Join(", ", roots);
        }
        catch
        {
            // Fall through to a conservative root hint derived from the current process.
        }

        return Path.GetPathRoot(Directory.GetCurrentDirectory()) ?? Path.GetFullPath(Path.DirectorySeparatorChar.ToString());
    }
}

public sealed record FileSearchArgs
{
    [ToolParam("Action to perform: list or search. Default: search.")]
    public string? Action { get; init; }

    [ToolParam("File search provider id. Default: auto-select Everything (fast) �� BuiltInRecursiveFileSearch (slow fallback). Supported providers: Everything, BuiltInRecursiveFileSearch. Use action=list to inspect availability.")]
    public string? Provider { get; init; }

    [ToolParam("File name text or glob pattern. Default: *")]
    public string? Pattern { get; init; }

    [ToolParam("Root directory to search. Everything requires an absolute directory and searches one root at a time; validation errors list available drive roots from this host. BuiltInRecursiveFileSearch accepts workspace-relative directories.")]
    public string? Directory { get; init; }

    [ToolParam("Search subdirectories. Default: true.")]
    public bool? Recursive { get; init; }

    [ToolParam("Maximum result count, 1-500. Default: 50.")]
    public int? MaxResults { get; init; }
}

public interface IFileSearchProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    bool IsAvailable { get; }
    Task<IReadOnlyList<string>> SearchAsync(string directory, string pattern, bool recursive, int maxResults, CancellationToken ct);
}

internal sealed class BuiltInRecursiveFileSearchProvider : IFileSearchProvider
{
    public string ProviderId => "BuiltInRecursiveFileSearch";
    public string DisplayName => "Built-in recursive file search";
    public bool IsAvailable => true;

    public Task<IReadOnlyList<string>> SearchAsync(string directory, string pattern, bool recursive, int maxResults, CancellationToken ct)
    {
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var results = Directory.EnumerateFiles(directory, pattern, searchOption)
            .Take(maxResults)
            .Select(path => Path.GetFileName(path) ?? path)
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(results);
    }
}

internal interface IEverythingSdk
{
    bool IsAvailable(out string? error);
    Task<EverythingQueryResult> QueryAsync(EverythingQueryRequest request, CancellationToken ct);
}

internal sealed record EverythingQueryRequest(string Directory, string Pattern, int MaxResults);

internal sealed record EverythingQueryResult(IReadOnlyList<EverythingQueryItem> Items);

internal sealed record EverythingQueryItem(string FullPath);

internal sealed class EverythingSearchProvider : IFileSearchProvider
{
    private readonly IEverythingSdk _sdk;

    public EverythingSearchProvider(IEverythingSdk sdk)
    {
        _sdk = sdk;
    }

    public string ProviderId => "Everything";
    public string DisplayName => "Everything (full-disk instant search)";
    public bool IsAvailable => _sdk.IsAvailable(out _);

    public async Task<IReadOnlyList<string>> SearchAsync(string directory, string pattern, bool recursive, int maxResults, CancellationToken ct)
    {
        if (!_sdk.IsAvailable(out var unavailableReason))
            throw new InvalidOperationException(unavailableReason ?? "Everything SDK is not available.");

        var request = new EverythingQueryRequest(directory, pattern, maxResults);
        var result = await _sdk.QueryAsync(request, ct);
        var root = Path.GetFullPath(directory);
        var rootTrimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return result.Items
            .Select(i => i.FullPath)
            .Where(path => FileSearchPathHelpers.IsInsideDirectory(path, root))
            .Where(path => recursive || IsDirectChild(path, rootTrimmed))
            .Where(path => FileSearchPatternMatcher.Matches(path, root, pattern))
            .Take(maxResults)
            .ToArray();
    }

    private static bool IsDirectChild(string path, string rootDirectory)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))
            ?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(parent, rootDirectory, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class EverythingSdk : IEverythingSdk
{
    private const uint EverythingErrorOk = 0;
    private static readonly SemaphoreSlim s_gate = new(1, 1);

    public bool IsAvailable(out string? error)
    {
        error = null;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            error = "Everything provider unavailable: Everything64.dll is supported only on Windows.";
            return false;
        }

        if (!Environment.Is64BitProcess)
        {
            error = "Everything provider unavailable: Pudding must run as a 64-bit process to load Everything64.dll.";
            return false;
        }

        if (!NativeLibrary.TryLoad("Everything64.dll", out var handle))
        {
            error = "Everything provider unavailable: Everything64.dll was not found or could not be loaded. Ensure Everything64.dll is present in the application output directory, or use provider BuiltInRecursiveFileSearch.";
            return false;
        }

        NativeLibrary.Free(handle);
        return true;
    }

    public async Task<EverythingQueryResult> QueryAsync(EverythingQueryRequest request, CancellationToken ct)
    {
        await s_gate.WaitAsync(ct);
        try
        {
            Everything_Reset();
            Everything_SetSearchW(BuildSearchText(request));
            Everything_SetMatchPath(true);
            Everything_SetMatchCase(false);
            Everything_SetRegex(false);
            Everything_SetMax((uint)Math.Clamp(request.MaxResults, 1, 500));

            if (!Everything_QueryW(true))
                throw CreateQueryException();

            var count = Everything_GetNumResults();
            var items = new List<EverythingQueryItem>((int)Math.Min(count, (uint)request.MaxResults));
            for (uint i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var buffer = new StringBuilder(32768);
                var length = Everything_GetResultFullPathNameW(i, buffer, (uint)buffer.Capacity);
                if (length > 0)
                    items.Add(new EverythingQueryItem(buffer.ToString()));
            }

            return new EverythingQueryResult(items);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException("Everything provider unavailable: Everything64.dll was not found or could not be loaded. Use provider BuiltInRecursiveFileSearch.", ex);
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidOperationException("Everything provider unavailable: Everything64.dll could not be loaded by this process. Use provider BuiltInRecursiveFileSearch.", ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new InvalidOperationException("Everything provider unavailable: Everything64.dll does not expose the expected SDK entry points. Use provider BuiltInRecursiveFileSearch.", ex);
        }
        finally
        {
            try
            {
                Everything_Reset();
            }
            finally
            {
                s_gate.Release();
            }
        }
    }

    private static string BuildSearchText(EverythingQueryRequest request)
    {
        var directory = Path.GetFullPath(request.Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pattern = string.IsNullOrWhiteSpace(request.Pattern) ? "*" : request.Pattern.Trim();
        return $"\"{directory}\" {pattern}";
    }

    private static InvalidOperationException CreateQueryException()
    {
        var lastError = Everything_GetLastError();
        var message = lastError == EverythingErrorOk
            ? "Everything provider query failed: Everything returned no success signal."
            : $"Everything provider query failed: Everything appears unavailable or not running. LastError={lastError}.";
        return new InvalidOperationException(message + " Use action=list to inspect providers or retry with BuiltInRecursiveFileSearch.");
    }

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
    private static extern bool Everything_SetSearchW(string lpSearchString);

    [DllImport("Everything64.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void Everything_SetMatchPath(bool bEnable);

    [DllImport("Everything64.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void Everything_SetMatchCase(bool bEnable);

    [DllImport("Everything64.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void Everything_SetRegex(bool bEnable);

    [DllImport("Everything64.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void Everything_SetMax(uint dwMax);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
    private static extern bool Everything_QueryW(bool bWait);

    [DllImport("Everything64.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern uint Everything_GetNumResults();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
    private static extern uint Everything_GetResultFullPathNameW(uint nIndex, StringBuilder lpString, uint nMaxCount);

    [DllImport("Everything64.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern uint Everything_GetLastError();

    [DllImport("Everything64.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void Everything_Reset();
}

internal static class FileSearchPathHelpers
{
    public static bool IsInsideDirectory(string path, string directory)
    {
        try
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
                   || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

internal static class FileSearchPatternMatcher
{
    public static string ToDirectorySearchPattern(string pattern)
    {
        var value = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;
        var searchPattern = value.Contains('*') || value.Contains('?')
            ? Regex.Replace(value, @"^\*\*[/\\]", "")
            : "*";
        return string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern;
    }

    public static bool Matches(string path, string rootDirectory, string pattern)
    {
        var value = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;
        var fileName = Path.GetFileName(path);
        var relativePath = Path.GetRelativePath(rootDirectory, path);

        if (!value.Contains('*') && !value.Contains('?'))
        {
            return fileName.Contains(value, StringComparison.OrdinalIgnoreCase)
                   || relativePath.Contains(value, StringComparison.OrdinalIgnoreCase);
        }

        var normalizedPattern = Regex.Replace(value, @"^\*\*[/\\]", "");
        return GlobLikeMatch(fileName, normalizedPattern)
               || GlobLikeMatch(relativePath, normalizedPattern);
    }

    public static bool MatchesFileOrPath(string path, string pattern)
    {
        var value = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;
        var fileName = Path.GetFileName(path);

        if (!value.Contains('*') && !value.Contains('?'))
        {
            return fileName.Contains(value, StringComparison.OrdinalIgnoreCase)
                   || path.Contains(value, StringComparison.OrdinalIgnoreCase);
        }

        var normalizedPattern = Regex.Replace(value, @"^\*\*[/\\]", "");
        return GlobLikeMatch(fileName, normalizedPattern)
               || GlobLikeMatch(path, normalizedPattern);
    }

    private static bool GlobLikeMatch(string value, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>���������������ı��ļ�ִ�оֲ�����������ƥ��Ĭ�Ϻ��Կհײ��ɼ��ַ���Tab?�ո�CRLF?LF����</summary>
[Tool(
    id: "file_patch",
    name: "Patch file",
    description: "Patch text files in the host workspace. Supports single/batch replacements, line-based operations (insert, delete, replace_lines), and regex replacements. Whitespace-insensitive matching by default.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.RequiresFileWrite | ToolSafetyFlags.Destructive,
    SortOrder = 45)]
public sealed class FilePatchTool : PuddingToolBase<FilePatchArgs>
{
    private readonly PuddingDataPaths _dataPaths;
    private readonly AuditLogger _audit;
    private readonly ILogger<FilePatchTool> _logger;

    public FilePatchTool()
        : this(CreateDefaultDataPaths(), new AuditLogger(CreateDefaultDataPaths()), NullLogger<FilePatchTool>.Instance)
    {
    }

    public FilePatchTool(ILogger<FilePatchTool> logger)
        : this(CreateDefaultDataPaths(), new AuditLogger(CreateDefaultDataPaths()), logger)
    {
    }

    public FilePatchTool(PuddingDataPaths dataPaths, AuditLogger audit, ILogger<FilePatchTool> logger)
    {
        _dataPaths = dataPaths;
        _audit = audit;
        _logger = logger;
    }

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        FilePatchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(args.PatchText))
            return Task.FromResult(ApplyUnifiedDiffPatch(args, context));

        var patches = ResolvePatches(args).ToArray();
        if (patches.Length == 0)
            return Task.FromResult(ToolExecutionResult.Fail("At least one patch with operations is required."));

        var summaries = new List<string>();
        foreach (var patch in patches)
        {
            if (!HostFileToolPaths.TryResolveInsideWorkspace(patch.Path, out var fullPath, out var resolveError, skipWorkspaceCheck: context.IsYoloMode))
            {
                _audit.Write(OperationZone.External, "file_patch", context.AgentInstanceId,
                    patch.Path, args.Reason, false, 0, context.Trace);
                return Task.FromResult(ToolExecutionResult.Fail(resolveError));
            }

            if (!File.Exists(fullPath))
            {
                var zone = OperationZoneClassifier.ClassifyPath(
                    fullPath, _dataPaths, context.WorkspaceId, context.AgentInstanceId);
                _audit.Write(zone, "file_patch", context.AgentInstanceId,
                    patch.Path, args.Reason, false, 0, context.Trace);
                return Task.FromResult(ToolExecutionResult.Fail($"File not found: {patch.Path}"));
            }

            var zone2 = OperationZoneClassifier.ClassifyPath(
                fullPath, _dataPaths, context.WorkspaceId, context.AgentInstanceId);

            if (zone2 == OperationZone.AgentPrivate && string.IsNullOrWhiteSpace(args.Reason))
            {
                _audit.Write(zone2, "file_patch", context.AgentInstanceId,
                    patch.Path, args.Reason, false, 0, context.Trace);
                return Task.FromResult(ToolExecutionResult.Fail(
                    "Patching agent private files requires a 'reason' parameter. Please explain the purpose of this patch."));
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var original = File.ReadAllText(fullPath, Encoding.UTF8);
                var replacementCount = 0;
                var errors = new List<string>();
                var relPath = Path.GetRelativePath(HostFileToolPaths.WorkspaceRoot, fullPath);

                var ops = patch.Operations ?? [];

                // ��ȡ��Χ����
                var scopeStartLine = args.ScopeStartLine;
                var scopeEndLine = args.ScopeEndLine;

                // �׶�1���ռ������ı��滻����ԭ������ƥ�䣬�������ţ�
                // Pre-validate operations before applying
            foreach (var op in ops)
            {
                var opType = (op.Type ?? "replace").Trim();
                if (opType.Equals("replace", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(op.OldText))
                    {
                        return Task.FromResult(ToolExecutionResult.Fail(
                            $"replace operation in {relPath} requires 'old_text'. " +
                            "Provide the exact text to find before replacing. " +
                            "Example: operations=[{type='replace', old_text='old code', new_text='new code'}]"));
                    }
                }
                if (opType.Contains("regex", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(op.Pattern))
                    {
                        return Task.FromResult(ToolExecutionResult.Fail(
                            $"regexReplace operation in {relPath} requires 'pattern'. " +
                            "Provide the regex pattern to match. " +
                            "Example: operations=[{type='regexReplace', pattern='Console.WriteLine', replacement='logger.Log'}]"));
                    }
                }
            }

            var replacements = CollectReplacements(original, ops, ref replacementCount, errors, relPath, scopeStartLine, scopeEndLine);
                var current = ApplyReplacements(original, replacements);

                // �׶�2��Ӧ���������
                foreach (var op in ops)
                {
                    var opType = (op.Type ?? "replace").Trim();
                    if (!opType.Contains("regex", StringComparison.OrdinalIgnoreCase)) continue;
                    current = ApplyRegexOperation(current, op, ref replacementCount);
                }

                // �׶�3��Ӧ���в�������ǰ����У�飩
                current = ApplyLineOperations(current, ops, ref replacementCount, errors, relPath);

                // DryRun default=true �� ǿ��Ԥ��
                var isDryRun = args.DryRun != false;
                var previewPrefix = isDryRun ? "(preview �� set dry_run=false to apply)" : "";
                if (current == original)
                {
                    var msg = $"{relPath}: unchanged {previewPrefix}".TrimEnd();
                    if (errors.Count > 0)
                        msg += $"\n  {errors.Count} issue(s):\n    " + string.Join("\n    ", errors);
                    summaries.Add(msg);
                    continue;
                }

                var diff = GenerateSimpleDiff(original, current);
                if (isDryRun)
                {
                    summaries.Add($"{relPath}: (preview �� set dry_run=false to apply)\n{diff}");
                    continue;
                }

                File.WriteAllText(fullPath, current, Encoding.UTF8);
                var successMsg = $"{relPath}: patched ({replacementCount} replacements)";
                if (errors.Count > 0)
                    successMsg += $"\n  ? {errors.Count} issue(s):\n    " + string.Join("\n    ", errors);
                if (!string.IsNullOrWhiteSpace(diff))
                    successMsg += $"\n{diff}";
                summaries.Add(successMsg);
                _logger.LogInformation("[FilePatchTool] path={Path} replacements={Replacements}", fullPath, replacementCount);
                _audit.Write(zone2, "file_patch", context.AgentInstanceId,
                    patch.Path, args.Reason, true, sw.ElapsedMilliseconds, context.Trace);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FilePatchTool] failed path={Path}", fullPath);
                _audit.Write(zone2, "file_patch", context.AgentInstanceId,
                    patch.Path, args.Reason, false, sw.ElapsedMilliseconds, context.Trace);
                return Task.FromResult(ToolExecutionResult.Fail($"Failed to patch file '{patch.Path}': {ex.Message}"));
            }
        }

        return Task.FromResult(ToolExecutionResult.Ok(string.Join(Environment.NewLine, summaries)));
    }

    private ToolExecutionResult ApplyUnifiedDiffPatch(FilePatchArgs args, ToolExecutionContext context)
    {
        return UnifiedDiffPatchRunner.Apply(
            args.PatchText!,
            args.Reason,
            args.DryRun,
            context,
            _dataPaths,
            _audit,
            "file_patch");
    }

    private static PuddingDataPaths CreateDefaultDataPaths() =>
        PuddingDataPaths.FromRoot(Path.Combine(HostFileToolPaths.WorkspaceRoot, "temp", "tool-audit-data"));

    private static IEnumerable<FilePatchItem> ResolvePatches(FilePatchArgs args)
    {
        if (args.Patches is { Count: > 0 })
            return args.Patches;

        if (!string.IsNullOrWhiteSpace(args.Path) && args.Operations is { Count: > 0 })
            return [new FilePatchItem { Path = args.Path, Operations = args.Operations }];

        return [];
    }

    /// <summary>�ռ����� replace ������ԭʼ�����е�ƥ�䣨�������ţ���</summary>
    private static List<(int Index, int Length, string NewText)> CollectReplacements(
        string original, IReadOnlyList<FilePatchOperation> ops, ref int replacementCount,
        List<string> errors, string relPath, int? scopeStartLine, int? scopeEndLine)
    {
        var matches = new List<(int Index, int Length, string NewText)>();
        var usedRanges = new SortedSet<(int Start, int End)>();

        int scopeStart = 0, scopeEnd = original.Length;
        if (scopeStartLine.HasValue || scopeEndLine.HasValue)
        {
            var slines = original.Replace("\r\n", "\n").Split('\n');
            if (scopeStartLine.HasValue && scopeStartLine.Value > 0)
                scopeStart = slines.Take(scopeStartLine.Value - 1).Sum(l => l.Length + 1);
            if (scopeEndLine.HasValue && scopeEndLine.Value <= slines.Length)
                scopeEnd = slines.Take(scopeEndLine.Value).Sum(l => l.Length + 1);
        }

        foreach (var op in ops)
        {
            var type = (op.Type ?? "replace").Trim();
            if (type.Contains("regex", StringComparison.OrdinalIgnoreCase)) continue;
            if (!type.Equals("replace", StringComparison.OrdinalIgnoreCase)) continue;

            var oldText = op.OldText ?? "";
            var newText = op.NewText ?? "";
            if (string.IsNullOrEmpty(oldText))
            {
                errors.Add("replace operation requires 'old_text' �� skipped.");
                continue;
            }

            var candidates = FindReplacementCandidates(original, oldText)
                .Where(match => match.Index >= scopeStart && match.Index + match.Length <= scopeEnd)
                .Where(match => !usedRanges.Any(r => match.Index < r.End && match.Index + match.Length > r.Start))
                .ToList();

            if (candidates.Count == 0)
            {
                var snippet = oldText.Length > 80 ? oldText[..80] + "..." : oldText;
                var closest = FindClosestMatch(original, oldText);
                var hint = "";
                if (closest is not null)
                {
                    var c = closest.Value;
                    hint = $" \u2014 Closest match (L{c.line}, {c.similarity:P0} similar): \"{c.text}\"";
                    if (!string.IsNullOrEmpty(c.beforeContext))
                        hint += $"\n  \u2191 before: \"{c.beforeContext}\"";
                    if (!string.IsNullOrEmpty(c.afterContext))
                        hint += $"\n  \u2193 after:  \"{c.afterContext}\"";
                }
                errors.Add($"old_string not found in {relPath}: \"{snippet.Replace("\n", "\\n").Replace("\r", "\\r")}\"{hint}");
                continue;
            }

            if (op.ReplaceAll == true)
            {
                foreach (var candidate in candidates)
                {
                    replacementCount++;
                    matches.Add((candidate.Index, candidate.Length, newText));
                    usedRanges.Add((candidate.Index, candidate.Index + candidate.Length));
                }
                continue;
            }

            if (candidates.Count > 1)
            {
                var locations = string.Join(", ", candidates.Take(5).Select(c => $"L{GetLineNumberOf(original, c.Index)} ({c.Strategy})"));
                errors.Add($"old_string matches {candidates.Count} locations in {relPath}: {locations}. Use scope_start_line/scope_end_line or add more context to old_text.");
                continue;
            }

            var match = candidates[0];
            replacementCount++;
            matches.Add((match.Index, match.Length, newText));
            usedRanges.Add((match.Index, match.Index + match.Length));
            if (!match.Strategy.Equals("exact", StringComparison.Ordinal))
                errors.Add($"\u2139 old_string matched in {relPath} at L{GetLineNumberOf(original, match.Index)} using {match.Strategy} matching.");
        }
        return matches;
    }

    private static IReadOnlyList<TextMatch> FindReplacementCandidates(string original, string oldText)
    {
        var exact = FindLiteralMatches(original, oldText, "exact");
        if (exact.Count > 0)
            return exact;

        // 歧义检测：归一化后 old_text 在原文中 ≥2 处匹配 → 拒绝模糊匹配
        if (IsAmbiguousBlockPattern(oldText, original))
            return [];

        var whitespace = FindNormalizedMatches(
            original,
            oldText,
            "whitespace-tolerant",
            static c => char.IsWhiteSpace(c) ? null : c.ToString());
        if (whitespace.Count > 0)
            return whitespace;

        return FindNormalizedMatches(
            original,
            oldText,
            "punctuation-normalized",
            NormalizePunctuationChar);
    }

    private static bool IsAmbiguousBlockPattern(string oldText, string original)
    {
        // 归一化后统计 catch 块数量
        var normalized = Regex.Replace(oldText, @"\s+", " ");
        var catchCount = Regex.Matches(normalized, @"\bcatch\s*\(").Count;
        var closeCount = Regex.Matches(
            normalized, @"\}\s*(catch|finally|try|else|namespace|class|struct)\b").Count;

        if (catchCount >= 2 || closeCount >= 2)
            return true;

        // 归一化 old_text 在原文中的匹配次数 ≥2 → 歧义
        var normOld = Regex.Replace(oldText, @"\s+", " ").Trim();
        var normOrg = Regex.Replace(original, @"\s+", " ");
        var cnt = 0;
        var idx = 0;
        while ((idx = normOrg.IndexOf(normOld, idx, StringComparison.Ordinal)) >= 0)
        {
            if (++cnt >= 2) return true;
            idx += normOld.Length;
        }

        return false;
    }

    private static List<TextMatch> FindLiteralMatches(string original, string oldText, string strategy)
    {
        var matches = new List<TextMatch>();
        var start = 0;
        while (start <= original.Length)
        {
            var idx = original.IndexOf(oldText, start, StringComparison.Ordinal);
            if (idx < 0)
                break;

            matches.Add(new TextMatch(idx, oldText.Length, strategy));
            start = idx + Math.Max(1, oldText.Length);
        }

        return matches;
    }

    private static List<TextMatch> FindNormalizedMatches(
        string original,
        string oldText,
        string strategy,
        Func<char, string?> normalize)
    {
        var source = BuildNormalizedIndex(original, normalize);
        var search = BuildNormalizedIndex(oldText, normalize);
        if (string.IsNullOrEmpty(search.Text) || string.IsNullOrEmpty(source.Text))
            return [];

        var matches = new List<TextMatch>();
        var start = 0;
        while (start <= source.Text.Length)
        {
            var idx = source.Text.IndexOf(search.Text, start, StringComparison.Ordinal);
            if (idx < 0)
                break;

            var end = idx + search.Text.Length - 1;
            if (idx < source.OriginalIndexes.Count && end < source.OriginalIndexes.Count)
            {
                var originalStart = source.OriginalIndexes[idx];
                var originalEnd = source.OriginalIndexes[end] + 1;
                matches.Add(new TextMatch(originalStart, originalEnd - originalStart, strategy));
            }

            start = idx + Math.Max(1, search.Text.Length);
        }

        return matches;
    }

    private static NormalizedTextIndex BuildNormalizedIndex(string value, Func<char, string?> normalize)
    {
        var text = new StringBuilder();
        var indexes = new List<int>();

        for (var i = 0; i < value.Length; i++)
        {
            var normalized = normalize(value[i]);
            if (string.IsNullOrEmpty(normalized))
                continue;

            foreach (var c in normalized)
            {
                text.Append(c);
                indexes.Add(i);
            }
        }

        return new NormalizedTextIndex(text.ToString(), indexes);
    }

    private static string? NormalizePunctuationChar(char c)
    {
        if (char.IsWhiteSpace(c))
            return null;

        return c switch
        {
            '\u2018' or '\u2019' or '\u201A' or '\u201B' => "'",
            '\u201C' or '\u201D' or '\u201E' or '\u201F' => "\"",
            '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2212' => "-",
            '\u2026' => "...",
            '\u00A0' => null,
            _ => c.ToString(),
        };
    }

    /// <summary>�Ӻ���ǰӦ���滻������������ȷ����</summary>
    private static string ApplyReplacements(string original, List<(int Index, int Length, string NewText)> replacements)
    {
        if (replacements.Count == 0) return original;
        replacements.Sort((a, b) => b.Index.CompareTo(a.Index));
        var result = original;
        foreach (var (idx, len, newTxt) in replacements)
            result = result.Remove(idx, len).Insert(idx, newTxt);
        return result;
    }

    private static string ApplyRegexOperation(string input, FilePatchOperation op, ref int replacementCount)
    {
                if (string.IsNullOrEmpty(op.Pattern))
            throw new InvalidOperationException("regexReplace operation requires 'pattern'. Pre-validation should have caught this.");

        var options = RegexOptions.None;
        if (op.Options?.Contains("ignoreCase", StringComparison.OrdinalIgnoreCase) == true)
            options |= RegexOptions.IgnoreCase;
        if (op.Options?.Contains("multiline", StringComparison.OrdinalIgnoreCase) == true)
            options |= RegexOptions.Multiline;
        if (op.Options?.Contains("singleline", StringComparison.OrdinalIgnoreCase) == true)
            options |= RegexOptions.Singleline;

        var count = 0;
        var replacement = op.Replacement ?? string.Empty;
        var output = Regex.Replace(input, op.Pattern, match =>
        {
            count++;
            return match.Result(replacement);
        }, options);
        replacementCount += count;
        return output;
    }

    /// <summary>Ӧ���в�����insert/delete/replace_lines�����Ӻ���ǰ����ǰ����У�顣</summary>
    private static string ApplyLineOperations(string input, IReadOnlyList<FilePatchOperation> ops,
        ref int replacementCount, List<string> errors, string relPath)
    {
        var fileLines = input.Replace("\r\n", "\n").Split('\n').ToList();
        var lineOps = new List<(int StartLine, int EndLine, string NewText, string Action,
            string? AnchorBefore, string? AnchorAfter)>();

        foreach (var op in ops)
        {
            var type = (op.Type ?? "").Trim().ToLowerInvariant();
            if (type != "insert" && type != "delete" && type != "replace_lines")
                continue;

            var startLine = op.StartLine ?? 0;
            var endLine = op.EndLine ?? startLine;
            var newText = op.NewText ?? "";

            // ǿ��ê�㽨�飨��������ê��ִ�У������棩
            if (string.IsNullOrWhiteSpace(op.AnchorBefore) && string.IsNullOrWhiteSpace(op.AnchorAfter))
            {
                errors.Add($"\u26a0 line operation '{type}' in {relPath}: no anchor_before or anchor_after provided. " +
                    "Line numbers may be stale �� provide anchor_before and anchor_after for safe auto-correction.");
            }

            lineOps.Add((startLine, endLine, newText, type,
                op.AnchorBefore, op.AnchorAfter));
        }

        if (lineOps.Count == 0) return input;

        // ��׼�����������Կհײ��ɼ��ַ���
        static string NormLine(string s) =>
            new string(s.Where(c => !char.IsWhiteSpace(c) || c == ' ').ToArray()).Trim();

        // Null-or-whitespace �� null
        static string? NormOrNull(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : NormLine(s);

        // �Ӻ���ǰִ�У������к�������ȷ��
        lineOps.Sort((a, b) => b.StartLine.CompareTo(a.StartLine));
        foreach (var (sl, el, newText, action, anchorBefore, anchorAfter) in lineOps)
        {
            var normBefore = NormOrNull(anchorBefore);
            var normAfter = NormOrNull(anchorAfter);

            // �T�T�T Level 1: λ��ƥ�䣨��·�����T�T�T
            int resolvedStart = sl;
            int resolvedEnd = el;
            bool useOriginalPosition = true;

            if (normBefore != null || normAfter != null)
            {
                var beforeMatch = true;
                var afterMatch = true;

                if (normBefore != null)
                {
                    if (sl < 1 || sl > fileLines.Count) beforeMatch = false;
                    else beforeMatch = NormLine(fileLines[sl - 1]) == normBefore;
                }
                if (normAfter != null)
                {
                    if (el < 0 || el >= fileLines.Count) afterMatch = false;
                    else afterMatch = NormLine(fileLines[el]) == normAfter;
                }

                if (!beforeMatch || !afterMatch)
                {
                    useOriginalPosition = false;
                    // �T�T�T Level 2: ȫ������������·�����T�T�T
                    var candidates = new List<int>();

                    for (int i = 0; i < fileLines.Count; i++)
                    {
                        var bc = normBefore == null || (i > 0 && NormLine(fileLines[i - 1]) == normBefore);
                        var ac = normAfter == null || (i < fileLines.Count - 1 && NormLine(fileLines[i]) == normAfter);

                        if (bc && ac)
                            candidates.Add(i + 1); // 1-based line: anchor_after �����о���Ŀ������
                    }

                    if (candidates.Count == 1)
                    {
                        resolvedStart = candidates[0];
                        resolvedEnd = action == "insert" ? resolvedStart : resolvedStart + (el - sl);
                        errors.Add($"\u2139 line operation '{action}' in {relPath}: line shifted from L{sl} to L{resolvedStart} �� auto-corrected");
                        useOriginalPosition = true;
                    }
                    else if (candidates.Count > 1)
                    {
                        errors.Add($"line operation '{action}' in {relPath}: anchors matched at {candidates.Count} positions " +
                            $"(L{string.Join(", L", candidates.Take(5))}). Provide more specific anchors to disambiguate.");
                        continue;
                    }
                    else
                    {
                        // �T�T�T Level 3: ʧ�ܱ��棨LCS ��ӽ�ƥ�䣩�T�T�T
                        var details = new List<string>();
                        if (normBefore != null)
                        {
                            // Find closest match for anchor_before
                            var bestBefore = FindClosestLine(fileLines, anchorBefore!);
                            if (bestBefore != null)
                                details.Add($"  anchor_before closest: L{bestBefore.Value.line} \"{bestBefore.Value.text.Trim()}\" ({bestBefore.Value.similarity:P0} similar)");
                        }
                        if (normAfter != null)
                        {
                            var bestAfter = FindClosestLine(fileLines, anchorAfter!);
                            if (bestAfter != null)
                                details.Add($"  anchor_after closest: L{bestAfter.Value.line} \"{bestAfter.Value.text.Trim()}\" ({bestAfter.Value.similarity:P0} similar)");
                        }
                        var detailStr = details.Count > 0 ? "\n" + string.Join("\n", details) : "";
                        errors.Add($"line operation '{action}' in {relPath}: anchors not found in current file �� file may have changed significantly, re-read and try again.{detailStr}");
                        continue;
                    }
                }
            }

            if (!useOriginalPosition) continue;

            // �T�T�T �к���Ч�Լ�� �T�T�T
            if (resolvedStart < 0 || resolvedStart > fileLines.Count)
            {
                errors.Add($"line operation '{action}' failed in {relPath}: start_line {resolvedStart} out of range (file has {fileLines.Count} lines)");
                continue;
            }
            if (resolvedEnd < resolvedStart || resolvedEnd > fileLines.Count)
            {
                errors.Add($"line operation '{action}' failed in {relPath}: end_line {resolvedEnd} out of range");
                continue;
            }

            var insertedLines = string.IsNullOrEmpty(newText) ? Array.Empty<string>()
                : newText.Replace("\r\n", "\n").Split('\n');

            if (action == "insert")
            {
                var insertAt = resolvedStart >= fileLines.Count ? fileLines.Count : resolvedStart;
                fileLines.InsertRange(insertAt, insertedLines);
            }
            else
            {
                var count = resolvedEnd - resolvedStart + 1;
                fileLines.RemoveRange(resolvedStart - 1, count);
                if (action == "replace_lines")
                    fileLines.InsertRange(resolvedStart - 1, insertedLines);
            }
            replacementCount++;
        }

        return string.Join("\n", fileLines);
    }

    /// <summary>�����б��в�����Ŀ���ı������Ƶ��У�LCS ���ʣ���</summary>
    private static (int line, string text, double similarity)? FindClosestLine(List<string> lines, string target)
    {
        if (string.IsNullOrEmpty(target) || lines.Count == 0) return null;
        var normTarget = new string(target.Where(c => !char.IsWhiteSpace(c) || c == ' ').ToArray()).Trim();
        var bestSim = 0.0;
        var bestIdx = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            var normLine = new string(lines[i].Where(c => !char.IsWhiteSpace(c) || c == ' ').ToArray()).Trim();
            var sim = LcsSimilarity(normLine, normTarget);
            if (sim > bestSim)
            {
                bestSim = sim;
                bestIdx = i;
            }
        }
        return bestIdx >= 0 ? (bestIdx + 1, lines[bestIdx], bestSim) : null;
    }

    /// <summary>���� LCS ���ȵ��ı����ƶȣ�0.0 ~ 1.0����</summary>
    private static double LcsSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        var lcsLen = LongestCommonSubsequenceLength(a, b);
        return (double)lcsLen / Math.Max(a.Length, b.Length);
    }

    /// <summary>���������ַ���������������г��ȣ���̬�滮�Ż��棩��</summary>
    private static int LongestCommonSubsequenceLength(string a, string b)
    {
        if (a.Length > b.Length) (a, b) = (b, a); // ȷ�� a ����
        var prev = new int[a.Length + 1];
        for (int i = 1; i <= b.Length; i++)
        {
            var curr = new int[a.Length + 1];
            for (int j = 1; j <= a.Length; j++)
            {
                if (b[i - 1] == a[j - 1])
                    curr[j] = prev[j - 1] + 1;
                else
                    curr[j] = Math.Max(prev[j], curr[j - 1]);
            }
            prev = curr;
        }
        return prev[a.Length];
    }

    private static string GenerateSimpleDiff(string original, string current)
    {
        var sb = new StringBuilder();
        var oldLines = original.Split('\n');
        var newLines = current.Split('\n');
        int maxShow = 10, shown = 0;
        for (int i = 0; i < Math.Max(oldLines.Length, newLines.Length) && shown < maxShow; i++)
        {
            var o = i < oldLines.Length ? oldLines[i].TrimEnd('\r') : null;
            var n = i < newLines.Length ? newLines[i].TrimEnd('\r') : null;
            if (o == n) continue;
            shown++;
            if (o != null) sb.AppendLine($"- {o}");
            if (n != null) sb.AppendLine($"+ {n}");
        }
        if (shown >= maxShow) sb.AppendLine("... (more changes)");
        return sb.Length > 0 ? sb.ToString() : "(no visible line changes)";
    }

    private static int GetLineNumberOf(string content, int charIndex)
    {
        int pos = 0;
        var ln = content.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < ln.Length; i++)
        {
            pos += ln[i].Length + 1;
            if (pos > charIndex) return i + 1;
        }
        return ln.Length;
    }

    private static (int line, string text, double similarity, string beforeContext, string afterContext)? FindClosestMatch(string fileContent, string searchText)
    {
        if (string.IsNullOrEmpty(searchText) || searchText.Length < 3) return null;
        var clines = fileContent.Replace("\r\n", "\n").Split('\n');
        var searchLines = searchText.Split('\n');
        var bestSim = 0.0;
        var bestStartLine = 0;
        var bestText = "";
        var bestBefore = "";
        var bestAfter = "";

        int maxWindow = Math.Min(searchLines.Length + 10, Math.Min(clines.Length, 20));
        for (int windowSize = Math.Max(searchLines.Length, 1); windowSize <= maxWindow; windowSize++)
        {
            for (int startLine = 0; startLine <= clines.Length - windowSize; startLine++)
            {
                var endLine = startLine + windowSize - 1;
                var window = string.Join("\n", clines[startLine..(endLine + 1)]);
                if (window.Length < searchText.Length / 3) continue;
                var sim = LongestCommonSubstringRatio(window, searchText);
                if (sim > bestSim)
                {
                    bestSim = sim;
                    bestStartLine = startLine + 1;
                    bestText = window.Length > 80 ? window[..80].TrimEnd() + "..." : window.TrimEnd();
                    bestBefore = startLine > 0 ? string.Join("\n", clines[Math.Max(0, startLine - 3)..startLine]) : "";
                    bestAfter = endLine < clines.Length - 1 ? string.Join("\n", clines[(endLine + 1)..Math.Min(clines.Length, endLine + 4)]) : "";
                }
            }
        }
        return bestSim > 0 ? (bestStartLine, bestText, bestSim, bestBefore, bestAfter) : null;
    }

    private static double LongestCommonSubstringRatio(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        var maxLen = 0;
        var prev = new int[b.Length + 1];
        for (int i = 0; i < a.Length; i++)
        {
            var curr = new int[b.Length + 1];
            for (int j = 0; j < b.Length; j++)
            {
                if (char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[j]))
                {
                    curr[j + 1] = prev[j] + 1;
                    if (curr[j + 1] > maxLen) maxLen = curr[j + 1];
                }
            }
            prev = curr;
        }
        return (double)maxLen / Math.Max(a.Length, b.Length);
    }
}

public sealed record FilePatchArgs
{
    [ToolParam("Single file path to patch.")]
    public string? Path { get; init; }

    [ToolParam("Unified diff text to apply transactionally. When set, path/operations are ignored and dry_run still defaults to true.")]
    [JsonPropertyName("patch_text")]
    public string? PatchText { get; init; }

    [ToolParam("Operations for the single file patch.")]
    public IReadOnlyList<FilePatchOperation>? Operations { get; init; }

    [ToolParam("Batch patches. Each item has path and operations.")]
    public IReadOnlyList<FilePatchItem>? Patches { get; init; }

    [ToolParam("Reason for patching. Required when patching agent private files.")]
    public string? Reason { get; init; }

    [ToolParam("If true, return diff preview without modifying files. Default: true (mandatory preview �� set false to actually apply changes).")]
    public bool? DryRun { get; init; }

    [ToolParam("Optional 1-based start line to scope text replacements within this region.")]
    public int? ScopeStartLine { get; init; }

    [ToolParam("Optional 1-based end line to scope text replacements within this region.")]
    public int? ScopeEndLine { get; init; }
}

internal sealed record TextMatch(int Index, int Length, string Strategy);

internal sealed record NormalizedTextIndex(string Text, IReadOnlyList<int> OriginalIndexes);

internal sealed record UnifiedDiffParseResult(bool Success, IReadOnlyList<UnifiedDiffFile> Files, string? Error)
{
    public static UnifiedDiffParseResult Ok(IReadOnlyList<UnifiedDiffFile> files) => new(true, files, null);
    public static UnifiedDiffParseResult Fail(string error) => new(false, [], error);
}

internal sealed record UnifiedDiffApplyResult(bool Success, string? Content, string? Error)
{
    public static UnifiedDiffApplyResult Ok(string content) => new(true, content, null);
    public static UnifiedDiffApplyResult Fail(string error) => new(false, null, error);
}

internal sealed record UnifiedDiffFile(string Path, IReadOnlyList<UnifiedDiffHunk> Hunks);

internal sealed record UnifiedDiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<UnifiedDiffLine> Lines);

internal sealed record UnifiedDiffLine(char Kind, string Text);

internal static class UnifiedDiffPatchRunner
{
    public static ToolExecutionResult Apply(
        string patchText,
        string? reason,
        bool? dryRun,
        ToolExecutionContext context,
        PuddingDataPaths dataPaths,
        AuditLogger audit,
        string toolId)
    {
        var parsed = UnifiedDiffParser.Parse(patchText);
        if (!parsed.Success)
            return ToolExecutionResult.Fail(parsed.Error ?? "Invalid unified diff.");

        var touchedFiles = new List<(UnifiedDiffFile Patch, string FullPath, string Original, string Current, OperationZone Zone)>();
        foreach (var patch in parsed.Files)
        {
            if (!HostFileToolPaths.TryResolveInsideWorkspace(patch.Path, out var fullPath, out var resolveError, skipWorkspaceCheck: context.IsYoloMode))
            {
                audit.Write(OperationZone.External, toolId, context.AgentInstanceId,
                    patch.Path, reason, false, 0, context.Trace);
                return ToolExecutionResult.Fail(resolveError);
            }

            if (!File.Exists(fullPath))
            {
                var zone = OperationZoneClassifier.ClassifyPath(
                    fullPath, dataPaths, context.WorkspaceId, context.AgentInstanceId);
                audit.Write(zone, toolId, context.AgentInstanceId,
                    patch.Path, reason, false, 0, context.Trace);
                return ToolExecutionResult.Fail($"File not found: {patch.Path}");
            }

            var fileZone = OperationZoneClassifier.ClassifyPath(
                fullPath, dataPaths, context.WorkspaceId, context.AgentInstanceId);
            if (fileZone == OperationZone.AgentPrivate && string.IsNullOrWhiteSpace(reason))
            {
                audit.Write(fileZone, toolId, context.AgentInstanceId,
                    patch.Path, reason, false, 0, context.Trace);
                return ToolExecutionResult.Fail(
                    "Patching agent private files requires a 'reason' parameter. Please explain the purpose of this patch.");
            }

            var original = File.ReadAllText(fullPath, Encoding.UTF8);
            var applyResult = UnifiedDiffApplier.Apply(original, patch);
            if (!applyResult.Success)
            {
                audit.Write(fileZone, toolId, context.AgentInstanceId,
                    patch.Path, reason, false, 0, context.Trace);
                return ToolExecutionResult.Fail(applyResult.Error ?? $"Failed to apply patch to {patch.Path}");
            }

            touchedFiles.Add((patch, fullPath, original, applyResult.Content!, fileZone));
        }

        var isDryRun = dryRun != false;
        var summaries = touchedFiles
            .Select(file => $"{Path.GetRelativePath(HostFileToolPaths.WorkspaceRoot, file.FullPath)}: {(isDryRun ? "preview" : "patched")}\n{GenerateSimpleDiff(file.Original, file.Current)}")
            .ToArray();

        if (isDryRun)
            return ToolExecutionResult.Ok(string.Join(Environment.NewLine, summaries));

        var sw = Stopwatch.StartNew();
        var backups = new List<(string FullPath, string BackupPath, OperationZone Zone, string RequestedPath)>();
        var tempFiles = new List<string>();
        try
        {
            foreach (var file in touchedFiles)
            {
                var backup = file.FullPath + ".bak." + Guid.NewGuid().ToString("N")[..8];
                var temp = file.FullPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
                File.Copy(file.FullPath, backup, overwrite: false);
                File.WriteAllText(temp, file.Current, Encoding.UTF8);
                backups.Add((file.FullPath, backup, file.Zone, file.Patch.Path));
                tempFiles.Add(temp);
            }

            for (var i = 0; i < touchedFiles.Count; i++)
                File.Move(tempFiles[i], touchedFiles[i].FullPath, overwrite: true);

            foreach (var file in touchedFiles)
            {
                audit.Write(file.Zone, toolId, context.AgentInstanceId,
                    file.Patch.Path, reason, true, sw.ElapsedMilliseconds, context.Trace);
            }

            return ToolExecutionResult.Ok(string.Join(Environment.NewLine, summaries));
        }
        catch (Exception ex)
        {
            foreach (var backup in backups)
            {
                try
                {
                    if (File.Exists(backup.BackupPath))
                        File.Move(backup.BackupPath, backup.FullPath, overwrite: true);
                    audit.Write(backup.Zone, toolId, context.AgentInstanceId,
                        backup.RequestedPath, reason, false, sw.ElapsedMilliseconds, context.Trace);
                }
                catch
                {
                    // Best-effort rollback; the original exception is returned below.
                }
            }

            return ToolExecutionResult.Fail($"Failed to write unified patch transaction: {ex.Message}");
        }
        finally
        {
            foreach (var temp in tempFiles)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
            foreach (var backup in backups)
            {
                try { if (File.Exists(backup.BackupPath)) File.Delete(backup.BackupPath); } catch { }
            }
        }
    }

    private static string GenerateSimpleDiff(string original, string current)
    {
        var sb = new StringBuilder();
        var oldLines = original.Split('\n');
        var newLines = current.Split('\n');
        int maxShow = 10, shown = 0;
        for (int i = 0; i < Math.Max(oldLines.Length, newLines.Length) && shown < maxShow; i++)
        {
            var o = i < oldLines.Length ? oldLines[i].TrimEnd('\r') : null;
            var n = i < newLines.Length ? newLines[i].TrimEnd('\r') : null;
            if (o == n) continue;
            shown++;
            if (o != null) sb.AppendLine($"- {o}");
            if (n != null) sb.AppendLine($"+ {n}");
        }
        if (shown >= maxShow) sb.AppendLine("... (more changes)");
        return sb.Length > 0 ? sb.ToString() : "(no visible line changes)";
    }
}

internal static class UnifiedDiffParser
{
    private static readonly Regex s_hunkHeader = new(
        @"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static UnifiedDiffParseResult Parse(string patchText)
    {
        var lines = patchText.Replace("\r\n", "\n").Split('\n');
        var files = new List<UnifiedDiffFile>();
        string? currentPath = null;
        var hunks = new List<UnifiedDiffHunk>();
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                if (currentPath is not null)
                {
                    files.Add(new UnifiedDiffFile(currentPath, hunks));
                    hunks = [];
                }

                if (i + 1 >= lines.Length || !lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
                    return UnifiedDiffParseResult.Fail("Unified diff file header must include matching +++ line.");

                currentPath = CleanDiffPath(lines[i + 1][4..].Trim());
                if (string.IsNullOrWhiteSpace(currentPath) || currentPath == "/dev/null")
                    return UnifiedDiffParseResult.Fail("Unified diff creates new files or uses an empty target path; file_patch patch_text currently patches existing files only.");

                i += 2;
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (currentPath is null)
                    return UnifiedDiffParseResult.Fail("Unified diff hunk appeared before a file header.");

                var match = s_hunkHeader.Match(line);
                if (!match.Success)
                    return UnifiedDiffParseResult.Fail($"Invalid unified diff hunk header: {line}");

                var hunkLines = new List<UnifiedDiffLine>();
                i++;
                while (i < lines.Length &&
                       !lines[i].StartsWith("@@ ", StringComparison.Ordinal) &&
                       !lines[i].StartsWith("--- ", StringComparison.Ordinal))
                {
                    var hunkLine = lines[i];
                    if (hunkLine.Length == 0)
                    {
                        hunkLines.Add(new UnifiedDiffLine(' ', string.Empty));
                        i++;
                        continue;
                    }

                    var kind = hunkLine[0];
                    if (kind is not (' ' or '+' or '-'))
                    {
                        if (hunkLine.StartsWith("\\ No newline at end of file", StringComparison.Ordinal))
                        {
                            i++;
                            continue;
                        }

                        return UnifiedDiffParseResult.Fail($"Invalid unified diff hunk line: {hunkLine}");
                    }

                    hunkLines.Add(new UnifiedDiffLine(kind, hunkLine[1..]));
                    i++;
                }

                hunks.Add(new UnifiedDiffHunk(
                    int.Parse(match.Groups["oldStart"].Value),
                    ParseOptionalCount(match.Groups["oldCount"].Value),
                    int.Parse(match.Groups["newStart"].Value),
                    ParseOptionalCount(match.Groups["newCount"].Value),
                    hunkLines));
                continue;
            }

            i++;
        }

        if (currentPath is not null)
            files.Add(new UnifiedDiffFile(currentPath, hunks));

        if (files.Count == 0)
            return UnifiedDiffParseResult.Fail("No files found in unified diff.");
        if (files.Any(f => f.Hunks.Count == 0))
            return UnifiedDiffParseResult.Fail("Unified diff file has no hunks.");

        return UnifiedDiffParseResult.Ok(files);
    }

    private static int ParseOptionalCount(string value) =>
        string.IsNullOrWhiteSpace(value) ? 1 : int.Parse(value);

    private static string CleanDiffPath(string path)
    {
        var tabIndex = path.IndexOf('\t');
        if (tabIndex >= 0)
            path = path[..tabIndex];
        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
            path = path[2..];
        return path.Trim();
    }
}

internal static class UnifiedDiffApplier
{
    public static UnifiedDiffApplyResult Apply(string original, UnifiedDiffFile patch)
    {
        var current = original.Replace("\r\n", "\n");
        foreach (var hunk in patch.Hunks)
        {
            var expected = hunk.Lines
                .Where(line => line.Kind is ' ' or '-')
                .Select(line => line.Text)
                .ToArray();
            var replacement = hunk.Lines
                .Where(line => line.Kind is ' ' or '+')
                .Select(line => line.Text)
                .ToArray();

            var currentLines = current.Split('\n').ToList();
            var candidates = FindHunkCandidates(currentLines, expected, hunk.OldStart);
            if (candidates.Count == 0)
                return UnifiedDiffApplyResult.Fail($"Hunk for {patch.Path} starting at old line {hunk.OldStart} did not match. Re-read the file and regenerate the patch.");
            if (candidates.Count > 1)
                return UnifiedDiffApplyResult.Fail($"Hunk for {patch.Path} starting at old line {hunk.OldStart} matched {candidates.Count} locations. Add more context lines.");

            var index = candidates[0];
            currentLines.RemoveRange(index, expected.Length);
            currentLines.InsertRange(index, replacement);
            current = string.Join("\n", currentLines);
        }

        if (original.Contains("\r\n", StringComparison.Ordinal))
            current = current.Replace("\n", "\r\n");

        return UnifiedDiffApplyResult.Ok(current);
    }

    private static List<int> FindHunkCandidates(IReadOnlyList<string> lines, IReadOnlyList<string> expected, int oldStart)
    {
        var candidates = new List<int>();
        var preferred = Math.Max(0, oldStart - 1);
        if (MatchesAt(lines, expected, preferred))
            candidates.Add(preferred);

        for (var i = 0; i <= lines.Count - expected.Count; i++)
        {
            if (i == preferred)
                continue;
            if (MatchesAt(lines, expected, i))
                candidates.Add(i);
        }

        return candidates;
    }

    private static bool MatchesAt(IReadOnlyList<string> lines, IReadOnlyList<string> expected, int index)
    {
        if (index < 0 || index + expected.Count > lines.Count)
            return false;

        for (var i = 0; i < expected.Count; i++)
        {
            if (!string.Equals(lines[index + i], expected[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}

public sealed record FilePatchItem
{
    [ToolParam("File path to patch.")]
    public required string Path { get; init; }

    [ToolParam("Patch operations.")]
    public required IReadOnlyList<FilePatchOperation> Operations { get; init; }
}

public sealed record FilePatchOperation
{
    [ToolParam("Operation type: replace, insert, delete, replace_lines, or regexReplace.")]
    public string? Type { get; init; }

    [ToolParam("Text to replace for replace operations.")]
    [JsonPropertyName("old_text")]
    public string? OldText { get; init; }

    [ToolParam("Replacement text for replace/replace_lines/insert operations.")]
    [JsonPropertyName("new_text")]
    public string? NewText { get; init; }

    [ToolParam("Replace every occurrence when true; otherwise only the first occurrence.")]
    [JsonPropertyName("replace_all")]
    public bool? ReplaceAll { get; init; }

    [ToolParam("1-based start line for insert/delete/replace_lines operations.")]
    public int? StartLine { get; init; }

    [ToolParam("1-based end line (inclusive) for delete/replace_lines operations.")]
    public int? EndLine { get; init; }

    [ToolParam("Regex pattern for regexReplace operations.")]
    public string? Pattern { get; init; }

    [ToolParam("Regex replacement text.")]
    public string? Replacement { get; init; }

    [ToolParam("Regex options: ignoreCase|multiline|singleline.")]
    public string? Options { get; init; }

    [ToolParam("Full content of the line immediately BEFORE the target range (whitespace-insensitive match). Used to verify correct line number and auto-correct if file shifted.")]
    [JsonPropertyName("anchor_before")]
    public string? AnchorBefore { get; init; }

    [ToolParam("Full content of the line immediately AFTER the target range (whitespace-insensitive match).")]
    [JsonPropertyName("anchor_after")]
    public string? AnchorAfter { get; init; }
}
