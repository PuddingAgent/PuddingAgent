using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-057: Conversation Event Log 实体 — append-only。
/// </summary>
[Table("conversation_events")]
public class ConversationEventEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("conversation_id")]
    public string ConversationId { get; set; } = string.Empty;

    [Column("sequence")]
    public long Sequence { get; set; }

    [Required, MaxLength(64), Column("event_id")]
    public string EventId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("turn_id")]
    public string TurnId { get; set; } = string.Empty;

    [MaxLength(64), Column("command_id")]
    public string? CommandId { get; set; }

    [MaxLength(64), Column("run_id")]
    public string? RunId { get; set; }

    [MaxLength(64), Column("message_id")]
    public string? MessageId { get; set; }

    [Required, MaxLength(64), Column("type")]
    public string Type { get; set; } = string.Empty;

    [Column("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [Required, Column("payload")]
    public string Payload { get; set; } = string.Empty;

    [Required, Column("occurred_at")]
    public string OccurredAt { get; set; } = string.Empty;

    [Required, Column("committed_at")]
    public string CommittedAt { get; set; } = string.Empty;

    [MaxLength(64), Column("correlation_id")]
    public string? CorrelationId { get; set; }

    [MaxLength(64), Column("causation_id")]
    public string? CausationId { get; set; }

    [MaxLength(64), Column("producer_event_id")]
    public string? ProducerEventId { get; set; }
}
