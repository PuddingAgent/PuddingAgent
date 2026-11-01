namespace PuddingMemoryGraph;

/// <summary>渐进加载的三档。档位越高，单条占用越大、能容纳的条数越少。</summary>
public enum MemoryTier
{
    /// <summary>L0 索引：永远给，只要 Title。</summary>
    Index = 0,
    /// <summary>L1 摘要：只对被点名的节点展开。</summary>
    Summary = 1,
    /// <summary>L2 正文：必须调用方显式请求。</summary>
    Body = 2,
}

/// <summary>一条被选中的记忆切片。带 NodeId 与新鲜度是硬要求：没有它，模型无法判断该不该信。</summary>
public sealed record MemorySlice
{
    public required string NodeId { get; init; }
    public required MemoryNodeKind Kind { get; init; }
    public required MemoryTier Tier { get; init; }
    public required string Text { get; init; }
    public required double Freshness { get; init; }
    public required double Score { get; init; }

    /// <summary>估算 token（按 4 字符 ≈ 1 token 的保守折算，仅用于预算裁剪）。</summary>
    public int EstimatedTokens => (Text.Length + 3) / 4;
}

/// <summary>切片结果：选中的切片 + 因预算被丢掉的数量（用于诊断，不静默）。</summary>
public sealed record MemorySliceResult
{
    public required IReadOnlyList<MemorySlice> Slices { get; init; }
    public required int DroppedByBudget { get; init; }
    public required int UsedTokens { get; init; }

    public bool Truncated => DroppedByBudget > 0;
}

/// <summary>
/// 渐进加载器：把「排好序的节点」按档位与预算切成可注入的切片。
/// <para>
/// 预算语义刻意简单：**先到先得、放不下就停**（不是贪心填充）。
/// 理由是可预期——调用方看到 <see cref="MemorySliceResult.Truncated"/> 就知道被裁了，
/// 而不是去猜哪些被挑中。
/// </para>
/// </summary>
public static class MemoryProgressiveLoader
{
    /// <summary>取单条在指定档位下的文本。</summary>
    public static string TextFor(MemoryNode node, MemoryTier tier)
    {
        ArgumentNullException.ThrowIfNull(node);

        return tier switch
        {
            MemoryTier.Index => node.Title,
            MemoryTier.Summary => string.IsNullOrWhiteSpace(node.Summary) ? node.Title : node.Summary,
            _ => string.IsNullOrWhiteSpace(node.Body)
                ? (string.IsNullOrWhiteSpace(node.Summary) ? node.Title : node.Summary)
                : node.Body,
        };
    }

    /// <summary>把排好序的节点切成切片，受总预算约束。<paramref name="ranked"/> 的先后即是优先级。</summary>
    public static MemorySliceResult Slice(
        IReadOnlyList<MemoryNode> ranked,
        MemoryRankContext context,
        MemoryTier tier,
        int budgetTokens,
        MemoryRankingWeights? weights = null)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(context);

        var w = weights ?? MemoryRankingWeights.Default;
        var slices = new List<MemorySlice>();
        var used = 0;
        var dropped = 0;

        foreach (var node in ranked)
        {
            if (node.Tombstoned)
                continue;

            var slice = new MemorySlice
            {
                NodeId = node.NodeId,
                Kind = node.Kind,
                Tier = tier,
                Text = TextFor(node, tier),
                Freshness = node.Freshness,
                Score = MemoryRanker.Score(node, context, w),
            };

            if (string.IsNullOrWhiteSpace(slice.Text))
                continue;

            if (used + slice.EstimatedTokens > budgetTokens)
            {
                dropped++;
                continue;
            }

            slices.Add(slice);
            used += slice.EstimatedTokens;
        }

        return new MemorySliceResult
        {
            Slices = slices,
            DroppedByBudget = dropped,
            UsedTokens = used,
        };
    }
}
