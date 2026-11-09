using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>PA-03 S5/07A: completed review fact in the durable outbox. Delivery is at least once; the event id is stable.</summary>
[Table("review_events")]
public sealed class ReviewEventEntity
{
    [Key] [Column("event_id")] public string EventId { get; set; } = "";
    [Column("review_id")] public string ReviewId { get; set; } = "";
    [Column("job_id")] public string JobId { get; set; } = "";
    [Column("workspace_id")] public string WorkspaceId { get; set; } = "";
    [Column("parent_work_id")] public string? ParentWorkId { get; set; }
    [Column("goal_run_id")] public string? GoalRunId { get; set; }
    [Column("activation_epoch")] public long ActivationEpoch { get; set; }
    [Column("iteration_no")] public int IterationNo { get; set; }
    [Column("objective_version")] public string ObjectiveVersion { get; set; } = "";
    [Column("acceptance_contract_version")] public string AcceptanceContractVersion { get; set; } = "";
    [Column("artifact_set_hash")] public string ArtifactSetHash { get; set; } = "";
    [Column("review_policy_version")] public string ReviewPolicyVersion { get; set; } = "";
    [Column("finding")] public int Finding { get; set; }
    [Column("level")] public int Level { get; set; }
    [Column("revision")] public long Revision { get; set; }
    [Column("occurred_at")] public DateTimeOffset OccurredAt { get; set; }
    [Column("occurred_at_ms")] public long OccurredAtUnixMs { get; set; }
    [Column("published")] public bool Published { get; set; }
    [Column("published_at")] public DateTimeOffset? PublishedAt { get; set; }
}
