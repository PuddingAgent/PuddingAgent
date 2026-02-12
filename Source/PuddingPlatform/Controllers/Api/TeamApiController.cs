using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>团队与工作区管理 API</summary>
[Authorize]
[ApiController]
[Route("api/teams")]
public class TeamApiController(PlatformDbContext db) : ControllerBase
{
    // ══ TEAM ══════════════════════════════════════════════════

    // ── 团队列表 ──────────────────────────────────────────────
    [HttpGet]
    public async Task<ActionResult<List<TeamDto>>> List(CancellationToken ct)
    {
        var teams = await db.Teams
            .AsNoTracking()
            .Include(t => t.Members)
            .Include(t => t.Workspaces)
            .OrderBy(t => t.Id)
            .ToListAsync(ct);

        return Ok(teams.Select(MapTeamToDto).ToList());
    }

    // ── 团队详情（含成员 + 工作区）────────────────────────────
    [HttpGet("{teamId}")]
    public async Task<ActionResult<TeamDetailDto>> Get(string teamId, CancellationToken ct)
    {
        var team = await db.Teams
            .AsNoTracking()
            .Include(t => t.Members).ThenInclude(m => m.User)
            .Include(t => t.Workspaces).ThenInclude(w => w.Members)
            .FirstOrDefaultAsync(t => t.TeamId == teamId, ct);

        if (team is null) return NotFound();
        return Ok(MapTeamDetailToDto(team));
    }

    // ── 创建团队 ──────────────────────────────────────────────
    [HttpPost]
    public async Task<ActionResult<TeamDto>> Create(
        [FromBody] UpsertTeamRequest req, CancellationToken ct)
    {
        if (await db.Teams.AnyAsync(t => t.TeamId == req.TeamId, ct))
            return Conflict(new { message = $"TeamId '{req.TeamId}' 已存在" });

        var team = new TeamEntity
        {
            TeamId = req.TeamId,
            Name = req.Name,
            Description = req.Description,
            IsEnabled = req.IsEnabled,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { teamId = team.TeamId }, MapTeamToDto(team));
    }

    // ── 更新团队 ──────────────────────────────────────────────
    [HttpPut("{teamId}")]
    public async Task<ActionResult<TeamDto>> Update(
        string teamId, [FromBody] UpsertTeamRequest req, CancellationToken ct)
    {
        var team = await db.Teams
            .Include(t => t.Members)
            .Include(t => t.Workspaces)
            .FirstOrDefaultAsync(t => t.TeamId == teamId, ct);
        if (team is null) return NotFound();

        team.Name = req.Name;
        team.Description = req.Description;
        team.IsEnabled = req.IsEnabled;
        team.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(MapTeamToDto(team));
    }

    // ── 删除团队 ──────────────────────────────────────────────
    [HttpDelete("{teamId}")]
    public async Task<IActionResult> Delete(string teamId, CancellationToken ct)
    {
        var team = await db.Teams
            .Include(t => t.Workspaces)
            .FirstOrDefaultAsync(t => t.TeamId == teamId, ct);
        if (team is null) return NotFound();

        if (team.Workspaces.Any())
            return BadRequest(new { message = "请先删除团队下所有工作区" });

        db.Teams.Remove(team);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ══ TEAM MEMBERS ══════════════════════════════════════════

    // ── 成员列表 ──────────────────────────────────────────────
    [HttpGet("{teamId}/members")]
    public async Task<ActionResult<List<TeamMemberDto>>> ListMembers(
        string teamId, CancellationToken ct)
    {
        var team = await db.Teams.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TeamId == teamId, ct);
        if (team is null) return NotFound();

        var members = await db.TeamMembers
            .AsNoTracking()
            .Include(m => m.User)
            .Where(m => m.TeamEntityId == team.Id)
            .ToListAsync(ct);

        return Ok(members.Select(MapMemberToDto).ToList());
    }

    // ── 添加成员 ──────────────────────────────────────────────
    [HttpPost("{teamId}/members")]
    public async Task<ActionResult<TeamMemberDto>> AddMember(
        string teamId, [FromBody] AddTeamMemberRequest req, CancellationToken ct)
    {
        var team = await db.Teams.FirstOrDefaultAsync(t => t.TeamId == teamId, ct);
        if (team is null) return NotFound(new { message = "团队不存在" });

        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == req.UserId, ct);
        if (user is null) return NotFound(new { message = "用户不存在" });

        if (!Enum.TryParse<TeamMemberRole>(req.Role, ignoreCase: true, out var role))
            return BadRequest(new { message = "Role 无效，应为 Member 或 Admin" });

        if (await db.TeamMembers.AnyAsync(
                m => m.TeamEntityId == team.Id && m.UserEntityId == user.Id, ct))
            return Conflict(new { message = "用户已是团队成员" });

