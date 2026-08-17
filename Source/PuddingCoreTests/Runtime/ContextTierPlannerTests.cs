using PuddingCode.Runtime;

namespace PuddingCoreTests.Runtime;

/// <summary>
/// P1-1 Task C：ContextTierPlanner 纯逻辑分级规划器单测。
/// 覆盖轮次距离边界、原子组不可拆分、query 命中的有界晋升、自定义阈值与输出同序。
/// 纯逻辑测试，无 async/EF/Sqlite 依赖。
/// </summary>
[TestClass]
public sealed class ContextTierPlannerTests
{
    private static readonly IContextTierPlanner Planner = new ContextTierPlanner();

    private static TierPlannerSegmentInput Seg(
        string id, int turn, bool current = false, bool hit = false, string? group = null)
        => new(id, turn, current, hit, group);

    private static ContextTierPlan Plan(params TierPlannerSegmentInput[] segments)
        => Planner.Plan(segments);

    private static TierAssignment Find(ContextTierPlan plan, string segmentId)
        => plan.Assignments.First(a => a.SegmentId == segmentId);

    private static void AssertTier(ContextTierPlan plan, string segmentId, ContextSegmentTier expected)
        => Assert.AreEqual(expected, Find(plan, segmentId).Tier);

    [TestMethod]
    public void EmptyInput_ReturnsEmptyAssignments()
    {
        var plan = Plan();

        Assert.IsNotNull(plan.Assignments);
        Assert.IsEmpty(plan.Assignments);
    }

    [TestMethod]
    public void SingleCurrentTurnSegment_AssignedT0()
    {
        var plan = Plan(Seg("s1", 5, current: true));

        AssertTier(plan, "s1", ContextSegmentTier.T0);
        Assert.IsFalse(Find(plan, "s1").IsPromoted);
    }

    [TestMethod]
    public void SingleNonCurrentSegment_AssignedT1_WhenDistanceZero()
    {
        // TurnOrdinal=0，currentTurn=0，distance=0 → T1。
        var plan = Plan(Seg("s1", 0));

        AssertTier(plan, "s1", ContextSegmentTier.T1);
    }

    [TestMethod]
    public void DistanceBoundaries_MapToExpectedTiers()
    {
        // 锚点段提供 currentTurn=100；被测段按 distance 落位。
        var plan = Plan(
            Seg("anchor", 100),
            Seg("d2", 98),   // distance=2  → T1
            Seg("d3", 97),   // distance=3  → T2
            Seg("d10", 90),  // distance=10 → T2
            Seg("d11", 89),  // distance=11 → T3
            Seg("d50", 50),  // distance=50 → T3
            Seg("d51", 49)); // distance=51 → T4

        AssertTier(plan, "d2", ContextSegmentTier.T1);
        AssertTier(plan, "d3", ContextSegmentTier.T2);
        AssertTier(plan, "d10", ContextSegmentTier.T2);
        AssertTier(plan, "d11", ContextSegmentTier.T3);
        AssertTier(plan, "d50", ContextSegmentTier.T3);
        AssertTier(plan, "d51", ContextSegmentTier.T4);
    }

    [TestMethod]
    public void TurnOrdinalGapsAndShuffledOrder_PreserveInputOrderAndDistanceTiers()
    {
        // 轮次序号有空洞（99..50 缺位）且输入乱序：仍按 distance 分级，输出与输入同序。
        var plan = Plan(
            Seg("late", 49),   // distance=51 → T4
            Seg("anchor", 100),// distance=0  → T1
            Seg("mid", 90),    // distance=10 → T2
            Seg("near", 98));  // distance=2  → T1

        Assert.IsTrue(plan.Assignments.Select(a => a.SegmentId)
            .SequenceEqual(new[] { "late", "anchor", "mid", "near" }));
        AssertTier(plan, "late", ContextSegmentTier.T4);
        AssertTier(plan, "anchor", ContextSegmentTier.T1);
        AssertTier(plan, "mid", ContextSegmentTier.T2);
        AssertTier(plan, "near", ContextSegmentTier.T1);
    }

    [TestMethod]
    public void AtomicGroup_UnifiedToMostFaithfulTier()
    {
        // 同组：distance=2（T1）与 distance=3（T2）→ 整组统一为 T1，不可拆分。
        var plan = Plan(
            Seg("anchor", 100),
            Seg("g1a", 98, group: "g1"),
            Seg("g1b", 97, group: "g1"));

        AssertTier(plan, "g1a", ContextSegmentTier.T1);
        AssertTier(plan, "g1b", ContextSegmentTier.T1);
    }

    [TestMethod]
    public void AtomicGroup_AcrossT3T4Boundary_UnifiedToT3()
    {
        // 同组：distance=50（T3）与 distance=51（T4）→ 整组统一为 T3，边界不落组中间。
        var plan = Plan(
            Seg("anchor", 100),
            Seg("g2a", 50, group: "g2"),
            Seg("g2b", 49, group: "g2"));

        AssertTier(plan, "g2a", ContextSegmentTier.T3);
        AssertTier(plan, "g2b", ContextSegmentTier.T3);
    }

