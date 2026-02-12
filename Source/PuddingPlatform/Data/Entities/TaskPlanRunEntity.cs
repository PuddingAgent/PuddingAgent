using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>持久化的任务规划执行。</summary>
[Table("task_plan_runs")]
public sealed class TaskPlanRunEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("plan_id")]
    public string PlanId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

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

    [MaxLength(64), Column("trace_id")]
    public string? TraceId { get; set; }

    [MaxLength(64), Column("correlation_id")]
    public string? CorrelationId { get; set; }
}
