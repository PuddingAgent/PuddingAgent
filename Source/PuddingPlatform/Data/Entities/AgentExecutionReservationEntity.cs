using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

[Table("agent_execution_reservations")]
public sealed class AgentExecutionReservationEntity
{
    /// <summary>Monotonic SQLite identity; also the fencing token.</summary>
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity), Column("fencing_token")]
    public long FencingToken { get; set; }

    [Required, MaxLength(64), Column("reservation_id")]
    public string ReservationId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("agent_id")]
    public string AgentId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [MaxLength(64), Column("goal_run_id")]
    public string? GoalRunId { get; set; }

    [Required, MaxLength(64), Column("owner_id")]
    public string OwnerId { get; set; } = string.Empty;

    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "active";

    [Required, Column("lease_until_utc")]
    public DateTimeOffset LeaseUntilUtc { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [Required, Column("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }

    [Column("released_at_utc")]
    public DateTimeOffset? ReleasedAtUtc { get; set; }

    [MaxLength(64), Column("release_reason")]
    public string? ReleaseReason { get; set; }
}

