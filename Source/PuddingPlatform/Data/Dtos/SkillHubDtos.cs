namespace PuddingPlatform.Data.Dtos;

// ════════════════════════════════════════════════════════════════
// SKILL Hub（中央技能库）DTO —— 设计契约 §5.1/§5.2，字段冻结。
// 仅供 /api/skill-hub/* 端点使用；与 /api/skill-packages（SkillPackageDto）
// 是两条独立链路，互不影响。
// ════════════════════════════════════════════════════════════════

public sealed record HubSkillSummaryDto(
    string SkillId, string Name, string? Summary, string? Description,
    IReadOnlyList<string> Tags, IReadOnlyList<string> Keywords,
    string LatestVersion, string Status, string Visibility,
    string? OwnerWorkspaceId, string? SourceAgentId, string OriginKind,
    int VersionCount, int InstallCount, int PublishCount,
    string? LatestContentHash, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record HubSkillVersionDto(
    string SkillId, string Version, string ContentHash,
    string EvolutionAction, string? ParentVersion,
    IReadOnlyList<string> RelatedSkillIds,
    string? PublishedByAgentId, string? PublishedByWorkspaceId,
    string? PublishNote, int ContentBytes, DateTimeOffset CreatedAt);

/// <summary>单版本全文响应：HubSkillVersionDto 全部字段 + SkillMarkdown（契约 §5.2"版本全文"端点）。</summary>
public sealed record HubSkillVersionContentDto(
    string SkillId, string Version, string ContentHash,
    string EvolutionAction, string? ParentVersion,
    IReadOnlyList<string> RelatedSkillIds,
    string? PublishedByAgentId, string? PublishedByWorkspaceId,
    string? PublishNote, int ContentBytes, DateTimeOffset CreatedAt,
    string SkillMarkdown);

public sealed record HubSkillDetailDto(
    HubSkillSummaryDto Skill,
    IReadOnlyList<HubSkillVersionDto> Versions,
    IReadOnlyList<HubSkillInstallDto> RecentInstalls);

public sealed record HubSkillInstallDto(
    string SkillId, string AgentInstanceId, string? WorkspaceId,
    string InstalledVersion, string? ContentHash, string InstalledBy,
    DateTimeOffset InstalledAt, DateTimeOffset UpdatedAt);

public sealed record HubSkillEventDto(
    long Id, string SkillId, string? Version, string EventType,
    string ActorKind, string? ActorId, string? WorkspaceId,
    string? PayloadJson, DateTimeOffset CreatedAt);

public sealed record HubSkillStatsDto(
    int TotalSkills, int ActiveSkills, int RetiredSkills,
    int TotalVersions, int TotalInstalls, int DistinctAgents,
    int EvolvedSkills,            // VersionCount > 1 的技能数
    IReadOnlyList<HubSkillActionCountDto> EvolutionActionCounts,
    IReadOnlyList<HubSkillSummaryDto> TopInstalled,
    DateTimeOffset GeneratedAt);

public sealed record HubSkillActionCountDto(string Action, int Count);

// ── EVO MAP ────────────────────────────────────────────────
public sealed record EvoMapNodeDto(
    string NodeId,            // "{SkillId}@{Version}"
    string SkillId, string Version, string EvolutionAction,
    string? ParentNodeId,     // null = 根节点
    string Name, string Status,
    string? PublishedByAgentId, DateTimeOffset CreatedAt,
    int ContentBytes, int InstallCount);

public sealed record EvoMapEdgeDto(string FromNodeId, string ToNodeId, string Action);

public sealed record EvoMapDto(
    IReadOnlyList<EvoMapNodeDto> Nodes,
    IReadOnlyList<EvoMapEdgeDto> Edges,
    DateTimeOffset GeneratedAt);

// ── 请求体 ─────────────────────────────────────────────────
public sealed record PublishHubSkillRequest(
    string SkillId, string Name, string? Summary, string? Description,
    IReadOnlyList<string>? Tags, IReadOnlyList<string>? Keywords,
    string Version, string SkillMarkdown, string? ManifestJson,
    string? EvolutionAction, string? ParentVersion,
    IReadOnlyList<string>? RelatedSkillIds,
    string? PublishedByAgentId, string? PublishedByWorkspaceId,
    string? PublishNote, string? EvidenceJson,
    string Visibility = "global");

public sealed record UpdateHubSkillMetaRequest(
    string? Name, string? Summary, string? Description,
    IReadOnlyList<string>? Tags, IReadOnlyList<string>? Keywords,
    string? Status, string? Visibility);

public sealed record RegisterInstallRequest(
    string SkillId, string AgentInstanceId, string? WorkspaceId,
    string InstalledVersion, string? ContentHash, string? InstalledBy);

/// <summary>GET /updates 响应项：本地已登记版本落后于最新版本（契约 §5.2）。</summary>
public sealed record HubSkillUpdateDto(
    string SkillId, string Name, string InstalledVersion, string LatestVersion,
    string LatestEvolutionAction, DateTimeOffset LatestPublishedAt, string? PublishNote);
