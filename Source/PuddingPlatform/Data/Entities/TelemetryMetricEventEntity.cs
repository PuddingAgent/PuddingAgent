using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 结构化遥测事实表，保存可聚合的会话、LLM、工具、缓存和性能指标。
/// </summary>
[Table("telemetry_metric_events")]
public sealed class TelemetryMetricEventEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("metric_id")]
    public string MetricId { get; set; } = string.Empty;

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

    [Required, MaxLength(64), Column("source")]
    public string Source { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("category")]
    public string Category { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("name")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(32), Column("status")]
    public string? Status { get; set; }

    [Required, MaxLength(40), Column("occurred_at_utc")]
    public string OccurredAtUtc { get; set; } = string.Empty;

    [Column("duration_ms")]
    public long? DurationMs { get; set; }

    [Column("count_value")]
    public long? CountValue { get; set; }

    [Column("numeric_value")]
    public double? NumericValue { get; set; }

    [MaxLength(32), Column("unit")]
    public string? Unit { get; set; }

    [Required, MaxLength(16), Column("severity")]
    public string Severity { get; set; } = "info";

    [MaxLength(512), Column("summary")]
    public string? Summary { get; set; }

    [Column("dimensions_json")]
    public string? DimensionsJson { get; set; }

    [Column("debug_json")]
    public string? DebugJson { get; set; }

    [MaxLength(128), Column("error_code")]
    public string? ErrorCode { get; set; }

    [MaxLength(512), Column("error_message")]
    public string? ErrorMessage { get; set; }
}
