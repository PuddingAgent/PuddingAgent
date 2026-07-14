namespace PuddingCode.Platform;

/// <summary>
/// Lightweight app user row for queries.
/// </summary>
public sealed class AppUserRow
{
    public int Id { get; init; }
    public string UserId { get; init; } = string.Empty;
    public string? UserName { get; init; }
    public string? Email { get; init; }
    public string? PasswordHash { get; init; }
    public string UserType { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Repository for application user accounts.
/// </summary>
public interface IAppUserRepository
{
    Task<bool> AnyAdminExistsAsync(CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task<bool> AnyByUserIdAsync(string userId, CancellationToken ct = default);
    Task<bool> AnyByEmailAsync(string email, CancellationToken ct = default);
    Task<AppUserRow?> FindByIdAsync(string userId, CancellationToken ct = default);
    Task<AppUserRow> CreateAsync(string userId, string userName, string? email, string? passwordHash, string userType, string? displayName, CancellationToken ct = default);
    Task DeleteAsync(string userId, CancellationToken ct = default);
    Task UpdateAsync(AppUserRow user, CancellationToken ct = default);
}

/// <summary>
/// Repository for team aggregate.
/// </summary>
public interface ITeamRepository
{
    Task<TeamRow?> FindByIdAsync(string teamId, CancellationToken ct = default);
    Task<TeamRow> CreateAsync(string teamId, string name, string? description, CancellationToken ct = default);
    Task<bool> AnyTeamMemberAsync(int teamId, int userId, CancellationToken ct = default);
    Task AddTeamMemberAsync(int teamId, int userId, CancellationToken ct = default);
}

public sealed class TeamRow
{
    public int Id { get; init; }
    public string TeamId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
}

/// <summary>
/// Repository for workspace membership.
/// </summary>
public interface IWorkspaceMemberRepository
{
    Task<bool> AnyMemberAsync(int workspaceId, int userId, CancellationToken ct = default);
    Task AddMemberAsync(int workspaceId, int userId, CancellationToken ct = default);
}
