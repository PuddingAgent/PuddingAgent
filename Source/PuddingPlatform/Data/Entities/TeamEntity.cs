using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 部门 / 团队。Workspace 由团队持有。
/// URI 格式：/workspace/{teamId}/{workspaceSlug}
/// </summary>
public class TeamEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>团队唯一 slug（英文），用于 URI 路径，如 "platform-team"</summary>
    [Required, MaxLength(64)]
    public string TeamId { get; set; } = string.Empty;

    /// <summary>团队显示名称</summary>
    [Required, MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>团队描述</summary>
    [MaxLength(512)]
    public string? Description { get; set; }

    /// <summary>是否启用</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航
    public ICollection<TeamMemberEntity> Members { get; set; } = [];
    public ICollection<WorkspaceEntity> Workspaces { get; set; } = [];
}
