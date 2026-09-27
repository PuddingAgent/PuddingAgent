using Microsoft.EntityFrameworkCore;
using PuddingCode.Skills;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Utils;

namespace PuddingPlatform.Services;

public sealed record UserDraft(
    string UserId, string Username, string Email, string? DisplayName, string UserType, string? Password);

public sealed record UserMetaUpdate(string Username, string Email, string? DisplayName, string UserType, bool IsEnabled);

/// <summary>
/// 用户管理应用操作——从 AppUserApiController 原位下沉（该控制器原先直接使用 DbContext）。
///
/// 保留 Core 的既有规则：UserId 与 Email 重复为冲突；UserType 必须是 Admin/SimpleUser；密码至少 6 位；
/// 角色分配是**全量替换**；删除 Admin 时至少保留一个 Admin。
///
/// 一处刻意的行为修正：原 Update 不检查 Email 唯一性，改成一个已被占用的邮箱会撞唯一索引、以 500 结束；
/// 现在与 Create 一致地返回冲突（同一个数据库约束，只是错误形态更诚实）。
/// </summary>
public sealed class UserService(PlatformDbContext db)
{
    public static IReadOnlyList<string> KnownUserTypes { get; } = [nameof(UserType.Admin), nameof(UserType.SimpleUser)];
    public const int MinimumPasswordLength = 6;

    public async Task<List<AppUserDto>> ListAsync(CancellationToken ct = default)
        => (await ReadQuery().OrderBy(user => user.Id).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<AppUserDto?> GetAsync(string userId, CancellationToken ct = default)
        => await ReadQuery().FirstOrDefaultAsync(user => user.UserId == userId, ct) is { } user ? ToDto(user) : null;

    public async Task<SkillHubResult<AppUserDto>> CreateAsync(UserDraft draft, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(draft.UserId)) return SkillHubResult<AppUserDto>.BadRequest("UserId 不能为空。");
        if (string.IsNullOrWhiteSpace(draft.Username)) return SkillHubResult<AppUserDto>.BadRequest("用户名不能为空。");
        if (string.IsNullOrWhiteSpace(draft.Email)) return SkillHubResult<AppUserDto>.BadRequest("邮箱不能为空。");
        if (!TryParseUserType(draft.UserType, out var userType))
            return SkillHubResult<AppUserDto>.BadRequest("UserType 无效，应为 Admin 或 SimpleUser");
        // 新建用户的初始密码沿用 Core 的下限；改密接口也用它。
        if (string.IsNullOrWhiteSpace(draft.Password) || draft.Password.Length < MinimumPasswordLength)
            return SkillHubResult<AppUserDto>.BadRequest($"密码不得少于 {MinimumPasswordLength} 位");

        if (await db.AppUsers.AnyAsync(user => user.UserId == draft.UserId, ct))
            return SkillHubResult<AppUserDto>.Conflict($"UserId '{draft.UserId}' 已存在");
        if (await db.AppUsers.AnyAsync(user => user.Email == draft.Email, ct))
            return SkillHubResult<AppUserDto>.Conflict($"Email '{draft.Email}' 已被使用");

        var entity = new AppUserEntity
        {
            UserId = draft.UserId.Trim(),
            Username = draft.Username.Trim(),
            Email = draft.Email.Trim(),
            DisplayName = draft.DisplayName,
            PasswordHash = PasswordHasher.Hash(draft.Password),
            UserType = userType,
            IsEnabled = true,
        };
        db.AppUsers.Add(entity);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<AppUserDto>.Ok(ToDto(entity));
    }

    public async Task<SkillHubResult<AppUserDto>> UpdateAsync(
        string userId, UserMetaUpdate update, CancellationToken ct = default)
    {
        var user = await TrackedQuery().FirstOrDefaultAsync(candidate => candidate.UserId == userId, ct);
        if (user is null) return SkillHubResult<AppUserDto>.NotFound($"用户 '{userId}' 不存在");
        if (!TryParseUserType(update.UserType, out var userType))
            return SkillHubResult<AppUserDto>.BadRequest("UserType 无效");
        if (string.IsNullOrWhiteSpace(update.Username)) return SkillHubResult<AppUserDto>.BadRequest("用户名不能为空。");
        if (string.IsNullOrWhiteSpace(update.Email)) return SkillHubResult<AppUserDto>.BadRequest("邮箱不能为空。");
        // 修正：原实现不查重复邮箱，会撞唯一索引变成 500；这里与 Create 一致返回冲突。
        if (await db.AppUsers.AnyAsync(candidate => candidate.UserId != userId && candidate.Email == update.Email, ct))
            return SkillHubResult<AppUserDto>.Conflict($"Email '{update.Email}' 已被使用");

        user.Username = update.Username.Trim();
        user.Email = update.Email.Trim();
        user.DisplayName = update.DisplayName;
        user.UserType = userType;
        user.IsEnabled = update.IsEnabled;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return SkillHubResult<AppUserDto>.Ok(ToDto(user));
    }

