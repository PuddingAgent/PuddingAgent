using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Tools;
using PuddingDesktop.Foundation;
using PuddingRuntime.Services.Plugins;

namespace PuddingDesktop.Composition;

/// <summary>
/// Binds the tool registry and plugin catalogue to the existing Core services. Both are read-only:
/// capabilities are derived from the runtime registry, and plugin packages are declared by
/// plugin.json — this adapter never installs, enables or executes anything.
/// </summary>
internal sealed class DesktopToolPluginSettings(IDesktopKernel kernel) : IToolPluginSettings
{
    public Task<IReadOnlyList<ToolCatalogEntry>> ListToolsAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("tools.registry.list", (scope, _) =>
        {
            var catalog = scope.Services.GetRequiredService<IPuddingToolCatalogService>();
            var tools = catalog.ListTools()
                .OrderBy(descriptor => descriptor.SortOrder)
                .ThenBy(descriptor => descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
                .Select(Map)
                .ToArray();
            return Task.FromResult((IReadOnlyList<ToolCatalogEntry>)tools);
        }, cancellationToken);

    public Task<PluginCatalogReport> ReadPluginCatalogAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("tools.plugins.read", (scope, _) =>
        {
            var plugins = scope.Services.GetRequiredService<PluginManifestCatalog>().ListPlugins();
            var packages = plugins
                .OrderBy(plugin => plugin.PluginId, StringComparer.OrdinalIgnoreCase)
                .Select(plugin => new PluginPackageSummary(plugin.PluginId, plugin.Name, plugin.Version,
                    plugin.Status.ToString(), plugin.StatusReason, plugin.ManifestPath, plugin.Tools.Count))
                .ToArray();
            // Declared tools keep the plugin that declares them, and their RuntimeStatus is the
            // registry-facing answer the manifest-only tool carries.
            var declared = plugins
                .SelectMany(plugin => plugin.Tools.Select(tool => new PluginToolDeclaration(
                    plugin.PluginId, tool.ToolId, tool.Name, tool.RuntimeStatus, tool.IsEnabledByDefault)))
                .OrderBy(tool => tool.PluginId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var diagnostics = scope.Services.GetRequiredService<PluginDiagnosticsReader>()
                .ListRecent(new PluginDiagnosticsQuery(Limit: 50))
                .OrderByDescending(entry => entry.OccurredAtUtc)
                .Select(entry => new PluginDiagnosticEntry(entry.OccurredAtUtc, entry.EventType,
                    entry.PluginId ?? "", entry.Status ?? "", entry.Message ?? ""))
                .ToArray();
            return Task.FromResult(new PluginCatalogReport(packages, declared, diagnostics));
        }, cancellationToken);

    public Task ReloadPluginsAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("tools.plugins.reload", (scope, _) =>
        {
            scope.Services.GetRequiredService<PluginManifestCatalog>().Reload();
            return Task.FromResult(true);
        }, cancellationToken);

    private static ToolCatalogEntry Map(ToolDescriptor descriptor)
    {
        var required = descriptor.Parameters.Required;
        var parameters = descriptor.Parameters.Properties
            .Select(property => new ToolParameterSummary(property.Name, property.Type, property.Description,
                required.Contains(property.Name)))
            .ToArray();
        return new ToolCatalogEntry(descriptor.ToolId, descriptor.Name, descriptor.Description,
            descriptor.Category.ToString(), descriptor.SourceKind, descriptor.SourceId ?? "",
            descriptor.RuntimeStatus, descriptor.IsEnabledByDefault, descriptor.PermissionLevel.ToString(),
            descriptor.SortOrder, parameters);
    }
}