    [TestMethod]
    public void QueryHitT3Segment_PromotedToPromotionTarget()
    {
        // T3 命中段 → 晋升 T1，带原因标记。
        var plan = Plan(
            Seg("anchor", 100),
            Seg("hit", 89, hit: true)); // distance=11 → T3

        var assignment = Find(plan, "hit");
        Assert.AreEqual(ContextSegmentTier.T1, assignment.Tier);
        Assert.IsTrue(assignment.IsPromoted);
        Assert.AreEqual("query-hit", assignment.PromotionReason);
    }

    [TestMethod]
    public void QueryHitWithinAtomicGroup_PromotesWholeGroup()
    {
        // 命中段属于原子组 → 整组晋升；未直接命中的成员也晋升（原子性优先于有界）。
        var plan = Plan(
            Seg("anchor", 100),
            Seg("hA", 89, hit: true, group: "gh"), // distance=11 → T3
            Seg("hB", 88, group: "gh"));           // distance=12 → T3，未直接命中

        AssertTier(plan, "hA", ContextSegmentTier.T1);
        AssertTier(plan, "hB", ContextSegmentTier.T1);
        Assert.IsTrue(Find(plan, "hA").IsPromoted);
        Assert.AreEqual("query-hit", Find(plan, "hA").PromotionReason);
        Assert.IsTrue(Find(plan, "hB").IsPromoted);
        Assert.AreEqual("query-hit", Find(plan, "hB").PromotionReason);
    }

    [TestMethod]
    public void QueryHit_OnlyPromotesHitSegment_NotSameTurnSiblings()
    {
        // 同轮次两段，仅命中者晋升；未命中者保持 T3（有界，不恢复整个旧窗口）。
        var plan = Plan(
            Seg("anchor", 100),
            Seg("xHit", 89, hit: true),
            Seg("yNoHit", 89));

        AssertTier(plan, "xHit", ContextSegmentTier.T1);
        Assert.IsTrue(Find(plan, "xHit").IsPromoted);

        AssertTier(plan, "yNoHit", ContextSegmentTier.T3);
        Assert.IsFalse(Find(plan, "yNoHit").IsPromoted);
    }

    [TestMethod]
    public void QueryHit_AlreadyAtOrAboveTarget_NotPromoted()
    {
        // 已为 T1 / T0 的命中段：tier 已 <= 目标，不晋升。
        var plan = Plan(
            Seg("anchor", 100),
            Seg("t1Hit", 99, hit: true),        // distance=1 → T1
            Seg("curHit", 100, current: true, hit: true)); // 当前轮 → T0

        AssertTier(plan, "t1Hit", ContextSegmentTier.T1);
        Assert.IsFalse(Find(plan, "t1Hit").IsPromoted);
        AssertTier(plan, "curHit", ContextSegmentTier.T0);
        Assert.IsFalse(Find(plan, "curHit").IsPromoted);
    }

    [TestMethod]
    public void CustomOptions_ThresholdsAndPromotionTargetApplied()
    {
        // 自定义：RecentTurnCount=1、WarmThroughDistance=5、ColdThroughDistance=20、PromotionTarget=T2。
        var options = new ContextTierPlanOptions(
            RecentTurnCount: 1,
            WarmThroughDistance: 5,
            ColdThroughDistance: 20,
            PromotionTarget: ContextSegmentTier.T2);

        var plan = Planner.Plan(new[]
        {
            Seg("anchor", 100),
            Seg("d1", 99),            // distance=1  → T1
            Seg("d2", 98),            // distance=2  → T2
            Seg("d5", 95),            // distance=5  → T2
            Seg("d6", 94),            // distance=6  → T3
            Seg("d6Hit", 94, hit: true), // distance=6 → T3，命中 → 晋升 T2
            Seg("d20", 80),           // distance=20 → T3
            Seg("d21", 79),           // distance=21 → T4
        }, options);

        AssertTier(plan, "d1", ContextSegmentTier.T1);
        AssertTier(plan, "d2", ContextSegmentTier.T2);
        AssertTier(plan, "d5", ContextSegmentTier.T2);
        AssertTier(plan, "d6", ContextSegmentTier.T3);
        AssertTier(plan, "d6Hit", ContextSegmentTier.T2);
        Assert.IsTrue(Find(plan, "d6Hit").IsPromoted);
        Assert.AreEqual("query-hit", Find(plan, "d6Hit").PromotionReason);
        AssertTier(plan, "d20", ContextSegmentTier.T3);
        AssertTier(plan, "d21", ContextSegmentTier.T4);
    }

    [TestMethod]
    public void NullAtomicGroupId_SegmentsClassifiedIndependently()
    {
        // 无原子组 ID 的相邻段：各自按 distance 分级，不互相绑定。
        var plan = Plan(
            Seg("anchor", 100),
            Seg("a", 98), // distance=2 → T1
            Seg("b", 97)); // distance=3 → T2

        AssertTier(plan, "a", ContextSegmentTier.T1);
        AssertTier(plan, "b", ContextSegmentTier.T2);
    }

    [TestMethod]
    public void CurrentTurnFlag_OverridesSameTurnNonCurrent()
    {
        // 同轮次：当前执行段强制 T0，已完成段按 distance=0 → T1。
        var plan = Plan(
            Seg("cur", 100, current: true),
            Seg("same", 100));

        AssertTier(plan, "cur", ContextSegmentTier.T0);
        AssertTier(plan, "same", ContextSegmentTier.T1);
    }
}
