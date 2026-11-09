using Microsoft.Extensions.DependencyInjection.Extensions;
using PuddingEventing;
using PuddingEventing.Contracts;
using PuddingPlatform.Services.Eventing;
using PuddingPlatform.Services.Files;

namespace PuddingAgent.Services;

/// <summary>
/// CORE plugin workspace-event composition: committed outbox workspace events are dispatched by the existing
/// <see cref="PluginEventDispatchWorker"/> through the real plugin manager. The worker is registered but not hosted;
/// enabling the background loop is a product decision separate from the contract freeze.
/// </summary>
public static partial class PuddingServiceCollectionExtensions
{
    public static void AddPluginWorkspaceEvents(WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<OutboxCommittedEventLog>();
        builder.Services.TryAddSingleton<ICommittedEventLog>(sp => sp.GetRequiredService<OutboxCommittedEventLog>());
        builder.Services.TryAddSingleton<SubscriptionDispatcher>();
        builder.Services.TryAddSingleton<IEventDispatchWorkspaceSource, OutboxWorkspaceSource>();
        builder.Services.TryAddSingleton<IWorkspacePluginEventTarget, HostWorkspacePluginEventTarget>();
        builder.Services.TryAddSingleton<IPluginEventDeliverySink, PluginManagerEventDeliverySink>();
        builder.Services.TryAddSingleton<PluginEventDispatchWorker>();
        builder.Services.TryAddSingleton<PluginArchiveStore>();
        builder.Services.TryAddSingleton<EventOutboxUnitOfWork>();
        builder.Services.TryAddSingleton<WorkspaceArtifactService>();
        builder.Services.TryAddSingleton<PluginSubscriptionSynchronizer>();
        builder.Services.TryAddSingleton<IActivePluginSubscriptionSource, HostActivePluginSubscriptionSource>();
        builder.Services.TryAddSingleton<PluginSubscriptionLifecycle>();
    }
}
