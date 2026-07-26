using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>Materialized room transcript fact produced by the message fabric.</summary>
[Table("room_messages")]
public sealed class RoomMessageEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("message_id")]
    public string MessageId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = "default";

    [Required, MaxLength(64), Column("room_id")]
    public string RoomId { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("from_kind")]
    public string FromKind { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("from_id")]
    public string FromId { get; set; } = string.Empty;

    [MaxLength(256), Column("from_display_name")]
    public string? FromDisplayName { get; set; }

    [Required, MaxLength(32), Column("audience")]
    public string Audience { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("visibility")]
    public string Visibility { get; set; } = string.Empty;

    [Required, Column("content")]
    public string Content { get; set; } = string.Empty;

    [MaxLength(64), Column("conversation_id")]
    public string? ConversationId { get; set; }

    [MaxLength(128), Column("reply_to_message_id")]
    public string? ReplyToMessageId { get; set; }

    [MaxLength(128), Column("correlation_id")]
    public string? CorrelationId { get; set; }

    [MaxLength(128), Column("causation_id")]
    public string? CausationId { get; set; }

    [Column("metadata_json")]
    public string? MetadataJson { get; set; }

    [Column("created_at")]
    public long CreatedAt { get; set; }
}
