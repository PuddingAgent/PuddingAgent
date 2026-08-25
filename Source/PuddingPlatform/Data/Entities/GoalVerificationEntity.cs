using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-074 §12.3: goal_verifications — 只读 Verifier 的输入摘要、verdict 与证据引用。
/// G1 冻结 schema；G3 起写入。Unique (goal_run_id, activation_epoch, source_turn_id, contract_version)。
/// </summary>
[Table("goal_verifications")]
public class GoalVerificationEntity
{
    [Key, Required, MaxLength(64), Column("verification_id")]
    public string VerificationId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("goal_run_id")]
    public string GoalRunId { get; set; } = string.Empty;

    [Required, Column("activation_epoch")]
    public int ActivationEpoch { get; set; }

    [Required, Column("iteration_no")]
    public int IterationNo { get; set; }

    [MaxLength(64), Column("source_turn_id")]
    public string? SourceTurnId { get; set; }

    [Column("source_terminal_sequence")]
    public long? SourceTerminalSequence { get; set; }

    [Required, Column("contract_version")]
    public int ContractVersion { get; set; } = 1;

    [Column("route_snapshot_json")]
    public string? RouteSnapshotJson { get; set; }

    /// <summary>pending | running | succeeded | failed。</summary>
    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "pending";

    /// <summary>continue | complete | blocked | needs_user | unsafe（verdict 只是建议）。</summary>
    [MaxLength(16), Column("verdict")]
    public string? Verdict { get; set; }

    [Column("summary")]
    public string? Summary { get; set; }

    [Column("unmet_criteria_json")]
    public string? UnmetCriteriaJson { get; set; }

    [Column("next_action")]
    public string? NextAction { get; set; }

    [MaxLength(64), Column("blocker_code")]
    public string? BlockerCode { get; set; }

    [Column("blocker_message")]
    public string? BlockerMessage { get; set; }

    [Column("evidence_refs_json")]
    public string? EvidenceRefsJson { get; set; }

    [MaxLength(256), Column("raw_output_artifact_ref")]
    public string? RawOutputArtifactRef { get; set; }

    [MaxLength(128), Column("raw_output_sha256")]
    public string? RawOutputSha256 { get; set; }

    [Required, Column("input_tokens")]
    public long InputTokens { get; set; }

    [Required, Column("output_tokens")]
    public long OutputTokens { get; set; }

    [Required, Column("cost")]
    public decimal Cost { get; set; }

    [MaxLength(64), Column("error_id")]
    public string? ErrorId { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [Column("completed_at_utc")]
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
