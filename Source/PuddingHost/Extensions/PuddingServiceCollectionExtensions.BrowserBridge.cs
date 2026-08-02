using Microsoft.AspNetCore.Builder;
using PuddingHost.BrowserBridge;
using PuddingHost.Hosting;

namespace PuddingAgent.Services;

public static partial class PuddingServiceCollectionExtensions
{
    private static void AddBrowserBridgeServices(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IDesktopBrowserConnectionRegistry, DesktopBrowserConnectionRegistry>();
        builder.Services.AddSingleton<IDesktopBrowserCommandBroker, DesktopBrowserCommandBroker>();
        builder.Services.AddSingleton<IBrowserBridgeClock, SystemBrowserBridgeClock>();
    }

    public static WebApplication MapDesktopBrowserBridgeEndpoint(this WebApplication app)
    {
        var hostOptions = app.Services.GetRequiredService<PuddingHostOptions>();

        if (hostOptions.Mode != PuddingHostMode.DesktopChild)
            return app;

        app.MapDesktopBrowserBridge();
        return app;
    }
}
