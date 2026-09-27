using Microsoft.EntityFrameworkCore;
using PuddingCode.Skills;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

public sealed record WorkspaceDraft(
    string WorkspaceId, string TeamId, string Name, string? Description,
    string TeamAccessPolicy, string CompanyAccessPolicy, string? UserProfile);

public sealed record WorkspaceEdit(
    string Name, string? Description,
    string TeamAccessPolicy, string CompanyAccessPolicy, bool IsEnabled, string? UserProfile);

/// <summary>
/// 工作区与成员应用操作——从 WorkspaceApiController 原位下沉（该控制器原先直接使用 DbContext）。
/// Web 与原生客户端共用同一份校验与语义结果；控制器只做 HTTP 映射。
/// </summary>
public sealed class WorkspaceService(PlatformDbContext db)
{
    public async Task<List<WorkspaceWithPermDto>> ListAsync(CancellationToken ct = default)
        => (await db.Workspaces.AsNoTracking().Include(w => w.Team).Include(w => w.Members)
            .OrderBy(w => w.Id).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<WorkspaceWithPermDto?> GetAsync(string workspaceId, CancellationToken ct = default)
    {
        var workspace = await FindWithTeamAsync(workspaceId, ct);
        return workspace is null ? null : ToDto(workspace);
    }

    public async Task<SkillHubResult<WorkspaceWithPermDto>> CreateAsync(WorkspaceDraft draft, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(draft.WorkspaceId)) return SkillHubResult<WorkspaceWithPermDto>.BadRequest("WorkspaceId 不能为空");
        if (string.IsNullOrWhiteSpace(draft.Name)) return SkillHubResult<WorkspaceWithPermDto>.BadRequest("工作区名称不能为空");
        if (await db.Workspaces.AnyAsync(w => w.WorkspaceId == draft.WorkspaceId, ct))
            return SkillHubResult<WorkspaceWithPermDto>.Conflict($"WorkspaceId '{draft.WorkspaceId}' 已存在");

        var team = await db.Teams.FirstOrDefaultAsync(t => t.TeamId == draft.TeamId, ct);
        if (team is null) return SkillHubResult<WorkspaceWithPermDto>.BadRequest($"Team '{draft.TeamId}' 不存在");

        if (!TryParsePolicy(draft.TeamAccessPolicy, out var teamPolicy))
            return SkillHubResult<WorkspaceWithPermDto>.BadRequest("TeamAccessPolicy 无效");
        if (!TryParsePolicy(draft.CompanyAccessPolicy, out var companyPolicy))
            return SkillHubResult<WorkspaceWithPermDto>.BadRequest("CompanyAccessPolicy 无效");

        var entity = new WorkspaceEntity
        {
            WorkspaceId = draft.WorkspaceId,
            Slug = draft.WorkspaceId,
            TeamEntityId = team.Id,
            Name = draft.Name,
            Description = draft.Description,
            UserProfile = draft.UserProfile,
            TeamAccessPolicy = teamPolicy,
            CompanyAccessPolicy = companyPolicy,
            IsEnabled = true,
            IsFrozen = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Workspaces.Add(entity);
        await db.SaveChangesAsync(ct);
        entity.Team = team;
        return SkillHubResult<WorkspaceWithPermDto>.Ok(ToDto(entity));
    }

    public async Task<SkillHubResult<WorkspaceWithPermDto>> UpdateAsync(string workspaceId, WorkspaceEdit edit, CancellationToken ct = default)
    {
        var workspace = await FindWithMembersAsync(workspaceId, ct);
        if (workspace is null) return SkillHubResult<WorkspaceWithPermDto>.NotFound($"工作区 '{workspaceId}' 不存在");
        if (string.IsNullOrWhiteSpace(edit.Name)) return SkillHubResult<WorkspaceWithPermDto>.BadRequest("工作区名称不能为空");
        if (!TryParsePolicy(edit.TeamAccessPolicy, out var teamPolicy))
            return SkillHubResult<WorkspaceWithPermDto>.BadRequest("TeamAccessPolicy 无效");
        if (!TryParsePolicy(edit.CompanyAccessPolicy, out var companyPolicy))
            return SkillHubResult<WorkspaceWithPermDto>.BadRequest("CompanyAccessPolicy 无效");

        workspace.Name = edit.Name;
        workspace.Description = edit.Description;
        workspace.UserProfile = edit.UserProfile;
        workspace.TeamAccessPolicy = teamPolicy;
        workspace.CompanyAccessPolicy = companyPolicy;
        workspace.IsEnabled = edit.IsEnabled;
        workspace.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return SkillHubResult<WorkspaceWithPermDto>.Ok(ToDto(workspace));
    }

    /// <summary>The built-in default workspace is never deletable.</summary>
    public async Task<SkillHubResult<WorkspaceWithPermDto>> DeleteAsync(string workspaceId, CancellationToken ct = default)
    {
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (workspace is null) return SkillHubResult<WorkspaceWithPermDto>.NotFound($"工作区 '{workspaceId}' 不存在");
        if (workspace.WorkspaceId == "default")
            return SkillHubResult<WorkspaceWithPermDto>.BadRequest("不能删除内置默认工作空间");

        db.Workspaces.Remove(workspace);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<WorkspaceWithPermDto>.Ok(ToDto(workspace));
    }

    public async Task<SkillHubResult<WorkspaceWithPermDto>> SetFrozenAsync(string workspaceId, bool frozen, CancellationToken ct = default)
    {
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (workspace is null) return SkillHubResult<WorkspaceWithPermDto>.NotFound($"工作区 '{workspaceId}' 不存在");
        workspace.IsFrozen = frozen;
        workspace.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return SkillHubResult<WorkspaceWithPermDto>.Ok(ToDto(workspace));
    }

    public async Task<SkillHubResult<List<WorkspaceMemberDto>>> ListMembersAsync(string workspaceId, CancellationToken ct = default)
    {
        var workspace = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (workspace is null) return SkillHubResult<List<WorkspaceMemberDto>>.NotFound($"工作区 '{workspaceId}' 不存在");

        var members = await db.WorkspaceMembers.AsNoTracking()
            .Where(member => member.WorkspaceEntityId == workspace.Id)
            .Include(member => member.User)
            .OrderBy(member => member.Id)
            .ToListAsync(ct);
        return SkillHubResult<List<WorkspaceMemberDto>>.Ok(members.Select(member => new WorkspaceMemberDto(
            member.Id, member.User.UserId, member.User.Username, member.User.DisplayName,
            member.AccessLevel.ToString())).ToList());
    }

    public async Task<SkillHubResult<WorkspaceMemberDto>> AddMemberAsync(
        string workspaceId, string userId, string accessLevel, CancellationToken ct = default)
    {
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (workspace is null) return SkillHubResult<WorkspaceMemberDto>.NotFound($"工作区 '{workspaceId}' 不存在");
        if (string.IsNullOrWhiteSpace(userId)) return SkillHubResult<WorkspaceMemberDto>.BadRequest("UserId 不能为空");

        var user = await db.AppUsers.FirstOrDefaultAsync(candidate => candidate.UserId == userId, ct);
        if (user is null) return SkillHubResult<WorkspaceMemberDto>.BadRequest($"用户 '{userId}' 不存在");
        if (!TryParsePolicy(accessLevel, out var level))
            return SkillHubResult<WorkspaceMemberDto>.BadRequest("AccessLevel 无效");

        if (await db.WorkspaceMembers.AnyAsync(
                member => member.WorkspaceEntityId == workspace.Id && member.UserEntityId == user.Id, ct))
            return SkillHubResult<WorkspaceMemberDto>.Conflict("该用户已是工作空间成员");

        var entity = new WorkspaceMemberEntity
        {
            WorkspaceEntityId = workspace.Id,
            UserEntityId = user.Id,
            AccessLevel = level,
            AddedAt = DateTimeOffset.UtcNow,
        };
        db.WorkspaceMembers.Add(entity);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<WorkspaceMemberDto>.Ok(new WorkspaceMemberDto(
            entity.Id, user.UserId, user.Username, user.DisplayName, entity.AccessLevel.ToString()));
    }

    /// <summary>A member id from another workspace is "not found" here, never a cross-workspace delete.</summary>
    public async Task<SkillHubResult<WorkspaceMemberDto>> RemoveMemberAsync(
        string workspaceId, int memberId, CancellationToken ct = default)
    {
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (workspace is null) return SkillHubResult<WorkspaceMemberDto>.NotFound($"工作区 '{workspaceId}' 不存在");

        var member = await db.WorkspaceMembers
            .FirstOrDefaultAsync(candidate => candidate.Id == memberId && candidate.WorkspaceEntityId == workspace.Id, ct);
        if (member is null) return SkillHubResult<WorkspaceMemberDto>.NotFound($"成员 {memberId} 不在工作区 '{workspaceId}' 内");

        db.WorkspaceMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<WorkspaceMemberDto>.Ok(new WorkspaceMemberDto(
            member.Id, "", "", null, member.AccessLevel.ToString()));
    }

    /// <summary>Access-policy vocabulary shared by workspace policies and member levels.</summary>
    public static IReadOnlyList<string> AccessLevels { get; } = Enum.GetNames<WorkspaceAccessPolicy>();

    public static bool TryParsePolicy(string? value, out WorkspaceAccessPolicy policy) =>
        Enum.TryParse(value, ignoreCase: true, out policy) && Enum.IsDefined(policy);

    private Task<WorkspaceEntity?> FindWithTeamAsync(string workspaceId, CancellationToken ct) =>
        db.Workspaces.AsNoTracking().Include(w => w.Team).Include(w => w.Members)
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);

    private Task<WorkspaceEntity?> FindWithMembersAsync(string workspaceId, CancellationToken ct) =>
        db.Workspaces.Include(w => w.Team).Include(w => w.Members)
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);

    private static WorkspaceWithPermDto ToDto(WorkspaceEntity workspace) => new(
        workspace.Id, workspace.WorkspaceId, workspace.Slug,
        workspace.Team?.TeamId ?? workspace.TeamEntityId.ToString(),
        workspace.Team?.Name ?? string.Empty,
        workspace.Name, workspace.Description,
        workspace.TeamAccessPolicy.ToString(), workspace.CompanyAccessPolicy.ToString(),
        workspace.IsEnabled, workspace.IsFrozen, workspace.Members?.Count ?? 0,
        workspace.CreatedAt, workspace.UserProfile);
}
