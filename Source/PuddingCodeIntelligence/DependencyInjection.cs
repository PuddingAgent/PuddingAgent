using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.TryAddSingleton<ICodeQueryService, CodeQueryService>();
        services.TryAddSingleton<ILanguageServerService, NoOpLanguageServerService>();
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
