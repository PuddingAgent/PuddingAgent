using System.ComponentModel.DataAnnotations;

namespace PuddingMemoryEngine.Entities;

/// <summary>
/// 潜意识抽取后的事实实体。
/// </summary>
public class MemoryFactEntity
{
    [Key]
    [MaxLength(32)]
    public string FactId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>Agent 实例 ID（null = 共享知识）。ADR-042 Agent 记忆隔离。</summary>
    [MaxLength(64)]
    public string? AgentInstanceId { get; set; }

    public string Statement { get; set; } = string.Empty;

    public double Confidence { get; set; } = 0.8;

    [MaxLength(32)]
    public string Category { get; set; } = "general";

    [MaxLength(64)]
    public string? SourceSessionId { get; set; }

    [MaxLength(32)]
    public string? SourceMessageId { get; set; }

    public string? Tags { get; set; }

    /// <summary>向量字节（float32[]）。</summary>
    public byte[]? Embedding { get; set; }

    [MaxLength(32)]
    public string Status { get; set; } = "active";

    [MaxLength(32)]
    public string? MergedInto { get; set; }

    public int AccessCount { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>外部引用 URL 或内部会话记录路径，用于核实事实</summary>
    [MaxLength(1024)]
    public string? SourceReference { get; set; }

    /// <summary>引用类型：internal（会话记录）/ external（外部URL）/ none</summary>
    [MaxLength(32)]
    public string? ReferenceType { get; set; }
}

/// <summary>
/// 潜意识抽取后的偏好实体。
/// </summary>
public class MemoryPreferenceEntity
{
    [Key]
    [MaxLength(32)]
    public string PreferenceId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>Agent 实例 ID（null = 共享知识）。ADR-042 Agent 记忆隔离。</summary>
    [MaxLength(64)]
    public string? AgentInstanceId { get; set; }

    [MaxLength(32)]
    public string Category { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? SourceSessionId { get; set; }

    [MaxLength(32)]
    public string? SourceMessageId { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>外部引用 URL 或内部会话记录路径，用于核实偏好来源</summary>
    [MaxLength(1024)]
    public string? SourceReference { get; set; }

    /// <summary>引用类型：internal（会话记录）/ external（外部URL）/ none</summary>
    [MaxLength(32)]
    public string? ReferenceType { get; set; }
}

/// <summary>
/// 潜意识后台任务日志实体（可观测性）。
/// </summary>
public class SubconsciousJobLogEntity
{
    [Key]
    [MaxLength(32)]
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string SessionId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Status { get; set; } = "pending";

    public int FactsExtracted { get; set; }

    public int FactsMerged { get; set; }

    public int FactsDiscarded { get; set; }

    public int ChaptersCreated { get; set; }

    public int LlmTokensUsed { get; set; }

    [MaxLength(128)]
    public string? LlmModelId { get; set; }

    public int ElapsedMs { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long? StartedAt { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long? CompletedAt { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// Durable subconscious background job used by framework hooks.
/// </summary>
public class SubconsciousJobEntity
{
    [Key]
    [MaxLength(32)]
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(128)]
    public string JobType { get; set; } = string.Empty;

    [MaxLength(256)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Status { get; set; } = "pending";

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string SessionId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AgentId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AgentTemplateId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? SourceHookName { get; set; }

    [MaxLength(64)]
    public string? SourceEventId { get; set; }

    [MaxLength(64)]
    public string? SourceCompactionId { get; set; }

    public string PayloadJson { get; set; } = "{}";

    public string? ResultJson { get; set; }

    public int RetryCount { get; set; }

    [MaxLength(128)]
    public string? LeaseOwner { get; set; }

    public long? LeaseUntil { get; set; }

    public long AvailableAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long? StartedAt { get; set; }

    public long? CompletedAt { get; set; }

    public string? ErrorMessage { get; set; }

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
