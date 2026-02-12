using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

[Table("runtime_activity")]
public sealed class RuntimeActivityEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("activity_id")]
    public string ActivityId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("trace_id")]
    public string TraceId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("correlation_id")]
    public string CorrelationId { get; set; } = string.Empty;

    [MaxLength(64), Column("session_id")]
    public string? SessionId { get; set; }

    [MaxLength(64), Column("workspace_id")]
    public string? WorkspaceId { get; set; }

    [MaxLength(64), Column("execution_id")]
    public string? ExecutionId { get; set; }

    [MaxLength(64), Column("parent_execution_id")]
    public string? ParentExecutionId { get; set; }

    [MaxLength(64), Column("sub_agent_id")]
    public string? SubAgentId { get; set; }

    [MaxLength(64), Column("event_id")]
    public string? EventId { get; set; }

    [MaxLength(64), Column("connector_id")]
    public string? ConnectorId { get; set; }

    [MaxLength(128), Column("user_id")]
    public string? UserId { get; set; }

    [Required, MaxLength(64), Column("component")]
    public string Component { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("operation")]
    public string Operation { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("status")]
    public string Status { get; set; } = string.Empty;

    [Required, MaxLength(40), Column("started_at_utc")]
    public string StartedAtUtc { get; set; } = string.Empty;

    [MaxLength(40), Column("ended_at_utc")]
    public string? EndedAtUtc { get; set; }

    [Column("duration_ms")]
    public long? DurationMs { get; set; }

    [Required, MaxLength(16), Column("severity")]
    public string Severity { get; set; } = "info";

    [MaxLength(512), Column("summary")]
    public string? Summary { get; set; }

    [Column("metadata_json")]
    public string? MetadataJson { get; set; }

    [MaxLength(128), Column("error_code")]
    public string? ErrorCode { get; set; }

    [MaxLength(512), Column("error_message")]
    public string? ErrorMessage { get; set; }
}
