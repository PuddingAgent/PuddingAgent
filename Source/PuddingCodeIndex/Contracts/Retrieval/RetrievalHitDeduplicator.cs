namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 跨 scope 去重的产出（ADR-089 §2.4 / §8.10）：<b>去重必须先于过载判定</b>。
/// <para>
/// <b>注意</b>：本类型的取值相等不比较 <see cref="Hits"/> 的内容（集合字段按引用比较）——
/// 断言请逐项进行（<c>Hits.Count</c> / 元素字段）。
/// </para>
/// </summary>
public sealed record RetrievalDeduplicationResult
{
    internal RetrievalDeduplicationResult(
        IReadOnlyList<RetrievalHit> hits,
        int inputCount,
        int distinctSymbolCount,
        int removedDuplicateCount)
    {
        Hits = hits;
        InputCount = inputCount;
        DistinctSymbolCount = distinctSymbolCount;
        RemovedDuplicateCount = removedDuplicateCount;
    }

    /// <summary>去重后的命中（已按给定意图的全序排列）。</summary>
    public IReadOnlyList<RetrievalHit> Hits { get; }

    /// <summary>去重前的命中条数。</summary>
    public int InputCount { get; }

    /// <summary>去重后的不同符号身份数（= <see cref="Hits"/> 的长度）。</summary>
    public int DistinctSymbolCount { get; }

    /// <summary>被折叠掉的重复项条数（多 scope 副本）。</summary>
    public int RemovedDuplicateCount { get; }
}

/// <summary>
/// 符号身份去重（ADR-089 §2.4）：同一 <see cref="SymbolIdentity"/> 的多个 scope 副本
/// <b>只对外出现一次</b>，多 scope 体现为 <see cref="RetrievalHit.Scopes"/> 与
/// <see cref="RetrievalHit.ScopeCorroborationWeight"/>（佐证加权），而不是重复项。
/// <para>
/// 为什么必须存在：实测（§2.4）仓库根与三个子目录被各自注册为项目，同一符号在
/// 3 个 project_id 下各出现一次；这类噪音**加任何过滤条件都消不掉**，
/// 且会让"命中很多"看起来像"查询过泛"。
/// </para>
/// </summary>
public static class RetrievalHitDeduplicator
{
    /// <summary>按符号身份去重；每组保留全序最靠前的一条，并并入全部 scope 盖章。</summary>
    public static RetrievalDeduplicationResult Deduplicate(
        IEnumerable<RetrievalHit> hits,
        RetrievalIntent intent = RetrievalIntent.Auto)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var input = hits.ToList();
        var comparer = RetrievalHitComparer.ForIntent(intent);

        var groups = new Dictionary<SymbolIdentity, List<RetrievalHit>>();
        foreach (var hit in input)
        {
            if (!groups.TryGetValue(hit.Identity, out var group))
                groups[hit.Identity] = group = [];

            group.Add(hit);
        }

        var merged = new List<RetrievalHit>(groups.Count);
        merged.AddRange(groups.Values.Select(group => Merge(group, comparer)));
        merged.Sort(comparer);

        return new RetrievalDeduplicationResult(
            merged,
            input.Count,
            merged.Count,
            input.Count - merged.Count);
    }

    private static RetrievalHit Merge(List<RetrievalHit> group, RetrievalHitComparer comparer)
    {
        var best = group[0];
        if (group.Count == 1)
            return best;

        foreach (var candidate in group)
        {
            if (comparer.Compare(candidate, best) < 0)
                best = candidate;
        }

        var scopes = ScopeIdSet.Empty;
        foreach (var hit in group)
            scopes = scopes.Union(hit.Scopes);

        return best.WithScopes(scopes.Ids);
    }
}
