using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 聊天执行命令 — 只保存执行引用，配置由 Worker 动态装配。
/// ADR-058: payload_json, agent_template_id 已删除。
/// ADR-059: 增加 batch_id（受理批次关联）、run_id（执行时分配）。
/// </summary>
[Table("chat_execution_commands")]
public class ChatExecutionCommandEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("command_id")]
    public string CommandId { get; set; } = string.Empty;

    /// <summary>ADR-059: 受理批次 ID（幂等重放时关联）。</summary>
    [Required, MaxLength(64), Column("batch_id")]
    public string BatchId { get; set; } = string.Empty;

    [MaxLength(64), Column("client_request_id")]
    public string? ClientRequestId { get; set; }

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [MaxLength(64), Column("message_id")]
    public string MessageId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("user_message_id")]
    public string UserMessageId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("turn_id")]
    public string TurnId { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("agent_instance_id")]
    public string AgentInstanceId { get; set; } = string.Empty;

    [MaxLength(64), Column("user_id")]
    public string? UserId { get; set; }

    [MaxLength(32), Column("channel_id")]
    public string? ChannelId { get; set; }

    /// <summary>ADR-059: Worker 领取时分配的 run_id。</summary>
    [MaxLength(64), Column("run_id")]
    public string? RunId { get; set; }

    /// <summary>ADR-059: 终态事件的 sequence（CommitTerminal 时写入）。</summary>
    [Column("terminal_sequence")]
    public long? TerminalSequence { get; set; }

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
    public string? FenceToken { get; set; }
}
