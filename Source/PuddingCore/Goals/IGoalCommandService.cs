namespace PuddingCode.Goals;

/// <summary>
/// ADR-074 §4: Goal 命令应用服务。slash 文本入口（SystemCommandHandler）与
/// 结构化 Control Plane API 共用同一实现、同一幂等语义，产生相同事件。
/// </summary>
public interface IGoalCommandService
{
    Task<GoalCommandResult> ExecuteAsync(GoalCommandRequest request, CancellationToken ct = default);
}

/// <summary>Goal 只读查询。任意入口看到的状态都来自同一服务端投影。</summary>
public interface IGoalQueryService
{
    /// <summary>会话当前非终态 Goal；无则返回 null。</summary>
    Task<GoalSnapshot?> GetActiveAsync(
        string workspaceId,
        string conversationId,
        CancellationToken ct = default);

    Task<GoalSnapshot?> GetAsync(string goalRunId, CancellationToken ct = default);

    /// <summary>会话最近一个 Goal（含终态），供 Banner 在无活动 Goal 时展示历史回执。</summary>
    Task<GoalSnapshot?> GetLatestAsync(
        string workspaceId,
        string conversationId,
        CancellationToken ct = default);

    Task<IReadOnlyList<GoalIterationSnapshot>> GetIterationsAsync(
        string goalRunId,
        CancellationToken ct = default);

    /// <summary>
    /// W2：目标 → 冻结计划步骤（depth1 叶子）+ 目标级校验状态的只读投影；goal 不存在返回 null。
    /// 注意：快照中的 Checks 是<b>目标级</b>投影（goal_check_records 只有 goal_run_id 外键，
    /// 与具体步骤没有外键关联），不得当作 per-step 校验展示。
    /// </summary>
    Task<GoalStepsSnapshot?> GetStepsAsync(string goalRunId, CancellationToken ct = default);
}

/// <summary>
/// W2：目标步骤与校验状态的只读投影（服务端唯一事实源，控制器只做形状映射）。
/// </summary>
public sealed record GoalStepsSnapshot
{
    public required string GoalRunId { get; init; }

    public required GoalPhase Phase { get; init; }

    /// <summary>绑定是否存在冻结计划（plan run 行存在且含 depth1 叶子）。false 时 Steps 为空、计数全 0。</summary>
    public required bool HasPlan { get; init; }

    /// <summary>冻结计划的 plan_version（取该 plan_id 的最高版本）；无计划时为 null。</summary>
    public int? PlanVersion { get; init; }

    public required GoalStepProgressSnapshot Progress { get; init; }

    /// <summary>depth1 叶子步骤，按 sequence_no 升序。</summary>
    public required IReadOnlyList<GoalStepSnapshot> Steps { get; init; }

    /// <summary>目标级校验记录（仅当前 activation epoch）——不是 per-step 校验。</summary>
    public required IReadOnlyList<GoalCheckSnapshot> Checks { get; init; }
}

/// <summary>步骤进度计数。终态口径与 GoalRunStore.FindCurrentTaskWorkUnitAsync 一致。</summary>
public sealed record GoalStepProgressSnapshot
{
    public required int StepsTotal { get; init; }

    public required int StepsPassed { get; init; }

    public required int StepsFailed { get; init; }

    /// <summary>非终态步骤数（Completed/Cancelled/Superseded 之外均计为进行中）。</summary>
    public required int StepsInProgress { get; init; }

    /// <summary>当前步骤；复用 GoalRunStore.FindCurrentTaskWorkUnitAsync 选定（首个非终态 depth1 叶子）。</summary>
    public string? CurrentStepId { get; init; }
}

/// <summary>单个步骤（task_nodes depth1 叶子）的只读投影。</summary>
public sealed record GoalStepSnapshot
{
    public required string NodeId { get; init; }

    public required int SequenceNo { get; init; }

    public string? Kind { get; init; }

    public string? Title { get; init; }

    /// <summary>task_nodes.status 原文（如 Draft/Planned/Assigned/Running/Blocked/Completed/Failed）。</summary>
    public required string Status { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>task_nodes 无 blocker code 列；不伪造，当前恒为 null（保留字段位）。</summary>
    public string? BlockerCode { get; init; }

    /// <summary>
    /// 只投射真实存在的关联：task_nodes.result_artifact_ref / checkpoint_artifact_ref，
    /// 以及 work_unit_await_handles 的 (plan_id, task_node_id) 外键（格式 await-handle:{await_handle_id}）。
    /// goal_check_records 与步骤之间没有任何外键，检查绝不混入这里。
    /// </summary>
    public required IReadOnlyList<string> EvidenceRefs { get; init; }
}

/// <summary>目标级校验记录（goal_check_records）的只读投影。非 per-step：与步骤无外键关联。</summary>
public sealed record GoalCheckSnapshot
{
    public required string CheckId { get; init; }

    public required string CriterionId { get; init; }

    /// <summary>finished 记录取报告状态（passed/failed/waiting/invalidated）；否则 pending/leased。</summary>
    public required string Status { get; init; }

    /// <summary>报告的进程退出码（build/test 期望 0）；无报告为 null。</summary>
    public int? ExitCode { get; init; }

    /// <summary>报告的人类可读说明（GoalCheckReport.Message）；无报告为 null。</summary>
    public string? Summary { get; init; }

    /// <summary>来自 GoalCheckReport.EvidenceRefs 的原始证据引用；无报告为空。</summary>
    public required IReadOnlyList<string> EvidenceRefs { get; init; }
}
