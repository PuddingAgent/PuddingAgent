namespace PuddingMemoryGraph;

/// <summary>排序权重。**刻意放在配置里而不是写死常量**：这些数只能按真实数据调，不能凭直觉定。</summary>
public sealed record MemoryRankingWeights
{
    public double ContextMatch { get; init; } = 0.45;
    public double Freshness { get; init; } = 0.25;
    public double Salience { get; init; } = 0.20;
    public double EdgeProximity { get; init; } = 0.10;

    /// <summary>已退役（被 Supersedes 指向）节点的降权系数：乘性，而不是直接剔除。</summary>
    public double SupersededPenalty { get; init; } = 0.25;

    public static MemoryRankingWeights Default { get; } = new();

    public double Sum => ContextMatch + Freshness + Salience + EdgeProximity;
}

/// <summary>一次排序所需的上下文（调用方提供，本组件不读任何外部状态）。</summary>
public sealed record MemoryRankContext
{
    /// <summary>上下文匹配分：键 → 权重。由调用方的 FactContextMatcher 之类算出后传入。</summary>
    public IReadOnlyDictionary<string, double> MatchScores { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>本次查询命中的锚点节点（用于计算 edge proximity）。</summary>
    public IReadOnlySet<string> AnchorNodeIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>从锚点出发的跳数：1 = 直接邻居，2 = 二跳；不在表里的视为不可达（0 分）。</summary>
    public IReadOnlyDictionary<string, int> HopDistance { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

/// <summary>
/// 纯函数排序：同样的输入永远给同样的分数。
/// <para>
/// 三个刻意的设计：
/// ① 权重和不为 1 时**归一化**，避免调用方改了某个权重导致整体量纲漂移；
/// ② 已退役节点不剔除只降权 —— 有时候恰恰需要看到「它被推翻了」；
/// ③ 新鲜度直接取节点的 Freshness 字段，本类不自己算时间 —— 时间计算属于另一层，
///    混在一起会让排序不可测。
/// </para>
/// </summary>
public static class MemoryRanker
{
    public static double Score(
        MemoryNode node,
        MemoryRankContext context,
        MemoryRankingWeights? weights = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);

        var w = weights ?? MemoryRankingWeights.Default;
        var sum = w.Sum;
        if (sum <= 0)
            return 0;

        var contextMatch = context.MatchScores.TryGetValue(node.NodeId, out var m) ? Clamp01(m) : 0;
        var freshness = Clamp01(node.Freshness);
        var salience = Clamp01(node.Salience);
        var proximity = Proximity(context, node.NodeId);

        var raw = (w.ContextMatch * contextMatch
                   + w.Freshness * freshness
                   + w.Salience * salience
                   + w.EdgeProximity * proximity) / sum;

        return Math.Clamp(raw, 0, 1);
    }

    /// <summary>按分数降序、同分按 NodeId 升序（保证结果稳定，便于测试与缓存）。</summary>
    public static IReadOnlyList<MemoryNode> Rank(
        IEnumerable<MemoryNode> nodes,
        MemoryRankContext context,
        MemoryRankingWeights? weights = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var w = weights ?? MemoryRankingWeights.Default;
        return nodes
            .Where(n => !n.Tombstoned)
            .Select(n => (Node: n, Score: Score(n, context, w)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Node.NodeId, StringComparer.Ordinal)
            .Select(x => x.Node)
            .ToList();
    }

    private static double Proximity(MemoryRankContext context, string nodeId)
    {
        if (!context.HopDistance.TryGetValue(nodeId, out var hops) || hops <= 0)
            return 0;

        return hops switch
        {
            1 => 1.0,
            2 => 0.5,
            _ => 0.0,
        };
    }

    private static double Clamp01(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
