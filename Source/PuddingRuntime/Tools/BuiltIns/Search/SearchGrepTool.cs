using System.Diagnostics;
using System.Text;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Tools;
using PuddingCode.Tools.Definitions;
using PuddingCode.Tools.Retrieval;
using PuddingFullTextIndex.Contracts;
using PuddingPathFiltering;
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
    description: "在指定目录的代码文件中搜索指定文本。支持正则表达式。可选参数 pattern 过滤文件名（如 \"*.cs\"），file_ext 过滤扩展名（如 \"cs;ts\"），directory 限定搜索目录，exclude_dirs 排除子目录（默认 = 单一真源 PathNoiseRules 派生的噪声目录名单：构建产物/依赖/IDE/工具产物，含 .pudding/.tmp-build/.pnpm-store），exclude_dirs_append 追加排除目录，max_line_bytes 单行截断上限（默认 8192），max_total_bytes 结果总量上限（默认 16384）；结果不足时缩小范围后渐进检索。Hard limits: at most 2000 files are enumerated, at most 2000 files / 64MB are scanned, and one call is capped at 10s. Whenever a limit is hit, the output MUST carry an explicit notice — it never degrades into a silent \"(no matches)\". For large or unknown scopes prefer the indexed tools first: code_symbol_search / code_explore (code index, millisecond latency) or file_search (file-name index); then use search_grep to grep inside a narrow directory. The optional backend parameter routes the call: omit it (or backend='scan') for the legacy managed scan (unchanged), or pass backend='index' to query the full-text index directly (no managed scan, returns per-call timing, requires the scope to be indexed).",

    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
public sealed class SearchGrepTool : PuddingToolBase<SearchGrepArgs>
{
    private readonly ILogger<SearchGrepTool> _logger;
    private readonly IFullTextSearchEngine _searchEngine;
    private readonly ITelemetryMetricSink? _telemetry;
    private readonly ISearchAttemptLedger _ledger;
    private readonly TimeSpan _searchTimeout;

    private const int DefaultMaxResults = 20;
    private const long MaxFileSizeBytes = 1 * 1024 * 1024;
    // ADR-089 U4-4 D4: 默认排除名单不再是本文件里的 12 项私有副本，而是从单一真源渲染而来。
    // 排序：先按长度再按序数，保证字符串稳定（不随 HashSet 枚举顺序变化，测试可断言）。
    private static readonly string DefaultExcludeDirs = string.Join(';', PathNoiseRules.DirectoryNames
        .OrderBy(name => name.Length)
        .ThenBy(name => name, StringComparer.Ordinal));
    private const long DefaultMaxLineBytes = 8 * 1024;
    private const long DefaultMaxTotalBytes = 16 * 1024;
    private const string TruncatedMarker = "...[truncated, original={0} bytes]";
    private const string TotalCapMessage = "结果已截断，共命中 {0} 处，请缩小范围";
    private const string EnumerationTruncatedMessage = "文件枚举已达上限 {0} 个，结果可能不完整（建议缩小 directory/pattern/file_ext 范围，或改用索引工具 code_symbol_search / code_explore / file_search 精确定位，毫秒级返回）";
    private const string ScanBudgetMessage = "扫描已达预算上限（{0} 个文件 / {1} 字节），结果可能不完整（建议缩小 directory/pattern/file_ext 范围，或改用索引工具 code_symbol_search / code_explore / file_search 精确定位）";
    private const string ErrorBudgetMessage = "有 {0} 个文件读取失败，已提前结束扫描，结果可能不完整";
    private const string ReadErrorsMessage = "有 {0} 个文件读取失败，结果可能不完整";
    private const string EnumerationErroredMessage = "{0} 个子目录枚举失败，结果可能不完整";
    private const string MaxResultsReachedMessage = "达到 max_results 上限（{0}），结果可能不完整";
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

    // ADR-089 U4-5a（用户裁定 2026-09-24）：backend 路由参数的两个合法值。
    // 默认（未传/空）= scan = 旧路径；只有显式 index 才进入新的全文索引后端。
    private const string BackendScan = "scan";
    private const string BackendIndex = "index";

    public SearchGrepTool(
        ILogger<SearchGrepTool> logger,
        IFullTextSearchEngine searchEngine,
        ITelemetryMetricSink? telemetry = null,
        ISearchAttemptLedger? ledger = null,
        TimeSpan? searchTimeout = null)
    {
        _logger = logger;
        _searchEngine = searchEngine;
        _telemetry = telemetry;
        _ledger = ledger ?? new SearchAttemptLedger();
        // ADR-089 U0 R2：单次调用预算可注入（测试用小预算验证候选+扫描共享同一 deadline），
        // 未注入或非法时使用产品默认 10s。
        _searchTimeout = searchTimeout is { } timeout && timeout > TimeSpan.Zero
            ? timeout
            : ManagedSearchTimeout;
    }

