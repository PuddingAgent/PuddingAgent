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
using PuddingPlatform.Services.Files;
using PuddingPlatform.Services.Goals;
using PuddingPlatform.Services.Security;
using PuddingPlatform.Services.Scheduling;
using PuddingPlatform.Services.Tasks;
using PuddingPlatform.Services.TaskPlanning;
using PuddingPlatform.Services.Todo;

namespace PuddingHost.Hosting;

/// <summary>
/// Idempotent database/schema initialization, workspace catalog loading,
/// and jieba token backfill. Extracted from PuddingApplicationInitializationExtensions.
/// </summary>
public static class PuddingApplicationInitializer
{
    public static async Task InitializeAsync(
        WebApplication app,
        CancellationToken cancellationToken,
        StartupPhaseTracker? phases = null)
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

            phases?.Mark(StartupPhases.Schema("EnsureCreated"));
            await platformDb.Database.EnsureCreatedAsync(cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(AppUserSchemaBootstrapper)));
            await AppUserSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(TokenUsageSchemaBootstrapper)));
            await TokenUsageSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(ConversationCommandSchemaBootstrapper)));
            await ConversationCommandSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(ChatMessageSchemaBootstrapper)));
            await ChatMessageSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(SessionSteeringSchemaBootstrapper)));
            await SessionSteeringSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(ExecutionRunSchemaBootstrapper)));
            await ExecutionRunSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(SubAgentRunSchemaBootstrapper)));
            await SubAgentRunSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(MessageFabricSchemaBootstrapper)));
            await MessageFabricSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(ConnectorStreamProjectionSchemaBootstrapper)));
            await ConnectorStreamProjectionSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(AgentOrchestrationSchemaBootstrapper)));
            await AgentOrchestrationSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(TaskDispatchSchemaBootstrapper)));
            await TaskDispatchSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(WorkspaceTaskSchemaBootstrapper)));
            await WorkspaceTaskSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(TaskPlanningSchemaBootstrapper)));
            await TaskPlanningSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(GoalSchemaBootstrapper)));
            await GoalSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(TodoSchemaBootstrapper)));
            await TodoSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(TaskSchedulingSchemaBootstrapper)));
            await TaskSchedulingSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(TaskSchedulerIntentSchemaBootstrapper)));
            await TaskSchedulerIntentSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(TaskSchedulerIntentOutcomeSchemaBootstrapper)));
            await TaskSchedulerIntentOutcomeSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(TaskSchedulerDecisionSchemaBootstrapper)));
            await TaskSchedulerDecisionSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(TaskSchedulerScanRunSchemaBootstrapper)));
            await TaskSchedulerScanRunSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(ExternalAccessTokenSchemaBootstrapper)));
            await ExternalAccessTokenSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(ExternalTaskApiSchemaBootstrapper)));
            await ExternalTaskApiSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            phases?.Mark(StartupPhases.Schema(nameof(ProviderFileRefSchemaBootstrapper)));
            await ProviderFileRefSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
            // SKILL Hub 中央技能库（4 张 Hub* 表 + 索引）；EF 迁移快照漂移，故走同一幂等模式
            phases?.Mark(StartupPhases.Schema(nameof(SkillHubSchemaBootstrapper)));
            await SkillHubSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);

            // ── ADR-074 §12 / ADR-092：Core 重启后按 resume_policy 分流：默认 disarm 为 paused，
            //    auto_resume_on_restart 保持 Active 并换发 fence；显式 /goal resume 始终可用 ──
            try
            {
                var goalReconciler = scope.ServiceProvider.GetRequiredService<GoalRestartReconciler>();
                var reconcile = await goalReconciler.DisarmActiveGoalsAsync(
                    Guid.NewGuid().ToString("N"), cancellationToken);
                if (reconcile.DisarmedCount > 0 || reconcile.AutoResumedCount > 0)
                {
                    Console.WriteLine(
                        $"[Startup] Goal restart reconcile: {reconcile.DisarmedCount} disarmed -> paused," +
                        $" {reconcile.AutoResumedCount} auto-resumed");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup] Goal restart disarm failed: {ex.Message}");
            }

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
            phases?.Mark(StartupPhases.PlatformDbEnsure);

            // ── Conversation Event Store ──────────────────
            try
            {
                using var scope2 = app.Services.CreateScope();
                var eventStore = scope2.ServiceProvider.GetRequiredService<IConversationEventStore>();
                await eventStore.EnsureTablesAsync(cancellationToken);
                Console.WriteLine("[Startup] Conversation Event Store tables ensured");
                phases?.Mark(StartupPhases.EventStoreEnsure);
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
        phases?.Mark(StartupPhases.MemoryDbEnsure);

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

        phases?.Mark(StartupPhases.WorkspaceCatalog);

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

        phases?.Mark(StartupPhases.JiebaBackfill);
    }
}
