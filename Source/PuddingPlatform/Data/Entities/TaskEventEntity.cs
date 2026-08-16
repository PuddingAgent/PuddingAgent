using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PuddingCode.Tasks;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// TB-02: task_events 表实体 — 对应 PuddingCode.Tasks.TaskEvent。
/// 附加 long Id 自增主键；枚举存 int、时间存 DateTimeOffset、列名 snake_case。
/// </summary>
[Table("task_events")]
public class TaskEventEntity
{
    [Key, Column("id")]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("event_id")]
    public string EventId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, Column("sequence")]
    public long Sequence { get; set; }

    [Required, Column("event_type")]
    public TaskEventType EventType { get; set; }

    [MaxLength(64), Column("assignment_id")]
    public string? AssignmentId { get; set; }

    [MaxLength(64), Column("agent_id")]
    public string? AgentId { get; set; }

    [MaxLength(64), Column("delivery_id")]
    public string? DeliveryId { get; set; }

    [MaxLength(64), Column("execution_id")]
    public string? ExecutionId { get; set; }

    [MaxLength(64), Column("session_id")]
    public string? SessionId { get; set; }

    [Column("origin")]
    public TaskOrigin? Origin { get; set; }

    [Column("priority")]
    public TaskPriority? Priority { get; set; }

    [Column("decision_code")]
    public string? DecisionCode { get; set; }

    [Column("next_eligible_at_utc")]
    public DateTimeOffset? NextEligibleAtUtc { get; set; }

    [MaxLength(64), Column("trace_id")]
    public string? TraceId { get; set; }

    [MaxLength(64), Column("correlation_id")]
    public string? CorrelationId { get; set; }

    [MaxLength(64), Column("causation_id")]
    public string? CausationId { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }
}
