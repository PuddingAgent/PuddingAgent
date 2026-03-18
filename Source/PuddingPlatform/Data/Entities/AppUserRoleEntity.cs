namespace PuddingPlatform.Data.Entities;

/// <summary>用户 → 角色 多对多中间表</summary>
public class AppUserRoleEntity
{
    /// <summary>FK → AppUserEntity.Id</summary>
    public int UserEntityId { get; set; }

    /// <summary>FK → AppRoleEntity.Id</summary>
    public int RoleEntityId { get; set; }

    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航
    public AppUserEntity User { get; set; } = null!;
    public AppRoleEntity Role { get; set; } = null!;
}
