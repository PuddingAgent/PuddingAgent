using System.ComponentModel.DataAnnotations;

namespace PuddingMemoryEngine.Entities;

/// <summary>
/// 会话实体。
/// 对应 ADR-013 的 Sessions 主表。
/// </summary>
public class SessionEntity
{
    [Key]
    [MaxLength(32)]
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AgentId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Title { get; set; }

    [MaxLength(32)]
    public string Mode { get; set; } = "chat";

    [MaxLength(32)]
    public string Status { get; set; } = "active";

    /// <summary>JSON 数组字符串。</summary>
    public string? Tags { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long LastActivityAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public int MessageCount { get; set; }

    public long TokenTotal { get; set; }

    /// <summary>JSON 扩展字段。</summary>
    public string? Metadata { get; set; }

    public ICollection<MessageEntity> Messages { get; set; } = new List<MessageEntity>();
}
