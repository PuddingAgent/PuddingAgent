using System.Text;
using System.Text.RegularExpressions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Tools;
using PuddingFullTextIndex.Contracts;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// SearchGrepTool — 工作区文件 grep 搜索工具。
/// 策略链：Lucene 全文索引 (ms) → 纯C#托管 grep (带预算)。
/// </summary>
[Tool(
    id: "search_grep",
    name: "search_grep",
    description: "在指定目录的代码文件中搜索指定文本。支持正则表达式。可选参数 pattern 过滤文件名（如 \"*.cs\"），file_ext 过滤扩展名（如 \"cs;ts\"），directory 限定搜索目录，exclude_dirs 排除子目录（默认 $outputWwwroot;dist;node_modules;bin;obj;.git;TestResults;artifacts;publish;.venv;.tmp），exclude_dirs_append 追加排除目录，max_line_bytes 单行截断上限（默认 8192），max_total_bytes 结果总量上限（默认 262144）。",
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
    private const string DefaultExcludeDirs = "$outputWwwroot;dist;node_modules;bin;obj;.git;TestResults;artifacts;publish;.venv;.tmp";
    private const long DefaultMaxLineBytes = 8 * 1024;
    private const long DefaultMaxTotalBytes = 256 * 1024;
    private const string TruncatedMarker = "...[truncated, original={0} bytes]";
    private const string TotalCapMessage = "结果已截断，共命中 {0} 处，请缩小范围";
    private const string EnumerationTruncatedMessage = "文件枚举已达上限 {0} 个，结果可能不完整（建议缩小 directory/pattern/file_ext 范围）";
    private const string ScanBudgetMessage = "扫描已达预算上限（{0} 个文件 / {1} 字节），结果可能不完整";

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
        long maxLineBytes = args.MaxLineBytes is null ? DefaultMaxLineBytes : Math.Max(0, args.MaxLineBytes.Value);
        long maxTotalBytes = args.MaxTotalBytes is null ? DefaultMaxTotalBytes : Math.Max(0, args.MaxTotalBytes.Value);
        var excludeDirs = ParseExcludeDirs(args.ExcludeDirs);
        AppendExcludeDirs(excludeDirs, args.ExcludeDirsAppend);

        // Lucene 分支保持原始 directory 语义（相对索引根）；托管 grep 分支使用
        // 执行快照冻结的 WorkingDirectory 解析出的绝对路径（与 file 工具同源），
        // 避免回落到进程 Environment.CurrentDirectory（运行时 bin 目录）。
        var managedDirectory = ResolveManagedSearchDirectory(args.Directory, context);

        return await SearchCoreAsync(
            query, args.Pattern, args.FileExt, args.Directory, managedDirectory,
            caseSensitive, maxResults, excludeDirs, maxLineBytes, maxTotalBytes, ct);
    }

    private static string ResolveManagedSearchDirectory(string? directory, ToolExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);
        if (Path.IsPathRooted(directory))
            return directory;
        return Path.GetFullPath(Path.Combine(
            HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory), directory));
    }

    private async Task<ToolExecutionResult> SearchCoreAsync(
        string query, string? pattern, string? fileExt, string? directory, string managedDirectory,
        bool caseSensitive, int maxResults, HashSet<string> excludeDirs,
        long maxLineBytes, long maxTotalBytes, CancellationToken ct)
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
                long totalBytes = 0;
                long matchCount = 0;
                bool capReached = false;
                foreach (var r in luceneResults.Matches)
                {
                    if (added >= maxResults) break;
                    if (IsPathInExcludedDir(r.FilePath, directory, excludeDirs)) continue;
                    matchCount++;
                    var lineText = TruncateLine(r.LineText, maxLineBytes);
                    var entry = $"{r.FilePath}:{r.LineNumber}: {lineText}";
                    var entryBytes = Encoding.UTF8.GetByteCount(entry);
                    if (maxTotalBytes > 0 && totalBytes + entryBytes > maxTotalBytes)
                    {
                        capReached = true;
                        break;
                    }
                    totalBytes += entryBytes;
                    sb.AppendLine(entry);
                    added++;
                }
                if (capReached)
                    sb.AppendLine(string.Format(TotalCapMessage, matchCount));
                if (added > 0)
                    return ToolExecutionResult.Ok(sb.ToString().TrimEnd());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SearchGrep] Lucene search failed, falling back");
        }

        // 优先级2：托管 grep
        return await ManagedGrepAsync(query, pattern, managedDirectory, caseSensitive, maxResults,
            filter ?? patternFilter, excludeDirs, maxLineBytes, maxTotalBytes, ct);
    }

    private async Task<ToolExecutionResult> ManagedGrepAsync(
        string query, string? pattern, string? directory,
        bool caseSensitive, int maxResults, string[]? extFilter,
        HashSet<string> excludeDirs, long maxLineBytes, long maxTotalBytes, CancellationToken ct)
    {
                var cwd = string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory;
        if (!Directory.Exists(cwd))
            return ToolExecutionResult.Fail(
                $"Directory '{cwd}' not found. Use 'directory' to specify an existing path, or omit it to search the workspace root ({cwd}).");

        var results = new List<string>();
        var files = new List<string>();
        var errors = 0;
        var scannedFiles = 0;
        long scannedBytes = 0;
        long totalResultBytes = 0;
        long matchCount = 0;
        bool totalCapReached = false;

                var filePattern = string.IsNullOrWhiteSpace(pattern) ? "*.*" : pattern;
        bool enumerationTruncated = false;
        try
        {
            // 枚举阶段即按目录名剪枝排除目录（bin/obj 等整棵子树不再进入枚举），
            // 避免排除目录中的文件占用 MaxEnumeratedFiles 名额，导致真实源码目录被跳过（假阴性）。
            files = EnumerateFilesPruningExcluded(cwd, filePattern, excludeDirs, MaxEnumeratedFiles, out enumerationTruncated);
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

                bool scanBudgetExceeded = false;
        foreach (var file in files)
        {
            if (cts.IsCancellationRequested || totalCapReached || results.Count >= maxResults) break;
            if (errors >= MaxErrors) break;
            if (scannedFiles >= MaxScannedFiles || scannedBytes >= MaxScannedBytes)
            {
                scanBudgetExceeded = true;
                break;
            }

            if (extFilter is { Length: > 0 })
            {
                var ext = Path.GetExtension(file);
                if (!extFilter.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
            }

            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxFileSizeBytes) continue;

                var raw = await File.ReadAllBytesAsync(file, cts.Token);
                scannedFiles++;
                scannedBytes += raw.Length;

                // 二进制保护：含 NUL 字节视为二进制文件，跳过（不计入匹配）
                if (Array.IndexOf(raw, (byte)0) >= 0) continue;

                var text = Encoding.UTF8.GetString(raw);
                if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..]; // 去除 UTF-8 BOM
                var lines = text.Split('\n');

                for (int i = 0; i < lines.Length; i++)
                {
                    if (results.Count >= maxResults || totalCapReached) break;
                    var line = lines[i].TrimEnd('\r');
                    bool match = isRegex
                        ? regex?.IsMatch(line) == true
                        : line.IndexOf(query, comparison) >= 0;
                    if (!match) continue;

                    matchCount++;
                    var lineText = TruncateLine(line.Trim(), maxLineBytes);
                    var relPath = Path.GetRelativePath(cwd, file);
                    var entry = $"{relPath}:{i + 1}: {lineText}";
                    var entryBytes = Encoding.UTF8.GetByteCount(entry);
                    if (maxTotalBytes > 0 && totalResultBytes + entryBytes > maxTotalBytes)
                    {
                        totalCapReached = true;
                        break;
                    }
                    totalResultBytes += entryBytes;
                    results.Add(entry);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { errors++; }
        }

        var notes = new List<string>();
        if (enumerationTruncated)
            notes.Add(string.Format(EnumerationTruncatedMessage, MaxEnumeratedFiles));
        if (scanBudgetExceeded)
            notes.Add(string.Format(ScanBudgetMessage, MaxScannedFiles, MaxScannedBytes));
        if (totalCapReached)
            notes.Add(string.Format(TotalCapMessage, matchCount));

        if (results.Count == 0)
        {
            var emptyMsg = scannedFiles > 0 ? "(no matches)" : "(no files scanned)";
            if (notes.Count > 0)
                return ToolExecutionResult.Ok(emptyMsg + "\n" + string.Join("\n", notes));
            return ToolExecutionResult.Ok(emptyMsg);
        }

        var output = string.Join("\n", results);
        if (notes.Count > 0)
            output += "\n" + string.Join("\n", notes);
        return ToolExecutionResult.Ok(output);
    }

        /// <summary>
    /// 深度优先枚举文件：按目录名剪枝，排除目录（bin/obj 等）的整棵子树直接跳过。
    /// 避免排除目录中的大量文件占用枚举名额，导致真实源码目录被跳过（假阴性），
    /// 同时避免遍历 bin/obj 等大目录带来的无谓开销。
    /// </summary>
    private static List<string> EnumerateFilesPruningExcluded(
        string root, string pattern, HashSet<string> excludeDirs, int maxFiles, out bool truncated)
    {
        var files = new List<string>(1024);
        truncated = false;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (!visited.Add(dir)) continue; // 防符号链接/联接导致的循环

            string[] subDirs;
            string[] dirFiles;
            try
            {
                subDirs = Directory.GetDirectories(dir, "*", SearchOption.TopDirectoryOnly);
                dirFiles = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception) when (dir != root)
            {
                continue; // 子目录不可访问或枚举失败：跳过该目录
            }
            // 根目录枚举失败则向上抛出，由调用方转换为 Fail

            foreach (var sub in subDirs)
            {
                if (excludeDirs.Contains(Path.GetFileName(sub))) continue;
                stack.Push(sub);
            }

            foreach (var file in dirFiles)
            {
                if (files.Count >= maxFiles)
                {
                    truncated = true;
                    return files;
                }
                files.Add(file);
            }
        }

        return files;
    }

    private static bool LooksLikeRegex(string q) =>
        q.Any(c => c is '\\' or '^' or '$' or '.' or '|' or '?' or '*' or '+' or '(' or ')' or '[' or '{');

        private static HashSet<string> ParseExcludeDirs(string? excludeDirs)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // null → use default; empty string → no exclusion
        var raw = excludeDirs ?? DefaultExcludeDirs;
        foreach (var d in raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = d.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length > 0) set.Add(trimmed);
        }
        return set;
    }

    private static void AppendExcludeDirs(HashSet<string> set, string? extra)
    {
        if (string.IsNullOrWhiteSpace(extra)) return;
        foreach (var d in extra.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = d.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length > 0) set.Add(trimmed);
        }
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

    private static string TruncateLine(string line, long maxLineBytes)
    {
        if (maxLineBytes <= 0) return line;
        var byteCount = Encoding.UTF8.GetByteCount(line);
        if (byteCount <= maxLineBytes) return line;
        return TruncateByBytes(line, maxLineBytes) + string.Format(TruncatedMarker, byteCount);
    }

    private static string TruncateByBytes(string s, long maxBytes)
    {
        var count = 0;
        var i = 0;
        while (i < s.Length)
        {
            int runeLen = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            int byteLen = runeLen == 2 ? 4 : s[i] <= 0x7F ? 1 : s[i] <= 0x7FF ? 2 : 3;
            if (count + byteLen > maxBytes) break;
            count += byteLen;
            i += runeLen;
        }
        return s[..i];
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
    [ToolParam("Directories to exclude, semicolon-separated. Default: $outputWwwroot;dist;node_modules;bin;obj;.git;TestResults;artifacts;publish;.venv;.tmp")]
    public string? ExcludeDirs { get; init; }
    [ToolParam("Extra directories to exclude, appended to the effective exclude list, semicolon-separated")]
    public string? ExcludeDirsAppend { get; init; }
    [ToolParam("Max bytes per matching line before truncation. 0 disables truncation. Default: 8192")]
    public long? MaxLineBytes { get; init; }
    [ToolParam("Max total bytes of results before truncation. 0 disables the cap. Default: 262144")]
    public long? MaxTotalBytes { get; init; }
}
