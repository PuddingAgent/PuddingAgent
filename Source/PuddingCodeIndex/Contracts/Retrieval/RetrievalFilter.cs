using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 过滤面的名字（ADR-089 §2.3）：用于"放宽哪个面会得到结果"（<see cref="RetrievalEmptyReasonPolicy.SuggestRelaxationFacet"/>）
/// 与"我替你加了哪些面"的回显。**枚举即闭集**，因此放宽建议不可能是自由文本乱写。
/// </summary>
public enum RetrievalFacet
{
    /// <summary>匹配域（符号名 / 声明 / 文档注释 / 正文 / 路径）。</summary>
    MatchTarget = 0,

    /// <summary>符号种类（复用 <c>CodeSymbolKind</c>）。</summary>
    SymbolKind = 1,

    /// <summary>命中层（<see cref="RetrievalHitKind"/>）。</summary>
    HitKind = 2,

    /// <summary>关系类型（<see cref="RetrievalRelationKind"/>）。</summary>
    RelationKind = 3,

    /// <summary>置信度下限（<see cref="RetrievalConfidence"/>）。</summary>
    ConfidenceFloor = 4,

    /// <summary>文件类型 / 扩展名。</summary>
    FileExtension = 5,

    /// <summary>目录 / 子目录（含递归开关）。</summary>
    Directory = 6,
}

/// <summary>
/// 正交过滤面（ADR-089 §2.3）：各面**可独立开关、AND 组合**。
/// <para>
/// 用户第七轮裁定原文："假设：要搜索一个 conf 的类，那么 Agent 可以给出过滤条件，
/// 比如搜 '.cs'、限制目录、只关注类名称等，可以有效的过滤掉噪音。"
/// 实测证据：<c>search_grep("conf", dir=Source/PuddingRuntime/Services/Messaging, ext=cs)</c>
/// ⇒ <c>(no matches)</c> —— 加"目录 + 扩展名"两个面后，查询精确到**可以合法地返回空**。
/// </para>
/// <para>
/// 空洞取值在这里被拒：<see cref="RetrievalMatchTarget.None"/>（必然空结果）、
/// 空扩展名、以及"关掉递归却不给目录"（无意义的开关）都会在构造时抛异常。
/// </para>
/// </summary>
public sealed record RetrievalFilter
{
    private readonly string[] _fileExtensions;
    private readonly CodeSymbolKind[] _symbolKinds;
    private readonly RetrievalHitKind[] _hitKinds;
    private readonly RetrievalRelationKind[] _relationKinds;

    /// <summary>构造过滤器。所有面都可省略；省略 = 该面不生效。</summary>
    public RetrievalFilter(
        IEnumerable<string>? fileExtensions = null,
        string? directoryPath = null,
        bool recurse = true,
        RetrievalMatchTarget? matchTargets = null,
        IEnumerable<CodeSymbolKind>? symbolKinds = null,
        IEnumerable<RetrievalHitKind>? hitKinds = null,
        IEnumerable<RetrievalRelationKind>? relationKinds = null,
        RetrievalConfidence? confidenceFloor = null)
    {
        if (directoryPath is null && !recurse)
            throw new ArgumentException(
                "递归开关（recurse=false）必须同时给出目录面 —— 没有目录就没有\"是否递归\"的语义，该开关是无界噪音。",
                nameof(recurse));

        if (matchTargets is { } targets && targets == RetrievalMatchTarget.None)
            throw new ArgumentException(
                "匹配域为 None 的过滤器必然返回空结果 —— 这是\"空洞否定\"的燃料，不可表示。",
                nameof(matchTargets));

        _fileExtensions = NormalizeExtensions(fileExtensions);
        _symbolKinds = DistinctSorted(symbolKinds);
        _hitKinds = DistinctSorted(hitKinds);
        _relationKinds = DistinctSorted(relationKinds);
        DirectoryPath = directoryPath is null ? null : NormalizeDirectory(directoryPath);
        Recurse = directoryPath is null || recurse;
        MatchTargets = matchTargets;
        ConfidenceFloor = confidenceFloor;
        HasAnyConstraint = _fileExtensions.Length > 0
            || DirectoryPath is not null
            || MatchTargets is not null
            || _symbolKinds.Length > 0
            || _hitKinds.Length > 0
            || _relationKinds.Length > 0
            || ConfidenceFloor is not null;
    }

