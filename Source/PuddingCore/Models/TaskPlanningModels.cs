namespace PuddingCode.Models;

/// <summary>Lifecycle states for a task planning run.</summary>
public enum TaskPlanStatuses
{
    /// <summary>Plan is created but not yet activated.</summary>
    Draft,

    /// <summary>Plan is currently executing and accepting node updates.</summary>
    Active,

    /// <summary>Plan finished successfully.</summary>
    Completed,

    /// <summary>Plan failed and requires follow-up planning.</summary>
    Failed,

    /// <summary>Plan was cancelled and will no longer receive new work.</summary>
    Cancelled
}

/// <summary>Lifecycle states for an individual task node.</summary>
public enum TaskNodeStatuses
{
    /// <summary>Node exists but is not yet planned.</summary>
    Draft,

    /// <summary>Node is planned and ready for assignment.</summary>
    Planned,

    /// <summary>Node has a designated assignee.</summary>
    Assigned,

    /// <summary>Node is currently running.</summary>
    Running,

    /// <summary>Node is blocked on external input or dependency.</summary>
    Blocked,

    /// <summary>Node completed successfully.</summary>
    Completed,

    /// <summary>Node failed and needs recovery or replanning.</summary>
    Failed,

    /// <summary>Node was cancelled and will not continue.</summary>
    Cancelled,

    /// <summary>Node is replaced by a newer node.</summary>
    Superseded
}

/// <summary>Who receives task node execution.</summary>
public enum TaskAssignmentKinds
{
    /// <summary>Execution stays with the current leader agent.</summary>
    Leader,

    /// <summary>Execution is delegated to a workspace agent.</summary>
    WorkspaceAgent,

    /// <summary>Execution is delegated to a spawned sub-agent.</summary>
    SubAgent,

    /// <summary>No assignment yet; waiting for delegation.</summary>
    Unassigned
}

/// <summary>One durable task planning run.</summary>
public sealed record TaskPlanRun
{
    /// <summary>任务计划 ID。</summary>
    public required string PlanId { get; init; }

    /// <summary>所属工作区 ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>触发计划的会话 ID。</summary>
    public required string RootSessionId { get; init; }

    /// <summary>发起 Leader Agent ID。</summary>
    public required string LeaderAgentId { get; init; }

    /// <summary>任务目标描述。</summary>
    public string? Objective { get; init; }

    /// <summary>计划状态。</summary>
    public TaskPlanStatuses Status { get; init; } = TaskPlanStatuses.Draft;

    /// <summary>本计划可用的最大委派深度。</summary>
    public int MaxDelegationDepth { get; init; } = 2;

    /// <summary>是否允许子节点继续委派。</summary>
    public bool DefaultAllowSubDelegation { get; init; } = true;

    /// <summary>是否允许 Leader 创建 Team Agent。</summary>
    public bool AllowAgentCreationByLeader { get; init; } = true;

    /// <summary>计划内允许的最大活跃节点数。</summary>
    public int MaxActiveTaskNodesPerPlan { get; init; } = 50;

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>更新时间（UTC）。</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>完成时间（UTC），仅在 Completed/Failed/Cancelled 时设置。</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>结果摘要（如 plan-level outcome）。</summary>
    public string? ResultSummary { get; init; }

    /// <summary>失败原因（如 plan-level error）。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>追踪标识。</summary>
    public string? TraceId { get; init; }

    /// <summary>请求级追踪标识。</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>One task node in a planning tree.</summary>
public sealed record TaskNode
{
    /// <summary>任务节点 ID。</summary>
    public required string TaskNodeId { get; init; }

    /// <summary>归属的计划 ID。</summary>
    public required string PlanId { get; init; }

    /// <summary>父节点 ID，根节点为空。</summary>
    public string? ParentTaskNodeId { get; init; }

    /// <summary>节点深度：根节点为 0。</summary>
    public int Depth { get; init; }

    /// <summary>节点标题。</summary>
    public string? Title { get; init; }

    /// <summary>节点目标。</summary>
    public string? Objective { get; init; }

    /// <summary>上下文摘要。</summary>
    public string? InputContextSummary { get; init; }

    /// <summary>期望输出契约。</summary>
    public string? ExpectedOutputContract { get; init; }

    /// <summary>分配对象类型。</summary>
    public TaskAssignmentKinds AssignedToKind { get; init; } = TaskAssignmentKinds.Unassigned;

    /// <summary>分配对象 ID。</summary>
    public string? AssignedToId { get; init; }

    /// <summary>关联模板 ID（仅 sub-agent 分配场景）。</summary>
    public string? AssignedTemplateId { get; init; }

    /// <summary>创建该节点的 Agent ID。</summary>
    public string? CreatedByAgentId { get; init; }

    /// <summary>节点状态。</summary>
    public TaskNodeStatuses Status { get; init; } = TaskNodeStatuses.Draft;

    /// <summary>是否允许子节点继续委派。</summary>
    public bool AllowSubDelegation { get; init; } = true;

    /// <summary>是否允许创建子代理。</summary>
    public bool AllowAgentCreation { get; init; } = true;

    /// <summary>结果摘要。</summary>
    public string? ResultSummary { get; init; }

    /// <summary>结果产物引用。</summary>
    public string? ResultArtifactRef { get; init; }

    /// <summary>失败原因。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>被替代节点 ID（可选）。</summary>
    public string? SupersededByTaskNodeId { get; init; }

