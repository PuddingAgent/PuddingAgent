using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// 调度评分权重冻结表。所有因子归一到 0..1，权重为相对重要度（无单位），
/// Total = Σ(weight × factor)，量纲 0..100 分。权重一经冻结不可随配置漂移——
/// 评分必须可跨扫描对账，任何调整都应产出新的冻结版本并在决策行中可区分。
/// </summary>
public static class TaskSchedulerScoreWeights
{
    /// <summary>优先级：P0 独占 30 分，是排序的第一信号。</summary>
    public const double Priority = 30;

    /// <summary>due 紧迫度：一周内线性升温（20 分）。</summary>
    public const double DueUrgency = 20;

    /// <summary>排队时长：两周封顶线性增长（10 分）。</summary>
    public const double Age = 10;

    /// <summary>关键路径：当前 schema 无数据源，权重保留、因子冻结中性值（8 分）。</summary>
    public const double CriticalPath = 8;

    /// <summary>重试惩罚：带 failure_code 的任务降权（7 分）。</summary>
    public const double RetryPenalty = 7;

    /// <summary>预期时长：当前 schema 无数据源，权重保留、因子冻结中性值（5 分）。</summary>
    public const double ExpectedDuration = 5;

    /// <summary>Agent 能力匹配：RouteMatcher 兼容者必为 1.0（8 分）。</summary>
    public const double AgentCapability = 8;

    /// <summary>Agent 路由偏好：preferred_agent 高于 compatible_agent（6 分）。</summary>
    public const double AgentRoute = 6;

    /// <summary>Agent 健康度：Idle 满分、Unknown 半分（4 分）。</summary>
    public const double AgentHealth = 4;

    /// <summary>Agent 容量：可接受自动任务才满分（2 分）。</summary>
    public const double AgentCapacity = 2;

    /// <summary>权重总和（=100，Total 的量纲上界）。</summary>
    public static readonly double TotalWeight =
        Priority + DueUrgency + Age + CriticalPath + RetryPenalty + ExpectedDuration
        + AgentCapability + AgentRoute + AgentHealth + AgentCapacity;
}

/// <summary>
/// 确定性纯函数评分器：同输入恒同输出，无 I/O、无时钟读取、无随机。
/// 缺数据源的成分冻结中性值 0.5——同一批候选的中性项不影响相对排序。
/// </summary>
public static class TaskSchedulerScorer
{
    /// <summary>compatible_agent（非 preferred）的路由因子。</summary>
    public const double CompatibleRouteFactor = 0.7;

    /// <summary>无数据源成分的冻结中性值。</summary>
    public const double NeutralFactor = 0.5;

    /// <summary>带 failure_code 任务的 retry 惩罚因子。</summary>
    public const double FailedRetryFactor = 0.25;

    /// <summary>无 Agent 上下文的 Task 侧评分（deny/defer 在选定 Agent 前即可产生）。</summary>
    public static TaskSchedulerScoreBreakdown Score(
        WorkspaceTaskEntity task,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(task);
        var breakdown = new TaskSchedulerScoreBreakdown
        {
            Priority = PriorityFactor(task.Priority),
            DueUrgency = DueUrgencyFactor(task.DueAtUtc, now),
            Age = AgeFactor(task.CreatedAtUtc, now),
            CriticalPath = NeutralFactor,
            RetryPenalty = string.IsNullOrWhiteSpace(task.FailureCode) ? 1.0 : FailedRetryFactor,
            ExpectedDuration = NeutralFactor,
            AgentCapability = 0,
            AgentRoute = 0,
            AgentHealth = 0,
            AgentCapacity = 0,
        };
        return breakdown with { Total = WeightedTotal(breakdown) };
    }

    /// <summary>
    /// 完整评分（Evaluator 专用）：携带 RouteMatcher 的兼容性结论与 selection code。
    /// 兼容者能力因子恒 1.0（RouteMatcher 已保证 required capabilities 全覆盖）。
    /// </summary>
    public static TaskSchedulerScoreBreakdown Score(
        WorkspaceTaskEntity task,
        AgentAvailabilitySnapshot? availability,
        bool routeCompatible,
        string? routeSelectionCode,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(task);
        var breakdown = Score(task, now);
        breakdown = breakdown with
        {
            AgentCapability = routeCompatible ? 1.0 : 0,
            AgentRoute = routeCompatible
                ? string.Equals(routeSelectionCode, "preferred_agent", StringComparison.Ordinal)
                    ? 1.0
                    : CompatibleRouteFactor
                : 0,
            AgentHealth = availability?.State switch
            {
                AgentAvailabilityState.Idle => 1.0,
                AgentAvailabilityState.Unknown => NeutralFactor,
                _ => 0,
            },
            AgentCapacity = availability is { } avail && avail.CanAcceptAutomaticTask(now) ? 1.0 : 0,
        };
        return breakdown with { Total = WeightedTotal(breakdown) };
    }

    /// <summary>加权和（0..100 标度）。</summary>
    public static double WeightedTotal(TaskSchedulerScoreBreakdown breakdown)
        => Math.Round(
            (TaskSchedulerScoreWeights.Priority * Clamp01(breakdown.Priority))
            + (TaskSchedulerScoreWeights.DueUrgency * Clamp01(breakdown.DueUrgency))
            + (TaskSchedulerScoreWeights.Age * Clamp01(breakdown.Age))
            + (TaskSchedulerScoreWeights.CriticalPath * Clamp01(breakdown.CriticalPath))
            + (TaskSchedulerScoreWeights.RetryPenalty * Clamp01(breakdown.RetryPenalty))
            + (TaskSchedulerScoreWeights.ExpectedDuration * Clamp01(breakdown.ExpectedDuration))
            + (TaskSchedulerScoreWeights.AgentCapability * Clamp01(breakdown.AgentCapability))
            + (TaskSchedulerScoreWeights.AgentRoute * Clamp01(breakdown.AgentRoute))
            + (TaskSchedulerScoreWeights.AgentHealth * Clamp01(breakdown.AgentHealth))
            + (TaskSchedulerScoreWeights.AgentCapacity * Clamp01(breakdown.AgentCapacity)),
            4,
            MidpointRounding.AwayFromZero);

    /// <summary>P0→1.0 / P1→0.75 / P2→0.5 / P3+→0.25。</summary>
    public static double PriorityFactor(TaskPriority priority) => priority switch
    {
        TaskPriority.P0 => 1.0,
        TaskPriority.P1 => 0.75,
        TaskPriority.P2 => 0.5,
        _ => 0.25,
    };

    /// <summary>due 已过=1.0；一周内线性 1→0.25；一周外或无 due=0.25（中性低值）。</summary>
    public static double DueUrgencyFactor(DateTimeOffset? dueAtUtc, DateTimeOffset now)
    {
        if (dueAtUtc is null)
            return 0.25;
        if (dueAtUtc.Value <= now)
            return 1.0;
        var hoursUntil = (dueAtUtc.Value - now).TotalHours;
        if (hoursUntil >= 168)
            return 0.25;
        return Clamp01(1.0 - (0.75 * hoursUntil / 168.0));
    }

    /// <summary>两周封顶线性增长。</summary>
    public static double AgeFactor(DateTimeOffset createdAtUtc, DateTimeOffset now)
        => Clamp01((now - createdAtUtc).TotalDays / 14.0);

    private static double Clamp01(double value) => value switch
    {
        < 0 => 0,
        > 1 => 1,
        _ => value,
    };
}
