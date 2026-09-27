using Microsoft.EntityFrameworkCore;
using PuddingCode.Skills;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

public sealed record TeamDraft(string TeamId, string Name, string? Description, bool IsEnabled);

public sealed record TeamWorkspaceDraft(
    string WorkspaceId, string Name, string? Description, string? UserProfile,
    string TeamAccessPolicy, string CompanyAccessPolicy, bool IsEnabled);

/// <summary>
/// 团队与工作区应用操作——从 TeamApiController 原位下沉（该控制器原先直接使用 DbContext）。
///
/// 保留 Core 的既有规则：团队重名冲突；**团队下还有工作区时不允许删除团队**；团队成员要求用户存在、
/// 角色属于 Member/Admin、不可重复；工作区成员要求访问级别属于枚举且**不能是 None**、不可重复；
/// 跨团队/跨工作区的 id 一律 NotFound。
///
/// 一处刻意的行为修正：本控制器的工作区删除**没有**默认工作区保护（WorkspaceApiController 有），
/// 从团队页删掉 default 会让整个安装失去默认工作区；这里补上同一条保护。
/// </summary>
public sealed class TeamService(PlatformDbContext db)
{
    public static IReadOnlyList<string> MemberRoles { get; } = [nameof(TeamMemberRole.Member), nameof(TeamMemberRole.Admin)];
    public static IReadOnlyList<string> AccessLevels { get; } = Enum.GetNames<WorkspaceAccessPolicy>();

    // ── 团队 ──────────────────────────────────────────────────────────

    public async Task<List<TeamDto>> ListAsync(CancellationToken ct = default)
        => (await db.Teams.AsNoTracking().Include(team => team.Members).Include(team => team.Workspaces)
            .OrderBy(team => team.Id).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<TeamDetailDto?> GetAsync(string teamId, CancellationToken ct = default)
        => await db.Teams.AsNoTracking()
            .Include(team => team.Members).ThenInclude(member => member.User)
            .Include(team => team.Workspaces).ThenInclude(workspace => workspace.Members)
            .FirstOrDefaultAsync(team => team.TeamId == teamId, ct) is { } team ? ToDetailDto(team) : null;

    public async Task<SkillHubResult<TeamDto>> CreateAsync(TeamDraft draft, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(draft.TeamId)) return SkillHubResult<TeamDto>.BadRequest("TeamId 不能为空。");
        if (string.IsNullOrWhiteSpace(draft.Name)) return SkillHubResult<TeamDto>.BadRequest("团队名称不能为空。");
        if (await db.Teams.AnyAsync(team => team.TeamId == draft.TeamId, ct))
            return SkillHubResult<TeamDto>.Conflict($"TeamId '{draft.TeamId}' 已存在");

        var team = new TeamEntity
        {
            TeamId = draft.TeamId.Trim(),
            Name = draft.Name.Trim(),
            Description = draft.Description,
            IsEnabled = draft.IsEnabled,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<TeamDto>.Ok(ToDto(team));
    }

    public async Task<SkillHubResult<TeamDto>> UpdateAsync(string teamId, TeamDraft draft, CancellationToken ct = default)
    {
        var team = await TrackedTeams().Include(candidate => candidate.Members).Include(candidate => candidate.Workspaces)
            .FirstOrDefaultAsync(candidate => candidate.TeamId == teamId, ct);
        if (team is null) return SkillHubResult<TeamDto>.NotFound($"团队 '{teamId}' 不存在");
        if (string.IsNullOrWhiteSpace(draft.Name)) return SkillHubResult<TeamDto>.BadRequest("团队名称不能为空。");

        team.Name = draft.Name.Trim();
        team.Description = draft.Description;
        team.IsEnabled = draft.IsEnabled;
        team.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return SkillHubResult<TeamDto>.Ok(ToDto(team));
    }

    /// <summary>A team that still owns workspaces cannot be deleted; Core refuses rather than orphaning them.</summary>
    public async Task<SkillHubResult<TeamDto>> DeleteAsync(string teamId, CancellationToken ct = default)
    {
        var team = await TrackedTeams().Include(candidate => candidate.Workspaces)
            .FirstOrDefaultAsync(candidate => candidate.TeamId == teamId, ct);
        if (team is null) return SkillHubResult<TeamDto>.NotFound($"团队 '{teamId}' 不存在");
        if (team.Workspaces.Count > 0)
            return SkillHubResult<TeamDto>.BadRequest("请先删除团队下所有工作区");

        db.Teams.Remove(team);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<TeamDto>.Ok(ToDto(team));
    }

    // ── 团队成员 ──────────────────────────────────────────────────────

    public async Task<SkillHubResult<List<TeamMemberDto>>> ListMembersAsync(string teamId, CancellationToken ct = default)
    {
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.TeamId == teamId, ct);
        if (team is null) return SkillHubResult<List<TeamMemberDto>>.NotFound($"团队 '{teamId}' 不存在");

        var members = await db.TeamMembers.AsNoTracking().Include(member => member.User)
            .Where(member => member.TeamEntityId == team.Id).ToListAsync(ct);
        return SkillHubResult<List<TeamMemberDto>>.Ok(members.Select(ToDto).ToList());
    }

