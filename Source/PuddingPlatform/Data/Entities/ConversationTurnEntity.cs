using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-059: Conversation Turn 实体 — Turn 与 Command/ExecutionRun 分离。
/// 每个 Turn 恰好一个终态；Turn.AcceptedSequence 和 Turn.TerminalSequence 构成事件窗口。
/// </summary>
[Table("conversation_turns")]
public class ConversationTurnEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("conversation_id")]
    public string ConversationId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("turn_id")]
    public string TurnId { get; set; } = string.Empty;

    [MaxLength(64), Column("command_id")]
    public string? CommandId { get; set; }

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(64), Column("user_id")]
    public string? UserId { get; set; }

    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "accepted";

    [Column("accepted_sequence")]
    public long AcceptedSequence { get; set; }

    [Column("terminal_sequence")]
    public long? TerminalSequence { get; set; }

    [MaxLength(16), Column("terminal_kind")]
    public string? TerminalKind { get; set; }

    [Column("created_at")]
    public long CreatedAt { get; set; }

    [Column("completed_at")]
    public long? CompletedAt { get; set; }
}
