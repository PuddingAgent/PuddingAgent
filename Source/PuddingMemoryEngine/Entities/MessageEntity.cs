using System.ComponentModel.DataAnnotations;

namespace PuddingMemoryEngine.Entities;

/// <summary>
/// 消息实体。
/// 对应 ADR-013 的 Messages 主表，支持父子链路、分支与压缩引用。
/// </summary>
public class MessageEntity
{
    [Key]
    [MaxLength(32)]
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(32)]
    public string SessionId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? ParentId { get; set; }

    [MaxLength(32)]
    public string BranchType { get; set; } = "MAIN";

    public long Sequence { get; set; }

    [MaxLength(32)]
    public string Role { get; set; } = "user";

    [MaxLength(32)]
    public string ContentType { get; set; } = "text";

    public string? Content { get; set; }

    public string? ToolCallsJson { get; set; }

    public string? ToolResultJson { get; set; }

    public string? ThinkingJson { get; set; }

    public string? AttachmentsJson { get; set; }

    public string? UsageJson { get; set; }

    [MaxLength(128)]
    public string? ModelId { get; set; }

    [MaxLength(64)]
    public string? AgentId { get; set; }

    [MaxLength(64)]
    public string? Source { get; set; }

    [MaxLength(32)]
    public string? CompactedBy { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public string? Metadata { get; set; }

    // Navigation
    public SessionEntity? Session { get; set; }

    public MessageEntity? Parent { get; set; }

    public MessageEntity? CompactingMessage { get; set; }

    public ICollection<MessageEntity> Children { get; set; } = new List<MessageEntity>();
}
