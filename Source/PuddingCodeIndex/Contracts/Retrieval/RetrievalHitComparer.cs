namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 显式**全序**比较器（ADR-089 §2.1 第 5 条 + §2.2）：同一 <c>query + scope + 索引版本</c>
/// 必须给出同一结果与同一顺序，否则 Agent 会因"上次没找到、这次找到了"而反复重试。
/// <para>
/// 键链（依次比较，全部成文）：<br/>
/// ① <see cref="RetrievalHit.EffectiveRank"/> 降序（分数 × 多 scope 佐证权重）<br/>
/// ② 层序升序（层序表由 <see cref="RetrievalIntentPolicy.LayerOrder"/> 给出；显式意图用自己的表）<br/>
/// ③ 文件路径（OrdinalIgnoreCase 升序）④ 行号升序 ⑤ 符号名（OrdinalIgnoreCase 升序）<br/>
/// ⑥ <see cref="SymbolIdentity.Key"/>（Ordinal 升序）—— <b>末级 tie-break</b>
/// </para>
/// <para>
/// 因为去重后同一份结果里身份唯一，"键链走到 ⑥"即为真全序 —— 这就是分页游标的前提。
/// </para>
/// </summary>
public sealed class RetrievalHitComparer : IComparer<RetrievalHit>
{
    private readonly RetrievalHitKind[] _layers;

    private RetrievalHitComparer(RetrievalIntent intent)
    {
        Intent = intent;
        _layers = [.. RetrievalIntentPolicy.LayerOrder(intent)];
    }

    /// <summary>该比较器所依据的意图（决定层序表）。</summary>
    public RetrievalIntent Intent { get; }

    /// <summary>缺省（Auto）全序。</summary>
    public static RetrievalHitComparer Auto { get; } = new(RetrievalIntent.Auto);

    /// <summary>按指定意图取全序比较器（显式意图优先，见 <see cref="RetrievalIntentPolicy"/>）。</summary>
    public static RetrievalHitComparer ForIntent(RetrievalIntent intent) => new(intent);

    /// <inheritdoc />
    public int Compare(RetrievalHit? x, RetrievalHit? y)
    {
        if (ReferenceEquals(x, y))
            return 0;

        if (x is null)
            return 1;

        if (y is null)
            return -1;

        var byRank = y.EffectiveRank.CompareTo(x.EffectiveRank);
        if (byRank != 0)
            return byRank;

        var byLayer = LayerRank(x.HitKind).CompareTo(LayerRank(y.HitKind));
        if (byLayer != 0)
            return byLayer;

        var byPath = string.Compare(
            x.Evidence.NormalizedFilePath,
            y.Evidence.NormalizedFilePath,
            StringComparison.OrdinalIgnoreCase);
        if (byPath != 0)
            return byPath;

        var byLine = x.Evidence.Line.CompareTo(y.Evidence.Line);
        if (byLine != 0)
            return byLine;

        var byName = string.Compare(x.SymbolName, y.SymbolName, StringComparison.OrdinalIgnoreCase);
        if (byName != 0)
            return byName;

        return string.CompareOrdinal(x.Identity.Key, y.Identity.Key);
    }

    /// <summary>按本全序排序（结果与输入顺序无关）。</summary>
    public IReadOnlyList<RetrievalHit> Sort(IEnumerable<RetrievalHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var list = hits.ToList();
        list.Sort(this);
        return list;
    }

    private int LayerRank(RetrievalHitKind kind)
    {
        var index = Array.IndexOf(_layers, kind);
        return index < 0 ? _layers.Length : index;
    }
}

/// <summary>
/// 构件层的顺序/身份断言（"先裁决再构造"，避免把不确定的序列交给分页游标）。
/// </summary>
public static class RetrievalHitOrdering
{
    /// <summary>序列是否按给定意图的全序非递减。</summary>
    public static void EnsureOrdered(IReadOnlyList<RetrievalHit> hits, RetrievalIntent intent)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var comparer = RetrievalHitComparer.ForIntent(intent);
        for (var i = 1; i < hits.Count; i++)
        {
            if (comparer.Compare(hits[i - 1], hits[i]) > 0)
                throw new InvalidOperationException(
                    $"命中序列未按 intent={intent} 的显式全序排列（第 {i} 条越序）—— "
                    + "分页游标要求确定性顺序（ADR-089 §2.1 第 5 条）。");
        }
    }

    /// <summary>身份是否唯一（跨 scope 去重必须先于构造，见 §2.4）。</summary>
    public static void EnsureUniqueIdentities(IReadOnlyList<RetrievalHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            if (!seen.Add(hit.Identity.Key))
                throw new InvalidOperationException(
                    $"命中序列存在重复符号身份 '{hit.Identity.Key}' —— 跨 scope 去重必须先于结果构造（ADR-089 §2.4 / §8.10），"
                    + "否则结构性膨胀会被误判成查询过泛。");
        }
    }
}
