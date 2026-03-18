namespace PuddingPlatform.Data.Entities;

/// <summary>团队成员角色</summary>
public enum TeamMemberRole
{
    Member = 0,
    Admin = 1,
}

/// <summary>用户 → 团队 多对多中间表（含成员角色）</summary>
public class TeamMemberEntity
{
    /// <summary>FK → TeamEntity.Id</summary>
    public int TeamEntityId { get; set; }

    /// <summary>FK → AppUserEntity.Id</summary>
    public int UserEntityId { get; set; }

    /// <summary>在该团队内的角色</summary>
    public TeamMemberRole Role { get; set; } = TeamMemberRole.Member;

    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航
    public TeamEntity Team { get; set; } = null!;
    public AppUserEntity User { get; set; } = null!;
}
