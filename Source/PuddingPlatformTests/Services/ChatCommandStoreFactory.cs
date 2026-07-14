using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

internal static class ChatCommandStoreFactory
{
    public static ChatCommandStore Create(SqliteConnection connection, ILogger<ChatCommandStore> logger)
    {
        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(options =>
        {
            options.UseSqlite(connection);
            options.ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.AmbientTransactionWarning));
        });
        services.AddDbContextFactory<PlatformDbContext>(options =>
        {
            options.UseSqlite(connection);
            options.ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.AmbientTransactionWarning));
        }, ServiceLifetime.Singleton);

        var provider = services.BuildServiceProvider();

        using (var db = provider.GetRequiredService<PlatformDbContext>())
        {
            db.Database.EnsureCreated();
        }

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new ChatCommandStore(scopeFactory, logger);
    }
}
