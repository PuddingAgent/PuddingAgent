using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PuddingCodeIntelligence.Bicep;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Cpp;
using PuddingCodeIntelligence.CSharp;
using PuddingCodeIntelligence.Extractors;
using PuddingCodeIntelligence.Json;
using PuddingCodeIntelligence.Lsp;
using PuddingCodeIntelligence.Markdown;
using PuddingCodeIntelligence.PowerShell;
using PuddingCodeIntelligence.Python;
using PuddingCodeIntelligence.Services;
using PuddingCodeIntelligence.TypeScript;
using PuddingCodeIntelligence.Yaml;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;
using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIntelligence;

public static class DependencyInjection
{
    public static IServiceCollection AddPuddingCodeIntelligence(this IServiceCollection services)
    {
        // ── Core services ──────────────────────────────────────────────
        services.TryAddSingleton<ICodeProjectRegistry, CodeProjectRegistry>();
        services.TryAddSingleton<ICodeWorkspaceResolver, DefaultCodeWorkspaceResolver>();
        services.TryAddSingleton<ICodeProjectRootDetector, DefaultProjectRootDetector>();
        services.TryAddSingleton<ICodeIndexScopeRegistry, CodeIndexScopeRegistry>();
        services.TryAddSingleton<ICodeIndexScopeResolver, CodeIndexScopeResolver>();
        services.TryAddSingleton<ICodeIndexScheduler, CodeIndexScheduler>();

        // ── Index maintenance driver wiring (U3-B2a — closes the P0 "enqueue with nobody pumping") ──
        // The scheduler keeps no background loop since U3-B1 (a378a9d8): something outside it must pump the
        // queue through ICodeIndexSchedulerDriver.ProcessPendingAsync. Without that pump an Enqueue is queued
        // forever, because the production acceptor (code_index_register_project) enqueues directly and
        // produces no change batch — a driver that only reacted to batches would never service it.
        //
        // The pump must reach the *same* instance as ICodeIndexScheduler: two schedulers would mean two
        // writers over one index (ADR-089 §2 驱动归属). Fail closed with a diagnosable message rather than
        // silently building a second instance.
        services.TryAddSingleton<ICodeIndexSchedulerDriver>(sp =>
            sp.GetRequiredService<ICodeIndexScheduler>() is ICodeIndexSchedulerDriver driver
                ? driver
                : throw new InvalidOperationException(
                    "ICodeIndexScheduler must also implement ICodeIndexSchedulerDriver: the scheduler owns " +
                    "no background loop and the host driver can only pump it through that port."));

        // The change source of every scope is real file-system watching; the logger is passed explicitly
        // because the non-generic ILogger is not registered in the container (it would silently be null).
        services.TryAddSingleton<ICodeIndexWatcherFactory>(sp =>
            new FileSystemCodeIndexWatcherFactory(
                sp.GetService<ILoggerFactory>()?.CreateLogger("PuddingCodeIndex.CodeIndexWatcher")));

        // The single driver of the change-capture pipeline *and* of the scheduler queue. It stays a plain
        // component service: hosting (IHostedService) is wired in PuddingHost, because the component must
        // not depend on the Host.
        services.TryAddSingleton<ICodeIndexMaintenance, CodeIndexMaintenanceService>();

        // ── D4 source maintenance (2026-10-02) ─────────────────────────
        // 登记 + 装配（S5）：新链路的零件在这里装配成对象图，**暂时没有驱动者** ——
        // 因此运行中实例的行为一字未变；把驱动从旧路径切到协调器是独立的一步，需要外部重启验收。
        services.TryAddSingleton<ICodeSourceIgnoreRules, WorkspaceCodeSourceIgnoreRules>();
        services.TryAddSingleton<ICodeSourceScanner, FileSystemCodeSourceScanner>();
        services.TryAddSingleton<CodeSourceScanService>();
        services.TryAddSingleton<CodeSourceFingerprintReader>();

        // 能力端口都用**同一个** store 实例：两个 store 就是两个写者（与 scheduler/pump 的同一实例约束同理）。
        services.TryAddSingleton<ICodeSourceMaintenanceStore>(sp =>
            sp.GetRequiredService<ICodeIndexStore>() as ICodeSourceMaintenanceStore
            ?? throw new InvalidOperationException(
                "ICodeIndexStore must also implement ICodeSourceMaintenanceStore: the source-maintenance " +
                "chain persists manifest/ledger/atomic replacements through that port."));

        services.TryAddSingleton<ICodeGraphDependencyQuery>(sp =>
            sp.GetRequiredService<ICodeIndexStore>() as ICodeGraphDependencyQuery
            ?? throw new InvalidOperationException(
                "ICodeIndexStore must also implement ICodeGraphDependencyQuery: reverse-dependency expansion " +
                "reads the relation/reference graph from it."));

        services.TryAddSingleton<ICodeIndexFileBatchUpdater>(sp =>
            sp.GetRequiredService<ICodeIndexer>() as ICodeIndexFileBatchUpdater
            ?? throw new InvalidOperationException(
                "ICodeIndexer must also implement ICodeIndexFileBatchUpdater: the batch seam is what keeps a " +
                "batch to a single workspace/compile snapshot."));

        services.TryAddSingleton<CodeSourceMaintenanceCoordinator>();

        services.TryAddSingleton<ICodeQueryService, CodeQueryService>();
        services.TryAddSingleton<ILanguageServerService, IndexBasedLanguageServerService>();

        // ── Code indexers: multi-language fan-out ──────────────────────
        // Every language implementation is registered under the marker port ILanguageCodeIndexer and the
        // aggregate ICodeIndexer fans a scope out to all of them. Two ports and not one: an aggregate that
        // injected IEnumerable<ICodeIndexer> would inject *itself* (it is an ICodeIndexer as well) and
        // recurse without bound, so the split between "language implementation" and "the contract callers
        // consume" is enforced at compile time instead.
        // Registration order IS the merge order of messages and per-language outcomes: C# → TS/JS → Python.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILanguageCodeIndexer, RoslynCSharpIndexer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILanguageCodeIndexer, TypeScriptIndexer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILanguageCodeIndexer, PythonIndexer>());
        services.TryAddSingleton<ICodeIndexer, CompositeCodeIndexer>();

        // Extractor assets belong to this component and are resolved from the directory holding its own
        // assembly (B4+). Registered so the container-built TypeScriptIndexer/PythonIndexer registered
        // above use exactly the same resolver their default constructor falls back to.
        services.TryAddSingleton<IExtractorAssetResolver, ExtractorAssetResolver>();

        // ── File outliners (multi-language) ─────────────────────────────
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFileOutliner, TypeScriptFileOutliner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFileOutliner, MarkdownFileOutliner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFileOutliner, JsonFileOutliner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFileOutliner, YamlFileOutliner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFileOutliner, PowerShellFileOutliner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFileOutliner, BicepFileOutliner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFileOutliner, CppFileOutliner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFileOutliner, PythonFileOutliner>());
        services.TryAddSingleton<IFileOutlinerRegistry, FileOutlinerRegistry>();

        return services;
    }
}
