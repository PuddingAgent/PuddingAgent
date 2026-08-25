using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-074 §12.2: goal_iterations — 每个 accepted Goal Iteration 的事实行。
/// G1 冻结 schema；G2 durable outbox 续行起写入。
/// 物理 Turn IDs 是事实引用，不允许从 Goal ID 或 Session ID 反推。
/// </summary>
[Table("goal_iterations")]
public class GoalIterationEntity
{
    [Key, Required, MaxLength(64), Column("goal_iteration_id")]
    public string GoalIterationId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("goal_run_id")]
    public string GoalRunId { get; set; } = string.Empty;

    [Required, Column("activation_epoch")]
    public int ActivationEpoch { get; set; }

    [Required, Column("iteration_no")]
    public int IterationNo { get; set; }

    /// <summary>accepted | running | settled | cancelled | failed（字符串状态，ADR-074 §12.2）。</summary>
    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "accepted";

    [MaxLength(64), Column("command_id")]
    public string? CommandId { get; set; }

    [MaxLength(64), Column("turn_id")]
    public string? TurnId { get; set; }

    [MaxLength(64), Column("run_id")]
    public string? RunId { get; set; }

    [MaxLength(64), Column("trace_id")]
    public string? TraceId { get; set; }

    [Column("accepted_sequence")]
    public long? AcceptedSequence { get; set; }

    [Column("terminal_sequence")]
    public long? TerminalSequence { get; set; }

    [MaxLength(64), Column("stop_reason")]
    public string? StopReason { get; set; }

    [MaxLength(64), Column("error_id")]
    public string? ErrorId { get; set; }

    [Column("started_at_utc")]
    public DateTimeOffset? StartedAtUtc { get; set; }

    [Column("settled_at_utc")]
    public DateTimeOffset? SettledAtUtc { get; set; }

    [Required, Column("llm_rounds")]
    public int LlmRounds { get; set; }

    [Required, Column("tool_calls")]
    public int ToolCalls { get; set; }

    [Required, Column("input_tokens")]
    public long InputTokens { get; set; }

    [Required, Column("output_tokens")]
    public long OutputTokens { get; set; }

    [MaxLength(128), Column("progress_fingerprint")]
    public string? ProgressFingerprint { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }
}