    /// <summary>不带任何约束的过滤器（等价于"不过滤"）。</summary>
    public static RetrievalFilter None { get; } = new();

    /// <summary>文件类型面（用户第七轮举的第一个例子：搜 <c>.cs</c>）。</summary>
    public static RetrievalFilter ForFileExtensions(params string[] extensions) => new(fileExtensions: extensions);

    /// <summary>目录面（用户第七轮举的第二个例子：限制目录）。</summary>
    public static RetrievalFilter ForDirectory(string directoryPath, bool recurse = true) =>
        new(directoryPath: directoryPath, recurse: recurse);

    /// <summary>匹配域面（用户第七轮举的第三个例子：只关注类名称）。</summary>
    public static RetrievalFilter ForMatchTargets(RetrievalMatchTarget matchTargets) =>
        new(matchTargets: matchTargets);

    /// <summary>符号种类面（复用 <c>code_symbol_search</c> 已有的 <c>kind</c> 参数语义，不重造）。</summary>
    public static RetrievalFilter ForSymbolKinds(params CodeSymbolKind[] kinds) => new(symbolKinds: kinds);

    /// <summary>扩展名面（规范化后的 <c>.cs</c> 形式，已去重并确定性排序）。</summary>
    public IReadOnlyList<string> FileExtensions => _fileExtensions;

    /// <summary>目录面（归一化为 <c>/</c> 分隔）或 null。</summary>
    public string? DirectoryPath { get; }

    /// <summary>目录面是否递归（无目录面时恒为 true）。</summary>
    public bool Recurse { get; }

    /// <summary>匹配域面（null = 不限）。</summary>
    public RetrievalMatchTarget? MatchTargets { get; }

    /// <summary>符号种类面（空 = 不限）。</summary>
    public IReadOnlyList<CodeSymbolKind> SymbolKinds => _symbolKinds;

    /// <summary>命中层面（空 = 不限）。</summary>
    public IReadOnlyList<RetrievalHitKind> HitKinds => _hitKinds;

    /// <summary>关系类型面（空 = 不限）。</summary>
    public IReadOnlyList<RetrievalRelationKind> RelationKinds => _relationKinds;

    /// <summary>置信度下限面（null = 不限；按信任秩"≥ 下限"解释）。</summary>
    public RetrievalConfidence? ConfidenceFloor { get; }

    /// <summary>
    /// 是否至少约束了一个面。§8.9 的"过滤性空 ≠ 真空"就是按这一条裁决：
    /// 有约束 + 0 命中 ⇒ <see cref="RetrievalEmptyReason.FilteredOut"/>。
    /// </summary>
    public bool HasAnyConstraint { get; }

    /// <summary>
    /// 生效的面，按**固定的优选顺序**返回（匹配域 → 符号种类 → 命中层 → 关系 → 置信度 → 扩展名 → 目录）。
    /// 顺序成文的原因：放宽建议要挑"最可能吃掉真命中"的面，而不是罗列。
    /// </summary>
    public IReadOnlyList<RetrievalFacet> ConstrainedFacets()
    {
        var facets = new List<RetrievalFacet>(7);
        if (MatchTargets is not null)
            facets.Add(RetrievalFacet.MatchTarget);

        if (_symbolKinds.Length > 0)
            facets.Add(RetrievalFacet.SymbolKind);

        if (_hitKinds.Length > 0)
            facets.Add(RetrievalFacet.HitKind);

        if (_relationKinds.Length > 0)
            facets.Add(RetrievalFacet.RelationKind);

        if (ConfidenceFloor is not null)
            facets.Add(RetrievalFacet.ConfidenceFloor);

        if (_fileExtensions.Length > 0)
            facets.Add(RetrievalFacet.FileExtension);

        if (DirectoryPath is not null)
            facets.Add(RetrievalFacet.Directory);

        return facets;
    }

