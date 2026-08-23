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
using PuddingPlatform.Services.Execution;
using PuddingPlatform.Services.MessageFabric;
using PuddingPlatform.Services.Orchestration;
using PuddingPlatform.Services.ExternalApi;
using PuddingPlatform.Services.Security;
using PuddingPlatform.Services.Tasks;

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
            await AppUserSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            await TokenUsageSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            await ConversationCommandSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            await ChatMessageSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            await ExecutionRunSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            await MessageFabricSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            await ConnectorStreamProjectionSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            await AgentOrchestrationSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            await TaskDispatchSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            await WorkspaceTaskSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            await ExternalAccessTokenSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            await ExternalTaskApiSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);

            // ── ADR-075: ExternalTaskApi 显式配置校验（越界即启动错误，不静默回默认）──
            var externalApiOptions = scope.ServiceProvider.GetRequiredService<ExternalTaskApiOptionsProvider>();
            var configErrors = ExternalTaskApiOptionsProvider.Validate(externalApiOptions.Current);
            if (configErrors.Count > 0)
            {
                foreach (var error in configErrors)
                    Console.WriteLine($"[Startup] ExternalTaskApi config error: {error}");
                throw new InvalidOperationException(
                    "Invalid ExternalTaskApi configuration in system.json: " + string.Join("; ", configErrors));
            }

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
