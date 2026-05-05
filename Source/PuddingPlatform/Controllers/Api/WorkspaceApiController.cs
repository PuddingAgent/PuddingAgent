using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using System.Security.Claims;

namespace PuddingPlatform.Controllers.Api;

/// <summary>Workspace 全局管理 API（供 Admin SPA 调用，直接操作 PlatformDbContext）。</summary>
[Authorize]
[ApiController]
[Route("api/workspaces")]
public class WorkspaceApiController(PlatformDbContext db) : ControllerBase
{
    // GET /api/workspaces — 跨团队全局列表
    [HttpGet]
    public async Task<ActionResult<List<WorkspaceWithPermDto>>> List(CancellationToken ct)
    {
        var list = await db.Workspaces
            .AsNoTracking()
            .Include(w => w.Team)
            .Include(w => w.Members)
            .OrderBy(w => w.Id)
            .ToListAsync(ct);

        return Ok(list.Select(ToDto).ToList());
    }

    // GET /api/workspaces/{id}
    [HttpGet("{id}")]
    public async Task<ActionResult<WorkspaceWithPermDto>> Get(string id, CancellationToken ct)
    {
        var ws = await db.Workspaces
            .AsNoTracking()
            .Include(w => w.Team)
            .Include(w => w.Members)
            .FirstOrDefaultAsync(w => w.WorkspaceId == id, ct);

        return ws is null ? NotFound() : Ok(ToDto(ws));
    }

    // POST /api/workspaces
    [HttpPost]
    public async Task<ActionResult<WorkspaceWithPermDto>> Create(
        [FromBody] CreateWorkspaceRequest req, CancellationToken ct)
    {
        if (await db.Workspaces.AnyAsync(w => w.WorkspaceId == req.WorkspaceId, ct))
            return Conflict(new { message = $"WorkspaceId '{req.WorkspaceId}' 已存在" });

        var team = await db.Teams.FirstOrDefaultAsync(t => t.TeamId == req.TeamId, ct);
        if (team is null)
            return BadRequest(new { message = $"Team '{req.TeamId}' 不存在" });

        if (!Enum.TryParse<WorkspaceAccessPolicy>(req.TeamAccessPolicy, ignoreCase: true, out var tap))
            return BadRequest(new { message = "TeamAccessPolicy 无效" });

        if (!Enum.TryParse<WorkspaceAccessPolicy>(req.CompanyAccessPolicy, ignoreCase: true, out var cap))
            return BadRequest(new { message = "CompanyAccessPolicy 无效" });

        var entity = new WorkspaceEntity
        {
            WorkspaceId         = req.WorkspaceId,
            Slug                = req.WorkspaceId,
            TeamEntityId        = team.Id,
            Name                = req.Name,
            Description         = req.Description,
            UserProfile         = req.UserProfile,
            TeamAccessPolicy    = tap,
            CompanyAccessPolicy = cap,
            IsEnabled           = true,
            IsFrozen            = false,
            CreatedAt           = DateTimeOffset.UtcNow,
            UpdatedAt           = DateTimeOffset.UtcNow,
        };
        db.Workspaces.Add(entity);
        await db.SaveChangesAsync(ct);

        entity.Team = team;
        return CreatedAtAction(nameof(Get), new { id = entity.WorkspaceId }, ToDto(entity));
    }

    // PUT /api/workspaces/{id}
    [HttpPut("{id}")]
    public async Task<ActionResult<WorkspaceWithPermDto>> Update(
        string id, [FromBody] UpdateWorkspaceRequest req, CancellationToken ct)
    {
        var ws = await db.Workspaces
            .Include(w => w.Team)
            .Include(w => w.Members)
            .FirstOrDefaultAsync(w => w.WorkspaceId == id, ct);
        if (ws is null) return NotFound();

        if (!Enum.TryParse<WorkspaceAccessPolicy>(req.TeamAccessPolicy, ignoreCase: true, out var tap))
            return BadRequest(new { message = "TeamAccessPolicy 无效" });

        if (!Enum.TryParse<WorkspaceAccessPolicy>(req.CompanyAccessPolicy, ignoreCase: true, out var cap))
            return BadRequest(new { message = "CompanyAccessPolicy 无效" });

        ws.Name                = req.Name;
        ws.Description         = req.Description;
        ws.UserProfile         = req.UserProfile;
        ws.TeamAccessPolicy    = tap;
        ws.CompanyAccessPolicy = cap;
        ws.IsEnabled           = req.IsEnabled;
        ws.UpdatedAt           = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(ToDto(ws));
    }

