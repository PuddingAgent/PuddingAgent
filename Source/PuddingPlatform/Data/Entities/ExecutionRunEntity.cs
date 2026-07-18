using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-059: Execution Run 实体 — 每次执行尝试独立记录。
/// FencingToken 是 SQLite 自增主键，也是 Journal 校验的 fencing 值。
/// 一个 Command 可以有多个 ExecutionRun（重试场景）。
/// </summary>
[Table("execution_runs")]
public class ExecutionRunEntity
{
    /// <summary>DB 自增主键，既是 Run 行 ID 也是 fencing token。</summary>
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("fencing_token")]
    public long FencingToken { get; set; }

    [Required, MaxLength(64), Column("run_id")]
    public string RunId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("command_id")]
    public string CommandId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("conversation_id")]
    public string ConversationId { get; set; } = string.Empty;

    [MaxLength(64), Column("turn_id")]
    public string? TurnId { get; set; }

    [Column("attempt")]
    public int Attempt { get; set; }

    [MaxLength(64), Column("worker_id")]
    public string? WorkerId { get; set; }

    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "leased";

    [Column("lease_until")]
    public long? LeaseUntil { get; set; }

    [MaxLength(64), Column("snapshot_id")]
    public string? SnapshotId { get; set; }

    [Column("started_at")]
    public long? StartedAt { get; set; }

    [Column("completed_at")]
    public long? CompletedAt { get; set; }

    [Column("terminal_sequence")]
    public long? TerminalSequence { get; set; }
}
