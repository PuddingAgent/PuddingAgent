using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// Verifies the dependencies required to accept and execute a conversation turn.
/// Liveness must not use this probe; readiness must.
/// </summary>
public sealed class PlatformReadinessProbe(
    IDbContextFactory<PlatformDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<PlatformReadinessProbe> logger)
{
    public async Task<PlatformReadinessResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);

            using var scope = scopeFactory.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<ISubmitTurnHandler>();
            _ = scope.ServiceProvider.GetRequiredService<IExecutionRunCoordinator>();

            return new PlatformReadinessResult(true, null);
        }
        catch (Exception ex)
        {
            var errorId = Guid.NewGuid().ToString("N")[..12];
            logger.LogError(ex, "[Readiness] Conversation execution dependency check failed errorId={ErrorId}", errorId);
            return new PlatformReadinessResult(false, errorId);
        }
    }
}

public sealed record PlatformReadinessResult(bool IsReady, string? ErrorId);
