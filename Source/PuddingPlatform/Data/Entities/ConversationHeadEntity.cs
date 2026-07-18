using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-057: Conversation head — tracks the highest committed sequence per conversation.
/// </summary>
[Table("conversation_heads")]
public class ConversationHeadEntity
{
    [Key, Required, MaxLength(64), Column("conversation_id")]
    public string ConversationId { get; set; } = string.Empty;

    [Column("head_sequence")]
    public long HeadSequence { get; set; }
}
