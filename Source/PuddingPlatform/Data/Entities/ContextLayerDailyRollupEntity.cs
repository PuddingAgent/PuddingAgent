using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 已结束 UTC 日的上下文层级分析 rollup（按 day × layer × provider × model 缓存）。
/// payload_json 保存精确合并所需的加法标量 + 分布数组（token 数、缓存命中率）+ 去重哈希集合，
/// 因此月中位数 / P95 / distinctHashes 可以跨日精确还原，stats 页面不再整月加载
/// context_layer_metric_events 明细。今天的数据始终实时计算，不落此表。
/// </summary>
[Table("context_layer_daily_rollups")]
public sealed class ContextLayerDailyRollupEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(10), Column("day_utc")]
    public string DayUtc { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("layer_name")]
    public string LayerName { get; set; } = string.Empty;

    [Column("layer_order")]
    public int LayerOrder { get; set; }

    [Required, MaxLength(64), Column("layer_role")]
    public string LayerRole { get; set; } = string.Empty;

    [MaxLength(64), Column("provider_id")]
    public string? ProviderId { get; set; }

    [MaxLength(128), Column("model_id")]
    public string? ModelId { get; set; }

    [Required, Column("payload_json", TypeName = "TEXT")]
    public string PayloadJson { get; set; } = string.Empty;

    [Required, Column("built_at_utc")]
    public DateTimeOffset BuiltAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
