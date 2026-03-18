using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// Workspace 默认访问策略级别。
/// 可分别为"团队范围"和"全公司范围"配置，取最大值生效。
/// 若两者均为 None，则仅白名单用户（WorkspaceMemberEntity）可访问。
/// </summary>
public enum WorkspaceAccessPolicy
{
    /// <summary>无权限（白名单模式基础）</summary>
    None = 0,

    /// <summary>仅可读（查看 Workspace 内容，不可创建/编辑）</summary>
    ReadOnly = 1,

    /// <summary>可读写（可创建 Session、调用 Agent）</summary>
    Write = 2,

    /// <summary>可管理（可修改 Workspace 配置、Agent 模板、成员）</summary>
    Manage = 3,
}

/// <summary>
/// EF Core 持久化工作区。由团队持有，含团队级与公司级访问策略。
/// URI：/workspace/{team.TeamId}/{WorkspaceSlug}
/// </summary>
public class WorkspaceEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>全局唯一 slug（英文），向后兼容现有 WorkspaceId 字符串引用</summary>
    [Required, MaxLength(128)]
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>Workspace 在团队内的 slug（英文），与 TeamEntityId 联合唯一</summary>
    [Required, MaxLength(64)]
    public string Slug { get; set; } = string.Empty;

    /// <summary>FK → TeamEntity.Id（所属团队）</summary>
    public int TeamEntityId { get; set; }

    /// <summary>显示名称</summary>
    [Required, MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>描述</summary>
    [MaxLength(512)]
    public string? Description { get; set; }

    /// <summary>
    /// 团队成员默认访问策略。
    /// None = 仅显式白名单；ReadOnly/Write/Manage = 团队内所有人默认获得该权限。
    /// </summary>
    public WorkspaceAccessPolicy TeamAccessPolicy { get; set; } = WorkspaceAccessPolicy.ReadOnly;

    /// <summary>
    /// 全公司成员默认访问策略。
    /// None = 公司其他人无权；ReadOnly/Write/Manage = 全公司所有人默认获得该权限。
    /// </summary>
    public WorkspaceAccessPolicy CompanyAccessPolicy { get; set; } = WorkspaceAccessPolicy.None;

    /// <summary>是否启用</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>是否冻结（冻结时仅 Admin/Manage 可操作）</summary>
    public bool IsFrozen { get; set; } = false;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航
    public TeamEntity Team { get; set; } = null!;
    public ICollection<WorkspaceMemberEntity> Members { get; set; } = [];
}
