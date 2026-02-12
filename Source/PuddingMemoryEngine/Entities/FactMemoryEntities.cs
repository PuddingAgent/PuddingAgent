using System.ComponentModel.DataAnnotations;

namespace PuddingMemoryEngine.Entities;

/// <summary>
/// 新版事实记忆空间。它是事实集合的租户和管理边界，不承载旧版 Book/Chapter 语义。
/// </summary>
public sealed class MemorySpaceEntity
{
    [Key]
    [MaxLength(32)]
    public string MemorySpaceId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AgentId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [MaxLength(32)]
    public string Status { get; set; } = "active";

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>事实是新版记忆库的主对象。</summary>
public sealed class GraphMemoryFactEntity
{
    [Key]
    [MaxLength(32)]
    public string FactId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AgentId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string MemorySpaceId { get; set; } = string.Empty;

    public string Statement { get; set; } = string.Empty;

    public string? StructuredPayloadJson { get; set; }

    [MaxLength(128)]
    public string FactType { get; set; } = string.Empty;

    public double Confidence { get; set; }

    [MaxLength(32)]
    public string Status { get; set; } = "pending";

    [MaxLength(32)]
    public string? SupersededByFactId { get; set; }

    [MaxLength(64)]
    public string CreatedByType { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? CreatedById { get; set; }

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long? AcceptedAt { get; set; }

    public long? RejectedAt { get; set; }

    public long? ArchivedAt { get; set; }
}

/// <summary>事实证据。所有正式事实写入都必须至少绑定一条证据。</summary>
public sealed class MemoryFactEvidenceEntity
{
    [Key]
    [MaxLength(32)]
    public string EvidenceId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AgentId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string MemorySpaceId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string FactId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string SourceType { get; set; } = string.Empty;

    [MaxLength(256)]
    public string SourceId { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? SourceRange { get; set; }

    public string? QuoteSummary { get; set; }

    [MaxLength(128)]
    public string? EvidenceHash { get; set; }

    public double Confidence { get; set; }

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>事实适用上下文。</summary>
public sealed class MemoryFactContextEntity
{
    [Key]
    [MaxLength(32)]
    public string ContextId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AgentId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string MemorySpaceId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string FactId { get; set; } = string.Empty;

    public string ContextJson { get; set; } = "{}";

    [MaxLength(128)]
    public string ContextHash { get; set; } = string.Empty;

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>事实新鲜度配置。</summary>
public sealed class MemoryFactFreshnessEntity
{
    [Key]
    [MaxLength(32)]
    public string FreshnessId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AgentId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string MemorySpaceId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string FactId { get; set; } = string.Empty;

    public long? ObservedAt { get; set; }

    public long? LastVerifiedAt { get; set; }

    public long? ValidFrom { get; set; }

    public long? ValidTo { get; set; }

    public long? HalfLifeSeconds { get; set; }

    [MaxLength(32)]
    public string DecayKind { get; set; } = "stable";

    public double StaleThreshold { get; set; } = 0.5;

    public double ExpiredThreshold { get; set; } = 0.1;

    [MaxLength(64)]
    public string? RefreshHint { get; set; }

    public string? FreshnessReason { get; set; }

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>事实中出现的实体提及。</summary>
public sealed class MemoryFactEntityMentionEntity
{
    [Key]
    [MaxLength(32)]
    public string MentionId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AgentId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string MemorySpaceId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string FactId { get; set; } = string.Empty;

    [MaxLength(256)]
    public string EntityKey { get; set; } = string.Empty;

    [MaxLength(128)]
    public string EntityType { get; set; } = string.Empty;

    [MaxLength(256)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Role { get; set; } = string.Empty;

    public string? AliasesJson { get; set; }

    public string? PropertiesJson { get; set; }

    public double Confidence { get; set; }

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>事实支撑的实体、事实或上下文关联。</summary>
public sealed class MemoryFactAssociationEntity
{
    [Key]
    [MaxLength(32)]
    public string AssociationId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AgentId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string MemorySpaceId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string FactId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string SourceKind { get; set; } = string.Empty;

    [MaxLength(256)]
    public string SourceKey { get; set; } = string.Empty;

    [MaxLength(64)]
    public string TargetKind { get; set; } = string.Empty;

    [MaxLength(256)]
    public string TargetKey { get; set; } = string.Empty;

    [MaxLength(128)]
    public string AssociationType { get; set; } = string.Empty;

    public double Weight { get; set; }

    public double Confidence { get; set; }

    public string? ContextJson { get; set; }

    public string? EvidenceIdsJson { get; set; }

    public long? ObservedAt { get; set; }

    public long? HalfLifeSeconds { get; set; }

    public string? Reason { get; set; }

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>事实修订审计。</summary>
public sealed class MemoryFactRevisionEntity
{
    [Key]
    [MaxLength(32)]
    public string RevisionId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AgentId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string MemorySpaceId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string FactId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string RevisionType { get; set; } = string.Empty;

    public string? BeforeJson { get; set; }

    public string? AfterJson { get; set; }

    [MaxLength(64)]
    public string ActorType { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? ActorId { get; set; }

    public string? Reason { get; set; }

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
