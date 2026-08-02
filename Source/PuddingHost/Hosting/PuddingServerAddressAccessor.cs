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
        // Accept only loopback HTTP addresses
        var loopback = addresses
            .Select(a => new Uri(a, UriKind.Absolute))
            .FirstOrDefault(u => u.IsLoopback
                && string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase));

        BaseAddress = loopback;
    }
}
