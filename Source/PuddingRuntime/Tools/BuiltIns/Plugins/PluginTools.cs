using System.Text.Json;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Plugins;

namespace PuddingRuntime.Services.Tools;

[Tool("plugin_catalog", "plugin_catalog", "List visible product tool packages, installed executable plugins, SDK location, current scope and live activations. Develop a net10.0 DLL against the SDK, ZIP it with plugin.json, import, then load. Loading and unloading do not restart Core.", ToolCategory.Query, ToolPermissionLevel.Low, ToolSafetyFlags.ReadOnly)]
public sealed class PluginCatalogTool(ExecutablePluginService plugins, HostToolContributions? host = null) : PuddingToolBase<PluginEmptyArgs, object>
{
    protected override Task<object> ExecuteCoreAsync(PluginEmptyArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var catalog = plugins.Packages.Scan();
        return Task.FromResult<object>(new { schema = "pudding-tool-plugin/v2", sdkDirectory = ExecutablePluginService.SdkDirectory,
            authoringGuide = System.IO.Path.Combine(ExecutablePluginService.SdkDirectory, "AUTHORING.md"),
            exampleDirectory = System.IO.Path.Combine(ExecutablePluginService.SdkDirectory, "sdk-example"),
            workerInstalled = File.Exists(ExecutablePluginService.WorkerPath), coreProcessId = Environment.ProcessId,
            scope = ExecutablePluginService.Scope(context), revision = plugins.Revision,
            catalog, hostTools = host?.Catalog(context), activations = plugins.Manager.List(ExecutablePluginService.Scope(context)),
            instructions = "Import immutable ZIP with plugin_import; plugin_load requires approval to execute local code. Use the returned generation-specific toolNames on the next model round. Unload explicitly when finished. Hook contributions are advisory. Workers run as the current user; process isolation is not a security sandbox." });
    }
}
[Tool("plugin_search", "plugin_search", "Search the plugin repository by id, name, description or declared capability. Read-only: it reports what the repository holds and whether each entry is currently active in this agent context. Use contentHash with plugin_load to install.", ToolCategory.Query, ToolPermissionLevel.Low, ToolSafetyFlags.ReadOnly)]
public sealed class PluginSearchTool(ExecutablePluginService plugins) : PuddingToolBase<PluginSearchArgs, object>
{
    private const int MaxLimit = 200;
    protected override Task<object> ExecuteCoreAsync(PluginSearchArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var snapshot = plugins.Packages.Scan();
        var active = plugins.Manager.List(ExecutablePluginService.Scope(context))
            .Where(a => a.State == "Active")
            .Select(a => a.ContentHash)
            .ToHashSet(StringComparer.Ordinal);
        var query = args.Query?.Trim() ?? "";
        var limit = Math.Clamp(args.Limit, 1, MaxLimit);
        var matches = snapshot.Packages
            .Where(item => query.Length == 0 || Matches(item, query))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Version, StringComparer.Ordinal)
            .Select(item => new
            {
                id = item.Id, version = item.Version, contentHash = item.ContentHash, status = item.Status,
                name = item.Manifest?.Name, description = item.Manifest?.Description,
                capabilities = item.Manifest?.RequestedCapabilities,
                active = active.Contains(item.ContentHash),
            })
            .ToArray();
        return Task.FromResult<object>(new
        {
            query, limit, complete = snapshot.Complete, repositoryTotal = snapshot.Packages.Count,
            matched = matches.Length, returned = Math.Min(matches.Length, limit),
            items = matches.Take(limit).ToArray(),
            instructions = "active=true means the plugin is mounted in this agent context. Use plugin_load with contentHash to mount an entry; plugin_unload revokes it.",
        });
    }

    private static bool Matches(PluginCatalogItem item, string query)
    {
        if (item.Id.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.Version.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.Manifest is not { } manifest) return false;
        if (manifest.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (manifest.Description.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return manifest.RequestedCapabilities.Any(c => c.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}
[Tool("plugin_import", "plugin_import", "Validate and install a v2 plugin ZIP from the current workspace. Does not execute code or replace an active version. Returns immutable contentHash for plugin_load.", ToolCategory.FileSystem, ToolPermissionLevel.High, ToolSafetyFlags.RequiresFileWrite)]
public sealed class PluginImportTool(ExecutablePluginService plugins) : PuddingToolBase<PluginImportArgs, PluginPackage>
{
    protected override async Task<PluginPackage> ExecuteCoreAsync(PluginImportArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.WorkingDirectory) || !Path.IsPathFullyQualified(context.WorkingDirectory))
            throw new InvalidOperationException("Plugin import requires an absolute workspace root from the execution environment.");
        var path = PluginValidation.Inside(context.WorkingDirectory, args.Path);
        await using var file = File.OpenRead(path);
        return await plugins.Packages.ImportAsync(file, ct);
    }
}
[Tool("plugin_load", "plugin_load", "Mount an installed product tool package or run an SDK plugin in its own Worker for this agent and session. Requires native-code execution approval. Use its contentHash. Product packages take no capabilities or hooks and keep the agent tool policy. SDK packages require the manifest grants. Returns live tool names; use them next round. No Core restart.", ToolCategory.Execute, ToolPermissionLevel.High)]
public sealed class PluginLoadTool(ExecutablePluginService plugins, HostToolContributions? host = null) : PuddingToolBase<PluginLoadArgs>
{
    protected override Task<ToolExecutionResult> ExecuteCoreAsync(PluginLoadArgs args, ToolExecutionContext context, CancellationToken ct)
        => PluginLifecycleResult.RunAsync(() => ToolPluginLifecycle.LoadAsync(plugins, host, args.ContentHash, args.OperationId, ExecutablePluginService.Scope(context),
            new(true, args.ApprovedCapabilities.ToHashSet(StringComparer.Ordinal), args.EnableHooks), args.ExpectedRevision, ct, context), "Active");
}
[Tool("plugin_unload", "plugin_unload", "Revoke this session's plugin tools/hooks immediately and stop its worker and children. Returns Unloaded only after exit is observed. No Core restart. Does not remove installed packages.", ToolCategory.Security, ToolPermissionLevel.Low)]
public sealed class PluginUnloadTool(ExecutablePluginService plugins, HostToolContributions? host = null) : PuddingToolBase<PluginUnloadArgs>
{
    protected override Task<ToolExecutionResult> ExecuteCoreAsync(PluginUnloadArgs args, ToolExecutionContext context, CancellationToken ct)
        => PluginLifecycleResult.RunAsync(() => ToolPluginLifecycle.UnloadAsync(plugins, host, args.ActivationId, ExecutablePluginService.Scope(context), args.ExpectedRevision, ct), "Unloaded");
}
internal static class PluginLifecycleResult
{
    public static async Task<ToolExecutionResult> RunAsync(Func<Task<PluginActivation>> operation, string expectedState)
    {
        try
        {
            var activation = await operation(); var success = activation.State == expectedState;
            return new() { Success = success, Output = JsonSerializer.Serialize(activation, PluginJson.Options), ExitCode = success ? 0 : 1,
                Error = success ? null : activation.Failure?.Message ?? $"Activation state is {activation.State}.", Status = success ? null : activation.Failure?.Code ?? "plugin.lifecycle_incomplete" };
        }
        catch (PluginException ex)
        { return new() { Success = false, Output = JsonSerializer.Serialize(ex.Failure, PluginJson.Options), Error = ex.Message, ExitCode = 1, Status = ex.Failure.Code }; }
    }
}
[Tool("plugin_diagnostics", "plugin_diagnostics", "Inspect this session's plugin states and bounded diagnostic failures. Distinguishes initialization, protocol, timeout, callback denial and unconfirmed stop.", ToolCategory.Query, ToolPermissionLevel.Low, ToolSafetyFlags.ReadOnly)]
public sealed class PluginDiagnosticsTool(ExecutablePluginService plugins, HostToolContributions? host = null) : PuddingToolBase<PluginEmptyArgs, object>
{
    protected override Task<object> ExecuteCoreAsync(PluginEmptyArgs args, ToolExecutionContext context, CancellationToken ct)
        => Task.FromResult<object>(new { revision = plugins.Revision, activations = plugins.Manager.List(ExecutablePluginService.Scope(context)),
            hostTools = host?.Catalog(context), failures = plugins.Manager.Diagnostics(ExecutablePluginService.Scope(context)) });
}
public sealed record PluginEmptyArgs;
public sealed record PluginSearchArgs
{
    [ToolParam("Case-insensitive substring over id, version, name, description and declared capabilities. Omit to list everything.")]
    public string? Query { get; init; }
    [ToolParam("Maximum entries to return (1-200, default 20).")]
    public int Limit { get; init; } = 20;
}
public sealed record PluginImportArgs { [ToolParam("ZIP path relative to this execution's working directory; use forward slashes.")] public required string Path { get; init; } }
public sealed record PluginLoadArgs
{
    public required string ContentHash { get; init; }
    [ToolParam("Unique operation id. Retrying the same content/id is idempotent; a new activation after unload requires a new id.")]
    public required string OperationId { get; init; }
    public string[] ApprovedCapabilities { get; init; } = [];
    public bool EnableHooks { get; init; }
    public long? ExpectedRevision { get; init; }
}
public sealed record PluginUnloadArgs { public required string ActivationId { get; init; } public long? ExpectedRevision { get; init; } }
