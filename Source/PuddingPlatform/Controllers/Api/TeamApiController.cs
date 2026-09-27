using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Skills;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>团队与工作区管理 HTTP 出口。逻辑在 TeamService（Web 与原生客户端共用）。</summary>
[Authorize]
[ApiController]
[Route("api/teams")]
public class TeamApiController(TeamService teams) : ControllerBase
{
    // ══ TEAM ══════════════════════════════════════════════════

    [HttpGet]
    public async Task<ActionResult<List<TeamDto>>> List(CancellationToken ct)
        => Ok(await teams.ListAsync(ct));

    [HttpGet("{teamId}")]
    public async Task<ActionResult<TeamDetailDto>> Get(string teamId, CancellationToken ct)
        => await teams.GetAsync(teamId, ct) is { } team ? Ok(team) : NotFound();

    [HttpPost]
    public async Task<ActionResult<TeamDto>> Create([FromBody] UpsertTeamRequest req, CancellationToken ct)
    {
        var result = await teams.CreateAsync(new TeamDraft(req.TeamId, req.Name, req.Description, req.IsEnabled), ct);
        return result.IsOk
            ? CreatedAtAction(nameof(Get), new { teamId = result.Value!.TeamId }, result.Value)
            : Problem(result);
    }

    [HttpPut("{teamId}")]
    public async Task<ActionResult<TeamDto>> Update(string teamId, [FromBody] UpsertTeamRequest req, CancellationToken ct)
    {
        var result = await teams.UpdateAsync(teamId, new TeamDraft(req.TeamId, req.Name, req.Description, req.IsEnabled), ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    [HttpDelete("{teamId}")]
    public async Task<IActionResult> Delete(string teamId, CancellationToken ct)
    {
        var result = await teams.DeleteAsync(teamId, ct);
        return result.IsOk ? NoContent() : Problem(result);
    }

    // ══ TEAM MEMBERS ══════════════════════════════════════════

    [HttpGet("{teamId}/members")]
    public async Task<ActionResult<List<TeamMemberDto>>> ListMembers(string teamId, CancellationToken ct)
    {
        var result = await teams.ListMembersAsync(teamId, ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    [HttpPost("{teamId}/members")]
    public async Task<ActionResult<TeamMemberDto>> AddMember(
        string teamId, [FromBody] AddTeamMemberRequest req, CancellationToken ct)
    {
        var result = await teams.AddMemberAsync(teamId, req.UserId, req.Role, ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    [HttpDelete("{teamId}/members/{userId}")]
    public async Task<IActionResult> RemoveMember(string teamId, string userId, CancellationToken ct)
    {
        var result = await teams.RemoveMemberAsync(teamId, userId, ct);
        return result.IsOk ? NoContent() : Problem(result);
    }

    // ══ WORKSPACES ════════════════════════════════════════════

    [HttpGet("{teamId}/workspaces")]
    public async Task<ActionResult<List<WorkspaceWithPermDto>>> ListWorkspaces(string teamId, CancellationToken ct)
    {
        var result = await teams.ListWorkspacesAsync(teamId, ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    [HttpPost("{teamId}/workspaces")]
    public async Task<ActionResult<WorkspaceWithPermDto>> CreateWorkspace(
        string teamId, [FromBody] CreateWorkspaceRequest req, CancellationToken ct)
    {
        var result = await teams.CreateWorkspaceAsync(teamId, new TeamWorkspaceDraft(
            req.WorkspaceId, req.Name, req.Description, req.UserProfile,
            req.TeamAccessPolicy, req.CompanyAccessPolicy, true), ct);
        return result.IsOk
            ? CreatedAtAction(nameof(GetWorkspace), new { workspaceId = result.Value!.WorkspaceId }, result.Value)
            : Problem(result);
    }

    [HttpGet("workspaces/{workspaceId}")]
    public async Task<ActionResult<WorkspaceWithPermDto>> GetWorkspace(string workspaceId, CancellationToken ct)
        => await teams.FindWorkspaceAsync(workspaceId, ct) is { } workspace ? Ok(workspace) : NotFound();

    [HttpPut("workspaces/{workspaceId}")]
    public async Task<ActionResult<WorkspaceWithPermDto>> UpdateWorkspace(
        string workspaceId, [FromBody] UpdateWorkspaceRequest req, CancellationToken ct)
    {
        var result = await teams.UpdateWorkspaceAsync(workspaceId, new TeamWorkspaceDraft(
            workspaceId, req.Name, req.Description, req.UserProfile,
            req.TeamAccessPolicy, req.CompanyAccessPolicy, req.IsEnabled), ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    [HttpDelete("workspaces/{workspaceId}")]
    public async Task<IActionResult> DeleteWorkspace(string workspaceId, CancellationToken ct)
    {
        var result = await teams.DeleteWorkspaceAsync(workspaceId, ct);
        return result.IsOk ? NoContent() : Problem(result);
    }

    // ── 工作区成员（白名单）──────────────────────────────────

    [HttpGet("workspaces/{workspaceId}/members")]
    public async Task<ActionResult<List<WorkspaceMemberDto>>> ListWorkspaceMembers(string workspaceId, CancellationToken ct)
    {
        var result = await teams.ListWorkspaceMembersAsync(workspaceId, ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    [HttpPost("workspaces/{workspaceId}/members")]
    public async Task<ActionResult<WorkspaceMemberDto>> AddWorkspaceMember(
        string workspaceId, [FromBody] AddWorkspaceMemberRequest req, CancellationToken ct)
    {
        var result = await teams.AddWorkspaceMemberAsync(workspaceId, req.UserId, req.AccessLevel, ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    [HttpDelete("workspaces/{workspaceId}/members/{id:int}")]
    public async Task<IActionResult> RemoveWorkspaceMember(string workspaceId, int id, CancellationToken ct)
    {
        var result = await teams.RemoveWorkspaceMemberAsync(workspaceId, id, ct);
        return result.IsOk ? NoContent() : Problem(result);
    }

    private ActionResult Problem<T>(SkillHubResult<T> result) where T : class =>
        result.Status switch
        {
            SkillHubStatus.BadRequest => BadRequest(new { message = result.Error }),
            SkillHubStatus.Conflict => Conflict(new { message = result.Error }),
            _ => NotFound(new { message = result.Error })
        };
}
