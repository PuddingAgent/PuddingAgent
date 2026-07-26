using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// Durable cursor and external-resource state for a connector streaming reply.
/// Conversation events remain the content facts; this row only records projection state.
/// </summary>
[Table("connector_stream_projections")]
public sealed class ConnectorStreamProjectionEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("projection_id")]
    public string ProjectionId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("command_id")]
    public string CommandId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("conversation_id")]
    public string ConversationId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("message_id")]
    public string MessageId { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("connector_id")]
    public string ConnectorId { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("external_conversation_id")]
    public string ExternalConversationId { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("external_message_id")]
    public string ExternalMessageId { get; set; } = string.Empty;

    [MaxLength(128), Column("external_resource_id")]
    public string? ExternalResourceId { get; set; }

    [MaxLength(128), Column("external_reply_id")]
    public string? ExternalReplyId { get; set; }

    [Required, MaxLength(64), Column("element_id")]
    public string ElementId { get; set; } = string.Empty;

    [Required, MaxLength(24), Column("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>Highest connector operation sequence reserved for this resource.</summary>
    [Column("operation_sequence")]
    public int OperationSequence { get; set; }

    /// <summary>Highest Conversation event sequence confirmed at the connector.</summary>
    [Column("last_event_sequence")]
    public long LastEventSequence { get; set; }

    /// <summary>
    /// Event cursor reserved by an in-flight update. Retrying uses the same connector
    /// sequence, UUID, and content snapshot.
    /// </summary>
    [Column("pending_event_sequence")]
    public long? PendingEventSequence { get; set; }

    [Required, Column("content")]
    public string Content { get; set; } = string.Empty;

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("available_at")]
    public long? AvailableAt { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }

    [Column("created_at")]
    public long CreatedAt { get; set; }

    [Column("updated_at")]
    public long UpdatedAt { get; set; }
}
