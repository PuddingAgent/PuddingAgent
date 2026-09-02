using PuddingCode.Tasks;

namespace PuddingCode.Scheduling;

public enum TaskAutoDispatchCandidateVerdict
{
    Eligible = 0,
    Deferred = 1,
    Denied = 2,
}

/// <summary>
/// 确定性调度评分分解。所有因子归一到 0..1，Total 为冻结权重的加权和（0..100 标度）。
/// 权重常量冻结在 PuddingPlatform 的 <c>TaskSchedulerScoreWeights</c>；本记录只承载数值，
/// 可 JSON 序列化并随决策持久化到 task_scheduler_decisions.score_breakdown_json。
/// </summary>
public sealed record TaskSchedulerScoreBreakdown
{
    // ── Task 侧成分（6 项，冻结）──────────────────────────────
    /// <summary>优先级因子（P0 最高）。</summary>
    public double Priority { get; init; }

    /// <summary>due 紧迫度因子（越临近越高；无 due 为中性低值）。</summary>
    public double DueUrgency { get; init; }

    /// <summary>排队时长因子（age，越老越高）。</summary>
    public double Age { get; init; }

    /// <summary>关键路径因子（当前 schema 无数据源，冻结中性值 0.5）。</summary>
    public double CriticalPath { get; init; }

    /// <summary>重试惩罚因子（带 failure_code 的任务降权）。</summary>
    public double RetryPenalty { get; init; }

    /// <summary>预期时长因子（当前 schema 无数据源，冻结中性值 0.5）。</summary>
    public double ExpectedDuration { get; init; }

    // ── Agent 匹配因子（4 项，源自 RouteMatcher / AvailabilityProjection 已有信号）──
    /// <summary>能力匹配因子（RouteMatcher 兼容者必为 1.0，不兼容为 0）。</summary>
    public double AgentCapability { get; init; }

    /// <summary>路由偏好因子（preferred_agent=1.0，compatible_agent=0.7）。</summary>
    public double AgentRoute { get; init; }

    /// <summary>健康度因子（Idle=1.0，Unknown=0.5，其余 0）。</summary>
    public double AgentHealth { get; init; }

    /// <summary>容量因子（可接受自动任务=1.0，否则 0）。</summary>
    public double AgentCapacity { get; init; }

    /// <summary>加权和（0..100 标度），排序规则：Total desc → AgentId 稳定序。</summary>
    public double Total { get; init; }
}

/// <summary>Evaluate-only scheduling result. It never implies that a Task was dispatched.</summary>
public sealed record TaskAutoDispatchCandidateDecision
{
    public required string WorkspaceId { get; init; }
    public required string TaskId { get; init; }
    public int? TaskVersion { get; init; }
    public string? AgentId { get; init; }
    public string? TaskType { get; init; }
    public string? AgentSelectionCode { get; init; }
    public string? AgentRoutingFingerprint { get; init; }
    public string? ExecutionPlanFingerprint { get; init; }
    public int? ExecutionPlanSchemaVersion { get; init; }
    public int? ExecutionPlanVersion { get; init; }
    public string? ConversationId { get; init; }
    public TaskExecutionWindow? ExecutionWindow { get; init; }
    public required TaskAutoDispatchCandidateVerdict Verdict { get; init; }
    public required string Code { get; init; }
    public required DateTimeOffset EvaluatedAtUtc { get; init; }
    public DateTimeOffset? NextEligibleAtUtc { get; init; }
    public long? AvailabilityVersion { get; init; }
    public string? AvailabilityReason { get; init; }
    public string? DependencyState { get; init; }
    public string? WindowCode { get; init; }
    public TaskSchedulerScoreBreakdown? ScoreBreakdown { get; init; }
}

public interface ITaskAutoDispatchEvaluator
{
    Task<IReadOnlyList<TaskAutoDispatchCandidateDecision>> EvaluateAsync(
        string workspaceId,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Task-scoped 事件驱动评估（实施方案 §5.2）：只评估指定的 taskIds（仍限于 Ready/Deferred
    /// 且 auto_dispatch_enabled 的候选），供事件驱动 Coordinator 按触发 Task 结算 Intent。
    /// 不在可评估集合内的 taskId 不产生 decision——由调用方落 terminal/ineligible outcome。
    /// </summary>
    Task<IReadOnlyList<TaskAutoDispatchCandidateDecision>> EvaluateTasksAsync(
        string workspaceId,
        IReadOnlyCollection<string> taskIds,
        int candidateLimit,
        CancellationToken ct = default);
}
