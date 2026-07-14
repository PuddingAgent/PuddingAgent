using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// EF Core implementation of IWorkspaceRepository.
/// </summary>
public sealed class WorkspaceRepository : IWorkspaceRepository
{
    private readonly PlatformDbContext _db;

    public WorkspaceRepository(PlatformDbContext db) => _db = db;

    public async Task<WorkspaceRow?> FindByIdAsync(string workspaceId, CancellationToken ct = default)
    {
        var entity = await _db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        return entity is null ? null : Map(entity);
    }

    public async Task<bool> ExistsAsync(string workspaceId, CancellationToken ct = default)
        => await _db.Workspaces.AnyAsync(w => w.WorkspaceId == workspaceId, ct);

    public async Task CreateAsync(string workspaceId, string name, string? description, bool isEnabled, CancellationToken ct = default)
    {
        _db.Workspaces.Add(new WorkspaceEntity
        {
            WorkspaceId = workspaceId,
            Name = name,
            Description = description,
            IsEnabled = isEnabled,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(long id, string name, string? description, bool isEnabled, CancellationToken ct = default)
    {
        var entity = await _db.Workspaces.FindAsync([id], ct);
        if (entity is null) return;
        entity.Name = name;
        entity.Description = description;
        entity.IsEnabled = isEnabled;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static WorkspaceRow Map(WorkspaceEntity e) => new()
    {
        Id = e.Id,
        WorkspaceId = e.WorkspaceId,
        Name = e.Name,
        Description = e.Description,
        IsEnabled = e.IsEnabled,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}
