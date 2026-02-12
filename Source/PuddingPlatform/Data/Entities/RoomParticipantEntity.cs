using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>Persisted room participant membership for users, agents, connectors, and system endpoints.</summary>
[Table("room_participants")]
public sealed class RoomParticipantEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(128), Column("participant_id")]
    public string ParticipantId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = "default";

    [Required, MaxLength(64), Column("room_id")]
    public string RoomId { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("kind")]
    public string Kind { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("endpoint_id")]
    public string EndpointId { get; set; } = string.Empty;

    [MaxLength(256), Column("display_name")]
    public string? DisplayName { get; set; }

    [MaxLength(1024), Column("avatar_url")]
    public string? AvatarUrl { get; set; }

    [Column("can_send")]
    public bool CanSend { get; set; } = true;

    [Column("can_receive")]
    public bool CanReceive { get; set; } = true;

    [Required, MaxLength(32), Column("status")]
    public string Status { get; set; } = "available";

    [Column("created_at")]
    public long CreatedAt { get; set; }

    [Column("updated_at")]
    public long UpdatedAt { get; set; }
}
