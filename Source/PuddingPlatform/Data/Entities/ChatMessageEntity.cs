using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 聊天消息实体——持久化管理员聊天消息到 PostgreSQL。
/// </summary>
public class ChatMessageEntity
{
    [Key]
    public long Id { get; set; }

    public string SessionId { get; set; } = string.Empty;

    public string WorkspaceId { get; set; } = string.Empty;

    public string AgentInstanceId { get; set; } = string.Empty;

    public string AgentTemplateId { get; set; } = string.Empty;

    public string Role { get; set; } = "user";

    public string Content { get; set; } = string.Empty;

    public string? ThinkingJson { get; set; }

    public string? UsageJson { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; }
}
