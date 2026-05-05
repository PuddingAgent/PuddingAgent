using System.ComponentModel.DataAnnotations;

namespace PuddingMemoryEngine.Entities;

/// <summary>
/// 记忆实体。
/// 对应 ADR-013 的 Memories 表。
/// </summary>
public class MemoryEntity
{
    [Key]
    [MaxLength(32)]
    public string MemoryId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(16)]
    public string Scope { get; set; } = "session";

    [MaxLength(32)]
    public string? SessionId { get; set; }

    [MaxLength(64)]
    public string? WorkspaceId { get; set; }

    [MaxLength(64)]
    public string? AgentId { get; set; }

    [MaxLength(64)]
    public string Tag { get; set; } = "general";

    public string Content { get; set; } = string.Empty;

    public double Importance { get; set; } = 0.5;

    public double Confidence { get; set; } = 0.8;

    [MaxLength(32)]
    public string? SourceMessageId { get; set; }

    public int AccessCount { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long? LastAccessedAt { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long? ExpiresAt { get; set; }

    [MaxLength(32)]
    public string? SupersededBy { get; set; }

    public string? Metadata { get; set; }
}
