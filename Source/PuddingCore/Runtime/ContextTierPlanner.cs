namespace PuddingCode.Runtime;

/// <summary>
/// 纯函数式 T0–T4 分级规划器（设计方案 §8.1 分级策略、§9 去重规则）。
/// 三遍式：基础分级（轮次距离）→ 原子组校正（不可拆分）→ 有界晋升（query 命中）。
/// 不依赖存储、不接线任何压缩流程，输入输出均为纯内存契约。
/// </summary>
public sealed class ContextTierPlanner : IContextTierPlanner
{
    /// <summary>query 命中的晋升原因标记。</summary>
    private const string QueryHitPromotionReason = "query-hit";

    /// <inheritdoc />
    public ContextTierPlan Plan(
        IReadOnlyList<TierPlannerSegmentInput> segments,
        ContextTierPlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var opts = options ?? new ContextTierPlanOptions();
        ValidateOptions(opts);

        // 1. 空输入 → 空 Assignments。
        if (segments.Count == 0)
        {
            return new ContextTierPlan(Array.Empty<TierAssignment>());
        }

        // 2. 当前轮 = 最大轮次序号。
        int currentTurn = segments.Max(s => s.TurnOrdinal);

        var baseTiers = new ContextSegmentTier[segments.Count];
        var assignments = new TierAssignment[segments.Count];

        // 3. 第一遍：基础分级（轮次距离）。
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            baseTiers[i] = ClassifyByDistance(segment, currentTurn, opts);
            assignments[i] = new TierAssignment(segment.SegmentId, baseTiers[i], false, null);
        }

        // 4. 第二遍：原子组校正（同组同 tier，取组内最小/最保真 tier，不可拆分）。
        var groupIndexes = GroupIndexes(segments);
        foreach (var indexes in groupIndexes.Values)
        {
            var groupMinTier = (ContextSegmentTier)indexes.Min(i => (int)baseTiers[i]);
            foreach (int i in indexes)
            {
                baseTiers[i] = groupMinTier;
                assignments[i] = assignments[i] with { Tier = groupMinTier };
            }
        }

        // 5. 第三遍：有界晋升（query 命中 → 晋升到 PromotionTarget）。
        //    原子组内任一成员命中 → 整组视为命中（原子性优先于有界）。
        var hitFlags = ComputeHitFlags(segments, groupIndexes);
        for (int i = 0; i < segments.Count; i++)
        {
            if (!hitFlags[i])
            {
                continue;
            }

            if ((int)baseTiers[i] > (int)opts.PromotionTarget)
            {
                assignments[i] = assignments[i] with
                {
                    Tier = opts.PromotionTarget,
                    IsPromoted = true,
                    PromotionReason = QueryHitPromotionReason,
                };
            }
        }

        // 6. 输出与输入同序。
        return new ContextTierPlan(assignments);
    }

    /// <summary>
    /// 按轮次距离做基础分级：当前轮强制 T0；否则按
    /// T1 ≤ RecentTurnCount、T2 ≤ WarmThroughDistance、T3 ≤ ColdThroughDistance、其余 T4。
    /// </summary>
    private static ContextSegmentTier ClassifyByDistance(
        TierPlannerSegmentInput segment,
        int currentTurn,
        ContextTierPlanOptions options)
    {
        if (segment.IsCurrentTurn)
        {
            return ContextSegmentTier.T0;
        }

        int distance = currentTurn - segment.TurnOrdinal;
        if (distance <= options.RecentTurnCount)
        {
            return ContextSegmentTier.T1;
        }

        if (distance <= options.WarmThroughDistance)
        {
            return ContextSegmentTier.T2;
        }

        if (distance <= options.ColdThroughDistance)
        {
            return ContextSegmentTier.T3;
        }

        return ContextSegmentTier.T4;
    }

    /// <summary>按 AtomicGroupId 收集段索引；忽略 null / 空字符串组 ID。</summary>
    private static Dictionary<string, List<int>> GroupIndexes(
        IReadOnlyList<TierPlannerSegmentInput> segments)
    {
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < segments.Count; i++)
        {
            string? groupId = segments[i].AtomicGroupId;
            if (string.IsNullOrEmpty(groupId))
            {
                continue;
            }

            if (!groups.TryGetValue(groupId, out var indexes))
            {
                indexes = new List<int>(2);
                groups[groupId] = indexes;
            }

            indexes.Add(i);
        }

        return groups;
    }

    /// <summary>
    /// 计算每个段的命中标记：自身 IsQueryHit，或所属原子组内任一成员命中（整组视为命中）。
    /// </summary>
    private static bool[] ComputeHitFlags(
        IReadOnlyList<TierPlannerSegmentInput> segments,
        Dictionary<string, List<int>> groupIndexes)
    {
        var flags = new bool[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            flags[i] = segments[i].IsQueryHit;
        }

        foreach (var indexes in groupIndexes.Values)
        {
            if (indexes.Any(i => flags[i]))
            {
                foreach (int i in indexes)
                {
                    flags[i] = true;
                }
            }
        }

        return flags;
    }

    /// <summary>校验分级阈值单调性：0 ≤ Recent ≤ Warm ≤ Cold。</summary>
    private static void ValidateOptions(ContextTierPlanOptions options)
    {
        if (options.RecentTurnCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RecentTurnCount 不能为负。");
        }

        if (options.WarmThroughDistance < options.RecentTurnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "WarmThroughDistance 不能小于 RecentTurnCount。");
        }

        if (options.ColdThroughDistance < options.WarmThroughDistance)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ColdThroughDistance 不能小于 WarmThroughDistance。");
        }
    }
}
