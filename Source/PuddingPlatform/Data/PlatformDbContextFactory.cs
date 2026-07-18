using Microsoft.EntityFrameworkCore;

namespace PuddingPlatform.Data;

/// <summary>
/// Creates independent PlatformDbContext instances from immutable singleton
/// options. Singleton services depend on the factory; HTTP/application scopes
/// receive a scoped context created by the same factory.
/// </summary>
public sealed class PlatformDbContextFactory(
    DbContextOptions<PlatformDbContext> options)
    : IDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext()
        => new(options);

    public Task<PlatformDbContext> CreateDbContextAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
