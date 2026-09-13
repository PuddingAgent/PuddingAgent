using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCode.Tools.Retrieval;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "file_search",
    name: "Search files",
    description: "按文件名搜索（文件搜索）并返回规范化绝对路径。默认使用 Everything Provider（如有）。Everything 需要绝对目录；验证错误会包含从宿主机枚举到的可用盘符根。搜索多个盘时对每个盘根调用一次。Everything 不可用时回退到内置递归文件搜索。使用 action=list 可检查提供方。",
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
        string? fallbackFrom = null;
        string? fallbackReason = null;
        var requireProvider = args.RequireProvider == true;
        var requestedProvider = args.Provider?.Trim();

        if (!string.IsNullOrWhiteSpace(requestedProvider)
            && !string.Equals(requestedProvider, "auto", StringComparison.OrdinalIgnoreCase))
        {
            providerId = requestedProvider;
            provider = _providers.FirstOrDefault(p =>
                string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (provider == null)
                return ToolExecutionResult.Fail($"File search provider not found: {providerId}");
            if (!provider.IsAvailable)
            {
                if (requireProvider)
                    return ToolExecutionResult.Fail($"Provider {providerId} is not available on this host.");

                var builtInFallback = FindBuiltInProvider();
                if (builtInFallback is null)
                    return ToolExecutionResult.Fail(
                        $"Provider {providerId} is not available on this host and no fallback provider is available.");

                fallbackFrom = providerId;
                fallbackReason = "requested provider is unavailable on this host";
                provider = builtInFallback;
                providerId = builtInFallback.ProviderId;
            }
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
                    // ADR-089 U0-S3（D-f）：auto 降级不再静默——注册了 Everything provider 但不可用时，
                    // 显式声明 fallback（覆盖/降级说明单点产出，见 BuildSearchOutput）。
                    if (everythingProvider is not null)
                    {
                        fallbackFrom = everythingProvider.ProviderId;
                        fallbackReason = "everything is unavailable on this host";
                    }
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

            // 相对目录不直接拒绝：先按与 BuiltIn 分支同源的方式归一化为 workspace 绝对路径，
            // 只有最终无效/不存在的路径才会在下方 Directory.Exists 检查处返回 allowed roots 提示。
            // 这消除了“Everything 绝对目录 97 次”无效调用的根因（相对路径被误报为错误）。
        }

        var directory = string.IsNullOrWhiteSpace(args.Directory) ? "." : args.Directory;
        if (!Path.IsPathRooted(directory))
        {
            directory = Path.GetFullPath(Path.Combine(
                HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory),
                directory));
        }

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
            var primaryResult = await provider.SearchWithCoverageAsync(directory, pattern, recursive, maxResults, ct);
            var (results, coverage) = await MergeWithBaselineAsync(
                primaryResult, providerId, requireProvider, directory, pattern, recursive, maxResults, ct);
            var output = BuildSearchOutput(results, fallbackFrom, providerId, fallbackReason);
            // ADR-089 U0-S3：覆盖声明单点产出，且仅非 Complete 时出现（对齐 U0-S2 SearchGrepTool
            // 惯例：Complete = 无声明行 + status ok/no_match）；同一声明恰好一次（U0-S2 教训见 ff79f3b）。
            if (!coverage.IsComplete)
                output += Environment.NewLine + BuildCoverageNote(coverage);
            var noMatch = coverage.IsComplete && results.Count == 0;
            if (noMatch)
                output += Environment.NewLine + Environment.NewLine + BuildNoResultsGuidance(providerId, directory, pattern, recursive);
            return ToolExecutionResult.Ok(output, status: BuildResultStatus(coverage, results.Count));
        }
        catch (Exception ex) when (
            IsEverythingProvider(providerId)
            && !requireProvider
            && FindBuiltInProvider() is not null)
        {
            var builtInFallback = FindBuiltInProvider()!;
            try
            {
                // 降级到基线枚举时其覆盖状态已自定案（Complete/Truncated），无需再差分。
                var fallbackResult = await builtInFallback.SearchWithCoverageAsync(directory, pattern, recursive, maxResults, ct);
                var results = NormalizeAbsolutePaths(fallbackResult.Paths, directory);
                var output = BuildSearchOutput(
                    results,
                    providerId,
                    builtInFallback.ProviderId,
                    $"provider query failed: {ex.Message}");
                if (!fallbackResult.Coverage.IsComplete)
                    output += Environment.NewLine + BuildCoverageNote(fallbackResult.Coverage);
                var noMatch = fallbackResult.Coverage.IsComplete && results.Count == 0;
                if (noMatch)
                {
                    output += Environment.NewLine + Environment.NewLine +
                              BuildNoResultsGuidance(
                                  builtInFallback.ProviderId,
                                  directory,
                                  pattern,
                                  recursive);
                }

                return ToolExecutionResult.Ok(output, status: BuildResultStatus(fallbackResult.Coverage, results.Count));
            }
            catch (Exception fallbackException)
            {
                return ToolExecutionResult.Fail(
                    $"File search failed using provider {providerId}: {ex.Message}. " +
                    $"Fallback provider {builtInFallback.ProviderId} also failed: {fallbackException.Message}");
            }
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail($"File search failed using provider {providerId}: {ex.Message}");
        }
    }

    private IFileSearchProvider? FindBuiltInProvider() =>
        _providers.FirstOrDefault(p =>
            string.Equals(
                p.ProviderId,
                "BuiltInRecursiveFileSearch",
                StringComparison.OrdinalIgnoreCase)
            && p.IsAvailable);

    private static string BuildSearchOutput(
        IReadOnlyList<string> results,
        string? fallbackFrom,
        string selectedProvider,
        string? fallbackReason)
    {
        var output = JsonSerializer.Serialize(results);
        if (string.IsNullOrWhiteSpace(fallbackFrom))
            return output;

        return output + Environment.NewLine + Environment.NewLine +
               $"Provider fallback: {fallbackFrom} -> {selectedProvider}. Reason: {fallbackReason}.";
    }

    private static IReadOnlyList<string> NormalizeAbsolutePaths(
        IReadOnlyList<string> results,
        string searchRoot)
    {
        return results
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(searchRoot, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsEverythingProvider(string providerId) =>
        string.Equals(providerId, "Everything", StringComparison.OrdinalIgnoreCase);

    // ADR-089 U0-S3（设计 L100/L107/L128）：主 provider 清单不可验证（Partial）时，
    // 以内置全量枚举为基线做同 scope 差分补足，防止「索引清单遗漏新文件」被误报为 no_match。
    private async Task<(IReadOnlyList<string> Paths, RetrievalCoverage Coverage)> MergeWithBaselineAsync(
        FileSearchProviderResult primary,
        string primaryProviderId,
        bool requireProvider,
        string directory,
        string pattern,
        bool recursive,
        int maxResults,
        CancellationToken ct)
    {
        primary = primary with { Paths = NormalizeAbsolutePaths(primary.Paths, directory) };

        // Complete/Truncated 已定案；Truncated 时结果集已满额，补足不改变覆盖结论（合法集合运算豁免）。
        if (primary.Coverage.Status is RetrievalCoverageStatus.Complete or RetrievalCoverageStatus.Truncated)
            return (primary.Paths, primary.Coverage);

        var builtIn = FindBuiltInProvider();
        if (builtIn is null || requireProvider ||
            string.Equals(builtIn.ProviderId, primaryProviderId, StringComparison.OrdinalIgnoreCase))
        {
            // 无法差分（无基线/显式要求单一 provider）：保持 Partial 诚实上报，不得伪装 Complete 或 no_match。
            return (primary.Paths, primary.Coverage);
        }

        FileSearchProviderResult baseline;
        try
        {
            baseline = await builtIn.SearchWithCoverageAsync(directory, pattern, recursive, maxResults, ct);
        }
        catch (Exception ex)
        {
            // 差分失败不否定主结果：保留清单结果并声明无法补足（设计 L108：不得静默降级）。
            return (primary.Paths, RetrievalCoverage.Partial(
                $"differential enumeration failed: {ex.Message}",
                primary.Coverage.Reasons));
        }

        var mergedPaths = primary.Paths
            .Concat(baseline.Paths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (baseline.Coverage.Status == RetrievalCoverageStatus.Truncated || mergedPaths.Count > maxResults)
        {
            return (mergedPaths.Take(maxResults).ToArray(),
                RetrievalCoverage.Of(RetrievalCoverageStatus.Truncated,
                    $"scope may contain more matches than the result limit {maxResults}"));
        }

        if (baseline.Coverage.Status != RetrievalCoverageStatus.Complete)
        {
            // 基线自身中断：并集不完整，合并双方原因诚实上报。
            return (mergedPaths,
                new RetrievalCoverage(
                    RetrievalCoverageStatus.Partial,
                    primary.Coverage.Reasons.Concat(baseline.Coverage.Reasons).ToList(),
                    isComplete: false));
        }

        // 基线自然完成 → 差分实测定案（设计 L100：覆盖与时效由差分验证）。
        var missedCount = mergedPaths
            .Except(primary.Paths, StringComparer.OrdinalIgnoreCase)
            .Count();
        return missedCount > 0
            ? (mergedPaths, RetrievalCoverage.Partial(
                $"everything manifest missed {missedCount} file(s) present on disk; differential enumeration added them"))
            : (mergedPaths, RetrievalCoverage.Complete(
                ["everything manifest verified against on-disk enumeration"]));
    }

    // 覆盖声明单点产出：整个输出中恰好出现一次（U0-S2 教训：同一声明出现两次）。
    private static string BuildCoverageNote(RetrievalCoverage coverage) =>
        $"(coverage: {coverage.Status.ToString().ToLowerInvariant()}" +
        (coverage.Status == RetrievalCoverageStatus.Complete
            ? " — the declared scope was fully enumerated; an empty result is a verified no-match)"
            : $" — {string.Join("; ", coverage.Reasons)})");

    // 状态映射（对齐 U0-S2 SearchGrepTool）：只有覆盖 Complete 才允许 no_match；
    // 非 Complete 一律 truncated（结构化状态），细节由覆盖声明承载。
    private static string BuildResultStatus(RetrievalCoverage coverage, int resultCount) =>
        !coverage.IsComplete ? ToolResultStatuses.Truncated
        : resultCount == 0 ? ToolResultStatuses.NoMatch
        : ToolResultStatuses.Ok;

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

    [ToolParam("Require the explicitly selected provider. Default: false, so unavailable or failed Everything searches fall back to BuiltInRecursiveFileSearch.")]
    public bool? RequireProvider { get; init; }

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
    /// <summary>Returns normalized absolute file paths.</summary>
    Task<IReadOnlyList<string>> SearchAsync(string directory, string pattern, bool recursive, int maxResults, CancellationToken ct);

    /// <summary>
    /// ADR-089 U0-S3（设计 L100/L107/L108）：provider 侧必须声明结果覆盖状态，调用方据此决定
    /// 是否需要差分补足同 scope 枚举。默认实现包装 <see cref="SearchAsync"/> 并声明 Complete——
    /// 旧合同没有“不完整”信号，按既有语义信任其自述完整，保证外部实现零改动兼容。
    /// </summary>
    async Task<FileSearchProviderResult> SearchWithCoverageAsync(string directory, string pattern, bool recursive, int maxResults, CancellationToken ct)
    {
        var paths = await SearchAsync(directory, pattern, recursive, maxResults, ct);
        return new FileSearchProviderResult(paths, RetrievalCoverage.Complete());
    }
}

/// <summary>ADR-089 U0-S3：provider 搜索结果与覆盖报告。复用 U0 覆盖合同（RetrievalCoverage），不新增第二套契约 DTO。</summary>
public sealed record FileSearchProviderResult(IReadOnlyList<string> Paths, RetrievalCoverage Coverage);

internal sealed class BuiltInRecursiveFileSearchProvider : IFileSearchProvider
{
    public string ProviderId => "BuiltInRecursiveFileSearch";
    public string DisplayName => "Built-in recursive file search";
    public bool IsAvailable => true;

    // ADR-089 U0-G3：legacy 非覆盖路径不再把 pattern 直接交给 Win32 searchPattern——
    // Windows 前导匹配怪癖（*.txt 命中 a.txtx）与统一匹配合同冲突。改为 "*"
    // 全量枚举 + FileSearchPatternMatcher 过滤（通配 = canonical glob 合同；非通配 =
    // 大小写不敏感子串包含），先过滤再 Take，防止截断发生在过滤之前造成漏文件。
    public Task<IReadOnlyList<string>> SearchAsync(string directory, string pattern, bool recursive, int maxResults, CancellationToken ct)
    {
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var root = Path.GetFullPath(directory);
        var results = Directory.EnumerateFiles(directory, "*", searchOption)
            .Where(path => FileSearchPatternMatcher.Matches(path, root, pattern))
            .Take(maxResults)
            .Select(Path.GetFullPath)
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(results);
    }

    // ADR-089 U0-S3：基线枚举必须与 Everything 路径同一匹配合同（设计 L128）——
    // 全量枚举 "*" 后统一用 FileSearchPatternMatcher 复核，消除 Windows searchPattern
    // （3 字符扩展名怪癖等）与 glob 语义的口径分歧；覆盖状态诚实上报，供上层差分定案。
    public Task<FileSearchProviderResult> SearchWithCoverageAsync(string directory, string pattern, bool recursive, int maxResults, CancellationToken ct)
    {
        var root = Path.GetFullPath(directory);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            // 仅跳过 reparse point 防 junction 循环；Hidden/System 必须枚举，否则基线漏文件。
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var results = new List<string>();
        var hitResultLimit = false;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", options))
            {
                if (!FileSearchPatternMatcher.Matches(path, root, pattern))
                    continue;
                if (results.Count >= maxResults)
                {
                    // 第 maxResults+1 个匹配出现才判截断：恰好等于上限且枚举自然结束时仍是全量。
                    hitResultLimit = true;
                    break;
                }

                results.Add(path);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 设计 L108：枚举中断不得伪装完整——保留部分结果并声明 Partial。
            return Task.FromResult(new FileSearchProviderResult(
                results.Select(Path.GetFullPath).ToArray(),
                RetrievalCoverage.Partial($"builtin enumeration interrupted: {ex.Message}")));
        }

        var coverage = hitResultLimit
            ? RetrievalCoverage.Of(RetrievalCoverageStatus.Truncated,
                $"builtin enumeration found more than {maxResults} matches")
            : RetrievalCoverage.Complete();
        return Task.FromResult(new FileSearchProviderResult(
            results.Select(Path.GetFullPath).ToArray(), coverage));
    }
}

internal interface IEverythingSdk
{
    bool IsAvailable(out string? error);
    Task<EverythingQueryResult> QueryAsync(EverythingQueryRequest request, CancellationToken ct);
}

internal sealed record EverythingQueryRequest(string Directory, string Pattern, int MaxResults);

// ADR-089 U0-S3：IsCompleteManifest 供测试桩/未来 SDK 自证清单完整；真实 Everything64.dll
// 无该出口，恒为 false → 上层必须差分补足（设计 L100：覆盖和时效不可验证时补充现有文件枚举）。
internal sealed record EverythingQueryResult(IReadOnlyList<EverythingQueryItem> Items, bool IsCompleteManifest = false);

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
            .Select(i => Path.GetFullPath(i.FullPath))
            .Where(path => FileSearchPathHelpers.IsInsideDirectory(path, root))
            .Where(path => recursive || IsDirectChild(path, rootTrimmed))
            .Where(path => FileSearchPatternMatcher.Matches(path, root, pattern))
            .Take(maxResults)
            .ToArray();
    }

    // ADR-089 U0-S3（设计 L100/L107/L108）：Everything 是索引快照，结果必须按真实路径 +
    // scope + glob 后置复核；清单完整性无 SDK 自证出口时声明 Partial，由上层差分补足定案。
    public async Task<FileSearchProviderResult> SearchWithCoverageAsync(string directory, string pattern, bool recursive, int maxResults, CancellationToken ct)
    {
        if (!_sdk.IsAvailable(out var unavailableReason))
            throw new InvalidOperationException(unavailableReason ?? "Everything SDK is not available.");

        var request = new EverythingQueryRequest(directory, pattern, maxResults);
        var result = await _sdk.QueryAsync(request, ct);
        var root = Path.GetFullPath(directory);
        var rootTrimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 后置校验（设计 L107）：File.Exists 滤掉索引滞后的幽灵条目（已删除文件仍可能在清单中）。
        var paths = result.Items
            .Select(i => Path.GetFullPath(i.FullPath))
            .Where(path => File.Exists(path))
            .Where(path => FileSearchPathHelpers.IsInsideDirectory(path, root))
            .Where(path => recursive || IsDirectChild(path, rootTrimmed))
            .Where(path => FileSearchPatternMatcher.Matches(path, root, pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 截断判定：结果集满额；或 SDK 原始条目已撞 Everything_SetMax 上限——SetMax 截断发生在
        // 过滤之前，过滤后不满额不代表没截断，按可能截断处理并要求差分，不得当作完整清单。
        var coverage = paths.Length >= maxResults
            ? RetrievalCoverage.Of(RetrievalCoverageStatus.Truncated,
                $"everything manifest reached the result limit {maxResults}; scope may contain more matches")
            : result.Items.Count >= maxResults
                ? RetrievalCoverage.Partial(
                    $"everything index hit its result limit {maxResults} before filtering; manifest may omit matches")
                : result.IsCompleteManifest
                    ? RetrievalCoverage.Complete()
                    : RetrievalCoverage.Partial(
                        "everything manifest completeness is not verifiable for this scope; differential enumeration required");
        return new FileSearchProviderResult(paths, coverage);
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

/// <summary>
/// 文件级 pattern 过滤（ADR-089 U0-G3 起分两个显式分支）：
/// <list type="bullet">
/// <item>含通配符（<c>*</c>/<c>?</c>）：canonical glob 合同，统一委托
/// <see cref="RetrievalGlobMatcher"/>（** 前缀单次剥离、[]{} 字面、整串 *.* ≡ *、
/// 分隔符归一为 /、* 与 ? 不跨 /）。</item>
/// <item>不含通配符：<b>大小写不敏感子串包含，不是 glob 合同</b>（父级决策 2026-09-13
/// 显式保留）。依据：Pattern 参数语义是「文件名文本」过滤（工具描述
/// "File name text or glob pattern. Default: *"，默认值 <c>*</c>），改成精确匹配属未授权
/// 行为回归；U1 计划把「pattern 文本」与「glob 过滤器」拆为两个参数后在此标注迁移点。</item>
/// </list>
/// </summary>
internal static class FileSearchPatternMatcher
{
    public static bool Matches(string path, string rootDirectory, string pattern)
    {
        var value = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;
        var fileName = Path.GetFileName(path);
        var relativePath = Path.GetRelativePath(rootDirectory, path);

        // 非通配分支：大小写不敏感子串包含，不是 glob 合同（见类型级 XML doc；父级决策显式保留）。
        if (!value.Contains('*') && !value.Contains('?'))
        {
            return fileName.Contains(value, StringComparison.OrdinalIgnoreCase)
                   || relativePath.Contains(value, StringComparison.OrdinalIgnoreCase);
        }

        // 通配分支（G3 统一）：** 前缀剥离由 RetrievalGlobMatcher 规范 2 内建；
        // 本地 **/ 剥离正则与 GlobLikeMatch（* 可跨 / 的旧语义）已删除。
        return RetrievalGlobMatcher.Matches(fileName, relativePath, value, ignoreCase: true);
    }

    public static bool MatchesFileOrPath(string path, string pattern)
    {
        var value = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;
        var fileName = Path.GetFileName(path);

        // 非通配分支：同 Matches，显式保留子串包含语义（not-glob 契约）。
        if (!value.Contains('*') && !value.Contains('?'))
        {
            return fileName.Contains(value, StringComparison.OrdinalIgnoreCase)
                   || path.Contains(value, StringComparison.OrdinalIgnoreCase);
        }

        // 通配分支（G3 统一）：canonical glob 无绝对路径模式——把绝对路径分隔符归一为 /
        // 后作为 relativePath 实参传入（父级决策 2026-09-13），glob 含分隔符时按该归一路径匹配。
        return RetrievalGlobMatcher.Matches(fileName, path.Replace('\\', '/'), value, ignoreCase: true);
    }
}