    /// <summary>
    /// 该命中是否通过过滤器。<b>所有面 AND 组合</b>（ADR-089 §2.3 "必须 AND 组合、可独立开关"）。
    /// <para>纯判定，无 IO、无排序 —— 不是检索实现，只是把"面的语义"钉成可测的东西。</para>
    /// </summary>
    public bool Matches(RetrievalHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);

        if (_fileExtensions.Length > 0)
        {
            var extension = Path.GetExtension(hit.Evidence.NormalizedFilePath);
            if (!_fileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        if (!MatchesDirectory(hit.Evidence.NormalizedFilePath))
            return false;

        if (MatchTargets is { } targets && (hit.MatchTargets & targets) == RetrievalMatchTarget.None)
            return false;

        if (_symbolKinds.Length > 0 && Array.IndexOf(_symbolKinds, hit.SymbolKind) < 0)
            return false;

        if (_hitKinds.Length > 0 && Array.IndexOf(_hitKinds, hit.HitKind) < 0)
            return false;

        if (_relationKinds.Length > 0 && Array.IndexOf(_relationKinds, hit.Relation) < 0)
            return false;

        if (ConfidenceFloor is { } floor && (int)hit.Confidence < (int)floor)
            return false;

        return true;
    }

    /// <summary>一行回显（用于"我替你加了哪些面"，ADR-089 §2.3 的显式回显要求）。</summary>
    public string Describe()
    {
        if (!HasAnyConstraint)
            return "no-filter";

        var parts = new List<string>(7);
        if (_fileExtensions.Length > 0)
            parts.Add("ext:" + string.Join(',', _fileExtensions));

        if (DirectoryPath is not null)
            parts.Add($"dir:{DirectoryPath}" + (Recurse ? "(recursive)" : "(top-level-only)"));

        if (MatchTargets is { } targets)
            parts.Add("match:" + targets);

        if (_symbolKinds.Length > 0)
            parts.Add("kind:" + string.Join(',', _symbolKinds));

        if (_hitKinds.Length > 0)
            parts.Add("hit:" + string.Join(',', _hitKinds));

        if (_relationKinds.Length > 0)
            parts.Add("rel:" + string.Join(',', _relationKinds));

        if (ConfidenceFloor is { } floor)
            parts.Add($"conf>={floor}");

        return string.Join("; ", parts);
    }

    private bool MatchesDirectory(string normalizedFilePath)
    {
        if (DirectoryPath is null)
            return true;

        if (Recurse)
        {
            return string.Equals(normalizedFilePath, DirectoryPath, StringComparison.OrdinalIgnoreCase)
                || normalizedFilePath.StartsWith(DirectoryPath + "/", StringComparison.OrdinalIgnoreCase);
        }

        var lastSeparator = normalizedFilePath.LastIndexOf('/');
        return lastSeparator > 0
            && string.Equals(normalizedFilePath[..lastSeparator], DirectoryPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] NormalizeExtensions(IEnumerable<string>? extensions)
    {
        if (extensions is null)
            return [];

        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in extensions)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new ArgumentException("文件扩展名不得为空。", nameof(extensions));

            var extension = raw.Trim();
            if (!extension.StartsWith('.'))
                extension = "." + extension;

            // 小写化：路径已归一化为小写（SymbolIdentity.NormalizePath），扩展名面也必须同口径，
            // 否则回显与去重会因大小写产生两套值。
            set.Add(extension.ToLowerInvariant());
        }

        return [.. set];
    }

    private static string NormalizeDirectory(string directoryPath)
    {
        var builder = new System.Text.StringBuilder(directoryPath.Length);
        var previousWasSeparator = false;
        foreach (var ch in directoryPath.Trim())
        {
            var c = ch == '\\' ? '/' : ch;
            if (c == '/')
            {
                if (previousWasSeparator)
                    continue;

                previousWasSeparator = true;
            }
            else
            {
                previousWasSeparator = false;
            }

            builder.Append(c);
        }

        var normalized = builder.ToString().TrimEnd('/');
        return normalized.Length == 0
            ? throw new ArgumentException("目录面不得为空白或仅分隔符。", nameof(directoryPath))
            : normalized;
    }

    private static T[] DistinctSorted<T>(IEnumerable<T>? values)
        where T : struct, Enum
    {
        if (values is null)
            return [];

        return [.. new SortedSet<T>(values)];
    }
}