    public async Task<SkillHubResult<AppUserDto>> ChangePasswordAsync(
        string userId, string newPassword, CancellationToken ct = default)
    {
        var user = await db.AppUsers.FirstOrDefaultAsync(candidate => candidate.UserId == userId, ct);
        if (user is null) return SkillHubResult<AppUserDto>.NotFound($"用户 '{userId}' 不存在");
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < MinimumPasswordLength)
            return SkillHubResult<AppUserDto>.BadRequest($"密码不得少于 {MinimumPasswordLength} 位");

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return SkillHubResult<AppUserDto>.Ok(ToDto(user));
    }

    /// <summary>
    /// Full replacement, matching Core: roles not listed are removed. Unknown role ids are ignored by Core
    /// (they simply match nothing), so the result reports which ids were actually applied.
    /// </summary>
    public async Task<SkillHubResult<AppUserDto>> AssignRolesAsync(
        string userId, IReadOnlyList<string> roleIds, CancellationToken ct = default)
    {
        var user = await TrackedQuery().FirstOrDefaultAsync(candidate => candidate.UserId == userId, ct);
        if (user is null) return SkillHubResult<AppUserDto>.NotFound($"用户 '{userId}' 不存在");

        var targets = await db.AppRoles.Where(role => roleIds.Contains(role.RoleId)).ToListAsync(ct);
        db.AppUserRoles.RemoveRange(user.UserRoles);
        foreach (var role in targets)
            db.AppUserRoles.Add(new AppUserRoleEntity { UserEntityId = user.Id, RoleEntityId = role.Id });

        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await db.Entry(user).Collection(candidate => candidate.UserRoles).LoadAsync(ct);
        return SkillHubResult<AppUserDto>.Ok(ToDto(user));
    }

    /// <summary>The last remaining Admin cannot be deleted; Core refuses rather than orphaning the install.</summary>
    public async Task<SkillHubResult<AppUserDto>> DeleteAsync(string userId, CancellationToken ct = default)
    {
        var user = await TrackedQuery().FirstOrDefaultAsync(candidate => candidate.UserId == userId, ct);
        if (user is null) return SkillHubResult<AppUserDto>.NotFound($"用户 '{userId}' 不存在");

        if (user.UserType == UserType.Admin
            && await db.AppUsers.CountAsync(candidate => candidate.UserType == UserType.Admin, ct) <= 1)
            return SkillHubResult<AppUserDto>.BadRequest("至少保留一个 Admin 账号");

        db.AppUsers.Remove(user);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<AppUserDto>.Ok(ToDto(user));
    }

    public static bool TryParseUserType(string? value, out UserType userType) =>
        Enum.TryParse(value, ignoreCase: true, out userType) && Enum.IsDefined(userType);

    /// <summary>
    /// Reads are no-tracking; mutations must use <see cref="TrackedQuery"/> or SaveChanges is a no-op.
    /// Both include the Role navigation: without it the DTO falls back to the numeric role entity id, so
    /// roleIds came back as "5" instead of the role id the caller assigned.
    /// </summary>
    private IQueryable<AppUserEntity> ReadQuery() => db.AppUsers.AsNoTracking()
        .Include(user => user.UserRoles).ThenInclude(link => link.Role);

    private IQueryable<AppUserEntity> TrackedQuery() => db.AppUsers
        .Include(user => user.UserRoles).ThenInclude(link => link.Role);

    private static AppUserDto ToDto(AppUserEntity user) => new(
        user.Id, user.UserId, user.Username, user.Email, user.DisplayName,
        user.UserType.ToString(), user.IsEnabled,
        user.UserRoles.Select(link => link.Role?.RoleId ?? link.RoleEntityId.ToString()).ToList(),
        user.CreatedAt, user.Avatar);
}
