using Microsoft.EntityFrameworkCore;
using PuddingAgents;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Security;

namespace PuddingPlatform.Services.MessageFabric;

public sealed class ExternalAgentDirectory(
    IDbContextFactory<PlatformDbContext> databaseFactory,
    ExternalTaskApiOptionsProvider options,
    AgentAdapterRegistry registry,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<ExternalAgentDirectoryEntry>> ListAsync(string workspaceId, CancellationToken ct)
    {
        if (!options.Current.Enabled)
            return [];

        await using var db = await databaseFactory.CreateDbContextAsync(ct);
        var rows = await (
            from adapter in db.ExternalAgentAdapters.AsNoTracking()
            join token in db.ExternalAccessTokens.AsNoTracking() on adapter.TokenId equals token.TokenId
            where adapter.WorkspaceId == workspaceId && adapter.RegisteredAtUtc != null && token.RevokedAtUtc == null
                && db.AppUsers.Any(u => u.UserId == token.OwnerUserId && u.IsEnabled)
                && db.Workspaces.Any(w => w.WorkspaceId == workspaceId && w.IsEnabled && !w.IsFrozen)
            select new { adapter.AgentId, token.Name, token.ExpiresAtUtc }).ToListAsync(ct);
        var now = clock.GetUtcNow();
        return rows.Where(row => row.ExpiresAtUtc > now)
            .Select(row => new ExternalAgentDirectoryEntry(row.AgentId, row.Name,
                registry.Resolve(workspaceId, row.AgentId) is not null)).ToArray();
    }
}

public sealed record ExternalAgentDirectoryEntry(string AgentId, string Name, bool Online);
