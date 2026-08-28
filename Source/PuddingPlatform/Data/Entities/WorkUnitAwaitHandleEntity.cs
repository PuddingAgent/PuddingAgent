using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>Durable wait registration for long-running WorkUnit dependencies.</summary>
[Table("work_unit_await_handles")]
public sealed class WorkUnitAwaitHandleEntity
{
    [Key, Required, MaxLength(64), Column("await_handle_id")]
    public string AwaitHandleId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("plan_id")]
    public string PlanId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("task_node_id")]
    public string TaskNodeId { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("kind")]
    public string Kind { get; set; } = string.Empty;

    [MaxLength(160), Column("external_id")]
    public string? ExternalId { get; set; }

    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "waiting";

    [Column("fencing_token")]
    public long FencingToken { get; set; }

    [Column("metadata_json")]
    public string? MetadataJson { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [Required, Column("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }

    [Column("signaled_at_utc")]
    public DateTimeOffset? SignaledAtUtc { get; set; }

    [Column("consumed_at_utc")]
    public DateTimeOffset? ConsumedAtUtc { get; set; }
}
