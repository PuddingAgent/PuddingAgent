using Microsoft.Extensions.Logging;

namespace PuddingHost.Hosting;

/// <summary>
/// Rewrites PlatformApiClient control-plane calls to the actual bound Core address
/// when the Host is running with a dynamic Desktop loopback port.
/// </summary>
public sealed class PuddingControllerAddressRewriteHandler(
    PuddingHostOptions hostOptions,
    IPuddingServerAddressAccessor serverAddressAccessor,
    ILogger<PuddingControllerAddressRewriteHandler> logger) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (hostOptions.Mode is not (PuddingHostMode.Desktop or PuddingHostMode.DesktopChild))
            return base.SendAsync(request, cancellationToken);

        var boundAddress = serverAddressAccessor.BaseAddress
            ?? throw new InvalidOperationException(
                "Desktop Core address has not been captured. " +
                "PlatformApiClient can only be used after the Host has started.");
        var originalAddress = request.RequestUri
            ?? throw new InvalidOperationException("PlatformApiClient request URI is missing.");

        var relativeAddress = originalAddress.IsAbsoluteUri
            ? originalAddress.PathAndQuery + originalAddress.Fragment
            : originalAddress.OriginalString;
        request.RequestUri = new Uri(boundAddress, relativeAddress);

        logger.LogDebug(
            "[PlatformApi] Rewrote Desktop control-plane request {OriginalAddress} to {BoundAddress}",
            originalAddress,
            request.RequestUri);

        return base.SendAsync(request, cancellationToken);
    }
}