    public async Task<SkillHubResult<TeamMemberDto>> AddMemberAsync(
        string teamId, string userId, string role, CancellationToken ct = default)
    {
        var team = await db.Teams.FirstOrDefaultAsync(candidate => candidate.TeamId == teamId, ct);
        if (team is null) return SkillHubResult<TeamMemberDto>.NotFound("团队不存在");

        var user = await db.AppUsers.FirstOrDefaultAsync(candidate => candidate.UserId == userId, ct);
        if (user is null) return SkillHubResult<TeamMemberDto>.NotFound("用户不存在");
        if (!TryParseMemberRole(role, out var parsed))
            return SkillHubResult<TeamMemberDto>.BadRequest("Role 无效，应为 Member 或 Admin");
        if (await db.TeamMembers.AnyAsync(
                member => member.TeamEntityId == team.Id && member.UserEntityId == user.Id, ct))
            return SkillHubResult<TeamMemberDto>.Conflict("用户已是团队成员");

        var member = new TeamMemberEntity { TeamEntityId = team.Id, UserEntityId = user.Id, Role = parsed };
        db.TeamMembers.Add(member);
        await db.SaveChangesAsync(ct);
        member.User = user;
        return SkillHubResult<TeamMemberDto>.Ok(ToDto(member));
    }

    public async Task<SkillHubResult<TeamMemberDto>> RemoveMemberAsync(
        string teamId, string userId, CancellationToken ct = default)
    {
        var team = await db.Teams.FirstOrDefaultAsync(candidate => candidate.TeamId == teamId, ct);
        if (team is null) return SkillHubResult<TeamMemberDto>.NotFound("团队不存在");
        var user = await db.AppUsers.FirstOrDefaultAsync(candidate => candidate.UserId == userId, ct);
        if (user is null) return SkillHubResult<TeamMemberDto>.NotFound("用户不存在");

        var member = await db.TeamMembers.FirstOrDefaultAsync(
            candidate => candidate.TeamEntityId == team.Id && candidate.UserEntityId == user.Id, ct);
        if (member is null) return SkillHubResult<TeamMemberDto>.NotFound("该用户不是团队成员");

        db.TeamMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<TeamMemberDto>.Ok(new TeamMemberDto(
            user.UserId, user.Username, user.DisplayName, member.Role.ToString()));
    }

    // ── 团队下的工作区 ────────────────────────────────────────────────

    public async Task<SkillHubResult<List<WorkspaceWithPermDto>>> ListWorkspacesAsync(string teamId, CancellationToken ct = default)
    {
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.TeamId == teamId, ct);
        if (team is null) return SkillHubResult<List<WorkspaceWithPermDto>>.NotFound($"团队 '{teamId}' 不存在");

