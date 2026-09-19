using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 持久化的任务规划执行（task_plan_runs）。
/// <para>
/// <see cref="Status"/> 不是执行期状态机，而是 Goal 结算的派生投影：
/// <c>Active</c> 仅在 TaskGoalDispatchTransactionStore.AddExecutionPlan 创建时写入；
/// <c>Failed</c> 仅由 GoalSettlementStore 在结算终局写入，语义 =「该计划未被完成」，
/// 不是「计划有缺陷」——有意设计（见 GoalSettlementStore.ApplyBoundPlanVerdict 注释）。
/// </para>
/// <para>
/// <see cref="FailureCode"/>：结构化失败原因码，由 Goal 结算写入，取自结算 BlockerCode
/// 或内置码（如 acceptance_contract_missing、no_progress_circuit_open、
/// accepted_iteration_budget_exhausted）；
/// <see cref="FailedStage"/>：失败发生阶段，verdict = ApplyBoundPlanVerdict 的 Stop 分支，
/// settlement = 结算主流程（不可恢复终态 / 迭代预算耗尽）。二者仅 Failed 计划非空。
/// </para>
/// </summary>
[Table("task_plan_runs")]
public sealed class TaskPlanRunEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("plan_id")]
    public string PlanId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(64), Column("workspace_task_id")]
    public string? WorkspaceTaskId { get; set; }

    [Column("workspace_task_version")]
    public int? WorkspaceTaskVersion { get; set; }

    [Column("plan_version")]
    public int PlanVersion { get; set; } = 1;

    [Column("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [Required, MaxLength(32), Column("plan_kind")]
    public string PlanKind { get; set; } = "delegation";

    [MaxLength(64), Column("plan_fingerprint")]
    public string? PlanFingerprint { get; set; }

    [Required, MaxLength(64), Column("root_session_id")]
    public string RootSessionId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("leader_agent_id")]
    public string LeaderAgentId { get; set; } = string.Empty;

    [MaxLength(1024), Column("objective")]
    public string? Objective { get; set; }

    [Required, MaxLength(32), Column("status")]
    public string Status { get; set; } = "Draft";

    [Column("max_delegation_depth")]
    public int MaxDelegationDepth { get; set; } = 2;

    [Column("default_allow_sub_delegation")]
    public bool DefaultAllowSubDelegation { get; set; } = true;

    [Column("allow_agent_creation_by_leader")]
    public bool AllowAgentCreationByLeader { get; set; } = true;

    [Column("max_active_task_nodes_per_plan")]
    public int MaxActiveTaskNodesPerPlan { get; set; } = 50;

    [Column("created_at")]
    public long CreatedAt { get; set; }

    [Column("updated_at")]
    public long UpdatedAt { get; set; }

    [Column("completed_at")]
    public long? CompletedAt { get; set; }

    [Column("result_summary")]
    public string? ResultSummary { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    /// <summary>结构化失败原因码（Goal 结算写入；仅 Failed 计划非空，取值见类注释）。</summary>
    [MaxLength(128), Column("failure_code")]
    public string? FailureCode { get; set; }

    /// <summary>失败发生阶段（verdict / settlement；仅 Failed 计划非空，取值见类注释）。</summary>
    [MaxLength(128), Column("failed_stage")]
    public string? FailedStage { get; set; }

    [MaxLength(64), Column("trace_id")]
    public string? TraceId { get; set; }

    [MaxLength(64), Column("correlation_id")]
    public string? CorrelationId { get; set; }
}
