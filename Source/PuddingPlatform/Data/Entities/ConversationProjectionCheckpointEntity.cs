using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-057: Projection checkpoint per conversation — tracks last projected event sequence.
/// </summary>
[Table("conversation_projection_checkpoints")]
public class ConversationProjectionCheckpointEntity
{
    [Key, Required, MaxLength(64), Column("conversation_id")]
    public string ConversationId { get; set; } = string.Empty;

    [Column("projected_through")]
    public long ProjectedThrough { get; set; }

    [Required, Column("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;
}