    /// <summary>
    /// search_grep 的展示投影声明（tool-owned presentation）：kind=search，
    /// meta 取参数里确有的事实 query/pattern/directory。
    /// 命中数不进 meta：工具输出是自由文本且带截断语义（文本行数 ≠ 命中数），没有结构化计数事实。
    /// </summary>
    public static ToolPresentationIntent? Present(ToolPresentationInput input)
    {
        var args = ToolPresentationArgs.Create(input.Arguments);
        var meta = new ToolPresentationMeta();
        meta.AddString("query", args.GetString("query"));
        meta.AddString("pattern", args.GetString("pattern"));
        meta.AddString("directory", args.GetString("directory"));

        return new ToolPresentationIntent
        {
            Kind = ToolPresentationIntentKind.Search,
            Meta = meta.Build(),
        };
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

        // ADR-089 U4-5a（用户裁定 2026-09-24）：backend 路由。
        // 默认（未传/空/'scan'）= 旧路径，逐字节不变；只有显式 'index' 才进入新的全文索引后端。
        // 旧路径完整保留作为兜底：新后端不可用时删掉该参数即可回退，无需回滚代码。
        // 新后端**不写失败账本**：否则一次 index 调用的 no_match 会把同 query 的 scan 调用短路，
        // 反而破坏兜底（账本键与 scan 共用，见 BuildAttemptKey）。
        var backend = (args.Backend ?? string.Empty).Trim().ToLowerInvariant();
        if (backend.Length == 0) backend = BackendScan;
        if (backend is not (BackendScan or BackendIndex))
            return ToolExecutionResult.Fail(
                $"Unknown backend '{args.Backend}'. Valid values: '{BackendScan}' (default, legacy managed scan) "
                + $"and '{BackendIndex}' (full-text index backend). Omit the parameter to use the default path.",
                status: ToolResultStatuses.ContractError);

        if (backend == BackendIndex)
            return await IndexBackendSearchAsync(
                query, args.Pattern, args.FileExt, args.Directory, managedDirectory,
                ParseBool(args.CaseSensitive), maxResults, excludeDirs, maxLineBytes, context, ct);

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
        // ADR-089 U0 R2：调用方取消必须在进入任何后端之前显式传播。
        ct.ThrowIfCancellationRequested();

        // ADR-089 U0-S2（S2-1）：进入任何后端前先按统一合同建 matcher（literal/regex 同源）。
        // 非法正则在此显式失败（ContractError），绝不静默降级为字面搜索，也不触发 Lucene 与扫描。
        bool isRegex = LooksLikeRegex(query);
        if (!RetrievalMatcher.TryCreate(
                isRegex ? RetrievalMatchMode.Regex : RetrievalMatchMode.Literal,
                caseSensitive ? RetrievalCaseMode.Sensitive : RetrievalCaseMode.Insensitive,
                query, _searchTimeout, out var matcherOrNull, out var contractError)
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

        // ADR-089 U0 R2：整次调用共享唯一 deadline——覆盖 Lucene 调用、候选准入、目录枚举与扫描全流程，
        // 候选阶段不再游离于预算之外。内部超时与调用方取消通过令牌对（cts/ct）区分。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_searchTimeout);

        // ADR-089 U0 R1：Lucene 候选只决定「优先读取哪些文件」——仅产出去重、有序（按 Lucene 相关度）
        // 的候选文件路径集合。候选不再直接输出索引文本、不再预填去重键；
        // 当前内容复核统一由托管扫描路径完成（候选文件优先，其次枚举文件）。
        // Lucene 抛错/无索引/零命中一律不改变后续流程（覆盖由托管扫描保证）。
        var candidatePaths = new List<string>();
        var candidateSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                ct: cts.Token);

            // Matches 为 null（无索引/异常结果）等同于零候选：不改变后续流程，覆盖由扫描保证。
            foreach (var r in luceneResults.Matches ?? [])
            {
                if (IsPathInExcludedDir(r.FilePath, directory, excludeDirs)) continue;
                var full = Path.IsPathRooted(r.FilePath)
                    ? Path.GetFullPath(r.FilePath)
                    : Path.GetFullPath(Path.Combine(
                        string.IsNullOrWhiteSpace(directory) ? managedDirectory : directory, r.FilePath));
                if (candidateSeen.Add(full))
                    candidatePaths.Add(full);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // R2.4/R3.3：调用方取消必须传播，不得被 Lucene 兜底分支吞掉
        }
        catch (Exception ex)
        {
            // 内部预算超时或 Lucene 故障：不改变流程，覆盖由扫描路径表达。
            _logger.LogWarning(ex, "[SearchGrep] Lucene search failed, falling back");
        }

        // ADR-089 U0-S2（S2-3）：候选非空也必须继续托管扫描同 scope/glob 的允许文件——
        // Lucene 零候选/未进候选的文件中的真实命中不得被漏掉（假阴性防御），索引不暗示文件系统完整。
        return await ManagedGrepAsync(matcher, pattern, managedDirectory,
            filter ?? patternFilter, excludeDirs, maxLineBytes, maxTotalBytes,
            maxResults, candidatePaths, cts, ct);
    }

