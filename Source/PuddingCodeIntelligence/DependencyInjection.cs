using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PuddingCodeIntelligence.Bicep;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Cpp;
using PuddingCodeIntelligence.CSharp;
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

        services.TryAddSingleton<ICodeQueryService, CodeQueryService>();
        services.TryAddSingleton<ILanguageServerService, IndexBasedLanguageServerService>();
        services.TryAddSingleton<ICodeIndexer, RoslynCSharpIndexer>();

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
