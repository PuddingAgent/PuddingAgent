using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Utils;

namespace PuddingPlatform.Controllers.Api;

/// <summary>用户管理 API — CRUD、密码、角色分配</summary>
[Authorize]
[ApiController]
[Route("api/users")]
public class AppUserApiController(PlatformDbContext db) : ControllerBase
{
    // ── 用户列表 ──────────────────────────────────────────────
    [HttpGet]
    public async Task<ActionResult<List<AppUserDto>>> List(CancellationToken ct)
    {
        var users = await db.AppUsers
            .AsNoTracking()
            .Include(u => u.UserRoles)
            .OrderBy(u => u.Id)
            .ToListAsync(ct);

        return Ok(users.Select(MapToDto).ToList());
    }

    // ── 单个用户 ──────────────────────────────────────────────
    [HttpGet("{userId}")]
    public async Task<ActionResult<AppUserDto>> Get(string userId, CancellationToken ct)
    {
        var user = await db.AppUsers
            .AsNoTracking()
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.UserId == userId, ct);

        if (user is null) return NotFound();
        return Ok(MapToDto(user));
    }

    // ── 创建用户 ──────────────────────────────────────────────
    [HttpPost]
    public async Task<ActionResult<AppUserDto>> Create(
        [FromBody] CreateUserRequest req, CancellationToken ct)
    {
        if (await db.AppUsers.AnyAsync(u => u.UserId == req.UserId, ct))
            return Conflict(new { message = $"UserId '{req.UserId}' 已存在" });

        if (await db.AppUsers.AnyAsync(u => u.Email == req.Email, ct))
            return Conflict(new { message = $"Email '{req.Email}' 已被使用" });

        if (!Enum.TryParse<UserType>(req.UserType, ignoreCase: true, out var userType))
            return BadRequest(new { message = "UserType 无效，应为 Admin 或 SimpleUser" });

        var user = new AppUserEntity
        {
            UserId = req.UserId,
            Username = req.Username,
            Email = req.Email,
            DisplayName = req.DisplayName,
            PasswordHash = PasswordHasher.Hash(req.Password),
            UserType = userType,
            IsEnabled = true,
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { userId = user.UserId }, MapToDto(user));
    }

    // ── 更新用户基本信息 ──────────────────────────────────────
    [HttpPut("{userId}")]
    public async Task<ActionResult<AppUserDto>> Update(
        string userId, [FromBody] UpdateUserRequest req, CancellationToken ct)
    {
        var user = await db.AppUsers
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return NotFound();

        if (!Enum.TryParse<UserType>(req.UserType, ignoreCase: true, out var userType))
            return BadRequest(new { message = "UserType 无效" });

        user.Username = req.Username;
        user.Email = req.Email;
        user.DisplayName = req.DisplayName;
        user.UserType = userType;
        user.IsEnabled = req.IsEnabled;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(MapToDto(user));
    }

    // ── 修改密码 ──────────────────────────────────────────────
    [HttpPut("{userId}/password")]
    public async Task<IActionResult> ChangePassword(
        string userId, [FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return NotFound();

        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
            return BadRequest(new { message = "密码不得少于 6 位" });

        user.PasswordHash = PasswordHasher.Hash(req.NewPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── 分配角色（全量替换）──────────────────────────────────
    [HttpPut("{userId}/roles")]
    public async Task<ActionResult<AppUserDto>> AssignRoles(
        string userId, [FromBody] AssignRolesRequest req, CancellationToken ct)
    {
        var user = await db.AppUsers
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return NotFound();

        var targetRoles = await db.AppRoles
            .Where(r => req.RoleIds.Contains(r.RoleId))
            .ToListAsync(ct);

        // 全量替换
        db.AppUserRoles.RemoveRange(user.UserRoles);
        foreach (var role in targetRoles)
        {
            db.AppUserRoles.Add(new AppUserRoleEntity
            {
                UserEntityId = user.Id,
                RoleEntityId = role.Id,
            });
        }

        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // 重新加载
        await db.Entry(user).Collection(u => u.UserRoles).LoadAsync(ct);
        return Ok(MapToDto(user));
    }

    // ── 删除用户 ──────────────────────────────────────────────
    [HttpDelete("{userId}")]
    public async Task<IActionResult> Delete(string userId, CancellationToken ct)
    {
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return NotFound();

        if (user.UserType == UserType.Admin)
        {
            var adminCount = await db.AppUsers.CountAsync(u => u.UserType == UserType.Admin, ct);
            if (adminCount <= 1) return BadRequest(new { message = "至少保留一个 Admin 账号" });
        }

        db.AppUsers.Remove(user);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────
    private static AppUserDto MapToDto(AppUserEntity u) => new(
        u.Id, u.UserId, u.Username, u.Email, u.DisplayName,
        u.UserType.ToString(),
        u.IsEnabled,
        u.UserRoles.Select(ur => ur.Role?.RoleId ?? ur.RoleEntityId.ToString()).ToList(),
        u.CreatedAt
    );
}
