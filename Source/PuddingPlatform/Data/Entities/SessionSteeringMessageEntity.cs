using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// Pending user guidance that should be injected into the next LLM request for a running session.
/// </summary>
[Table("session_steering_messages")]
public sealed class SessionSteeringMessageEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("steering_id")]
    public string SteeringId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [MaxLength(64), Column("agent_id")]
    public string? AgentId { get; set; }

    [MaxLength(64), Column("source_queue_item_id")]
    public string? SourceQueueItemId { get; set; }

    [Required, Column("message_text")]
    public string MessageText { get; set; } = string.Empty;

    [Column("priority")]
    public int Priority { get; set; } = 100;

    [Required, MaxLength(32), Column("status")]
    public string Status { get; set; } = SessionSteeringStatuses.Pending;

    [MaxLength(128), Column("created_by")]
    public string? CreatedBy { get; set; }

    [Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [Column("consumed_at_utc")]
    public DateTimeOffset? ConsumedAtUtc { get; set; }

    [Column("consumed_round")]
    public int? ConsumedRound { get; set; }

    [Column("expired_at_utc")]
    public DateTimeOffset? ExpiredAtUtc { get; set; }
}

public static class SessionSteeringStatuses
{
    public const string Pending = "pending";
    public const string Consumed = "consumed";
    public const string Expired = "expired";
}
