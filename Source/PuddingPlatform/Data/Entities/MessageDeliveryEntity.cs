using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>Per-endpoint delivery fact backing event delivery and pull-based inboxes.</summary>
[Table("message_deliveries")]
public sealed class MessageDeliveryEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("delivery_id")]
    public string DeliveryId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("message_id")]
    public string MessageId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = "default";

    [MaxLength(64), Column("room_id")]
    public string? RoomId { get; set; }

    [Required, MaxLength(32), Column("target_kind")]
    public string TargetKind { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("target_id")]
    public string TargetId { get; set; } = string.Empty;

    [MaxLength(256), Column("target_display_name")]
    public string? TargetDisplayName { get; set; }

    [Required, MaxLength(32), Column("status")]
    public string Status { get; set; } = "queued";

    [Column("priority")]
    public int Priority { get; set; }

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("available_at")]
    public long? AvailableAt { get; set; }

    [Column("lease_until")]
    public long? LeaseUntil { get; set; }

    [MaxLength(128), Column("claimed_by_execution_id")]
    public string? ClaimedByExecutionId { get; set; }

    [MaxLength(1024), Column("last_error")]
    public string? LastError { get; set; }

    [Column("created_at")]
    public long CreatedAt { get; set; }

    [Column("updated_at")]
    public long UpdatedAt { get; set; }

    [Column("read_at")]
    public long? ReadAt { get; set; }

    [Column("ack_at")]
    public long? AckAt { get; set; }
}
