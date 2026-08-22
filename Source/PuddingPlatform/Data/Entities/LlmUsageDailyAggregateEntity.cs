using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

public static class LlmUsageAggregateSources
{
    /// <summary>来自 llm_gateway_usage_events（本地计费口径）。</summary>
    public const string Gateway = "gateway";

    /// <summary>来自 TokenUsageEvents（会话归因投影）。</summary>
    public const string Legacy = "legacy";
}

/// <summary>
/// 已结束 UTC 日的 Token 用量聚合行（按 day × source × provider × model 不可变缓存）。
/// 由 TokenUsageDailyAggregateService 在闭日首次被查询时构建；stats 页面读取这些行，
/// 不再每次打开都全量扫描 llm_gateway_usage_events / TokenUsageEvents。
/// 账本重建（TokenUsageRebuildService）后按月删除重算。
/// </summary>
[Table("llm_usage_daily_aggregates")]
public sealed class LlmUsageDailyAggregateEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(10), Column("day_utc")]
    public string DayUtc { get; set; } = string.Empty;

    [Required, MaxLength(7), Column("year_month")]
    public string YearMonth { get; set; } = string.Empty;

    [Required, MaxLength(16), Column("source")]
    public string Source { get; set; } = LlmUsageAggregateSources.Gateway;

    [Required, MaxLength(64), Column("provider_id")]
    public string ProviderId { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [Column("prompt_tokens")]
    public long PromptTokens { get; set; }

    [Column("completion_tokens")]
    public long CompletionTokens { get; set; }

    [Column("cache_hit_tokens")]
    public long CacheHitTokens { get; set; }

    [Column("cache_miss_tokens")]
    public long CacheMissTokens { get; set; }

    [Column("request_count")]
    public long RequestCount { get; set; }

    [Column("input_cost", TypeName = "decimal(18,10)")]
    public decimal InputCost { get; set; }

    [Column("cache_hit_cost", TypeName = "decimal(18,10)")]
    public decimal CacheHitCost { get; set; }

    [Column("output_cost", TypeName = "decimal(18,10)")]
    public decimal OutputCost { get; set; }

    [Column("total_cost", TypeName = "decimal(18,10)")]
    public decimal TotalCost { get; set; }

    [Required, Column("built_at_utc")]
    public DateTimeOffset BuiltAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
