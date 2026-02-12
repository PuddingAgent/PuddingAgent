using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// Context layer metric facts for long-term cache-hit and prompt-composition analysis.
/// One row is written for one assembled context layer within one LLM call.
/// </summary>
[Table("context_layer_metric_events")]
public sealed class ContextLayerMetricEventEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Required, MaxLength(32)]
    [Column("source_type")]
    public string SourceType { get; set; } = string.Empty;

    [Required, MaxLength(128)]
    [Column("source_id")]
    public string SourceId { get; set; } = string.Empty;

    [MaxLength(64)]
    [Column("workspace_id")]
    public string? WorkspaceId { get; set; }

    [MaxLength(64)]
    [Column("session_id")]
    public string? SessionId { get; set; }

    [MaxLength(64)]
    [Column("provider_id")]
    public string? ProviderId { get; set; }

    [MaxLength(128)]
    [Column("model_id")]
    public string? ModelId { get; set; }

    [Column("occurred_at_utc")]
    public DateTimeOffset OccurredAtUtc { get; set; }

    [Required, MaxLength(32)]
    [Column("assembler_version")]
    public string AssemblerVersion { get; set; } = "context-v1";

    [Required, MaxLength(32)]
    [Column("layout_version")]
    public string LayoutVersion { get; set; } = "layer-v1";

    [Required, MaxLength(64)]
    [Column("layer_name")]
    public string LayerName { get; set; } = string.Empty;

    [Column("layer_order")]
    public int LayerOrder { get; set; }

    [Required, MaxLength(64)]
    [Column("layer_role")]
    public string LayerRole { get; set; } = string.Empty;

    [Column("token_count")]
    public long TokenCount { get; set; }

    [Column("char_count")]
    public long CharCount { get; set; }

    [Required, MaxLength(64)]
    [Column("content_hash")]
    public string ContentHash { get; set; } = string.Empty;

    [MaxLength(64)]
    [Column("previous_hash")]
    public string? PreviousHash { get; set; }

    [Column("is_changed")]
    public bool IsChanged { get; set; }

    [MaxLength(128)]
    [Column("change_reason")]
    public string? ChangeReason { get; set; }

    [Column("starts_at_token")]
    public long StartsAtToken { get; set; }

    [Column("ends_at_token")]
    public long EndsAtToken { get; set; }

    [Column("is_cache_eligible")]
    public bool IsCacheEligible { get; set; }

    [Column("estimated_cache_hit_tokens")]
    public long EstimatedCacheHitTokens { get; set; }

    [Column("estimated_cache_miss_tokens")]
    public long EstimatedCacheMissTokens { get; set; }

    [Column("estimated_cache_hit_rate")]
    public double? EstimatedCacheHitRate { get; set; }

    [Required, MaxLength(16)]
    [Column("confidence")]
    public string Confidence { get; set; } = "estimated";

    [Column("truncated_tokens")]
    public long TruncatedTokens { get; set; }

    [MaxLength(128)]
    [Column("truncated_reason")]
    public string? TruncatedReason { get; set; }

    [Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
