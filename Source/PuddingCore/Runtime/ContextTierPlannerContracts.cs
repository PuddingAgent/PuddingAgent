namespace PuddingCode.Runtime;

/// <summary>
/// TierPlanner 的单个段输入。由调用方从 ContextSegment / 消息序列映射而来；
/// planner 不关心存储，只关心「轮次距离 + 当前执行 + query 命中 + 原子组」。
/// </summary>
/// <param name="SegmentId">段唯一标识（回显用）。</param>
/// <param name="TurnOrdinal">轮次序号，0-based，从最早开始递增。</param>
/// <param name="IsCurrentTurn">是否属于当前执行（未完成）轮 → 强制 T0。</param>
/// <param name="IsQueryHit">是否被当前 query 命中 → 用于有界晋升。</param>
/// <param name="AtomicGroupId">原子工具组 ID（可空）；同 ID 的段必须同 tier，不可拆分。</param>
public sealed record TierPlannerSegmentInput(
    string SegmentId,
    int TurnOrdinal,
    bool IsCurrentTurn,
    bool IsQueryHit,
    string? AtomicGroupId);

/// <summary>单个段的 tier 分配结果。</summary>
/// <param name="SegmentId">段唯一标识（与输入一致）。</param>
/// <param name="Tier">分配到的分级（T0–T4，见 <see cref="ContextSegmentTier"/>）。</param>
/// <param name="IsPromoted">是否因 query 命中发生有界晋升。</param>
/// <param name="PromotionReason">晋升原因（如 "query-hit"；未晋升时为 null）。</param>
public sealed record TierAssignment(
    string SegmentId,
    ContextSegmentTier Tier,
    bool IsPromoted,
    string? PromotionReason);

/// <summary>整段规划结果（与输入同序）。</summary>
/// <param name="Assignments">与输入同序的分配结果列表。</param>
public sealed record ContextTierPlan(
    IReadOnlyList<TierAssignment> Assignments);

/// <summary>分级阈值与晋升目标。</summary>
/// <param name="RecentTurnCount">T1：距离 0..N（含当前轮已完成部分）。</param>
/// <param name="WarmThroughDistance">T2：距离上限（含）。</param>
/// <param name="ColdThroughDistance">T3：距离上限（含）。</param>
/// <param name="PromotionTarget">query 命中的有界晋升目标 tier。</param>
public sealed record ContextTierPlanOptions(
    int RecentTurnCount = 2,
    int WarmThroughDistance = 10,
    int ColdThroughDistance = 50,
    ContextSegmentTier PromotionTarget = ContextSegmentTier.T1);

/// <summary>
/// 纯函数式分级规划器契约：输入段序列，输出每段 T0–T4 分级。
/// 规划器不感知存储与消息结构，只依赖「轮次距离 + 当前执行 + query 命中 + 原子组」。
/// </summary>
public interface IContextTierPlanner
{
    /// <summary>对段序列进行 T0–T4 分级规划。</summary>
    /// <param name="segments">有序段序列（轮次距离与当前执行由调用方映射）。</param>
    /// <param name="options">分级阈值与晋升目标；null 时使用默认值。</param>
    /// <returns>与输入同序的分级结果。</returns>
    ContextTierPlan Plan(
        IReadOnlyList<TierPlannerSegmentInput> segments,
        ContextTierPlanOptions? options = null);
}
