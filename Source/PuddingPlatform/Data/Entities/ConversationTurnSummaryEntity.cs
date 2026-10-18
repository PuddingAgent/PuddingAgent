using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 会话读模型 · Turn 摘要（Plan《会话首屏与渐进加载架构优化方案》§3.2）。
/// <para>
/// 由投影工作者按 canonical 事件增量归约，与消息投影、checkpoint 在同一事务提交；
/// 只保存首屏需要的终态/计数/有界预览，不保存事件明细或完整历史正文。
/// </para>
/// <para>主键 <c>(conversation_id, turn_id)</c>；<c>read_model_version</c> 用于缓存/ETag 失效。</para>
/// </summary>
[Table("conversation_turn_summaries")]
public class ConversationTurnSummaryEntity
{
    [Required, MaxLength(64), Column("conversation_id")]
    public string ConversationId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("turn_id")]
    public string TurnId { get; set; } = string.Empty;

    [MaxLength(64), Column("root_run_id")]
    public string? RootRunId { get; set; }

    [MaxLength(64), Column("user_message_id")]
    public string? UserMessageId { get; set; }

    [MaxLength(64), Column("assistant_message_id")]
    public string? AssistantMessageId { get; set; }

    /// <summary><see cref="PuddingConversationReadModel.TurnTerminalState"/> 的持久化值。</summary>
    [Column("terminal_state")]
    public int TerminalState { get; set; }

    [Column("has_assistant_body")]
    public bool HasAssistantBody { get; set; }

    [Column("content_append_count")]
    public int ContentAppendCount { get; set; }

    [Column("thinking_append_count")]
    public int ThinkingAppendCount { get; set; }

    [Column("tool_call_requested_count")]
    public int ToolCallRequestedCount { get; set; }

    [Column("tool_call_completed_count")]
    public int ToolCallCompletedCount { get; set; }

    [Column("tool_call_failed_count")]
    public int ToolCallFailedCount { get; set; }

    [Column("sub_agent_run_count")]
    public int SubAgentRunCount { get; set; }

    /// <summary>活动正文有界 UTF-8 尾部预览（≤16 KiB），不是完整正文。</summary>
    [Column("active_preview")]
    public string ActivePreview { get; set; } = string.Empty;

    [Column("active_preview_truncated")]
    public bool ActivePreviewTruncated { get; set; }

    [Column("first_sequence")]
    public long FirstSequence { get; set; }

    [Column("last_sequence")]
    public long LastSequence { get; set; }

    [Column("summary_revision")]
    public int SummaryRevision { get; set; }

    /// <summary><see cref="PuddingConversationReadModel.SummaryCompleteness"/> 的持久化值。</summary>
    [Column("process_completeness")]
    public int ProcessCompleteness { get; set; }

    [MaxLength(64), Column("incomplete_reason")]
    public string? IncompleteReason { get; set; }

    [Column("read_model_version")]
    public int ReadModelVersion { get; set; }

    [Required, Column("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;
}
