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

namespace PuddingAgent.Services;

/// <summary>
/// Applies idempotent database/schema initialization and loads startup catalogs.
/// </summary>
public static class PuddingApplicationInitializationExtensions
{
    public static async Task InitializePuddingDataAsync(this WebApplication app)
    {
        // ── Workspace Catalog 初始化：从 DB 加载或播种 default workspace ──
        Console.WriteLine("[Startup] Ensuring Platform DB tables...");
        try
        {
            using (var scope = app.Services.CreateScope())
            {
                var platformDb = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var schemaLogger = scope.ServiceProvider
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("PlatformSchema");

                await platformDb.Database.EnsureCreatedAsync();
                await TokenUsageSchemaBootstrapper.EnsureCreatedAsync(
                    platformDb,
                    schemaLogger,
                    CancellationToken.None);
                await ConversationCommandSchemaBootstrapper.EnsureCreatedAsync(
                    platformDb,
                    schemaLogger,
                    CancellationToken.None);
                await MessageFabricSchemaBootstrapper.EnsureCreatedAsync(
                    platformDb,
                    schemaLogger,
                    CancellationToken.None);
                await ConnectorStreamProjectionSchemaBootstrapper.EnsureCreatedAsync(
                    platformDb,
                    schemaLogger,
                    CancellationToken.None);
            }
            Console.WriteLine("[Startup] Platform DB tables and schema upgrades ensured");

            // ADR-057: Ensure conversation event store tables exist (not lazy).
            try
            {
                using var scope2 = app.Services.CreateScope();
                var eventStore = scope2.ServiceProvider.GetRequiredService<IConversationEventStore>();
                await eventStore.EnsureTablesAsync(CancellationToken.None);
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

        Console.WriteLine("[Startup] Ensuring Memory DB tables...");
        using (var scope = app.Services.CreateScope())
        {
            var coreMemoryFactory = scope.ServiceProvider.GetRequiredService<
                IDbContextFactory<PuddingMemoryEngine.Data.MemoryDbContext>>();
            var libraryMemoryFactory = scope.ServiceProvider.GetRequiredService<
                IDbContextFactory<PuddingMemoryEngine.Data.MemoryLibraryDbContext>>();
            var memoryLogger = scope.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("MemoryDatabaseInitialization");

            await PuddingMemoryEngine.Data.MemoryDbInitializer.InitializeAsync(coreMemoryFactory);
            await PuddingMemoryEngine.Data.MemoryLibraryDbInitializer.InitializeAsync(
                libraryMemoryFactory,
                memoryLogger);
        }
        Console.WriteLine("[Startup] Memory DB tables ensured");

        // ── Workspace Catalog 初始化：从 DB 加载或播种 default workspace ──
        Console.WriteLine("[Startup] Initializing Workspace Catalog...");
        try
        {
            var catalog = app.Services.GetRequiredService<InMemoryWorkspaceCatalog>();
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ControllerDbContext>();
                await db.Database.EnsureCreatedAsync();
            }
            await catalog.LoadAsync();
            Console.WriteLine($"[Startup] Workspace Catalog loaded, {catalog.GetAll().Count} workspace(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Workspace Catalog init failed: {ex.Message}");
        }

        // ── jieba 分词回填：存量 Chapter 的 TitleTokens / ContentTokens ──
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
