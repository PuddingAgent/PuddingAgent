using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 会话读模型 · 会话级首屏状态（Plan《会话首屏与渐进加载架构优化方案》§3.2）。
/// <para>
/// 只保存首屏必需状态：当前根 Turn、压缩状态、必要子代理摘要（有界 JSON）、初始化状态与
/// readModelVersion。事件游标仍由 <c>conversation_projection_checkpoints</c> 作为唯一权威；
/// 本行与 checkpoint 在同一投影事务提交。
/// </para>
/// </summary>
[Table("conversation_read_state")]
public class ConversationReadStateEntity
{
    [Key, Required, MaxLength(64), Column("conversation_id")]
    public string ConversationId { get; set; } = string.Empty;

    [Column("initialization_state")]
    public int InitializationState { get; set; }

    [MaxLength(64), Column("current_turn_id")]
    public string? CurrentTurnId { get; set; }

    [MaxLength(64), Column("current_root_run_id")]
    public string? CurrentRootRunId { get; set; }

    [Column("current_root_terminal_state")]
    public int CurrentRootTerminalState { get; set; }

    [Column("compaction_running")]
    public bool CompactionRunning { get; set; }

    [MaxLength(64), Column("compaction_run_id")]
    public string? CompactionRunId { get; set; }

    [Column("compaction_started_sequence")]
    public long CompactionStartedSequence { get; set; }

    [Column("compaction_completed_sequence")]
    public long? CompactionCompletedSequence { get; set; }

    [Column("compaction_last_attempt_failed")]
    public bool CompactionLastAttemptFailed { get; set; }

    /// <summary>有界子代理摘要 JSON（≤ SubAgentSummaryLimit/32 KiB 合同），空数组默认。</summary>
    [Required, Column("sub_agent_runs_json")]
    public string SubAgentRunsJson { get; set; } = "[]";

    [Column("sub_agent_runs_omitted")]
    public int SubAgentRunsOmitted { get; set; }

    [Column("read_model_version")]
    public int ReadModelVersion { get; set; }

    [Column("revision")]
    public int Revision { get; set; }

    [Required, Column("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;
}
