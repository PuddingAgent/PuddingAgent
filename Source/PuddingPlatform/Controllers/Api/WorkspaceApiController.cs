using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Skills;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 工作区与成员 HTTP 出口。校验与数据访问在 WorkspaceService（Web 与原生客户端共用），
/// 控制器只把 SkillHubResult 映射为状态码。
/// </summary>
[Authorize]
[ApiController]
[Route("api/workspaces")]
public class WorkspaceApiController(WorkspaceService workspaces) : ControllerBase
{
    // GET /api/workspaces — 跨团队全局列表
    [HttpGet]
    public async Task<ActionResult<List<WorkspaceWithPermDto>>> List(CancellationToken ct)
        => Ok(await workspaces.ListAsync(ct));

    // GET /api/workspaces/{id}
    [HttpGet("{id}")]
    public async Task<ActionResult<WorkspaceWithPermDto>> Get(string id, CancellationToken ct)
        => await workspaces.GetAsync(id, ct) is { } dto ? Ok(dto) : NotFound();

    // POST /api/workspaces
    [HttpPost]
    public async Task<ActionResult<WorkspaceWithPermDto>> Create([FromBody] CreateWorkspaceRequest req, CancellationToken ct)
    {
        var result = await workspaces.CreateAsync(new WorkspaceDraft(
            req.WorkspaceId, req.TeamId, req.Name, req.Description,
            req.TeamAccessPolicy, req.CompanyAccessPolicy, req.UserProfile), ct);
        return result.IsOk
            ? CreatedAtAction(nameof(Get), new { id = result.Value!.WorkspaceId }, result.Value)
            : Problem(result);
    }

    // PUT /api/workspaces/{id}
    [HttpPut("{id}")]
    public async Task<ActionResult<WorkspaceWithPermDto>> Update(
        string id, [FromBody] UpdateWorkspaceRequest req, CancellationToken ct)
    {
        var result = await workspaces.UpdateAsync(id, new WorkspaceEdit(
            req.Name, req.Description, req.TeamAccessPolicy, req.CompanyAccessPolicy, req.IsEnabled, req.UserProfile), ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    // DELETE /api/workspaces/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var result = await workspaces.DeleteAsync(id, ct);
        return result.IsOk ? NoContent() : Problem(result);
    }

    // POST /api/workspaces/{id}/freeze
    [HttpPost("{id}/freeze")]
    public async Task<IActionResult> Freeze(string id, CancellationToken ct)
    {
        var result = await workspaces.SetFrozenAsync(id, frozen: true, ct);
        return result.IsOk ? Ok() : Problem(result);
    }

    // POST /api/workspaces/{id}/unfreeze
    [HttpPost("{id}/unfreeze")]
    public async Task<IActionResult> Unfreeze(string id, CancellationToken ct)
    {
        var result = await workspaces.SetFrozenAsync(id, frozen: false, ct);
        return result.IsOk ? Ok() : Problem(result);
    }

    // ──────────────────────────── Member 管理 ────────────────────────────

    // GET /api/workspaces/{id}/members
    [HttpGet("{id}/members")]
    public async Task<ActionResult<List<WorkspaceMemberDto>>> ListMembers(string id, CancellationToken ct)
    {
        var result = await workspaces.ListMembersAsync(id, ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    // POST /api/workspaces/{id}/members
    [HttpPost("{id}/members")]
    public async Task<ActionResult<WorkspaceMemberDto>> AddMember(
        string id, [FromBody] AddWorkspaceMemberRequest req, CancellationToken ct)
    {
        var result = await workspaces.AddMemberAsync(id, req.UserId, req.AccessLevel, ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    // DELETE /api/workspaces/{id}/members/{memberId}
    [HttpDelete("{id}/members/{memberId:int}")]
    public async Task<IActionResult> RemoveMember(string id, int memberId, CancellationToken ct)
    {
        var result = await workspaces.RemoveMemberAsync(id, memberId, ct);
        return result.IsOk ? NoContent() : Problem(result);
    }

    /// <summary>Maps the shared semantic result onto the status codes the Web client already expects.</summary>
    private ActionResult Problem<T>(SkillHubResult<T> result) where T : class
    {
        if (result.Status == SkillHubStatus.BadRequest) return BadRequest(new { message = result.Error });
        if (result.Status == SkillHubStatus.Conflict) return Conflict(new { message = result.Error });
        return NotFound();
    }
}
