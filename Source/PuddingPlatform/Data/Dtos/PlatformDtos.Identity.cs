namespace PuddingPlatform.Data.Dtos;

// ════════════════════════════════════════════════════════════════
// Identity DTOs — 用户、角色、团队、工作区成员。
// ════════════════════════════════════════════════════════════════

// ── App User 用户 ──────────────────────────────────────────────

public record AppUserDto(
    int Id,
    string UserId,
    string Username,
    string Email,
    string? DisplayName,
    string UserType,
    bool IsEnabled,
    List<string> RoleIds,
    DateTimeOffset CreatedAt
);

public record CreateUserRequest(
    string UserId,
    string Username,
    string Email,
    string Password,
    string? DisplayName,
    string UserType
);

public record UpdateUserRequest(
    string Username,
    string Email,
    string? DisplayName,
    string UserType,
    bool IsEnabled
);

public record ChangePasswordRequest(string NewPassword);

public record AssignRolesRequest(List<string> RoleIds);

// ── App Role 角色与权限 ────────────────────────────────────────

public record AppRoleDto(
    int Id,
    string RoleId,
    string Name,
    string? Description,
    List<string> Permissions,
    bool IsSystemRole,
    DateTimeOffset CreatedAt
);

public record UpsertRoleRequest(
    string RoleId,
    string Name,
    string? Description,
    List<string> Permissions
);

// ── Team 团队 ──────────────────────────────────────────────────

public record TeamDto(
    int Id,
    string TeamId,
    string Name,
    string? Description,
    bool IsEnabled,
    int MemberCount,
    int WorkspaceCount,
    DateTimeOffset CreatedAt
);

public record TeamDetailDto(
    int Id,
    string TeamId,
    string Name,
    string? Description,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    List<TeamMemberDto> Members,
    List<WorkspaceWithPermDto> Workspaces
);

public record UpsertTeamRequest(
    string TeamId,
    string Name,
    string? Description,
    bool IsEnabled
);

public record TeamMemberDto(
    string UserId,
    string Username,
    string? DisplayName,
    string Role
);

public record AddTeamMemberRequest(
    string UserId,
    string Role
);

// ── Workspace 工作区 ───────────────────────────────────────────

public record WorkspaceWithPermDto(
    int Id,
    string WorkspaceId,
    string Slug,
    string TeamId,
    string TeamName,
    string Name,
    string? Description,
    string TeamAccessPolicy,
    string CompanyAccessPolicy,
    bool IsEnabled,
    bool IsFrozen,
    int MemberCount,
    DateTimeOffset CreatedAt,
    string? UserProfile = null
);

public record CreateWorkspaceRequest(
    string WorkspaceId,
    string TeamId,
    string Name,
    string? Description,
    string TeamAccessPolicy,
    string CompanyAccessPolicy,
    string? UserProfile = null
);

public record UpdateWorkspaceRequest(
    string Name,
    string? Description,
    string TeamAccessPolicy,
    string CompanyAccessPolicy,
    bool IsEnabled,
    string? UserProfile = null
);

public record WorkspaceMemberDto(
    int Id,
    string UserId,
    string Username,
    string? DisplayName,
    string AccessLevel
);

public record AddWorkspaceMemberRequest(
    string UserId,
    string AccessLevel
);