    // DELETE /api/workspaces/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == id, ct);
        if (ws is null) return NotFound();

        if (ws.WorkspaceId == "default")
            return BadRequest(new { message = "不能删除内置默认工作空间" });

        db.Workspaces.Remove(ws);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // POST /api/workspaces/{id}/freeze
    [HttpPost("{id}/freeze")]
    public async Task<IActionResult> Freeze(string id, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == id, ct);
        if (ws is null) return NotFound();

        ws.IsFrozen  = true;
        ws.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    // POST /api/workspaces/{id}/unfreeze
    [HttpPost("{id}/unfreeze")]
    public async Task<IActionResult> Unfreeze(string id, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == id, ct);
        if (ws is null) return NotFound();

        ws.IsFrozen  = false;
        ws.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    // ──────────────────────────── Member 管理 ────────────────────────────

    // GET /api/workspaces/{id}/members
    [HttpGet("{id}/members")]
    public async Task<ActionResult<List<WorkspaceMemberDto>>> ListMembers(string id, CancellationToken ct)
    {
        var ws = await db.Workspaces
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == id, ct);
        if (ws is null) return NotFound();

        var members = await db.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.WorkspaceEntityId == ws.Id)
            .Include(m => m.User)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

        return Ok(members.Select(m => new WorkspaceMemberDto(
            m.Id,
            m.User.UserId,
            m.User.Username,
            m.User.DisplayName,
            m.AccessLevel.ToString())).ToList());
    }

    // POST /api/workspaces/{id}/members
    [HttpPost("{id}/members")]
    public async Task<ActionResult<WorkspaceMemberDto>> AddMember(
        string id, [FromBody] AddWorkspaceMemberRequest req, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == id, ct);
        if (ws is null) return NotFound();

        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == req.UserId, ct);
        if (user is null) return BadRequest(new { message = $"用户 '{req.UserId}' 不存在" });

        if (!Enum.TryParse<WorkspaceAccessPolicy>(req.AccessLevel, ignoreCase: true, out var level))
            return BadRequest(new { message = "AccessLevel 无效" });

        if (await db.WorkspaceMembers.AnyAsync(
                m => m.WorkspaceEntityId == ws.Id && m.UserEntityId == user.Id, ct))
            return Conflict(new { message = "该用户已是工作空间成员" });

        var entity = new WorkspaceMemberEntity
        {
            WorkspaceEntityId = ws.Id,
            UserEntityId      = user.Id,
            AccessLevel       = level,
            AddedAt           = DateTimeOffset.UtcNow,
        };
        db.WorkspaceMembers.Add(entity);
        await db.SaveChangesAsync(ct);

        return Ok(new WorkspaceMemberDto(
            entity.Id, user.UserId, user.Username, user.DisplayName, entity.AccessLevel.ToString()));
    }

    // DELETE /api/workspaces/{id}/members/{memberId}
    [HttpDelete("{id}/members/{memberId:int}")]
    public async Task<IActionResult> RemoveMember(string id, int memberId, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == id, ct);
        if (ws is null) return NotFound();

        var m = await db.WorkspaceMembers
            .FirstOrDefaultAsync(m => m.Id == memberId && m.WorkspaceEntityId == ws.Id, ct);
        if (m is null) return NotFound();

        db.WorkspaceMembers.Remove(m);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static WorkspaceWithPermDto ToDto(WorkspaceEntity w) => new(
        w.Id,
        w.WorkspaceId,
        w.Slug,
        w.Team?.TeamId  ?? w.TeamEntityId.ToString(),
        w.Team?.Name    ?? string.Empty,
        w.Name,
        w.Description,
        w.TeamAccessPolicy.ToString(),
        w.CompanyAccessPolicy.ToString(),
        w.IsEnabled,
        w.IsFrozen,
        w.Members?.Count ?? 0,
        w.CreatedAt,
        w.UserProfile);
}
