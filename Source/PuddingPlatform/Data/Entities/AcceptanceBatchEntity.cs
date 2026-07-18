using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-059: 受理批次 — 同一 clientRequestId 下所有 Turn/Command 的幂等容器。
/// 一个 POST = 一个 Batch = 1 条 Message + N 条 Turn + N 条 Command + N 个 turn.accepted Event。
/// </summary>
[Table("acceptance_batches")]
public class AcceptanceBatchEntity
{
    [Key]
    public long Id { get; set; }

    /// <summary>批次稳定 ID（UUID）。</summary>
    [Required, MaxLength(64), Column("batch_id")]
    public string BatchId { get; set; } = string.Empty;

    /// <summary>工作区 ID。</summary>
    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>前端幂等键，同一次提交所有重试复用。</summary>
    [Required, MaxLength(64), Column("client_request_id")]
    public string ClientRequestId { get; set; } = string.Empty;

    /// <summary>会话 ID。</summary>
    [Required, MaxLength(64), Column("conversation_id")]
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>用户消息 ID（稳定业务键）。</summary>
    [Required, MaxLength(64), Column("message_id")]
    public string MessageId { get; set; } = string.Empty;

    /// <summary>受理状态：accepted | rejected。</summary>
    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "accepted";

    /// <summary>该批次包含的 Turn 数量。</summary>
    [Column("turn_count")]
    public int TurnCount { get; set; }

    /// <summary>ADR-059: turn.accepted 事件的最大 sequence。幂等重放直接返回此值。</summary>
    [Column("accepted_sequence")]
    public long AcceptedSequence { get; set; }

    /// <summary>发起请求的用户 ID。</summary>
    [MaxLength(64), Column("user_id")]
    public string? UserId { get; set; }

    /// <summary>Unix 毫秒时间戳。</summary>
    [Column("created_at")]
    public long CreatedAt { get; set; }
}
