namespace PuddingDesktop.Hosting;

/// <summary>
/// Resolves the actual loopback HTTP address after the server has bound.
/// Desktop listens on http://127.0.0.1:0 (dynamic port).
/// </summary>
public sealed class DesktopLocalEndpointResolver
{
    private Uri? _baseAddress;

    public Uri? BaseAddress => _baseAddress;

    /// <summary>
    /// Called after the server has started. Accepts only loopback HTTP addresses.
    /// </summary>
    public void SetBoundAddress(Uri address)
    {
        if (!address.IsLoopback)
            throw new InvalidOperationException(
                $"Desktop host must listen on loopback only. Got: {address}");

        if (address.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException(
                $"Desktop host uses HTTP only. Got: {address.Scheme}");

        _baseAddress = address;
    }

    /// <summary>
    /// Returns the Admin URL for the WebView2 to navigate to.
    /// </summary>
    public Uri GetAdminUrl()
    {
        if (_baseAddress is null)
            throw new InvalidOperationException("Server has not bound yet.");

        return new Uri(_baseAddress, "/admin/");
    }
}