    /// <summary>
    /// 命中行去重键：归一化绝对路径 + 行号。Lucene 候选与托管扫描两条路径共用，
    /// 同一 (文件, 行) 只输出一次。
    /// </summary>
    /// <summary>
    /// ADR-089 U4-5a：全文索引后端（新路径，与旧路径完全独立）。
    /// 契约：只消费索引命中，**不做托管全量扫描** ⇒ 2000 文件 / 64 MB / 10 s 三重上限不适用，
    /// 因此不产生“扫描预算耗尽”型假否定；覆盖度 = 该 scope 已建索引的文件（索引不暗示文件系统完整）。
    /// 语义：query 交给引擎的 Lucene 查询解析器（与评测探针 LuceneFullTextProbe 同源实测的配置一致），
    /// 不是托管扫描的字面子串语义；需要字面/正则语义时用默认 backend（不传该参数）。
    /// 观测：输出尾行含 engineMs（引擎侧）与 totalMs（工具侧，含后置过滤），并作为 telemetry 维度上报。
    /// </summary>
    private async Task<ToolExecutionResult> IndexBackendSearchAsync(
        string query, string? pattern, string? fileExt, string? directory, string managedDirectory,
        bool caseSensitive, int maxResults, HashSet<string> excludeDirs, long maxLineBytes,
        ToolExecutionContext context, CancellationToken ct)
    {
        if (caseSensitive)
            return ToolExecutionResult.Fail(
                "backend='index' does not support case_sensitive=true: index hits come from the Lucene analyzer and do not honour case mode. "
                + "Omit 'backend' to use the managed scan path, which supports case_sensitive.",
                status: ToolResultStatuses.ContractError);

        var total = Stopwatch.StartNew();

        // scope：索引按绝对路径归一化哈希定位（LuceneSearchEngine.GetIndexDirectoryPath），故必须给绝对 scope。
        // 省略 directory 时默认**工作区根**（而不是进程 CWD）；这与旧路径的 CWD 回退有意不同，
        // 且在输出尾行里显式打印 scope 事实，调用方可核对。
        var scopeDirectory = string.IsNullOrWhiteSpace(directory)
            ? Path.GetFullPath(HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory))
            : Path.GetFullPath(managedDirectory);

