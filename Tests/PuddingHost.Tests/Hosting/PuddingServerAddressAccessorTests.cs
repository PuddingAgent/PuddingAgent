using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

public class PuddingServerAddressAccessorTests
{
    [Fact]
    public void SetBoundAddresses_RejectsNonLoopbackAddress()
    {
        var accessor = new PuddingServerAddressAccessor();
        accessor.SetBoundAddresses(new[] { "http://192.168.1.1:8080" });

        Assert.Null(accessor.BaseAddress);
    }

    [Fact]
    public void SetBoundAddresses_SelectsLoopbackHttpAddress()
    {
        var accessor = new PuddingServerAddressAccessor();
        accessor.SetBoundAddresses(new[]
        {
            "http://192.168.1.1:8080",
            "http://127.0.0.1:5000",
            "https://127.0.0.1:5001",
            "http://[::1]:6000"
        });

        Assert.NotNull(accessor.BaseAddress);
        Assert.Equal("http", accessor.BaseAddress!.Scheme);
        Assert.True(accessor.BaseAddress.IsLoopback);
        Assert.Equal(5000, accessor.BaseAddress.Port);
    }

    [Fact]
    public void SetBoundAddresses_EmptyList_NoBaseAddress()
    {
        var accessor = new PuddingServerAddressAccessor();
        accessor.SetBoundAddresses(Array.Empty<string>());

        Assert.Null(accessor.BaseAddress);
    }
}
