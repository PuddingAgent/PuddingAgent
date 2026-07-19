using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// Token 使用事件明细账本（ADR-043）。
/// 记录每次 LLM 调用的 usage 明细，作为长期统计事实表。
/// 由 TokenUsageRecorder 写入，支持重建和审计。
/// 唯一索引 (SourceType, SourceId) 保证幂等。
/// </summary>
public class TokenUsageEventEntity
{
    [Key]
    public long Id { get; set; }

    /// <summary>数据来源类型：chat_message / session_event / runtime_activity</summary>
    [Required, MaxLength(32)]
    public string SourceType { get; set; } = string.Empty;

    /// <summary>来源 ID（messageId 或 eventId），配合 SourceType 唯一标识一次调用</summary>
    [Required, MaxLength(128)]
    public string SourceId { get; set; } = string.Empty;

    /// <summary>工作区 ID</summary>
    [MaxLength(64)]
    public string? WorkspaceId { get; set; }

    /// <summary>会话 ID</summary>
    [MaxLength(64)]
    public string? SessionId { get; set; }

    /// <summary>父会话 ID（子代理 token 归因到父会话）</summary>
    [MaxLength(64)]
    public string? ParentSessionId { get; set; }

    /// <summary>服务商 ID（slug）</summary>
    [MaxLength(64)]
    public string? ProviderId { get; set; }

    /// <summary>模型 ID</summary>
    [MaxLength(128)]
    public string? ModelId { get; set; }

    /// <summary>调用发生时间（UTC）</summary>
    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>统计月份，格式 yyyy-MM</summary>
    [Required, MaxLength(7)]
    public string YearMonth { get; set; } = string.Empty;

    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }
    public long CacheHitTokens { get; set; }
    public long CacheMissTokens { get; set; }
    public long CacheEligibleTokens { get; set; }

    /// <summary>缓存命中率 0.0 ~ 1.0，无缓存事件时为 null</summary>
    public double? CacheHitRate { get; set; }

    /// <summary>输入 token 成本（未命中部分 × 输入单价）</summary>
    public decimal InputCost { get; set; }

    /// <summary>输出 token 成本</summary>
    public decimal OutputCost { get; set; }

    /// <summary>缓存命中 token 成本（命中部分 × 缓存命中单价）</summary>
    public decimal CacheHitCost { get; set; }

    /// <summary>总成本 = InputCost + OutputCost + CacheHitCost</summary>
    public decimal TotalCost { get; set; }

    /// <summary>原始 usage JSON，用于审计和后续扩展</summary>
    public string? RawUsageJson { get; set; }

    /// <summary>prefix 快照算法版本。</summary>
    [MaxLength(32)]
    public string? PrefixVersion { get; set; }

    /// <summary>system prompt 与工具规格组成的稳定前缀哈希。</summary>
    [MaxLength(64)]
    public string? PrefixHash { get; set; }

    /// <summary>system prompt 单独哈希。</summary>
    [MaxLength(64)]
    public string? SystemPromptHash { get; set; }

    /// <summary>工具规格哈希。</summary>
    [MaxLength(64)]
    public string? ToolSpecHash { get; set; }

    /// <summary>长期记忆哈希；当前未拆分时为空。</summary>
    [MaxLength(64)]
    public string? MemoryHash { get; set; }

    /// <summary>few-shot 示例哈希；当前未启用时为空。</summary>
    [MaxLength(64)]
    public string? FewShotHash { get; set; }

    /// <summary>本轮 prefix 改变原因；为空时诊断按 unexpected churn 处理。</summary>
    [MaxLength(128)]
    public string? PrefixChangeReason { get; set; }

    public int? PrefixMessageCount { get; set; }
    public int? PrefixToolCount { get; set; }

    /// <summary>Agent loop 轮次（0-based）。</summary>
    public int? TurnRound { get; set; }

    /// <summary>此轮 LLM 调用的工具数量。</summary>
    public int? ToolCallCount { get; set; }

    /// <summary>此轮请求的工具名列表（逗号分隔）。</summary>
    [MaxLength(512)]
    public string? ToolNames { get; set; }

    /// <summary>子代理 ID。</summary>
    [MaxLength(128)]
    public string? SubAgentId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
