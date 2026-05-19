using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

[Table("event_queue")]
public sealed class EventQueueEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("event_id")]
    public string EventId { get; set; } = string.Empty;

    [Column("priority")]
    public int Priority { get; set; }

    [Required, MaxLength(128), Column("event_type")]
    public string EventType { get; set; } = string.Empty;

    [MaxLength(64), Column("source_type")]
    public string? SourceType { get; set; }

    [MaxLength(256), Column("source_id")]
    public string? SourceId { get; set; }

    [MaxLength(64), Column("connector_id")]
    public string? ConnectorId { get; set; }

    [MaxLength(64), Column("session_id")]
    public string? SessionId { get; set; }

    [MaxLength(64), Column("workspace_id")]
    public string? WorkspaceId { get; set; }

    [MaxLength(64), Column("agent_id")]
    public string? AgentId { get; set; }

    [Required, Column("payload")]
    public string Payload { get; set; } = "{}";

    [Required, MaxLength(32), Column("status")]
    public string Status { get; set; } = "pending";

    [Column("retry_count")]
    public int RetryCount { get; set; }

    [Column("available_at")]
    public long AvailableAt { get; set; }

    [Column("lease_until")]
    public long? LeaseUntil { get; set; }

    [Column("started_at")]
    public long? StartedAt { get; set; }

    [Column("completed_at")]
    public long? CompletedAt { get; set; }

    [Column("created_at")]
    public long CreatedAt { get; set; }

    [Column("updated_at")]
    public long UpdatedAt { get; set; }

    [MaxLength(1024), Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [MaxLength(64), Column("causation_id")]
    public string? CausationId { get; set; }

    [MaxLength(64), Column("trace_id")]
    public string? TraceId { get; set; }

    [MaxLength(64), Column("correlation_id")]
    public string? CorrelationId { get; set; }

    [MaxLength(64), Column("execution_id")]
    public string? ExecutionId { get; set; }

    [MaxLength(64), Column("parent_execution_id")]
    public string? ParentExecutionId { get; set; }

    [MaxLength(64), Column("sub_agent_id")]
    public string? SubAgentId { get; set; }

    [MaxLength(128), Column("user_id")]
    public string? UserId { get; set; }
}
