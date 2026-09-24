namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>预算口径：先触顶的是哪一个（ADR-089 §2.2：条数 + 字节/token 双预算，token 优先）。</summary>
public enum RetrievalBudgetKind
{
    /// <summary>未触顶。</summary>
    None = 0,

    /// <summary>条数先触顶。</summary>
    ItemCount = 1,

    /// <summary>字节先触顶。</summary>
    Bytes = 2,

    /// <summary>token 先触顶（优先口径）。</summary>
    Tokens = 3,
}

/// <summary>
/// 落盘策略（ADR-089 §2.2 / §8.7）：落盘位置**必须**在已被 <c>.gitignore</c> 覆盖的目录内，
/// 否则污染仓库（<c>.pudding/</c> 下 20,932 个文件就是前车之鉴）。
/// <para>本类把该硬约束做成**可裁决的函数**，从而"随机往仓库里写结果文件"构造期即被拒。</para>
/// </summary>
public static class RetrievalSpillPolicy
{
    /// <summary>约定的落盘目录名（仓库内，已被 gitignore 覆盖）。</summary>
    public const string DefaultSpillDirectory = ".pudding";

    /// <summary>其它被 gitignore 覆盖的目录段名（项目约定：临时件写 <c>temp/</c>）。</summary>
    private static readonly string[] AdditionalIgnoredSegments = ["temp", ".tmp", ".pudding"];

    /// <summary>落盘路径是否在允许范围内（<c>.pudding</c> / <c>temp</c> / <c>.tmp</c> 段，或系统临时目录内）。</summary>
    public static bool IsAllowedSpillPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = SymbolIdentity.NormalizePath(path);
        var temporaryRoot = SymbolIdentity.NormalizePath(Path.GetTempPath());
        if (temporaryRoot.Length > 0 && normalized.StartsWith(temporaryRoot, StringComparison.Ordinal))
            return true;

        foreach (var segment in normalized.Split('/'))
        {
            foreach (var allowed in AdditionalIgnoredSegments)
            {
                if (string.Equals(segment, allowed, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>允许范围的文本描述（用于错误信息与给 Agent 的指引）。</summary>
    public static string DescribeAllowedRoots() =>
        $"'{DefaultSpillDirectory}/'（或 'temp/'、'.tmp/'）段内，或系统临时目录内（必须被 .gitignore 覆盖）";
}

/// <summary>
/// 超预算落盘信息（ADR-089 §2.2 A）：<b>路径 + 已写入条数 + 预算口径</b>。
/// <para>
/// 它的存在本身就是"禁止静默截断"的实现方式：<see cref="RetrievalResult"/> 里
/// "部分返回"这件事**只能**通过本类型表达，于是"截断了但不说"在结构上不可表示。
/// </para>
/// </summary>
public sealed record RetrievalOverflow
{
    private RetrievalOverflow(string path, int spilledCount, RetrievalBudgetKind triggeredBy)
    {
        Path = path;
        SpilledCount = spilledCount;
        TriggeredBy = triggeredBy;
    }

    /// <summary>完整结果的落盘路径（必须在 gitignore 覆盖目录内）。</summary>
    public string Path { get; }

    /// <summary>已写入落盘文件的条数。</summary>
    public int SpilledCount { get; }

    /// <summary>先触顶的预算口径。</summary>
    public RetrievalBudgetKind TriggeredBy { get; }

    /// <summary>构造落盘信息；路径不在允许范围内即拒绝。</summary>
    public static RetrievalOverflow Create(string path, int spilledCount, RetrievalBudgetKind triggeredBy)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("落盘信息必须给出文件路径。", nameof(path));

        if (!RetrievalSpillPolicy.IsAllowedSpillPath(path))
            throw new ArgumentException(
                $"落盘路径必须位于 {RetrievalSpillPolicy.DescribeAllowedRoots()}；收到 '{path}'（会污染仓库）。",
                nameof(path));

        if (spilledCount < 0)
            throw new ArgumentOutOfRangeException(nameof(spilledCount), spilledCount, "落盘条数不得为负。");

        if (triggeredBy == RetrievalBudgetKind.None)
            throw new ArgumentException("落盘了就必须说明是哪一个预算先触顶。", nameof(triggeredBy));

        return new RetrievalOverflow(path.Trim(), spilledCount, triggeredBy);
    }
}
