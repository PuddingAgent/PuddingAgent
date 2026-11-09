using Pudding.ToolPlugins.Contracts;

namespace Pudding.ToolPlugins.Sdk;

public abstract class ToolPlugin : IToolPlugin
{
    protected IPluginContext Context { get; private set; } = null!;
    public virtual ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    { Context = context; return ValueTask.CompletedTask; }
    public abstract IReadOnlyCollection<string> ToolIds { get; }
    public virtual IReadOnlyCollection<string> HookIds => [];
    public virtual IReadOnlyCollection<string> SubscriptionIds => [];
    public abstract ValueTask<PluginResult> ExecuteAsync(PluginCall call, CancellationToken ct);
    public virtual ValueTask<PluginHookResult> OnToolResultAsync(PluginHookCall call, CancellationToken ct)
        => ValueTask.FromResult(new PluginHookResult());
    public virtual ValueTask OnWorkspaceEventAsync(PluginWorkspaceEvent workspaceEvent, CancellationToken ct)
        => ValueTask.CompletedTask;
    public virtual ValueTask ShutdownAsync(CancellationToken ct) => ValueTask.CompletedTask;
}
