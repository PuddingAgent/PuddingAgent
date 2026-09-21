using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// SKILL Hub 技能主档——中央技能库的一等条目（设计契约 §4.1，字段冻结）。
/// 持有"最新版本指针"（LatestVersion / LatestContentHash）与聚合计数。
/// </summary>
public class HubSkillEntity
{
    [Key]
    public int Id { get; set; }

    [MaxLength(128)]
    public string SkillId { get; set; } = "";

    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(512)]
    public string? Summary { get; set; }

    [MaxLength(2048)]
    public string? Description { get; set; }

    /// <summary>JSON 字符串数组</summary>
    [MaxLength(1024)]
    public string TagsJson { get; set; } = "[]";

    [MaxLength(2048)]
    public string KeywordsJson { get; set; } = "[]";

    [MaxLength(64)]
    public string LatestVersion { get; set; } = "1.0.0";

    /// <summary>active | deprecated | retired</summary>
    [MaxLength(32)]
    public string Status { get; set; } = "active";

    /// <summary>global | workspace</summary>
    [MaxLength(32)]
    public string Visibility { get; set; } = "global";

    [MaxLength(128)]
    public string? OwnerWorkspaceId { get; set; }

    [MaxLength(128)]
    public string? SourceAgentId { get; set; }

    /// <summary>agent-evolved | manual | imported</summary>
    [MaxLength(32)]
    public string OriginKind { get; set; } = "agent-evolved";

    public int VersionCount { get; set; } = 1;

    /// <summary>去重后的 Agent 数</summary>
    public int InstallCount { get; set; }

    public int PublishCount { get; set; } = 1;

    [MaxLength(64)]
    public string? LatestContentHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
