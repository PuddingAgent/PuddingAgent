using System.Globalization;
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
/// <para>
/// Every step reports a phase to the optional <see cref="IStartupPhaseSink"/> so a slow start can be
/// attributed to a named step instead of to "startup". Step order, exception behavior and the
/// console lines are unchanged by this instrumentation.
/// </para>
/// </summary>
public static class PuddingApplicationInitializer
{
    public static async Task InitializeAsync(WebApplication app, IStartupPhaseSink? sink, CancellationToken cancellationToken)
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

            // Each schema group is its own phase: a slow or failing group must be identifiable.
            var schemaSteps = new (string Name, Func<Task> Run)[]
            {
                ("database", () => platformDb.Database.EnsureCreatedAsync(cancellationToken)),
                ("app-user", () => AppUserSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("token-usage", () => TokenUsageSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("conversation-command", () => ConversationCommandSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("chat-message", () => ChatMessageSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("session-steering", () => SessionSteeringSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("execution-run", () => ExecutionRunSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("sub-agent-run", () => SubAgentRunSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("message-fabric", () => MessageFabricSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("connector-stream-projection", () => ConnectorStreamProjectionSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("agent-orchestration", () => AgentOrchestrationSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("task-dispatch", () => TaskDispatchSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("workspace-task", () => WorkspaceTaskSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("task-planning", () => TaskPlanningSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("goal", () => GoalSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("todo", () => TodoSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("task-scheduling", () => TaskSchedulingSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("task-scheduler-intent", () => TaskSchedulerIntentSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("task-scheduler-intent-outcome", () => TaskSchedulerIntentOutcomeSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("task-scheduler-decision", () => TaskSchedulerDecisionSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("task-scheduler-scan-run", () => TaskSchedulerScanRunSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("external-access-token", () => ExternalAccessTokenSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("external-task-api", () => ExternalTaskApiSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                ("provider-file-ref", () => ProviderFileRefSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
                // SKILL Hub 中央技能库（4 张 Hub* 表 + 索引）；EF 迁移快照漂移，故走同一幂等模式
                ("skill-hub", () => SkillHubSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken)),
            };
            // ── 稳态快速路径 ─────────────────────────────
            // 阶梯本身按幂等构造，所以稳态下整条 25 步可以跳过。标记用 SQLite 原生 PRAGMA user_version
            // （只读库头，不新增表）。标记只能证明"这个库跑过修订 N"，不能证明结构没被外部改坏，因此
            // 命中标记时再探一次哨兵表；任何异常/未知一律回落全量阶梯（fail-open：宁可多干活）。
            var markerPhase = sink?.Phase(StartupPhaseNames.PlatformSchemaMarker);
            long revision;
            int sentinelFound;
            try
            {
                // 一次连接开合读完两件事：在这类 DataRoot 上，对一个大库开连接本身就是数百毫秒，
                // 付两次是纯浪费（这正是第一版实现被实测抓出来的问题）。
                (revision, sentinelFound) = await ProbeSchemaMarkerAsync(platformDb, cancellationToken).ConfigureAwait(false);
                markerPhase?.Complete(
                    $"user_version={revision} 哨兵表={(sentinelFound < 0 ? "未探测" : $"{sentinelFound}/{PlatformSchemaRevision.SentinelTables.Length}")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup] Schema marker probe failed: {ex.Message}");
                markerPhase?.Skip($"跳过：探测失败 {ex.GetType().Name}");
                revision = -1;
                sentinelFound = -1;
            }
            finally { markerPhase?.Dispose(); }

            var forced = PlatformSchemaRevision.ForceFullLadder(
                Environment.GetEnvironmentVariable("PUDDING_SCHEMA_LADDER"));
            if (forced || PlatformSchemaRevision.LadderRequired(revision, sentinelFound))
            {
                sink?.Metric(StartupMetrics.SchemaStepCount, schemaSteps.Length);
                sink?.Metric(StartupMetrics.SchemaRevision, revision);
                sink?.Metric(StartupMetrics.SchemaLadderSkipped, 0);
                if (forced) Console.WriteLine("[Startup] Platform schema ladder forced by PUDDING_SCHEMA_LADDER=full");
                foreach (var (name, run) in schemaSteps)
                    await StepAsync(sink, StartupPhaseNames.PlatformSchemaStepPrefix + name, run).ConfigureAwait(false);
                // 标记只在阶梯整体成功后写；中途失败则标记保持旧值，下次启动自然重跑（幂等）。
                await StepAsync(sink, StartupPhaseNames.PlatformSchemaStamp,
                    () => WriteUserVersionAsync(platformDb, PlatformSchemaRevision.Current, cancellationToken)).ConfigureAwait(false);
            }
            else
            {
                sink?.Metric(StartupMetrics.SchemaRevision, revision);
                sink?.Metric(StartupMetrics.SchemaLadderSkipped, 1);
                Console.WriteLine(
                    $"[Startup] Platform schema ladder skipped: user_version={revision} == revision " +
                    $"{PlatformSchemaRevision.Current}, {sentinelFound} sentinel table(s) present");
            }

            // ── ADR-074 §12 / ADR-092：Core 重启后按 resume_policy 分流：默认 disarm 为 paused，
            //    auto_resume_on_restart 保持 Active 并换发 fence；显式 /goal resume 始终可用 ──
            var goalPhase = sink?.Phase(StartupPhaseNames.GoalReconcile);
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
                goalPhase?.Complete($"{reconcile.DisarmedCount} disarmed / {reconcile.AutoResumedCount} auto-resumed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup] Goal restart disarm failed: {ex.Message}");
                goalPhase?.Skip($"跳过：{ex.GetType().Name}");
            }
            finally { goalPhase?.Dispose(); }

            // ── ADR-075: ExternalTaskApi 显式配置校验（越界即启动错误，不静默回默认）──
            using (var configPhase = sink?.Phase(StartupPhaseNames.ExternalApiConfig))
            {
                var externalApiOptions = scope.ServiceProvider.GetRequiredService<ExternalTaskApiOptionsProvider>();
                var configErrors = ExternalTaskApiOptionsProvider.Validate(externalApiOptions.Current);
                if (configErrors.Count > 0)
                {
                    foreach (var error in configErrors)
                        Console.WriteLine($"[Startup] ExternalTaskApi config error: {error}");
                    throw new InvalidOperationException(
                        "Invalid ExternalTaskApi configuration in system.json: " + string.Join("; ", configErrors));
                }
                configPhase?.Complete();
            }

            Console.WriteLine("[Startup] Platform DB tables and schema upgrades ensured");

            // ── Conversation Event Store ──────────────────
            var eventStorePhase = sink?.Phase(StartupPhaseNames.EventStore);
            try
            {
                using var scope2 = app.Services.CreateScope();
                var eventStore = scope2.ServiceProvider.GetRequiredService<IConversationEventStore>();
                await eventStore.EnsureTablesAsync(cancellationToken);
                Console.WriteLine("[Startup] Conversation Event Store tables ensured");
                eventStorePhase?.Complete();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup] Event Store table ensure failed: {ex.Message}");
                eventStorePhase?.Skip($"跳过：{ex.GetType().Name}");
            }
            finally { eventStorePhase?.Dispose(); }
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

            await StepAsync(sink, StartupPhaseNames.MemoryDbCore, () => MemoryDbInitializer.InitializeAsync(coreMemoryFactory)).ConfigureAwait(false);
            await StepAsync(sink, StartupPhaseNames.MemoryDbLibrary, () => MemoryLibraryDbInitializer.InitializeAsync(libraryMemoryFactory, memoryLogger)).ConfigureAwait(false);
        }
        Console.WriteLine("[Startup] Memory DB tables ensured");

        // ── Workspace Catalog ─────────────────────────────
        Console.WriteLine("[Startup] Initializing Workspace Catalog...");
        var catalogPhase = sink?.Phase(StartupPhaseNames.WorkspaceCatalog);
        try
        {
            var catalog = app.Services.GetRequiredService<InMemoryWorkspaceCatalog>();
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ControllerDbContext>();
            await db.Database.EnsureCreatedAsync(cancellationToken);
            await catalog.LoadAsync();
            var workspaceCount = catalog.GetAll().Count;
            Console.WriteLine($"[Startup] Workspace Catalog loaded, {workspaceCount} workspace(s)");
            sink?.Metric(StartupMetrics.WorkspaceCount, workspaceCount);
            catalogPhase?.Complete($"{workspaceCount} workspace(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Workspace Catalog init failed: {ex.Message}");
            catalogPhase?.Skip($"跳过：{ex.GetType().Name}");
        }
        finally { catalogPhase?.Dispose(); }

        // ── jieba backfill ───────────────────────────────
        Console.WriteLine("[Startup] Starting jieba backfill...");
        var jiebaPhase = sink?.Phase(StartupPhaseNames.JiebaBackfill);
        try
        {
            var library = app.Services.GetRequiredService<IMemoryLibrary>();
            if (library is MemoryLibrary memLib)
            {
                await memLib.BackfillTokensAsync();
                Console.WriteLine("[startup] jieba tokens backfill completed.");
                jiebaPhase?.Complete();
            }
            else
            {
                jiebaPhase?.Skip("跳过：IMemoryLibrary 不是 MemoryLibrary 实现");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[startup] jieba tokens backfill skipped: {ex.Message}");
            jiebaPhase?.Skip($"跳过：{ex.GetType().Name}");
        }
        finally { jiebaPhase?.Dispose(); }
    }

    /// <summary>Runs one step as a phase. A throwing step disposes its scope, which records Aborted.</summary>
    private static async Task StepAsync(IStartupPhaseSink? sink, string phase, Func<Task> body)
    {
        using var scope = sink?.Phase(phase);
        await body().ConfigureAwait(false);
        scope?.Complete();
    }

    /// <summary>
    /// 一次连接开合读出"修订标记 + 哨兵表数"。非 SQLite 返回 (-1, -1)（→ 判定落到"需要跑阶梯"）。
    /// <para>
    /// 修订号不匹配时**不**再扫 <c>sqlite_master</c>：阶梯无论如何都要跑，探测只是浪费一次往返。
    /// 只有标记命中（可能跳过阶梯）才值得确认哨兵表还在。
    /// </para>
    /// </summary>
    private static async Task<(long Revision, int SentinelFound)> ProbeSchemaMarkerAsync(
        PlatformDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsSqlite()) return (-1, -1);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = db.Database.GetDbConnection();
            await using (var readVersion = connection.CreateCommand())
            {
                readVersion.CommandText = "PRAGMA user_version;";
                var value = await readVersion.ExecuteScalarAsync(ct).ConfigureAwait(false);
                var revision = value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                if (revision != PlatformSchemaRevision.Current) return (revision, -1);

                // 表名来自 PlatformSchemaRevision.SentinelTables（编译期常量），只拼这一条查询。
                var names = string.Join(",", PlatformSchemaRevision.SentinelTables.Select(name => "'" + name + "'"));
                await using var countSentinel = connection.CreateCommand();
                countSentinel.CommandText =
                    $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ({names});";
                var found = await countSentinel.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return (revision, found is null or DBNull ? 0 : Convert.ToInt32(found, CultureInfo.InvariantCulture));
            }
        }
        finally { await db.Database.CloseConnectionAsync().ConfigureAwait(false); }
    }

    /// <summary>写修订标记。只在整条阶梯成功后调用。</summary>
    private static async Task WriteUserVersionAsync(PlatformDbContext db, long revision, CancellationToken ct)
    {
        if (!db.Database.IsSqlite()) return;
        await db.Database.ExecuteSqlRawAsync($"PRAGMA user_version = {revision};", ct).ConfigureAwait(false);
    }
}
