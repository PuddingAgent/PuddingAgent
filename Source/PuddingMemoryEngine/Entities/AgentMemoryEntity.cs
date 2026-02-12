using System.ComponentModel.DataAnnotations;

namespace PuddingMemoryEngine.Entities;

/// <summary>
/// Agent 实例级工作记忆实体。
/// 与通用持久记忆表 MemoryEntity 分离，专门承载 long_term/daily/session 三类 Agent 记忆。
/// </summary>
public class AgentMemoryEntity
{
    [Key]
    public long Id { get; set; }

    [MaxLength(64)]
    public string MemoryId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string AgentInstanceId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string MemoryType { get; set; } = "long_term";

    public string Content { get; set; } = string.Empty;

    [MaxLength(10)]
    public string? DateKey { get; set; }

    public int ImportanceScore { get; set; } = 50;

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long AccessedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}