using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Skills;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

public sealed record RoleDraft(string RoleId, string Name, string? Description, IReadOnlyList<string> Permissions);

/// <summary>
/// 权限角色应用操作——从 AppRoleApiController 原位下沉（该控制器原先直接使用 DbContext）。
///
/// 保留 Core 的既有语义：系统内置角色**不可修改也不可删除**；RoleId 重复为冲突；权限列表按字符串
/// 原样存 JSON（Core 不做白名单校验——表单侧校验，界面会把这条差异写清楚）。
/// </summary>
public sealed class RoleService(PlatformDbContext db)
{
    /// <summary>The permission strings Core's own seeded roles use; nothing else appears anywhere in Core.</summary>
    public static IReadOnlyList<string> KnownPermissions { get; } =
    [
        "workspace:read", "workspace:write", "workspace:manage",
        "agent:run", "agent:manage",
        "template:read", "template:manage",
        "llm:read", "llm:manage",
    ];

    public async Task<List<AppRoleDto>> ListAsync(CancellationToken ct = default)
        => (await db.AppRoles.AsNoTracking().OrderBy(role => role.Id).ToListAsync(ct))
            .Select(ToDto).ToList();

    public async Task<AppRoleDto?> GetAsync(string roleId, CancellationToken ct = default)
        => await db.AppRoles.AsNoTracking().FirstOrDefaultAsync(role => role.RoleId == roleId, ct) is { } role
            ? ToDto(role)
            : null;

    public async Task<SkillHubResult<AppRoleDto>> CreateAsync(RoleDraft draft, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(draft.RoleId)) return SkillHubResult<AppRoleDto>.BadRequest("RoleId 不能为空。");
        if (string.IsNullOrWhiteSpace(draft.Name)) return SkillHubResult<AppRoleDto>.BadRequest("角色名称不能为空。");
        if (await db.AppRoles.AnyAsync(role => role.RoleId == draft.RoleId, ct))
            return SkillHubResult<AppRoleDto>.Conflict($"RoleId '{draft.RoleId}' 已存在");

        var role = new AppRoleEntity
        {
            RoleId = draft.RoleId.Trim(),
            Name = draft.Name.Trim(),
            Description = draft.Description,
            PermissionsJson = JsonSerializer.Serialize(draft.Permissions),
            IsSystemRole = false,
        };
        db.AppRoles.Add(role);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<AppRoleDto>.Ok(ToDto(role));
    }

    /// <summary>The built-in system roles are immutable; Core refuses rather than silently ignoring.</summary>
    public async Task<SkillHubResult<AppRoleDto>> UpdateAsync(
        string roleId, RoleDraft draft, CancellationToken ct = default)
    {
        var role = await db.AppRoles.FirstOrDefaultAsync(candidate => candidate.RoleId == roleId, ct);
        if (role is null) return SkillHubResult<AppRoleDto>.NotFound($"角色 '{roleId}' 不存在");
        if (role.IsSystemRole) return SkillHubResult<AppRoleDto>.BadRequest("系统内置角色不可修改");
        if (string.IsNullOrWhiteSpace(draft.Name)) return SkillHubResult<AppRoleDto>.BadRequest("角色名称不能为空。");

        role.Name = draft.Name.Trim();
        role.Description = draft.Description;
        role.PermissionsJson = JsonSerializer.Serialize(draft.Permissions);
        role.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return SkillHubResult<AppRoleDto>.Ok(ToDto(role));
    }

    public async Task<SkillHubResult<AppRoleDto>> DeleteAsync(string roleId, CancellationToken ct = default)
    {
        var role = await db.AppRoles.FirstOrDefaultAsync(candidate => candidate.RoleId == roleId, ct);
        if (role is null) return SkillHubResult<AppRoleDto>.NotFound($"角色 '{roleId}' 不存在");
        if (role.IsSystemRole) return SkillHubResult<AppRoleDto>.BadRequest("系统内置角色不可删除");

        db.AppRoles.Remove(role);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<AppRoleDto>.Ok(ToDto(role));
    }

    /// <summary>A malformed PermissionsJson reads as an empty list, matching Core's tolerance.</summary>
    private static AppRoleDto ToDto(AppRoleEntity role)
    {
        var permissions = new List<string>();
        try { permissions = JsonSerializer.Deserialize<List<string>>(role.PermissionsJson) ?? []; }
        catch (JsonException) { /* malformed — 与 Core 一致，按空列表处理 */ }
        return new AppRoleDto(role.Id, role.RoleId, role.Name, role.Description, permissions,
            role.IsSystemRole, role.CreatedAt);
    }
}
