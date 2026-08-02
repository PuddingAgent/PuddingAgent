using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingController.Data;
using PuddingController.Services;
using PuddingMemoryEngine;
using PuddingMemoryEngine.Data;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingPlatform.Services.MessageFabric;

namespace PuddingHost.Hosting;

/// <summary>
/// Idempotent database/schema initialization, workspace catalog loading,
/// and jieba token backfill. Extracted from PuddingApplicationInitializationExtensions.
/// </summary>
public static class PuddingApplicationInitializer
{
    public static async Task InitializeAsync(WebApplication app, CancellationToken cancellationToken)
    {
        // ── Platform DB ───────────────────────────────────
        Console.WriteLine("[Startup] Ensuring Platform DB tables...");
        try
        {
            using var scope = app.Services.CreateScope();
            var platformDb = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var schemaLogger = scope.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("PlatformSchema");

            await platformDb.Database.EnsureCreatedAsync(cancellationToken);
            await TokenUsageSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            await ConversationCommandSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            await MessageFabricSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            await ConnectorStreamProjectionSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);

            Console.WriteLine("[Startup] Platform DB tables and schema upgrades ensured");

            // ── Conversation Event Store ──────────────────
            try
            {
                using var scope2 = app.Services.CreateScope();
                var eventStore = scope2.ServiceProvider.GetRequiredService<IConversationEventStore>();
                await eventStore.EnsureTablesAsync(cancellationToken);
                Console.WriteLine("[Startup] Conversation Event Store tables ensured");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup] Event Store table ensure failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Platform DB ensure failed: {ex.Message}");
            throw;
        }

        // ── Memory DB ────────────────────────────────────
        Console.WriteLine("[Startup] Ensuring Memory DB tables...");
        using (var scope = app.Services.CreateScope())
        {
            var coreMemoryFactory = scope.ServiceProvider.GetRequiredService<
                IDbContextFactory<MemoryDbContext>>();
            var libraryMemoryFactory = scope.ServiceProvider.GetRequiredService<
                IDbContextFactory<MemoryLibraryDbContext>>();
            var memoryLogger = scope.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("MemoryDatabaseInitialization");

            await MemoryDbInitializer.InitializeAsync(coreMemoryFactory);
            await MemoryLibraryDbInitializer.InitializeAsync(libraryMemoryFactory, memoryLogger);
        }
        Console.WriteLine("[Startup] Memory DB tables ensured");

        // ── Workspace Catalog ─────────────────────────────
        Console.WriteLine("[Startup] Initializing Workspace Catalog...");
        try
        {
            var catalog = app.Services.GetRequiredService<InMemoryWorkspaceCatalog>();
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ControllerDbContext>();
            await db.Database.EnsureCreatedAsync(cancellationToken);
            await catalog.LoadAsync();
            Console.WriteLine($"[Startup] Workspace Catalog loaded, {catalog.GetAll().Count} workspace(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Workspace Catalog init failed: {ex.Message}");
        }

        // ── jieba backfill ───────────────────────────────
        Console.WriteLine("[Startup] Starting jieba backfill...");
        try
        {
            var library = app.Services.GetRequiredService<IMemoryLibrary>();
            if (library is MemoryLibrary memLib)
            {
                await memLib.BackfillTokensAsync();
                Console.WriteLine("[startup] jieba tokens backfill completed.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[startup] jieba tokens backfill skipped: {ex.Message}");
        }
    }
}
