using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Plugins;

/// <summary>One management route for installed host-service packages and imported executable packages.</summary>
internal static class ToolPluginLifecycle
{
    public static Task<PluginActivation> LoadAsync(ExecutablePluginService executable, HostToolContributions? host,
        string hash, string operation, PluginScope scope, PluginAuthorization authorization, long? revision, CancellationToken ct,
        ToolExecutionContext? caller = null)
    {
        ct.ThrowIfCancellationRequested();
        if (host?.IsInstalled(hash) == true)
        {
            if (authorization.Capabilities.Count != 0 || authorization.HooksApproved)
                throw new PluginException("plugin.capability_denied", "load", "Host-service packages do not grant capabilities or hooks.");
            return Task.FromResult(host.Load(hash, scope, operation, revision, authorization.ExecuteApproved, caller));
        }
        if (caller is not null && caller.CapabilityPolicy?.AllowShellExecution != true)
            throw new PluginException("plugin.capability_denied", "load", "Native Worker execution requires the agent's process execution permission.");
        return executable.Manager.LoadAsync(operation, executable.Find(hash), scope, authorization, revision, ct);
    }

    public static Task<PluginActivation> UnloadAsync(ExecutablePluginService executable, HostToolContributions? host,
        string id, PluginScope scope, long? revision, CancellationToken ct) =>
        host is not null && id.StartsWith("host-", StringComparison.Ordinal)
            ? host.UnloadAsync(id, scope, revision, ct) : executable.Manager.UnloadAsync(id, scope, revision, ct);
}
