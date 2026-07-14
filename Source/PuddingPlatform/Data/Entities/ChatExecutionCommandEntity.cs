using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 聊天执行命令 — 可靠受理、持久队列、租约恢复的事实源。
/// 
/// 关联 ADR：Docs/07架构/57ADR-056聊天消息受理与可靠事件流架构ADR.md §5 (ADR-056-B)
/// </summary>
[Table("chat_execution_commands")]
public class ChatExecutionCommandEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("command_id")]
    public string CommandId { get; set; } = string.Empty;

    [MaxLength(64), Column("client_request_id")]
    public string? ClientRequestId { get; set; }

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("message_id")]
    public string MessageId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("turn_id")]
    public string TurnId { get; set; } = string.Empty;

    [MaxLength(128), Column("agent_instance_id")]
    public string? AgentInstanceId { get; set; }

    [MaxLength(128), Column("agent_template_id")]
    public string? AgentTemplateId { get; set; }

    [MaxLength(64), Column("user_id")]
    public string? UserId { get; set; }

    [Required, Column("payload_json")]
    public string PayloadJson { get; set; } = string.Empty;

    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "pending";

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [MaxLength(64), Column("lease_owner")]
    public string? LeaseOwner { get; set; }

    [Column("lease_until")]
    public long? LeaseUntil { get; set; }

    [Column("created_at")]
    public long CreatedAt { get; set; }

    [Column("started_at")]
    public long? StartedAt { get; set; }

    [Column("completed_at")]
    public long? CompletedAt { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }

    [MaxLength(32), Column("event_cursor")]
    public string? EventCursor { get; set; }

    [MaxLength(64), Column("fence_token")]
    public string? FenceToken { get; set; }     // 每次 LeaseNext 生成唯一 token，用于完成/释放所有权校验
}
