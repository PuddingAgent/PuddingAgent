namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 工作区显式成员（白名单）。
/// 为特定用户分配比默认策略更高（或更低）的访问级别。
/// </summary>
public class WorkspaceMemberEntity
{
    public int Id { get; set; }

    /// <summary>FK → WorkspaceEntity.Id</summary>
    public int WorkspaceEntityId { get; set; }

    /// <summary>FK → AppUserEntity.Id</summary>
    public int UserEntityId { get; set; }

    /// <summary>该用户在此 Workspace 的显式权限级别</summary>
    public WorkspaceAccessPolicy AccessLevel { get; set; } = WorkspaceAccessPolicy.ReadOnly;

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航
    public WorkspaceEntity Workspace { get; set; } = null!;
    public AppUserEntity User { get; set; } = null!;
}
