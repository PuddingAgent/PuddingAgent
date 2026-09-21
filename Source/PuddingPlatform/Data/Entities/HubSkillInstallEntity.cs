using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// SKILL Hub 安装台账——记录哪个 Agent 实例装了哪个技能的哪个版本
/// （设计契约 §4.3，字段冻结）。(SkillId, AgentInstanceId) 唯一，upsert 语义。
/// </summary>
public class HubSkillInstallEntity
{
    [Key]
    public int Id { get; set; }

    [MaxLength(128)]
    public string SkillId { get; set; } = "";

    [MaxLength(128)]
    public string AgentInstanceId { get; set; } = "";

    [MaxLength(128)]
    public string? WorkspaceId { get; set; }

    [MaxLength(64)]
    public string InstalledVersion { get; set; } = "";

    [MaxLength(64)]
    public string? ContentHash { get; set; }

    /// <summary>agent | user | auto</summary>
    [MaxLength(32)]
    public string InstalledBy { get; set; } = "agent";

    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
