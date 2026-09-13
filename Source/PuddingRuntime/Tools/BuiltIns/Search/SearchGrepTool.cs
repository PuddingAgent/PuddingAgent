using System.Text;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Tools;
using PuddingCode.Tools.Retrieval;
using PuddingFullTextIndex.Contracts;
using PuddingRuntime.Services.Search;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// SearchGrepTool — 工作区文件 grep 搜索工具。
/// 策略链：Lucene 全文索引 (ms) → 纯C#托管 grep (带预算)。
/// </summary>
[Tool(
    id: "search_grep",
    name: "search_grep",
    description: "在指定目录的代码文件中搜索指定文本。支持正则表达式。可选参数 pattern 过滤文件名（如 \"*.cs\"），file_ext 过滤扩展名（如 \"cs;ts\"），directory 限定搜索目录，exclude_dirs 排除子目录（默认 $outputWwwroot;dist;node_modules;bin;obj;.git;.pudding;TestResults;artifacts;publish;.venv;.tmp），exclude_dirs_append 追加排除目录，max_line_bytes 单行截断上限（默认 8192），max_total_bytes 结果总量上限（默认 16384）；结果不足时缩小范围后渐进检索。Hard limits: at most 2000 files are enumerated, at most 2000 files / 64MB are scanned, and one call is capped at 10s. Whenever a limit is hit, the output MUST carry an explicit notice — it never degrades into a silent \"(no matches)\". For large or unknown scopes prefer the indexed tools first: code_symbol_search / code_explore (code index, millisecond latency) or file_search (file-name index); then use search_grep to grep inside a narrow directory.",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
public sealed class SearchGrepTool : PuddingToolBase<SearchGrepArgs>
{
    private readonly ILogger<SearchGrepTool> _logger;
    private readonly IFullTextSearchEngine _searchEngine;
    private readonly ITelemetryMetricSink? _telemetry;
    private readonly ISearchAttemptLedger _ledger;

    private const int DefaultMaxResults = 20;
    private const long MaxFileSizeBytes = 1 * 1024 * 1024;
    private const string DefaultExcludeDirs = "$outputWwwroot;dist;node_modules;bin;obj;.git;.pudding;TestResults;artifacts;publish;.venv;.tmp";
    private const long DefaultMaxLineBytes = 8 * 1024;
    private const long DefaultMaxTotalBytes = 16 * 1024;
    private const string TruncatedMarker = "...[truncated, original={0} bytes]";
    private const string TotalCapMessage = "结果已截断，共命中 {0} 处，请缩小范围";
    private const string EnumerationTruncatedMessage = "文件枚举已达上限 {0} 个，结果可能不完整（建议缩小 directory/pattern/file_ext 范围，或改用索引工具 code_symbol_search / code_explore / file_search 精确定位，毫秒级返回）";
    private const string ScanBudgetMessage = "扫描已达预算上限（{0} 个文件 / {1} 字节），结果可能不完整（建议缩小 directory/pattern/file_ext 范围，或改用索引工具 code_symbol_search / code_explore / file_search 精确定位）";
    private const string ErrorBudgetMessage = "有 {0} 个文件读取失败，已提前结束扫描，结果可能不完整";
    private const string LargeFileSkippedMessage = "已跳过 {0} 个超过 1MB 的大文件，结果可能不完整";
    private const string PaginationReportMessage = "返回数量为 {0} 个超过预算 100，完整结果已释放到临时文件，路径为 {1}，如果需要阅读完整的请使用 file_read 工具以 OffsetLines 参数分页阅读。";
    // ADR-089 U0-S2：覆盖声明行。非 Complete 的结果必须显式声明剩余范围未搜索，空输出不得伪装为"查无结果"。
    private const string CoveragePartialMessage = "(coverage: partial — scanned {0}/{1} files, {2}/{3} bytes; remaining files not searched)";
    private const int MaxInlineResults = 100;

    private static readonly TimeSpan ManagedSearchTimeout = TimeSpan.FromSeconds(10);
    private const int MaxEnumeratedFiles = 2000;
    private const int MaxScannedFiles = 2000;
    private const long MaxScannedBytes = 64 * 1024 * 1024;
    private const int MaxErrors = 100;

    public SearchGrepTool(
        ILogger<SearchGrepTool> logger,
        IFullTextSearchEngine searchEngine,
        ITelemetryMetricSink? telemetry = null,
        ISearchAttemptLedger? ledger = null)
    {
        _logger = logger;
        _searchEngine = searchEngine;
        _telemetry = telemetry;
        _ledger = ledger ?? new SearchAttemptLedger();
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SearchGrepArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var query = args.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return ToolExecutionResult.Fail(
                "query is required — provide the text or regex to search for inside files. " +
                "Use 'pattern' to filter file names (e.g. '*.cs'), and 'query' for the content to search. " +
                "Example: query='class FileSearchTool', pattern='*.cs', directory='Source'",
                status: ToolResultStatuses.ContractError);

        int maxResults = Math.Clamp(args.MaxResults ?? DefaultMaxResults, 1, 200);
        bool caseSensitive = ParseBool(args.CaseSensitive);
        long maxLineBytes = args.MaxLineBytes is null ? DefaultMaxLineBytes : Math.Max(0, args.MaxLineBytes.Value);
        long maxTotalBytes = args.MaxTotalBytes is null ? DefaultMaxTotalBytes : Math.Max(0, args.MaxTotalBytes.Value);
        var excludeDirs = ParseExcludeDirs(args.ExcludeDirs);
        AppendExcludeDirs(excludeDirs, args.ExcludeDirsAppend);

        // Lucene 分支保持原始 directory 语义（相对索引根）；托管 grep 分支使用
        // 执行快照冻结的 WorkingDirectory 解析出的绝对路径（与 file 工具同源），
        // 避免回落到进程 Environment.CurrentDirectory（运行时 bin 目录）。
        var managedDirectory = ResolveManagedSearchDirectory(args.Directory, context);

        // 失败账本：仅对确定性重试（query/scope/glob/case/workspaceVersion 完全一致）短路。
        var key = BuildAttemptKey(query, args.Pattern, managedDirectory, caseSensitive, context);
        if (_ledger.TryGetSuppression(key, out var prior))
        {
            ReportTelemetry(
                "search_attempt",
                TelemetryMetricStatuses.Succeeded,
                ToolResultStatuses.ExactRetrySuppressed,
                BuildAttemptDimensions(query, managedDirectory, args.Pattern, caseSensitive),
                context);
            return ToolExecutionResult.Ok(
                BuildSuppressionHint(prior, query),
                status: ToolResultStatuses.ExactRetrySuppressed);
        }

        var result = await SearchCoreAsync(
            query, args.Pattern, args.FileExt, args.Directory, managedDirectory,
            caseSensitive, maxResults, excludeDirs, maxLineBytes, maxTotalBytes, ct);

        RecordAttempt(key, result, context);
        return result;
    }

    private static string ResolveManagedSearchDirectory(string? directory, ToolExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return Directory.GetCurrentDirectory();
        if (Path.IsPathRooted(directory))
            return directory;
        return Path.GetFullPath(Path.Combine(
            HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory), directory));
    }

    /// <summary>
    /// 归一化历史字符串布尔：true/1/yes/on（不区分大小写）→ true，其余→ false。
    /// 只接受 schema 明确声明的少量别名，不引入未声明别名。
    /// </summary>
    private static bool ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return value.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";
    }

    private SearchAttemptKey BuildAttemptKey(
        string query,
        string? pattern,
        string managedDirectory,
        bool caseSensitive,
        ToolExecutionContext context)
    {
        var workspaceRoot = HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);
        return new SearchAttemptKey(
            "search_grep",
            query,
            SearchAttemptKeyNormalizer.NormalizeScope(managedDirectory),
            pattern ?? string.Empty,
            caseSensitive,
            SearchWorkspaceVersion.Resolve(workspaceRoot));
    }

    private void RecordAttempt(SearchAttemptKey key, ToolExecutionResult result, ToolExecutionContext context)
    {
        var (outcome, summary, count) = Classify(result);
        _ledger.Record(key, new SearchAttemptRecord(outcome, summary, count, DateTimeOffset.UtcNow));

        var name = outcome switch
        {
            SearchAttemptOutcome.NoMatch => "no_match",
            SearchAttemptOutcome.Timeout => "timeout",
            SearchAttemptOutcome.Error => "contract_error",
            SearchAttemptOutcome.Truncated => "truncated",
            _ => "hit",
        };
        var status = outcome == SearchAttemptOutcome.Error
            ? TelemetryMetricStatuses.Failed
            : TelemetryMetricStatuses.Succeeded;
        ReportTelemetry(
            "search_attempt",
            status,
            name,
            BuildAttemptDimensions(key.Query, key.Scope, key.Glob, key.CaseSensitive),
            context);
    }

    private static (SearchAttemptOutcome Outcome, string Summary, int Count) Classify(ToolExecutionResult result)
    {
        if (!result.Success)
            return (SearchAttemptOutcome.Error, result.Error ?? "error", 0);

        var status = result.Status;
        if (string.Equals(status, ToolResultStatuses.NoMatch, StringComparison.Ordinal))
            return (SearchAttemptOutcome.NoMatch, result.Output, 0);
        if (string.Equals(status, ToolResultStatuses.Timeout, StringComparison.Ordinal))
            return (SearchAttemptOutcome.Timeout, result.Output, 0);
        if (string.Equals(status, ToolResultStatuses.Truncated, StringComparison.Ordinal))
            return (SearchAttemptOutcome.Truncated, result.Output, CountResultLines(result.Output));

        return (SearchAttemptOutcome.Hit, result.Output, CountResultLines(result.Output));
    }

    private static int CountResultLines(string output)
    {
        if (string.IsNullOrEmpty(output))
            return 0;
        var count = 0;
        foreach (var line in output.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
                count++;
        }
        return count;
    }

    private static string BuildSuppressionHint(SearchAttemptRecord prior, string query)
    {
        var verb = prior.Outcome == SearchAttemptOutcome.Timeout ? "扫描超时" : "已查无结果";
        var advice = prior.Outcome == SearchAttemptOutcome.Timeout
            ? "建议缩小 directory/pattern/file_ext 范围后继续，或缩短 query。"
            : "建议换 query、缩小范围或调整 pattern/file_ext；若文件可能已变化，可用不同 directory 或 query 重新检索。";
        return $"(exact retry suppressed) 同一搜索（query=\"{query}\"，scope/glob/case 完全相同）此前{verb}：" +
               $"{prior.Summary} {advice}";
    }

    private void ReportTelemetry(
        string name,
        string status,
        string errorCode,
        IReadOnlyDictionary<string, string>? dimensions,
        ToolExecutionContext context)
    {
        if (_telemetry is null)
            return;

        try
        {
            var trace = context.Trace ?? RuntimeTraceContext.CreateNew(context.SessionId, context.WorkspaceId);
            _ = _telemetry.RecordAsync(new TelemetryMetric
            {
                Trace = trace,
                Source = "search_grep",
                Category = TelemetryMetricCategories.Tool,
                Name = name,
                Status = status,
                Severity = status == TelemetryMetricStatuses.Failed ? "error" : "info",
                Summary = name,
                ErrorCode = errorCode,
                Dimensions = dimensions,
            });
        }
        catch
        {
            // telemetry is best-effort and must never affect the search result
        }
    }

    private static IReadOnlyDictionary<string, string> BuildAttemptDimensions(
        string query,
        string scope,
        string? glob,
        bool caseSensitive)
        => new Dictionary<string, string>
        {
            ["query"] = query,
            ["scope"] = scope,
            ["glob"] = glob ?? string.Empty,
            ["case_sensitive"] = caseSensitive ? "true" : "false",
        };

    private async Task<ToolExecutionResult> SearchCoreAsync(
        string query, string? pattern, string? fileExt, string? directory, string managedDirectory,
        bool caseSensitive, int maxResults, HashSet<string> excludeDirs,
        long maxLineBytes, long maxTotalBytes, CancellationToken ct)
    {
        // ADR-089 U0-S2（S2-1）：进入任何后端前先按统一合同建 matcher（literal/regex 同源）。
        // 非法正则在此显式失败（ContractError），绝不静默降级为字面搜索，也不触发 Lucene 与扫描。
        bool isRegex = LooksLikeRegex(query);
        if (!RetrievalMatcher.TryCreate(
                isRegex ? RetrievalMatchMode.Regex : RetrievalMatchMode.Literal,
                caseSensitive ? RetrievalCaseMode.Sensitive : RetrievalCaseMode.Insensitive,
                query, ManagedSearchTimeout, out var matcherOrNull, out var contractError)
            || matcherOrNull is null)
        {
            return ToolExecutionResult.Fail(
                $"{contractError ?? "matcher initialization failed"}. Fix the regex pattern, or remove regex metacharacters to search it as literal text.",
                status: ToolResultStatuses.ContractError);
        }

        var matcher = matcherOrNull;

        var filter = fileExt?.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e).ToArray();
        var patternFilter = PatternToExtensionFilter(pattern);

        // ADR-089 U0-S2（S2-2）：Lucene 只做候选优先级。
        // 候选行必须经同一 matcher 精确复核，分词召回的假阳性在此丢弃；
        // Lucene 抛错/无索引/零命中一律不改变后续流程（覆盖由托管扫描保证）。
        var results = new List<string>();
        var emittedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalResultBytes = 0;
        long matchCount = 0;
        bool totalCapReached = false;

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

            // Matches 为 null（无索引/异常结果）等同于零候选：不改变后续流程，覆盖由扫描保证。
            foreach (var r in luceneResults.Matches ?? [])
            {
                if (results.Count >= maxResults) break;
                if (IsPathInExcludedDir(r.FilePath, directory, excludeDirs)) continue;
                if (!matcher.IsMatch(r.LineText)) continue; // 候选复核：未通过统一 matcher 的一律丢弃
                matchCount++;
                var lineText = TruncateLine(r.LineText, maxLineBytes);
                var entry = $"{r.FilePath}:{r.LineNumber}: {lineText}";
                var entryBytes = Encoding.UTF8.GetByteCount(entry);
                if (maxTotalBytes > 0 && totalResultBytes + entryBytes > maxTotalBytes)
                {
                    totalCapReached = true;
                    break;
                }
                totalResultBytes += entryBytes;
                results.Add(entry);
                emittedKeys.Add(BuildDedupKey(r.FilePath, r.LineNumber));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SearchGrep] Lucene search failed, falling back");
        }

        // ADR-089 U0-S2（S2-3）：候选非空也必须继续托管扫描同 scope/glob 的允许文件——
        // Lucene 零候选/未进候选的文件中的真实命中不得被漏掉（假阴性防御），索引不暗示文件系统完整。
        return await ManagedGrepAsync(matcher, pattern, managedDirectory,
            filter ?? patternFilter, excludeDirs, maxLineBytes, maxTotalBytes,
            results, totalResultBytes, matchCount, totalCapReached, emittedKeys, ct);
    }

    /// <summary>
    /// 命中行去重键：归一化绝对路径 + 行号。Lucene 候选与托管扫描两条路径共用，
    /// 同一 (文件, 行) 只输出一次。
    /// </summary>
    private static string BuildDedupKey(string filePath, int lineNumber) =>
        $"{Path.GetFullPath(filePath).ToLowerInvariant()}|{lineNumber}";

    private async Task<ToolExecutionResult> ManagedGrepAsync(
        RetrievalMatcher matcher, string? pattern, string? directory,
        string[]? extFilter, HashSet<string> excludeDirs, long maxLineBytes, long maxTotalBytes,
        List<string> results, long totalResultBytes, long matchCount, bool totalCapReached,
        HashSet<string> emittedKeys, CancellationToken ct)
    {
                var cwd = string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory;
        if (!Directory.Exists(cwd))
            return ToolExecutionResult.Fail(
                $"Directory '{cwd}' not found. Use 'directory' to specify an existing path, or omit it to search the workspace root ({cwd}).");

        var files = new List<string>();
        var errors = 0;
        var scannedFiles = 0;
        long scannedBytes = 0;
        int skippedLargeFiles = 0;

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

        // ADR-089 U0-S2（D3 修复）：regex 构造已上移至 SearchCoreAsync 的统一合同入口，
        // 非法正则显式失败（ContractError），不再静默降级为字面搜索。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ManagedSearchTimeout);

                bool scanBudgetExceeded = false;
        bool errorBudgetExceeded = false;
        foreach (var file in files)
        {
            if (cts.IsCancellationRequested || totalCapReached) break;
            if (errors >= MaxErrors) { errorBudgetExceeded = true; break; }
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
                if (info.Length > MaxFileSizeBytes) { skippedLargeFiles++; continue; }

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
                    if (totalCapReached) break;
                    var line = lines[i].TrimEnd('\r');
                    // 统一匹配合同（ADR-089 L128）：与 Lucene 候选复核同源，两条路径结论一致。
                    if (!matcher.IsMatch(line)) continue;

                    matchCount++;
                    // 已由 Lucene 候选输出的命中行不再重复输出，但仍计入扫描统计（matchCount）。
                    if (emittedKeys.Contains(BuildDedupKey(file, i + 1))) continue;
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

        bool timedOut = cts.IsCancellationRequested && !ct.IsCancellationRequested;

        // ADR-089 U0-S2（S2-4）：覆盖状态诚实推导——任何预算耗尽/截断/跳过都使覆盖非 Complete。
        bool coverageComplete = !timedOut && !enumerationTruncated && !scanBudgetExceeded
            && !errorBudgetExceeded && !totalCapReached && skippedLargeFiles == 0;

        var notes = new List<string>();
        if (timedOut)
            notes.Add("搜索超时（10s），结果可能不完整，建议缩小 directory/pattern/file_ext 范围，或改用索引工具 code_symbol_search / code_explore / file_search");
        if (enumerationTruncated)
            notes.Add(string.Format(EnumerationTruncatedMessage, MaxEnumeratedFiles));
        if (scanBudgetExceeded)
            notes.Add(string.Format(ScanBudgetMessage, MaxScannedFiles, MaxScannedBytes));
        if (totalCapReached)
            notes.Add(string.Format(TotalCapMessage, matchCount));
        if (errorBudgetExceeded)
            notes.Add(string.Format(ErrorBudgetMessage, MaxErrors));
        if (skippedLargeFiles > 0)
            notes.Add(string.Format(LargeFileSkippedMessage, skippedLargeFiles));
        if (!coverageComplete)
            notes.Add(string.Format(CoveragePartialMessage, scannedFiles, MaxScannedFiles, scannedBytes, MaxScannedBytes));

        // 状态映射（S2-4）：partial/truncated/timeout 一律不得用 Ok；只有覆盖 Complete 的空结果才是 no_match。
        string status;
        if (timedOut)
            status = ToolResultStatuses.Timeout;
        else if (coverageComplete)
            status = results.Count == 0 ? ToolResultStatuses.NoMatch : ToolResultStatuses.Ok;
        else
            status = ToolResultStatuses.Truncated;

        if (results.Count == 0)
        {
            var notesText = notes.Count > 0 ? "\n" + string.Join("\n", notes) : string.Empty;
            if (timedOut)
                return ToolExecutionResult.Ok("(search timed out)" + notesText, status: ToolResultStatuses.Timeout);

            // 空输出时只有 Complete 才允许 (no matches)：非 Complete 必须带 partial 声明，
            // 防止上层把"没扫完"误读为"查无结果"（no_match 仅表示声明范围已完成）。
            // 覆盖声明行已由 notes 统一产出（!coverageComplete 时必然入列），此处不得重复拼接，
            // 否则同一行 coverage 声明会出现两次。
            if (!coverageComplete)
            {
                var partialOutput = notes.Count > 0
                    ? string.Join("\n", notes)
                    : string.Format(CoveragePartialMessage, scannedFiles, MaxScannedFiles, scannedBytes, MaxScannedBytes);
                return ToolExecutionResult.Ok(partialOutput, status: ToolResultStatuses.Truncated);
            }

            var emptyMsg = scannedFiles > 0 ? "(no matches)" : "(no files scanned)";
            return ToolExecutionResult.Ok(emptyMsg + notesText, status: ToolResultStatuses.NoMatch);
        }

        if (results.Count > MaxInlineResults)
        {
            string? tmpPath = null;
            try
            {
                tmpPath = Path.GetTempFileName();
                File.WriteAllText(tmpPath, string.Join("\n", results), new UTF8Encoding(false));

                var output = string.Join("\n", results.Take(MaxInlineResults));
                if (notes.Count > 0)
                    output += "\n" + string.Join("\n", notes);
                output += "\n" + string.Format(PaginationReportMessage, results.Count, tmpPath);
                return ToolExecutionResult.Ok(output, status: status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SearchGrep] Failed to persist results to temp file, falling back to inline output");
                if (tmpPath != null)
                {
                    try { File.Delete(tmpPath); } catch { /* best effort */ }
                }
            }
        }

        var finalOutput = string.Join("\n", results);
        if (notes.Count > 0)
            finalOutput += "\n" + string.Join("\n", notes);
        return ToolExecutionResult.Ok(finalOutput, status: status);
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
    [ToolParam("Directories to exclude, semicolon-separated. Default: $outputWwwroot;dist;node_modules;bin;obj;.git;.pudding;TestResults;artifacts;publish;.venv;.tmp")]
    public string? ExcludeDirs { get; init; }
    [ToolParam("Extra directories to exclude, appended to the effective exclude list, semicolon-separated")]
    public string? ExcludeDirsAppend { get; init; }
    [ToolParam("Max bytes per matching line before truncation. 0 disables truncation. Default: 8192")]
    public long? MaxLineBytes { get; init; }
    [ToolParam("Max total bytes of results before truncation. 0 disables the cap. Default: 16384")]
    public long? MaxTotalBytes { get; init; }
}
