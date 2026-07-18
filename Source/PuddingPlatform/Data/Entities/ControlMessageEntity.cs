using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-059: 持久化控制消息 — 统一 Cancel、Steering、Approval 等入口。
/// API Handler 写消息；Runtime 在 LLM/Tool 边界通过 IControlInbox 拉取。
/// </summary>
[Table("execution_control_messages")]
public class ControlMessageEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("control_id")]
    public string ControlId { get; set; } = string.Empty;

    [Column("sequence")]
    public long Sequence { get; set; }

    [Required, MaxLength(64), Column("conversation_id")]
    public string ConversationId { get; set; } = string.Empty;

    [MaxLength(64), Column("turn_id")]
    public string? TurnId { get; set; }

    [Required, MaxLength(32), Column("kind")]
    public string Kind { get; set; } = string.Empty;

    [Required, Column("payload")]
    public string Payload { get; set; } = string.Empty;

    [MaxLength(64), Column("source_user_id")]
    public string? SourceUserId { get; set; }

    [Column("priority")]
    public int Priority { get; set; }

    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "pending";

    [Column("consumed_at")]
    public long? ConsumedAt { get; set; }

    [MaxLength(64), Column("consumed_by_run_id")]
    public string? ConsumedByRunId { get; set; }

    [Column("created_at")]
    public long CreatedAt { get; set; }
}
