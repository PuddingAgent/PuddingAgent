using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PuddingHost.Hosting;

/// <summary>
/// Registers internal desktop lifecycle endpoints for DesktopChild mode only.
/// Endpoints:
///   POST /internal/desktop/shutdown — graceful shutdown (requires X-Pudding-Desktop-Token)
/// </summary>
public static class DesktopLifecycleEndpointExtensions
{
    public static IEndpointRouteBuilder MapDesktopChildEndpoints(
        this IEndpointRouteBuilder endpoints,
        PuddingHostOptions options)
    {
        if (options.Mode != PuddingHostMode.DesktopChild)
            return endpoints;

        endpoints.MapPost("/internal/desktop/shutdown", async (HttpContext context) =>
        {
            // Only loopback
            if (!IsLoopback(context))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Forbidden: loopback only.");
                return;
            }

            var validator = context.RequestServices.GetRequiredService<DesktopControlTokenValidator>();
            var presentedToken = context.Request.Headers["X-Pudding-Desktop-Token"].FirstOrDefault();

            if (!validator.Validate(presentedToken))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized: invalid or missing token.");
                return;
            }

            var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            await context.Response.WriteAsJsonAsync(new { status = "shutting_down" });
            await context.Response.CompleteAsync();
            lifetime.StopApplication();
        });

        return endpoints;
    }

    /// <summary>
    /// Registers DesktopChild-specific hosted services (parent process monitor)
    /// and the token validator into DI.
    /// </summary>
    public static IServiceCollection AddDesktopChildServices(
        this IServiceCollection services,
        PuddingHostOptions options)
    {
        if (options.Mode != PuddingHostMode.DesktopChild)
            return services;

        // Token validator (reads from system.json on each request)
        services.AddSingleton(new DesktopControlTokenValidator(options.DataRoot));

        // Parent process monitor (stops Core when Desktop exits)
        if (options.DesktopParentPid is > 0)
        {
            services.AddSingleton<IHostedService>(sp =>
                new DesktopParentProcessMonitor(
                    options.DesktopParentPid.Value,
                    sp.GetRequiredService<IHostApplicationLifetime>()));
        }

        return services;
    }

    private static bool IsLoopback(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        return ip is not null
            && (System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.IPv6Loopback));
    }
}
