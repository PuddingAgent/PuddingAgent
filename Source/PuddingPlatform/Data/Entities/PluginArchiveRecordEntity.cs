using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// One plugin archive/audit entry. <c>operation_id</c> is the host idempotency key (frozen observer contract uses
/// OperationId=EventId), so a redelivered event never creates a second logical archive record.
/// </summary>
[Table("plugin_archive_records")]
public sealed class PluginArchiveRecordEntity
{
    [Key] [Column("archive_id")] public string ArchiveId { get; set; } = "";
    [Column("operation_id")] public string OperationId { get; set; } = "";
    [Column("event_id")] public string EventId { get; set; } = "";
    [Column("workspace_id")] public string WorkspaceId { get; set; } = "";
    [Column("plugin_id")] public string PluginId { get; set; } = "";
    [Column("artifact_ref")] public string ArtifactRef { get; set; } = "";
    [Column("payload_hash")] public string PayloadHash { get; set; } = "";
    [Column("recorded_at")] public DateTimeOffset RecordedAt { get; set; }
}
