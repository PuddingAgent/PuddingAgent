using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 聊天消息实体 — ADR-058: 增加稳定业务字段 MessageId/TurnId/CommandId/UserId。
/// 用户消息在受理时原子创建，Worker 按 UserMessageId 引用加载。
/// </summary>
public class ChatMessageEntity
{
    [Key]
    public long Id { get; set; }

    /// <summary>稳定业务 ID（幂等键，前端 clientMessageId）。</summary>
    [Required, MaxLength(64), Column("message_id")]
    public string MessageId { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string WorkspaceId { get; set; } = string.Empty;

    public string AgentInstanceId { get; set; } = string.Empty;

    // ADR-058: 以下字段已迁移到 AgentTemplate (文件配置)，不再持久化。
    // 保留列但标记为已废弃，Worker 现在通过 IAgentRuntimeProfileResolver 解析。
    [Obsolete("AgentTemplateId 已迁移到 AgentTemplate 文件配置。")]
    public string AgentTemplateId { get; set; } = string.Empty;

    public string Role { get; set; } = "user";

    public string Content { get; set; } = string.Empty;

    public string? ThinkingJson { get; set; }

    public string? UsageJson { get; set; }

    [MaxLength(64), Column("turn_id")]
    public string? TurnId { get; set; }

    [MaxLength(64), Column("command_id")]
    public string? CommandId { get; set; }

    [MaxLength(64), Column("user_id")]
    public string? UserId { get; set; }

    [Column("metadata_json")]
    public string? MetadataJson { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; }
}