        // 扩展名过滤与旧路径同源：file_ext 优先，其次 pattern 为 "*.ext" 形态时的等价扩展名。
        var filter = fileExt?.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e).ToArray();
        var patternFilter = PatternToExtensionFilter(pattern);
        string? extFilter = filter is { Length: > 0 }
            ? string.Join(";", filter)
            : patternFilter is { Length: > 0 } ? string.Join(";", patternFilter) : null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_searchTimeout);

        FullTextSearchResult engineResult;
        try
        {
            // maxResults + 1：多取一条用于判定“是否被 max_results 截断”，不改变返回语义。
            engineResult = await _searchEngine.SearchAsync(
                query, scopeDirectory, maxResults + 1,
                fileExtensionFilter: extFilter,
                subDirectoryFilter: null,
                ct: cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 调用方取消必须传播
        }
        catch (OperationCanceledException)
        {
            ReportIndexBackendTelemetry(context, scopeDirectory, "timeout", total.ElapsedMilliseconds);
            return ToolExecutionResult.Fail(
                $"Index backend timed out after {_searchTimeout.TotalSeconds:0.##}s for scope '{scopeDirectory}'. "
                + "Narrow the scope or omit 'backend' to use the managed scan path.",
                status: ToolResultStatuses.Timeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SearchGrep] index backend failed");
            ReportIndexBackendTelemetry(context, scopeDirectory, "error", total.ElapsedMilliseconds);
            return ToolExecutionResult.Fail(
                $"Index backend failed: {ex.Message}. Omit 'backend' (or pass 'scan') to use the managed scan path.",
                status: ToolResultStatuses.ContractError);
        }

        if (!engineResult.Success)
        {
            ReportIndexBackendTelemetry(context, scopeDirectory, "unavailable", total.ElapsedMilliseconds);
            return ToolExecutionResult.Fail(
                $"Index backend unavailable for scope '{scopeDirectory}': {engineResult.Error ?? "engine reported failure"}. "
                + "The scope must be indexed first; omit 'backend' (or pass 'scan') to use the managed scan path.",
                status: ToolResultStatuses.ContractError);
        }

        // ADR-089 U4-5a：引擎把 query 交给 Lucene 查询解析器，而 Lucene 的运算符语义与调用方的
        // 正则/字面直觉并不一致——实测同一批词：'A|B|C' 静默 0 命中，而 'A OR B OR C' 命中 41 条
        // （即在此 parser 配置下 '|' 不等价于 OR）。引擎自身对此返回 Success=true + 0 命中，
        // 调用方于是拿到一句空洞的 "(no matches)"，会以为“这里真的没有”而换词重试 ⇒ 打转。
        // 这正是 ADR-089 §8 硬约束 6 明禁的假否定，故在引擎内做**一次**降级重试并显式标注：
        // 把 query 按非词字符拆词、以显式 OR 连接（已实测可用的形态）重试一次。
        // 仅在“引擎 0 命中且 query 含 Lucene 语法字符”时触发 ⇒ 有命中的正常路径零行为变化。
        string? queryFallback = null;
        if ((engineResult.Matches?.Count ?? 0) == 0 && engineResult.TotalMatches == 0
            && TryBuildOrFallbackQuery(query, out var fallbackQuery))
        {
            queryFallback = fallbackQuery;
            try
            {
                var retried = await _searchEngine.SearchAsync(
                    fallbackQuery, scopeDirectory, maxResults + 1,
                    fileExtensionFilter: extFilter,
                    subDirectoryFilter: null,
                    ct: cts.Token);
                if (retried.Success && retried.TotalMatches > 0)
                    engineResult = retried;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SearchGrep] index backend OR-fallback retry failed");
            }
        }

        // 后置过滤与旧路径共用 canonical 合同（IsPathInExcludedDir / RetrievalGlobMatcher），避免语义分叉。
        // 索引是快照：命中指向的文件若已被删除/移动，则不输出（避免把陈旧路径当命中）。
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        bool truncated = false;
        int staleSkipped = 0;
        int engineMatchCount = 0;
        string? firstStaleRawPath = null;
        foreach (var match in engineResult.Matches ?? [])
        {
            engineMatchCount++;
            if (lines.Count >= maxResults) { truncated = true; break; }
            // 索引里存的 path 不保证是绝对的：建索引时若 scope 写的是相对路径（探针的 --scope "." 即如此），
            // 写入的就是相对路径。必须以「本次查询的 scope」为基准解析，而不是进程 CWD——
            // 否则相对路径会被整体误判成陈旧条目而静默丢弃（引擎命中了、工具却答“没有”，即假否定）。
            var fullPath = Path.IsPathRooted(match.FilePath)
                ? Path.GetFullPath(match.FilePath)
                : Path.GetFullPath(Path.Combine(scopeDirectory, match.FilePath));
            if (!File.Exists(fullPath))
            {
                staleSkipped++;
                firstStaleRawPath ??= match.FilePath;
                continue;
            }
            if (IsPathInExcludedDir(fullPath, scopeDirectory, excludeDirs)) continue;
            var relative = NormalizeRelativePath(scopeDirectory, fullPath);
            if (!string.IsNullOrWhiteSpace(pattern)
                && !RetrievalGlobMatcher.Matches(Path.GetFileName(fullPath), relative, pattern, ignoreCase: true))
                continue;
            if (!emitted.Add(BuildDedupKey(fullPath, match.LineNumber))) continue;
            var lineText = TruncateLine((match.LineText ?? string.Empty).Trim(), maxLineBytes);
            lines.Add($"{relative}:{match.LineNumber}: {lineText}");
        }

        total.Stop();
        var summary = $"(backend=index: scope={scopeDirectory}, lines={lines.Count}, engineMatches={engineMatchCount}, "
            + $"engineTotalMatches={engineResult.TotalMatches}, engineMs={engineResult.ElapsedMs}, totalMs={total.ElapsedMilliseconds}"
            + (truncated ? ", truncated=max_results" : string.Empty)
            + (staleSkipped > 0 ? $", staleSkipped={staleSkipped}" : string.Empty)
            + (firstStaleRawPath is not null ? $", firstStaleRawPath={firstStaleRawPath}" : string.Empty)
            + (queryFallback is not null ? $", queryFallback=\"{queryFallback}\"" : string.Empty)
            + "; single Lucene query-parser call, no managed scan — the 2000-file/64MB/10s caps do not apply, "
            + "coverage = files indexed under this scope, line text comes from the index snapshot)";

        var outcome = lines.Count == 0 ? "no_match" : truncated ? "truncated" : "hit";
        ReportIndexBackendTelemetry(context, scopeDirectory, outcome, total.ElapsedMilliseconds);

        // 全部命中都陈旧时不得只回一句空洞的 "(no matches)"：调用方会以为“这里没有”，转而换关键词重试 ——
        // 那正是要消除的打转。必须给出可行动的原因与下一步。
        var output = lines.Count == 0
            ? (staleSkipped > 0
                ? $"(no matches — every one of the {staleSkipped} index hit(s) was skipped as stale: "
                  + "the index snapshot for this scope points at paths that no longer exist; rebuild this scope's index, "
                  + "or omit 'backend' to use the managed scan path)\n" + summary
                : queryFallback is not null
                    ? $"(no matches — this query contains Lucene query-syntax characters, which the index backend "
                      + $"interprets with Lucene semantics, not as a regex; it was retried as '{queryFallback}' "
                      + "(explicit OR) and still returned 0 hits. Split the terms with spaces, use explicit 'OR'/'AND', "
                      + "or omit 'backend' to use the managed scan path, which evaluates the query as a regex.)\n" + summary
                    : "(no matches)\n" + summary)
            : string.Join('\n', lines) + "\n" + summary;
        return ToolExecutionResult.Ok(output,
            status: lines.Count == 0 ? ToolResultStatuses.NoMatch
                : truncated ? ToolResultStatuses.Truncated : null);
    }

    /// <summary>
    /// ADR-089 U4-5a：index 后端的 telemetry。指标名与旧路径的 search_attempt 分离，
    /// 避免两个后端的计数/维度互相污染；elapsed_ms 作为维度上报，供成本/延迟分析。
    /// </summary>
    private void ReportIndexBackendTelemetry(
        ToolExecutionContext context, string scope, string outcome, long elapsedMs)
    {
        ReportTelemetry(
            "search_backend_index",
            outcome is "timeout" or "error" ? TelemetryMetricStatuses.Failed : TelemetryMetricStatuses.Succeeded,
            outcome,
            new Dictionary<string, string>
            {
                ["backend"] = BackendIndex,
                ["scope"] = scope,
                ["outcome"] = outcome,
                ["elapsed_ms"] = elapsedMs.ToString(),
            },
            context);
    }

    private static string BuildDedupKey(string filePath, int lineNumber) =>
        $"{Path.GetFullPath(filePath).ToLowerInvariant()}|{lineNumber}";

    /// <summary>
    /// ADR-089 U4-5a：Lucene 查询语法字符集合。出现这些字符说明调用方给的很可能是正则/字面语法，
    /// 而索引后端会按 Lucene 语义解释——二者语义不同（实测 '|' 不等价于 OR），必须显式降级而非静默返空。
    /// </summary>
    private static readonly char[] LuceneSyntaxChars =
        ['|', '&', '(', ')', '"', '*', '?', '~', '^', ':', '[', ']', '{', '}', '+', '-', '!', '\\', '/'];

    /// <summary>降级重试时用于拆词的非词字符。</summary>
    private static readonly char[] FallbackTermSeparators =
        [' ', '\t', '\r', '\n', '|', '&', '(', ')', '"', '*', '?', '~', '^', ':', '[', ']', '{', '}', '+',
         '-', '!', '\\', '/', ',', ';', '=', '<', '>', '%', '$', '@', '#', '\''];

    /// <summary>
    /// ADR-089 U4-5a：把含 Lucene 语法字符的 query 降级成显式 OR 形态（已实测可用的形态）。
    /// 返回 false 表示无需/无法降级（不含语法字符，或拆不出 ≥2 个词——单词查询的 0 命中就是真的 0）。
    /// </summary>
    private static bool TryBuildOrFallbackQuery(string query, out string fallback)
    {
        fallback = string.Empty;
        if (query.IndexOfAny(LuceneSyntaxChars) < 0)
            return false;

        var terms = query
            .Split(FallbackTermSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (terms.Length < 2)
            return false;

        fallback = string.Join(" OR ", terms);
        return true;
    }

    /// <summary>
    /// ADR-089 U0 R1.2 / G2：路径级准入谓词——Lucene 候选与目录枚举共用同一约束：
    /// 存在、scope（搜索目录之内）、完整 glob（统一走 RetrievalGlobMatcher canonical 合同，
    /// 含 Keep*.txt 这类非纯扩展名 glob 与含分隔符 glob）、扩展名过滤、排除目录。
    /// 任一不满足 → 该路径被丢弃，不得作为命中输出。
    /// </summary>
    private static bool IsAdmissiblePath(
        string fullPath, string cwd, string? glob, string[]? extFilter, HashSet<string> excludeDirs)
    {
        if (!File.Exists(fullPath))
            return false;

        // scope：路径必须位于本次声明的搜索目录之内（相对路径已在收集阶段与 directory 合并）。
        var scopeRoot = Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(scopeRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        if (IsPathInExcludedDir(fullPath, cwd, excludeDirs))
            return false;

        // ADR-089 U0-G2：glob 判定统一走 RetrievalGlobMatcher，消除仓库内语义分叉
        // （旧语义 A=MatchesSimpleExpression 仅文件名、*.* 要求含点；旧语义 B=Win32 searchPattern
        // 的 *.txt 会误配 a.txtx）。含分隔符 glob 按 cwd 相对路径匹配（分隔符归一为 /，* 不跨 /）。
        // ignoreCase 恒为 true：Windows First，与 Directory.GetFiles 的大小写不敏感行为一致。
        if (!string.IsNullOrWhiteSpace(glob))
        {
            var relative = NormalizeRelativePath(cwd, fullPath);
            if (!RetrievalGlobMatcher.Matches(Path.GetFileName(fullPath), relative, glob, ignoreCase: true))

                return false;
        }

        if (extFilter is { Length: > 0 })
        {
            var ext = Path.GetExtension(fullPath);
            if (!extFilter.Contains(ext, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>G2：root 相对路径归一（\ → /），供 RetrievalGlobMatcher 相对路径语义使用。</summary>
    private static string NormalizeRelativePath(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private async Task<ToolExecutionResult> ManagedGrepAsync(
        RetrievalMatcher matcher, string? pattern, string? directory,
        string[]? extFilter, HashSet<string> excludeDirs, long maxLineBytes, long maxTotalBytes,
        int maxResults, List<string> candidatePaths,
        CancellationTokenSource cts, CancellationToken ct)
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
        int enumerationErroredDirs = 0;
        try
        {
            // 枚举阶段即按目录名剪枝排除目录（bin/obj 等整棵子树不再进入枚举），
            // 避免排除目录中的文件占用 MaxEnumeratedFiles 名额，导致真实源码目录被跳过（假阴性）。
            files = EnumerateFilesMatchingGlob(cwd, filePattern, excludeDirs, MaxEnumeratedFiles,
                out enumerationTruncated, out enumerationErroredDirs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SearchGrep] Enumeration error");
            return ToolExecutionResult.Fail($"Search error: {ex.Message}");
        }

        // ADR-089 U0 R2：唯一 cts 已在 SearchCoreAsync 入口创建（覆盖候选+枚举+扫描全流程），
        // 此处只消费；token 是内部预算与调用方取消的联合令牌。
        var token = cts.Token;

        // ADR-089 U0 R1 / G2：候选与枚举文件统一先过同一准入谓词 IsAdmissiblePath 再入工作清单；
        // 候选排最前（Lucene 相关度序），其后是枚举文件（枚举顺序）。枚举文件再过一次谓词是幂等的
        // （glob/排除/scope 结论一致），换取「两条路径同一合同」的结构保证。
        // processedFiles 保证每个文件只被处理一次：候选与枚举重叠时不重复扫描、不重复输出。
        var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workList = new List<string>(files.Count + candidatePaths.Count);
        foreach (var candidate in candidatePaths)
        {
            if (IsAdmissiblePath(candidate, cwd, pattern, extFilter, excludeDirs))
                workList.Add(candidate);
        }
        foreach (var file in files)
        {
            if (IsAdmissiblePath(file, cwd, pattern, extFilter, excludeDirs))
                workList.Add(file);
        }

        var results = new List<string>();
        var emittedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalResultBytes = 0;
        long matchCount = 0;
        bool totalCapReached = false;
        bool scanBudgetExceeded = false;
        bool errorBudgetExceeded = false;
        bool regexTimedOut = false;
        bool maxResultsReached = false;
        foreach (var file in workList)
        {
            if (token.IsCancellationRequested || totalCapReached || maxResultsReached) break;
            if (errors >= MaxErrors) { errorBudgetExceeded = true; break; }
            if (scannedFiles >= MaxScannedFiles || scannedBytes >= MaxScannedBytes)
            {
                scanBudgetExceeded = true;
                break;
            }

            // ADR-089 U0 G2：扩展名过滤已由工作清单装配阶段的 IsAdmissiblePath 统一覆盖，
            // 扫描循环内不再重复判断（消除两处语义分叉的死代码）。
            // ADR-089 U0 R1.5：候选与枚举可能命中同一文件——每文件只处理一次。
            if (!processedFiles.Add(Path.GetFullPath(file))) continue;

            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxFileSizeBytes) { skippedLargeFiles++; continue; }

                var raw = await File.ReadAllBytesAsync(file, token);
                scannedFiles++;
                scannedBytes += raw.Length;

                // 二进制保护：含 NUL 字节视为二进制文件，跳过（不计入匹配）
                if (Array.IndexOf(raw, (byte)0) >= 0) continue;

                var text = Encoding.UTF8.GetString(raw);
                if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..]; // 去除 UTF-8 BOM
                var lines = text.Split('\n');

                for (int i = 0; i < lines.Length; i++)
                {
                    if (totalCapReached || maxResultsReached) break;
                    // R2.3：每行先检查取消——内部预算超时也必须及时停下扫描。
                    if (token.IsCancellationRequested) break;
                    var line = lines[i].TrimEnd('\r');
                    // 统一匹配合同（ADR-089 L128）：与候选准入同源，两条路径结论一致；
                    // TryMatch 区分「不匹配」与「正则求值超时（未能判定）」。
                    var outcome = matcher.TryMatch(line, token);
                    if (outcome == RetrievalMatchOutcome.Timeout)
                    {
                        regexTimedOut = true;
                        break;
                    }
                    if (outcome == RetrievalMatchOutcome.NoMatch) continue;

                    matchCount++;
                    // 行级去重键保留为兜底（R1.5）：同一 (文件, 行) 只输出一次，但仍计入扫描统计（matchCount）。
                    if (!emittedKeys.Add(BuildDedupKey(file, i + 1))) continue;

                    // ADR-089 U0 R4.1：max_results 统一作用于合并后的结果集——
                    // 候选文件与枚举文件共用 results 计数，出现第 maxResults+1 个匹配即停止扫描并声明 truncated。
                    if (results.Count >= maxResults)
                    {
                        maxResultsReached = true;
                        break;
                    }
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // R3.3：调用方取消必须传播，不得落为任何结果状态
            }
            catch (OperationCanceledException)
            {
                break; // 内部预算超时打断读取：由 timedOut/regexTimedOut 表达
            }
            catch { errors++; }
        }

        // R2.4：区分取消来源——调用方取消在上面已直接传播，走到这里 ct 必然未取消；
        // cts 已触发即内部预算超时。
        ct.ThrowIfCancellationRequested();
        bool timedOut = cts.IsCancellationRequested;

        // ADR-089 U0 R3.1：覆盖完整性覆盖「任何未完成的部分」——错误预算（MaxErrors）只是停止阈值，
        // 任何已发生的读取失败（哪怕 1 次、未达阈值）都使覆盖非 Complete；
        // 调用方取消已向上传播不会到达这里；regexTimedOut 同样是「未能判定」。
        bool coverageComplete = !timedOut && !regexTimedOut && !enumerationTruncated
            && enumerationErroredDirs == 0 && !scanBudgetExceeded && !maxResultsReached
            && !totalCapReached && skippedLargeFiles == 0 && errors == 0;

        var notes = new List<string>();
        if (timedOut)
            notes.Add($"搜索超时（{_searchTimeout.TotalSeconds:0.##}s），结果可能不完整，建议缩小 directory/pattern/file_ext 范围，或改用索引工具 code_symbol_search / code_explore / file_search");
        if (regexTimedOut)
            notes.Add($"正则求值超时（{_searchTimeout.TotalSeconds:0.##}s），未能完成判定，结果可能不完整；建议简化 query（避免灾难性回溯）或缩小范围");
        if (enumerationTruncated)
            notes.Add(string.Format(EnumerationTruncatedMessage, MaxEnumeratedFiles));
        if (enumerationErroredDirs > 0)
            notes.Add(string.Format(EnumerationErroredMessage, enumerationErroredDirs));
        if (scanBudgetExceeded)
            notes.Add(string.Format(ScanBudgetMessage, MaxScannedFiles, MaxScannedBytes));
        if (totalCapReached)
            notes.Add(string.Format(TotalCapMessage, matchCount));
        if (errorBudgetExceeded)
            notes.Add(string.Format(ErrorBudgetMessage, MaxErrors));
        else if (errors > 0)
            notes.Add(string.Format(ReadErrorsMessage, errors));
        if (skippedLargeFiles > 0)
            notes.Add(string.Format(LargeFileSkippedMessage, skippedLargeFiles));
        if (maxResultsReached)
            notes.Add(string.Format(MaxResultsReachedMessage, maxResults));
        if (!coverageComplete)
            notes.Add(string.Format(CoveragePartialMessage, scannedFiles, MaxScannedFiles, scannedBytes, MaxScannedBytes));

        // 状态映射（S2-4）：partial/truncated/timeout 一律不得用 Ok；只有覆盖 Complete 的空结果才是 no_match。
        // R2：内部超时（整体预算或正则求值）都映射 timeout，绝不落为 no_match。
        bool timeoutLike = timedOut || regexTimedOut;
        string status;
        if (timeoutLike)
            status = ToolResultStatuses.Timeout;
        else if (coverageComplete)
            status = results.Count == 0 ? ToolResultStatuses.NoMatch : ToolResultStatuses.Ok;
        else
            status = ToolResultStatuses.Truncated;

        if (results.Count == 0)
        {
            var notesText = notes.Count > 0 ? "\n" + string.Join("\n", notes) : string.Empty;
            if (timeoutLike)
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
    /// ADR-089 U0-G2：glob 不再交给 Win32 searchPattern（旧语义 B 的 *.txt 会误配 a.txtx）——
    /// 目录内取全部文件（pattern 恒为 "*"），统一用 RetrievalGlobMatcher 过滤；
    /// 上限 maxMatchedFiles 只统计命中文件（matched-only），未命中文件不占名额，杜绝假截断。
    /// glob 恒用 ignoreCase: true（Windows First；与 Directory.GetFiles 大小写不敏感行为一致）。
    /// </summary>
    private static List<string> EnumerateFilesMatchingGlob(
        string root, string? glob, HashSet<string> excludeDirs, int maxMatchedFiles,
        out bool truncated, out int failedDirs)
    {
        var files = new List<string>(1024);
        truncated = false;
        failedDirs = 0;
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
                // G2：glob 不交给 Win32（语义 B），取全部文件后统一过 RetrievalGlobMatcher。
                dirFiles = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception) when (dir != root)
            {
                // ADR-089 U0 R3.2：子目录枚举失败必须计入完整性（failedDirs），
                // 不得与符号链接防循环跳过（visited 集合）混同后静默吞掉。
                failedDirs++;
                continue;
            }
            // 根目录枚举失败则向上抛出，由调用方转换为 Fail

            foreach (var sub in subDirs)
            {
                if (excludeDirs.Contains(Path.GetFileName(sub))) continue;
                stack.Push(sub);
            }

            foreach (var file in dirFiles)
            {
                // G2：只有命中 glob 的文件才计入名额（matched-only），未命中文件不占枚举上限。
                var rel = NormalizeRelativePath(root, file);
                if (!RetrievalGlobMatcher.Matches(Path.GetFileName(file), rel, glob, ignoreCase: true))
                    continue;
                if (files.Count >= maxMatchedFiles)
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
    [ToolParam("Directories to exclude, semicolon-separated. Default: the single-source noise directory set (PathNoiseRules.DirectoryNames) — build artifacts, dependencies, IDE and tool output, including .pudding/.tmp-build/.pnpm-store/.tmp-test-out.")]
    public string? ExcludeDirs { get; init; }
    [ToolParam("Extra directories to exclude, appended to the effective exclude list, semicolon-separated")]
    public string? ExcludeDirsAppend { get; init; }
    [ToolParam("Max bytes per matching line before truncation. 0 disables truncation. Default: 8192")]
    public long? MaxLineBytes { get; init; }
    [ToolParam("Max total bytes of results before truncation. 0 disables the cap. Default: 16384")]
    public long? MaxTotalBytes { get; init; }
    [ToolParam("Search backend routing. 'scan' (default) = legacy managed scan, unchanged behavior (2000-file / 64MB / 10s caps apply). 'index' = full-text (Lucene) index backend: queries the index directly, runs no managed scan, and returns engineMs/totalMs timing. Unknown values fail with a contract error. Requires the scope to be indexed.")]
    public string? Backend { get; init; }
}
