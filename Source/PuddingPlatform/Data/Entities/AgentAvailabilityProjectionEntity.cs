using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PuddingCode.Scheduling;

namespace PuddingPlatform.Data.Entities;

[Table("agent_availability_projection")]
public sealed class AgentAvailabilityProjectionEntity
{
    [Key, Column("id")]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("agent_id")]
    public string AgentId { get; set; } = string.Empty;

    [Required, Column("state")]
    public AgentAvailabilityState State { get; set; }

    [Required, Column("activity_reason")]
    public AgentActivityReason ActivityReason { get; set; }

    [Required, Column("version")]
    public long Version { get; set; }

    [Required, Column("observed_at_utc")]
    public DateTimeOffset ObservedAtUtc { get; set; }

    [Required, Column("valid_until_utc")]
    public DateTimeOffset ValidUntilUtc { get; set; }

    [Column("idle_since_utc")]
    public DateTimeOffset? IdleSinceUtc { get; set; }

    [MaxLength(64), Column("main_conversation_id")]
    public string? MainConversationId { get; set; }

    [MaxLength(64), Column("active_turn_id")]
    public string? ActiveTurnId { get; set; }

    [MaxLength(64), Column("active_execution_id")]
    public string? ActiveExecutionId { get; set; }

    [MaxLength(64), Column("active_task_id")]
    public string? ActiveTaskId { get; set; }

    [MaxLength(64), Column("active_goal_run_id")]
    public string? ActiveGoalRunId { get; set; }

    [MaxLength(64), Column("active_sub_agent_run_id")]
    public string? ActiveSubAgentRunId { get; set; }

    [MaxLength(64), Column("reservation_id")]
    public string? ReservationId { get; set; }

    [Column("cooldown_until_utc")]
    public DateTimeOffset? CooldownUntilUtc { get; set; }

    [Required, MaxLength(64), Column("reason_code")]
    public string ReasonCode { get; set; } = "availability_unknown";
}

