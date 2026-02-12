using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 消息话题索引：存储以 # 开头的用户消息的话题标题。
/// 用于话题搜索和消息引用。
/// </summary>
[Table("message_topics")]
public class MessageTopicEntity
{
    [Key]
    public long Id { get; set; }

    /// <summary>关联 ChatMessages.Id。</summary>
    [Column("message_id")]
    public long MessageId { get; set; }

    /// <summary>去掉 # 前缀的话题标题。</summary>
    [Required, MaxLength(256), Column("topic_title")]
    public string TopicTitle { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>ISO 8601 时间戳。</summary>
    [Required, MaxLength(32), Column("created_at")]
    public string CreatedAt { get; set; } = string.Empty;
}