        var member = new TeamMemberEntity
        {
            TeamEntityId = team.Id,
            UserEntityId = user.Id,
            Role = role,
        };
        db.TeamMembers.Add(member);
        await db.SaveChangesAsync(ct);

        member.User = user;
        return Ok(MapMemberToDto(member));
    }

    // ── 移除成员 ──────────────────────────────────────────────
    [HttpDelete("{teamId}/members/{userId}")]
    public async Task<IActionResult> RemoveMember(
        string teamId, string userId, CancellationToken ct)
    {
        var team = await db.Teams.FirstOrDefaultAsync(t => t.TeamId == teamId, ct);
        if (team is null) return NotFound();

        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return NotFound();

        var member = await db.TeamMembers.FirstOrDefaultAsync(
            m => m.TeamEntityId == team.Id && m.UserEntityId == user.Id, ct);
        if (member is null) return NotFound();

        db.TeamMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ══ WORKSPACES ════════════════════════════════════════════

    // ── 工作区列表（按团队）──────────────────────────────────
    [HttpGet("{teamId}/workspaces")]
    public async Task<ActionResult<List<WorkspaceWithPermDto>>> ListWorkspaces(
        string teamId, CancellationToken ct)
    {
        var team = await db.Teams.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TeamId == teamId, ct);
        if (team is null) return NotFound();

        var workspaces = await db.Workspaces
            .AsNoTracking()
            .Include(w => w.Team)
            .Include(w => w.Members)
            .Where(w => w.TeamEntityId == team.Id)
            .ToListAsync(ct);

        return Ok(workspaces.Select(MapWorkspaceToDto).ToList());
    }

    // ── 创建工作区 ────────────────────────────────────────────
    [HttpPost("{teamId}/workspaces")]
    public async Task<ActionResult<WorkspaceWithPermDto>> CreateWorkspace(
        string teamId, [FromBody] CreateWorkspaceRequest req, CancellationToken ct)
    {
        var team = await db.Teams
            .Include(t => t.Workspaces)
            .FirstOrDefaultAsync(t => t.TeamId == teamId, ct);
        if (team is null) return NotFound(new { message = "团队不存在" });

        if (await db.Workspaces.AnyAsync(w => w.WorkspaceId == req.WorkspaceId, ct))
            return Conflict(new { message = $"WorkspaceId '{req.WorkspaceId}' 已存在" });

        if (!Enum.TryParse<WorkspaceAccessPolicy>(req.TeamAccessPolicy, ignoreCase: true, out var tap))
            return BadRequest(new { message = "TeamAccessPolicy 无效" });

        if (!Enum.TryParse<WorkspaceAccessPolicy>(req.CompanyAccessPolicy, ignoreCase: true, out var cap))
            return BadRequest(new { message = "CompanyAccessPolicy 无效" });

        var ws = new WorkspaceEntity
        {
            WorkspaceId = req.WorkspaceId,
            Slug = req.WorkspaceId,
            TeamEntityId = team.Id,
            Name = req.Name,
            Description = req.Description,
            UserProfile = req.UserProfile,
            TeamAccessPolicy = tap,
            CompanyAccessPolicy = cap,
        };
        db.Workspaces.Add(ws);
        await db.SaveChangesAsync(ct);

        ws.Team = team;
        return CreatedAtAction(nameof(GetWorkspace),
            new { workspaceId = ws.WorkspaceId }, MapWorkspaceToDto(ws));
    }

    // ── 查询工作区 ────────────────────────────────────────────
    [HttpGet("workspaces/{workspaceId}")]
    public async Task<ActionResult<WorkspaceWithPermDto>> GetWorkspace(
        string workspaceId, CancellationToken ct)
    {
        var ws = await db.Workspaces
            .AsNoTracking()
            .Include(w => w.Team)
            .Include(w => w.Members)
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (ws is null) return NotFound();
        return Ok(MapWorkspaceToDto(ws));
    }

    // ── 更新工作区 ────────────────────────────────────────────
    [HttpPut("workspaces/{workspaceId}")]
    public async Task<ActionResult<WorkspaceWithPermDto>> UpdateWorkspace(
        string workspaceId, [FromBody] UpdateWorkspaceRequest req, CancellationToken ct)
    {
        var ws = await db.Workspaces
            .Include(w => w.Team)
            .Include(w => w.Members)
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (ws is null) return NotFound();

        if (!Enum.TryParse<WorkspaceAccessPolicy>(req.TeamAccessPolicy, ignoreCase: true, out var tap))
            return BadRequest(new { message = "TeamAccessPolicy 无效" });

        if (!Enum.TryParse<WorkspaceAccessPolicy>(req.CompanyAccessPolicy, ignoreCase: true, out var cap))
            return BadRequest(new { message = "CompanyAccessPolicy 无效" });

        ws.Name = req.Name;
        ws.Description = req.Description;
        ws.UserProfile = req.UserProfile;
        ws.TeamAccessPolicy = tap;
        ws.CompanyAccessPolicy = cap;
        ws.IsEnabled = req.IsEnabled;
        ws.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(MapWorkspaceToDto(ws));
    }

    // ── 删除工作区 ────────────────────────────────────────────
    [HttpDelete("workspaces/{workspaceId}")]
    public async Task<IActionResult> DeleteWorkspace(string workspaceId, CancellationToken ct)
    {
        var ws = await db.Workspaces
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (ws is null) return NotFound();

        db.Workspaces.Remove(ws);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── 工作区成员列表 ────────────────────────────────────────
    [HttpGet("workspaces/{workspaceId}/members")]
    public async Task<ActionResult<List<WorkspaceMemberDto>>> ListWorkspaceMembers(
        string workspaceId, CancellationToken ct)
    {
        var ws = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (ws is null) return NotFound();

        var members = await db.WorkspaceMembers
            .AsNoTracking()
            .Include(m => m.User)
            .Where(m => m.WorkspaceEntityId == ws.Id)
            .ToListAsync(ct);

        return Ok(members.Select(MapWsMemberToDto).ToList());
    }

    // ── 添加工作区成员（白名单）──────────────────────────────
    [HttpPost("workspaces/{workspaceId}/members")]
    public async Task<ActionResult<WorkspaceMemberDto>> AddWorkspaceMember(
        string workspaceId, [FromBody] AddWorkspaceMemberRequest req, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (ws is null) return NotFound(new { message = "工作区不存在" });

        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == req.UserId, ct);
        if (user is null) return NotFound(new { message = "用户不存在" });

        if (!Enum.TryParse<WorkspaceAccessPolicy>(req.AccessLevel, ignoreCase: true, out var level))
            return BadRequest(new { message = "AccessLevel 无效" });

        if (level == WorkspaceAccessPolicy.None)
            return BadRequest(new { message = "AccessLevel 不能为 None" });

        if (await db.WorkspaceMembers.AnyAsync(
                m => m.WorkspaceEntityId == ws.Id && m.UserEntityId == user.Id, ct))
            return Conflict(new { message = "用户已在工作区白名单中" });

        var member = new WorkspaceMemberEntity
        {
            WorkspaceEntityId = ws.Id,
            UserEntityId = user.Id,
            AccessLevel = level,
        };
        db.WorkspaceMembers.Add(member);
        await db.SaveChangesAsync(ct);

        member.User = user;
        return Ok(MapWsMemberToDto(member));
    }

    // ── 移除工作区成员 ────────────────────────────────────────
    [HttpDelete("workspaces/{workspaceId}/members/{id:int}")]
    public async Task<IActionResult> RemoveWorkspaceMember(
        string workspaceId, int id, CancellationToken ct)
    {
        var member = await db.WorkspaceMembers
            .Include(m => m.Workspace)
            .FirstOrDefaultAsync(m => m.Id == id && m.Workspace.WorkspaceId == workspaceId, ct);
        if (member is null) return NotFound();

        db.WorkspaceMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────
    private static TeamDto MapTeamToDto(TeamEntity t) => new(
        t.Id, t.TeamId, t.Name, t.Description, t.IsEnabled,
        t.Members.Count, t.Workspaces.Count, t.CreatedAt);

    private static TeamDetailDto MapTeamDetailToDto(TeamEntity t) => new(
        t.Id, t.TeamId, t.Name, t.Description, t.IsEnabled, t.CreatedAt,
        t.Members.Select(MapMemberToDto).ToList(),
        t.Workspaces.Select(MapWorkspaceToDto).ToList());

    private static TeamMemberDto MapMemberToDto(TeamMemberEntity m) => new(
        m.User?.UserId ?? m.UserEntityId.ToString(),
        m.User?.Username ?? "",
        m.User?.DisplayName,
        m.Role.ToString());

    private static WorkspaceWithPermDto MapWorkspaceToDto(WorkspaceEntity w) => new(
        w.Id, w.WorkspaceId, w.Slug,
        w.Team?.TeamId ?? w.TeamEntityId.ToString(),
        w.Team?.Name ?? "",
        w.Name, w.Description,
        w.TeamAccessPolicy.ToString(), w.CompanyAccessPolicy.ToString(),
        w.IsEnabled, w.IsFrozen,
        w.Members.Count, w.CreatedAt, w.UserProfile);

    private static WorkspaceMemberDto MapWsMemberToDto(WorkspaceMemberEntity m) => new(
        m.Id,
        m.User?.UserId ?? m.UserEntityId.ToString(),
        m.User?.Username ?? "",
        m.User?.DisplayName,
        m.AccessLevel.ToString());
}
