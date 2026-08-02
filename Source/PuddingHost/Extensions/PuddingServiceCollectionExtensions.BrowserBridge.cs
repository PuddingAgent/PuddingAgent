using Microsoft.AspNetCore.Builder;
using PuddingHost.BrowserBridge;
using PuddingHost.Hosting;

namespace PuddingAgent.Services;

public static partial class PuddingServiceCollectionExtensions
{
    private static void AddBrowserBridgeServices(
        WebApplicationBuilder builder,
        PuddingHostOptions hostOptions)
    {
        builder.Services.AddDesktopBrowserAutomation(hostOptions);
    }

    public static WebApplication MapDesktopBrowserBridgeEndpoint(this WebApplication app)
    {
        var hostOptions = app.Services.GetRequiredService<PuddingHostOptions>();

        if (hostOptions.Mode != PuddingHostMode.DesktopChild
            || !hostOptions.BrowserAutomationEnabled)
            return app;

        app.MapDesktopBrowserBridge();
        return app;
    }
}