        var workspaces = await db.Workspaces.AsNoTracking().Include(workspace => workspace.Team)
            .Include(workspace => workspace.Members)
            .Where(workspace => workspace.TeamEntityId == team.Id).ToListAsync(ct);
        return SkillHubResult<List<WorkspaceWithPermDto>>.Ok(workspaces.Select(ToDto).ToList());
    }

    /// <summary>Single workspace lookup by id (the team-scoped GET endpoint).</summary>
    public async Task<WorkspaceWithPermDto?> FindWorkspaceAsync(string workspaceId, CancellationToken ct = default)
        => await db.Workspaces.AsNoTracking().Include(workspace => workspace.Team)
            .Include(workspace => workspace.Members)
            .FirstOrDefaultAsync(workspace => workspace.WorkspaceId == workspaceId, ct) is { } workspace
            ? ToDto(workspace)
            : null;

    public async Task<SkillHubResult<WorkspaceWithPermDto>> CreateWorkspaceAsync(
        string teamId, TeamWorkspaceDraft draft, CancellationToken ct = default)
    {
        var team = await db.Teams.FirstOrDefaultAsync(candidate => candidate.TeamId == teamId, ct);
        if (team is null) return SkillHubResult<WorkspaceWithPermDto>.NotFound("团队不存在");
        if (string.IsNullOrWhiteSpace(draft.WorkspaceId)) return SkillHubResult<WorkspaceWithPermDto>.BadRequest("WorkspaceId 不能为空。");
        if (string.IsNullOrWhiteSpace(draft.Name)) return SkillHubResult<WorkspaceWithPermDto>.BadRequest("工作区名称不能为空。");
        if (await db.Workspaces.AnyAsync(workspace => workspace.WorkspaceId == draft.WorkspaceId, ct))
            return SkillHubResult<WorkspaceWithPermDto>.Conflict($"WorkspaceId '{draft.WorkspaceId}' 已存在");
        if (!TryParseAccessLevel(draft.TeamAccessPolicy, out var teamPolicy))
            return SkillHubResult<WorkspaceWithPermDto>.BadRequest("TeamAccessPolicy 无效");
        if (!TryParseAccessLevel(draft.CompanyAccessPolicy, out var companyPolicy))
            return SkillHubResult<WorkspaceWithPermDto>.BadRequest("CompanyAccessPolicy 无效");

        var workspace = new WorkspaceEntity
        {
            WorkspaceId = draft.WorkspaceId.Trim(),
            Slug = draft.WorkspaceId.Trim(),
            TeamEntityId = team.Id,
            Name = draft.Name.Trim(),
            Description = draft.Description,
            UserProfile = draft.UserProfile,
            TeamAccessPolicy = teamPolicy,
            CompanyAccessPolicy = companyPolicy,
            IsEnabled = draft.IsEnabled,
        };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(ct);
        workspace.Team = team;
        return SkillHubResult<WorkspaceWithPermDto>.Ok(ToDto(workspace));
    }

    public async Task<SkillHubResult<WorkspaceWithPermDto>> UpdateWorkspaceAsync(
        string workspaceId, TeamWorkspaceDraft draft, CancellationToken ct = default)
    {
        var workspace = await TrackedWorkspaces().Include(candidate => candidate.Team).Include(candidate => candidate.Members)
            .FirstOrDefaultAsync(candidate => candidate.WorkspaceId == workspaceId, ct);
        if (workspace is null) return SkillHubResult<WorkspaceWithPermDto>.NotFound($"工作区 '{workspaceId}' 不存在");
        if (string.IsNullOrWhiteSpace(draft.Name)) return SkillHubResult<WorkspaceWithPermDto>.BadRequest("工作区名称不能为空。");
        if (!TryParseAccessLevel(draft.TeamAccessPolicy, out var teamPolicy))
            return SkillHubResult<WorkspaceWithPermDto>.BadRequest("TeamAccessPolicy 无效");
        if (!TryParseAccessLevel(draft.CompanyAccessPolicy, out var companyPolicy))
            return SkillHubResult<WorkspaceWithPermDto>.BadRequest("CompanyAccessPolicy 无效");

        workspace.Name = draft.Name.Trim();
        workspace.Description = draft.Description;
        workspace.UserProfile = draft.UserProfile;
        workspace.TeamAccessPolicy = teamPolicy;
        workspace.CompanyAccessPolicy = companyPolicy;
        workspace.IsEnabled = draft.IsEnabled;
        workspace.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return SkillHubResult<WorkspaceWithPermDto>.Ok(ToDto(workspace));
    }

    /// <summary>The built-in default workspace stays protected here too (this path previously had no guard).</summary>
    public async Task<SkillHubResult<WorkspaceWithPermDto>> DeleteWorkspaceAsync(string workspaceId, CancellationToken ct = default)
    {
        var workspace = await TrackedWorkspaces().FirstOrDefaultAsync(candidate => candidate.WorkspaceId == workspaceId, ct);
        if (workspace is null) return SkillHubResult<WorkspaceWithPermDto>.NotFound($"工作区 '{workspaceId}' 不存在");
        if (workspace.WorkspaceId == "default")
            return SkillHubResult<WorkspaceWithPermDto>.BadRequest("不能删除内置默认工作空间");

        db.Workspaces.Remove(workspace);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<WorkspaceWithPermDto>.Ok(ToDto(workspace));
    }

    // ── 工作区成员（白名单）───────────────────────────────────────────

    public async Task<SkillHubResult<List<WorkspaceMemberDto>>> ListWorkspaceMembersAsync(
        string workspaceId, CancellationToken ct = default)
    {
        var workspace = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.WorkspaceId == workspaceId, ct);
        if (workspace is null) return SkillHubResult<List<WorkspaceMemberDto>>.NotFound($"工作区 '{workspaceId}' 不存在");

        var members = await db.WorkspaceMembers.AsNoTracking().Include(member => member.User)
            .Where(member => member.WorkspaceEntityId == workspace.Id).ToListAsync(ct);
        return SkillHubResult<List<WorkspaceMemberDto>>.Ok(members.Select(ToDto).ToList());
    }

    /// <summary>None is not a usable access level for a whitelist entry; Core rejects it outright.</summary>
    public async Task<SkillHubResult<WorkspaceMemberDto>> AddWorkspaceMemberAsync(
        string workspaceId, string userId, string accessLevel, CancellationToken ct = default)
    {
        var workspace = await db.Workspaces.FirstOrDefaultAsync(candidate => candidate.WorkspaceId == workspaceId, ct);
        if (workspace is null) return SkillHubResult<WorkspaceMemberDto>.NotFound("工作区不存在");

        var user = await db.AppUsers.FirstOrDefaultAsync(candidate => candidate.UserId == userId, ct);
        if (user is null) return SkillHubResult<WorkspaceMemberDto>.NotFound("用户不存在");
        if (!TryParseAccessLevel(accessLevel, out var level))
            return SkillHubResult<WorkspaceMemberDto>.BadRequest("AccessLevel 无效");
        if (level == WorkspaceAccessPolicy.None)
            return SkillHubResult<WorkspaceMemberDto>.BadRequest("AccessLevel 不能为 None");
        if (await db.WorkspaceMembers.AnyAsync(
                member => member.WorkspaceEntityId == workspace.Id && member.UserEntityId == user.Id, ct))
            return SkillHubResult<WorkspaceMemberDto>.Conflict("用户已在工作区白名单中");

        var entity = new WorkspaceMemberEntity
        {
            WorkspaceEntityId = workspace.Id,
            UserEntityId = user.Id,
            AccessLevel = level,
        };
        db.WorkspaceMembers.Add(entity);
        await db.SaveChangesAsync(ct);
        entity.User = user;
        return SkillHubResult<WorkspaceMemberDto>.Ok(ToDto(entity));
    }

    /// <summary>A member row id from another workspace is NotFound here, never a cross-workspace delete.</summary>
    public async Task<SkillHubResult<WorkspaceMemberDto>> RemoveWorkspaceMemberAsync(
        string workspaceId, int memberId, CancellationToken ct = default)
    {
        var member = await db.WorkspaceMembers.Include(candidate => candidate.Workspace)
            .Include(candidate => candidate.User)
            .FirstOrDefaultAsync(candidate => candidate.Id == memberId
                && candidate.Workspace.WorkspaceId == workspaceId, ct);
        if (member is null)
            return SkillHubResult<WorkspaceMemberDto>.NotFound($"成员 {memberId} 不在工作区 '{workspaceId}' 内");

        db.WorkspaceMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<WorkspaceMemberDto>.Ok(ToDto(member));
    }

    public static bool TryParseMemberRole(string? value, out TeamMemberRole role) =>
        Enum.TryParse(value, ignoreCase: true, out role) && Enum.IsDefined(role);

    public static bool TryParseAccessLevel(string? value, out WorkspaceAccessPolicy policy) =>
        Enum.TryParse(value, ignoreCase: true, out policy) && Enum.IsDefined(policy);

    private IQueryable<TeamEntity> TrackedTeams() => db.Teams;

    private IQueryable<WorkspaceEntity> TrackedWorkspaces() => db.Workspaces;

    private static TeamDto ToDto(TeamEntity team) => new(
        team.Id, team.TeamId, team.Name, team.Description, team.IsEnabled,
        team.Members.Count, team.Workspaces.Count, team.CreatedAt);

    private static TeamDetailDto ToDetailDto(TeamEntity team) => new(
        team.Id, team.TeamId, team.Name, team.Description, team.IsEnabled, team.CreatedAt,
        team.Members.Select(ToDto).ToList(), team.Workspaces.Select(ToDto).ToList());

    private static TeamMemberDto ToDto(TeamMemberEntity member) => new(
        member.User?.UserId ?? member.UserEntityId.ToString(),
        member.User?.Username ?? "", member.User?.DisplayName, member.Role.ToString());

    private static WorkspaceWithPermDto ToDto(WorkspaceEntity workspace) => new(
        workspace.Id, workspace.WorkspaceId, workspace.Slug,
        workspace.Team?.TeamId ?? workspace.TeamEntityId.ToString(), workspace.Team?.Name ?? "",
        workspace.Name, workspace.Description,
        workspace.TeamAccessPolicy.ToString(), workspace.CompanyAccessPolicy.ToString(),
        workspace.IsEnabled, workspace.IsFrozen, workspace.Members.Count, workspace.CreatedAt, workspace.UserProfile);

    private static WorkspaceMemberDto ToDto(WorkspaceMemberEntity member) => new(
        member.Id, member.User?.UserId ?? member.UserEntityId.ToString(),
        member.User?.Username ?? "", member.User?.DisplayName, member.AccessLevel.ToString());
}
