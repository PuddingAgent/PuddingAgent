using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>权限角色管理 API — CRUD</summary>
[Authorize]
[ApiController]
[Route("api/roles")]
public class AppRoleApiController(PlatformDbContext db) : ControllerBase
{
    // ── 角色列表 ──────────────────────────────────────────────
    [HttpGet]
    public async Task<ActionResult<List<AppRoleDto>>> List(CancellationToken ct)
    {
        var roles = await db.AppRoles.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
        return Ok(roles.Select(MapToDto).ToList());
    }

    // ── 单个角色 ──────────────────────────────────────────────
    [HttpGet("{roleId}")]
    public async Task<ActionResult<AppRoleDto>> Get(string roleId, CancellationToken ct)
    {
        var role = await db.AppRoles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RoleId == roleId, ct);
        if (role is null) return NotFound();
        return Ok(MapToDto(role));
    }

    // ── 创建角色 ──────────────────────────────────────────────
    [HttpPost]
    public async Task<ActionResult<AppRoleDto>> Create(
        [FromBody] UpsertRoleRequest req, CancellationToken ct)
    {
        if (await db.AppRoles.AnyAsync(r => r.RoleId == req.RoleId, ct))
            return Conflict(new { message = $"RoleId '{req.RoleId}' 已存在" });

        var role = new AppRoleEntity
        {
            RoleId = req.RoleId,
            Name = req.Name,
            Description = req.Description,
            PermissionsJson = JsonSerializer.Serialize(req.Permissions),
            IsSystemRole = false,
        };
        db.AppRoles.Add(role);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { roleId = role.RoleId }, MapToDto(role));
    }

    // ── 更新角色 ──────────────────────────────────────────────
    [HttpPut("{roleId}")]
    public async Task<ActionResult<AppRoleDto>> Update(
        string roleId, [FromBody] UpsertRoleRequest req, CancellationToken ct)
    {
        var role = await db.AppRoles.FirstOrDefaultAsync(r => r.RoleId == roleId, ct);
        if (role is null) return NotFound();

        if (role.IsSystemRole)
            return BadRequest(new { message = "系统内置角色不可修改" });

        role.Name = req.Name;
        role.Description = req.Description;
        role.PermissionsJson = JsonSerializer.Serialize(req.Permissions);
        role.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(MapToDto(role));
    }

    // ── 删除角色 ──────────────────────────────────────────────
    [HttpDelete("{roleId}")]
    public async Task<IActionResult> Delete(string roleId, CancellationToken ct)
    {
        var role = await db.AppRoles.FirstOrDefaultAsync(r => r.RoleId == roleId, ct);
        if (role is null) return NotFound();

        if (role.IsSystemRole)
            return BadRequest(new { message = "系统内置角色不可删除" });

        db.AppRoles.Remove(role);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────
    private static AppRoleDto MapToDto(AppRoleEntity r)
    {
        var perms = new List<string>();
        try { perms = JsonSerializer.Deserialize<List<string>>(r.PermissionsJson) ?? []; }
        catch { /* ignore */ }
        return new(r.Id, r.RoleId, r.Name, r.Description, perms, r.IsSystemRole, r.CreatedAt);
    }
}
