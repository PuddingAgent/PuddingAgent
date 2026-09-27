using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Skills;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>权限角色管理 HTTP 出口。校验与数据访问在 RoleService（Web 与原生客户端共用）。</summary>
[Authorize]
[ApiController]
[Route("api/roles")]
public class AppRoleApiController(RoleService roles) : ControllerBase
{
    // ── 角色列表 ──────────────────────────────────────────────
    [HttpGet]
    public async Task<ActionResult<List<AppRoleDto>>> List(CancellationToken ct)
        => Ok(await roles.ListAsync(ct));

    // ── 单个角色 ──────────────────────────────────────────────
    [HttpGet("{roleId}")]
    public async Task<ActionResult<AppRoleDto>> Get(string roleId, CancellationToken ct)
        => await roles.GetAsync(roleId, ct) is { } role ? Ok(role) : NotFound();

    // ── 创建角色 ──────────────────────────────────────────────
    [HttpPost]
    public async Task<ActionResult<AppRoleDto>> Create([FromBody] UpsertRoleRequest req, CancellationToken ct)
    {
        var result = await roles.CreateAsync(new RoleDraft(req.RoleId, req.Name, req.Description,
            req.Permissions ?? []), ct);
        return result.IsOk
            ? CreatedAtAction(nameof(Get), new { roleId = result.Value!.RoleId }, result.Value)
            : Problem(result);
    }

    // ── 更新角色 ──────────────────────────────────────────────
    [HttpPut("{roleId}")]
    public async Task<ActionResult<AppRoleDto>> Update(
        string roleId, [FromBody] UpsertRoleRequest req, CancellationToken ct)
    {
        var result = await roles.UpdateAsync(roleId, new RoleDraft(req.RoleId, req.Name, req.Description,
            req.Permissions ?? []), ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    // ── 删除角色 ──────────────────────────────────────────────
    [HttpDelete("{roleId}")]
    public async Task<IActionResult> Delete(string roleId, CancellationToken ct)
    {
        var result = await roles.DeleteAsync(roleId, ct);
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
