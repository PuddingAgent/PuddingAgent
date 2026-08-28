using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-074 §12/§22: task_goal_bindings — Task/Assignment 与 GoalRun 的 1:1 活动绑定。
/// G1 冻结 schema；Task-bound Goal 施工批次起写入。
/// 一个自动 Task 在任一时刻最多绑定一个非终态 GoalRun，反之亦然。
/// </summary>
[Table("task_goal_bindings")]
public class TaskGoalBindingEntity
{
    [Key, Required, MaxLength(64), Column("binding_id")]
    public string BindingId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [MaxLength(64), Column("assignment_id")]
    public string? AssignmentId { get; set; }

    [Column("expected_task_version")]
    public int? ExpectedTaskVersion { get; set; }

    [Required, MaxLength(64), Column("goal_run_id")]
    public string GoalRunId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("agent_instance_id")]
    public string AgentInstanceId { get; set; } = string.Empty;

    [MaxLength(64), Column("reservation_id")]
    public string? ReservationId { get; set; }

    [Column("reservation_fencing_token")]
    public long? ReservationFencingToken { get; set; }

    [MaxLength(64), Column("task_plan_id")]
    public string? TaskPlanId { get; set; }

    [MaxLength(64), Column("plan_fingerprint")]
    public string? PlanFingerprint { get; set; }

    [Column("execution_window_snapshot_json")]
    public string? ExecutionWindowSnapshotJson { get; set; }

    /// <summary>active | released | terminal。</summary>
    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "active";

    [MaxLength(160), Column("idempotency_key")]
    public string? IdempotencyKey { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [Column("released_at_utc")]
    public DateTimeOffset? ReleasedAtUtc { get; set; }
}
