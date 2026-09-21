using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// SKILL Hub 审计事件流——所有写操作的追加式审计记录
/// （设计契约 §4.4，字段冻结）。
/// </summary>
public class HubSkillEventEntity
{
    [Key]
    public long Id { get; set; }

    [MaxLength(128)]
    public string SkillId { get; set; } = "";

    [MaxLength(64)]
    public string? Version { get; set; }

    /// <summary>publish|update_version|install|update|status_change|delete</summary>
    [MaxLength(32)]
    public string EventType { get; set; } = "";

    /// <summary>agent | user | system</summary>
    [MaxLength(32)]
    public string ActorKind { get; set; } = "agent";

    [MaxLength(128)]
    public string? ActorId { get; set; }

    [MaxLength(128)]
    public string? WorkspaceId { get; set; }

    public string? PayloadJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
