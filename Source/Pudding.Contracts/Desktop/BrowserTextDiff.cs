namespace Pudding.Contracts.Desktop;

/// <summary>
/// 读取内容的**行级差异**（设计方案 §145：「diff 是只读观察优化，绑定 baseline 和同一文档」）。
///
/// 算法与承诺（刻意保守）：
/// ① 先削掉**公共前缀**与**公共后缀**，只对中间那段做差异 —— 于是"追加/删除/替换一块"都能给出
///    **精确的**新增行与删除行，成本是 O(n)，且**永远不会发明没出现过的行**；
/// ② 不做最小编辑脚本（LCS/Myers）：中间段的交错改动会被归成一个"替换块"。
///    宁可少说，也不编一个看似精巧但可能错位的脚本；
/// ③ 差异超过预算时如实标注 <see cref="Truncated"/>，**不静默截断**（调用方据此决定要不要整份重读）。
/// </summary>
public sealed record BrowserTextDiff
{
    public const int DefaultMaxLines = 200;

    private BrowserTextDiff(
        IReadOnlyList<string> added,
        IReadOnlyList<string> removed,
        bool truncated)
    {
        Added = added;
        Removed = removed;
        Truncated = truncated;
    }

    /// <summary>新增（或被替换后的新）行，按原顺序。</summary>
    public IReadOnlyList<string> Added { get; }

    /// <summary>删除（或被替换掉的旧）行，按原顺序。</summary>
    public IReadOnlyList<string> Removed { get; }

    /// <summary>是否因预算只给出了部分差异（<see cref="Added"/>/<see cref="Removed"/> 是前 N 行）。</summary>
    public bool Truncated { get; }

    /// <summary>是否完全没有变化。</summary>
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;

    public int AddedCount { get; private init; }

    public int RemovedCount { get; private init; }

    /// <summary>
    /// 由**已经算好的两侧**构造（用于把另一层算出的差异投影成本形状，例如桥接宿主算完后搬运）。
    /// 计数默认为列表长度；调用方给出真实总数时可覆盖。
    /// </summary>
    public static BrowserTextDiff FromParts(
        IReadOnlyList<string> added,
        IReadOnlyList<string> removed,
        int? addedCount = null,
        int? removedCount = null,
        bool truncated = false) => new(added, removed, truncated)
        {
            AddedCount = addedCount ?? added.Count,
            RemovedCount = removedCount ?? removed.Count,
        };

    /// <summary>
    /// 计算 <paramref name="baseline"/> → <paramref name="current"/> 的差异。
    /// 两侧都必须已经被规范化成"行数组"（空文本 ⇒ 0 行，而不是 1 个空串）。
    /// </summary>
    public static BrowserTextDiff Compute(
        IReadOnlyList<string> baseline,
        IReadOnlyList<string> current,
        int maxLines = DefaultMaxLines)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);

        var prefix = 0;
        while (prefix < baseline.Count && prefix < current.Count
               && string.Equals(baseline[prefix], current[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < baseline.Count - prefix && suffix < current.Count - prefix
               && string.Equals(
                   baseline[baseline.Count - 1 - suffix],
                   current[current.Count - 1 - suffix],
                   StringComparison.Ordinal))
        {
            suffix++;
        }

        var removedCount = baseline.Count - prefix - suffix;
        var addedCount = current.Count - prefix - suffix;

        var removed = Take(baseline, prefix, removedCount, maxLines);
        var added = Take(current, prefix, addedCount, maxLines);
        var truncated = removedCount > removed.Count || addedCount > added.Count;

        return new BrowserTextDiff(added, removed, truncated)
        {
            AddedCount = addedCount,
            RemovedCount = removedCount,
        };
    }

    /// <summary>按行切分（空文本 ⇒ 0 行；<c>\r\n</c>/<c>\r</c> 统一成 <c>\n</c>）。</summary>
    public static IReadOnlyList<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static IReadOnlyList<string> Take(
        IReadOnlyList<string> lines, int start, int count, int maxLines)
    {
        if (count <= 0)
        {
            return [];
        }

        var take = Math.Min(count, maxLines);
        var result = new string[take];
        for (var index = 0; index < take; index++)
        {
            result[index] = lines[start + index];
        }

        return result;
    }
}
