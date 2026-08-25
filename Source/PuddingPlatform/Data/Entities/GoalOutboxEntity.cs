using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-074 §12.4: goal_outbox — continuation/verification 的持久工作意图。
/// G1 冻结 schema；G2 起 GoalContinuationWorker 按 due_at + lease claim，
/// 并在受理前重校验 Goal CAS、epoch 与 session admission。旧 epoch 只能 suppress。
/// </summary>
[Table("goal_outbox")]
public class GoalOutboxEntity
{
    [Key, Required, MaxLength(64), Column("outbox_id")]
    public string OutboxId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("goal_run_id")]
    public string GoalRunId { get; set; } = string.Empty;

    [Required, Column("activation_epoch")]
    public int ActivationEpoch { get; set; }

    [Required, Column("aggregate_version")]
    public int AggregateVersion { get; set; }

    /// <summary>continuation | verification | notification | retry。</summary>
    [Required, MaxLength(16), Column("kind")]
    public string Kind { get; set; } = "continuation";

    /// <summary>goalId + activationEpoch + iterationNumber 的确定性幂等键（唯一索引）。</summary>
    [Required, MaxLength(160), Column("idempotency_key")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Column("payload_json")]
    public string? PayloadJson { get; set; }

    /// <summary>pending | leased | completed | cancelled | dead_lettered。</summary>
    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "pending";

    [Required, Column("due_at_utc")]
    public DateTimeOffset DueAtUtc { get; set; }

    [MaxLength(64), Column("lease_owner")]
    public string? LeaseOwner { get; set; }

    [Column("lease_until_utc")]
    public DateTimeOffset? LeaseUntilUtc { get; set; }

    [Required, Column("fencing_token")]
    public long FencingToken { get; set; }

    [Required, Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [Column("completed_at_utc")]
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
