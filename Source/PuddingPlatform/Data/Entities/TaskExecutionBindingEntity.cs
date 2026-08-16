using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// TB-05: task_execution_bindings 表实体 — 关联 Task/Assignment/Delivery/Execution。
/// <para>
/// UNIQUE(task_id, assignment_id, delivery_id)（不变量 #8：按 idempotency key 找回同一
/// Delivery 时绑定不重复）；execution_id/session_id 由 TB-06 补写。
/// </para>
/// </summary>
[Table("task_execution_bindings")]
public sealed class TaskExecutionBindingEntity
{
    [Key, Column("id")]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("assignment_id")]
    public string AssignmentId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("delivery_id")]
    public string DeliveryId { get; set; } = string.Empty;

    [MaxLength(64), Column("execution_id")]
    public string? ExecutionId { get; set; }

    [MaxLength(64), Column("session_id")]
    public string? SessionId { get; set; }

    [Required, Column("bound_at_utc")]
    public DateTimeOffset BoundAtUtc { get; set; }
}
