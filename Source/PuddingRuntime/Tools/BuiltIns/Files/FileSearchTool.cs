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

        string providerId;
        IFileSearchProvider? provider;

        if (!string.IsNullOrWhiteSpace(args.Provider))
        {
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
        }

        return Path.GetPathRoot(Directory.GetCurrentDirectory()) ?? Path.GetFullPath(Path.DirectorySeparatorChar.ToString());
    }
}

public sealed record FileSearchArgs
{
    [ToolParam("Action to perform: list or search. Default: search.")]
    public string? Action { get; init; }

    [ToolParam("File search provider id. Default: auto-select Everything (fast) or BuiltInRecursiveFileSearch (slow fallback). Supported providers: Everything, BuiltInRecursiveFileSearch. Use action=list to inspect availability.")]
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
