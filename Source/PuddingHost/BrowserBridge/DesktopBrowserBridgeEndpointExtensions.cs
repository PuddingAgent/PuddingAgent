using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingHost.Hosting;

namespace PuddingHost.BrowserBridge;

public static class DesktopBrowserBridgeEndpointExtensions
{
    public static WebApplication MapDesktopBrowserBridge(this WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.Zero // We manage our own heartbeat
        });

        app.Map(PuddingBrowser.Protocol.BrowserBridgeProtocol.EndpointPath, async (HttpContext context) =>
        {
            var registry = context.RequestServices.GetRequiredService<IDesktopBrowserConnectionRegistry>();
            var broker = context.RequestServices.GetRequiredService<IDesktopBrowserCommandBroker>();
            var tokenValidator = context.RequestServices.GetRequiredService<DesktopControlTokenValidator>();
            var clock = context.RequestServices.GetRequiredService<IBrowserBridgeClock>();
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("PuddingHost.BrowserBridge");

            await DesktopBrowserBridgeWebSocketEndpoint.HandleAsync(
                context, registry, broker, tokenValidator, clock, logger);
        });

        return app;
    }
}
