using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Skills;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>用户管理 HTTP 出口 — CRUD、密码、角色分配（仅 Admin）。逻辑在 UserService。</summary>
[Authorize(Roles = "admin")]
[ApiController]
[Route("api/users")]
public class AppUserApiController(UserService users) : ControllerBase
{
    // ── 用户列表 ──────────────────────────────────────────────
    [HttpGet]
    public async Task<ActionResult<List<AppUserDto>>> List(CancellationToken ct)
        => Ok(await users.ListAsync(ct));

    // ── 单个用户 ──────────────────────────────────────────────
    [HttpGet("{userId}")]
    public async Task<ActionResult<AppUserDto>> Get(string userId, CancellationToken ct)
        => await users.GetAsync(userId, ct) is { } user ? Ok(user) : NotFound();

    // ── 创建用户 ──────────────────────────────────────────────
    [HttpPost]
    public async Task<ActionResult<AppUserDto>> Create([FromBody] CreateUserRequest req, CancellationToken ct)
    {
        var result = await users.CreateAsync(new UserDraft(req.UserId, req.Username, req.Email,
            req.DisplayName, req.UserType, req.Password), ct);
        return result.IsOk
            ? CreatedAtAction(nameof(Get), new { userId = result.Value!.UserId }, result.Value)
            : Problem(result);
    }

    // ── 更新用户基本信息 ──────────────────────────────────────
    [HttpPut("{userId}")]
    public async Task<ActionResult<AppUserDto>> Update(
        string userId, [FromBody] UpdateUserRequest req, CancellationToken ct)
    {
        var result = await users.UpdateAsync(userId, new UserMetaUpdate(req.Username, req.Email,
            req.DisplayName, req.UserType, req.IsEnabled), ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    // ── 修改密码 ──────────────────────────────────────────────
    [HttpPut("{userId}/password")]
    public async Task<IActionResult> ChangePassword(
        string userId, [FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        var result = await users.ChangePasswordAsync(userId, req.NewPassword ?? "", ct);
        return result.IsOk ? NoContent() : Problem(result);
    }

    // ── 分配角色（全量替换）──────────────────────────────────
    [HttpPut("{userId}/roles")]
    public async Task<ActionResult<AppUserDto>> AssignRoles(
        string userId, [FromBody] AssignRolesRequest req, CancellationToken ct)
    {
        var result = await users.AssignRolesAsync(userId, req.RoleIds ?? [], ct);
        return result.IsOk ? Ok(result.Value) : Problem(result);
    }

    // ── 删除用户 ──────────────────────────────────────────────
    [HttpDelete("{userId}")]
    public async Task<IActionResult> Delete(string userId, CancellationToken ct)
    {
        var result = await users.DeleteAsync(userId, ct);
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
