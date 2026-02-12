using System.Text;
using System.Text.RegularExpressions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Tools;
using PuddingFullTextIndex.Contracts;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// SearchGrepTool — 工作区文件 grep 搜索工具。
/// 策略链：Lucene 全文索引 (ms) → 纯C#托管 grep (带预算)。
/// </summary>
[Tool(
    id: "search_grep",
    name: "search_grep",
    description: "在指定目录的代码文件中搜索指定文本。支持正则表达式。可选参数 pattern 过滤文件名（如 \"*.cs\"），file_ext 过滤扩展名（如 \"cs;ts\"），directory 限定搜索目录，exclude_dirs 排除子目录（默认 bin;obj;node_modules;.git）。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
public sealed class SearchGrepTool : PuddingToolBase<SearchGrepArgs>
{
    private readonly ILogger<SearchGrepTool> _logger;
    private readonly IFullTextSearchEngine _searchEngine;
    private readonly ITelemetryMetricSink? _telemetry;

    private const int DefaultMaxResults = 30;
    private const long MaxFileSizeBytes = 1 * 1024 * 1024;
    private const string DefaultExcludeDirs = "bin;obj;node_modules;.git";

    private static readonly TimeSpan ManagedSearchTimeout = TimeSpan.FromSeconds(10);
    private const int MaxEnumeratedFiles = 2000;
    private const int MaxScannedFiles = 1000;
    private const long MaxScannedBytes = 64 * 1024 * 1024;
    private const int MaxErrors = 100;

    public SearchGrepTool(
        ILogger<SearchGrepTool> logger,
        IFullTextSearchEngine searchEngine,
        ITelemetryMetricSink? telemetry = null)
    {
        _logger = logger;
        _searchEngine = searchEngine;
        _telemetry = telemetry;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SearchGrepArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var query = args.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return ToolExecutionResult.Fail(
                "query is required — provide the text or regex to search for inside files. " +
                "Use 'pattern' to filter file names (e.g. '*.cs'), and 'query' for the content to search. " +
                "Example: query='class FileSearchTool', pattern='*.cs', directory='Source'");

        int maxResults = Math.Clamp(args.MaxResults ?? DefaultMaxResults, 1, 200);
        bool caseSensitive = args.CaseSensitive?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        var excludeDirs = ParseExcludeDirs(args.ExcludeDirs);

        return await SearchCoreAsync(
            query, args.Pattern, args.FileExt, args.Directory,
            caseSensitive, maxResults, excludeDirs, ct);
    }

    private async Task<ToolExecutionResult> SearchCoreAsync(
        string query, string? pattern, string? fileExt, string? directory,
        bool caseSensitive, int maxResults, HashSet<string> excludeDirs, CancellationToken ct)
    {
        var filter = fileExt?.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e).ToArray();
        var patternFilter = PatternToExtensionFilter(pattern);

        // 优先级1：Lucene 全文索引
        try
        {
            string? extFilter = null;
            if (filter is { Length: > 0 })
                extFilter = string.Join(";", filter);
            else if (patternFilter is { Length: > 0 })
                extFilter = string.Join(";", patternFilter);

            // 有排除目录时多取 5 倍结果，缓冲后置过滤的损耗
            var luceneFetchCount = excludeDirs.Count > 0 ? maxResults * 5 : maxResults;
            var luceneResults = await _searchEngine.SearchAsync(query, directory ?? "", luceneFetchCount,
                fileExtensionFilter: extFilter,
                subDirectoryFilter: directory,
                ct: ct);

            if (luceneResults.Matches.Count > 0)
            {
                var sb = new StringBuilder();
                var added = 0;
                foreach (var r in luceneResults.Matches)
                {
                    if (added >= maxResults) break;
                    if (IsPathInExcludedDir(r.FilePath, directory, excludeDirs)) continue;
                    sb.AppendLine($"{r.FilePath}:{r.LineNumber}: {r.LineText}");
                    added++;
                }
                if (added > 0)
                    return ToolExecutionResult.Ok(sb.ToString().TrimEnd());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SearchGrep] Lucene search failed, falling back");
        }

        // 优先级2：托管 grep
        return await ManagedGrepAsync(query, pattern, directory, caseSensitive, maxResults, filter ?? patternFilter, excludeDirs, ct);
    }

    private async Task<ToolExecutionResult> ManagedGrepAsync(
        string query, string? pattern, string? directory,
        bool caseSensitive, int maxResults, string[]? extFilter,
        HashSet<string> excludeDirs, CancellationToken ct)
    {
        var cwd = string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory;
        if (!Directory.Exists(cwd))
            return ToolExecutionResult.Fail(
                $"Directory '{cwd}' not found. Use 'directory' to specify an existing path, or omit it to search the current workspace root ({Environment.CurrentDirectory}).");

        var results = new List<string>();
        var files = new List<string>();
        var errors = 0;
        var scannedFiles = 0;
        long scannedBytes = 0;

        var filePattern = string.IsNullOrWhiteSpace(pattern) ? "*.*" : pattern;
        try
        {
            var enumeration = Directory.EnumerateFiles(cwd, filePattern, SearchOption.AllDirectories);
            foreach (var file in enumeration)
            {
                if (files.Count >= MaxEnumeratedFiles) break;
                files.Add(file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SearchGrep] Enumeration error");
            return ToolExecutionResult.Fail($"Search error: {ex.Message}");
        }

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        bool isRegex = LooksLikeRegex(query);
        Regex? regex = null;
        if (isRegex)
        {
            try { regex = new Regex(query, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, ManagedSearchTimeout); }
            catch { isRegex = false; }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ManagedSearchTimeout);

        foreach (var file in files)
        {
            if (cts.IsCancellationRequested || scannedFiles >= MaxScannedFiles || scannedBytes >= MaxScannedBytes) break;
            if (errors >= MaxErrors) break;

            if (IsPathInExcludedDir(file, cwd, excludeDirs)) continue;

            if (extFilter is { Length: > 0 })
            {
                var ext = Path.GetExtension(file);
                if (!extFilter.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
            }

            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxFileSizeBytes) continue;

                var lines = await File.ReadAllLinesAsync(file, cts.Token);
                scannedFiles++;
                scannedBytes += info.Length;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (results.Count >= maxResults) break;
                    bool match = isRegex
                        ? regex?.IsMatch(lines[i]) == true
                        : lines[i].IndexOf(query, comparison) >= 0;
                    if (match)
                    {
                        var relPath = Path.GetRelativePath(cwd, file);
                        results.Add($"{relPath}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { errors++; }
        }

        if (results.Count == 0)
            return ToolExecutionResult.Ok(scannedFiles > 0 ? "(no matches)" : "(no files scanned)");

        return ToolExecutionResult.Ok(string.Join("\n", results));
    }

    private static bool LooksLikeRegex(string q) =>
        q.Any(c => c is '\\' or '^' or '$' or '.' or '|' or '?' or '*' or '+' or '(' or ')' or '[' or '{');

    private static HashSet<string> ParseExcludeDirs(string? excludeDirs)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var raw = !string.IsNullOrWhiteSpace(excludeDirs) ? excludeDirs : DefaultExcludeDirs;
        foreach (var d in raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = d.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length > 0) set.Add(trimmed);
        }
        return set;
    }

    private static bool IsPathInExcludedDir(string filePath, string? searchDir, HashSet<string> excludeDirs)
    {
        if (excludeDirs.Count == 0) return false;
        var relative = !string.IsNullOrWhiteSpace(searchDir)
            ? Path.GetRelativePath(searchDir, filePath)
            : filePath;
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var part in parts)
        {
            if (excludeDirs.Contains(part)) return true;
        }
        return false;
    }

    private static string[]? PatternToExtensionFilter(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        if (pattern.StartsWith("*.") && !pattern.Contains('?') && pattern.Count(c => c == '*') == 1)
            return [pattern[1..]]; // "*.cs" → ".cs"
        return null;
    }
}

public sealed record SearchGrepArgs
{
    [ToolParam("Text or regex to search for in files")]
    public required string Query { get; init; }
    [ToolParam("File glob pattern to filter files")]
    public string? Pattern { get; init; }
    [ToolParam("File extensions to filter, e.g. cs;ts")]
    public string? FileExt { get; init; }
    [ToolParam("Case sensitive search: true/false")]
    public string? CaseSensitive { get; init; }
    [ToolParam("Maximum matching lines to return")]
    public int? MaxResults { get; init; }
    [ToolParam("Directory to search in. Default: current directory.")]
    public string? Directory { get; init; }
    [ToolParam("Directories to exclude, semicolon-separated. Default: bin;obj;node_modules;.git")]
    public string? ExcludeDirs { get; init; }
}
