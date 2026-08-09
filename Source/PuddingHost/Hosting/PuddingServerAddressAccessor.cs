namespace PuddingHost.Hosting;

public interface IPuddingServerAddressAccessor
{
    Uri? BaseAddress { get; }
    void SetBoundAddresses(IEnumerable<string> addresses);
}

public sealed class PuddingServerAddressAccessor : IPuddingServerAddressAccessor
{
    public Uri? BaseAddress { get; private set; }

    public void SetBoundAddresses(IEnumerable<string> addresses)
    {
        var httpAddresses = addresses
            .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null
                && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            .Cast<Uri>()
            .ToArray();

        BaseAddress = httpAddresses.FirstOrDefault(uri => uri.IsLoopback);
        if (BaseAddress is not null)
            return;

        var wildcard = httpAddresses.FirstOrDefault(uri =>
            string.Equals(
                uri.DnsSafeHost,
                System.Net.IPAddress.Any.ToString(),
                StringComparison.Ordinal)
            || string.Equals(
                uri.DnsSafeHost,
                System.Net.IPAddress.IPv6Any.ToString(),
                StringComparison.Ordinal));

        BaseAddress = wildcard is null
            ? null
            : new UriBuilder(wildcard)
            {
                Host = System.Net.IPAddress.Loopback.ToString(),
            }.Uri;
    }
}
