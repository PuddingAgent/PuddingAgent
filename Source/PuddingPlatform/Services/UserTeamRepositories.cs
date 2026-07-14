using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

public sealed class AppUserRepository : IAppUserRepository
{
    private readonly PlatformDbContext _db;

    public AppUserRepository(PlatformDbContext db) => _db = db;

    public async Task<bool> AnyAdminExistsAsync(CancellationToken ct = default)
        => await _db.AppUsers.AnyAsync(u => u.UserType == UserType.Admin, ct);

    public async Task<int> CountAsync(CancellationToken ct = default)
        => await _db.AppUsers.CountAsync(ct);

    public async Task<bool> AnyByUserIdAsync(string userId, CancellationToken ct = default)
        => await _db.AppUsers.AnyAsync(u => u.UserId == userId, ct);

    public async Task<bool> AnyByEmailAsync(string email, CancellationToken ct = default)
        => await _db.AppUsers.AnyAsync(u => u.Email == email, ct);

    public async Task<AppUserRow?> FindByIdAsync(string userId, CancellationToken ct = default)
    {
        var e = await _db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        return e is null ? null : Map(e);
    }

    public async Task<AppUserRow?> FindByUserIdOrEmailAsync(string login, CancellationToken ct = default)
    {
        var e = await _db.AppUsers.FirstOrDefaultAsync(u => u.UserId == login || u.Email == login, ct);
        return e is null ? null : Map(e);
    }

    public async Task<AppUserRow> CreateAsync(string userId, string userName, string? email, string? passwordHash, string userType, string? displayName, CancellationToken ct = default)
    {
        var entity = new AppUserEntity
        {
            UserId = userId,
            Username = userName,
            Email = email,
            PasswordHash = passwordHash,
            UserType = userType switch
            {
                "admin" => UserType.Admin,
                _ => UserType.SimpleUser,
            },
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.AppUsers.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task DeleteAsync(string userId, CancellationToken ct = default)
    {
        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is not null)
        {
            _db.AppUsers.Remove(user);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task UpdateAsync(AppUserRow user, CancellationToken ct = default)
    {
        var entity = await _db.AppUsers.FirstOrDefaultAsync(u => u.UserId == user.UserId, ct);
        if (entity is null) return;
        entity.Username = user.Username;
        entity.Email = entity.Email;
        entity.DisplayName = user.DisplayName;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static AppUserRow Map(AppUserEntity e) => new()
    {
        Id = e.Id,
        UserId = e.UserId,
        Username = e.Username,
        Email = e.Email,
        PasswordHash = e.PasswordHash,
        UserType = e.UserType == UserType.Admin ? "admin" : "simple",
        IsEnabled = e.IsEnabled,
        DisplayName = e.DisplayName,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

public sealed class TeamRepository : ITeamRepository
{
    private readonly PlatformDbContext _db;

    public TeamRepository(PlatformDbContext db) => _db = db;

    public async Task<TeamRow?> FindByIdAsync(string teamId, CancellationToken ct = default)
    {
        var e = await _db.Teams.FirstOrDefaultAsync(t => t.TeamId == teamId, ct);
        return e is null ? null : new TeamRow { Id = e.Id, TeamId = e.TeamId, Name = e.Name, Description = e.Description };
    }

    public async Task<TeamRow> CreateAsync(string teamId, string name, string? description, CancellationToken ct = default)
    {
        var entity = new TeamEntity
        {
            TeamId = teamId,
            Name = name,
            Description = description,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.Teams.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new TeamRow { Id = entity.Id, TeamId = entity.TeamId, Name = entity.Name, Description = entity.Description };
    }

    public async Task<bool> AnyTeamMemberAsync(int teamId, int userId, CancellationToken ct = default)
        => await _db.TeamMembers.AnyAsync(m => m.TeamEntityId == teamId && m.UserEntityId == userId, ct);

    public async Task AddTeamMemberAsync(int teamId, int userId, CancellationToken ct = default)
    {
        _db.TeamMembers.Add(new TeamMemberEntity { TeamEntityId = teamId, UserEntityId = userId });
        await _db.SaveChangesAsync(ct);
    }
}

public sealed class WorkspaceMemberRepository : IWorkspaceMemberRepository
{
    private readonly PlatformDbContext _db;

    public WorkspaceMemberRepository(PlatformDbContext db) => _db = db;

    public async Task<bool> AnyMemberAsync(int workspaceId, int userId, CancellationToken ct = default)
        => await _db.WorkspaceMembers.AnyAsync(m => m.WorkspaceEntityId == workspaceId && m.UserEntityId == userId, ct);

    public async Task AddMemberAsync(int workspaceId, int userId, CancellationToken ct = default)
    {
        _db.WorkspaceMembers.Add(new WorkspaceMemberEntity { WorkspaceEntityId = workspaceId, UserEntityId = userId });
        await _db.SaveChangesAsync(ct);
    }
}
