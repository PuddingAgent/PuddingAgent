using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// One provider-returned usage payload per LLM gateway request.
/// Unlike TokenUsageEvents, this table is not a conversation attribution
/// projection and therefore is safe to use as the billing/request-count source.
/// </summary>
[Table("llm_gateway_usage_events")]
public sealed class LlmGatewayUsageEventEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(128), Column("source_id")]
    public string SourceId { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("operation")]
    public string Operation { get; set; } = string.Empty;

    [MaxLength(64), Column("workspace_id")]
    public string? WorkspaceId { get; set; }

    [MaxLength(64), Column("session_id")]
    public string? SessionId { get; set; }

    [MaxLength(128), Column("agent_template_id")]
    public string? AgentTemplateId { get; set; }

    [Required, MaxLength(64), Column("provider_id")]
    public string ProviderId { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [Required, Column("occurred_at_utc")]
    public DateTimeOffset OccurredAtUtc { get; set; }

    [Required, MaxLength(7), Column("year_month")]
    public string YearMonth { get; set; } = string.Empty;

    [Column("prompt_tokens")]
    public long PromptTokens { get; set; }

    [Column("completion_tokens")]
    public long CompletionTokens { get; set; }

    [Column("total_tokens")]
    public long TotalTokens { get; set; }

    [Column("cache_hit_tokens")]
    public long CacheHitTokens { get; set; }

    [Column("cache_miss_tokens")]
    public long CacheMissTokens { get; set; }

    [Column("input_cost", TypeName = "decimal(18,10)")]
    public decimal InputCost { get; set; }

    [Column("output_cost", TypeName = "decimal(18,10)")]
    public decimal OutputCost { get; set; }

    [Column("cache_hit_cost", TypeName = "decimal(18,10)")]
    public decimal CacheHitCost { get; set; }

    [Column("total_cost", TypeName = "decimal(18,10)")]
    public decimal TotalCost { get; set; }

    [Column("raw_usage_json", TypeName = "TEXT")]
    public string? RawUsageJson { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
