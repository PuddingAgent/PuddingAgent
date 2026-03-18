using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>平台用户类型</summary>
public enum UserType
{
    SimpleUser = 0,
    Admin = 1,
}

/// <summary>
/// 平台用户实体。Admin 是平台最高权限；SimpleUser 通过角色获得细粒度权限。
/// </summary>
public class AppUserEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>登录名/唯一 slug，如 "alice"、"john-doe"</summary>
    [Required, MaxLength(64)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>用户名（可重复，用于显示）</summary>
    [Required, MaxLength(128)]
    public string Username { get; set; } = string.Empty;

    /// <summary>邮箱（唯一）</summary>
    [Required, MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>PBKDF2-SHA256 密码哈希，格式：base64(salt):base64(hash)</summary>
    [Required, MaxLength(512)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>显示名称（可选）</summary>
    [MaxLength(128)]
    public string? DisplayName { get; set; }

    /// <summary>用户类型</summary>
    public UserType UserType { get; set; } = UserType.SimpleUser;

    /// <summary>是否启用</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航属性
    public ICollection<AppUserRoleEntity> UserRoles { get; set; } = [];
    public ICollection<TeamMemberEntity> TeamMemberships { get; set; } = [];
    public ICollection<WorkspaceMemberEntity> WorkspaceMemberships { get; set; } = [];
}
