using PuddingMemoryGraph;

namespace PuddingMemoryGraphTests;

/// <summary>
/// 排序与渐进加载的契约。
/// <para>
/// 这里刻意**不测"分数等于某个魔数"** —— 权重是可调的，锁死具体数值会让测试变成绊脚石。
/// 测的是**序关系与预算行为**：这些才是调用方真正依赖的东西。
/// </para>
/// </summary>
[TestClass]
public sealed class MemoryRankingTests
{
    private static MemoryNode Node(
        string id,
        double freshness = 1.0,
        double salience = 0.5,
        string title = "t",
        string summary = "",
        string body = "",
        bool tombstoned = false) => new()
    {
        NodeId = id,
        Kind = MemoryNodeKind.Fact,
        Title = title,
        Summary = summary,
        Body = body,
        Freshness = freshness,
        Salience = salience,
        Tombstoned = tombstoned,
    };

    private static MemoryRankContext Context(
        Dictionary<string, double>? match = null,
        Dictionary<string, int>? hops = null) => new()
    {
        MatchScores = match ?? new Dictionary<string, double>(StringComparer.Ordinal),
        HopDistance = hops ?? new Dictionary<string, int>(StringComparer.Ordinal),
    };

    [TestMethod]
    public void Higher_context_match_ranks_first()
    {
        var a = Node("a");
        var b = Node("b");

        var ranked = MemoryRanker.Rank([a, b], Context(new() { ["b"] = 1.0 }));

        Assert.AreEqual("b", ranked[0].NodeId, "上下文命中的节点应排在前");
    }

    [TestMethod]
    public void Fresher_node_wins_when_context_equal()
    {
        var stale = Node("stale", freshness: 0.2);
        var fresh = Node("fresh", freshness: 0.9);

        var ranked = MemoryRanker.Rank([stale, fresh], Context());

        Assert.AreEqual("fresh", ranked[0].NodeId);
    }

    [TestMethod]
    public void One_hop_neighbor_beats_two_hop_and_unknown()
    {
        var near = Node("near");
        var far = Node("far");
        var lonely = Node("lonely");

        var ranked = MemoryRanker.Rank(
            [lonely, far, near],
            Context(hops: new() { ["near"] = 1, ["far"] = 2 }));

        Assert.AreEqual("near", ranked[0].NodeId);
        Assert.AreEqual("far", ranked[1].NodeId);
        Assert.AreEqual("lonely", ranked[2].NodeId);
    }

    [TestMethod]
    public void Tombstoned_nodes_are_never_ranked()
    {
        var alive = Node("alive");
        var dead = Node("dead", tombstoned: true);

        var ranked = MemoryRanker.Rank([dead, alive], Context());

        CollectionAssert.AreEqual(new[] { "alive" }, ranked.Select(n => n.NodeId).ToArray(),
            "软删节点不得出现在排序结果里");
    }

    [TestMethod]
    public void Ranking_is_stable_for_equal_scores()
    {
        var ranked = MemoryRanker.Rank([Node("b"), Node("a"), Node("c")], Context());

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, ranked.Select(n => n.NodeId).ToArray(),
            "同分必须按 NodeId 稳定排序，否则缓存与测试都会抖");
    }

    [TestMethod]
    public void Score_is_normalized_between_zero_and_one()
    {
        var node = Node("x", freshness: 1.0, salience: 1.0);
        var score = MemoryRanker.Score(node, Context(new() { ["x"] = 1.0 }, new() { ["x"] = 1 }));

        Assert.IsTrue(score is >= 0 and <= 1, $"分数必须在 [0,1]，实际 {score}");
    }

    [TestMethod]
    public void Zero_weight_sum_does_not_divide_by_zero()
    {
        var weights = new MemoryRankingWeights
        {
            ContextMatch = 0, Freshness = 0, Salience = 0, EdgeProximity = 0,
        };

        Assert.AreEqual(0d, MemoryRanker.Score(Node("x"), Context(), weights));
    }
}

/// <summary>渐进加载：档位取文本 + 预算裁剪 + 不静默丢条目。</summary>
[TestClass]
public sealed class MemoryProgressiveLoaderTests
{
    private static MemoryNode Node(string id, string title, string summary, string body) => new()
    {
        NodeId = id,
        Kind = MemoryNodeKind.Note,
        Title = title,
        Summary = summary,
        Body = body,
    };

    private static MemoryRankContext Empty() => new();

    [TestMethod]
    public void Index_tier_uses_title_only()
    {
        var node = Node("n1", "标题", "摘要", "正文");

        Assert.AreEqual("标题", MemoryProgressiveLoader.TextFor(node, MemoryTier.Index));
    }

    [TestMethod]
    public void Summary_tier_falls_back_to_title_when_summary_missing()
    {
        var node = Node("n1", "标题", "", "正文");

        Assert.AreEqual("标题", MemoryProgressiveLoader.TextFor(node, MemoryTier.Summary));
    }

