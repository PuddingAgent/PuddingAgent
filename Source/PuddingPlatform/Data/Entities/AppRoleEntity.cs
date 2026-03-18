using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 权限角色（权限组）。Admin 可创建自定义角色并分配给 SimpleUser。
/// SystemRole 为内置角色，不允许删除。
/// </summary>
public class AppRoleEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>角色唯一 slug，如 "workspace-admin"、"workspace-viewer"</summary>
    [Required, MaxLength(64)]
    public string RoleId { get; set; } = string.Empty;

    /// <summary>角色显示名称</summary>
    [Required, MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>角色描述</summary>
    [MaxLength(512)]
    public string? Description { get; set; }

    /// <summary>权限列表（JSON 字符串数组），如 ["workspace:read","workspace:manage"]</summary>
    public string PermissionsJson { get; set; } = "[]";

    /// <summary>是否系统内置（不允许删除）</summary>
    public bool IsSystemRole { get; set; } = false;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航
    public ICollection<AppUserRoleEntity> UserRoles { get; set; } = [];
}
