namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 分布摘要（ADR-089 §2.2）：按 <see cref="RetrievalHitKind"/> / <c>CodeSymbolKind</c> / 语言 / 顶层目录计数。
/// <para>
/// 用户第六轮裁定原文："如果返回的大量的命中的 …… 结合经验来说，如果返回的命中超级多，
/// 那么也代表这次查询质量不高，可以考虑通过组合查询的方式或者查询目录限制等过滤掉无效的查询。"
/// ⇒ 分布摘要就是让 <b>Agent 和自己</b>看到"这堆命中长什么样"的最小代价反馈；
/// 没有它，"只给 20 条"等于让 Agent 再发一次查询去探索（§2.2 ⛔禁止静默截断）。
/// </para>
/// <para>
/// 所有字典都是 <c>SortedDictionary</c>（Ordinal 比较器）⇒ 枚举顺序确定，
/// 同一批命中任何两次统计都逐位相同。
/// </para>
/// <para>
/// <b>注意</b>：本类型的取值相等不比较字典内容（集合字段按引用比较）—— 断言请逐项进行。
/// </para>
/// </summary>
public sealed record RetrievalDistribution
{
    private RetrievalDistribution(
        SortedDictionary<RetrievalHitKind, int> byHitKind,
        SortedDictionary<CodeSymbolKind, int> bySymbolKind,
        SortedDictionary<string, int> byLanguage,
        SortedDictionary<string, int> byTopLevelDirectory,
        int totalCount)
    {
        ByHitKind = byHitKind;
        BySymbolKind = bySymbolKind;
        ByLanguage = byLanguage;
        ByTopLevelDirectory = byTopLevelDirectory;
        TotalCount = totalCount;
    }

    /// <summary>空分布（0 命中）。</summary>
    public static RetrievalDistribution Empty { get; } = new(
        new SortedDictionary<RetrievalHitKind, int>(),
        new SortedDictionary<CodeSymbolKind, int>(),
        new SortedDictionary<string, int>(StringComparer.Ordinal),
        new SortedDictionary<string, int>(StringComparer.Ordinal),
        0);

    /// <summary>命中层计数。</summary>
    public IReadOnlyDictionary<RetrievalHitKind, int> ByHitKind { get; }

    /// <summary>符号种类计数。</summary>
    public IReadOnlyDictionary<CodeSymbolKind, int> BySymbolKind { get; }

    /// <summary>语言计数（由证据路径推导）。</summary>
    public IReadOnlyDictionary<string, int> ByLanguage { get; }

    /// <summary>顶层目录计数。</summary>
    public IReadOnlyDictionary<string, int> ByTopLevelDirectory { get; }

    /// <summary>参与统计的命中条数。</summary>
    public int TotalCount { get; }

    /// <summary>是否没有任何计数。</summary>
    public bool IsEmpty => TotalCount == 0;

    /// <summary>按命中统计出分布（确定性）。</summary>
    public static RetrievalDistribution FromHits(IEnumerable<RetrievalHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var byHitKind = new SortedDictionary<RetrievalHitKind, int>();
        var bySymbolKind = new SortedDictionary<CodeSymbolKind, int>();
        var byLanguage = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byDirectory = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var total = 0;

        foreach (var hit in hits)
        {
            Increment(byHitKind, hit.HitKind);
            Increment(bySymbolKind, hit.SymbolKind);
            Increment(byLanguage, hit.Language);
            Increment(byDirectory, RetrievalPathFacts.TopLevelDirectoryOf(hit.Evidence.NormalizedFilePath));
            total++;
        }

        return total == 0
            ? Empty
            : new RetrievalDistribution(byHitKind, bySymbolKind, byLanguage, byDirectory, total);
    }

    /// <summary>一行摘要（给 Agent 看的最小反馈，也是"过载即信号"的论据）。</summary>
    public string Describe()
    {
        if (IsEmpty)
            return "distribution: empty";

        return string.Join(
            "; ",
            $"total={TotalCount}",
            "hitKind=" + Format(ByHitKind),
            "symbolKind=" + Format(BySymbolKind),
            "language=" + Format(ByLanguage),
            "dir=" + Format(ByTopLevelDirectory));
    }

    private static string Format<TKey>(IReadOnlyDictionary<TKey, int> counts)
        where TKey : notnull =>
        counts.Count == 0
            ? "none"
            : string.Join(',', counts.Select(pair => $"{pair.Key}={pair.Value}"));

    private static void Increment<TKey>(SortedDictionary<TKey, int> counts, TKey key)
        where TKey : notnull =>
        counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
}
