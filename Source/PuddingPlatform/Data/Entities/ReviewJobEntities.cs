using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>PA-03 S5: durable review job. State is the review job's own state, never a Goal phase.</summary>
[Table("review_jobs")]
public sealed class ReviewJobEntity
{
    [Key] [Column("job_id")] public string JobId { get; set; } = "";
    [Column("review_id")] public string ReviewId { get; set; } = "";
    [Column("workspace_id")] public string WorkspaceId { get; set; } = "";
    [Column("parent_work_id")] public string? ParentWorkId { get; set; }
    [Column("goal_run_id")] public string? GoalRunId { get; set; }
    [Column("activation_epoch")] public long ActivationEpoch { get; set; }
    [Column("iteration_no")] public int IterationNo { get; set; }
    [Column("objective_version")] public string ObjectiveVersion { get; set; } = "";
    [Column("acceptance_contract_version")] public string AcceptanceContractVersion { get; set; } = "";
    [Column("artifact_set_hash")] public string ArtifactSetHash { get; set; } = "";
    [Column("review_policy_version")] public string ReviewPolicyVersion { get; set; } = "";
    [Column("required")] public bool Required { get; set; }
    [Column("budget_account_id")] public string BudgetAccountId { get; set; } = "";
    [Column("actor_id")] public string? ActorId { get; set; }
    [Column("deadline")] public DateTimeOffset Deadline { get; set; }
    [Column("accepted_at")] public DateTimeOffset AcceptedAt { get; set; }
    [Column("accepted_sequence")] public long AcceptedSequence { get; set; }
    [Column("state")] public int State { get; set; }
    [Column("revision")] public long Revision { get; set; }
    [Column("owner_epoch")] public long OwnerEpoch { get; set; }
    [Column("lease_until")] public long? LeaseUntilUnixMs { get; set; }
    [Column("outcome_finding")] public int? OutcomeFinding { get; set; }
    [Column("outcome_level")] public int? OutcomeLevel { get; set; }
    [Column("outcome_criteria_json")] public string? OutcomeCriteriaJson { get; set; }
    [Column("reason_code")] public string? ReasonCode { get; set; }
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>PA-03 S5: submit idempotency. One row per OperationId; reused with different content is a conflict.</summary>
[Table("review_job_operations")]
public sealed class ReviewJobOperationEntity
{
    [Key] [Column("operation_id")] public string OperationId { get; set; } = "";
    [Column("job_id")] public string JobId { get; set; } = "";
    [Column("review_id")] public string ReviewId { get; set; } = "";
}