    /// <summary>开始时间（UTC）。</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>完成时间（UTC）。</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>更新时间（UTC）。</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Contract for creating a task plan.</summary>
public sealed record TaskPlanCreateRequest
{
    /// <summary>所属工作区 ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>触发计划的会话 ID。</summary>
    public required string RootSessionId { get; init; }

    /// <summary>发起 Leader Agent ID。</summary>
    public required string LeaderAgentId { get; init; }

    /// <summary>任务目标。</summary>
    public string? Objective { get; init; }

    /// <summary>覆盖最大委派深度。</summary>
    public int? MaxDelegationDepth { get; init; }

    /// <summary>覆盖“允许子节点继续委派”配置。</summary>
    public bool? DefaultAllowSubDelegation { get; init; }

    /// <summary>覆盖“允许 Leader 创建 Agent”配置。</summary>
    public bool? AllowAgentCreationByLeader { get; init; }

    /// <summary>覆盖每个计划最大活跃节点数。</summary>
    public int? MaxActiveTaskNodesPerPlan { get; init; }

    /// <summary>追踪标识。</summary>
    public string? TraceId { get; init; }

    /// <summary>请求级追踪标识。</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>Contract for creating a task node.</summary>
public sealed record TaskNodeCreateRequest
{
    /// <summary>归属计划 ID。</summary>
    public required string PlanId { get; init; }

    /// <summary>父节点 ID，根节点可为空。</summary>
    public string? ParentTaskNodeId { get; init; }

    /// <summary>节点深度。</summary>
    public int Depth { get; init; }

    /// <summary>节点标题。</summary>
    public string? Title { get; init; }

    /// <summary>节点目标。</summary>
    public required string Objective { get; init; }

    /// <summary>上下文摘要。</summary>
    public string? InputContextSummary { get; init; }

    /// <summary>期望输出契约。</summary>
    public string? ExpectedOutputContract { get; init; }

    /// <summary>分配对象。</summary>
    public TaskAssignmentKinds AssignedToKind { get; init; } = TaskAssignmentKinds.Unassigned;

    /// <summary>分配对象 ID（可选）。</summary>
    public string? AssignedToId { get; init; }

    /// <summary>创建者 Agent ID。</summary>
    public string? CreatedByAgentId { get; init; }

    /// <summary>关联模板 ID（用于 sub-agent）。</summary>
    public string? AssignedTemplateId { get; init; }

    /// <summary>是否允许子节点继续委派。</summary>
    public bool? AllowSubDelegation { get; init; }

    /// <summary>是否允许创建子代理。</summary>
    public bool? AllowAgentCreation { get; init; }
}

/// <summary>Contract for assigning a task node to a target.</summary>
public sealed record TaskAssignmentRequest
{
    /// <summary>要分配的任务节点 ID。</summary>
    public required string TaskNodeId { get; init; }

    /// <summary>分配对象类型。</summary>
    public TaskAssignmentKinds AssignedToKind { get; init; } = TaskAssignmentKinds.Unassigned;

    /// <summary>分配对象 ID（Leader/WorkspaceAgent 目标）。</summary>
    public string? AssignedToId { get; init; }

    /// <summary>SubAgent 使用的模板 ID。</summary>
    public string? AssignedTemplateId { get; init; }

    /// <summary>可在任务分配时覆盖目标任务描述。</summary>
    public string? Objective { get; init; }

    /// <summary>任务树中的角色标识（可选）。</summary>
    public string? RoleInPlan { get; init; }

    /// <summary>期望输出契约（可选）。</summary>
    public string? ExpectedOutputContract { get; init; }
}

/// <summary>Contract for updating a task node status.</summary>
public sealed record TaskNodeStatusUpdateRequest
{
    /// <summary>要更新的任务节点 ID。</summary>
    public required string TaskNodeId { get; init; }

    /// <summary>新的节点状态。</summary>
    public TaskNodeStatuses Status { get; init; }

    /// <summary>结果摘要。</summary>
    public string? ResultSummary { get; init; }

    /// <summary>结果产物引用。</summary>
    public string? ResultArtifactRef { get; init; }

    /// <summary>失败原因（失败路径可选）。</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>Query for locating task planning runs.</summary>
public sealed record TaskPlanQuery
{
    /// <summary>按工作区过滤。</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>按状态过滤。</summary>
    public TaskPlanStatuses? Status { get; init; }

    /// <summary>按创建者过滤。</summary>
    public string? LeaderAgentId { get; init; }

    /// <summary>起始时间。</summary>
    public DateTimeOffset? CreatedFrom { get; init; }

    /// <summary>截止时间。</summary>
    public DateTimeOffset? CreatedTo { get; init; }

    /// <summary>分页大小。</summary>
    public int Limit { get; init; } = 100;

    /// <summary>分页偏移。</summary>
    public int Offset { get; init; }
}

/// <summary>Query for locating task nodes.</summary>
public sealed record TaskNodeQuery
{
    /// <summary>按计划过滤。</summary>
    public string? PlanId { get; init; }

    /// <summary>按父节点过滤。</summary>
    public string? ParentTaskNodeId { get; init; }

    /// <summary>按状态过滤。</summary>
    public TaskNodeStatuses? Status { get; init; }

    /// <summary>按分配对象类型过滤。</summary>
    public TaskAssignmentKinds? AssignedToKind { get; init; }

    /// <summary>按分配目标 ID 过滤。</summary>
    public string? AssignedToId { get; init; }

    /// <summary>按深度过滤。</summary>
    public int? Depth { get; init; }

    /// <summary>分页大小。</summary>
    public int Limit { get; init; } = 100;

    /// <summary>分页偏移。</summary>
    public int Offset { get; init; }
}
