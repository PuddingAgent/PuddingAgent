using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 按日缓存完成标记：某 cache_key 的某 UTC 日已完整聚合（含零数据日）。
/// 标记存在即代表该日不需要再扫描事件表；删除标记即强制重算。
/// </summary>
[Table("stats_daily_cache_days")]
public sealed class StatsDailyCacheDayEntity
{
    [Required, MaxLength(64), Column("cache_key")]
    public string CacheKey { get; set; } = string.Empty;

    [Required, MaxLength(10), Column("day_utc")]
    public string DayUtc { get; set; } = string.Empty;

    [Required, Column("built_at_utc")]
    public DateTimeOffset BuiltAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
