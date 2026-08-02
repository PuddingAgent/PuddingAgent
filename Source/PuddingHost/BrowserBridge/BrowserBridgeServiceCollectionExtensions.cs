using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PuddingBrowser.Abstractions;
using PuddingBrowser.AgentTools;
using PuddingCode.Tools;
using PuddingHost.Hosting;
using PuddingRuntime.Services.Tools;

namespace PuddingHost.BrowserBridge;

public static class BrowserBridgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the executable Core-side browser proxy and Agent tools only when the
    /// process was launched by PuddingDesktop. Console/dev hosts expose no browser tools.
    /// </summary>
    public static IServiceCollection AddDesktopBrowserAutomation(
        this IServiceCollection services,
        PuddingHostOptions hostOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(hostOptions);

        if (hostOptions.Mode != PuddingHostMode.DesktopChild
            || !hostOptions.BrowserAutomationEnabled)
        {
            return services;
        }

        // Register Origin accessor (AsyncLocal, singleton for process lifetime)
        services.TryAddSingleton<IBrowserOperationOriginAccessor, BrowserOperationOriginAccessor>();

        services.AddSingleton<IDesktopBrowserConnectionRegistry, DesktopBrowserConnectionRegistry>();
        services.AddSingleton<IDesktopBrowserCommandBroker, DesktopBrowserCommandBroker>();
        services.AddSingleton<IBrowserBridgeClock, SystemBrowserBridgeClock>();
        services.AddSingleton<RemoteBrowserRuntime>();
        services.AddSingleton<IBrowserRuntime>(sp => sp.GetRequiredService<RemoteBrowserRuntime>());
        services.AddPuddingToolsFromAssembly(typeof(BrowserContextTool).Assembly);
        return services;
    }
}
