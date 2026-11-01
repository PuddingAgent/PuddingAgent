namespace PuddingMemoryGraph;

/// <summary>一跳展开的结果：可达节点 + 它们到锚点的最小跳数。</summary>
public sealed record MemoryTraversalResult
{
    public required IReadOnlyDictionary<string, int> HopDistance { get; init; }

    /// <summary>被丢弃的边（权重低于阈值 / 超过节点上限），用于诊断。</summary>
    public required int SkippedEdges { get; init; }

    public required bool HitNodeLimit { get; init; }
}

/// <summary>
/// 图遍历（纯函数，不碰存储）。
/// <para>
/// 三条刻意的约束：
/// ① **只走白名单关系** —— `Contradicts` 默认不展开，否则一条矛盾边会把整片旧记忆拉回来；
/// ② **最小跳数优先**（BFS），保证 <see cref="MemoryTraversalResult.HopDistance"/> 可解释；
/// ③ **有硬上限**（节点数 + 跳数 + 权重下限），因为记忆图的出度不可控，
///    不设上限的展开会让一次召回变成全图扫描。
/// </para>
/// </summary>
public static class MemoryGraphTraversal
{
    /// <summary>默认展开的关系：只走「同类/细化/来源」方向，不走「矛盾/取代」。</summary>
    public static IReadOnlySet<MemoryRelation> DefaultExpandable { get; } = new HashSet<MemoryRelation>
    {
        MemoryRelation.RelatedTo,
        MemoryRelation.About,
        MemoryRelation.Refines,
        MemoryRelation.DerivedFrom,
    };

    public static MemoryTraversalResult Expand(
        IReadOnlyCollection<string> anchorNodeIds,
        IReadOnlyList<MemoryEdge> edges,
        int maxHops = 1,
        int maxNodes = 32,
        double minWeight = 0.2,
        IReadOnlySet<MemoryRelation>? expandable = null)
    {
        ArgumentNullException.ThrowIfNull(anchorNodeIds);
        ArgumentNullException.ThrowIfNull(edges);

        var allowed = expandable ?? DefaultExpandable;
        var hops = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var anchor in anchorNodeIds)
        {
            if (!string.IsNullOrWhiteSpace(anchor))
                hops[anchor] = 0;
        }

        var skipped = 0;
        var hitLimit = false;
        var effectiveHops = Math.Clamp(maxHops, 0, 2);

        for (var hop = 0; hop < effectiveHops; hop++)
        {
            var frontier = hops.Where(kv => kv.Value == hop).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
            if (frontier.Count == 0)
                break;

            foreach (var edge in edges)
            {
                if (!allowed.Contains(edge.Relation) || edge.Weight < minWeight)
                {
                    skipped++;
                    continue;
                }

                // 双向展开：记忆图的边是"关联"，方向只表达来源语义，不限制可达性。
                string? from = null;
                string? to = null;
                if (frontier.Contains(edge.FromNodeId))
                {
                    from = edge.FromNodeId;
                    to = edge.ToNodeId;
                }
                else if (frontier.Contains(edge.ToNodeId))
                {
                    from = edge.ToNodeId;
                    to = edge.FromNodeId;
                }

                if (from is null || to is null || hops.ContainsKey(to))
                    continue;

                if (hops.Count >= maxNodes)
                {
                    hitLimit = true;
                    break;
                }

                hops[to] = hop + 1;
            }

            if (hitLimit)
                break;
        }

        return new MemoryTraversalResult
        {
            HopDistance = hops,
            SkippedEdges = skipped,
            HitNodeLimit = hitLimit,
        };
    }
}
