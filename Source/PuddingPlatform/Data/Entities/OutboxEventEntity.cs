using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>PA-07A S5 transactional outbox fact: inserted by the UoW inside the business transaction.</summary>
[Table("event_outbox")]
public sealed class OutboxEventEntity
{
    [Key] [Column("event_id")] public string EventId { get; set; } = "";
    [Column("stream_id")] public string StreamId { get; set; } = "";
    [Column("sequence")] public long Sequence { get; set; }
    [Column("kind")] public string Kind { get; set; } = "";
    [Column("schema_version")] public int SchemaVersion { get; set; }
    [Column("occurred_at")] public DateTimeOffset OccurredAt { get; set; }
    [Column("occurred_at_ms")] public long OccurredAtUnixMs { get; set; }
    [Column("workspace_id")] public string WorkspaceId { get; set; } = "";
    [Column("payload_ref")] public string PayloadRef { get; set; } = "";
    [Column("payload_hash")] public string PayloadHash { get; set; } = "";
    [Column("producer")] public string Producer { get; set; } = "";
    [Column("causation_id")] public string? CausationId { get; set; }
    [Column("correlation_id")] public string? CorrelationId { get; set; }
    [Column("business_ref")] public string? BusinessRef { get; set; }
    [Column("published")] public bool Published { get; set; }
}