    [TestMethod]
    public void Body_tier_falls_back_to_summary_then_title()
    {
        var withSummary = Node("n1", "标题", "摘要", "");
        var onlyTitle = Node("n2", "标题", "", "");

        Assert.AreEqual("摘要", MemoryProgressiveLoader.TextFor(withSummary, MemoryTier.Body));
        Assert.AreEqual("标题", MemoryProgressiveLoader.TextFor(onlyTitle, MemoryTier.Body));
    }

    [TestMethod]
    public void Budget_overflow_is_reported_not_silent()
    {
        var nodes = Enumerable.Range(0, 5)
            .Select(i => Node($"n{i}", new string('x', 400), "", ""))
            .ToList();

        // 每条 title 400 字符 ≈ 100 tokens；预算 220 ⇒ 只能放 2 条。
        var result = MemoryProgressiveLoader.Slice(nodes, Empty(), MemoryTier.Index, budgetTokens: 220);

        Assert.AreEqual(2, result.Slices.Count);
        Assert.AreEqual(3, result.DroppedByBudget, "被预算丢掉的条数必须如实报告");
        Assert.IsTrue(result.Truncated);
        Assert.IsTrue(result.UsedTokens <= 220);
    }

    [TestMethod]
    public void Empty_text_nodes_are_skipped_without_counting_as_dropped()
    {
        var empty = new MemoryNode { NodeId = "e", Kind = MemoryNodeKind.Fact, Title = "   " };
        var real = Node("r", "真实", "", "");

        var result = MemoryProgressiveLoader.Slice([empty, real], Empty(), MemoryTier.Index, 1000);

        Assert.AreEqual(1, result.Slices.Count);
        Assert.AreEqual(0, result.DroppedByBudget, "空文本不是被预算裁掉的，不该计入 dropped");
    }

    [TestMethod]
    public void Tombstoned_nodes_are_skipped()
    {
        var dead = new MemoryNode
        {
            NodeId = "d", Kind = MemoryNodeKind.Fact, Title = "已删", Tombstoned = true,
        };

        var result = MemoryProgressiveLoader.Slice([dead], Empty(), MemoryTier.Index, 1000);

        Assert.AreEqual(0, result.Slices.Count);
    }
}

/// <summary>图遍历：白名单关系、跳数正确、有硬上限。</summary>
[TestClass]
public sealed class MemoryGraphTraversalTests
{
    private static MemoryEdge Edge(string from, string to, MemoryRelation rel, double weight = 1.0) => new()
    {
        FromNodeId = from,
        ToNodeId = to,
        Relation = rel,
        Weight = weight,
    };

    [TestMethod]
    public void One_hop_neighbors_are_found_bidirectionally()
    {
        var edges = new[] { Edge("a", "b", MemoryRelation.RelatedTo) };

        var result = MemoryGraphTraversal.Expand(["a"], edges, maxHops: 1);

        Assert.AreEqual(0, result.HopDistance["a"]);
        Assert.AreEqual(1, result.HopDistance["b"], "边方向只表达来源语义，不应限制可达性");
    }

    [TestMethod]
    public void Contradicts_is_not_expanded_by_default()
    {
        var edges = new[] { Edge("a", "old", MemoryRelation.Contradicts) };

        var result = MemoryGraphTraversal.Expand(["a"], edges, maxHops: 1);

        Assert.IsFalse(result.HopDistance.ContainsKey("old"),
            "矛盾边默认不展开：否则一条矛盾会把整片旧记忆拉回来");
    }

    [TestMethod]
    public void Low_weight_edges_are_skipped_and_counted()
    {
        var edges = new[] { Edge("a", "b", MemoryRelation.RelatedTo, weight: 0.05) };

        var result = MemoryGraphTraversal.Expand(["a"], edges, maxHops: 1, minWeight: 0.2);

        Assert.IsFalse(result.HopDistance.ContainsKey("b"));
        Assert.AreEqual(1, result.SkippedEdges);
    }

    [TestMethod]
    public void Max_nodes_limit_is_reported()
    {
        var edges = Enumerable.Range(0, 10)
            .Select(i => Edge("a", $"n{i}", MemoryRelation.RelatedTo))
            .ToArray();

        var result = MemoryGraphTraversal.Expand(["a"], edges, maxHops: 1, maxNodes: 3);

        Assert.IsTrue(result.HitNodeLimit, "触到节点上限必须显式报告，不能悄悄截断");
        Assert.IsTrue(result.HopDistance.Count <= 3);
    }

    [TestMethod]
    public void Two_hops_reach_distance_two()
    {
        var edges = new[]
        {
            Edge("a", "b", MemoryRelation.RelatedTo),
            Edge("b", "c", MemoryRelation.RelatedTo),
        };

        var result = MemoryGraphTraversal.Expand(["a"], edges, maxHops: 2);

        Assert.AreEqual(1, result.HopDistance["b"]);
        Assert.AreEqual(2, result.HopDistance["c"]);
    }

    [TestMethod]
    public void Hops_beyond_two_are_clamped()
    {
        var result = MemoryGraphTraversal.Expand(["a"], [], maxHops: 9);

        Assert.AreEqual(0, result.HopDistance["a"]);
        Assert.IsFalse(result.HitNodeLimit);
    }
}
