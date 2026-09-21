using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// SKILL Hub 技能版本——EVO MAP 的节点（设计契约 §4.2，字段冻结）。
/// 内容为 SKILL.md 全文，带进化动作与父版本血缘。
/// </summary>
public class HubSkillVersionEntity
{
    [Key]
    public int Id { get; set; }

    [MaxLength(128)]
    public string SkillId { get; set; } = "";

    [MaxLength(64)]
    public string Version { get; set; } = "1.0.0";

    [MaxLength(64)]
    public string ContentHash { get; set; } = "";

    /// <summary>SKILL.md 全文</summary>
    public string SkillMarkdown { get; set; } = "";

    /// <summary>manifest 原文</summary>
    public string ManifestJson { get; set; } = "{}";

    /// <summary>create|patch|split|compress|retire|merge|fork</summary>
    [MaxLength(32)]
    public string EvolutionAction { get; set; } = "create";

    /// <summary>同一 SkillId 内的父版本；create 时为 null</summary>
    [MaxLength(64)]
    public string? ParentVersion { get; set; }

    /// <summary>分叉/合并时指向的其它 SkillId（JSON 字符串数组）</summary>
    [MaxLength(512)]
    public string? RelatedSkillIdsJson { get; set; }

    [MaxLength(128)]
    public string? PublishedByAgentId { get; set; }

    [MaxLength(128)]
    public string? PublishedByWorkspaceId { get; set; }

    [MaxLength(512)]
    public string? PublishNote { get; set; }

    /// <summary>证据（触发该进化的证据摘要，JSON）</summary>
    public string? EvidenceJson { get; set; }

    public int ContentBytes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
