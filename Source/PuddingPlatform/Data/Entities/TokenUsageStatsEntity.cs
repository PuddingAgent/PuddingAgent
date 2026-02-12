using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// Token 使用统计聚合表（按月/按服务商/按模型聚合）。
/// 非事务源，由 ChatMessageEntity 持久化时 fire-and-forget 增量更新。
/// </summary>
public class TokenUsageStatsEntity
{
    [Key]
    public long Id { get; set; }

    /// <summary>Provider ID（slug）</summary>
    [Required, MaxLength(64)]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>模型 ID</summary>
    [Required, MaxLength(128)]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>统计月份，格式 yyyy-MM</summary>
    [Required, MaxLength(7)]
    public string YearMonth { get; set; } = string.Empty;

    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CacheHitTokens { get; set; }
    public long CacheMissTokens { get; set; }
    public long RequestCount { get; set; }
    public decimal TotalCost { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
