using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// Assignment Attempt 状态（task_assignment_attempts.status，枚举存 int）。
/// 与 TB-01 冻结的 <c>PuddingCode.Tasks.AssignmentStatus</c>（Assigned/Accepted/Completed/Rejected）
/// 严格区分：这是 TB-03 建表所用的 Attempt 状态全集。
/// TB-03 只负责创建 <see cref="Reserved"/>；Assigned/InProgress/Completed/Failed/Rejected/Cancelled
/// 由 TB-05/TB-06（Assignment 打通 + task_* 工具）推进。wire 值待定，未知值 fail-closed。
/// </summary>
public enum AssignmentAttemptStatus
{
    /// <summary>已保留（调度器短暂持久所有权）。</summary>
    Reserved,

    /// <summary>已分配。</summary>
    Assigned,

    /// <summary>进行中。</summary>
    InProgress,

    /// <summary>已完成（终态）。</summary>
    Completed,

    /// <summary>已失败（终态）。</summary>
    Failed,

    /// <summary>已拒绝（终态）。</summary>
    Rejected,

    /// <summary>已取消（终态）。</summary>
    Cancelled
}

/// <summary>
/// TB-03: task_assignment_attempts 表实体 — Assign/RunNow 的 Assignment 记录。
/// 枚举存 int、时间存 DateTimeOffset、列名 snake_case（遵循 TB-02 约定）。
/// partial unique index：(task_id) WHERE released_at_utc IS NULL（保证每 task 最多一个 active assignment，ADR-072 §17）。
/// </summary>
[Table("task_assignment_attempts")]
public class TaskAssignmentAttemptEntity
{
    [Key, Required, MaxLength(64), Column("attempt_id")]
    public string AttemptId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("agent_id")]
    public string AgentId { get; set; } = string.Empty;

    [Required, Column("attempt_number")]
    public int AttemptNumber { get; set; } = 1;

    [Required, Column("status")]
    public AssignmentAttemptStatus Status { get; set; }

    /// <summary>RunNow 的 windowDecision 记录（仅记录，不判定）。</summary>
    [Column("window_decision")]
    public string? WindowDecision { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [Required, Column("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>标记 active 的时间（Reserved 阶段为 null，Assigned/InProgress 时置值）。</summary>
    [Column("active_at_utc")]
    public DateTimeOffset? ActiveAtUtc { get; set; }

    /// <summary>释放/失效时间（null 表示仍是 active，受 partial unique index 约束）。</summary>
    [Column("released_at_utc")]
    public DateTimeOffset? ReleasedAtUtc { get; set; }
}
